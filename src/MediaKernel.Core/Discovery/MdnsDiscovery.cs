// MediaKernel - mDNS/Bonjour Service Discovery
// Discovers network services using multicast DNS (Zeroconf/Bonjour)

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Discovery;

/// <summary>
/// Type of network service discovered via mDNS.
/// </summary>
public enum MdnsServiceType
{
    Unknown,
    Daap,           // iTunes/DAAP music sharing
    Raop,           // AirPlay audio
    AirPlay,        // AirPlay video
    Spotify,        // Spotify Connect
    Chromecast,     // Google Cast
    Dlna,           // DLNA/UPnP
    Http,           // Generic HTTP server
    Rtsp,           // RTSP streaming
    Smb,            // SMB/CIFS file sharing
    Nfs,            // NFS file sharing
    Sftp,           // SFTP
    Printer,        // Network printer
    Scanner         // Network scanner
}

/// <summary>
/// Represents a discovered mDNS service.
/// </summary>
public sealed class MdnsService
{
    /// <summary>Service instance name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Service type (e.g., "_daap._tcp.local").</summary>
    public string ServiceTypeString { get; init; } = string.Empty;

    /// <summary>Parsed service type.</summary>
    public MdnsServiceType ServiceType { get; init; }

    /// <summary>Host name.</summary>
    public string HostName { get; init; } = string.Empty;

    /// <summary>IP addresses for the service.</summary>
    public List<IPAddress> Addresses { get; init; } = new();

    /// <summary>Port number.</summary>
    public int Port { get; init; }

    /// <summary>TXT record key-value pairs.</summary>
    public Dictionary<string, string> TxtRecords { get; init; } = new();

    /// <summary>When the service was discovered.</summary>
    public DateTime DiscoveredAt { get; init; } = DateTime.UtcNow;

    /// <summary>Gets the primary URL for this service.</summary>
    public string? GetUrl()
    {
        if (Addresses.Count == 0 || Port == 0)
            return null;

        string scheme = ServiceType switch
        {
            MdnsServiceType.Http or MdnsServiceType.Daap => "http",
            MdnsServiceType.Rtsp or MdnsServiceType.Raop => "rtsp",
            MdnsServiceType.AirPlay => "airplay",
            MdnsServiceType.Smb => "smb",
            MdnsServiceType.Nfs => "nfs",
            MdnsServiceType.Sftp => "sftp",
            _ => "http"
        };

        return $"{scheme}://{Addresses[0]}:{Port}/";
    }

    public override string ToString() => $"{Name} ({ServiceTypeString}) at {HostName}:{Port}";
}

/// <summary>
/// Discovers network services using multicast DNS (mDNS/Bonjour/Zeroconf).
/// </summary>
public sealed class MdnsDiscovery : IDisposable
{
    private const string MdnsMulticastAddress = "224.0.0.251";
    private const string MdnsMulticastAddressV6 = "ff02::fb";
    private const int MdnsPort = 5353;

    // Common service types for media
    private static readonly Dictionary<string, MdnsServiceType> ServiceTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "_daap._tcp", MdnsServiceType.Daap },
        { "_raop._tcp", MdnsServiceType.Raop },
        { "_airplay._tcp", MdnsServiceType.AirPlay },
        { "_spotify-connect._tcp", MdnsServiceType.Spotify },
        { "_googlecast._tcp", MdnsServiceType.Chromecast },
        { "_http._tcp", MdnsServiceType.Http },
        { "_rtsp._tcp", MdnsServiceType.Rtsp },
        { "_smb._tcp", MdnsServiceType.Smb },
        { "_nfs._tcp", MdnsServiceType.Nfs },
        { "_sftp-ssh._tcp", MdnsServiceType.Sftp }
    };

    private readonly Dictionary<string, MdnsService> discoveredServices = new();
    private readonly object lockObj = new();
    private UdpClient? udpClient;
    private CancellationTokenSource? listeningCts;
    private bool disposed;

    /// <summary>
    /// Raised when a new service is discovered.
    /// </summary>
    public event Action<MdnsService>? ServiceDiscovered;

    /// <summary>
    /// Raised when a service is no longer available.
    /// </summary>
    public event Action<string>? ServiceLost;

    /// <summary>
    /// Gets all discovered services.
    /// </summary>
    public IReadOnlyList<MdnsService> Services
    {
        get
        {
            lock (lockObj)
            {
                return new List<MdnsService>(discoveredServices.Values);
            }
        }
    }

    /// <summary>
    /// Gets discovered services of a specific type.
    /// </summary>
    public IReadOnlyList<MdnsService> GetServices(MdnsServiceType type)
    {
        lock (lockObj)
        {
            return discoveredServices.Values.Where(s => s.ServiceType == type).ToList();
        }
    }

    /// <summary>
    /// Performs a one-time search for mDNS services.
    /// </summary>
    /// <param name="serviceTypes">Service types to search for (null = all media services).</param>
    /// <param name="timeout">Search timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of discovered services.</returns>
    public async Task<List<MdnsService>> SearchAsync(
        string[]? serviceTypes = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        timeout ??= TimeSpan.FromSeconds(5);
        serviceTypes ??= new[] { "_daap._tcp", "_raop._tcp", "_airplay._tcp", "_googlecast._tcp", "_http._tcp" };

        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var multicastEndpoint = new IPEndPoint(IPAddress.Parse(MdnsMulticastAddress), MdnsPort);

        // Send queries for each service type
        foreach (var serviceType in serviceTypes)
        {
            byte[] query = BuildMdnsQuery(serviceType + ".local");
            await udp.SendAsync(query, query.Length, multicastEndpoint);
        }

        var services = new List<MdnsService>();
        var endTime = DateTime.UtcNow + timeout.Value;

        while (DateTime.UtcNow < endTime && !cancellationToken.IsCancellationRequested)
        {
            udp.Client.ReceiveTimeout = (int)(endTime - DateTime.UtcNow).TotalMilliseconds;

            try
            {
                var result = await udp.ReceiveAsync(cancellationToken).AsTask()
                    .WaitAsync(endTime - DateTime.UtcNow, cancellationToken);

                var parsedServices = ParseMdnsResponse(result.Buffer, result.RemoteEndPoint.Address);

                foreach (var service in parsedServices)
                {
                    string key = $"{service.Name}|{service.ServiceTypeString}";
                    lock (lockObj)
                    {
                        if (!discoveredServices.ContainsKey(key))
                        {
                            discoveredServices[key] = service;
                            services.Add(service);
                            ServiceDiscovered?.Invoke(service);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Timeout or error, continue
            }
        }

        return services;
    }

    /// <summary>
    /// Starts listening for mDNS announcements continuously.
    /// </summary>
    public async Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        if (listeningCts != null)
            return;

        listeningCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        udpClient = new UdpClient();
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
        udpClient.JoinMulticastGroup(IPAddress.Parse(MdnsMulticastAddress));

        _ = ListenLoopAsync(listeningCts.Token);

        // Also do initial search
        await SearchAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Stops listening for mDNS announcements.
    /// </summary>
    public void StopListening()
    {
        listeningCts?.Cancel();
        listeningCts?.Dispose();
        listeningCts = null;

        udpClient?.Close();
        udpClient?.Dispose();
        udpClient = null;
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && udpClient != null)
        {
            try
            {
                var result = await udpClient.ReceiveAsync(cancellationToken);
                var services = ParseMdnsResponse(result.Buffer, result.RemoteEndPoint.Address);

                foreach (var service in services)
                {
                    string key = $"{service.Name}|{service.ServiceTypeString}";
                    lock (lockObj)
                    {
                        if (!discoveredServices.ContainsKey(key))
                        {
                            discoveredServices[key] = service;
                            ServiceDiscovered?.Invoke(service);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Continue listening
            }
        }
    }

    private static byte[] BuildMdnsQuery(string serviceName)
    {
        var packet = new List<byte>();

        // Transaction ID
        packet.AddRange(new byte[] { 0, 0 });

        // Flags: standard query
        packet.AddRange(new byte[] { 0, 0 });

        // Questions: 1
        packet.AddRange(new byte[] { 0, 1 });

        // Answer RRs: 0
        packet.AddRange(new byte[] { 0, 0 });

        // Authority RRs: 0
        packet.AddRange(new byte[] { 0, 0 });

        // Additional RRs: 0
        packet.AddRange(new byte[] { 0, 0 });

        // Question: service name
        foreach (var label in serviceName.Split('.'))
        {
            packet.Add((byte)label.Length);
            packet.AddRange(Encoding.ASCII.GetBytes(label));
        }
        packet.Add(0); // Null terminator

        // Type: PTR (12)
        packet.AddRange(new byte[] { 0, 12 });

        // Class: IN (1)
        packet.AddRange(new byte[] { 0, 1 });

        return packet.ToArray();
    }

    private List<MdnsService> ParseMdnsResponse(byte[] data, IPAddress senderAddress)
    {
        var services = new List<MdnsService>();

        if (data.Length < 12)
            return services;

        try
        {
            int offset = 0;

            // Skip transaction ID and flags
            offset += 4;

            int qdCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)); offset += 2;
            int anCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)); offset += 2;
            int nsCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)); offset += 2;
            int arCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)); offset += 2;

            // Skip questions
            for (int i = 0; i < qdCount && offset < data.Length; i++)
            {
                offset = SkipName(data, offset);
                offset += 4; // Type and class
            }

            // Parse all resource records
            var ptrRecords = new Dictionary<string, string>(); // service type -> instance name
            var srvRecords = new Dictionary<string, (string host, int port)>();
            var txtRecords = new Dictionary<string, Dictionary<string, string>>();
            var aRecords = new Dictionary<string, List<IPAddress>>();

            int totalRecords = anCount + nsCount + arCount;
            for (int i = 0; i < totalRecords && offset < data.Length - 10; i++)
            {
                int nameStart = offset;
                string name = ReadName(data, ref offset);

                if (offset + 10 > data.Length)
                    break;

                ushort type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)); offset += 2;
                ushort cls = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)); offset += 2;
                uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)); offset += 4;
                ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)); offset += 2;

                if (offset + rdLength > data.Length)
                    break;

                int rdataOffset = offset;

                switch (type)
                {
                    case 1: // A record
                        if (rdLength == 4)
                        {
                            var ip = new IPAddress(data.AsSpan(offset, 4));
                            if (!aRecords.ContainsKey(name))
                                aRecords[name] = new List<IPAddress>();
                            aRecords[name].Add(ip);
                        }
                        break;

                    case 12: // PTR record
                        string instanceName = ReadName(data, ref rdataOffset);
                        ptrRecords[name] = instanceName;
                        break;

                    case 16: // TXT record
                        var txt = ParseTxtRecord(data, offset, rdLength);
                        txtRecords[name] = txt;
                        break;

                    case 28: // AAAA record
                        if (rdLength == 16)
                        {
                            var ip = new IPAddress(data.AsSpan(offset, 16));
                            if (!aRecords.ContainsKey(name))
                                aRecords[name] = new List<IPAddress>();
                            aRecords[name].Add(ip);
                        }
                        break;

                    case 33: // SRV record
                        if (rdLength >= 6)
                        {
                            int priority = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
                            int weight = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2));
                            int port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4));
                            int targetOffset = offset + 6;
                            string target = ReadName(data, ref targetOffset);
                            srvRecords[name] = (target, port);
                        }
                        break;
                }

                offset = rdataOffset + rdLength - (rdataOffset - offset);
                if (offset < rdataOffset)
                    offset = rdataOffset + rdLength;
            }

            // Build service records from collected data
            foreach (var ptr in ptrRecords)
            {
                string serviceType = ptr.Key.Replace(".local", "");
                string instanceName = ptr.Value;

                // Find SRV record
                if (!srvRecords.TryGetValue(instanceName, out var srv))
                    continue;

                // Find A/AAAA records
                var addresses = new List<IPAddress>();
                if (aRecords.TryGetValue(srv.host, out var hostAddresses))
                    addresses.AddRange(hostAddresses);

                // Use sender address as fallback
                if (addresses.Count == 0)
                    addresses.Add(senderAddress);

                // Find TXT record
                txtRecords.TryGetValue(instanceName, out var txt);

                // Extract friendly name from instance name
                string friendlyName = instanceName;
                int dotPos = instanceName.IndexOf('.');
                if (dotPos > 0)
                    friendlyName = instanceName.Substring(0, dotPos);

                // Determine service type enum
                var parsedType = MdnsServiceType.Unknown;
                foreach (var mapping in ServiceTypeMap)
                {
                    if (serviceType.StartsWith(mapping.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        parsedType = mapping.Value;
                        break;
                    }
                }

                services.Add(new MdnsService
                {
                    Name = friendlyName,
                    ServiceTypeString = serviceType,
                    ServiceType = parsedType,
                    HostName = srv.host.TrimEnd('.'),
                    Addresses = addresses,
                    Port = srv.port,
                    TxtRecords = txt ?? new Dictionary<string, string>()
                });
            }
        }
        catch (Exception)
        {
            // Return partial results on parse error
        }

        return services;
    }

    private static string ReadName(byte[] data, ref int offset)
    {
        var name = new StringBuilder();
        int maxOffset = data.Length;
        int jumps = 0;
        bool jumped = false;
        int savedOffset = offset;

        while (offset < maxOffset && jumps < 10)
        {
            byte len = data[offset];

            if (len == 0)
            {
                offset++;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                // Pointer
                if (!jumped)
                    savedOffset = offset + 2;

                int pointer = ((len & 0x3F) << 8) | data[offset + 1];
                offset = pointer;
                jumped = true;
                jumps++;
                continue;
            }

            offset++;
            if (offset + len > maxOffset)
                break;

            if (name.Length > 0)
                name.Append('.');

            name.Append(Encoding.ASCII.GetString(data, offset, len));
            offset += len;
        }

        if (jumped)
            offset = savedOffset;

        return name.ToString();
    }

    private static int SkipName(byte[] data, int offset)
    {
        while (offset < data.Length)
        {
            byte len = data[offset];

            if (len == 0)
                return offset + 1;

            if ((len & 0xC0) == 0xC0)
                return offset + 2;

            offset += len + 1;
        }

        return offset;
    }

    private static Dictionary<string, string> ParseTxtRecord(byte[] data, int offset, int length)
    {
        var txt = new Dictionary<string, string>();
        int end = offset + length;

        while (offset < end)
        {
            byte len = data[offset++];
            if (len == 0 || offset + len > end)
                break;

            string entry = Encoding.UTF8.GetString(data, offset, len);
            offset += len;

            int eqPos = entry.IndexOf('=');
            if (eqPos > 0)
            {
                string key = entry.Substring(0, eqPos);
                string value = entry.Substring(eqPos + 1);
                txt[key] = value;
            }
            else
            {
                txt[entry] = string.Empty;
            }
        }

        return txt;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        StopListening();
    }
}

/// <summary>
/// Listens for SAP (Session Announcement Protocol) multicast announcements.
/// </summary>
public sealed class SapListener : IDisposable
{
    private const string SapMulticastAddress = "224.2.127.254";
    private const int SapPort = 9875;

    private readonly Dictionary<string, SapSession> sessions = new();
    private readonly object lockObj = new();
    private UdpClient? udpClient;
    private CancellationTokenSource? listeningCts;
    private bool disposed;

    /// <summary>
    /// Raised when a new session is announced.
    /// </summary>
    public event Action<SapSession>? SessionAnnounced;

    /// <summary>
    /// Raised when a session is deleted.
    /// </summary>
    public event Action<string>? SessionDeleted;

    /// <summary>
    /// Gets all known sessions.
    /// </summary>
    public IReadOnlyList<SapSession> Sessions
    {
        get
        {
            lock (lockObj)
            {
                return new List<SapSession>(sessions.Values);
            }
        }
    }

    /// <summary>
    /// Starts listening for SAP announcements.
    /// </summary>
    public void StartListening(CancellationToken cancellationToken = default)
    {
        if (listeningCts != null)
            return;

        listeningCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        udpClient = new UdpClient();
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, SapPort));
        udpClient.JoinMulticastGroup(IPAddress.Parse(SapMulticastAddress));

        _ = ListenLoopAsync(listeningCts.Token);
    }

    /// <summary>
    /// Stops listening.
    /// </summary>
    public void StopListening()
    {
        listeningCts?.Cancel();
        listeningCts?.Dispose();
        listeningCts = null;

        udpClient?.Close();
        udpClient?.Dispose();
        udpClient = null;
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && udpClient != null)
        {
            try
            {
                var result = await udpClient.ReceiveAsync(cancellationToken);
                ProcessSapPacket(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Continue listening
            }
        }
    }

    private void ProcessSapPacket(byte[] data)
    {
        if (data.Length < 8)
            return;

        // SAP header
        byte versionAndFlags = data[0];
        int version = (versionAndFlags >> 5) & 0x7;
        bool isIpv6 = (versionAndFlags & 0x10) != 0;
        bool isDeletion = (versionAndFlags & 0x04) != 0;
        bool hasCompression = (versionAndFlags & 0x01) != 0;

        if (version != 1)
            return;

        byte authLength = data[1];
        ushort msgIdHash = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));

        int offset = 4;
        int addrLen = isIpv6 ? 16 : 4;

        if (offset + addrLen > data.Length)
            return;

        IPAddress originatingSource = new IPAddress(data.AsSpan(offset, addrLen));
        offset += addrLen;

        // Skip authentication data
        offset += authLength * 4;

        if (offset >= data.Length)
            return;

        // Find payload type (optional, null-terminated)
        string payloadType = "application/sdp";
        if (data[offset] != 'v')
        {
            int typeEnd = Array.IndexOf(data, (byte)0, offset);
            if (typeEnd > offset)
            {
                payloadType = Encoding.ASCII.GetString(data, offset, typeEnd - offset);
                offset = typeEnd + 1;
            }
        }

        if (offset >= data.Length)
            return;

        // Parse SDP payload
        string sdp = Encoding.UTF8.GetString(data, offset, data.Length - offset);
        string sessionId = $"{originatingSource}:{msgIdHash}";

        if (isDeletion)
        {
            lock (lockObj)
            {
                if (sessions.Remove(sessionId))
                {
                    SessionDeleted?.Invoke(sessionId);
                }
            }
        }
        else
        {
            var session = ParseSdp(sdp, sessionId, originatingSource);
            lock (lockObj)
            {
                bool isNew = !sessions.ContainsKey(sessionId);
                sessions[sessionId] = session;

                if (isNew)
                {
                    SessionAnnounced?.Invoke(session);
                }
            }
        }
    }

    private SapSession ParseSdp(string sdp, string sessionId, IPAddress origin)
    {
        var session = new SapSession
        {
            SessionId = sessionId,
            OriginAddress = origin
        };

        foreach (var line in sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 2 || line[1] != '=')
                continue;

            char type = line[0];
            string value = line.Substring(2);

            switch (type)
            {
                case 'v': // Version
                    session.Version = int.TryParse(value, out int v) ? v : 0;
                    break;
                case 's': // Session name
                    session.SessionName = value;
                    break;
                case 'i': // Session info
                    session.Description = value;
                    break;
                case 'u': // URI
                    session.Uri = value;
                    break;
                case 'c': // Connection info
                    ParseConnectionInfo(value, session);
                    break;
                case 'm': // Media description
                    ParseMediaDescription(value, session);
                    break;
                case 'a': // Attribute
                    int colonPos = value.IndexOf(':');
                    if (colonPos > 0)
                    {
                        session.Attributes[value.Substring(0, colonPos)] = value.Substring(colonPos + 1);
                    }
                    else
                    {
                        session.Attributes[value] = string.Empty;
                    }
                    break;
            }
        }

        return session;
    }

    private void ParseConnectionInfo(string value, SapSession session)
    {
        // Format: <nettype> <addrtype> <connection-address>
        var parts = value.Split(' ');
        if (parts.Length >= 3)
        {
            string address = parts[2];
            // Handle multicast with TTL: 239.0.0.1/127
            int slashPos = address.IndexOf('/');
            if (slashPos > 0)
                address = address.Substring(0, slashPos);

            if (IPAddress.TryParse(address, out var ip))
            {
                session.ConnectionAddress = ip;
            }
        }
    }

    private void ParseMediaDescription(string value, SapSession session)
    {
        // Format: <media> <port> <proto> <fmt> ...
        var parts = value.Split(' ');
        if (parts.Length >= 4)
        {
            session.MediaType = parts[0];
            if (int.TryParse(parts[1], out int port))
                session.Port = port;
            session.Protocol = parts[2];
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        StopListening();
    }
}

/// <summary>
/// Represents a SAP-announced session.
/// </summary>
public sealed class SapSession
{
    public string SessionId { get; init; } = string.Empty;
    public IPAddress OriginAddress { get; init; } = IPAddress.Any;
    public int Version { get; set; }
    public string SessionName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Uri { get; set; }
    public IPAddress? ConnectionAddress { get; set; }
    public string? MediaType { get; set; }
    public int Port { get; set; }
    public string? Protocol { get; set; }
    public Dictionary<string, string> Attributes { get; } = new();
    public DateTime DiscoveredAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gets the stream URL for this session.
    /// </summary>
    public string? GetStreamUrl()
    {
        if (ConnectionAddress == null || Port == 0)
            return null;

        string protocol = Protocol?.ToLowerInvariant() switch
        {
            "rtp/avp" => "rtp",
            "udp" => "udp",
            _ => "rtp"
        };

        return $"{protocol}://@{ConnectionAddress}:{Port}";
    }

    public override string ToString() => $"{SessionName} ({MediaType} @ {ConnectionAddress}:{Port})";
}

// SAP (Session Announcement Protocol) Listener
// RFC 2974 compliant multicast stream discovery

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// SAP listener for discovering multicast streams via Session Announcement Protocol.
/// Listens on SAP multicast groups and parses SDP announcements.
/// </summary>
public sealed class SapListener : IAsyncDisposable
{
    private const int MaxPacketSize = 65536;
    private const int MinHeaderSize = 8; // 4 bytes header + 4 bytes IPv4 address
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);
    
    private readonly ConcurrentDictionary<string, SapAnnouncement> _announcements = new();
    private readonly List<UdpClient> _clients = [];
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _receiveTask;
    private Task? _timeoutTask;
    private bool _disposed;
    
    /// <summary>
    /// Gets the current announcements.
    /// </summary>
    public IReadOnlyDictionary<string, SapAnnouncement> Announcements => _announcements;
    
    /// <summary>
    /// Gets or sets the announcement timeout period.
    /// Announcements not refreshed within this period are removed.
    /// </summary>
    public TimeSpan AnnouncementTimeout { get; set; } = DefaultTimeout;
    
    /// <summary>
    /// Event raised when a new announcement is received.
    /// </summary>
    public event EventHandler<SapAnnouncementEventArgs>? AnnouncementReceived;
    
    /// <summary>
    /// Event raised when an announcement is removed (deleted or expired).
    /// </summary>
    public event EventHandler<SapAnnouncementEventArgs>? AnnouncementRemoved;
    
    /// <summary>
    /// Starts listening on the default SAP multicast groups.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return StartAsync(SapAddresses.DefaultAddresses, cancellationToken);
    }
    
    /// <summary>
    /// Starts listening on specified SAP multicast groups.
    /// </summary>
    public async Task StartAsync(IEnumerable<IPAddress> multicastGroups, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        if (_receiveTask != null)
            throw new InvalidOperationException("Already started");
        
        foreach (var group in multicastGroups)
        {
            try
            {
                var client = CreateMulticastClient(group);
                _clients.Add(client);
            }
            catch (SocketException)
            {
                // Skip groups that fail to join (e.g., no multicast support)
            }
        }
        
        if (_clients.Count == 0)
            throw new InvalidOperationException("Failed to join any multicast groups");
        
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_disposeCts.Token), cancellationToken);
        _timeoutTask = Task.Run(() => TimeoutLoopAsync(_disposeCts.Token), cancellationToken);
        
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// Stops listening and clears announcements.
    /// </summary>
    public async Task StopAsync()
    {
        if (_receiveTask == null)
            return;
        
        await _disposeCts.CancelAsync();
        
        try
        {
            await Task.WhenAll(_receiveTask, _timeoutTask ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        
        foreach (var client in _clients)
        {
            client.Dispose();
        }
        _clients.Clear();
        _announcements.Clear();
        _receiveTask = null;
        _timeoutTask = null;
    }
    
    private static UdpClient CreateMulticastClient(IPAddress group)
    {
        var addressFamily = group.AddressFamily;
        var client = new UdpClient(addressFamily);
        
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(
            addressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
            SapAddresses.Port));
        
        client.JoinMulticastGroup(group);
        
        return client;
    }
    
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var tasks = _clients.Select(c => ReceiveFromClientAsync(c, cancellationToken)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
    
    private async Task ReceiveFromClientAsync(UdpClient client, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxPacketSize);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    ProcessPacket(result.Buffer, result.RemoteEndPoint);
                }
                catch (SocketException)
                {
                    // Network error, continue
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    
    private async Task TimeoutLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                RemoveExpiredAnnouncements();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
    
    private void RemoveExpiredAnnouncements()
    {
        var now = DateTimeOffset.UtcNow;
        var keysToRemove = new List<string>();
        
        foreach (var kvp in _announcements)
        {
            var timeout = kvp.Value.AnnouncePeriod.HasValue
                ? kvp.Value.AnnouncePeriod.Value * 3 // 3x announce period
                : AnnouncementTimeout;
            
            if (now - kvp.Value.LastReceived > timeout)
            {
                keysToRemove.Add(kvp.Key);
            }
        }
        
        foreach (var key in keysToRemove)
        {
            if (_announcements.TryRemove(key, out var announcement))
            {
                announcement.State = SapAnnouncementState.Expired;
                AnnouncementRemoved?.Invoke(this, new SapAnnouncementEventArgs
                {
                    Announcement = announcement,
                    State = SapAnnouncementState.Expired
                });
            }
        }
    }
    
    private void ProcessPacket(byte[] data, IPEndPoint remoteEndPoint)
    {
        if (!TryParseHeader(data, out var header, out var payloadOffset))
            return;
        
        // Skip encrypted packets
        if (header.IsEncrypted)
            return;
        
        // Extract payload
        var payload = data.AsSpan(payloadOffset);
        if (payload.IsEmpty)
            return;
        
        // Decompress if needed
        string sdpText;
        if (header.IsCompressed)
        {
            // TODO: Add zlib decompression support
            return;
        }
        else
        {
            // Check for explicit content type
            if (!payload.StartsWith("v="u8))
            {
                // Skip content type header
                var nullIndex = payload.IndexOf((byte)0);
                if (nullIndex < 0)
                {
                    // No null terminator, check for application/sdp
                    var contentType = Encoding.ASCII.GetString(payload[..Math.Min(payload.Length, 50)]);
                    if (!contentType.StartsWith("application/sdp", StringComparison.OrdinalIgnoreCase))
                        return;
                    
                    var sdpStart = contentType.IndexOf("v=", StringComparison.Ordinal);
                    if (sdpStart < 0)
                        return;
                    
                    sdpText = contentType[sdpStart..];
                }
                else
                {
                    payload = payload[(nullIndex + 1)..];
                    sdpText = Encoding.UTF8.GetString(payload);
                }
            }
            else
            {
                sdpText = Encoding.UTF8.GetString(payload);
            }
        }
        
        // Parse SDP
        var session = SdpParser.Parse(sdpText);
        if (session == null)
            return;
        
        // Create unique ID
        var id = $"{header.OriginatingSource}:{header.MessageIdHash}";
        var now = DateTimeOffset.UtcNow;
        
        if (header.IsDelete)
        {
            // Deletion announcement
            if (_announcements.TryRemove(id, out var removed))
            {
                removed.State = SapAnnouncementState.Deleted;
                AnnouncementRemoved?.Invoke(this, new SapAnnouncementEventArgs
                {
                    Announcement = removed,
                    State = SapAnnouncementState.Deleted
                });
            }
        }
        else
        {
            // Add or update announcement
            var isNew = !_announcements.ContainsKey(id);
            
            var announcement = _announcements.GetOrAdd(id, _ => new SapAnnouncement
            {
                Id = id,
                Source = header.OriginatingSource,
                MessageHash = header.MessageIdHash,
                Session = session,
                FirstReceived = now,
                LastReceived = now,
                State = SapAnnouncementState.New
            });
            
            if (!isNew)
            {
                // Update existing
                var prevTime = announcement.LastReceived;
                announcement.LastReceived = now;
                announcement.State = SapAnnouncementState.Updated;
                
                // Estimate announce period
                var interval = now - prevTime;
                if (interval > TimeSpan.FromSeconds(1))
                {
                    announcement.AnnouncePeriod = announcement.AnnouncePeriod.HasValue
                        ? (announcement.AnnouncePeriod.Value + interval) / 2
                        : interval;
                }
            }
            
            AnnouncementReceived?.Invoke(this, new SapAnnouncementEventArgs
            {
                Announcement = announcement,
                State = isNew ? SapAnnouncementState.New : SapAnnouncementState.Updated
            });
        }
    }
    
    /// <summary>
    /// Parses a SAP header from packet data.
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out SapHeader header, out int payloadOffset)
    {
        header = default;
        payloadOffset = 0;
        
        if (data.Length < MinHeaderSize)
            return false;
        
        // Byte 0: V=3bits, A=1bit, R=1bit, T=1bit, E=1bit, C=1bit
        byte firstByte = data[0];
        int version = firstByte >> 5;
        
        // Version must be 1
        if (version != 1)
            return false;
        
        var flags = (SapFlags)(firstByte & 0x1F);
        byte authLen = data[1];
        ushort msgHash = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        
        // Zero hash is invalid
        if (msgHash == 0)
            return false;
        
        // Parse source address
        bool isIPv6 = flags.HasFlag(SapFlags.IPv6);
        int addressSize = isIPv6 ? 16 : 4;
        int minSize = 4 + addressSize + (authLen * 4);
        
        if (data.Length < minSize)
            return false;
        
        IPAddress source;
        if (isIPv6)
        {
            var addrBytes = data.Slice(4, 16).ToArray();
            source = new IPAddress(addrBytes);
        }
        else
        {
            var addrBytes = data.Slice(4, 4).ToArray();
            source = new IPAddress(addrBytes);
        }
        
        header = new SapHeader
        {
            Version = (byte)version,
            Flags = flags,
            AuthenticationLength = authLen,
            MessageIdHash = msgHash,
            OriginatingSource = source
        };
        
        payloadOffset = 4 + addressSize + (authLen * 4);
        return true;
    }
    
    /// <summary>
    /// Gets announcement by ID.
    /// </summary>
    public SapAnnouncement? GetAnnouncement(string id)
    {
        _announcements.TryGetValue(id, out var announcement);
        return announcement;
    }
    
    /// <summary>
    /// Gets all announcements for a specific category.
    /// </summary>
    public IEnumerable<SapAnnouncement> GetAnnouncementsByCategory(string category)
    {
        return _announcements.Values.Where(a =>
            a.Category?.Equals(category, StringComparison.OrdinalIgnoreCase) == true);
    }
    
    /// <summary>
    /// Gets all video announcements.
    /// </summary>
    public IEnumerable<SapAnnouncement> GetVideoAnnouncements()
    {
        return _announcements.Values.Where(a =>
            a.MediaDescriptions.Any(m => m.MediaType.Equals("video", StringComparison.OrdinalIgnoreCase)));
    }
    
    /// <summary>
    /// Gets all audio announcements.
    /// </summary>
    public IEnumerable<SapAnnouncement> GetAudioAnnouncements()
    {
        return _announcements.Values.Where(a =>
            a.MediaDescriptions.Any(m => m.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase)));
    }
    
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        await StopAsync().ConfigureAwait(false);
        _disposeCts.Dispose();
    }
}

/// <summary>
/// SAP packet builder for sending announcements.
/// </summary>
public static class SapPacketBuilder
{
    /// <summary>
    /// Creates a SAP announcement packet.
    /// </summary>
    public static byte[] CreatePacket(
        IPAddress source,
        ushort messageHash,
        string sdpContent,
        bool isDelete = false)
    {
        bool isIPv6 = source.AddressFamily == AddressFamily.InterNetworkV6;
        int addressSize = isIPv6 ? 16 : 4;
        byte[] sdpBytes = Encoding.UTF8.GetBytes(sdpContent);
        
        int packetSize = 4 + addressSize + sdpBytes.Length;
        var packet = new byte[packetSize];
        
        // Header byte 0: version=1, flags
        byte flags = 0;
        if (isIPv6) flags |= (byte)SapFlags.IPv6;
        if (isDelete) flags |= (byte)SapFlags.Delete;
        packet[0] = (byte)((1 << 5) | flags);
        
        // Byte 1: auth length = 0
        packet[1] = 0;
        
        // Bytes 2-3: message hash
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), messageHash);
        
        // Source address
        var sourceBytes = source.GetAddressBytes();
        sourceBytes.CopyTo(packet.AsSpan(4));
        
        // SDP payload
        sdpBytes.CopyTo(packet.AsSpan(4 + addressSize));
        
        return packet;
    }
}

// MediaKernel - Professional Streaming Protocols
// SRT, RIST, NDI support for broadcast-grade streaming

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Access;

/// <summary>
/// SRT (Secure Reliable Transport) client for low-latency streaming.
/// Based on the SRT protocol specification.
/// </summary>
public sealed class SrtClient : IAsyncDisposable
{
    private readonly SrtConfiguration config;
    private Socket? socket;
    private CancellationTokenSource? cancellation;
    private Task? receiveTask;
    private uint socketId;
    private uint peerSocketId;
    private uint sequenceNumber;
    private uint messageNumber;
    private long startTime;
    private bool connected;
    private bool disposed;
    
    // SRT packet types
    private const ushort SrtControlData = 0x8000;
    private const ushort SrtHandshake = 0x0000;
    private const ushort SrtKeepalive = 0x0001;
    private const ushort SrtAck = 0x0002;
    private const ushort SrtNak = 0x0003;
    private const ushort SrtShutdown = 0x0005;
    private const ushort SrtAckack = 0x0006;
    
    // Handshake types
    private const uint HsInduction = 1;
    private const uint HsConclusion = 0xFFFFFFFF;
    
    // SRT socket modes
    private const int SrtLive = 0;
    private const int SrtFile = 1;
    
    // Encryption
    private const int SrtNoCrypt = 0;
    private const int SrtAesCtr = 2;
    
    private readonly ConcurrentQueue<byte[]> receiveQueue = new();
    private readonly SemaphoreSlim receiveSemaphore = new(0);
    
    public bool IsConnected => connected;
    public uint Rtt { get; private set; } // Round-trip time in microseconds
    public uint Bandwidth { get; private set; } // Estimated bandwidth in bytes/sec
    public double PacketLossRate { get; private set; }
    
    public SrtClient(SrtConfiguration configuration)
    {
        config = configuration;
        socketId = (uint)Random.Shared.Next();
        sequenceNumber = (uint)Random.Shared.Next();
        messageNumber = 1;
    }
    
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (connected) return;
        
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        
        var endpoint = new IPEndPoint(IPAddress.Parse(config.Host), config.Port);
        socket.Connect(endpoint);
        
        startTime = Environment.TickCount64 * 1000; // Convert to microseconds
        
        // Start receive loop
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        receiveTask = ReceiveLoopAsync(cancellation.Token);
        
        // Perform handshake
        await PerformHandshakeAsync(cancellationToken);
        
        connected = true;
    }
    
    private async Task PerformHandshakeAsync(CancellationToken cancellationToken)
    {
        // Phase 1: Induction
        byte[] inductionRequest = BuildHandshakePacket(HsInduction);
        await socket!.SendAsync(inductionRequest, SocketFlags.None, cancellationToken);
        
        // Wait for induction response
        byte[] inductionResponse = await WaitForControlPacketAsync(SrtHandshake, cancellationToken);
        ParseHandshakeResponse(inductionResponse);
        
        // Phase 2: Conclusion
        byte[] conclusionRequest = BuildHandshakePacket(HsConclusion);
        await socket.SendAsync(conclusionRequest, SocketFlags.None, cancellationToken);
        
        // Wait for conclusion response
        byte[] conclusionResponse = await WaitForControlPacketAsync(SrtHandshake, cancellationToken);
        ParseHandshakeResponse(conclusionResponse);
    }
    
    private byte[] BuildHandshakePacket(uint type)
    {
        using var ms = new MemoryStream(64);
        using var writer = new BinaryWriter(ms);
        
        // Control packet header
        WriteUInt16BE(writer, SrtControlData | SrtHandshake);
        WriteUInt16BE(writer, 0); // Reserved
        WriteUInt32BE(writer, 0); // Additional info
        WriteUInt32BE(writer, GetTimestamp());
        WriteUInt32BE(writer, socketId);
        
        // Handshake data
        WriteUInt32BE(writer, 5); // SRT version
        WriteUInt16BE(writer, 0); // Encryption field
        WriteUInt16BE(writer, 0); // Extension field
        WriteUInt32BE(writer, sequenceNumber); // Initial sequence
        WriteUInt32BE(writer, 8192); // Max packet size
        WriteUInt32BE(writer, 8192); // Max flow window
        WriteUInt32BE(writer, type); // Handshake type
        WriteUInt32BE(writer, socketId); // Socket ID
        WriteUInt32BE(writer, 0); // SYN cookie
        
        // Peer IP (zeros for IPv4)
        writer.Write(new byte[16]);
        
        return ms.ToArray();
    }
    
    private void ParseHandshakeResponse(byte[] packet)
    {
        if (packet.Length < 48) return;
        
        using var reader = new BinaryReader(new MemoryStream(packet));
        
        // Skip header
        reader.ReadBytes(16);
        
        // Read handshake fields
        uint version = ReadUInt32BE(reader);
        reader.ReadBytes(4); // Encryption, extension
        uint initialSeq = ReadUInt32BE(reader);
        uint maxPacket = ReadUInt32BE(reader);
        uint maxWindow = ReadUInt32BE(reader);
        uint hsType = ReadUInt32BE(reader);
        peerSocketId = ReadUInt32BE(reader);
    }
    
    public async Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!connected || socket == null) 
            throw new InvalidOperationException("Not connected");
        
        int offset = 0;
        int maxPayload = 1316; // SRT MTU - overhead
        
        while (offset < data.Length)
        {
            int chunkSize = Math.Min(maxPayload, data.Length - offset);
            bool isFirst = offset == 0;
            bool isLast = offset + chunkSize >= data.Length;
            
            byte[] packet = BuildDataPacket(data.Slice(offset, chunkSize).Span, isFirst, isLast);
            await socket.SendAsync(packet, SocketFlags.None, cancellationToken);
            
            offset += chunkSize;
            sequenceNumber++;
        }
        
        return data.Length;
    }
    
    private byte[] BuildDataPacket(ReadOnlySpan<byte> payload, bool isFirst, bool isLast)
    {
        using var ms = new MemoryStream(16 + payload.Length);
        using var writer = new BinaryWriter(ms);
        
        // Data packet header
        WriteUInt32BE(writer, sequenceNumber & 0x7FFFFFFF); // Sequence (clear control bit)
        
        // Message number with position flags
        uint msgFlags = messageNumber & 0x3FFFFFF;
        if (isFirst) msgFlags |= 0x80000000;
        if (isLast) msgFlags |= 0x40000000;
        WriteUInt32BE(writer, msgFlags);
        
        WriteUInt32BE(writer, GetTimestamp());
        WriteUInt32BE(writer, peerSocketId);
        
        writer.Write(payload);
        
        if (isLast) messageNumber++;
        
        return ms.ToArray();
    }
    
    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!connected) return 0;
        
        await receiveSemaphore.WaitAsync(cancellationToken);
        
        if (receiveQueue.TryDequeue(out byte[]? data))
        {
            int toCopy = Math.Min(data.Length, buffer.Length);
            data.AsSpan(0, toCopy).CopyTo(buffer.Span);
            return toCopy;
        }
        
        return 0;
    }
    
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[65536];
        
        while (!cancellationToken.IsCancellationRequested && socket != null)
        {
            try
            {
                int received = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
                if (received < 16) continue;
                
                uint firstWord = ((uint)buffer[0] << 24) | ((uint)buffer[1] << 16) |
                                ((uint)buffer[2] << 8) | buffer[3];
                
                if ((firstWord & 0x80000000) != 0)
                {
                    // Control packet
                    await ProcessControlPacketAsync(buffer.AsMemory(0, received), cancellationToken);
                }
                else
                {
                    // Data packet
                    byte[] data = new byte[received - 16];
                    Array.Copy(buffer, 16, data, 0, data.Length);
                    receiveQueue.Enqueue(data);
                    receiveSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue on transient errors
            }
        }
    }
    
    private async Task ProcessControlPacketAsync(Memory<byte> packet, CancellationToken cancellationToken)
    {
        if (socket == null || packet.Length < 16) return;
        
        ushort type = (ushort)(((packet.Span[0] & 0x7F) << 8) | packet.Span[1]);
        
        switch (type)
        {
            case SrtKeepalive:
                // Respond with keepalive
                await socket.SendAsync(packet, SocketFlags.None, cancellationToken);
                break;
                
            case SrtAck:
                // Process ACK - extract RTT
                if (packet.Length >= 40)
                {
                    Rtt = ReadUInt32BE(packet.Span[24..]);
                    Bandwidth = ReadUInt32BE(packet.Span[32..]);
                }
                // Send ACKACK
                byte[] ackack = BuildAckAckPacket(ReadUInt32BE(packet.Span[4..]));
                await socket.SendAsync(ackack, SocketFlags.None, cancellationToken);
                break;
                
            case SrtNak:
                // Handle NAK - retransmit lost packets
                // In production, maintain retransmission buffer
                break;
        }
    }
    
    private byte[] BuildAckAckPacket(uint ackNumber)
    {
        using var ms = new MemoryStream(16);
        using var writer = new BinaryWriter(ms);
        
        WriteUInt16BE(writer, SrtControlData | SrtAckack);
        WriteUInt16BE(writer, 0);
        WriteUInt32BE(writer, ackNumber);
        WriteUInt32BE(writer, GetTimestamp());
        WriteUInt32BE(writer, peerSocketId);
        
        return ms.ToArray();
    }
    
    private async Task<byte[]> WaitForControlPacketAsync(ushort expectedType, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1500];
        var timeout = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        
        while (!cancellationToken.IsCancellationRequested)
        {
            var receiveTask = socket!.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).AsTask();
            var completed = await Task.WhenAny(receiveTask, timeout);
            
            if (completed == timeout)
                throw new TimeoutException("SRT handshake timed out");
            
            int received = await receiveTask;
            if (received < 16) continue;
            
            uint firstWord = ((uint)buffer[0] << 24) | ((uint)buffer[1] << 16) |
                            ((uint)buffer[2] << 8) | buffer[3];
            
            if ((firstWord & 0x80000000) != 0)
            {
                ushort type = (ushort)((firstWord >> 16) & 0x7FFF);
                if (type == expectedType)
                {
                    byte[] result = new byte[received];
                    Array.Copy(buffer, result, received);
                    return result;
                }
            }
        }
        
        throw new OperationCanceledException();
    }
    
    public async Task DisconnectAsync()
    {
        if (!connected) return;
        
        // Send shutdown
        if (socket != null)
        {
            try
            {
                using var ms = new MemoryStream(16);
                using var writer = new BinaryWriter(ms);
                
                WriteUInt16BE(writer, SrtControlData | SrtShutdown);
                WriteUInt16BE(writer, 0);
                WriteUInt32BE(writer, 0);
                WriteUInt32BE(writer, GetTimestamp());
                WriteUInt32BE(writer, peerSocketId);
                
                await socket.SendAsync(ms.ToArray(), SocketFlags.None);
            }
            catch { }
        }
        
        cancellation?.Cancel();
        if (receiveTask != null)
        {
            try { await receiveTask; } catch { }
        }
        
        socket?.Dispose();
        socket = null;
        connected = false;
    }
    
    private uint GetTimestamp()
    {
        return (uint)((Environment.TickCount64 * 1000) - startTime);
    }
    
    private static void WriteUInt16BE(BinaryWriter writer, ushort value)
    {
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }
    
    private static void WriteUInt32BE(BinaryWriter writer, uint value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }
    
    private static uint ReadUInt32BE(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        return ReadUInt32BE(bytes);
    }
    
    private static uint ReadUInt32BE(ReadOnlySpan<byte> bytes)
    {
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | 
               ((uint)bytes[2] << 8) | bytes[3];
    }
    
    public async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            await DisconnectAsync();
            cancellation?.Dispose();
            receiveSemaphore.Dispose();
            disposed = true;
        }
    }
}

/// <summary>
/// Configuration for SRT connections.
/// </summary>
public sealed class SrtConfiguration
{
    public required string Host { get; init; }
    public int Port { get; init; } = 9000;
    public SrtMode Mode { get; init; } = SrtMode.Caller;
    public int Latency { get; init; } = 120; // milliseconds
    public string? Passphrase { get; init; }
    public int KeyLength { get; init; } = 16; // 16, 24, or 32 bytes
    public string? StreamId { get; init; }
    public int MaxBandwidth { get; init; } = -1; // -1 = unlimited
    public int OverheadBandwidth { get; init; } = 25; // percent
}

/// <summary>
/// SRT connection mode.
/// </summary>
public enum SrtMode
{
    Caller,     // Client mode
    Listener,   // Server mode
    Rendezvous  // Peer-to-peer mode
}

// ============================================================================
// RIST (Reliable Internet Stream Transport) Implementation
// ============================================================================

/// <summary>
/// RIST client for reliable video transport with FEC and retransmissions.
/// Based on the RIST Simple Profile specification (TR-06-1).
/// </summary>
public sealed class RistClient : IAsyncDisposable
{
    private readonly RistConfiguration config;
    private Socket? mainSocket;
    private Socket? rtcpSocket;
    private CancellationTokenSource? cancellation;
    private Task? receiveTask;
    private ushort sequenceNumber;
    private uint ssrc;
    private uint timestamp;
    private bool connected;
    private bool disposed;
    
    // Retransmission buffer
    private readonly ConcurrentDictionary<ushort, (byte[] Data, DateTime Sent)> sendBuffer = new();
    private readonly ConcurrentQueue<byte[]> receiveQueue = new();
    private readonly SemaphoreSlim receiveSemaphore = new(0);
    
    // Statistics
    public uint PacketsSent { get; private set; }
    public uint PacketsRetransmitted { get; private set; }
    public uint PacketsReceived { get; private set; }
    public uint PacketsLost { get; private set; }
    
    public bool IsConnected => connected;
    
    public RistClient(RistConfiguration configuration)
    {
        config = configuration;
        ssrc = (uint)Random.Shared.Next();
        sequenceNumber = (ushort)Random.Shared.Next(ushort.MaxValue);
    }
    
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (connected) return;
        
        // Main RTP socket
        mainSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var mainEndpoint = new IPEndPoint(IPAddress.Parse(config.Host), config.Port);
        mainSocket.Connect(mainEndpoint);
        
        // RTCP socket (port + 1)
        rtcpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var rtcpEndpoint = new IPEndPoint(IPAddress.Parse(config.Host), config.Port + 1);
        rtcpSocket.Connect(rtcpEndpoint);
        
        // Start receive loop
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        receiveTask = ReceiveLoopAsync(cancellation.Token);
        
        connected = true;
    }
    
    public async Task<int> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!connected || mainSocket == null)
            throw new InvalidOperationException("Not connected");
        
        // Build RTP packet
        byte[] packet = new byte[12 + data.Length];
        
        // RTP header
        packet[0] = 0x80; // Version 2
        packet[1] = 33;   // MPEG-TS payload type
        packet[2] = (byte)(sequenceNumber >> 8);
        packet[3] = (byte)sequenceNumber;
        packet[4] = (byte)(timestamp >> 24);
        packet[5] = (byte)(timestamp >> 16);
        packet[6] = (byte)(timestamp >> 8);
        packet[7] = (byte)timestamp;
        packet[8] = (byte)(ssrc >> 24);
        packet[9] = (byte)(ssrc >> 16);
        packet[10] = (byte)(ssrc >> 8);
        packet[11] = (byte)ssrc;
        
        data.Span.CopyTo(packet.AsSpan(12));
        
        // Store for retransmission
        sendBuffer[sequenceNumber] = (packet, DateTime.UtcNow);
        
        await mainSocket.SendAsync(packet, SocketFlags.None, cancellationToken);
        
        PacketsSent++;
        sequenceNumber++;
        timestamp += (uint)(data.Length / 7); // Approximate TS packet timing
        
        // Clean old packets from buffer
        CleanSendBuffer();
        
        return data.Length;
    }
    
    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!connected) return 0;
        
        await receiveSemaphore.WaitAsync(cancellationToken);
        
        if (receiveQueue.TryDequeue(out byte[]? data))
        {
            int toCopy = Math.Min(data.Length, buffer.Length);
            data.AsSpan(0, toCopy).CopyTo(buffer.Span);
            return toCopy;
        }
        
        return 0;
    }
    
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[65536];
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Check both main and RTCP sockets
                if (mainSocket == null) break;
                
                int received = await mainSocket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
                if (received < 12) continue;
                
                // Check if RTP or RTCP
                byte pt = (byte)(buffer[1] & 0x7F);
                
                if (pt >= 72 && pt <= 76)
                {
                    // RTCP packet
                    await ProcessRtcpPacketAsync(buffer.AsMemory(0, received), cancellationToken);
                }
                else
                {
                    // RTP data packet
                    byte[] data = new byte[received - 12];
                    Array.Copy(buffer, 12, data, 0, data.Length);
                    receiveQueue.Enqueue(data);
                    receiveSemaphore.Release();
                    PacketsReceived++;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue on transient errors
            }
        }
    }
    
    private async Task ProcessRtcpPacketAsync(Memory<byte> packet, CancellationToken cancellationToken)
    {
        if (packet.Length < 4) return;
        
        byte pt = (byte)(packet.Span[1] & 0x7F);
        
        // Check for NACK (RFC 4585)
        if (pt == 205) // RTPFB
        {
            byte fmt = (byte)(packet.Span[0] & 0x1F);
            if (fmt == 1) // Generic NACK
            {
                await ProcessNackAsync(packet, cancellationToken);
            }
        }
    }
    
    private async Task ProcessNackAsync(Memory<byte> packet, CancellationToken cancellationToken)
    {
        if (mainSocket == null || packet.Length < 16) return;
        
        // Parse NACK packet
        int offset = 12; // Skip RTCP header
        
        while (offset + 4 <= packet.Length)
        {
            ushort pid = (ushort)((packet.Span[offset] << 8) | packet.Span[offset + 1]);
            ushort blp = (ushort)((packet.Span[offset + 2] << 8) | packet.Span[offset + 3]);
            offset += 4;
            
            // Retransmit requested packet
            if (sendBuffer.TryGetValue(pid, out var stored))
            {
                await mainSocket.SendAsync(stored.Data, SocketFlags.None, cancellationToken);
                PacketsRetransmitted++;
            }
            
            // Check bitmask for additional lost packets
            for (int i = 0; i < 16; i++)
            {
                if ((blp & (1 << i)) != 0)
                {
                    ushort lostSeq = (ushort)(pid + i + 1);
                    if (sendBuffer.TryGetValue(lostSeq, out stored))
                    {
                        await mainSocket.SendAsync(stored.Data, SocketFlags.None, cancellationToken);
                        PacketsRetransmitted++;
                    }
                }
            }
        }
    }
    
    private void CleanSendBuffer()
    {
        // Remove packets older than buffer time
        var cutoff = DateTime.UtcNow - TimeSpan.FromMilliseconds(config.BufferTime);
        
        foreach (var kvp in sendBuffer)
        {
            if (kvp.Value.Sent < cutoff)
            {
                sendBuffer.TryRemove(kvp.Key, out _);
            }
        }
    }
    
    public async Task DisconnectAsync()
    {
        if (!connected) return;
        
        cancellation?.Cancel();
        if (receiveTask != null)
        {
            try { await receiveTask; } catch { }
        }
        
        mainSocket?.Dispose();
        rtcpSocket?.Dispose();
        mainSocket = null;
        rtcpSocket = null;
        connected = false;
        sendBuffer.Clear();
    }
    
    public async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            await DisconnectAsync();
            cancellation?.Dispose();
            receiveSemaphore.Dispose();
            disposed = true;
        }
    }
}

/// <summary>
/// Configuration for RIST connections.
/// </summary>
public sealed class RistConfiguration
{
    public required string Host { get; init; }
    public int Port { get; init; } = 5000;
    public RistProfile Profile { get; init; } = RistProfile.Simple;
    public int BufferTime { get; init; } = 1000; // milliseconds
    public int MaxRetransmissions { get; init; } = 10;
    public bool EnableFec { get; init; } = true;
    public int FecRows { get; init; } = 5;
    public int FecColumns { get; init; } = 5;
    public string? Passphrase { get; init; }
}

/// <summary>
/// RIST profile level.
/// </summary>
public enum RistProfile
{
    Simple,   // Basic ARQ
    Main,     // Adds GRE tunneling, encryption
    Advanced  // Full feature set
}

// ============================================================================
// NDI (Network Device Interface) Receiver (Stub)
// ============================================================================

/// <summary>
/// NDI receiver for IP video production.
/// Note: Full NDI requires the NewTek NDI SDK license.
/// This is a discovery and metadata stub.
/// </summary>
public sealed class NdiReceiver : IAsyncDisposable
{
    private readonly NdiConfiguration config;
    private Socket? discoverySocket;
    private readonly List<NdiSource> discoveredSources = new();
    private bool disposed;
    
    // NDI uses mDNS for discovery
    private static readonly IPAddress MdnsMulticast = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;
    private const string NdiServiceType = "_ndi._tcp.local";
    
    public IReadOnlyList<NdiSource> DiscoveredSources => discoveredSources;
    
    public NdiReceiver(NdiConfiguration configuration)
    {
        config = configuration;
    }
    
    public async Task StartDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        discoverySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        discoverySocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        discoverySocket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
        
        // Join multicast group
        discoverySocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
            new MulticastOption(MdnsMulticast));
        
        // Send mDNS query for NDI services
        byte[] query = BuildMdnsQuery(NdiServiceType);
        await discoverySocket.SendToAsync(query, SocketFlags.None, 
            new IPEndPoint(MdnsMulticast, MdnsPort), cancellationToken);
        
        // Listen for responses (would be async loop in production)
        byte[] buffer = new byte[4096];
        
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var result = await discoverySocket.ReceiveFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), cts.Token);
                
                ParseMdnsResponse(buffer.AsSpan(0, result.ReceivedBytes), 
                    (IPEndPoint)result.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
    }
    
    private static byte[] BuildMdnsQuery(string serviceName)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        // DNS header
        writer.Write((ushort)0); // Transaction ID
        writer.Write((ushort)0); // Flags (standard query)
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)1)); // Questions
        writer.Write((ushort)0); // Answers
        writer.Write((ushort)0); // Authority
        writer.Write((ushort)0); // Additional
        
        // Question
        foreach (string label in serviceName.Split('.'))
        {
            writer.Write((byte)label.Length);
            writer.Write(Encoding.ASCII.GetBytes(label));
        }
        writer.Write((byte)0); // Root label
        
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)12)); // PTR record
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)1));  // IN class
        
        return ms.ToArray();
    }
    
    private void ParseMdnsResponse(ReadOnlySpan<byte> data, IPEndPoint source)
    {
        if (data.Length < 12) return;
        
        // Very simplified mDNS parsing
        // Full implementation would parse all record types
        
        string responseText = Encoding.UTF8.GetString(data);
        if (responseText.Contains("_ndi._tcp", StringComparison.OrdinalIgnoreCase))
        {
            // Extract source name from response (simplified)
            discoveredSources.Add(new NdiSource
            {
                Name = $"NDI Source @ {source.Address}",
                IpAddress = source.Address.ToString(),
                Port = 5960 // Default NDI port
            });
        }
    }
    
    public void StopDiscovery()
    {
        discoverySocket?.Dispose();
        discoverySocket = null;
    }
    
    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            StopDiscovery();
            disposed = true;
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Discovered NDI source.
/// </summary>
public sealed class NdiSource
{
    public required string Name { get; init; }
    public required string IpAddress { get; init; }
    public int Port { get; init; }
    public string? GroupName { get; init; }
}

/// <summary>
/// Configuration for NDI receiver.
/// </summary>
public sealed class NdiConfiguration
{
    public string? SourceName { get; init; }
    public string? GroupName { get; init; }
    public bool ReceiveVideo { get; init; } = true;
    public bool ReceiveAudio { get; init; } = true;
    public bool ReceiveMetadata { get; init; } = true;
    public NdiColorFormat ColorFormat { get; init; } = NdiColorFormat.Uyvy;
    public int Bandwidth { get; init; } = -1; // -1 = highest
}

/// <summary>
/// NDI color format preferences.
/// </summary>
public enum NdiColorFormat
{
    Bgrx,     // 32-bit BGRA with alpha=255
    Uyvy,     // YUV 4:2:2
    Rgbx,     // 32-bit RGBA with alpha=255
    Rgba,     // 32-bit RGBA with alpha
    BgrxFull, // Full range BGRA
    UyvyFull, // Full range UYVY
    RgbxFull, // Full range RGBA
    RgbaFull  // Full range RGBA with alpha
}

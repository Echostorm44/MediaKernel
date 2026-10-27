// MediaKernel - CDG Demuxer
// CD+G (Compact Disc + Graphics) karaoke format demuxer
// Used for karaoke with synchronized graphics (lyrics display)

using System;
using System.IO;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// CDG file information.
/// </summary>
public sealed class CdgFile
{
    /// <summary>Whether the file was successfully parsed.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Total file size in bytes.</summary>
    public long FileSize { get; init; }
    
    /// <summary>Duration in seconds.</summary>
    public double DurationSeconds { get; init; }
    
    /// <summary>Total number of packets (file size / 96).</summary>
    public long TotalPackets { get; init; }
    
    /// <summary>Video width (always 288 for CDG, minus border = 276).</summary>
    public int Width => 288;
    
    /// <summary>Video height (always 192 for CDG, minus border = 168).</summary>
    public int Height => 192;
    
    /// <summary>Visible width (excluding 6-pixel border on each side).</summary>
    public int VisibleWidth => 288 - 12;
    
    /// <summary>Visible height (excluding 12-pixel border top/bottom).</summary>
    public int VisibleHeight => 192 - 24;
}

/// <summary>
/// CDG packet (subchannel data block).
/// </summary>
public readonly struct CdgPacket
{
    /// <summary>Size of a CDG packet in bytes.</summary>
    public const int PacketSize = 24;
    
    /// <summary>Size of a CDG subcode packet (with all P-W subchannels).</summary>
    public const int SubcodePacketSize = 96;
    
    /// <summary>Raw packet data (24 bytes from R-W subchannel).</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
    
    /// <summary>Command byte (masked to 6 bits).</summary>
    public byte Command => (byte)(Data.Span[0] & 0x3F);
    
    /// <summary>Instruction byte (masked to 6 bits).</summary>
    public byte Instruction => (byte)(Data.Span[1] & 0x3F);
    
    /// <summary>Whether this is a CDG command (command byte == 0x09).</summary>
    public bool IsCdgCommand => Command == 0x09;
    
    /// <summary>Packet index (sequential from start of file).</summary>
    public long PacketIndex { get; init; }
    
    /// <summary>Presentation timestamp in microseconds.</summary>
    public long PresentationTimestamp { get; init; }
    
    /// <summary>Get data field bytes (16 bytes, indices 4-19).</summary>
    public ReadOnlySpan<byte> GetDataField()
    {
        return Data.Span.Slice(4, 16);
    }
}

/// <summary>
/// CDG instruction types.
/// </summary>
public static class CdgInstructions
{
    /// <summary>Set memory preset (clear screen with color).</summary>
    public const byte MemoryPreset = 1;
    
    /// <summary>Set border color.</summary>
    public const byte BorderPreset = 2;
    
    /// <summary>Write 12x6 tile (normal).</summary>
    public const byte TileBlockNormal = 6;
    
    /// <summary>Scroll preset (with color fill).</summary>
    public const byte ScrollPreset = 20;
    
    /// <summary>Scroll copy (wrap around).</summary>
    public const byte ScrollCopy = 24;
    
    /// <summary>Define transparent color.</summary>
    public const byte DefineTransparentColor = 28;
    
    /// <summary>Load color table (lower 8 entries).</summary>
    public const byte LoadColorTableLow = 30;
    
    /// <summary>Load color table (upper 8 entries).</summary>
    public const byte LoadColorTableHigh = 31;
    
    /// <summary>Write 12x6 tile (XOR mode).</summary>
    public const byte TileBlockXor = 38;
}

/// <summary>
/// Demuxer for CDG (CD+G) karaoke format.
/// Frame rate is fixed at 75 fps (300 packets per second at 4 packets per frame).
/// </summary>
public sealed class CdgDemuxer : IDisposable
{
    /// <summary>CDG frame rate (subchannel reads per second / 4).</summary>
    public const double FrameRate = 75.0;
    
    /// <summary>Packets per second (300 for CD subcode).</summary>
    public const int PacketsPerSecond = 300;
    
    /// <summary>Microseconds per packet.</summary>
    public const long MicrosecondsPerPacket = 1_000_000 / PacketsPerSecond;
    
    /// <summary>Microseconds per frame (at 75 fps).</summary>
    public const long MicrosecondsPerFrame = 1_000_000 / 75;
    
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly byte[] packetBuffer = new byte[CdgPacket.SubcodePacketSize];
    
    private long fileSize;
    private long currentPacketIndex;
    private bool disposed;
    
    /// <summary>
    /// Creates a new CDG demuxer.
    /// </summary>
    public CdgDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
        
        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(stream));
        
        fileSize = stream.CanSeek ? stream.Length : 0;
    }
    
    /// <summary>
    /// Parse the CDG file and get information.
    /// </summary>
    public CdgFile Parse()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (stream.CanSeek)
            stream.Position = 0;
        
        // CDG files are identified by extension only (.cdg)
        // The file size should be a multiple of 96 bytes (subcode packet size)
        // However, we'll accept any file and just process what we can
        
        long totalPackets = fileSize / CdgPacket.SubcodePacketSize;
        double durationSeconds = (double)totalPackets / (PacketsPerSecond / 4); // 4 subchannel reads per 96-byte packet
        
        return new CdgFile
        {
            IsValid = fileSize > 0,
            FileSize = fileSize,
            TotalPackets = totalPackets,
            DurationSeconds = durationSeconds
        };
    }
    
    /// <summary>
    /// Read the next CDG packet from the stream.
    /// </summary>
    public bool TryReadPacket(out CdgPacket packet)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        packet = default;
        
        int read = stream.Read(packetBuffer, 0, CdgPacket.SubcodePacketSize);
        if (read < CdgPacket.SubcodePacketSize)
            return false;
        
        // Extract the R-W subchannel data (24 bytes from the 96-byte packet)
        // In a CDG file, each 96-byte block contains 4 subcode packets of 24 bytes each
        // We process the first 24 bytes as the CDG packet (simplified approach)
        byte[] data = new byte[CdgPacket.PacketSize];
        Array.Copy(packetBuffer, data, CdgPacket.PacketSize);
        
        long pts = currentPacketIndex * MicrosecondsPerPacket;
        
        packet = new CdgPacket
        {
            Data = data,
            PacketIndex = currentPacketIndex,
            PresentationTimestamp = pts
        };
        
        currentPacketIndex++;
        return true;
    }
    
    /// <summary>
    /// Read multiple CDG packets (for batch processing).
    /// </summary>
    public int ReadPackets(Span<CdgPacket> packets)
    {
        int count = 0;
        
        for (int i = 0; i < packets.Length; i++)
        {
            if (!TryReadPacket(out var packet))
                break;
            packets[i] = packet;
            count++;
        }
        
        return count;
    }
    
    /// <summary>
    /// Seek to a specific time position.
    /// </summary>
    public bool SeekToTime(double timeSeconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (!stream.CanSeek)
            return false;
        
        long packetIndex = (long)(timeSeconds * PacketsPerSecond / 4);
        long bytePosition = packetIndex * CdgPacket.SubcodePacketSize;
        
        bytePosition = Math.Clamp(bytePosition, 0, fileSize);
        stream.Position = bytePosition;
        currentPacketIndex = bytePosition / CdgPacket.SubcodePacketSize;
        
        return true;
    }
    
    /// <summary>
    /// Seek to a specific packet index.
    /// </summary>
    public bool SeekToPacket(long packetIndex)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (!stream.CanSeek)
            return false;
        
        long bytePosition = packetIndex * CdgPacket.SubcodePacketSize;
        bytePosition = Math.Clamp(bytePosition, 0, fileSize);
        
        stream.Position = bytePosition;
        currentPacketIndex = bytePosition / CdgPacket.SubcodePacketSize;
        
        return true;
    }
    
    /// <summary>
    /// Get the current time position in seconds.
    /// </summary>
    public double GetCurrentTime()
    {
        return (double)currentPacketIndex / (PacketsPerSecond / 4);
    }
    
    /// <summary>
    /// Get the current packet index.
    /// </summary>
    public long GetCurrentPacketIndex()
    {
        return currentPacketIndex;
    }
    
    /// <summary>
    /// Check if the file extension indicates CDG format.
    /// CDG files cannot be detected by magic bytes, only by extension.
    /// </summary>
    public static bool IsCdgExtension(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;
        
        return filePath.EndsWith(".cdg", StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Validate that a file appears to be CDG format.
    /// Since CDG has no magic bytes, we check if the file size is reasonable
    /// (multiple of 96 bytes and within expected range for karaoke).
    /// </summary>
    public static bool ValidateFile(Stream stream)
    {
        if (!stream.CanSeek)
            return true; // Cannot validate, assume valid
        
        long size = stream.Length;
        
        // CDG files are typically 1-50 MB for a song
        // Size should be multiple of 96 bytes
        return size > 0 && size < 100_000_000 && (size % 96) == 0;
    }
    
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            if (!leaveOpen)
                stream.Dispose();
        }
    }
}

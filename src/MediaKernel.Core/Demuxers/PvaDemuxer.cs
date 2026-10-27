// MediaKernel - PVA Demuxer
// PVA (Packet Video Audio) container format used by DVB recording software
// Commonly used for DVB-S/DVB-T TV capture recordings

using System;
using System.Buffers.Binary;
using System.IO;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// PVA file header and format information.
/// </summary>
public sealed class PvaFile
{
    /// <summary>Duration in seconds (estimated from file size).</summary>
    public double DurationSeconds { get; init; }
    
    /// <summary>Total file size in bytes.</summary>
    public long FileSize { get; init; }
    
    /// <summary>Video stream ID (typically 0x01).</summary>
    public byte VideoStreamId { get; init; }
    
    /// <summary>Audio stream ID (typically 0x02).</summary>
    public byte AudioStreamId { get; init; }
    
    /// <summary>Whether the file has been validated.</summary>
    public bool IsValid { get; init; }
}

/// <summary>
/// PVA packet header.
/// </summary>
public readonly struct PvaPacket
{
    /// <summary>Stream ID (0x01 = video, 0x02 = audio).</summary>
    public byte StreamId { get; init; }
    
    /// <summary>Packet counter (0-255 wrapping).</summary>
    public byte Counter { get; init; }
    
    /// <summary>Packet flags.</summary>
    public byte Flags { get; init; }
    
    /// <summary>Payload length in bytes.</summary>
    public ushort PayloadLength { get; init; }
    
    /// <summary>Presentation timestamp in 90kHz units (if HasPts is true).</summary>
    public uint PresentationTimestamp { get; init; }
    
    /// <summary>Whether this packet contains a PTS.</summary>
    public bool HasPts => (Flags & 0x10) != 0;
    
    /// <summary>Number of prebytes before PTS (0-3).</summary>
    public int PrebytesCount => Flags & 0x03;
    
    /// <summary>Whether this is a video packet.</summary>
    public bool IsVideo => StreamId == 0x01;
    
    /// <summary>Whether this is an audio packet.</summary>
    public bool IsAudio => StreamId == 0x02;
    
    /// <summary>Offset of the payload data in the stream.</summary>
    public long PayloadOffset { get; init; }
    
    /// <summary>Convert PTS to seconds.</summary>
    public double PtsToSeconds() => PresentationTimestamp / 90000.0;
}

/// <summary>
/// Demuxer for PVA (Packet Video Audio) container format.
/// Used by DVB recording software (TechnoTrend, WinTV, etc.).
/// </summary>
public sealed class PvaDemuxer : IDisposable
{
    private const int PvaHeaderSize = 8;
    private const byte PvaMagic0 = 0x41; // 'A'
    private const byte PvaMagic1 = 0x56; // 'V'
    private const byte PvaSyncByte = 0x55;
    
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly byte[] headerBuffer = new byte[PvaHeaderSize];
    
    private bool isValid;
    private long fileSize;
    private int videoPacketCounter = -1;
    private int audioPacketCounter = -1;
    private bool disposed;
    
    /// <summary>
    /// Creates a new PVA demuxer.
    /// </summary>
    public PvaDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
        
        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(stream));
        
        fileSize = stream.CanSeek ? stream.Length : 0;
    }
    
    /// <summary>
    /// Parse the PVA file header and validate.
    /// </summary>
    public PvaFile Parse()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (stream.CanSeek)
            stream.Position = 0;
        
        int read = stream.Read(headerBuffer, 0, PvaHeaderSize);
        if (read < PvaHeaderSize)
        {
            return new PvaFile { IsValid = false };
        }
        
        // Validate PVA signature: "AV" at bytes 0-1, sync byte 0x55 at byte 4
        if (headerBuffer[0] != PvaMagic0 || headerBuffer[1] != PvaMagic1 || headerBuffer[4] != PvaSyncByte)
        {
            // Try to resynchronize
            if (!TryResynchronize())
            {
                return new PvaFile { IsValid = false };
            }
        }
        else
        {
            // Valid header at current position - seek back so we can read it properly
            if (stream.CanSeek)
                stream.Position = 0;
        }
        
        isValid = true;
        
        return new PvaFile
        {
            IsValid = true,
            FileSize = fileSize,
            VideoStreamId = 0x01,
            AudioStreamId = 0x02,
            DurationSeconds = 0 // Cannot determine without scanning entire file
        };
    }
    
    /// <summary>
    /// Read the next PVA packet from the stream.
    /// </summary>
    public bool TryReadPacket(out PvaPacket packet)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        packet = default;
        
        while (true)
        {
            int read = stream.Read(headerBuffer, 0, PvaHeaderSize);
            if (read < PvaHeaderSize)
                return false;
            
            // Validate sync
            if (headerBuffer[0] != PvaMagic0 || headerBuffer[1] != PvaMagic1 || headerBuffer[4] != PvaSyncByte)
            {
                // Lost sync - try to resynchronize
                if (!TryResynchronize())
                    return false;
                continue;
            }
            
            byte streamId = headerBuffer[2];
            byte counter = headerBuffer[3];
            byte flags = headerBuffer[5];
            ushort payloadLength = BinaryPrimitives.ReadUInt16BigEndian(headerBuffer.AsSpan(6));
            
            // Track packet loss
            if (streamId == 0x01)
            {
                if (videoPacketCounter >= 0 && ((videoPacketCounter + 1) & 0xFF) != counter)
                {
                    // Packet loss detected
                }
                videoPacketCounter = counter;
            }
            else if (streamId == 0x02)
            {
                if (audioPacketCounter >= 0 && ((audioPacketCounter + 1) & 0xFF) != counter)
                {
                    // Packet loss detected
                }
                audioPacketCounter = counter;
            }
            
            // Read PTS if present (video packets only)
            uint pts = 0;
            int extraBytes = 0;
            
            if ((flags & 0x10) != 0 && streamId == 0x01)
            {
                int prebytes = flags & 0x03;
                extraBytes = 4 + prebytes;
                
                byte[] ptsBuffer = new byte[extraBytes];
                if (stream.Read(ptsBuffer, 0, extraBytes) < extraBytes)
                    return false;
                
                pts = BinaryPrimitives.ReadUInt32BigEndian(ptsBuffer.AsSpan(0, 4));
                payloadLength = (ushort)(payloadLength - extraBytes);
            }
            
            packet = new PvaPacket
            {
                StreamId = streamId,
                Counter = counter,
                Flags = flags,
                PayloadLength = payloadLength,
                PresentationTimestamp = pts,
                PayloadOffset = stream.CanSeek ? stream.Position : -1
            };
            
            return true;
        }
    }
    
    /// <summary>
    /// Read the payload data of a packet.
    /// </summary>
    public int ReadPayload(in PvaPacket packet, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        int bytesToRead = Math.Min(packet.PayloadLength, destination.Length);
        return stream.Read(destination[..bytesToRead]);
    }
    
    /// <summary>
    /// Skip the payload of a packet.
    /// </summary>
    public bool SkipPayload(in PvaPacket packet)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (stream.CanSeek)
        {
            stream.Position += packet.PayloadLength;
            return true;
        }
        
        // Non-seekable: read and discard
        byte[] discard = new byte[Math.Min((int)packet.PayloadLength, 4096)];
        int remaining = packet.PayloadLength;
        
        while (remaining > 0)
        {
            int toRead = Math.Min(remaining, discard.Length);
            int read = stream.Read(discard, 0, toRead);
            if (read == 0)
                return false;
            remaining -= read;
        }
        
        return true;
    }
    
    /// <summary>
    /// Seek to a position in the file (approximate).
    /// </summary>
    public bool Seek(double timeSeconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (!stream.CanSeek || fileSize == 0)
            return false;
        
        // PVA has no index, so we can only seek by position ratio
        // The caller should handle resynchronization after seeking
        long position = (long)(timeSeconds * fileSize / 100); // Rough estimate
        position = Math.Clamp(position, 0, fileSize);
        
        stream.Position = position;
        
        return TryResynchronize();
    }
    
    /// <summary>
    /// Seek to a byte position in the file.
    /// </summary>
    public bool SeekToPosition(long position)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (!stream.CanSeek)
            return false;
        
        stream.Position = Math.Clamp(position, 0, fileSize);
        return TryResynchronize();
    }
    
    /// <summary>
    /// Try to resynchronize after losing sync or seeking.
    /// </summary>
    public bool TryResynchronize()
    {
        byte[] scanBuffer = new byte[1024];
        
        while (true)
        {
            int read = stream.Read(scanBuffer);
            if (read < 5)
                return false;
            
            for (int i = 0; i < read - 4; i++)
            {
                if (scanBuffer[i] == PvaMagic0 && 
                    scanBuffer[i + 1] == PvaMagic1 && 
                    scanBuffer[i + 4] == PvaSyncByte)
                {
                    // Found sync - seek back to start of packet
                    if (stream.CanSeek)
                    {
                        stream.Position -= (read - i);
                        return true;
                    }
                    return false;
                }
            }
            
            // Keep last 4 bytes in case sync spans buffer boundary
            if (stream.CanSeek)
                stream.Position -= 4;
        }
    }
    
    /// <summary>
    /// Check if data starts with PVA magic bytes.
    /// </summary>
    public static bool CanParse(ReadOnlySpan<byte> header)
    {
        if (header.Length < 8)
            return false;
        
        return header[0] == PvaMagic0 && 
               header[1] == PvaMagic1 && 
               header[4] == PvaSyncByte;
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

// MediaKernel - MJPEG Demuxer
// Motion JPEG stream demuxer for webcam and IP camera streams
// Parses JPEG frames from multipart MIME or raw MJPEG streams

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// MJPEG stream information.
/// </summary>
public sealed class MjpegStreamInfo
{
    /// <summary>Whether the stream was successfully parsed.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Whether this is a multipart MIME stream.</summary>
    public bool IsMultipartMime { get; init; }
    
    /// <summary>MIME boundary separator (for multipart streams).</summary>
    public string? Boundary { get; init; }
    
    /// <summary>Frame rate (0 for live streams).</summary>
    public double FrameRate { get; init; }
    
    /// <summary>File size in bytes (0 for live streams).</summary>
    public long FileSize { get; init; }
}

/// <summary>
/// MJPEG frame data.
/// </summary>
public readonly struct MjpegFrame
{
    /// <summary>JPEG frame data.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
    
    /// <summary>Presentation timestamp in microseconds.</summary>
    public long PresentationTimestamp { get; init; }
    
    /// <summary>Frame index.</summary>
    public long FrameIndex { get; init; }
    
    /// <summary>Whether this is a valid JPEG frame.</summary>
    public bool IsValid => Data.Length >= 2 && 
                           Data.Span[0] == 0xFF && 
                           Data.Span[1] == 0xD8;
}

/// <summary>
/// Demuxer for MJPEG (Motion JPEG) streams.
/// Supports multipart MIME and raw MJPEG formats.
/// </summary>
public sealed class MjpegDemuxer : IDisposable
{
    // JPEG markers
    private const byte JpegMarkerPrefix = 0xFF;
    private const byte JpegStartOfImage = 0xD8; // SOI
    private const byte JpegEndOfImage = 0xD9;   // EOI
    
    private const int DefaultFrameSizeEstimate = 15 * 1024;
    private const int MaxFrameSize = 10 * 1024 * 1024; // 10 MB max
    
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly byte[] scanBuffer = new byte[8192];
    
    private string? boundary;
    private bool isMultipartMime;
    private double frameRate;
    private long frameLengthMicroseconds;
    private long fileSize;
    private long frameIndex;
    private long pcrMicroseconds;
    private int frameSizeEstimate = DefaultFrameSizeEstimate;
    private bool disposed;
    
    /// <summary>
    /// Creates a new MJPEG demuxer.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="leaveOpen">Whether to leave the stream open when disposed.</param>
    /// <param name="frameRate">Frame rate for file playback (0 for live streams).</param>
    public MjpegDemuxer(Stream stream, bool leaveOpen = false, double frameRate = 0)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
        this.frameRate = frameRate;
        
        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(stream));
        
        fileSize = stream.CanSeek ? stream.Length : 0;
        frameLengthMicroseconds = frameRate > 0 ? (long)(1_000_000.0 / frameRate) : 0;
    }
    
    /// <summary>
    /// Parse the stream and detect format.
    /// </summary>
    public MjpegStreamInfo Parse()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (stream.CanSeek)
            stream.Position = 0;
        
        int read = stream.Read(scanBuffer, 0, Math.Min(scanBuffer.Length, 1024));
        if (read < 4)
        {
            return new MjpegStreamInfo { IsValid = false };
        }
        
        // Check for multipart MIME boundary
        if (scanBuffer[0] == '-' && scanBuffer[1] == '-')
        {
            // Multipart MIME format
            boundary = ExtractBoundary(scanBuffer.AsSpan(0, read));
            if (boundary != null)
            {
                isMultipartMime = true;
            }
        }
        else if (scanBuffer[0] == '\r' && scanBuffer[1] == '\n' && 
                 scanBuffer[2] == '-' && scanBuffer[3] == '-')
        {
            // CRLF-prefixed boundary
            boundary = ExtractBoundary(scanBuffer.AsSpan(2, read - 2));
            if (boundary != null)
            {
                isMultipartMime = true;
            }
        }
        
        // Check for raw JPEG SOI
        bool hasJpegSoi = false;
        if (!isMultipartMime)
        {
            if (scanBuffer[0] == JpegMarkerPrefix && scanBuffer[1] == JpegStartOfImage)
            {
                hasJpegSoi = true;
            }
        }
        
        // Seek back to start for demuxing
        if (stream.CanSeek)
            stream.Position = 0;
        
        if (!isMultipartMime && !hasJpegSoi)
        {
            return new MjpegStreamInfo { IsValid = false };
        }
        
        return new MjpegStreamInfo
        {
            IsValid = true,
            IsMultipartMime = isMultipartMime,
            Boundary = boundary,
            FrameRate = frameRate,
            FileSize = fileSize
        };
    }
    
    /// <summary>
    /// Read the next JPEG frame from the stream.
    /// </summary>
    public bool TryReadFrame(out MjpegFrame frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        frame = default;
        
        if (isMultipartMime)
        {
            return TryReadMimeFrame(out frame);
        }
        else
        {
            return TryReadRawFrame(out frame);
        }
    }
    
    /// <summary>
    /// Set the frame rate for timestamp generation.
    /// </summary>
    public void SetFrameRate(double fps)
    {
        frameRate = fps;
        frameLengthMicroseconds = fps > 0 ? (long)(1_000_000.0 / fps) : 0;
    }
    
    private bool TryReadRawFrame(out MjpegFrame frame)
    {
        frame = default;
        
        // Find JPEG SOI marker
        int soiPosition = FindMarker(JpegStartOfImage);
        if (soiPosition < 0)
            return false;
        
        // Skip to SOI
        if (soiPosition > 0)
        {
            SkipBytes(soiPosition);
        }
        
        // Read frame data until EOI
        using var frameData = new MemoryStream(frameSizeEstimate);
        
        // Write SOI marker
        frameData.WriteByte(JpegMarkerPrefix);
        frameData.WriteByte(JpegStartOfImage);
        
        // Skip SOI in stream
        SkipBytes(2);
        
        // Find EOI marker
        bool foundEoi = false;
        
        while (!foundEoi)
        {
            int read = stream.Read(scanBuffer, 0, scanBuffer.Length);
            if (read == 0)
                break;
            
            // Scan for EOI marker
            for (int i = 0; i < read - 1; i++)
            {
                if (scanBuffer[i] == JpegMarkerPrefix && scanBuffer[i + 1] == JpegEndOfImage)
                {
                    // Found EOI - write data up to and including EOI
                    frameData.Write(scanBuffer, 0, i + 2);
                    foundEoi = true;
                    
                    // Seek back to position after EOI
                    if (stream.CanSeek)
                    {
                        stream.Position -= (read - i - 2);
                    }
                    break;
                }
            }
            
            if (!foundEoi)
            {
                // Check if EOI might span buffer boundary
                if (scanBuffer[read - 1] == JpegMarkerPrefix)
                {
                    frameData.Write(scanBuffer, 0, read - 1);
                    
                    // Read next byte to check
                    int nextByte = stream.ReadByte();
                    if (nextByte == JpegEndOfImage)
                    {
                        frameData.WriteByte(JpegMarkerPrefix);
                        frameData.WriteByte((byte)nextByte);
                        foundEoi = true;
                    }
                    else if (nextByte >= 0)
                    {
                        frameData.WriteByte(JpegMarkerPrefix);
                        frameData.WriteByte((byte)nextByte);
                    }
                }
                else
                {
                    frameData.Write(scanBuffer, 0, read);
                }
            }
            
            // Safety check
            if (frameData.Length > MaxFrameSize)
            {
                return false;
            }
        }
        
        if (!foundEoi || frameData.Length < 4)
            return false;
        
        // Update frame size estimate
        frameSizeEstimate = Math.Max(frameSizeEstimate, (int)frameData.Length + 1024);
        
        byte[] data = frameData.ToArray();
        
        frame = new MjpegFrame
        {
            Data = data,
            PresentationTimestamp = pcrMicroseconds,
            FrameIndex = frameIndex
        };
        
        frameIndex++;
        pcrMicroseconds += frameLengthMicroseconds;
        
        return true;
    }
    
    private bool TryReadMimeFrame(out MjpegFrame frame)
    {
        frame = default;
        
        // Skip MIME headers until we find Content-Type: image/jpeg
        if (!SkipToMimeContent())
            return false;
        
        // Read JPEG frame until next boundary
        return TryReadMimeContent(out frame);
    }
    
    private bool SkipToMimeContent()
    {
        // Read lines until we find empty line (end of headers)
        while (true)
        {
            string? line = ReadLine();
            if (line == null)
                return false;
            
            // Empty line = end of headers
            if (line.Length == 0)
                return true;
            
            // Check for Content-Type (optional validation)
            // We just skip headers for now
        }
    }
    
    private bool TryReadMimeContent(out MjpegFrame frame)
    {
        frame = default;
        
        using var frameData = new MemoryStream(frameSizeEstimate);
        byte[] boundaryBytes = boundary != null ? 
            Encoding.ASCII.GetBytes("--" + boundary) : 
            "--"u8.ToArray();
        
        while (true)
        {
            int read = stream.Read(scanBuffer, 0, scanBuffer.Length);
            if (read == 0)
                break;
            
            // Look for boundary marker
            int boundaryPos = FindSequence(scanBuffer.AsSpan(0, read), boundaryBytes);
            
            if (boundaryPos >= 0)
            {
                // Found boundary - save data before it
                if (boundaryPos > 0)
                {
                    // Skip CRLF before boundary
                    int dataEnd = boundaryPos;
                    if (dataEnd >= 2 && scanBuffer[dataEnd - 2] == '\r' && scanBuffer[dataEnd - 1] == '\n')
                        dataEnd -= 2;
                    
                    frameData.Write(scanBuffer, 0, dataEnd);
                }
                
                // Position stream after boundary line
                if (stream.CanSeek)
                {
                    stream.Position -= (read - boundaryPos);
                    SkipLine(); // Skip the boundary line
                }
                
                break;
            }
            
            frameData.Write(scanBuffer, 0, read);
            
            if (frameData.Length > MaxFrameSize)
                return false;
        }
        
        if (frameData.Length < 4)
            return false;
        
        byte[] data = frameData.ToArray();
        
        // Validate JPEG markers
        if (data[0] != JpegMarkerPrefix || data[1] != JpegStartOfImage)
            return false;
        
        frame = new MjpegFrame
        {
            Data = data,
            PresentationTimestamp = pcrMicroseconds,
            FrameIndex = frameIndex
        };
        
        frameIndex++;
        pcrMicroseconds += frameLengthMicroseconds;
        
        return true;
    }
    
    private string? ExtractBoundary(ReadOnlySpan<byte> data)
    {
        // Skip "--"
        if (data.Length < 3 || data[0] != '-' || data[1] != '-')
            return null;
        
        int start = 2;
        int end = start;
        
        // Find end of boundary (newline or end of data)
        while (end < data.Length && data[end] != '\r' && data[end] != '\n')
        {
            end++;
        }
        
        if (end == start)
            return null;
        
        return Encoding.ASCII.GetString(data.Slice(start, end - start));
    }
    
    private int FindMarker(byte markerType)
    {
        int position = 0;
        
        while (true)
        {
            int read = stream.Read(scanBuffer, 0, scanBuffer.Length);
            if (read == 0)
                return -1;
            
            for (int i = 0; i < read - 1; i++)
            {
                if (scanBuffer[i] == JpegMarkerPrefix && scanBuffer[i + 1] == markerType)
                {
                    // Found marker - seek back
                    if (stream.CanSeek)
                    {
                        stream.Position -= (read - i);
                    }
                    return position + i;
                }
            }
            
            // Check boundary case
            if (scanBuffer[read - 1] == JpegMarkerPrefix)
            {
                position += read - 1;
                if (stream.CanSeek)
                    stream.Position -= 1;
            }
            else
            {
                position += read;
            }
            
            // Limit search
            if (position > 1024 * 1024)
                return -1;
        }
    }
    
    private static int FindSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }
    
    private string? ReadLine()
    {
        var lineBuilder = new StringBuilder();
        
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0)
                return lineBuilder.Length > 0 ? lineBuilder.ToString() : null;
            
            if (b == '\n')
                return lineBuilder.ToString();
            
            if (b != '\r')
                lineBuilder.Append((char)b);
            
            if (lineBuilder.Length > 1024)
                return lineBuilder.ToString();
        }
    }
    
    private void SkipLine()
    {
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0 || b == '\n')
                break;
        }
    }
    
    private void SkipBytes(int count)
    {
        if (stream.CanSeek)
        {
            stream.Position += count;
        }
        else
        {
            int remaining = count;
            while (remaining > 0)
            {
                int toRead = Math.Min(remaining, scanBuffer.Length);
                int read = stream.Read(scanBuffer, 0, toRead);
                if (read == 0) break;
                remaining -= read;
            }
        }
    }
    
    /// <summary>
    /// Check if data starts with JPEG SOI or MIME boundary.
    /// </summary>
    public static bool CanParse(ReadOnlySpan<byte> header)
    {
        if (header.Length < 2)
            return false;
        
        // JPEG SOI
        if (header[0] == JpegMarkerPrefix && header[1] == JpegStartOfImage)
            return true;
        
        // MIME boundary
        if (header[0] == '-' && header[1] == '-')
            return true;
        
        return false;
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

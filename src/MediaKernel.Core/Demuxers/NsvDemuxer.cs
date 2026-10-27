// MediaKernel - NSV Demuxer
// Nullsoft Streaming Video container format used by Winamp/SHOUTcast
// Supports VP3/VP5/VP6/VP8/H.264 video and MP3/AAC/PCM/Speex audio

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Video codec information from NSV file.
/// </summary>
public sealed class NsvVideoInfo
{
    /// <summary>FourCC codec identifier.</summary>
    public string FourCC { get; init; } = string.Empty;
    
    /// <summary>Video width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Video height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Frame rate (frames per second).</summary>
    public double FrameRate { get; init; }
}

/// <summary>
/// Audio codec information from NSV file.
/// </summary>
public sealed class NsvAudioInfo
{
    /// <summary>FourCC codec identifier.</summary>
    public string FourCC { get; init; } = string.Empty;
    
    /// <summary>Sample rate in Hz (for PCM).</summary>
    public int SampleRate { get; init; }
    
    /// <summary>Number of audio channels (for PCM).</summary>
    public int Channels { get; init; }
}

/// <summary>
/// NSV file information.
/// </summary>
public sealed class NsvFile
{
    /// <summary>Whether the file was successfully parsed.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Video stream information (null if no video).</summary>
    public NsvVideoInfo? Video { get; init; }
    
    /// <summary>Audio stream information (null if no audio).</summary>
    public NsvAudioInfo? Audio { get; init; }
    
    /// <summary>Total file size in bytes.</summary>
    public long FileSize { get; init; }
    
    /// <summary>Estimated duration in seconds.</summary>
    public double DurationSeconds { get; init; }
}

/// <summary>
/// NSV frame data.
/// </summary>
public readonly struct NsvFrame
{
    /// <summary>Presentation timestamp in microseconds.</summary>
    public long PresentationTimestamp { get; init; }
    
    /// <summary>Video data (null if no video in this frame).</summary>
    public ReadOnlyMemory<byte> VideoData { get; init; }
    
    /// <summary>Audio data (null if no audio in this frame).</summary>
    public ReadOnlyMemory<byte> AudioData { get; init; }
    
    /// <summary>Subtitle text (if present).</summary>
    public string? SubtitleText { get; init; }
    
    /// <summary>Whether this is a keyframe.</summary>
    public bool IsKeyFrame { get; init; }
}

/// <summary>
/// Demuxer for NSV (Nullsoft Streaming Video) container format.
/// </summary>
public sealed class NsvDemuxer : IDisposable
{
    private const int NsvfHeaderSize = 8;
    private const int NsvsHeaderSize = 19;
    
    private static readonly byte[] NsvfMagic = "NSVf"u8.ToArray();
    private static readonly byte[] NsvsMagic = "NSVs"u8.ToArray();
    
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly byte[] headerBuffer = new byte[32];
    
    private bool isValid;
    private long fileSize;
    private NsvVideoInfo? videoInfo;
    private NsvAudioInfo? audioInfo;
    private long pcrMicroseconds;
    private long pcrIncrementMicroseconds;
    private bool disposed;
    
    /// <summary>
    /// Creates a new NSV demuxer.
    /// </summary>
    public NsvDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
        
        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(stream));
        
        fileSize = stream.CanSeek ? stream.Length : 0;
    }
    
    /// <summary>
    /// Parse the NSV file and read stream information.
    /// </summary>
    public NsvFile Parse()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (stream.CanSeek)
            stream.Position = 0;
        
        // Read initial header to detect NSVf or NSVs
        int read = stream.Read(headerBuffer, 0, 8);
        if (read < 8)
        {
            return new NsvFile { IsValid = false };
        }
        
        // Check for NSVf (file header) or NSVs (sync header)
        if (!headerBuffer.AsSpan(0, 4).SequenceEqual(NsvfMagic) && 
            !headerBuffer.AsSpan(0, 4).SequenceEqual(NsvsMagic))
        {
            // Try to resynchronize
            if (!TryResynchronize())
            {
                return new NsvFile { IsValid = false };
            }
            
            // Re-read header after resync
            read = stream.Read(headerBuffer, 0, 8);
            if (read < 4)
            {
                return new NsvFile { IsValid = false };
            }
        }
        
        // Skip NSVf header if present
        if (headerBuffer.AsSpan(0, 4).SequenceEqual(NsvfMagic))
        {
            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer.AsSpan(4));
            if (headerSize == 0 || headerSize > int.MaxValue)
            {
                return new NsvFile { IsValid = false };
            }
            
            // Skip remaining header bytes
            long toSkip = headerSize - 8;
            if (toSkip > 0)
            {
                if (stream.CanSeek)
                {
                    stream.Position += toSkip;
                }
                else
                {
                    byte[] skipBuffer = new byte[Math.Min((int)toSkip, 4096)];
                    while (toSkip > 0)
                    {
                        int toRead = (int)Math.Min(toSkip, skipBuffer.Length);
                        int skipped = stream.Read(skipBuffer, 0, toRead);
                        if (skipped == 0) break;
                        toSkip -= skipped;
                    }
                }
            }
            
            // Now read the NSVs header
            read = stream.Read(headerBuffer, 0, 4);
            if (read < 4 || !headerBuffer.AsSpan(0, 4).SequenceEqual(NsvsMagic))
            {
                return new NsvFile { IsValid = false };
            }
            
            // Seek back so TryReadNsvsHeader can read the full header
            if (stream.CanSeek)
                stream.Position -= 4;
        }
        else
        {
            // Seek back to read full NSVs header
            if (stream.CanSeek)
                stream.Position -= 8;
        }
        
        // Read full NSVs header
        if (!TryReadNsvsHeader(out var video, out var audio, out double frameRate))
        {
            return new NsvFile { IsValid = false };
        }
        
        videoInfo = video;
        audioInfo = audio;
        pcrIncrementMicroseconds = frameRate > 0 ? (long)(1_000_000.0 / frameRate) : 40_000;
        isValid = true;
        
        return new NsvFile
        {
            IsValid = true,
            Video = video,
            Audio = audio,
            FileSize = fileSize,
            DurationSeconds = 0 // Cannot determine without scanning
        };
    }
    
    /// <summary>
    /// Read the next frame from the stream.
    /// </summary>
    public bool TryReadFrame(out NsvFrame frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        frame = default;
        
        while (true)
        {
            int read = stream.Read(headerBuffer, 0, 8);
            if (read < 2)
                return false;
            
            // Check for NSVf or NSVs headers
            if (read >= 4 && headerBuffer.AsSpan(0, 4).SequenceEqual(NsvfMagic))
            {
                // Skip NSVf header
                uint headerSize = read >= 8 ? 
                    BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer.AsSpan(4)) : 0;
                
                if (headerSize > 8)
                {
                    SkipBytes((int)(headerSize - 8));
                }
                continue;
            }
            
            if (read >= 4 && headerBuffer.AsSpan(0, 4).SequenceEqual(NsvsMagic))
            {
                // New sync header - re-read stream info
                if (stream.CanSeek)
                    stream.Position -= read;
                
                if (!TryReadNsvsHeader(out videoInfo, out audioInfo, out double newFrameRate))
                    return false;
                
                if (newFrameRate > 0)
                    pcrIncrementMicroseconds = (long)(1_000_000.0 / newFrameRate);
                    
                continue;
            }
            
            // Check for beef marker (next frame)
            if (read >= 2 && headerBuffer[0] == 0xEF && headerBuffer[1] == 0xBE)
            {
                // Read frame header (5 bytes: video size [20 bits] + extra [4 bits] + audio size [16 bits])
                if (stream.CanSeek)
                    stream.Position -= (read - 2);
                    
                read = stream.Read(headerBuffer, 0, 5);
                if (read < 5)
                    return false;
            }
            else
            {
                // Invalid sync - try to resynchronize
                if (!TryResynchronize())
                    return false;
                continue;
            }
            
            // Parse frame header
            int videoSize = (headerBuffer[0] >> 4) | (headerBuffer[1] << 4) | (headerBuffer[2] << 12);
            int extraFlags = headerBuffer[0] & 0x0F;
            int audioSize = headerBuffer[3] | (headerBuffer[4] << 8);
            
            // Read extra data if present
            string? subtitleText = null;
            if (extraFlags != 0 && videoSize > 0)
            {
                byte[] extraHeader = new byte[6];
                if (stream.Read(extraHeader, 0, 6) < 6)
                    return false;
                
                int auxSize = BinaryPrimitives.ReadUInt16LittleEndian(extraHeader.AsSpan(0, 2));
                string auxFourCC = Encoding.ASCII.GetString(extraHeader, 2, 4);
                
                if (auxFourCC == "SUBT" && auxSize > 2)
                {
                    // Skip 2-byte length prefix
                    stream.Read(new byte[2], 0, 2);
                    
                    byte[] subtitleData = new byte[auxSize - 2];
                    int subRead = stream.Read(subtitleData, 0, subtitleData.Length);
                    if (subRead > 0)
                    {
                        // Find null terminator (language name prefix)
                        int textStart = Array.IndexOf(subtitleData, (byte)0);
                        if (textStart >= 0 && textStart < subRead - 1)
                        {
                            subtitleText = Encoding.UTF8.GetString(subtitleData, textStart + 1, subRead - textStart - 1);
                        }
                    }
                }
                else
                {
                    SkipBytes(auxSize);
                }
                
                videoSize -= 6 + auxSize;
            }
            
            // Read video data
            byte[]? videoData = null;
            if (videoSize > 0)
            {
                videoData = new byte[videoSize];
                int videoRead = stream.Read(videoData, 0, videoSize);
                if (videoRead < videoSize)
                    Array.Resize(ref videoData, videoRead);
            }
            
            // Handle raw PCM audio header
            if (audioInfo?.FourCC == "araw" && audioSize > 0)
            {
                byte[] pcmHeader = new byte[4];
                if (stream.Read(pcmHeader, 0, 4) < 4)
                    return false;
                
                audioInfo = new NsvAudioInfo
                {
                    FourCC = "araw",
                    Channels = pcmHeader[1],
                    SampleRate = BinaryPrimitives.ReadUInt16LittleEndian(pcmHeader.AsSpan(2))
                };
                
                audioSize -= 4;
            }
            
            // Read audio data
            byte[]? audioData = null;
            if (audioSize > 0)
            {
                audioData = new byte[audioSize];
                int audioRead = stream.Read(audioData, 0, audioSize);
                if (audioRead < audioSize)
                    Array.Resize(ref audioData, audioRead);
            }
            
            frame = new NsvFrame
            {
                PresentationTimestamp = pcrMicroseconds,
                VideoData = videoData ?? ReadOnlyMemory<byte>.Empty,
                AudioData = audioData ?? ReadOnlyMemory<byte>.Empty,
                SubtitleText = subtitleText,
                IsKeyFrame = false // Cannot easily determine from header
            };
            
            pcrMicroseconds += pcrIncrementMicroseconds;
            return true;
        }
    }
    
    private bool TryReadNsvsHeader(out NsvVideoInfo? video, out NsvAudioInfo? audio, out double frameRate)
    {
        video = null;
        audio = null;
        frameRate = 0;
        
        int read = stream.Read(headerBuffer, 0, NsvsHeaderSize);
        if (read < NsvsHeaderSize)
            return false;
        
        // Validate NSVs magic
        if (!headerBuffer.AsSpan(0, 4).SequenceEqual(NsvsMagic))
            return false;
        
        // Video FourCC
        string videoFourCC = Encoding.ASCII.GetString(headerBuffer, 4, 4);
        if (videoFourCC != "NONE")
        {
            video = new NsvVideoInfo
            {
                FourCC = MapVideoFourCC(videoFourCC),
                Width = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(12)),
                Height = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(14)),
                FrameRate = 0 // Set below
            };
        }
        
        // Audio FourCC
        string audioFourCC = Encoding.ASCII.GetString(headerBuffer, 8, 4);
        if (audioFourCC != "NONE")
        {
            audio = new NsvAudioInfo
            {
                FourCC = MapAudioFourCC(audioFourCC)
            };
        }
        
        // Frame rate (byte 16)
        byte fpsCode = headerBuffer[16];
        frameRate = DecodeFrameRate(fpsCode);
        
        if (video != null)
        {
            video = new NsvVideoInfo
            {
                FourCC = video.FourCC,
                Width = video.Width,
                Height = video.Height,
                FrameRate = frameRate
            };
        }
        
        return video != null || audio != null;
    }
    
    private static double DecodeFrameRate(byte code)
    {
        if ((code & 0x80) != 0)
        {
            // Fractional frame rate
            double baseRate = (code & 0x03) switch
            {
                0 => 30.0,
                1 => 30.0 / 1.001, // 29.97
                2 => 25.0,
                3 => 24.0 / 1.001, // 23.976
                _ => 25.0
            };
            
            if (code < 0xC0)
            {
                // Multiply
                int multiplier = ((code ^ 0x80) >> 2) + 1;
                return baseRate * multiplier;
            }
            else
            {
                // Divide
                int divisor = ((code ^ 0xC0) >> 2) + 1;
                return baseRate / divisor;
            }
        }
        else if (code != 0)
        {
            // Integer frame rate
            return code;
        }
        
        return 25.0; // Default
    }
    
    private static string MapVideoFourCC(string fourCC)
    {
        return fourCC.ToUpperInvariant() switch
        {
            "VP3 " or "VP30" => "VP30",
            "VP31" => "VP31",
            "VP5 " or "VP50" => "VP50",
            "VP60" or "VP61" or "VP62" => "VP6",
            "VP80" => "VP8",
            "H264" => "H264",
            _ => fourCC
        };
    }
    
    private static string MapAudioFourCC(string fourCC)
    {
        return fourCC.ToUpperInvariant() switch
        {
            "MP3 " => "MP3",
            "PCM " => "araw",
            "AAC " or "AACP" => "AAC",
            "SPX " => "Speex",
            _ => fourCC
        };
    }
    
    private void SkipBytes(int count)
    {
        if (count <= 0) return;
        
        if (stream.CanSeek)
        {
            stream.Position += count;
        }
        else
        {
            byte[] skipBuffer = new byte[Math.Min(count, 4096)];
            int remaining = count;
            while (remaining > 0)
            {
                int toRead = Math.Min(remaining, skipBuffer.Length);
                int read = stream.Read(skipBuffer, 0, toRead);
                if (read == 0) break;
                remaining -= read;
            }
        }
    }
    
    /// <summary>
    /// Seek to a position in the file (approximate).
    /// </summary>
    public bool Seek(double positionRatio)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (!stream.CanSeek || fileSize == 0)
            return false;
        
        long position = (long)(positionRatio * fileSize);
        position = Math.Clamp(position, 0, fileSize);
        
        stream.Position = position;
        pcrMicroseconds = -1; // Invalidate time
        
        return TryResynchronize();
    }
    
    /// <summary>
    /// Try to resynchronize after losing sync.
    /// </summary>
    public bool TryResynchronize()
    {
        byte[] scanBuffer = new byte[1024];
        
        while (true)
        {
            int read = stream.Read(scanBuffer);
            if (read < 4)
                return false;
            
            for (int i = 0; i < read - 3; i++)
            {
                if ((scanBuffer[i] == 'N' && scanBuffer[i + 1] == 'S' && 
                     scanBuffer[i + 2] == 'V' && (scanBuffer[i + 3] == 'f' || scanBuffer[i + 3] == 's')))
                {
                    // Found sync
                    if (stream.CanSeek)
                    {
                        stream.Position -= (read - i);
                        return true;
                    }
                    return false;
                }
            }
            
            // Keep last 3 bytes
            if (stream.CanSeek)
                stream.Position -= 3;
        }
    }
    
    /// <summary>
    /// Check if data starts with NSV magic bytes.
    /// </summary>
    public static bool CanParse(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
            return false;
        
        return (header[0] == 'N' && header[1] == 'S' && header[2] == 'V' && 
                (header[3] == 'f' || header[3] == 's'));
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

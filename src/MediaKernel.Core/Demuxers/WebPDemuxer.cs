// MediaKernel - WebP Image Demuxer
// Demuxes WebP (Google's modern image format) still and animated images.
// Reference: WebP Container Specification
//
// Supports:
// - Simple lossy (VP8) and lossless (VP8L) formats
// - Extended format (VP8X) with alpha, animation, ICC profile, EXIF, XMP
// - Animated WebP (ANIM/ANMF chunks)
// - RIFF container parsing

using System.Buffers.Binary;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// WebP format type.
/// </summary>
public enum WebPFormatType
{
    /// <summary>Unknown or invalid format.</summary>
    Unknown,
    
    /// <summary>Simple lossy VP8 format.</summary>
    LossyVp8,
    
    /// <summary>Simple lossless VP8L format.</summary>
    LosslessVp8L,
    
    /// <summary>Extended format with VP8X header.</summary>
    Extended
}

/// <summary>
/// WebP VP8X feature flags.
/// </summary>
[Flags]
public enum WebPFeatureFlags : byte
{
    /// <summary>No features.</summary>
    None = 0,
    
    /// <summary>Animation frames present.</summary>
    Animation = 0x02,
    
    /// <summary>XMP metadata present.</summary>
    Xmp = 0x04,
    
    /// <summary>EXIF metadata present.</summary>
    Exif = 0x08,
    
    /// <summary>Alpha channel present.</summary>
    Alpha = 0x10,
    
    /// <summary>ICC color profile present.</summary>
    IccProfile = 0x20
}

/// <summary>
/// WebP probe result.
/// </summary>
public readonly struct WebPProbeResult
{
    public bool IsWebP { get; init; }
    public int Confidence { get; init; }
    public WebPFormatType FormatType { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool HasAlpha { get; init; }
    public bool HasAnimation { get; init; }
    public WebPFeatureFlags Features { get; init; }
}

/// <summary>
/// WebP animation frame.
/// </summary>
public sealed class WebPAnimationFrame
{
    public int FrameIndex { get; init; }
    public int XOffset { get; init; }
    public int YOffset { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Duration { get; init; }
    public bool BlendWithPrevious { get; init; }
    public bool DisposeToBackground { get; init; }
    public byte[] RawData { get; init; } = [];
}

/// <summary>
/// WebP image frame.
/// </summary>
public sealed class WebPFrame
{
    public int Width { get; init; }
    public int Height { get; init; }
    public WebPFormatType FormatType { get; init; }
    public bool HasAlpha { get; init; }
    public bool IsAnimated { get; init; }
    public int FrameCount { get; init; }
    public int LoopCount { get; init; }
    public uint BackgroundColor { get; init; }
    public byte[] RawData { get; init; } = [];
    public int FrameIndex { get; init; }
    public int Duration { get; init; }
}

/// <summary>
/// Demuxes WebP (Google's modern image format) still and animated images.
/// </summary>
public sealed class WebPDemuxer : IDisposable
{
    private static readonly byte[] RiffSignature = [(byte)'R', (byte)'I', (byte)'F', (byte)'F'];
    private static readonly byte[] WebPSignature = [(byte)'W', (byte)'E', (byte)'B', (byte)'P'];
    
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly long startPosition;
    private bool disposed;
    
    private byte[]? fileData;
    private readonly List<(long offset, int size)> animationFrameOffsets = [];
    private int currentFrame;
    private bool frameRead;
    
    public WebPDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
        startPosition = stream.CanSeek ? stream.Position : 0;
    }
    
    public bool IsParsed { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public WebPFormatType FormatType { get; private set; }
    public WebPFeatureFlags Features { get; private set; }
    public bool HasAlpha => (Features & WebPFeatureFlags.Alpha) != 0;
    public bool HasAnimation => (Features & WebPFeatureFlags.Animation) != 0;
    public bool HasIccProfile => (Features & WebPFeatureFlags.IccProfile) != 0;
    public bool HasExif => (Features & WebPFeatureFlags.Exif) != 0;
    public bool HasXmp => (Features & WebPFeatureFlags.Xmp) != 0;
    public int FrameCount => HasAnimation ? Math.Max(1, animationFrameOffsets.Count) : 1;
    public int LoopCount { get; private set; }
    public uint BackgroundColor { get; private set; }
    public int CurrentFrame => currentFrame;
    public bool IsEndOfStream => frameRead && currentFrame >= FrameCount - 1;
    
    /// <summary>
    /// Probes a stream to determine if it contains a valid WebP file.
    /// </summary>
    public static WebPProbeResult Probe(Stream stream)
    {
        Span<byte> header = stackalloc byte[30];
        int read = stream.Read(header);
        
        if (read < 12)
            return new WebPProbeResult { IsWebP = false };
        
        return Probe(header[..read]);
    }
    
    /// <summary>
    /// Probes data to determine if it contains a valid WebP file.
    /// </summary>
    public static WebPProbeResult Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
            return new WebPProbeResult { IsWebP = false };
        
        // Check RIFF header
        if (!data[..4].SequenceEqual(RiffSignature))
            return new WebPProbeResult { IsWebP = false };
        
        // Check WEBP signature at offset 8
        if (!data[8..12].SequenceEqual(WebPSignature))
            return new WebPProbeResult { IsWebP = false };
        
        // Determine format type from chunk at offset 12
        WebPFormatType formatType = WebPFormatType.Unknown;
        int width = 0;
        int height = 0;
        bool hasAlpha = false;
        bool hasAnimation = false;
        WebPFeatureFlags features = WebPFeatureFlags.None;
        int confidence = 95;
        
        if (data.Length >= 21)
        {
            var chunkType = data[12..16];
            
            if (chunkType.SequenceEqual("VP8 "u8))
            {
                formatType = WebPFormatType.LossyVp8;
                // Parse VP8 bitstream header for dimensions
                if (data.Length >= 30)
                {
                    (width, height) = ParseVp8Header(data[20..]);
                }
            }
            else if (chunkType.SequenceEqual("VP8L"u8))
            {
                formatType = WebPFormatType.LosslessVp8L;
                // VP8L chunk data starts at offset 20 (after chunk size)
                // First byte is signature 0x2F, then 4 bytes for dimensions
                if (data.Length >= 25)
                {
                    (width, height, hasAlpha) = ParseVp8LHeader(data[20..]);
                    if (hasAlpha) features |= WebPFeatureFlags.Alpha;
                }
            }
            else if (chunkType.SequenceEqual("VP8X"u8))
            {
                formatType = WebPFormatType.Extended;
                // VP8X has features and dimensions
                if (data.Length >= 30)
                {
                    (width, height, features) = ParseVp8XHeader(data[20..]);
                    hasAlpha = (features & WebPFeatureFlags.Alpha) != 0;
                    hasAnimation = (features & WebPFeatureFlags.Animation) != 0;
                }
            }
        }
        
        if (formatType == WebPFormatType.Unknown)
            confidence = 80; // Valid container but unknown chunk type
        
        return new WebPProbeResult
        {
            IsWebP = true,
            Confidence = confidence,
            FormatType = formatType,
            Width = width,
            Height = height,
            HasAlpha = hasAlpha,
            HasAnimation = hasAnimation,
            Features = features
        };
    }
    
    private static (int width, int height) ParseVp8Header(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
            return (0, 0);
        
        // VP8 frame header starts with 3 bytes of frame tag
        // Then keyframe sync code 0x9D012A
        // Then 2 bytes width (little-endian, 14 bits) + 2 bits scale
        // Then 2 bytes height (little-endian, 14 bits) + 2 bits scale
        
        // Check for keyframe sync code
        if (data.Length >= 6 && data[3] == 0x9D && data[4] == 0x01 && data[5] == 0x2A)
        {
            int width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3FFF;
            int height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3FFF;
            return (width, height);
        }
        
        return (0, 0);
    }
    
    private static (int width, int height, bool hasAlpha) ParseVp8LHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
            return (0, 0, false);
        
        // VP8L signature byte (should be 0x2F)
        if (data[0] != 0x2F)
            return (0, 0, false);
        
        // Next 4 bytes contain width, height, and alpha flag
        // bits 0-13: width - 1
        // bits 14-27: height - 1
        // bit 28: alpha used
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
        int width = (int)(bits & 0x3FFF) + 1;
        int height = (int)((bits >> 14) & 0x3FFF) + 1;
        bool hasAlpha = (bits & (1u << 28)) != 0;
        
        return (width, height, hasAlpha);
    }
    
    private static (int width, int height, WebPFeatureFlags features) ParseVp8XHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
            return (0, 0, WebPFeatureFlags.None);
        
        // VP8X chunk data:
        // byte 0: flags
        // bytes 1-3: reserved (must be 0)
        // bytes 4-6: canvas width - 1 (24 bits, little-endian)
        // bytes 7-9: canvas height - 1 (24 bits, little-endian)
        
        byte flags = data[0];
        WebPFeatureFlags features = WebPFeatureFlags.None;
        
        if ((flags & 0x02) != 0) features |= WebPFeatureFlags.Animation;
        if ((flags & 0x04) != 0) features |= WebPFeatureFlags.Xmp;
        if ((flags & 0x08) != 0) features |= WebPFeatureFlags.Exif;
        if ((flags & 0x10) != 0) features |= WebPFeatureFlags.Alpha;
        if ((flags & 0x20) != 0) features |= WebPFeatureFlags.IccProfile;
        
        int width = (data[4] | (data[5] << 8) | (data[6] << 16)) + 1;
        int height = (data[7] | (data[8] << 8) | (data[9] << 16)) + 1;
        
        return (width, height, features);
    }
    
    /// <summary>
    /// Parses the WebP structure.
    /// </summary>
    public bool Parse()
    {
        if (IsParsed) return true;
        
        stream.Position = startPosition;
        
        // Read entire file for now (WebP files are typically small)
        fileData = new byte[stream.Length - startPosition];
        int read = stream.Read(fileData);
        
        if (read < 12)
            return false;
        
        var probeResult = Probe(fileData);
        if (!probeResult.IsWebP)
            return false;
        
        FormatType = probeResult.FormatType;
        Width = probeResult.Width;
        Height = probeResult.Height;
        Features = probeResult.Features;
        
        // Parse extended format chunks
        if (FormatType == WebPFormatType.Extended)
        {
            ParseExtendedChunks();
        }
        
        IsParsed = true;
        return true;
    }
    
    private void ParseExtendedChunks()
    {
        if (fileData == null || fileData.Length < 12)
            return;
        
        int offset = 12; // After RIFF size WEBP
        
        while (offset + 8 <= fileData.Length)
        {
            var chunkType = Encoding.ASCII.GetString(fileData, offset, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(fileData.AsSpan(offset + 4));
            
            // Chunk size is padded to even length
            int paddedSize = (int)chunkSize + ((chunkSize & 1) == 1 ? 1 : 0);
            
            if (chunkType == "ANIM")
            {
                // Animation parameters
                if (offset + 8 + 6 <= fileData.Length)
                {
                    BackgroundColor = BinaryPrimitives.ReadUInt32LittleEndian(fileData.AsSpan(offset + 8));
                    LoopCount = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan(offset + 12));
                }
            }
            else if (chunkType == "ANMF")
            {
                // Animation frame
                animationFrameOffsets.Add((offset, (int)chunkSize + 8));
            }
            
            offset += 8 + paddedSize;
        }
    }
    
    /// <summary>
    /// Reads the current frame.
    /// </summary>
    public WebPFrame? ReadFrame()
    {
        if (!IsParsed || fileData == null) return null;
        if (frameRead && currentFrame >= FrameCount - 1) return null;
        
        if (!frameRead)
        {
            frameRead = true;
        }
        else
        {
            currentFrame++;
            if (currentFrame >= FrameCount) return null;
        }
        
        int duration = 0;
        byte[] rawData;
        
        if (HasAnimation && animationFrameOffsets.Count > 0)
        {
            // Get animation frame
            var (frameOffset, frameSize) = animationFrameOffsets[currentFrame];
            rawData = new byte[frameSize];
            Array.Copy(fileData, frameOffset, rawData, 0, Math.Min(frameSize, fileData.Length - (int)frameOffset));
            
            // Parse ANMF header for duration
            if (frameSize >= 24)
            {
                duration = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan((int)frameOffset + 20)) |
                          ((fileData[(int)frameOffset + 22] & 0xFF) << 16);
            }
        }
        else
        {
            // Return entire file as raw data
            rawData = fileData;
        }
        
        return new WebPFrame
        {
            Width = Width,
            Height = Height,
            FormatType = FormatType,
            HasAlpha = HasAlpha,
            IsAnimated = HasAnimation,
            FrameCount = FrameCount,
            LoopCount = LoopCount,
            BackgroundColor = BackgroundColor,
            RawData = rawData,
            FrameIndex = currentFrame,
            Duration = duration
        };
    }
    
    /// <summary>
    /// Seeks to a specific frame.
    /// </summary>
    public bool SeekToFrame(int frameIndex)
    {
        if (frameIndex < 0 || frameIndex >= FrameCount)
            return false;
        
        currentFrame = frameIndex;
        frameRead = false;
        return true;
    }
    
    /// <summary>
    /// Resets to the first frame.
    /// </summary>
    public void Reset()
    {
        currentFrame = 0;
        frameRead = false;
    }
    
    /// <summary>
    /// Gets track information for the video track.
    /// </summary>
    public DemuxerTrackInfo? GetTrackInfo()
    {
        if (!IsParsed) return null;
        
        return new DemuxerTrackInfo
        {
            TrackType = DemuxerTrackType.Video,
            TrackId = 0,
            CodecId = FormatType switch
            {
                WebPFormatType.LossyVp8 => "vp8",
                WebPFormatType.LosslessVp8L => "vp8l",
                _ => "webp"
            },
            Width = Width,
            Height = Height,
            ChannelCount = HasAlpha ? 4 : 3
        };
    }
    
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        
        fileData = null;
        
        if (!leaveOpen)
            stream.Dispose();
    }
}

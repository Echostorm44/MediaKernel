// MediaKernel - IVF (Indeo Video Format) Demuxer
// Demuxes IVF files containing VP8, VP9, or AV1 video.
// Reference: https://wiki.multimedia.cx/index.php/IVF

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Configuration for IVF demuxer.
/// </summary>
public sealed class IvfDemuxerConfiguration
{
    /// <summary>
    /// Read buffer size for frame data.
    /// </summary>
    public int ReadBufferSize { get; init; } = 64 * 1024;
}

/// <summary>
/// Result from probing a stream for IVF content.
/// </summary>
public readonly struct IvfProbeResult
{
    /// <summary>Whether valid IVF content was detected.</summary>
    public bool IsIvf { get; init; }
    
    /// <summary>The fourcc codec identifier.</summary>
    public string FourCC { get; init; }
    
    /// <summary>Video width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Video height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Frame rate (timebase denominator / numerator).</summary>
    public double FrameRate { get; init; }
    
    /// <summary>Number of frames in the file.</summary>
    public int FrameCount { get; init; }
}

/// <summary>
/// IVF file header (32 bytes).
/// </summary>
/// <remarks>
/// Format:
///   bytes 0-3:   signature "DKIF"
///   bytes 4-5:   version (0)
///   bytes 6-7:   header length (32)
///   bytes 8-11:  fourcc (e.g., "VP80", "VP90", "AV01")
///   bytes 12-13: width
///   bytes 14-15: height
///   bytes 16-19: time base denominator (frame rate numerator)
///   bytes 20-23: time base numerator (frame rate denominator)
///   bytes 24-27: number of frames
///   bytes 28-31: unused
/// </remarks>
public readonly struct IvfFileHeader
{
    /// <summary>Codec fourcc (e.g., "VP80", "VP90", "AV01").</summary>
    public string FourCC { get; init; }
    
    /// <summary>Video width in pixels.</summary>
    public ushort Width { get; init; }
    
    /// <summary>Video height in pixels.</summary>
    public ushort Height { get; init; }
    
    /// <summary>Timebase denominator (frame rate numerator).</summary>
    public uint TimebaseDenominator { get; init; }
    
    /// <summary>Timebase numerator (frame rate denominator).</summary>
    public uint TimebaseNumerator { get; init; }
    
    /// <summary>Number of frames in file.</summary>
    public uint FrameCount { get; init; }
    
    /// <summary>Calculated frame rate.</summary>
    public double FrameRate => TimebaseNumerator > 0 
        ? (double)TimebaseDenominator / TimebaseNumerator 
        : 30.0;
}

/// <summary>
/// Demuxer for IVF (Indeo Video Format) files.
/// </summary>
/// <remarks>
/// IVF is a simple container format used primarily for VP8, VP9, and AV1 video.
/// It consists of a 32-byte file header followed by frame headers (12 bytes each)
/// with the raw video frame data.
/// </remarks>
public sealed class IvfDemuxer : IDisposable
{
    private const int FileHeaderSize = 32;
    private const int FrameHeaderSize = 12;
    private static readonly byte[] Signature = [(byte)'D', (byte)'K', (byte)'I', (byte)'F'];
    
    private readonly Stream stream;
    private readonly IvfDemuxerConfiguration configuration;
    
    private bool isParsed;
    private bool isEndOfStream;
    private IvfFileHeader fileHeader;
    private int currentFrameIndex;
    private string codecId = "unknown";
    private long dataStartPosition;
    
    /// <summary>
    /// Gets the video width in pixels.
    /// </summary>
    public int Width => fileHeader.Width;
    
    /// <summary>
    /// Gets the video height in pixels.
    /// </summary>
    public int Height => fileHeader.Height;
    
    /// <summary>
    /// Gets the frame rate.
    /// </summary>
    public double FrameRate => fileHeader.FrameRate;
    
    /// <summary>
    /// Gets the codec identifier.
    /// </summary>
    public string Codec => codecId;
    
    /// <summary>
    /// Gets the fourcc from the file header.
    /// </summary>
    public string FourCC => fileHeader.FourCC;
    
    /// <summary>
    /// Gets the total number of frames.
    /// </summary>
    public int FrameCount => (int)fileHeader.FrameCount;
    
    /// <summary>
    /// Gets the current frame index.
    /// </summary>
    public int CurrentFrame => currentFrameIndex;
    
    /// <summary>
    /// Gets the estimated duration.
    /// </summary>
    public TimeSpan Duration => fileHeader.FrameCount > 0 && fileHeader.FrameRate > 0
        ? TimeSpan.FromSeconds(fileHeader.FrameCount / fileHeader.FrameRate)
        : TimeSpan.Zero;
    
    /// <summary>
    /// Gets whether the stream has been parsed.
    /// </summary>
    public bool IsParsed => isParsed;
    
    /// <summary>
    /// Gets whether we've reached end of stream.
    /// </summary>
    public bool IsEndOfStream => isEndOfStream;
    
    /// <summary>
    /// Creates a new IVF demuxer.
    /// </summary>
    public IvfDemuxer(Stream stream, IvfDemuxerConfiguration? configuration = null)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.configuration = configuration ?? new IvfDemuxerConfiguration();
    }
    
    /// <summary>
    /// Probes a stream for IVF content.
    /// </summary>
    public static IvfProbeResult Probe(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        
        if (!stream.CanRead)
            return default;
        
        long originalPosition = stream.CanSeek ? stream.Position : 0;
        
        try
        {
            byte[] buffer = new byte[FileHeaderSize];
            int bytesRead = stream.Read(buffer, 0, FileHeaderSize);
            
            if (bytesRead < FileHeaderSize)
                return default;
            
            return Probe(buffer);
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = originalPosition;
        }
    }
    
    /// <summary>
    /// Probes a buffer for IVF content.
    /// </summary>
    public static IvfProbeResult Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileHeaderSize)
            return default;
        
        // Check signature "DKIF"
        if (data[0] != 'D' || data[1] != 'K' || data[2] != 'I' || data[3] != 'F')
            return default;
        
        // Parse header
        string fourcc = System.Text.Encoding.ASCII.GetString(data.Slice(8, 4));
        ushort width = (ushort)(data[12] | (data[13] << 8));
        ushort height = (ushort)(data[14] | (data[15] << 8));
        uint tbDenom = (uint)(data[16] | (data[17] << 8) | (data[18] << 16) | (data[19] << 24));
        uint tbNumer = (uint)(data[20] | (data[21] << 8) | (data[22] << 16) | (data[23] << 24));
        uint frameCount = (uint)(data[24] | (data[25] << 8) | (data[26] << 16) | (data[27] << 24));
        
        double frameRate = tbNumer > 0 ? (double)tbDenom / tbNumer : 30.0;
        
        return new IvfProbeResult
        {
            IsIvf = true,
            FourCC = fourcc,
            Width = width,
            Height = height,
            FrameRate = frameRate,
            FrameCount = (int)frameCount
        };
    }
    
    /// <summary>
    /// Parses the IVF file header.
    /// </summary>
    public bool Parse()
    {
        if (isParsed)
            return true;
        
        byte[] headerBuffer = new byte[FileHeaderSize];
        int bytesRead = stream.Read(headerBuffer, 0, FileHeaderSize);
        
        if (bytesRead < FileHeaderSize)
            return false;
        
        // Check signature
        if (headerBuffer[0] != 'D' || headerBuffer[1] != 'K' || 
            headerBuffer[2] != 'I' || headerBuffer[3] != 'F')
            return false;
        
        // Parse header
        string fourcc = System.Text.Encoding.ASCII.GetString(headerBuffer, 8, 4);
        ushort width = (ushort)(headerBuffer[12] | (headerBuffer[13] << 8));
        ushort height = (ushort)(headerBuffer[14] | (headerBuffer[15] << 8));
        uint tbDenom = (uint)(headerBuffer[16] | (headerBuffer[17] << 8) | 
                              (headerBuffer[18] << 16) | (headerBuffer[19] << 24));
        uint tbNumer = (uint)(headerBuffer[20] | (headerBuffer[21] << 8) | 
                              (headerBuffer[22] << 16) | (headerBuffer[23] << 24));
        uint frameCount = (uint)(headerBuffer[24] | (headerBuffer[25] << 8) | 
                                 (headerBuffer[26] << 16) | (headerBuffer[27] << 24));
        
        fileHeader = new IvfFileHeader
        {
            FourCC = fourcc,
            Width = width,
            Height = height,
            TimebaseDenominator = tbDenom,
            TimebaseNumerator = tbNumer,
            FrameCount = frameCount
        };
        
        // Map fourcc to codec ID
        codecId = fourcc.ToUpperInvariant() switch
        {
            "VP80" => "vp8",
            "VP90" => "vp9",
            "AV01" => "av1",
            _ => fourcc.ToLowerInvariant()
        };
        
        dataStartPosition = stream.CanSeek ? stream.Position : FileHeaderSize;
        currentFrameIndex = 0;
        isParsed = true;
        
        return true;
    }
    
    /// <summary>
    /// Reads the next frame from the IVF file.
    /// </summary>
    public DemuxerPacket? ReadFrame()
    {
        if (!isParsed && !Parse())
            return null;
        
        if (isEndOfStream)
            return null;
        
        // Read frame header (12 bytes)
        // bytes 0-3:  frame size in bytes (little-endian)
        // bytes 4-11: presentation timestamp (little-endian)
        byte[] frameHeader = new byte[FrameHeaderSize];
        int headerBytesRead = stream.Read(frameHeader, 0, FrameHeaderSize);
        
        if (headerBytesRead < FrameHeaderSize)
        {
            isEndOfStream = true;
            return null;
        }
        
        uint frameSize = (uint)(frameHeader[0] | (frameHeader[1] << 8) | 
                                (frameHeader[2] << 16) | (frameHeader[3] << 24));
        ulong pts = (ulong)frameHeader[4] | ((ulong)frameHeader[5] << 8) | 
                    ((ulong)frameHeader[6] << 16) | ((ulong)frameHeader[7] << 24) |
                    ((ulong)frameHeader[8] << 32) | ((ulong)frameHeader[9] << 40) |
                    ((ulong)frameHeader[10] << 48) | ((ulong)frameHeader[11] << 56);
        
        if (frameSize == 0 || frameSize > 100 * 1024 * 1024) // Max 100MB frame
        {
            isEndOfStream = true;
            return null;
        }
        
        // Read frame data
        byte[] frameData = new byte[frameSize];
        int dataBytesRead = stream.Read(frameData, 0, (int)frameSize);
        
        if (dataBytesRead < (int)frameSize)
        {
            isEndOfStream = true;
            return null;
        }
        
        // Determine if keyframe based on codec
        bool isKeyframe = IsKeyframe(frameData, codecId);
        
        // Convert PTS to ticks (PTS is in timebase units)
        long ptsTicks = fileHeader.TimebaseNumerator > 0
            ? (long)(pts * TimeSpan.TicksPerSecond * fileHeader.TimebaseNumerator / fileHeader.TimebaseDenominator)
            : (long)pts * TimeSpan.TicksPerMillisecond;
        
        long durationTicks = (long)(TimeSpan.TicksPerSecond / fileHeader.FrameRate);
        
        var packet = new DemuxerPacket(
            trackId: 0,
            presentationTimestamp: ptsTicks,
            decodeTimestamp: ptsTicks,
            duration: durationTicks,
            isKeyframe: isKeyframe,
            data: frameData);
        
        currentFrameIndex++;
        
        return packet;
    }
    
    private static bool IsKeyframe(ReadOnlySpan<byte> data, string codec)
    {
        if (data.Length == 0)
            return false;
        
        return codec switch
        {
            "vp8" => (data[0] & 0x01) == 0, // VP8: bit 0 of first byte = 0 for keyframe
            "vp9" => (data[0] & 0x02) == 0, // VP9: profile 0/1 keyframe detection (simplified)
            "av1" => IsAv1Keyframe(data),
            _ => true // Assume keyframe for unknown codecs
        };
    }
    
    private static bool IsAv1Keyframe(ReadOnlySpan<byte> data)
    {
        // AV1 OBU parsing - check for sequence header or keyframe
        if (data.Length < 2)
            return true;
        
        int obuType = (data[0] >> 3) & 0x0F;
        
        // OBU type 1 = Sequence Header (usually precedes keyframes)
        // OBU type 6 = Frame (check frame header for keyframe)
        if (obuType == 1) // Sequence header
            return true;
        
        // For frame OBU, would need deeper parsing - assume keyframe for first frame
        return true;
    }
    
    /// <summary>
    /// Gets track information.
    /// </summary>
    public DemuxerTrackInfo GetTrackInfo()
    {
        return new DemuxerTrackInfo
        {
            TrackId = 0,
            TrackType = DemuxerTrackType.Video,
            CodecId = codecId,
            Width = fileHeader.Width,
            Height = fileHeader.Height,
            FrameRate = fileHeader.FrameRate
        };
    }
    
    /// <summary>
    /// Seeks to a specific frame.
    /// </summary>
    public SeekResult SeekToFrame(int frameIndex)
    {
        if (!stream.CanSeek)
            return SeekResult.Failed("Stream is not seekable");
        
        if (!isParsed)
            return SeekResult.Failed("Stream not parsed");
        
        if (frameIndex < 0)
            frameIndex = 0;
        
        // IVF doesn't have a frame index, so we need to scan frames
        // For now, only support seeking to beginning
        if (frameIndex == 0)
        {
            stream.Position = dataStartPosition;
            currentFrameIndex = 0;
            isEndOfStream = false;
            return SeekResult.Succeeded(0);
        }
        
        // Scan forward to find the frame
        stream.Position = dataStartPosition;
        currentFrameIndex = 0;
        isEndOfStream = false;
        
        while (currentFrameIndex < frameIndex)
        {
            byte[] frameHeader = new byte[FrameHeaderSize];
            int bytesRead = stream.Read(frameHeader, 0, FrameHeaderSize);
            
            if (bytesRead < FrameHeaderSize)
            {
                isEndOfStream = true;
                return SeekResult.Failed("Reached end of file before target frame");
            }
            
            uint frameSize = (uint)(frameHeader[0] | (frameHeader[1] << 8) | 
                                    (frameHeader[2] << 16) | (frameHeader[3] << 24));
            
            // Skip frame data
            stream.Position += frameSize;
            currentFrameIndex++;
        }
        
        long ptsTicks = (long)(currentFrameIndex * TimeSpan.TicksPerSecond / fileHeader.FrameRate);
        return SeekResult.Succeeded(ptsTicks, isApproximate: true);
    }
    
    /// <summary>
    /// Seeks to a time position.
    /// </summary>
    public SeekResult Seek(TimeSpan position)
    {
        if (!isParsed)
            return SeekResult.Failed("Stream not parsed");
        
        // Calculate target frame index
        double targetFrame = position.TotalSeconds * fileHeader.FrameRate;
        int frameIndex = (int)Math.Max(0, Math.Floor(targetFrame));
        
        return SeekToFrame(frameIndex);
    }
    
    /// <summary>
    /// Disposes the demuxer.
    /// </summary>
    public void Dispose()
    {
        // Nothing to dispose - we don't own the stream
    }
}

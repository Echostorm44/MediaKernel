// MediaKernel - MPEG-4 Part 2 Video Elementary Stream Demuxer
// Demuxes raw MPEG-4 Visual (Part 2) video elementary streams (.m4v, .mp4v)
// Reference: ISO/IEC 14496-2, VLC's modules/demux/mpeg/es.c

using MediaKernel.Core.Packetizers;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Configuration for MPEG-4 Part 2 Video ES demuxer.
/// </summary>
public sealed class Mpeg4VideoDemuxerConfiguration
{
    /// <summary>
    /// Frame rate to use when not available from VOL header.
    /// Default is 25.0 fps.
    /// </summary>
    public double DefaultFrameRate { get; init; } = 25.0;
    
    /// <summary>
    /// Read buffer size in bytes.
    /// </summary>
    public int ReadBufferSize { get; init; } = 64 * 1024;
    
    /// <summary>
    /// Skip offset in bytes (e.g., for .re4 files with 220-byte header).
    /// </summary>
    public int SkipOffset { get; init; } = 0;
}

/// <summary>
/// Result from probing a stream for MPEG-4 Visual content.
/// </summary>
public readonly struct Mpeg4VideoProbeResult
{
    /// <summary>Whether valid MPEG-4 Visual content was detected.</summary>
    public bool IsMpeg4Video { get; init; }
    
    /// <summary>Whether a Video Object Layer header was found.</summary>
    public bool HasVideoObjectLayer { get; init; }
    
    /// <summary>Video width from VOL header (0 if not available).</summary>
    public int Width { get; init; }
    
    /// <summary>Video height from VOL header (0 if not available).</summary>
    public int Height { get; init; }
    
    /// <summary>Frame rate from VOL (0 if not available).</summary>
    public double FrameRate { get; init; }
}

/// <summary>
/// Demuxer for raw MPEG-4 Part 2 (Visual) elementary streams.
/// </summary>
/// <remarks>
/// Handles .m4v and .mp4v files containing DivX, XviD, and other MPEG-4 ASP content.
/// Uses start codes (00 00 01 xx) for synchronization.
/// </remarks>
public sealed class Mpeg4VideoDemuxer : IDisposable
{
    // MPEG-4 start codes
    private const int VisualObjectSequenceStartCode = 0x1B0;
    private const int VideoObjectLayerStartCodeMin = 0x120;
    private const int VideoObjectLayerStartCodeMax = 0x12F;
    private const int VopStartCode = 0x1B6;
    
    private readonly Stream stream;
    private readonly Mpeg4VideoDemuxerConfiguration configuration;
    private readonly Mpeg4VideoPacketizer packetizer;
    private readonly byte[] readBuffer;
    
    private bool isParsed;
    private bool isEndOfStream;
    private int width;
    private int height;
    private double frameRate;
    private TimeSpan duration;
    private long frameCount;
    private int currentTrackId;
    private bool hasVideoObjectLayer;
    private long streamStartPosition;
    
    /// <summary>
    /// Gets the video width in pixels.
    /// </summary>
    public int Width => width;
    
    /// <summary>
    /// Gets the video height in pixels.
    /// </summary>
    public int Height => height;
    
    /// <summary>
    /// Gets the frame rate.
    /// </summary>
    public double FrameRate => frameRate;
    
    /// <summary>
    /// Gets the estimated duration.
    /// </summary>
    public TimeSpan Duration => duration;
    
    /// <summary>
    /// Gets the codec identifier.
    /// </summary>
    public string Codec => "mpeg4";
    
    /// <summary>
    /// Gets whether the stream has been parsed.
    /// </summary>
    public bool IsParsed => isParsed;
    
    /// <summary>
    /// Gets whether we've reached end of stream.
    /// </summary>
    public bool IsEndOfStream => isEndOfStream;
    
    /// <summary>
    /// Gets the VOL data for decoder initialization.
    /// </summary>
    public ReadOnlySpan<byte> VideoObjectLayerData => packetizer.VideoObjectLayerData;
    
    /// <summary>
    /// Creates a new MPEG-4 Video ES demuxer.
    /// </summary>
    public Mpeg4VideoDemuxer(Stream stream, Mpeg4VideoDemuxerConfiguration? configuration = null)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.configuration = configuration ?? new Mpeg4VideoDemuxerConfiguration();
        packetizer = new Mpeg4VideoPacketizer();
        readBuffer = new byte[this.configuration.ReadBufferSize];
    }
    
    /// <summary>
    /// Probes a stream for MPEG-4 Visual content.
    /// </summary>
    public static Mpeg4VideoProbeResult Probe(Stream stream, int probeSize = 8192)
    {
        ArgumentNullException.ThrowIfNull(stream);
        
        if (!stream.CanRead)
            return default;
        
        long originalPosition = stream.CanSeek ? stream.Position : 0;
        
        try
        {
            byte[] buffer = new byte[Math.Min(probeSize, 64 * 1024)];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            
            if (bytesRead < 4)
                return default;
            
            return Probe(buffer.AsSpan(0, bytesRead));
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = originalPosition;
        }
    }
    
    /// <summary>
    /// Probes a buffer for MPEG-4 Visual content.
    /// </summary>
    public static Mpeg4VideoProbeResult Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return default;
        
        // Look for MPEG-4 start codes
        bool foundStartCode = false;
        bool foundVol = false;
        int width = 0, height = 0;
        double frameRate = 0;
        
        for (int i = 0; i <= data.Length - 4; i++)
        {
            // Check for start code prefix (00 00 01)
            if (data[i] == 0x00 && data[i + 1] == 0x00 && data[i + 2] == 0x01)
            {
                int startCode = data[i + 3];
                
                // Visual Object Sequence start code (0xB0)
                if (startCode == 0xB0)
                {
                    foundStartCode = true;
                }
                // Video Object Layer start codes (0x20-0x2F)
                else if (startCode >= 0x20 && startCode <= 0x2F)
                {
                    foundStartCode = true;
                    foundVol = true;
                    
                    // Try to parse VOL to extract dimensions
                    if (i + 20 < data.Length)
                    {
                        var dimensions = TryParseVolDimensions(data.Slice(i + 4));
                        if (dimensions.HasValue)
                        {
                            width = dimensions.Value.width;
                            height = dimensions.Value.height;
                            frameRate = dimensions.Value.frameRate;
                        }
                    }
                }
                // VOP start code (0xB6)
                else if (startCode == 0xB6)
                {
                    foundStartCode = true;
                }
                // Video Object start codes (0x00-0x1F)
                else if (startCode <= 0x1F)
                {
                    foundStartCode = true;
                }
                
                if (foundVol)
                    break;
            }
        }
        
        return new Mpeg4VideoProbeResult
        {
            IsMpeg4Video = foundStartCode,
            HasVideoObjectLayer = foundVol,
            Width = width,
            Height = height,
            FrameRate = frameRate
        };
    }
    
    private static (int width, int height, double frameRate)? TryParseVolDimensions(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
            return null;
        
        // VOL parsing is complex; use packetizer for full parsing
        // This is a simplified check for probe purposes
        try
        {
            using var tempPacketizer = new Mpeg4VideoPacketizer();
            
            // Create fake VOL start code data
            byte[] volData = new byte[data.Length + 4];
            volData[0] = 0x00;
            volData[1] = 0x00;
            volData[2] = 0x01;
            volData[3] = 0x20; // VOL start code
            data.CopyTo(volData.AsSpan(4));
            
            tempPacketizer.Feed(volData);
            
            if (tempPacketizer.HasVideoObjectLayer)
            {
                return (tempPacketizer.Width, tempPacketizer.Height, tempPacketizer.FrameRate);
            }
        }
        catch
        {
            // Parsing failed, return null
        }
        
        return null;
    }
    
    /// <summary>
    /// Parses the stream and extracts video information.
    /// </summary>
    public bool Parse()
    {
        if (isParsed)
            return true;
        
        // Skip any header offset (e.g., for .re4 files)
        if (configuration.SkipOffset > 0 && stream.CanSeek)
        {
            stream.Position = configuration.SkipOffset;
        }
        
        streamStartPosition = stream.CanSeek ? stream.Position : 0;
        
        // Read initial data to find VOL
        int bytesRead = stream.Read(readBuffer, 0, readBuffer.Length);
        if (bytesRead < 4)
            return false;
        
        // Check for start code
        bool hasStartCode = false;
        for (int i = 0; i <= bytesRead - 4; i++)
        {
            if (readBuffer[i] == 0x00 && readBuffer[i + 1] == 0x00 && readBuffer[i + 2] == 0x01)
            {
                hasStartCode = true;
                break;
            }
        }
        
        if (!hasStartCode)
            return false;
        
        // Feed to packetizer to parse VOL
        packetizer.Feed(readBuffer.AsSpan(0, bytesRead));
        
        // Continue reading until we find VOL
        int maxIterations = 10;
        while (!packetizer.HasVideoObjectLayer && maxIterations-- > 0)
        {
            bytesRead = stream.Read(readBuffer, 0, readBuffer.Length);
            if (bytesRead == 0)
                break;
            
            packetizer.Feed(readBuffer.AsSpan(0, bytesRead));
        }
        
        if (packetizer.HasVideoObjectLayer)
        {
            width = packetizer.Width;
            height = packetizer.Height;
            frameRate = packetizer.FrameRate > 0 ? packetizer.FrameRate : configuration.DefaultFrameRate;
            hasVideoObjectLayer = true;
        }
        else
        {
            // Use defaults
            width = 0;
            height = 0;
            frameRate = configuration.DefaultFrameRate;
        }
        
        // Estimate duration from file size
        if (stream.CanSeek)
        {
            long fileSize = stream.Length - streamStartPosition;
            // Rough estimate: assume ~2 Mbps average bitrate for MPEG-4 ASP
            double estimatedBitrate = 2_000_000; // 2 Mbps
            double durationSeconds = (fileSize * 8.0) / estimatedBitrate;
            duration = TimeSpan.FromSeconds(durationSeconds);
            
            // Reset to start of content
            stream.Position = streamStartPosition;
        }
        
        currentTrackId = 0;
        isParsed = true;
        return true;
    }
    
    /// <summary>
    /// Reads the next frame from the stream.
    /// </summary>
    public DemuxerPacket? ReadFrame()
    {
        if (!isParsed && !Parse())
            return null;
        
        if (isEndOfStream)
            return null;
        
        Mpeg4Vop vop;
        
        // Try to get a VOP from packetizer
        while (!packetizer.TryGetVop(out vop))
        {
            int bytesRead = stream.Read(readBuffer, 0, readBuffer.Length);
            if (bytesRead == 0)
            {
                // End of stream - flush packetizer
                packetizer.Flush();
                if (!packetizer.TryGetVop(out vop))
                {
                    isEndOfStream = true;
                    return null;
                }
                
                // Fall through to return the flushed VOP
                return CreatePacket(vop);
            }
            
            // Calculate timestamp based on frame count
            long pts = (long)(frameCount * TimeSpan.TicksPerSecond / frameRate);
            packetizer.Feed(readBuffer.AsSpan(0, bytesRead), pts);
        }
        
        return CreatePacket(vop);
    }
    
    private DemuxerPacket CreatePacket(Mpeg4Vop vop)
    {
        frameCount++;
        
        // Use packetizer timestamps if available, otherwise interpolate
        long pts = vop.PresentationTimestamp >= 0 
            ? vop.PresentationTimestamp 
            : (long)((frameCount - 1) * TimeSpan.TicksPerSecond / frameRate);
        
        long dts = vop.DecodeTimestamp >= 0 
            ? vop.DecodeTimestamp 
            : pts;
        
        long duration = (long)(TimeSpan.TicksPerSecond / frameRate);
        
        return new DemuxerPacket(
            trackId: currentTrackId,
            presentationTimestamp: pts,
            decodeTimestamp: dts,
            duration: duration,
            isKeyframe: vop.IsKeyframe,
            data: vop.Data);
    }
    
    /// <summary>
    /// Gets track information.
    /// </summary>
    public DemuxerTrackInfo GetTrackInfo()
    {
        return new DemuxerTrackInfo
        {
            TrackId = currentTrackId,
            TrackType = DemuxerTrackType.Video,
            CodecId = Codec,
            Width = width,
            Height = height,
            FrameRate = frameRate
        };
    }
    
    /// <summary>
    /// Seeks to a position in the stream.
    /// </summary>
    public SeekResult Seek(TimeSpan position)
    {
        if (!stream.CanSeek)
            return SeekResult.Failed("Stream is not seekable");
        
        if (!isParsed)
            return SeekResult.Failed("Stream not parsed");
        
        // For ES streams, we can only do approximate seeking
        double targetSeconds = position.TotalSeconds;
        if (targetSeconds < 0)
            targetSeconds = 0;
        
        // Estimate position based on file size and duration
        if (duration.TotalSeconds > 0)
        {
            double ratio = targetSeconds / duration.TotalSeconds;
            ratio = Math.Clamp(ratio, 0, 1);
            
            long targetPosition = streamStartPosition + (long)((stream.Length - streamStartPosition) * ratio);
            stream.Position = Math.Max(streamStartPosition, targetPosition);
        }
        else
        {
            stream.Position = streamStartPosition;
        }
        
        // Reset packetizer state
        packetizer.Flush();
        while (packetizer.TryGetVop(out _)) { } // Clear output queue
        
        // Find next start code
        if (!ScanToNextStartCode())
        {
            stream.Position = streamStartPosition;
            return SeekResult.Failed("Could not find start code after seek");
        }
        
        isEndOfStream = false;
        
        // Update frame count estimate
        frameCount = (long)(position.Ticks * frameRate / TimeSpan.TicksPerSecond);
        
        return SeekResult.Succeeded(position.Ticks, isApproximate: true);
    }
    
    private bool ScanToNextStartCode()
    {
        // Scan for 00 00 01 pattern
        int state = 0;
        int maxBytes = 256 * 1024; // Limit scan to 256KB
        
        while (maxBytes-- > 0)
        {
            int b = stream.ReadByte();
            if (b < 0)
                return false;
            
            if (state == 0)
            {
                if (b == 0) state = 1;
            }
            else if (state == 1)
            {
                if (b == 0) state = 2;
                else state = 0;
            }
            else if (state == 2)
            {
                if (b == 1)
                {
                    // Found start code - back up to include it
                    stream.Position -= 3;
                    return true;
                }
                else if (b != 0)
                {
                    state = 0;
                }
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Disposes the demuxer.
    /// </summary>
    public void Dispose()
    {
        packetizer.Dispose();
    }
}

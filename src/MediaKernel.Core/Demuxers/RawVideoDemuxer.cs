// MediaKernel - Raw Video Demuxer
// Demuxer for raw/headerless video data with configurable format parameters
// Also supports YUV4MPEG2 (Y4M) format with embedded headers

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Raw video pixel format / chroma subsampling.
/// </summary>
public enum RawVideoFormat
{
    /// <summary>YUV 4:2:0 planar (I420/YV12) - 12 bits per pixel.</summary>
    I420,
    
    /// <summary>YUV 4:2:2 planar - 16 bits per pixel.</summary>
    I422,
    
    /// <summary>YUV 4:4:4 planar - 24 bits per pixel.</summary>
    I444,
    
    /// <summary>NV12 (Y plane + interleaved UV) - 12 bits per pixel.</summary>
    Nv12,
    
    /// <summary>NV21 (Y plane + interleaved VU) - 12 bits per pixel.</summary>
    Nv21,
    
    /// <summary>YUY2 packed 4:2:2 (YUYV) - 16 bits per pixel.</summary>
    Yuy2,
    
    /// <summary>UYVY packed 4:2:2 - 16 bits per pixel.</summary>
    Uyvy,
    
    /// <summary>Grayscale 8-bit - 8 bits per pixel.</summary>
    Gray8,
    
    /// <summary>Grayscale 16-bit - 16 bits per pixel.</summary>
    Gray16,
    
    /// <summary>RGB 24-bit packed - 24 bits per pixel.</summary>
    Rgb24,
    
    /// <summary>BGR 24-bit packed - 24 bits per pixel.</summary>
    Bgr24,
    
    /// <summary>RGBA 32-bit packed - 32 bits per pixel.</summary>
    Rgba32,
    
    /// <summary>BGRA 32-bit packed - 32 bits per pixel.</summary>
    Bgra32
}

/// <summary>
/// Configuration for raw video demuxer.
/// </summary>
public sealed class RawVideoConfiguration
{
    /// <summary>Video width in pixels. Required.</summary>
    public int Width { get; init; }
    
    /// <summary>Video height in pixels. Required.</summary>
    public int Height { get; init; }
    
    /// <summary>Frame rate numerator. Default is 30000 (for 29.97fps).</summary>
    public int FrameRateNumerator { get; init; } = 30000;
    
    /// <summary>Frame rate denominator. Default is 1001 (for 29.97fps).</summary>
    public int FrameRateDenominator { get; init; } = 1001;
    
    /// <summary>Pixel format / chroma. Default is I420.</summary>
    public RawVideoFormat Format { get; init; } = RawVideoFormat.I420;
    
    /// <summary>Sample aspect ratio width (for anamorphic video). Default is 1.</summary>
    public int SampleAspectRatioWidth { get; init; } = 1;
    
    /// <summary>Sample aspect ratio height. Default is 1.</summary>
    public int SampleAspectRatioHeight { get; init; } = 1;
}

/// <summary>
/// Information about raw video stream.
/// </summary>
public sealed class RawVideoInfo
{
    /// <summary>Whether the file was successfully parsed.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Video width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Video height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Frame rate numerator.</summary>
    public int FrameRateNumerator { get; init; }
    
    /// <summary>Frame rate denominator.</summary>
    public int FrameRateDenominator { get; init; }
    
    /// <summary>Frames per second as double.</summary>
    public double FramesPerSecond => FrameRateDenominator > 0 
        ? (double)FrameRateNumerator / FrameRateDenominator 
        : 0;
    
    /// <summary>Pixel format / chroma subsampling.</summary>
    public RawVideoFormat Format { get; init; }
    
    /// <summary>Sample aspect ratio width.</summary>
    public int SampleAspectRatioWidth { get; init; }
    
    /// <summary>Sample aspect ratio height.</summary>
    public int SampleAspectRatioHeight { get; init; }
    
    /// <summary>Size of one frame in bytes.</summary>
    public int FrameSize { get; init; }
    
    /// <summary>Total number of frames (estimated from file size).</summary>
    public long TotalFrames { get; init; }
    
    /// <summary>Total duration in 100-nanosecond units.</summary>
    public long Duration { get; init; }
    
    /// <summary>Whether this is a Y4M file with embedded headers.</summary>
    public bool IsY4M { get; init; }
    
    /// <summary>Offset to first frame data (after Y4M header if present).</summary>
    public long DataOffset { get; init; }
}

/// <summary>
/// A single frame from raw video stream.
/// </summary>
public sealed class RawVideoFrame
{
    /// <summary>Frame number (0-based).</summary>
    public long FrameNumber { get; init; }
    
    /// <summary>Presentation timestamp in 100-nanosecond units.</summary>
    public long PresentationTimestamp { get; init; }
    
    /// <summary>Duration in 100-nanosecond units.</summary>
    public long Duration { get; init; }
    
    /// <summary>Frame pixel data.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
}

/// <summary>
/// Demuxer for raw/headerless video data. Also supports YUV4MPEG2 (Y4M) format.
/// </summary>
public sealed class RawVideoDemuxer : IDisposable
{
    private const int TicksPerSecond = 10_000_000;
    
    // Y4M magic
    private static readonly byte[] Y4MMagic = "YUV4MPEG2"u8.ToArray();
    
    // Standard resolution presets
    private static readonly (string name, int width, int height)[] Presets =
    [
        ("sqcif", 128, 96),
        ("qcif", 176, 144),
        ("cif", 352, 288),
        ("4cif", 704, 576),
        ("16cif", 1408, 1152)
    ];
    
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly RawVideoConfiguration configuration;
    private readonly byte[] frameBuffer;
    
    private RawVideoInfo? info;
    private long currentFrame;
    private long dataStartOffset;
    private bool disposed;
    
    /// <summary>
    /// Creates a new raw video demuxer.
    /// </summary>
    public RawVideoDemuxer(Stream stream, RawVideoConfiguration configuration, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.leaveOpen = leaveOpen;
        
        int frameSize = CalculateFrameSize(configuration.Width, configuration.Height, configuration.Format);
        frameBuffer = new byte[frameSize];
    }
    
    /// <summary>
    /// Checks if data starts with Y4M header.
    /// </summary>
    public static bool CanParse(ReadOnlySpan<byte> header)
    {
        if (header.Length < Y4MMagic.Length)
            return false;
        
        return header[..Y4MMagic.Length].SequenceEqual(Y4MMagic);
    }
    
    /// <summary>
    /// Creates a demuxer for Y4M files (auto-detects parameters from header).
    /// </summary>
    public static RawVideoDemuxer? CreateFromY4M(Stream stream, bool leaveOpen = false)
    {
        Span<byte> header = stackalloc byte[256];
        int headerLength = 0;
        
        // Read Y4M header line (until newline)
        int b;
        while (headerLength < 255 && (b = stream.ReadByte()) != -1)
        {
            if (b == '\n')
                break;
            header[headerLength++] = (byte)b;
        }
        
        if (headerLength < Y4MMagic.Length)
            return null;
        
        string headerStr = Encoding.ASCII.GetString(header[..headerLength]);
        if (!headerStr.StartsWith("YUV4MPEG2"))
            return null;
        
        // Parse header parameters
        int width = 0, height = 0;
        int fpsNum = 30, fpsDen = 1;
        int sarNum = 1, sarDen = 1;
        RawVideoFormat format = RawVideoFormat.I420;
        
        string[] parts = headerStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            if (part.Length < 2)
                continue;
            
            char key = part[0];
            string value = part[1..];
            
            switch (key)
            {
                case 'W':
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
                    break;
                    
                case 'H':
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
                    break;
                    
                case 'F':
                    int colonIdx = value.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        int.TryParse(value[..colonIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out fpsNum);
                        int.TryParse(value[(colonIdx + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out fpsDen);
                    }
                    else
                    {
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out fpsNum);
                        fpsDen = 1;
                    }
                    break;
                    
                case 'A':
                    int aColonIdx = value.IndexOf(':');
                    if (aColonIdx > 0)
                    {
                        int.TryParse(value[..aColonIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out sarNum);
                        int.TryParse(value[(aColonIdx + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out sarDen);
                    }
                    break;
                    
                case 'C':
                    format = ParseY4MChroma(value);
                    break;
            }
        }
        
        if (width <= 0 || height <= 0)
            return null;
        
        var config = new RawVideoConfiguration
        {
            Width = width,
            Height = height,
            FrameRateNumerator = fpsNum > 0 ? fpsNum : 30,
            FrameRateDenominator = fpsDen > 0 ? fpsDen : 1,
            Format = format,
            SampleAspectRatioWidth = sarNum > 0 ? sarNum : 1,
            SampleAspectRatioHeight = sarDen > 0 ? sarDen : 1
        };
        
        var demuxer = new RawVideoDemuxer(stream, config, leaveOpen);
        demuxer.dataStartOffset = stream.Position;
        return demuxer;
    }
    
    private static RawVideoFormat ParseY4MChroma(string chroma)
    {
        return chroma.ToLowerInvariant() switch
        {
            "420" or "420jpeg" or "420paldv" or "420mpeg2" => RawVideoFormat.I420,
            "422" => RawVideoFormat.I422,
            "444" => RawVideoFormat.I444,
            "mono" or "gray" => RawVideoFormat.Gray8,
            _ => RawVideoFormat.I420
        };
    }
    
    /// <summary>
    /// Parses the raw video stream and returns info.
    /// </summary>
    public RawVideoInfo Parse()
    {
        if (info != null)
            return info;
        
        int width = configuration.Width;
        int height = configuration.Height;
        
        if (width <= 0 || height <= 0)
        {
            info = new RawVideoInfo { IsValid = false };
            return info;
        }
        
        int frameSize = CalculateFrameSize(width, height, configuration.Format);
        if (frameSize <= 0)
        {
            info = new RawVideoInfo { IsValid = false };
            return info;
        }
        
        // Calculate total frames from file size
        long streamLength = stream.CanSeek ? stream.Length : 0;
        long dataLength = streamLength - dataStartOffset;
        long totalFrames = frameSize > 0 ? dataLength / frameSize : 0;
        
        // Calculate duration
        long durationTicks = 0;
        if (totalFrames > 0 && configuration.FrameRateDenominator > 0 && configuration.FrameRateNumerator > 0)
        {
            durationTicks = (totalFrames * configuration.FrameRateDenominator * TicksPerSecond) 
                / configuration.FrameRateNumerator;
        }
        
        info = new RawVideoInfo
        {
            IsValid = true,
            Width = width,
            Height = height,
            FrameRateNumerator = configuration.FrameRateNumerator,
            FrameRateDenominator = configuration.FrameRateDenominator,
            Format = configuration.Format,
            SampleAspectRatioWidth = configuration.SampleAspectRatioWidth,
            SampleAspectRatioHeight = configuration.SampleAspectRatioHeight,
            FrameSize = frameSize,
            TotalFrames = totalFrames,
            Duration = durationTicks,
            IsY4M = dataStartOffset > 0,
            DataOffset = dataStartOffset
        };
        
        return info;
    }
    
    /// <summary>
    /// Reads the next frame from the stream.
    /// </summary>
    public RawVideoFrame? ReadFrame()
    {
        if (info == null)
            Parse();
        
        if (info == null || !info.IsValid)
            return null;
        
        // Skip Y4M frame header if present
        if (info.IsY4M)
        {
            // Each Y4M frame starts with "FRAME" followed by optional params and newline
            byte[] frameHeader = new byte[6];
            int read = stream.Read(frameHeader, 0, 6);
            if (read < 6)
                return null;
            
            // Verify FRAME header
            if (frameHeader[0] != 'F' || frameHeader[1] != 'R' || 
                frameHeader[2] != 'A' || frameHeader[3] != 'M' || 
                frameHeader[4] != 'E')
            {
                return null;
            }
            
            // Skip to newline
            if (frameHeader[5] != '\n')
            {
                int b;
                while ((b = stream.ReadByte()) != -1 && b != '\n')
                {
                    // Skip frame parameters
                }
            }
        }
        
        // Read frame data
        int bytesRead = stream.Read(frameBuffer, 0, info.FrameSize);
        if (bytesRead < info.FrameSize)
            return null;
        
        // Calculate timestamp
        long frameDuration = configuration.FrameRateNumerator > 0
            ? (configuration.FrameRateDenominator * TicksPerSecond) / configuration.FrameRateNumerator
            : TicksPerSecond / 30;
        
        long pts = currentFrame * frameDuration;
        
        var frame = new RawVideoFrame
        {
            FrameNumber = currentFrame,
            PresentationTimestamp = pts,
            Duration = frameDuration,
            Data = frameBuffer.AsMemory()[..info.FrameSize]
        };
        
        currentFrame++;
        return frame;
    }
    
    /// <summary>
    /// Seeks to a specific frame.
    /// </summary>
    public bool SeekToFrame(long frameNumber)
    {
        if (info == null)
            Parse();
        
        if (info == null || !info.IsValid || !stream.CanSeek)
            return false;
        
        if (frameNumber < 0 || frameNumber >= info.TotalFrames)
            return false;
        
        // For Y4M, we can't easily seek because of variable frame headers
        // For raw video, we can directly calculate the position
        if (!info.IsY4M)
        {
            long position = dataStartOffset + (frameNumber * info.FrameSize);
            stream.Position = position;
            currentFrame = frameNumber;
            return true;
        }
        
        // For Y4M, seek from start (slow but accurate)
        stream.Position = dataStartOffset;
        currentFrame = 0;
        
        while (currentFrame < frameNumber)
        {
            // Skip frame header
            int b;
            while ((b = stream.ReadByte()) != -1 && b != '\n')
            {
                // Skip to newline
            }
            
            if (b == -1)
                return false;
            
            // Skip frame data
            long newPos = stream.Position + info.FrameSize;
            if (newPos > stream.Length)
                return false;
            
            stream.Position = newPos;
            currentFrame++;
        }
        
        return true;
    }
    
    /// <summary>
    /// Seeks to a specific time.
    /// </summary>
    public bool SeekToTime(long ticks)
    {
        if (info == null)
            Parse();
        
        if (info == null || !info.IsValid || configuration.FrameRateNumerator <= 0)
            return false;
        
        // Calculate target frame
        long targetFrame = (ticks * configuration.FrameRateNumerator) 
            / (configuration.FrameRateDenominator * TicksPerSecond);
        
        return SeekToFrame(targetFrame);
    }
    
    /// <summary>
    /// Current position in the stream.
    /// </summary>
    public long Position => stream.Position;
    
    /// <summary>
    /// Current frame number.
    /// </summary>
    public long CurrentFrame => currentFrame;
    
    /// <summary>
    /// Calculates frame size in bytes for given format and dimensions.
    /// </summary>
    public static int CalculateFrameSize(int width, int height, RawVideoFormat format)
    {
        if (width <= 0 || height <= 0)
            return 0;
        
        return format switch
        {
            // Planar YUV formats
            RawVideoFormat.I420 or RawVideoFormat.Nv12 or RawVideoFormat.Nv21 
                => (width * height) + (width / 2 * height / 2) * 2, // Y + U/2 + V/2
            
            RawVideoFormat.I422 
                => (width * height) + (width / 2 * height) * 2, // Y + U/2 + V/2
            
            RawVideoFormat.I444 
                => width * height * 3, // Y + U + V
            
            // Packed YUV formats
            RawVideoFormat.Yuy2 or RawVideoFormat.Uyvy 
                => width * height * 2,
            
            // Grayscale
            RawVideoFormat.Gray8 => width * height,
            RawVideoFormat.Gray16 => width * height * 2,
            
            // RGB formats
            RawVideoFormat.Rgb24 or RawVideoFormat.Bgr24 
                => width * height * 3,
            
            RawVideoFormat.Rgba32 or RawVideoFormat.Bgra32 
                => width * height * 4,
            
            _ => 0
        };
    }
    
    /// <summary>
    /// Gets bits per pixel for a format.
    /// </summary>
    public static int GetBitsPerPixel(RawVideoFormat format)
    {
        return format switch
        {
            RawVideoFormat.I420 or RawVideoFormat.Nv12 or RawVideoFormat.Nv21 => 12,
            RawVideoFormat.I422 or RawVideoFormat.Yuy2 or RawVideoFormat.Uyvy or RawVideoFormat.Gray16 => 16,
            RawVideoFormat.I444 or RawVideoFormat.Rgb24 or RawVideoFormat.Bgr24 => 24,
            RawVideoFormat.Rgba32 or RawVideoFormat.Bgra32 => 32,
            RawVideoFormat.Gray8 => 8,
            _ => 0
        };
    }
    
    public void Dispose()
    {
        if (disposed)
            return;
        
        disposed = true;
        
        if (!leaveOpen)
            stream.Dispose();
    }
}

// MediaKernel - Raw Audio Demuxer
// Demuxer for raw/headerless audio data with configurable format parameters
// Supports PCM, floating point, and various sample depths

using System;
using System.IO;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Raw audio sample format.
/// </summary>
public enum RawAudioFormat
{
    /// <summary>Signed 8-bit PCM.</summary>
    Signed8Bit,
    
    /// <summary>Unsigned 8-bit PCM.</summary>
    Unsigned8Bit,
    
    /// <summary>Signed 16-bit PCM, little-endian.</summary>
    Signed16LittleEndian,
    
    /// <summary>Signed 16-bit PCM, big-endian.</summary>
    Signed16BigEndian,
    
    /// <summary>Signed 24-bit PCM, little-endian (3 bytes per sample).</summary>
    Signed24LittleEndian,
    
    /// <summary>Signed 24-bit PCM, big-endian (3 bytes per sample).</summary>
    Signed24BigEndian,
    
    /// <summary>Signed 32-bit PCM, little-endian.</summary>
    Signed32LittleEndian,
    
    /// <summary>Signed 32-bit PCM, big-endian.</summary>
    Signed32BigEndian,
    
    /// <summary>32-bit IEEE floating point, little-endian.</summary>
    Float32LittleEndian,
    
    /// <summary>32-bit IEEE floating point, big-endian.</summary>
    Float32BigEndian,
    
    /// <summary>64-bit IEEE floating point, little-endian.</summary>
    Float64LittleEndian,
    
    /// <summary>64-bit IEEE floating point, big-endian.</summary>
    Float64BigEndian
}

/// <summary>
/// Configuration for raw audio demuxer.
/// </summary>
public sealed class RawAudioConfiguration
{
    /// <summary>Sample rate in Hz. Default is 48000.</summary>
    public int SampleRate { get; init; } = 48000;
    
    /// <summary>Number of audio channels. Default is 2 (stereo).</summary>
    public int ChannelCount { get; init; } = 2;
    
    /// <summary>Sample format. Default is signed 16-bit little-endian.</summary>
    public RawAudioFormat Format { get; init; } = RawAudioFormat.Signed16LittleEndian;
    
    /// <summary>Audio language (ISO 639 code). Default is "eng".</summary>
    public string Language { get; init; } = "eng";
    
    /// <summary>Frame duration in milliseconds. Default is 50ms.</summary>
    public int FrameDurationMilliseconds { get; init; } = 50;
    
    /// <summary>Validate configuration parameters.</summary>
    public bool IsValid()
    {
        return SampleRate >= 1 && SampleRate <= 384000 &&
               ChannelCount >= 1 && ChannelCount <= 32 &&
               FrameDurationMilliseconds >= 1 && FrameDurationMilliseconds <= 1000;
    }
}

/// <summary>
/// Raw audio stream information.
/// </summary>
public sealed class RawAudioStreamInfo
{
    /// <summary>Whether parsing was successful.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }
    
    /// <summary>Number of audio channels.</summary>
    public int ChannelCount { get; init; }
    
    /// <summary>Sample format.</summary>
    public RawAudioFormat Format { get; init; }
    
    /// <summary>Bits per sample.</summary>
    public int BitsPerSample { get; init; }
    
    /// <summary>Byte size of one sample frame (all channels).</summary>
    public int BytesPerFrame { get; init; }
    
    /// <summary>Bit rate in bits per second.</summary>
    public int BitRate { get; init; }
    
    /// <summary>File size in bytes (0 for streams).</summary>
    public long FileSize { get; init; }
    
    /// <summary>Total duration in microseconds (0 if unknown).</summary>
    public long DurationMicroseconds { get; init; }
    
    /// <summary>Total number of samples (0 if unknown).</summary>
    public long TotalSampleCount { get; init; }
}

/// <summary>
/// Raw audio frame data.
/// </summary>
public readonly struct RawAudioFrame
{
    /// <summary>Audio sample data.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
    
    /// <summary>Presentation timestamp in microseconds.</summary>
    public long PresentationTimestamp { get; init; }
    
    /// <summary>Decode timestamp in microseconds.</summary>
    public long DecodeTimestamp { get; init; }
    
    /// <summary>Duration in microseconds.</summary>
    public long DurationMicroseconds { get; init; }
    
    /// <summary>Number of samples in this frame.</summary>
    public int SampleCount { get; init; }
    
    /// <summary>Frame index.</summary>
    public long FrameIndex { get; init; }
}

/// <summary>
/// Demuxer for raw/headerless audio data.
/// Requires format parameters to be specified since there is no header.
/// </summary>
public sealed class RawAudioDemuxer : IDisposable
{
    private const int MaxBitRate = 50_000_000; // 50 Mbps limit
    
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly RawAudioConfiguration config;
    
    private int bitsPerSample;
    private int bytesPerSampleFrame;
    private int frameSize;
    private int samplesPerFrame;
    private int bitRate;
    private long fileSize;
    private long totalSamples;
    private long durationMicroseconds;
    private long samplePosition;
    private long frameIndex;
    private bool disposed;
    
    /// <summary>
    /// Creates a new raw audio demuxer.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="configuration">Audio format configuration.</param>
    /// <param name="leaveOpen">Whether to leave the stream open when disposed.</param>
    public RawAudioDemuxer(Stream stream, RawAudioConfiguration configuration, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.config = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.leaveOpen = leaveOpen;
        
        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(stream));
        
        if (!configuration.IsValid())
            throw new ArgumentException("Invalid configuration", nameof(configuration));
        
        bitsPerSample = GetBitsPerSample(configuration.Format);
        bytesPerSampleFrame = config.ChannelCount * (bitsPerSample / 8);
        samplesPerFrame = Math.Max(config.SampleRate * config.FrameDurationMilliseconds / 1000, 1);
        frameSize = samplesPerFrame * bytesPerSampleFrame;
        bitRate = config.SampleRate * config.ChannelCount * bitsPerSample;
        
        if (bitRate > MaxBitRate)
            throw new ArgumentException($"Bit rate {bitRate} exceeds maximum of {MaxBitRate}");
        
        fileSize = stream.CanSeek ? stream.Length : 0;
        
        if (fileSize > 0 && bytesPerSampleFrame > 0)
        {
            totalSamples = fileSize / bytesPerSampleFrame;
            durationMicroseconds = totalSamples * 1_000_000L / config.SampleRate;
        }
    }
    
    /// <summary>
    /// Parse the stream and return format information.
    /// </summary>
    public RawAudioStreamInfo Parse()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        return new RawAudioStreamInfo
        {
            IsValid = true,
            SampleRate = config.SampleRate,
            ChannelCount = config.ChannelCount,
            Format = config.Format,
            BitsPerSample = bitsPerSample,
            BytesPerFrame = bytesPerSampleFrame,
            BitRate = bitRate,
            FileSize = fileSize,
            DurationMicroseconds = durationMicroseconds,
            TotalSampleCount = totalSamples
        };
    }
    
    /// <summary>
    /// Read the next audio frame from the stream.
    /// </summary>
    public bool TryReadFrame(out RawAudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        frame = default;
        
        byte[] buffer = new byte[frameSize];
        int read = stream.Read(buffer, 0, frameSize);
        
        if (read == 0)
            return false;
        
        // Handle partial read at end of file
        int actualSamples = read / bytesPerSampleFrame;
        if (actualSamples == 0)
            return false;
        
        int actualBytes = actualSamples * bytesPerSampleFrame;
        
        long presentationTimestamp = samplePosition * 1_000_000L / config.SampleRate;
        long frameDuration = actualSamples * 1_000_000L / config.SampleRate;
        
        frame = new RawAudioFrame
        {
            Data = buffer.AsMemory(0, actualBytes),
            PresentationTimestamp = presentationTimestamp,
            DecodeTimestamp = presentationTimestamp,
            DurationMicroseconds = frameDuration,
            SampleCount = actualSamples,
            FrameIndex = frameIndex
        };
        
        samplePosition += actualSamples;
        frameIndex++;
        
        return true;
    }
    
    /// <summary>
    /// Seek to a specific time position.
    /// </summary>
    /// <param name="timeMicroseconds">Target time in microseconds.</param>
    /// <returns>True if seek was successful.</returns>
    public bool SeekToTime(long timeMicroseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        
        if (!stream.CanSeek)
            return false;
        
        // Calculate sample position
        long targetSample = timeMicroseconds * config.SampleRate / 1_000_000L;
        
        // Align to frame boundary
        targetSample = (targetSample / samplesPerFrame) * samplesPerFrame;
        
        // Calculate byte position
        long bytePosition = targetSample * bytesPerSampleFrame;
        
        if (bytePosition < 0)
            bytePosition = 0;
        
        if (bytePosition > fileSize)
            bytePosition = fileSize;
        
        stream.Position = bytePosition;
        samplePosition = bytePosition / bytesPerSampleFrame;
        
        return true;
    }
    
    /// <summary>
    /// Get current playback position in microseconds.
    /// </summary>
    public long GetPositionMicroseconds()
    {
        return samplePosition * 1_000_000L / config.SampleRate;
    }
    
    /// <summary>
    /// Get total duration in microseconds.
    /// </summary>
    public long GetDurationMicroseconds()
    {
        return durationMicroseconds;
    }
    
    /// <summary>
    /// Get bits per sample for a given format.
    /// </summary>
    public static int GetBitsPerSample(RawAudioFormat format)
    {
        return format switch
        {
            RawAudioFormat.Signed8Bit => 8,
            RawAudioFormat.Unsigned8Bit => 8,
            RawAudioFormat.Signed16LittleEndian => 16,
            RawAudioFormat.Signed16BigEndian => 16,
            RawAudioFormat.Signed24LittleEndian => 24,
            RawAudioFormat.Signed24BigEndian => 24,
            RawAudioFormat.Signed32LittleEndian => 32,
            RawAudioFormat.Signed32BigEndian => 32,
            RawAudioFormat.Float32LittleEndian => 32,
            RawAudioFormat.Float32BigEndian => 32,
            RawAudioFormat.Float64LittleEndian => 64,
            RawAudioFormat.Float64BigEndian => 64,
            _ => throw new ArgumentException($"Unknown format: {format}")
        };
    }
    
    /// <summary>
    /// Get bytes per sample for a given format.
    /// </summary>
    public static int GetBytesPerSample(RawAudioFormat format)
    {
        return GetBitsPerSample(format) / 8;
    }
    
    /// <summary>
    /// Check if format uses floating point samples.
    /// </summary>
    public static bool IsFloatingPoint(RawAudioFormat format)
    {
        return format == RawAudioFormat.Float32LittleEndian ||
               format == RawAudioFormat.Float32BigEndian ||
               format == RawAudioFormat.Float64LittleEndian ||
               format == RawAudioFormat.Float64BigEndian;
    }
    
    /// <summary>
    /// Check if format uses big-endian byte order.
    /// </summary>
    public static bool IsBigEndian(RawAudioFormat format)
    {
        return format == RawAudioFormat.Signed16BigEndian ||
               format == RawAudioFormat.Signed24BigEndian ||
               format == RawAudioFormat.Signed32BigEndian ||
               format == RawAudioFormat.Float32BigEndian ||
               format == RawAudioFormat.Float64BigEndian;
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

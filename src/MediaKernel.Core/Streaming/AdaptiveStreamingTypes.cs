// MediaKernel - Adaptive Streaming Infrastructure
// Enables HLS/DASH playback with automatic quality switching

using System;
using System.Collections.Generic;
using System.Threading;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// Represents a quality variant in an adaptive stream.
/// </summary>
public readonly struct StreamVariant
{
    public readonly long Bandwidth;
    public readonly int Width;
    public readonly int Height;
    public readonly double FrameRate;
    public readonly string Codecs;
    public readonly Uri PlaylistUri;
    public readonly string Language;
    public readonly bool IsDefault;
    public readonly int VariantIndex;

    public StreamVariant(
        long bandwidth,
        int width,
        int height,
        double frameRate,
        string codecs,
        Uri playlistUri,
        string language,
        bool isDefault,
        int variantIndex)
    {
        Bandwidth = bandwidth;
        Width = width;
        Height = height;
        FrameRate = frameRate;
        Codecs = codecs ?? string.Empty;
        PlaylistUri = playlistUri;
        Language = language ?? string.Empty;
        IsDefault = isDefault;
        VariantIndex = variantIndex;
    }

    public bool HasVideo => Width > 0 && Height > 0;
    public bool HasAudio => !string.IsNullOrEmpty(Language) || Codecs.Contains("mp4a", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Represents a media segment in an adaptive stream.
/// </summary>
public readonly struct MediaSegment
{
    public readonly Uri Uri;
    public readonly long SequenceNumber;
    public readonly long DurationMicroseconds;
    public readonly long StartTimeMicroseconds;
    public readonly byte[]? InitializationData;
    public readonly bool IsDiscontinuity;
    public readonly bool IsEndOfStream;
    public readonly int VariantIndex;
    public readonly string? ByteRange;

    public MediaSegment(
        Uri uri,
        long sequenceNumber,
        long durationMicroseconds,
        long startTimeMicroseconds,
        byte[]? initializationData,
        bool isDiscontinuity,
        bool isEndOfStream,
        int variantIndex,
        string? byteRange = null)
    {
        Uri = uri;
        SequenceNumber = sequenceNumber;
        DurationMicroseconds = durationMicroseconds;
        StartTimeMicroseconds = startTimeMicroseconds;
        InitializationData = initializationData;
        IsDiscontinuity = isDiscontinuity;
        IsEndOfStream = isEndOfStream;
        VariantIndex = variantIndex;
        ByteRange = byteRange;
    }

    public TimeSpan Duration => TimeSpan.FromTicks(DurationMicroseconds * 10);
    public TimeSpan StartTime => TimeSpan.FromTicks(StartTimeMicroseconds * 10);
}

/// <summary>
/// Downloaded segment with timing metadata.
/// </summary>
public readonly struct DownloadedSegment
{
    public readonly MediaSegment Segment;
    public readonly byte[] Data;
    public readonly long DownloadTimeMilliseconds;
    public readonly long BytesDownloaded;

    public DownloadedSegment(
        MediaSegment segment,
        byte[] data,
        long downloadTimeMilliseconds,
        long bytesDownloaded)
    {
        Segment = segment;
        Data = data;
        DownloadTimeMilliseconds = downloadTimeMilliseconds;
        BytesDownloaded = bytesDownloaded;
    }

    public double DownloadBitsPerSecond =>
        DownloadTimeMilliseconds > 0
            ? (BytesDownloaded * 8.0 * 1000.0) / DownloadTimeMilliseconds
            : 0;
}

/// <summary>
/// Events raised by the segment tracker.
/// </summary>
public enum TrackerEventType
{
    /// <summary>Stream discontinuity detected (codec change, timestamp reset)</summary>
    Discontinuity,

    /// <summary>Gap in segment sequence</summary>
    SegmentGap,

    /// <summary>Quality level switched</summary>
    VariantSwitch,

    /// <summary>Playlist updated with new segments</summary>
    PlaylistUpdated,

    /// <summary>Playlist update failed</summary>
    PlaylistUpdateFailed,

    /// <summary>Codec/format changed</summary>
    FormatChange,

    /// <summary>New segment started</summary>
    SegmentChange,

    /// <summary>Buffer state changed</summary>
    BufferingStateChanged,

    /// <summary>Buffer level changed</summary>
    BufferLevelChanged,

    /// <summary>Playback position changed (seek)</summary>
    PositionChanged,

    /// <summary>End of stream reached</summary>
    EndOfStream
}

/// <summary>
/// Event data for tracker events.
/// </summary>
public readonly struct TrackerEvent
{
    public readonly TrackerEventType Type;
    public readonly long TimestampMicroseconds;
    public readonly int PreviousVariantIndex;
    public readonly int CurrentVariantIndex;
    public readonly long SequenceNumber;
    public readonly string? Message;

    public TrackerEvent(
        TrackerEventType type,
        long timestampMicroseconds = 0,
        int previousVariantIndex = -1,
        int currentVariantIndex = -1,
        long sequenceNumber = 0,
        string? message = null)
    {
        Type = type;
        TimestampMicroseconds = timestampMicroseconds;
        PreviousVariantIndex = previousVariantIndex;
        CurrentVariantIndex = currentVariantIndex;
        SequenceNumber = sequenceNumber;
        Message = message;
    }
}

/// <summary>
/// Adaptation logic strategy for quality selection.
/// </summary>
public enum AdaptationStrategy
{
    /// <summary>Always select highest available quality</summary>
    AlwaysBest,

    /// <summary>Always select lowest available quality (data saver)</summary>
    AlwaysLowest,

    /// <summary>Select based on measured download rate</summary>
    RateBased,

    /// <summary>Predict bandwidth and select proactively</summary>
    Predictive,

    /// <summary>BOLA-like near-optimal buffer-based algorithm</summary>
    NearOptimal
}

/// <summary>
/// Statistics for adaptive streaming performance.
/// </summary>
public class AdaptiveStreamingStatistics
{
    private long totalBytesDownloaded;
    private long totalDownloadTimeMilliseconds;
    private int segmentsDownloaded;
    private int variantSwitches;
    private int bufferStalls;
    private int downloadErrors;

    public long TotalBytesDownloaded => Interlocked.Read(ref totalBytesDownloaded);
    public long TotalDownloadTimeMilliseconds => Interlocked.Read(ref totalDownloadTimeMilliseconds);
    public int SegmentsDownloaded => Volatile.Read(ref segmentsDownloaded);
    public int VariantSwitches => Volatile.Read(ref variantSwitches);
    public int BufferStalls => Volatile.Read(ref bufferStalls);
    public int DownloadErrors => Volatile.Read(ref downloadErrors);

    public double AverageBitsPerSecond =>
        TotalDownloadTimeMilliseconds > 0
            ? (TotalBytesDownloaded * 8.0 * 1000.0) / TotalDownloadTimeMilliseconds
            : 0;

    public void RecordSegmentDownload(long bytes, long milliseconds)
    {
        Interlocked.Add(ref totalBytesDownloaded, bytes);
        Interlocked.Add(ref totalDownloadTimeMilliseconds, milliseconds);
        Interlocked.Increment(ref segmentsDownloaded);
    }

    public void RecordVariantSwitch() => Interlocked.Increment(ref variantSwitches);
    public void RecordBufferStall() => Interlocked.Increment(ref bufferStalls);
    public void RecordDownloadError() => Interlocked.Increment(ref downloadErrors);

    public void Reset()
    {
        Interlocked.Exchange(ref totalBytesDownloaded, 0);
        Interlocked.Exchange(ref totalDownloadTimeMilliseconds, 0);
        Volatile.Write(ref segmentsDownloaded, 0);
        Volatile.Write(ref variantSwitches, 0);
        Volatile.Write(ref bufferStalls, 0);
        Volatile.Write(ref downloadErrors, 0);
    }
}

/// <summary>
/// Buffer level information for adaptation decisions.
/// </summary>
public readonly struct BufferLevel
{
    public readonly long CurrentMicroseconds;
    public readonly long TargetMicroseconds;
    public readonly long MinimumMicroseconds;
    public readonly long MaximumMicroseconds;

    public BufferLevel(
        long currentMicroseconds,
        long targetMicroseconds,
        long minimumMicroseconds,
        long maximumMicroseconds)
    {
        CurrentMicroseconds = currentMicroseconds;
        TargetMicroseconds = targetMicroseconds;
        MinimumMicroseconds = minimumMicroseconds;
        MaximumMicroseconds = maximumMicroseconds;
    }

    public double FillRatio =>
        MaximumMicroseconds > MinimumMicroseconds
            ? (double)(CurrentMicroseconds - MinimumMicroseconds) / (MaximumMicroseconds - MinimumMicroseconds)
            : 0;

    public bool IsLow => CurrentMicroseconds < MinimumMicroseconds;
    public bool IsHigh => CurrentMicroseconds >= TargetMicroseconds;
    public bool IsCritical => CurrentMicroseconds < MinimumMicroseconds / 2;
}

/// <summary>
/// Configuration for adaptive streaming.
/// </summary>
public sealed class AdaptiveStreamingConfiguration
{
    /// <summary>Target buffer duration in seconds</summary>
    public double TargetBufferSeconds { get; set; } = 30.0;

    /// <summary>Minimum buffer duration before stall in seconds</summary>
    public double MinimumBufferSeconds { get; set; } = 5.0;

    /// <summary>Maximum buffer duration in seconds</summary>
    public double MaximumBufferSeconds { get; set; } = 60.0;

    /// <summary>Initial buffer duration before playback starts</summary>
    public double InitialBufferSeconds { get; set; } = 10.0;

    /// <summary>Safety factor for bandwidth estimation (0.0-1.0)</summary>
    public double BandwidthSafetyFactor { get; set; } = 0.8;

    /// <summary>Minimum bandwidth for quality upgrades (prevent oscillation)</summary>
    public double UpgradeBandwidthThreshold { get; set; } = 1.3;

    /// <summary>Adaptation strategy to use</summary>
    public AdaptationStrategy Strategy { get; set; } = AdaptationStrategy.RateBased;

    /// <summary>Maximum video resolution (0 = unlimited)</summary>
    public int MaxVideoWidth { get; set; }
    public int MaxVideoHeight { get; set; }

    /// <summary>Maximum bandwidth in bits per second (0 = unlimited)</summary>
    public long MaxBandwidth { get; set; }

    /// <summary>Preferred language for audio tracks</summary>
    public string? PreferredAudioLanguage { get; set; }

    /// <summary>Number of retry attempts for failed downloads</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Delay between retry attempts in milliseconds</summary>
    public int RetryDelayMilliseconds { get; set; } = 500;

    /// <summary>Timeout for segment downloads in milliseconds</summary>
    public int DownloadTimeoutMilliseconds { get; set; } = 30000;

    /// <summary>How often to refresh live playlists in milliseconds</summary>
    public int LivePlaylistRefreshMilliseconds { get; set; } = 5000;
}

/// <summary>
/// Stream type for adaptive content.
/// </summary>
public enum AdaptiveStreamType
{
    Unknown,
    Video,
    Audio,
    Subtitle
}

/// <summary>
/// Represents a complete adaptive stream with all variants.
/// </summary>
public sealed class AdaptiveStream
{
    public AdaptiveStreamType Type { get; init; }
    public List<StreamVariant> Variants { get; } = new();
    public int CurrentVariantIndex { get; set; }
    public bool IsLive { get; init; }
    public long DurationMicroseconds { get; init; }

    public StreamVariant? CurrentVariant =>
        CurrentVariantIndex >= 0 && CurrentVariantIndex < Variants.Count
            ? Variants[CurrentVariantIndex]
            : null;

    public TimeSpan Duration => TimeSpan.FromTicks(DurationMicroseconds * 10);
}

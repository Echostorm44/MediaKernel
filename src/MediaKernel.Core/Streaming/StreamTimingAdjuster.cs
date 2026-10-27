// Stream timing adjustment utilities for A/V synchronization
// Allows adjusting DTS/PTS timestamps to fix lip-sync issues

using System;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// Types of media streams that can have timing adjusted.
/// </summary>
public enum MediaStreamType
{
    /// <summary>Video stream.</summary>
    Video,
    
    /// <summary>Audio stream.</summary>
    Audio,
    
    /// <summary>Subtitle stream.</summary>
    Subtitle,
    
    /// <summary>All streams.</summary>
    All
}

/// <summary>
/// Timing adjustment for a specific stream type.
/// Positive delay means the stream plays later, negative means earlier.
/// </summary>
public readonly struct StreamTimingOffset
{
    /// <summary>Stream type this offset applies to.</summary>
    public MediaStreamType StreamType { get; init; }
    
    /// <summary>Timing offset in milliseconds. Positive = delay, Negative = advance.</summary>
    public double OffsetMs { get; init; }
    
    /// <summary>Optional specific track ID (0 for all tracks of this type).</summary>
    public int TrackId { get; init; }
    
    /// <summary>Creates a timing offset for video.</summary>
    public static StreamTimingOffset Video(double offsetMs, int trackId = 0)
        => new() { StreamType = MediaStreamType.Video, OffsetMs = offsetMs, TrackId = trackId };
    
    /// <summary>Creates a timing offset for audio.</summary>
    public static StreamTimingOffset Audio(double offsetMs, int trackId = 0)
        => new() { StreamType = MediaStreamType.Audio, OffsetMs = offsetMs, TrackId = trackId };
    
    /// <summary>Creates a timing offset for subtitles.</summary>
    public static StreamTimingOffset Subtitle(double offsetMs, int trackId = 0)
        => new() { StreamType = MediaStreamType.Subtitle, OffsetMs = offsetMs, TrackId = trackId };
}

/// <summary>
/// Manages timing adjustments for media streams to fix A/V sync issues.
/// Applies timestamp offsets to DTS/PTS values.
/// </summary>
public sealed class StreamTimingAdjuster
{
    private double videoOffsetMs;
    private double audioOffsetMs;
    private double subtitleOffsetMs;
    private readonly object syncLock = new();
    
    /// <summary>
    /// Video timing offset in milliseconds.
    /// Positive = video plays later, Negative = video plays earlier.
    /// </summary>
    public double VideoOffsetMs
    {
        get { lock (syncLock) return videoOffsetMs; }
        set { lock (syncLock) videoOffsetMs = value; }
    }
    
    /// <summary>
    /// Audio timing offset in milliseconds.
    /// Positive = audio plays later, Negative = audio plays earlier.
    /// </summary>
    public double AudioOffsetMs
    {
        get { lock (syncLock) return audioOffsetMs; }
        set { lock (syncLock) audioOffsetMs = value; }
    }
    
    /// <summary>
    /// Subtitle timing offset in milliseconds.
    /// Positive = subtitles appear later, Negative = subtitles appear earlier.
    /// </summary>
    public double SubtitleOffsetMs
    {
        get { lock (syncLock) return subtitleOffsetMs; }
        set { lock (syncLock) subtitleOffsetMs = value; }
    }
    
    /// <summary>
    /// Offset in microseconds for internal calculations.
    /// </summary>
    public long VideoOffsetUs => (long)(VideoOffsetMs * 1000);
    public long AudioOffsetUs => (long)(AudioOffsetMs * 1000);
    public long SubtitleOffsetUs => (long)(SubtitleOffsetMs * 1000);
    
    /// <summary>
    /// Apply offset to the given stream type.
    /// </summary>
    public void SetOffset(MediaStreamType streamType, double offsetMs)
    {
        switch (streamType)
        {
            case MediaStreamType.Video:
                VideoOffsetMs = offsetMs;
                break;
            case MediaStreamType.Audio:
                AudioOffsetMs = offsetMs;
                break;
            case MediaStreamType.Subtitle:
                SubtitleOffsetMs = offsetMs;
                break;
            case MediaStreamType.All:
                VideoOffsetMs = offsetMs;
                AudioOffsetMs = offsetMs;
                SubtitleOffsetMs = offsetMs;
                break;
        }
    }
    
    /// <summary>
    /// Get offset for the given stream type.
    /// </summary>
    public double GetOffset(MediaStreamType streamType)
    {
        return streamType switch
        {
            MediaStreamType.Video => VideoOffsetMs,
            MediaStreamType.Audio => AudioOffsetMs,
            MediaStreamType.Subtitle => SubtitleOffsetMs,
            _ => 0
        };
    }
    
    /// <summary>
    /// Adjust a presentation timestamp for the given stream type.
    /// </summary>
    /// <param name="pts">Original PTS in microseconds.</param>
    /// <param name="streamType">Type of stream.</param>
    /// <returns>Adjusted PTS in microseconds.</returns>
    public long AdjustPts(long pts, MediaStreamType streamType)
    {
        if (pts < 0) return pts; // Invalid timestamp
        
        long offsetUs = streamType switch
        {
            MediaStreamType.Video => VideoOffsetUs,
            MediaStreamType.Audio => AudioOffsetUs,
            MediaStreamType.Subtitle => SubtitleOffsetUs,
            _ => 0
        };
        
        return pts + offsetUs;
    }
    
    /// <summary>
    /// Adjust a decode timestamp for the given stream type.
    /// </summary>
    /// <param name="dts">Original DTS in microseconds.</param>
    /// <param name="streamType">Type of stream.</param>
    /// <returns>Adjusted DTS in microseconds.</returns>
    public long AdjustDts(long dts, MediaStreamType streamType)
    {
        if (dts < 0) return dts; // Invalid timestamp
        
        long offsetUs = streamType switch
        {
            MediaStreamType.Video => VideoOffsetUs,
            MediaStreamType.Audio => AudioOffsetUs,
            MediaStreamType.Subtitle => SubtitleOffsetUs,
            _ => 0
        };
        
        return dts + offsetUs;
    }
    
    /// <summary>
    /// Adjust both PTS and DTS for a packet.
    /// </summary>
    public (long adjustedPts, long adjustedDts) AdjustTimestamps(
        long pts, long dts, MediaStreamType streamType)
    {
        return (AdjustPts(pts, streamType), AdjustDts(dts, streamType));
    }
    
    /// <summary>
    /// Adjust a TimeSpan timestamp.
    /// </summary>
    public TimeSpan AdjustTimeSpan(TimeSpan timestamp, MediaStreamType streamType)
    {
        double offsetMs = GetOffset(streamType);
        return timestamp + TimeSpan.FromMilliseconds(offsetMs);
    }
    
    /// <summary>
    /// Reset all timing offsets to zero.
    /// </summary>
    public void Reset()
    {
        lock (syncLock)
        {
            videoOffsetMs = 0;
            audioOffsetMs = 0;
            subtitleOffsetMs = 0;
        }
    }
    
    /// <summary>
    /// Check if any offsets are applied.
    /// </summary>
    public bool HasAnyOffset
    {
        get
        {
            lock (syncLock)
            {
                return Math.Abs(videoOffsetMs) > 0.001 ||
                       Math.Abs(audioOffsetMs) > 0.001 ||
                       Math.Abs(subtitleOffsetMs) > 0.001;
            }
        }
    }
    
    /// <summary>
    /// Create timing offset from common audio delay scenario.
    /// If audio is ahead of video, use positive audio delay.
    /// If audio is behind video, use negative audio delay (or positive video delay).
    /// </summary>
    /// <param name="audioDelayMs">Delay to apply to audio in milliseconds.</param>
    public void SetAudioDelay(double audioDelayMs)
    {
        // Reset video to zero and apply offset to audio only
        VideoOffsetMs = 0;
        AudioOffsetMs = audioDelayMs;
    }
    
    /// <summary>
    /// Alternative: Apply inverse delay to video instead of delaying audio.
    /// This is useful when negative timestamps should be avoided.
    /// </summary>
    /// <param name="audioDelayMs">Desired audio delay. Positive delays audio, negative advances it.</param>
    public void SetAudioDelayViaVideo(double audioDelayMs)
    {
        // Instead of negative audio delay, use positive video delay (or vice versa)
        if (audioDelayMs < 0)
        {
            // Audio needs to be advanced = delay video instead
            VideoOffsetMs = -audioDelayMs;
            AudioOffsetMs = 0;
        }
        else
        {
            VideoOffsetMs = 0;
            AudioOffsetMs = audioDelayMs;
        }
    }
}

/// <summary>
/// Extension methods for applying timing adjustments.
/// </summary>
public static class TimingAdjusterExtensions
{
    /// <summary>
    /// Convert milliseconds to 90kHz PTS/DTS ticks (MPEG standard).
    /// </summary>
    public static long MsTo90kHz(this double milliseconds)
        => (long)(milliseconds * 90);
    
    /// <summary>
    /// Convert 90kHz PTS/DTS ticks to milliseconds.
    /// </summary>
    public static double From90kHzToMs(this long ticks)
        => ticks / 90.0;
    
    /// <summary>
    /// Convert milliseconds to microseconds.
    /// </summary>
    public static long MsToUs(this double milliseconds)
        => (long)(milliseconds * 1000);
    
    /// <summary>
    /// Convert microseconds to milliseconds.
    /// </summary>
    public static double UsToMs(this long microseconds)
        => microseconds / 1000.0;
}

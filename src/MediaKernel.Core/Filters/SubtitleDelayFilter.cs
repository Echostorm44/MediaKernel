// MediaKernel - Subtitle Delay Filter
// Adjusts subtitle timing for sync correction

using System;
using MediaKernel.Core.Subtitles;

namespace MediaKernel.Core.Filters;

/// <summary>
/// Filter that adjusts subtitle timing by applying a delay offset.
/// Used for manual subtitle synchronization.
/// </summary>
public sealed class SubtitleDelayFilter
{
    private long delayMicroseconds;
    private double speedMultiplier = 1.0;
    private long referenceTimeUs;
    
    /// <summary>
    /// Current delay offset in microseconds. Positive values delay subtitles,
    /// negative values make them appear earlier.
    /// </summary>
    public long DelayMicroseconds
    {
        get => delayMicroseconds;
        set => delayMicroseconds = value;
    }
    
    /// <summary>
    /// Current delay as TimeSpan.
    /// </summary>
    public TimeSpan Delay
    {
        get => TimeSpan.FromTicks(delayMicroseconds * 10);
        set => delayMicroseconds = value.Ticks / 10;
    }
    
    /// <summary>
    /// Speed multiplier for subtitle timing. 1.0 = normal speed,
    /// less than 1.0 = slower (subtitles stretched), greater = faster.
    /// </summary>
    public double SpeedMultiplier
    {
        get => speedMultiplier;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Speed must be positive");
            speedMultiplier = value;
        }
    }
    
    /// <summary>
    /// Reference time point for speed adjustments. Typically set to the
    /// current playback position when speed is changed.
    /// </summary>
    public long ReferenceTimeMicroseconds
    {
        get => referenceTimeUs;
        set => referenceTimeUs = value;
    }
    
    /// <summary>
    /// Adjust delay by a relative amount (e.g., +500ms or -200ms).
    /// </summary>
    public void AdjustDelay(TimeSpan adjustment)
    {
        delayMicroseconds += adjustment.Ticks / 10;
    }
    
    /// <summary>
    /// Adjust delay by milliseconds.
    /// </summary>
    public void AdjustDelayMilliseconds(long milliseconds)
    {
        delayMicroseconds += milliseconds * 1000;
    }
    
    /// <summary>
    /// Reset all timing adjustments to default.
    /// </summary>
    public void Reset()
    {
        delayMicroseconds = 0;
        speedMultiplier = 1.0;
        referenceTimeUs = 0;
    }
    
    /// <summary>
    /// Apply timing adjustment to a subtitle cue.
    /// </summary>
    public SubtitleCue Apply(in SubtitleCue cue)
    {
        long adjustedStart = AdjustTime(cue.StartTimeMicroseconds);
        long adjustedEnd = AdjustTime(cue.EndTimeMicroseconds);
        
        // Ensure valid timing (start before end, no negative times)
        if (adjustedStart < 0) adjustedStart = 0;
        if (adjustedEnd <= adjustedStart) adjustedEnd = adjustedStart + 1;
        
        return new SubtitleCue(
            adjustedStart,
            adjustedEnd,
            cue.Text,
            cue.Type,
            cue.StyleName,
            cue.Position,
            cue.Bitmap);
    }
    
    /// <summary>
    /// Apply timing adjustment to raw timestamp.
    /// </summary>
    public long AdjustTime(long originalTimeUs)
    {
        // Apply speed adjustment relative to reference point
        long adjusted = originalTimeUs;
        
        if (Math.Abs(speedMultiplier - 1.0) > 0.0001)
        {
            long offset = originalTimeUs - referenceTimeUs;
            adjusted = referenceTimeUs + (long)(offset / speedMultiplier);
        }
        
        // Apply delay offset
        adjusted += delayMicroseconds;
        
        return adjusted;
    }
    
    /// <summary>
    /// Check if a cue should be visible at the given playback time.
    /// </summary>
    public bool IsVisible(in SubtitleCue cue, long playbackTimeUs)
    {
        long adjustedStart = AdjustTime(cue.StartTimeMicroseconds);
        long adjustedEnd = AdjustTime(cue.EndTimeMicroseconds);
        
        return playbackTimeUs >= adjustedStart && playbackTimeUs < adjustedEnd;
    }
    
    /// <summary>
    /// Sync subtitle to audio by setting delay based on expected vs actual time.
    /// Call this when you know a subtitle should appear at a specific moment.
    /// </summary>
    /// <param name="subtitleTimeUs">The original subtitle timestamp</param>
    /// <param name="actualAudioTimeUs">When the audio actually occurs</param>
    public void SyncToAudio(long subtitleTimeUs, long actualAudioTimeUs)
    {
        delayMicroseconds = actualAudioTimeUs - subtitleTimeUs;
    }
}

/// <summary>
/// Manages subtitle timing synchronization with support for multiple tracks.
/// </summary>
public sealed class SubtitleSyncManager
{
    private readonly SubtitleDelayFilter[] trackFilters;
    private int activeTrackIndex;
    
    /// <summary>
    /// Create manager supporting up to the specified number of subtitle tracks.
    /// </summary>
    public SubtitleSyncManager(int maxTracks = 8)
    {
        trackFilters = new SubtitleDelayFilter[maxTracks];
        for (int i = 0; i < maxTracks; i++)
        {
            trackFilters[i] = new SubtitleDelayFilter();
        }
    }
    
    /// <summary>
    /// Currently active subtitle track index.
    /// </summary>
    public int ActiveTrackIndex
    {
        get => activeTrackIndex;
        set
        {
            if (value < 0 || value >= trackFilters.Length)
                throw new ArgumentOutOfRangeException(nameof(value));
            activeTrackIndex = value;
        }
    }
    
    /// <summary>
    /// Get the filter for the active track.
    /// </summary>
    public SubtitleDelayFilter ActiveFilter => trackFilters[activeTrackIndex];
    
    /// <summary>
    /// Get filter for a specific track.
    /// </summary>
    public SubtitleDelayFilter GetFilter(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= trackFilters.Length)
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        return trackFilters[trackIndex];
    }
    
    /// <summary>
    /// Apply active track's timing adjustment.
    /// </summary>
    public SubtitleCue Apply(in SubtitleCue cue) => ActiveFilter.Apply(cue);
    
    /// <summary>
    /// Reset timing for all tracks.
    /// </summary>
    public void ResetAll()
    {
        foreach (var filter in trackFilters)
        {
            filter.Reset();
        }
    }
    
    /// <summary>
    /// Set the same delay for all tracks.
    /// </summary>
    public void SetGlobalDelay(TimeSpan delay)
    {
        long delayUs = delay.Ticks / 10;
        foreach (var filter in trackFilters)
        {
            filter.DelayMicroseconds = delayUs;
        }
    }
}

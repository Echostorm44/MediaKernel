namespace MediaKernel.Core;

/// <summary>
/// High-precision master clock for audio/video synchronization.
/// Uses hardware timestamps for sub-millisecond accuracy.
/// </summary>
/// <remarks>
/// The audio stream typically acts as the master because human hearing
/// is more sensitive to audio jitters than the eye is to dropped video frames.
/// This clock tracks the relationship between system time and stream time.
/// </remarks>
public sealed class MasterClock
{
    private long referenceSystemTime;
    private long referenceStreamTime;
    private double playbackRate = 1.0;
    private bool isPaused = true;

    /// <summary>
    /// High precision frequency to convert hardware ticks to TimeSpan ticks.
    /// Cached at startup to avoid repeated division.
    /// </summary>
    private static readonly double TickFrequency = 
        (double)TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Gets the current playback rate. 1.0 is normal speed.
    /// </summary>
    public double PlaybackRate => playbackRate;

    /// <summary>
    /// Gets whether the clock is currently paused.
    /// </summary>
    public bool IsPaused => isPaused;

    /// <summary>
    /// Sets the reference point for stream time synchronization.
    /// Call this when starting playback or after seeking.
    /// </summary>
    /// <param name="streamTimeTicks">The stream time in TimeSpan ticks at the reference point.</param>
    public void SetReference(long streamTimeTicks)
    {
        referenceSystemTime = System.Diagnostics.Stopwatch.GetTimestamp();
        referenceStreamTime = streamTimeTicks;
        isPaused = false;
    }

    /// <summary>
    /// Gets the current stream time based on elapsed system time and playback rate.
    /// </summary>
    /// <returns>The current stream position in TimeSpan ticks.</returns>
    public long GetCurrentStreamTime()
    {
        if (isPaused) 
            return referenceStreamTime;

        long currentSystemTime = System.Diagnostics.Stopwatch.GetTimestamp();
        long elapsedSystemTicks = currentSystemTime - referenceSystemTime;

        // Convert hardware ticks to stream ticks, applying playback rate
        long elapsedStreamTime = (long)(elapsedSystemTicks * TickFrequency * playbackRate);
        return referenceStreamTime + elapsedStreamTime;
    }

    /// <summary>
    /// Pauses the clock, freezing the current stream time.
    /// </summary>
    public void Pause()
    {
        if (isPaused) 
            return;

        // Capture current position before pausing
        referenceStreamTime = GetCurrentStreamTime();
        isPaused = true;
    }

    /// <summary>
    /// Resumes the clock from the current position.
    /// </summary>
    public void Resume()
    {
        if (!isPaused) 
            return;

        referenceSystemTime = System.Diagnostics.Stopwatch.GetTimestamp();
        isPaused = false;
    }

    /// <summary>
    /// Adjusts the playback rate while maintaining the current stream position.
    /// </summary>
    /// <param name="newRate">The new playback rate. 1.0 is normal, 2.0 is double speed, etc.</param>
    public void AdjustRate(double newRate)
    {
        // Update references before changing rate to avoid time jumps
        referenceStreamTime = GetCurrentStreamTime();
        referenceSystemTime = System.Diagnostics.Stopwatch.GetTimestamp();
        playbackRate = newRate;
    }

    /// <summary>
    /// Resets the clock to its initial state.
    /// </summary>
    public void Reset()
    {
        referenceSystemTime = 0;
        referenceStreamTime = 0;
        playbackRate = 1.0;
        isPaused = true;
    }
}

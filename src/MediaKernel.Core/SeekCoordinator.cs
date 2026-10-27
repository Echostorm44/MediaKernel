// SeekCoordinator - Coordinates seeking across demuxer, decoders, and clock

using MediaKernel.Core.Demuxers;

namespace MediaKernel.Core;

/// <summary>
/// Coordinates seeking operations across the media pipeline.
/// Handles flush, decoder reset, and clock synchronization.
/// </summary>
public sealed class SeekCoordinator
{
    private readonly MasterClock masterClock;
    private readonly MediaPipeline pipeline;
    private readonly object seekLock = new();
    
    private volatile bool isSeeking;
    private long lastSeekTimestamp;

    /// <summary>
    /// Tolerance for considering a seek "accurate" (50ms).
    /// </summary>
    private const long SeekAccuracyToleranceTicks = 50 * TimeSpan.TicksPerMillisecond;

    public SeekCoordinator(MasterClock masterClock, MediaPipeline pipeline)
    {
        this.masterClock = masterClock ?? throw new ArgumentNullException(nameof(masterClock));
        this.pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }
    
    /// <summary>
    /// Gets whether a seek operation is in progress.
    /// </summary>
    public bool IsSeeking => isSeeking;
    
    /// <summary>
    /// Gets the last seek target timestamp.
    /// </summary>
    public long LastSeekTimestamp => lastSeekTimestamp;

    /// <summary>
    /// Initiates a seek operation to the specified timestamp.
    /// </summary>
    /// <param name="targetTimestampTicks">Target position in ticks.</param>
    /// <param name="seekAction">Delegate to perform the actual demuxer seek.</param>
    /// <param name="decoderResetActions">Actions to reset decoder state.</param>
    /// <returns>Result of the seek operation.</returns>
    public SeekResult Seek(
        long targetTimestampTicks,
        Func<long, SeekResult> seekAction,
        params Action[] decoderResetActions)
    {
        lock (seekLock)
        {
            isSeeking = true;
            lastSeekTimestamp = targetTimestampTicks;
            
            try
            {
                // 1. Pause the clock
                bool wasPlaying = !masterClock.IsPaused;
                masterClock.Pause();
                
                // 2. Flush the pipeline (clear all pending frames)
                pipeline.Flush();
                
                // 3. Reset all decoders
                foreach (var resetAction in decoderResetActions)
                {
                    try
                    {
                        resetAction();
                    }
                    catch (Exception ex)
                    {
                        // Log but continue - decoder reset should be best-effort
                        System.Diagnostics.Debug.WriteLine($"Decoder reset failed: {ex.Message}");
                    }
                }
                
                // 4. Perform the demuxer seek
                var result = seekAction(targetTimestampTicks);
                
                if (!result.Success)
                {
                    isSeeking = false;
                    return result;
                }
                
                // 5. Update the master clock to the new position
                masterClock.SetReference(result.ActualTimestampTicks);
                
                // 6. Pause again (seek should leave in paused state, caller can resume)
                if (!wasPlaying)
                {
                    masterClock.Pause();
                }
                
                return result;
            }
            finally
            {
                isSeeking = false;
            }
        }
    }
    
    /// <summary>
    /// Simplified seek that takes a timestamp in milliseconds.
    /// </summary>
    public SeekResult SeekMs(
        double targetTimestampMs,
        Func<double, bool> seekAction,
        params Action[] decoderResetActions)
    {
        long targetTicks = (long)(targetTimestampMs * TimeSpan.TicksPerMillisecond);
        
        return Seek(
            targetTicks,
            ticks =>
            {
                double ms = ticks / (double)TimeSpan.TicksPerMillisecond;
                bool success = seekAction(ms);
                return success 
                    ? SeekResult.Succeeded(ticks, isApproximate: true)
                    : SeekResult.Failed("Demuxer seek failed");
            },
            decoderResetActions);
    }
    
    /// <summary>
    /// Performs a seek using a SeekIndex for keyframe location.
    /// </summary>
    /// <param name="targetTimestampTicks">Target position in ticks.</param>
    /// <param name="seekIndex">Index to look up keyframe positions.</param>
    /// <param name="streamSeekAction">Action to seek the stream to a file position.</param>
    /// <param name="decoderResetActions">Actions to reset decoder state.</param>
    public SeekResult SeekWithIndex(
        long targetTimestampTicks,
        SeekIndex seekIndex,
        Action<long> streamSeekAction,
        params Action[] decoderResetActions)
    {
        if (seekIndex.IsEmpty)
        {
            return SeekResult.Failed("No seek index available");
        }
        
        return Seek(
            targetTimestampTicks,
            ticks =>
            {
                // Find the keyframe at or before the target
                var seekPoint = seekIndex.FindSeekPoint(ticks);
                
                if (seekPoint == null)
                {
                    return SeekResult.Failed("No suitable seek point found");
                }
                
                try
                {
                    streamSeekAction(seekPoint.Value.FilePosition);
                    
                    bool isApproximate = Math.Abs(seekPoint.Value.TimestampTicks - ticks) > SeekAccuracyToleranceTicks;
                    
                    return SeekResult.Succeeded(seekPoint.Value.TimestampTicks, isApproximate);
                }
                catch (Exception ex)
                {
                    return SeekResult.Failed($"Stream seek failed: {ex.Message}");
                }
            },
            decoderResetActions);
    }
    
    /// <summary>
    /// Performs a relative seek (skip forward/backward).
    /// </summary>
    /// <param name="deltaTicks">Amount to skip (positive = forward, negative = backward).</param>
    /// <param name="seekAction">Delegate to perform the actual seek.</param>
    /// <param name="decoderResetActions">Actions to reset decoder state.</param>
    public SeekResult SeekRelative(
        long deltaTicks,
        Func<long, SeekResult> seekAction,
        params Action[] decoderResetActions)
    {
        long currentPosition = masterClock.GetCurrentStreamTime();
        long targetPosition = Math.Max(0, currentPosition + deltaTicks);
        
        return Seek(targetPosition, seekAction, decoderResetActions);
    }
    
    /// <summary>
    /// Seeks to the beginning of the stream.
    /// </summary>
    public SeekResult SeekToStart(
        Func<long, SeekResult> seekAction,
        params Action[] decoderResetActions)
    {
        return Seek(0, seekAction, decoderResetActions);
    }
}

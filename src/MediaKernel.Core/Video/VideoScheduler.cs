using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MediaKernel.Core.Video
{
    /// <summary>
    /// Video frame scheduling result indicating what the renderer should do.
    /// </summary>
    public enum FrameScheduleResult
    {
        /// <summary>Frame should be displayed now.</summary>
        Display,
        
        /// <summary>Frame is too early, wait before displaying.</summary>
        Wait,
        
        /// <summary>Frame is too late, should be dropped.</summary>
        Drop,
        
        /// <summary>No frame available.</summary>
        Empty
    }

    /// <summary>
    /// Statistics about frame scheduling performance.
    /// </summary>
    public struct FrameScheduleStatistics
    {
        /// <summary>Number of frames displayed on time.</summary>
        public long FramesDisplayed;
        
        /// <summary>Number of frames dropped due to lateness.</summary>
        public long FramesDropped;
        
        /// <summary>Number of times we waited for an early frame.</summary>
        public long FramesWaited;
        
        /// <summary>Average lateness in microseconds for late frames.</summary>
        public long AverageLatenessMicroseconds;
        
        /// <summary>Maximum lateness observed in microseconds.</summary>
        public long MaxLatenessMicroseconds;

        /// <summary>Percentage of frames that were dropped.</summary>
        public readonly float DropRate => 
            FramesDisplayed + FramesDropped > 0 
                ? (float)FramesDropped / (FramesDisplayed + FramesDropped) * 100 
                : 0;
    }

    /// <summary>
    /// Scheduled frame with timing information.
    /// </summary>
    public readonly struct ScheduledFrame
    {
        /// <summary>The decoded frame data.</summary>
        public readonly nint FrameHandle;
        
        /// <summary>Presentation timestamp in stream time (microseconds).</summary>
        public readonly long PresentationTimestampMicroseconds;
        
        /// <summary>Frame duration in microseconds (1/framerate).</summary>
        public readonly long DurationMicroseconds;
        
        /// <summary>Frame width in pixels.</summary>
        public readonly int Width;
        
        /// <summary>Frame height in pixels.</summary>
        public readonly int Height;
        
        public ScheduledFrame(
            nint frameHandle,
            long presentationTimestampMicroseconds,
            long durationMicroseconds,
            int width,
            int height)
        {
            FrameHandle = frameHandle;
            PresentationTimestampMicroseconds = presentationTimestampMicroseconds;
            DurationMicroseconds = durationMicroseconds;
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// High-precision video frame scheduler with MasterClock integration.
    /// 
    /// Determines when decoded frames should be displayed based on their PTS
    /// and the master clock. Implements VLC-style timing thresholds:
    /// - Early frames: Wait up to tolerance before displaying
    /// - Late frames: Drop if beyond threshold
    /// 
    /// Uses Stopwatch.GetTimestamp() for sub-millisecond precision.
    /// </summary>
    public sealed class VideoScheduler
    {
        // Default thresholds (in microseconds)
        private const long DefaultLateThresholdMicroseconds = 20_000;      // 20ms - VLC's VOUT_DISPLAY_LATE_THRESHOLD
        private const long DefaultEarlyThresholdMicroseconds = 10_000;     // 10ms - how early we tolerate
        private const long DefaultRenderToleranceMicroseconds = 4_000;     // 4ms - VLC's VOUT_MWAIT_TOLERANCE
        private const long DefaultRedisplayDelayMicroseconds = 80_000;     // 80ms - VLC's VOUT_REDISPLAY_DELAY
        
        // Pre-calculated conversion factor
        private static readonly double MicrosecondsPerTick = 1_000_000.0 / Stopwatch.Frequency;
        
        // External master clock (if provided)
        private MasterClock? masterClock;
        
        // Internal clock reference (used when no MasterClock is provided)
        private long streamTimeAtReferenceMicroseconds;
        private long referenceSystemTimeTicks;
        private double playbackRate = 1.0;
        
        // Thresholds (can be adjusted per-stream based on framerate)
        private long lateThresholdMicroseconds;
        private long earlyThresholdMicroseconds;
        private long renderToleranceMicroseconds;
        
        // Statistics
        private FrameScheduleStatistics statistics;
        private long totalLatenessMicroseconds;
        
        // Synchronization
        private readonly object syncLock = new();
        private volatile bool isPaused;
        
        /// <summary>
        /// Gets whether the scheduler is using an external MasterClock.
        /// </summary>
        public bool IsUsingMasterClock => masterClock != null;
        
        /// <summary>
        /// Gets the late threshold in microseconds.
        /// </summary>
        public long LateThresholdMicroseconds => lateThresholdMicroseconds;
        
        /// <summary>
        /// Gets the early threshold in microseconds.
        /// </summary>
        public long EarlyThresholdMicroseconds => earlyThresholdMicroseconds;

        /// <summary>
        /// Creates a new video scheduler with default thresholds.
        /// </summary>
        public VideoScheduler()
        {
            lateThresholdMicroseconds = DefaultLateThresholdMicroseconds;
            earlyThresholdMicroseconds = DefaultEarlyThresholdMicroseconds;
            renderToleranceMicroseconds = DefaultRenderToleranceMicroseconds;
            
            Reset();
        }

        /// <summary>
        /// Creates a new video scheduler synchronized with a MasterClock.
        /// </summary>
        /// <param name="masterClock">The master clock to synchronize with.</param>
        public VideoScheduler(MasterClock masterClock) : this()
        {
            this.masterClock = masterClock;
        }

        /// <summary>
        /// Sets the MasterClock for synchronization.
        /// When set, the scheduler will use the MasterClock's stream time.
        /// </summary>
        /// <param name="clock">The master clock to use, or null to use internal timing.</param>
        public void SetMasterClock(MasterClock? clock)
        {
            lock (syncLock)
            {
                if (clock != null)
                {
                    masterClock = clock;
                }
                else
                {
                    // Capture current stream time before disconnecting
                    long currentTime = GetCurrentStreamTimeMicroseconds();
                    masterClock = null;
                    streamTimeAtReferenceMicroseconds = currentTime;
                    referenceSystemTimeTicks = Stopwatch.GetTimestamp();
                }
            }
        }

        /// <summary>
        /// Resets the scheduler to initial state.
        /// </summary>
        public void Reset()
        {
            lock (syncLock)
            {
                streamTimeAtReferenceMicroseconds = 0;
                referenceSystemTimeTicks = Stopwatch.GetTimestamp();
                playbackRate = 1.0;
                statistics = default;
                totalLatenessMicroseconds = 0;
                isPaused = false;
            }
        }

        /// <summary>
        /// Sets the clock reference point.
        /// Only used when not connected to a MasterClock.
        /// </summary>
        /// <param name="streamTimeMicroseconds">Current stream time in microseconds.</param>
        public void SetClockReference(long streamTimeMicroseconds)
        {
            lock (syncLock)
            {
                // If using MasterClock, sync it instead
                if (masterClock != null)
                {
                    // Convert microseconds to TimeSpan ticks (100ns units)
                    long streamTimeTicks = streamTimeMicroseconds * 10;
                    masterClock.SetReference(streamTimeTicks);
                }
                else
                {
                    streamTimeAtReferenceMicroseconds = streamTimeMicroseconds;
                    referenceSystemTimeTicks = Stopwatch.GetTimestamp();
                }
            }
        }

        /// <summary>
        /// Sets the playback rate (1.0 = normal, 2.0 = 2x speed).
        /// </summary>
        public void SetPlaybackRate(double rate)
        {
            if (rate <= 0)
                throw new ArgumentOutOfRangeException(nameof(rate), "Rate must be positive");
            
            lock (syncLock)
            {
                if (masterClock != null)
                {
                    masterClock.AdjustRate(rate);
                }
                else
                {
                    // Update reference to current position before changing rate
                    long currentStreamTime = GetCurrentStreamTimeMicroseconds();
                    streamTimeAtReferenceMicroseconds = currentStreamTime;
                    referenceSystemTimeTicks = Stopwatch.GetTimestamp();
                    playbackRate = rate;
                }
            }
        }

        /// <summary>
        /// Sets the late frame threshold based on framerate.
        /// </summary>
        /// <param name="framerateNumerator">Frame rate numerator (e.g., 24000 for 23.976fps).</param>
        /// <param name="framerateDenominator">Frame rate denominator (e.g., 1001 for 23.976fps).</param>
        public void SetFramerate(int framerateNumerator, int framerateDenominator)
        {
            if (framerateNumerator <= 0 || framerateDenominator <= 0)
                return;
            
            // Use one frame duration as late threshold (VLC behavior)
            long frameDurationMicroseconds = (long)(1_000_000.0 * framerateDenominator / framerateNumerator);
            lateThresholdMicroseconds = Math.Max(frameDurationMicroseconds, DefaultLateThresholdMicroseconds);
        }

        /// <summary>
        /// Pauses the scheduler (freezes stream time).
        /// </summary>
        public void Pause()
        {
            lock (syncLock)
            {
                if (masterClock != null)
                {
                    masterClock.Pause();
                    isPaused = masterClock.IsPaused;
                }
                else if (!isPaused)
                {
                    // Capture current stream time before pausing
                    streamTimeAtReferenceMicroseconds = GetCurrentStreamTimeMicroseconds();
                    isPaused = true;
                }
            }
        }

        /// <summary>
        /// Resumes the scheduler after a pause.
        /// </summary>
        public void Resume()
        {
            lock (syncLock)
            {
                if (masterClock != null)
                {
                    masterClock.Resume();
                    isPaused = masterClock.IsPaused;
                }
                else if (isPaused)
                {
                    referenceSystemTimeTicks = Stopwatch.GetTimestamp();
                    isPaused = false;
                }
            }
        }

        /// <summary>
        /// Gets the current stream time in microseconds.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long GetCurrentStreamTimeMicroseconds()
        {
            // Use MasterClock if available
            if (masterClock != null)
            {
                // Convert from TimeSpan ticks (100ns) to microseconds
                return masterClock.GetCurrentStreamTime() / 10;
            }
            
            if (isPaused)
                return streamTimeAtReferenceMicroseconds;
            
            long currentTicks = Stopwatch.GetTimestamp();
            long elapsedTicks = currentTicks - referenceSystemTimeTicks;
            double elapsedMicroseconds = elapsedTicks * MicrosecondsPerTick * playbackRate;
            return streamTimeAtReferenceMicroseconds + (long)elapsedMicroseconds;
        }

        /// <summary>
        /// Evaluates when a frame should be displayed.
        /// </summary>
        /// <param name="frame">The frame to schedule.</param>
        /// <returns>Scheduling result indicating action to take.</returns>
        public FrameScheduleResult EvaluateFrame(in ScheduledFrame frame)
        {
            if (frame.FrameHandle == 0)
                return FrameScheduleResult.Empty;
            
            long currentStreamTime = GetCurrentStreamTimeMicroseconds();
            long framePts = frame.PresentationTimestampMicroseconds;
            long timeDelta = framePts - currentStreamTime;
            
            // Frame is early - need to wait
            if (timeDelta > earlyThresholdMicroseconds)
            {
                return FrameScheduleResult.Wait;
            }
            
            // Frame is late beyond threshold - drop it
            if (timeDelta < -lateThresholdMicroseconds)
            {
                long lateness = -timeDelta;
                RecordDroppedFrame(lateness);
                return FrameScheduleResult.Drop;
            }
            
            // Frame is within acceptable range - display it
            return FrameScheduleResult.Display;
        }

        /// <summary>
        /// Calculates how long to wait before displaying an early frame.
        /// </summary>
        /// <param name="frame">The frame to schedule.</param>
        /// <returns>Wait time in microseconds, or 0 if frame should display now.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long CalculateWaitTimeMicroseconds(in ScheduledFrame frame)
        {
            long currentStreamTime = GetCurrentStreamTimeMicroseconds();
            long timeDelta = frame.PresentationTimestampMicroseconds - currentStreamTime;
            
            if (timeDelta <= renderToleranceMicroseconds)
                return 0;
            
            // Subtract render tolerance to account for processing time
            return timeDelta - renderToleranceMicroseconds;
        }

        /// <summary>
        /// Waits until a frame is ready to display.
        /// </summary>
        /// <param name="frame">The frame to wait for.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if frame is ready, false if cancelled or should be dropped.</returns>
        public bool WaitForFrame(in ScheduledFrame frame, CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long waitMicroseconds = CalculateWaitTimeMicroseconds(in frame);
                
                if (waitMicroseconds <= 0)
                {
                    RecordDisplayedFrame();
                    return true;
                }
                
                // Use high-precision spin-wait for short waits
                if (waitMicroseconds < 1000) // Less than 1ms
                {
                    SpinWaitMicroseconds(waitMicroseconds);
                }
                else
                {
                    // Use Thread.Sleep for longer waits (less CPU-intensive)
                    int sleepMs = (int)Math.Max(1, (waitMicroseconds - 1000) / 1000);
                    Thread.Sleep(sleepMs);
                }
                
                // Re-evaluate in case of pause/seek
                var result = EvaluateFrame(in frame);
                if (result == FrameScheduleResult.Drop)
                    return false;
            }
            
            return false;
        }

        /// <summary>
        /// Records that a frame was displayed successfully.
        /// </summary>
        public void RecordDisplayedFrame()
        {
            Interlocked.Increment(ref statistics.FramesDisplayed);
        }

        /// <summary>
        /// Records that a frame was dropped due to lateness.
        /// </summary>
        private void RecordDroppedFrame(long latenessMicroseconds)
        {
            Interlocked.Increment(ref statistics.FramesDropped);
            Interlocked.Add(ref totalLatenessMicroseconds, latenessMicroseconds);
            
            // Update max lateness (non-atomic but acceptable for statistics)
            if (latenessMicroseconds > statistics.MaxLatenessMicroseconds)
                statistics.MaxLatenessMicroseconds = latenessMicroseconds;
        }

        /// <summary>
        /// Gets current scheduling statistics.
        /// </summary>
        public FrameScheduleStatistics GetStatistics()
        {
            var stats = statistics;
            if (stats.FramesDropped > 0)
            {
                stats.AverageLatenessMicroseconds = totalLatenessMicroseconds / stats.FramesDropped;
            }
            return stats;
        }

        /// <summary>
        /// High-precision spin wait for short durations.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SpinWaitMicroseconds(long microseconds)
        {
            long targetTicks = Stopwatch.GetTimestamp() + (long)(microseconds / MicrosecondsPerTick);
            
            // Spin until target time
            SpinWait spinner = new();
            while (Stopwatch.GetTimestamp() < targetTicks)
            {
                spinner.SpinOnce(-1);
            }
        }

        /// <summary>
        /// Converts system ticks to stream time based on current clock reference.
        /// </summary>
        /// <param name="systemTicks">System timestamp from Stopwatch.GetTimestamp().</param>
        /// <returns>Stream time in microseconds.</returns>
        public long SystemTicksToStreamTime(long systemTicks)
        {
            // When using MasterClock, we don't need this conversion
            // as the MasterClock handles it internally
            if (masterClock != null || isPaused)
                return GetCurrentStreamTimeMicroseconds();
            
            long elapsedTicks = systemTicks - referenceSystemTimeTicks;
            double elapsedMicroseconds = elapsedTicks * MicrosecondsPerTick * playbackRate;
            return streamTimeAtReferenceMicroseconds + (long)elapsedMicroseconds;
        }

        /// <summary>
        /// Converts stream time to system ticks.
        /// </summary>
        /// <param name="streamTimeMicroseconds">Stream time in microseconds.</param>
        /// <returns>System timestamp that corresponds to this stream time.</returns>
        public long StreamTimeToSystemTicks(long streamTimeMicroseconds)
        {
            double effectiveRate = masterClock?.PlaybackRate ?? playbackRate;
            
            if (isPaused || effectiveRate == 0)
                return referenceSystemTimeTicks;
            
            long deltaStreamMicroseconds = streamTimeMicroseconds - streamTimeAtReferenceMicroseconds;
            double deltaTicks = deltaStreamMicroseconds / (MicrosecondsPerTick * effectiveRate);
            return referenceSystemTimeTicks + (long)deltaTicks;
        }
    }
}

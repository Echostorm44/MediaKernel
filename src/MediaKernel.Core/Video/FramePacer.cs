using System.Diagnostics;
using System.Threading.Channels;

namespace MediaKernel.Core.Video;

/// <summary>
/// Decides when each video frame is due, and whether one has fallen far enough behind to be worth
/// skipping.
/// </summary>
/// <remarks>
/// The render loop used to sleep once for up to 50 ms and then present the frame regardless, with
/// no way to skip a late one: a decoder that could not keep up degraded into smooth slow motion
/// rather than dropping frames. <see cref="VideoScheduler"/> already implemented the wait/drop
/// policy and was covered by tests, but nothing in the player called it. This is the seam that
/// puts it on the real path, and the same seam the headless bench mode runs, so what the bench
/// measures is what playback does.
/// </remarks>
public sealed class FramePacer
{
    private readonly VideoScheduler scheduler;
    private readonly long frameDurationMicroseconds;

    public FramePacer(MasterClock clock, double framesPerSecond)
    {
        scheduler = new VideoScheduler(clock);
        frameDurationMicroseconds = framesPerSecond > 0
            ? (long)(1_000_000.0 / framesPerSecond)
            : 0;
    }

    /// <summary>Frames presented, dropped and how late they ran.</summary>
    public FrameScheduleStatistics Statistics => scheduler.GetStatistics();

    /// <summary>
    /// Blocks until <paramref name="presentationTicks"/> is due, and reports whether the frame
    /// should still be presented. False means it is late enough to skip.
    /// </summary>
    /// <param name="presentationTicks">Frame PTS in <see cref="TimeSpan"/> ticks.</param>
    /// <param name="ct">Cancels the wait; a cancelled wait does not present.</param>
    public bool WaitUntilDue(long presentationTicks, CancellationToken ct)
    {
        // The scheduler works in microseconds; the pipeline and MasterClock in 100ns ticks. The
        // frame handle only has to be non-zero - it marks the frame as present, and nothing here
        // dereferences it.
        var scheduled = new ScheduledFrame(
            frameHandle: 1,
            presentationTimestampMicroseconds: presentationTicks / (TimeSpan.TicksPerMillisecond / 1000),
            durationMicroseconds: frameDurationMicroseconds,
            width: 0,
            height: 0);

        // VideoScheduler.WaitForFrame already sleeps the bulk and spins the last millisecond, and
        // re-checks for lateness after each nap. Sleeping the whole wait in one call instead would
        // routinely overshoot: Thread.Sleep's granularity on Windows is about 15 ms by default,
        // which is half a frame at 30 fps and enough to push a frame past the drop threshold.
        return scheduler.WaitForFrame(in scheduled, ct);
    }
}

/// <summary>
/// What a headless run measured. Reported by <c>--bench</c> and asserted by the playback tests.
/// </summary>
public readonly record struct BenchResult(
    long FramesPresented,
    long FramesDropped,
    double ElapsedSeconds,
    long MaxLatenessMicroseconds,
    long AudioSamples,
    int AudioSampleRate)
{
    /// <summary>Frames actually presented per second of wall time.</summary>
    public double PresentedFramesPerSecond =>
        ElapsedSeconds > 0 ? FramesPresented / ElapsedSeconds : 0;

    /// <summary>Frames presented plus dropped, per second of wall time.</summary>
    public double ScheduledFramesPerSecond =>
        ElapsedSeconds > 0 ? (FramesPresented + FramesDropped) / ElapsedSeconds : 0;

    /// <summary>Seconds of audio decoded, for comparing against wall time.</summary>
    public double AudioSeconds =>
        AudioSampleRate > 0 ? (double)AudioSamples / AudioSampleRate : 0;
}

/// <summary>
/// Runs the real scheduling path with no window, so achieved frame rate, drop count and lateness
/// are measurable without a display. Without this there was no way to tell a rendering problem
/// from a scheduling one, or to notice playback regressing to slow motion.
/// </summary>
public static class PlaybackBench
{
    public static async Task<BenchResult> RunAsync(
        DecodePipeline pipeline,
        bool hasAudio,
        double framesPerSecond,
        TimeSpan limit,
        CancellationTokenSource engineCts,
        CancellationToken ct)
    {
        var clock = new MasterClock();
        var pacer = new FramePacer(clock, framesPerSecond);

        long audioSamples = 0;
        int audioSampleRate = 0;

        // Drives the clock exactly as the SDL path does, so the timing under test is the real one.
        Task? audioTask = hasAudio
            ? Task.Run(() =>
            {
                bool referenced = false;
                foreach (var frame in pipeline.AudioFrames.ReadAllAsync(engineCts.Token).ToBlockingEnumerable(engineCts.Token))
                {
                    if (!referenced)
                    {
                        clock.SetReference(frame.Pts);
                        referenced = true;
                    }

                    if (audioSampleRate == 0)
                        audioSampleRate = frame.SampleRate;
                    audioSamples += frame.SampleCount;
                    frame.Dispose();
                }
            }, engineCts.Token)
            : null;

        var started = Stopwatch.StartNew();
        long presented = 0;
        bool firstFrame = true;

        while (!ct.IsCancellationRequested && started.Elapsed < limit)
        {
            if (pipeline.VideoFrames.TryRead(out var frame))
            {
                try
                {
                    if (firstFrame)
                    {
                        if (!hasAudio)
                            clock.SetReference(frame.Pts);
                        firstFrame = false;
                    }

                    if (pacer.WaitUntilDue(frame.Pts, ct))
                        presented++;
                }
                finally
                {
                    frame.Dispose();
                }
            }
            else if (pipeline.State is DecodePipelineState.Completed or DecodePipelineState.Faulted)
            {
                break;
            }
            else
            {
                await Task.Delay(2, ct).ConfigureAwait(false);
            }
        }

        started.Stop();
        await engineCts.CancelAsync().ConfigureAwait(false);
        if (audioTask != null)
        {
            try { await audioTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        var statistics = pacer.Statistics;
        return new BenchResult(
            FramesPresented: presented,
            FramesDropped: statistics.FramesDropped,
            ElapsedSeconds: started.Elapsed.TotalSeconds,
            MaxLatenessMicroseconds: statistics.MaxLatenessMicroseconds,
            AudioSamples: audioSamples,
            AudioSampleRate: audioSampleRate);
    }
}

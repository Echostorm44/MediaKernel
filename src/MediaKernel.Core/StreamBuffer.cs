using System.IO.Pipelines;

namespace MediaKernel.Core;

/// <summary>
/// High-performance stream buffer using System.IO.Pipelines for zero-copy streaming.
/// Implements high/low watermark logic to prevent playback stalls during network jitter.
/// </summary>
/// <remarks>
/// The buffer size must account for bitrate and expected jitter:
/// BufferSize = Bitrate × MaxJitterSeconds × SafetyMargin
/// For 20 Mbps with 2-second outage tolerance: ~5MB minimum
/// </remarks>
public sealed class StreamBuffer : IDisposable
{
    private readonly Pipe bufferPipe;
    private readonly long highWatermark;
    private readonly long lowWatermark;
    private long currentBufferSize;
    private bool isBuffering = true;
    private bool isDisposed;

    /// <summary>
    /// Raised when the buffer reaches the high watermark and playback can begin/resume.
    /// </summary>
    public event Action? BufferReady;

    /// <summary>
    /// Raised when the buffer drops below the low watermark and playback should pause.
    /// </summary>
    public event Action? BufferDepleted;

    /// <summary>
    /// Creates a new StreamBuffer with the specified capacity.
    /// </summary>
    /// <param name="bufferCapacity">Maximum buffer size in bytes before backpressure kicks in.</param>
    /// <param name="lowWatermarkRatio">Ratio of capacity at which buffering restarts (default 0.1).</param>
    public StreamBuffer(long bufferCapacity, double lowWatermarkRatio = 0.1)
    {
        if (bufferCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferCapacity));

        highWatermark = bufferCapacity;
        lowWatermark = (long)(bufferCapacity * lowWatermarkRatio);

        // Configure the pipe for high-performance media streaming
        var options = new PipeOptions(
            pauseWriterThreshold: bufferCapacity,
            resumeWriterThreshold: bufferCapacity / 2,
            useSynchronizationContext: false);

        bufferPipe = new Pipe(options);
    }

    /// <summary>
    /// Gets whether the buffer is currently in buffering mode (not yet ready for playback).
    /// </summary>
    public bool IsBuffering => isBuffering;

    /// <summary>
    /// Gets the current amount of data in the buffer.
    /// </summary>
    public long CurrentSize => Interlocked.Read(ref currentBufferSize);

    /// <summary>
    /// Gets the reader for consuming buffered data (used by demuxer).
    /// </summary>
    public PipeReader Reader => bufferPipe.Reader;

    /// <summary>
    /// Starts fetching data from the source stream and buffering it.
    /// This method runs until the source is exhausted or cancellation is requested.
    /// </summary>
    /// <param name="source">The source stream (file, network, etc.).</param>
    /// <param name="cancellation">Cancellation token for stopping the fetch.</param>
    public async Task StartFetchingAsync(Stream source, CancellationToken cancellation = default)
    {
        var writer = bufferPipe.Writer;

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                // Request memory directly from the pipe's internal pool
                Memory<byte> memory = writer.GetMemory(8192);
                int bytesRead = await source.ReadAsync(memory, cancellation);

                if (bytesRead == 0)
                    break; // End of stream

                // Advance the pipe's write cursor
                writer.Advance(bytesRead);

                // Track buffer size for watermark logic
                Interlocked.Add(ref currentBufferSize, bytesRead);
                CheckWatermarks();

                // Flush makes the data available to the Reader
                FlushResult result = await writer.FlushAsync(cancellation);
                if (result.IsCompleted)
                    break;
            }
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    /// <summary>
    /// Notifies the buffer that data has been consumed by the reader.
    /// Call this after processing data from the Reader.
    /// </summary>
    /// <param name="bytesConsumed">Number of bytes consumed.</param>
    public void NotifyConsumed(long bytesConsumed)
    {
        Interlocked.Add(ref currentBufferSize, -bytesConsumed);
        CheckWatermarks();
    }

    /// <summary>
    /// Checks watermark thresholds and raises appropriate events.
    /// </summary>
    private void CheckWatermarks()
    {
        long size = Interlocked.Read(ref currentBufferSize);

        if (isBuffering && size >= highWatermark)
        {
            isBuffering = false;
            BufferReady?.Invoke();
        }
        else if (!isBuffering && size < lowWatermark)
        {
            isBuffering = true;
            BufferDepleted?.Invoke();
        }
    }

    /// <summary>
    /// Forces the buffer into ready state (useful for local file playback
    /// where we don't need to wait for network buffering).
    /// </summary>
    public void ForceReady()
    {
        isBuffering = false;
        BufferReady?.Invoke();
    }

    /// <summary>
    /// Signals that the input is complete (end of file/stream).
    /// </summary>
    public void Complete()
    {
        bufferPipe.Writer.Complete();
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        isDisposed = true;
        bufferPipe.Reader.Complete();
        bufferPipe.Writer.Complete();
    }
}

/// <summary>
/// Monitors buffer health and provides feedback for adaptive bitrate decisions.
/// </summary>
public sealed class BufferMonitor
{
    private readonly StreamBuffer buffer;
    private readonly long targetSize;
    private DateTime lastCheckTime;
    private long lastSize;

    public BufferMonitor(StreamBuffer buffer, long targetSize)
    {
        this.buffer = buffer;
        this.targetSize = targetSize;
        lastCheckTime = DateTime.UtcNow;
        lastSize = buffer.CurrentSize;
    }

    /// <summary>
    /// Gets the current buffer health as a ratio (0.0 = empty, 1.0 = target reached).
    /// </summary>
    public double HealthRatio => Math.Min(1.0, (double)buffer.CurrentSize / targetSize);

    /// <summary>
    /// Calculates the fill rate in bytes per second.
    /// Positive = filling, Negative = draining.
    /// </summary>
    public double CalculateFillRate()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - lastCheckTime).TotalSeconds;
        if (elapsed < 0.1)
            return 0;

        long currentSize = buffer.CurrentSize;
        double rate = (currentSize - lastSize) / elapsed;

        lastCheckTime = now;
        lastSize = currentSize;

        return rate;
    }

    /// <summary>
    /// Estimates time until buffer is depleted at current consumption rate.
    /// Returns TimeSpan.MaxValue if buffer is filling.
    /// </summary>
    public TimeSpan EstimateTimeToDepletion()
    {
        double fillRate = CalculateFillRate();
        if (fillRate >= 0)
            return TimeSpan.MaxValue; // Buffer is filling or stable

        double secondsRemaining = buffer.CurrentSize / (-fillRate);
        return TimeSpan.FromSeconds(secondsRemaining);
    }

    /// <summary>
    /// Determines if adaptive bitrate should switch to a lower quality.
    /// </summary>
    public bool ShouldDowngradeQuality()
    {
        // Switch down if buffer is below 30% and draining
        return HealthRatio < 0.3 && CalculateFillRate() < 0;
    }

    /// <summary>
    /// Determines if adaptive bitrate can switch to a higher quality.
    /// </summary>
    public bool CanUpgradeQuality()
    {
        // Only upgrade if buffer is above 80% and filling
        return HealthRatio > 0.8 && CalculateFillRate() > 0;
    }
}

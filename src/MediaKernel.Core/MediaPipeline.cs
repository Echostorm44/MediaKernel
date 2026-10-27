using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Channels;

namespace MediaKernel.Core;

/// <summary>
/// High-performance media pipeline using System.IO.Pipelines for zero-copy streaming
/// and System.Threading.Channels for backpressure-aware frame delivery.
/// </summary>
/// <remarks>
/// This pipeline implements a "Push-Pull" hybrid architecture:
/// - IO.Pipelines "pushes" raw bytes from the source
/// - Channels "pull" decoded frames to the UI
/// Backpressure ensures the demuxer pauses if the GPU is slow.
/// </remarks>
public sealed class MediaPipeline : IDisposable
{
    private readonly PipeReader inputReader;
    private readonly PipeWriter inputWriter;
    private readonly Channel<VideoFrameHandle> videoFrameChannel;
    private readonly Channel<AudioBufferHandle> audioBufferChannel;
    private readonly CancellationTokenSource cancellationSource;
    private bool isDisposed;

    /// <summary>
    /// Maximum number of decoded video frames to buffer before backpressure kicks in.
    /// </summary>
    private const int MaxVideoFrameBuffer = 16;

    /// <summary>
    /// Maximum number of decoded audio buffers to buffer.
    /// </summary>
    private const int MaxAudioBufferCount = 32;

    public MediaPipeline()
    {
        // Configure the pipe for high-performance media streaming
        var pipeOptions = new PipeOptions(
            pool: MemoryPool<byte>.Shared,
            readerScheduler: PipeScheduler.ThreadPool,
            writerScheduler: PipeScheduler.ThreadPool,
            pauseWriterThreshold: 5 * 1024 * 1024,  // 5MB buffer before backpressure
            resumeWriterThreshold: 2 * 1024 * 1024, // Resume at 2MB
            useSynchronizationContext: false);

        var pipe = new Pipe(pipeOptions);
        inputReader = pipe.Reader;
        inputWriter = pipe.Writer;

        // Bounded channels provide backpressure to prevent memory bloat
        videoFrameChannel = Channel.CreateBounded<VideoFrameHandle>(
            new BoundedChannelOptions(MaxVideoFrameBuffer)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,  // Only Avalonia UI thread reads
                SingleWriter = false  // Multiple decoder threads may write
            });

        audioBufferChannel = Channel.CreateBounded<AudioBufferHandle>(
            new BoundedChannelOptions(MaxAudioBufferCount)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

        cancellationSource = new CancellationTokenSource();
    }

    /// <summary>
    /// Gets the reader for video frames. Used by the video scheduler/renderer.
    /// </summary>
    public ChannelReader<VideoFrameHandle> VideoFrames => videoFrameChannel.Reader;

    /// <summary>
    /// Gets the reader for audio buffers. Used by the audio output.
    /// </summary>
    public ChannelReader<AudioBufferHandle> AudioBuffers => audioBufferChannel.Reader;

    /// <summary>
    /// Gets the writer for pushing raw media data into the pipeline.
    /// </summary>
    public PipeWriter InputWriter => inputWriter;

    /// <summary>
    /// Gets the reader for consuming raw media data (used by demuxer).
    /// </summary>
    public PipeReader InputReader => inputReader;

    /// <summary>
    /// Gets the cancellation token for cooperative pipeline shutdown.
    /// </summary>
    public CancellationToken CancellationToken => cancellationSource.Token;

    /// <summary>
    /// Writes a decoded video frame to the output channel.
    /// Will block if the channel is full (backpressure).
    /// </summary>
    public async ValueTask WriteVideoFrameAsync(VideoFrameHandle frame, CancellationToken cancellation = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationSource.Token, cancellation);
        await videoFrameChannel.Writer.WriteAsync(frame, linkedCancellation.Token);
    }

    /// <summary>
    /// Writes decoded audio samples to the output channel.
    /// </summary>
    public async ValueTask WriteAudioBufferAsync(AudioBufferHandle buffer, CancellationToken cancellation = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationSource.Token, cancellation);
        await audioBufferChannel.Writer.WriteAsync(buffer, linkedCancellation.Token);
    }

    /// <summary>
    /// Flushes the pipeline during seek operations.
    /// Clears all pending frames and audio buffers.
    /// </summary>
    public void Flush()
    {
        // Drain video frames
        while (videoFrameChannel.Reader.TryRead(out var frame))
        {
            frame.Dispose();
        }

        // Drain audio buffers
        while (audioBufferChannel.Reader.TryRead(out var buffer))
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Signals that input is complete (end of file/stream).
    /// </summary>
    public void CompleteInput()
    {
        inputWriter.Complete();
    }

    /// <summary>
    /// Signals that video decoding is complete.
    /// </summary>
    public void CompleteVideoOutput()
    {
        videoFrameChannel.Writer.Complete();
    }

    /// <summary>
    /// Signals that audio decoding is complete.
    /// </summary>
    public void CompleteAudioOutput()
    {
        audioBufferChannel.Writer.Complete();
    }

    /// <summary>
    /// Stops the pipeline and releases resources.
    /// </summary>
    public void Stop()
    {
        cancellationSource.Cancel();
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        isDisposed = true;
        cancellationSource.Cancel();
        cancellationSource.Dispose();
        Flush();
    }
}

/// <summary>
/// A handle to a video frame that manages lifetime and allows pooling.
/// </summary>
public readonly struct VideoFrameHandle : IDisposable
{
    private readonly VideoFrame frame;
    private readonly Action<VideoFrame>? returnToPool;

    public VideoFrameHandle(VideoFrame frame, Action<VideoFrame>? returnToPool = null)
    {
        this.frame = frame;
        this.returnToPool = returnToPool;
    }

    public VideoFrame Frame => frame;

    public void Dispose()
    {
        returnToPool?.Invoke(frame);
    }
}

/// <summary>
/// A handle to an audio buffer that manages lifetime and allows pooling.
/// </summary>
public readonly struct AudioBufferHandle : IDisposable
{
    private readonly AudioBuffer buffer;
    private readonly Action<AudioBuffer>? returnToPool;

    public AudioBufferHandle(AudioBuffer buffer, Action<AudioBuffer>? returnToPool = null)
    {
        this.buffer = buffer;
        this.returnToPool = returnToPool;
    }

    public AudioBuffer Buffer => buffer;

    public void Dispose()
    {
        returnToPool?.Invoke(buffer);
    }
}

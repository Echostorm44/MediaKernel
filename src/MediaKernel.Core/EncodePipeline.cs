// MediaKernel — Encode Pipeline
// Takes raw video/audio frames, encodes them, and writes to a muxer.
// Thread model: caller pushes frames into bounded channels; background task encodes and muxes.

using System;
using System.Buffers;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaKernel.Core.Codecs;
using MediaKernel.Core.Muxers;

namespace MediaKernel.Core;

/// <summary>
/// Configuration for the encode pipeline.
/// </summary>
public sealed class EncodePipelineOptions
{
    /// <summary>Maximum video frames buffered before backpressure.</summary>
    public int MaxVideoFrameBuffer { get; init; } = 16;

    /// <summary>Maximum audio frames buffered before backpressure.</summary>
    public int MaxAudioFrameBuffer { get; init; } = 32;

    /// <summary>Video encoder configuration. Null to skip video encoding.</summary>
    public EncoderConfig? VideoConfig { get; init; }

    /// <summary>Audio encoder configuration. Null to skip audio encoding.</summary>
    public EncoderConfig? AudioConfig { get; init; }

    /// <summary>Muxer output format.</summary>
    public MuxerFormat OutputFormat { get; init; }

    /// <summary>Muxer configuration overrides.</summary>
    public MuxerConfiguration? MuxerConfig { get; init; }
}

/// <summary>
/// Encode pipeline state.
/// </summary>
public enum EncodePipelineState
{
    Created,
    Running,
    Draining,
    Completed,
    Faulted,
    Cancelled,
}

/// <summary>
/// Progress from the encode pipeline.
/// </summary>
public readonly struct EncodeProgress
{
    public long VideoFramesEncoded { get; init; }
    public long AudioFramesEncoded { get; init; }
    public long PacketsWritten { get; init; }
    public long BytesWritten { get; init; }
}

/// <summary>
/// Orchestrates encoding: raw frames → encoder → muxer → output file.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// await using var pipeline = EncodePipeline.Create("output.mp4", options);
/// var runTask = pipeline.RunAsync(ct);
/// pipeline.SubmitVideoFrame(frame);
/// pipeline.SubmitAudioFrame(audio);
/// pipeline.FinishInput();
/// await runTask;
/// </code>
/// </remarks>
public sealed class EncodePipeline : IAsyncDisposable, IDisposable
{
    private readonly Muxer muxer;
    private readonly bool ownsMuxer;
    private readonly EncodePipelineOptions options;

    // Encoders
    private IVideoEncoder? videoEncoder;
    private IAudioEncoder? audioEncoder;

    // Track IDs in the muxer
    private int videoTrackId = -1;
    private int audioTrackId = -1;

    // Input channels
    private readonly Channel<VideoFrame>? videoInputChannel;
    private readonly Channel<AudioFrame>? audioInputChannel;

    // Lifecycle
    private readonly CancellationTokenSource pipelineCts = new();
    private EncodePipelineState state = EncodePipelineState.Created;
    private Exception? faultException;

    // Progress
    private long videoFramesEncoded;
    private long audioFramesEncoded;
    private long packetsWritten;
    private long bytesWritten;

    private EncodePipeline(Muxer muxer, bool ownsMuxer, EncodePipelineOptions options)
    {
        this.muxer = muxer;
        this.ownsMuxer = ownsMuxer;
        this.options = options;

        if (options.VideoConfig != null)
        {
            videoInputChannel = Channel.CreateBounded<VideoFrame>(
                new BoundedChannelOptions(options.MaxVideoFrameBuffer)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = false,
                    SingleReader = true,
                });
        }

        if (options.AudioConfig != null)
        {
            audioInputChannel = Channel.CreateBounded<AudioFrame>(
                new BoundedChannelOptions(options.MaxAudioFrameBuffer)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = false,
                    SingleReader = true,
                });
        }
    }

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>Current pipeline state.</summary>
    public EncodePipelineState State => state;

    /// <summary>Exception if the pipeline faulted.</summary>
    public Exception? FaultException => faultException;

    /// <summary>Event raised periodically with encode progress.</summary>
    public event Action<EncodeProgress>? ProgressChanged;

    /// <summary>Gets the current encode progress snapshot.</summary>
    public EncodeProgress GetProgress() => new()
    {
        VideoFramesEncoded = Volatile.Read(ref videoFramesEncoded),
        AudioFramesEncoded = Volatile.Read(ref audioFramesEncoded),
        PacketsWritten = Volatile.Read(ref packetsWritten),
        BytesWritten = Volatile.Read(ref bytesWritten),
    };

    /// <summary>
    /// Submit a video frame for encoding. Blocks if the buffer is full.
    /// </summary>
    public async ValueTask SubmitVideoFrameAsync(VideoFrame frame, CancellationToken ct = default)
    {
        if (videoInputChannel == null)
            throw new InvalidOperationException("Video encoding not configured");
        await videoInputChannel.Writer.WriteAsync(frame, ct);
    }

    /// <summary>
    /// Submit an audio frame for encoding. Blocks if the buffer is full.
    /// </summary>
    public async ValueTask SubmitAudioFrameAsync(AudioFrame frame, CancellationToken ct = default)
    {
        if (audioInputChannel == null)
            throw new InvalidOperationException("Audio encoding not configured");
        await audioInputChannel.Writer.WriteAsync(frame, ct);
    }

    /// <summary>
    /// Signals that no more input frames will be submitted.
    /// The pipeline will drain remaining frames and complete.
    /// </summary>
    public void FinishInput()
    {
        videoInputChannel?.Writer.TryComplete();
        audioInputChannel?.Writer.TryComplete();
    }

    /// <summary>
    /// Cancels the pipeline.
    /// </summary>
    public void Cancel()
    {
        pipelineCts.Cancel();
    }

    // ── Factory ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an encode pipeline writing to a file.
    /// </summary>
    public static EncodePipeline Create(string outputPath, EncodePipelineOptions options)
    {
        var outputStream = new System.IO.FileStream(
            outputPath, System.IO.FileMode.Create, System.IO.FileAccess.Write,
            System.IO.FileShare.None, 65536);

        var muxerConfig = options.MuxerConfig ?? new MuxerConfiguration { Format = options.OutputFormat };
        var muxer = MuxerFactory.Create(outputStream, muxerConfig);

        var pipeline = new EncodePipeline(muxer, ownsMuxer: true, options);
        pipeline.InitializeEncoders();
        pipeline.RegisterTracks();
        return pipeline;
    }

    /// <summary>
    /// Creates an encode pipeline writing to an existing muxer.
    /// </summary>
    public static EncodePipeline Create(Muxer muxer, EncodePipelineOptions options)
    {
        var pipeline = new EncodePipeline(muxer, ownsMuxer: false, options);
        pipeline.InitializeEncoders();
        pipeline.RegisterTracks();
        return pipeline;
    }

    // ── Run ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the encode loop. Reads frames from input channels, encodes, and writes to muxer.
    /// Returns when all input is consumed or the pipeline is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellation = default)
    {
        if (state != EncodePipelineState.Created)
            throw new InvalidOperationException($"Pipeline cannot be started in state {state}");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(pipelineCts.Token, cancellation);
        state = EncodePipelineState.Running;

        try
        {
            await muxer.WriteHeaderAsync(linked.Token);

            // Run video and audio encode tasks in parallel
            var videoTask = videoInputChannel != null
                ? Task.Run(() => EncodeVideoLoop(linked.Token), linked.Token)
                : Task.CompletedTask;

            var audioTask = audioInputChannel != null
                ? Task.Run(() => EncodeAudioLoop(linked.Token), linked.Token)
                : Task.CompletedTask;

            await Task.WhenAll(videoTask, audioTask).ConfigureAwait(false);

            state = EncodePipelineState.Draining;

            // Flush encoders
            await FlushEncoders(linked.Token);

            await muxer.WriteTrailerAsync(linked.Token);
            state = EncodePipelineState.Completed;
        }
        catch (OperationCanceledException)
        {
            state = EncodePipelineState.Cancelled;
        }
        catch (Exception ex)
        {
            faultException = ex;
            state = EncodePipelineState.Faulted;
            throw;
        }
        finally
        {
            ProgressChanged?.Invoke(GetProgress());
        }
    }

    // ── Encode loops ────────────────────────────────────────────────────

    private async Task EncodeVideoLoop(CancellationToken ct)
    {
        int progressCounter = 0;

        await foreach (var frame in videoInputChannel!.Reader.ReadAllAsync(ct))
        {
            try
            {
                var packet = videoEncoder!.Encode(in frame);
                if (packet.HasValue)
                {
                    await WriteEncodedPacket(videoTrackId, packet.Value, ct);
                }
            }
            finally
            {
                // Dispose the frame to return its buffer to the pool
                var disposable = frame;
                disposable.Dispose();
            }

            Interlocked.Increment(ref videoFramesEncoded);

            if (++progressCounter >= 50)
            {
                progressCounter = 0;
                ProgressChanged?.Invoke(GetProgress());
            }
        }
    }

    private async Task EncodeAudioLoop(CancellationToken ct)
    {
        int progressCounter = 0;

        await foreach (var frame in audioInputChannel!.Reader.ReadAllAsync(ct))
        {
            try
            {
                var packet = audioEncoder!.Encode(in frame);
                if (packet.HasValue)
                {
                    await WriteEncodedPacket(audioTrackId, packet.Value, ct);
                }
            }
            finally
            {
                var disposable = frame;
                disposable.Dispose();
            }

            Interlocked.Increment(ref audioFramesEncoded);

            if (++progressCounter >= 50)
            {
                progressCounter = 0;
                ProgressChanged?.Invoke(GetProgress());
            }
        }
    }

    private async ValueTask WriteEncodedPacket(int trackId, EncodedPacket packet, CancellationToken ct)
    {
        var sample = MuxerSample.Create(
            trackId,
            packet.Data,
            packet.PresentationTimestampTicks,
            packet.IsKeyframe,
            packet.DurationTicks,
            packet.DecodeTimestampTicks);

        await muxer.WriteSampleAsync(sample, ct);

        Interlocked.Increment(ref packetsWritten);
        Interlocked.Add(ref bytesWritten, packet.Data.Length);
    }

    private async Task FlushEncoders(CancellationToken ct)
    {
        // Flush video encoder
        if (videoEncoder != null)
        {
            videoEncoder.Flush();
            // Drain remaining packets with empty frames
            var emptyFrame = default(VideoFrame);
            for (int i = 0; i < 256; i++)
            {
                var packet = videoEncoder.Encode(in emptyFrame);
                if (!packet.HasValue) break;
                await WriteEncodedPacket(videoTrackId, packet.Value, ct);
            }
        }

        // Flush audio encoder
        if (audioEncoder != null)
        {
            audioEncoder.Flush();
            var emptyFrame = default(AudioFrame);
            for (int i = 0; i < 256; i++)
            {
                var packet = audioEncoder.Encode(in emptyFrame);
                if (!packet.HasValue) break;
                await WriteEncodedPacket(audioTrackId, packet.Value, ct);
            }
        }
    }

    // ── Initialization ──────────────────────────────────────────────────

    private void InitializeEncoders()
    {
        if (options.VideoConfig != null)
        {
            videoEncoder = CreateVideoEncoder(options.VideoConfig);
        }

        if (options.AudioConfig != null)
        {
            audioEncoder = CreateAudioEncoder(options.AudioConfig);
        }
    }

    private void RegisterTracks()
    {
        if (videoEncoder != null)
        {
            var codecPrivate = videoEncoder.GetCodecPrivateData();
            videoTrackId = muxer.AddTrack(new MuxerTrackInfo
            {
                TrackType = MuxerTrackType.Video,
                Codec = options.VideoConfig!.CodecId,
                Width = videoEncoder.Width,
                Height = videoEncoder.Height,
                FrameRate = options.VideoConfig.FrameRate.Den > 0
                    ? (double)options.VideoConfig.FrameRate.Num / options.VideoConfig.FrameRate.Den
                    : 0,
                CodecPrivateData = codecPrivate.IsEmpty ? null : codecPrivate.ToArray(),
            });
        }

        if (audioEncoder != null)
        {
            var codecPrivate = audioEncoder.GetCodecPrivateData();
            audioTrackId = muxer.AddTrack(new MuxerTrackInfo
            {
                TrackType = MuxerTrackType.Audio,
                Codec = options.AudioConfig!.CodecId,
                SampleRate = audioEncoder.SampleRate,
                ChannelCount = audioEncoder.Channels,
                CodecPrivateData = codecPrivate.IsEmpty ? null : codecPrivate.ToArray(),
            });
        }
    }

    private static IVideoEncoder CreateVideoEncoder(EncoderConfig config)
    {
        // Video encoders will be implemented in future units
        // For now, try the codec registry
        var decoder = CodecRegistry.CreateDecoder(config.CodecId);
        if (decoder is IVideoEncoder encoder)
        {
            if (encoder.Initialize(config))
                return encoder;
            encoder.Dispose();
        }
        decoder?.Dispose();

        throw new NotSupportedException($"No video encoder available for codec '{config.CodecId}'");
    }

    private static IAudioEncoder CreateAudioEncoder(EncoderConfig config)
    {
        var decoder = CodecRegistry.CreateDecoder(config.CodecId);
        if (decoder is IAudioEncoder encoder)
        {
            if (encoder.Initialize(config))
                return encoder;
            encoder.Dispose();
        }
        decoder?.Dispose();

        throw new NotSupportedException($"No audio encoder available for codec '{config.CodecId}'");
    }

    // ── Disposal ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        pipelineCts.Cancel();
        pipelineCts.Dispose();
        videoEncoder?.Dispose();
        audioEncoder?.Dispose();

        // Drain input channels
        if (videoInputChannel != null)
            while (videoInputChannel.Reader.TryRead(out var frame)) frame.Dispose();
        if (audioInputChannel != null)
            while (audioInputChannel.Reader.TryRead(out var frame)) frame.Dispose();

        if (ownsMuxer) muxer.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

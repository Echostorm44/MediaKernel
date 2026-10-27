// MediaKernel — Decode Pipeline
// Connects demuxers to decoders, producing decoded frames via bounded channels.
// Thread model: single background task runs demux + decode loop.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaKernel.Core.Codecs;
using MediaKernel.Core.Codecs.H264;
using MediaKernel.Core.Codecs.H265;
using MediaKernel.Core.Demuxers;
using MediaKernel.Core.Input;

namespace MediaKernel.Core;

// ─── Pipeline Configuration ────────────────────────────────────────────────

/// <summary>
/// Configuration for the decode pipeline.
/// </summary>
public sealed class DecodePipelineOptions
{
    /// <summary>Maximum decoded video frames buffered before backpressure kicks in.</summary>
    public int MaxVideoFrameBuffer { get; init; } = 16;

    /// <summary>Maximum decoded audio frames buffered.</summary>
    public int MaxAudioFrameBuffer { get; init; } = 32;

    /// <summary>Maximum size of the demuxer read buffer in bytes.</summary>
    public int DemuxerReadBufferSize { get; init; } = 4 * 1024 * 1024;

    /// <summary>Track indices to decode. Null = decode all video + first audio.</summary>
    public int[]? SelectedTrackIndices { get; init; }

    /// <summary>
    /// Worker threads for in-frame filter stages that can be parallelised. Zero, the default,
    /// keeps every stage on the decode thread.
    /// </summary>
    /// <remarks>
    /// Only HEVC's SAO pass honours this today. It is opt-in because threaded output has to be
    /// shown byte-identical to serial output before it becomes the default, and because a decoder
    /// is not otherwise safe to drive from more than one thread.
    /// </remarks>
    public int VideoFilterThreads { get; init; }

    /// <summary>Whether to skip the loop filter (deblocking) during decode.</summary>
    public bool SkipLoopFilter { get; init; }

    /// <summary>Start time to seek to before decoding (null = from beginning).</summary>
    /// <remarks>
    /// This is an <em>accurate</em> seek, as <c>ffmpeg</c>'s is by default: the demuxer seeks to
    /// the keyframe at or before the position, and the frames between that keyframe and the
    /// position are decoded - they are needed as references - but not emitted. Audio is trimmed
    /// to the sample. See <see cref="DecodeProgress.VideoFramesTrimmed"/>.
    /// </remarks>
    public TimeSpan? StartTime { get; init; }

    /// <summary>Duration to decode (null = until end).</summary>
    /// <remarks>
    /// Measured from the first frame actually emitted, which is <c>trim</c>'s rule and so
    /// differs from <see cref="StartTime"/> plus this by up to one frame interval.
    /// </remarks>
    public TimeSpan? Duration { get; init; }
}

/// <summary>
/// Decode pipeline state.
/// </summary>
public enum DecodePipelineState
{
    Created,
    Running,
    Draining,
    Completed,
    Faulted,
    Cancelled,
}

/// <summary>
/// Progress report from the decode pipeline.
/// </summary>
public readonly struct DecodeProgress
{
    /// <summary>Number of packets read from the demuxer.</summary>
    public long PacketsRead { get; init; }

    /// <summary>Number of video frames decoded.</summary>
    public long VideoFramesDecoded { get; init; }

    /// <summary>Number of audio frames decoded.</summary>
    public long AudioFramesDecoded { get; init; }

    /// <summary>
    /// Video frames decoded and then discarded because they fall outside
    /// [<see cref="DecodePipelineOptions.StartTime"/>, +<see cref="DecodePipelineOptions.Duration"/>).
    /// </summary>
    /// <remarks>
    /// Reported so a test can assert the trim actually fired. A seek to ten seconds that lands
    /// on a keyframe at nine and emits everything from there is wrong in a way that looks
    /// entirely healthy from the outside - it was, for every <c>-ss</c> this pipeline ever
    /// served - and "frames came out" does not distinguish it from correct.
    /// </remarks>
    public long VideoFramesTrimmed { get; init; }

    /// <summary>Audio samples per channel discarded by the same trim.</summary>
    public long AudioSamplesTrimmed { get; init; }

    /// <summary>
    /// Audio samples per channel the CONTAINER said to throw away - an encoder's priming,
    /// which the edit list of an MP4 names and gapless playback exists to remove. Separate from
    /// <see cref="AudioSamplesTrimmed"/>, which is the window the caller asked for.
    /// </summary>
    public long AudioSamplesSkipped { get; init; }

    /// <summary>
    /// Video frames decoded as references and then dropped because the container's edit list
    /// does not present them.
    /// </summary>
    public long VideoFramesDiscarded { get; init; }

    /// <summary>Current decode position in the stream (TimeSpan ticks).</summary>
    public long CurrentPositionTicks { get; init; }

    /// <summary>Total duration of the stream (TimeSpan ticks), or 0 if unknown.</summary>
    public long TotalDurationTicks { get; init; }

    /// <summary>Progress ratio (0.0 - 1.0), or -1 if duration is unknown.</summary>
    public double ProgressRatio =>
        TotalDurationTicks > 0 ? Math.Clamp((double)CurrentPositionTicks / TotalDurationTicks, 0, 1) : -1;
}

// ─── Pipeline ──────────────────────────────────────────────────────────────

/// <summary>
/// Orchestrates the decode pipeline: demuxer → decoder → output channels.
/// Supports MP4, Matroska, and AVI containers with H.264/HEVC video decoders.
/// </summary>
/// <remarks>
/// <para>Usage pattern:</para>
/// <code>
/// await using var pipeline = DecodePipeline.Open("video.mp4");
/// var runTask = pipeline.RunAsync(cancellationToken);
/// await foreach (var frame in pipeline.VideoFrames.ReadAllAsync(cancellationToken))
/// {
///     // process frame
///     frame.Dispose(); // return buffer to pool
/// }
/// await runTask;
/// </code>
/// </remarks>
public sealed class DecodePipeline : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Bytes handed to the format probe. Large enough for MPEG-TS to check the sync byte at four
    /// 188-byte packet boundaries, and for several elementary audio frames to be chained - a
    /// 320 kbps MP3 frame alone is over 1 KB and an AC-3 frame reaches 3840 bytes, so a small
    /// buffer silently fails to identify perfectly ordinary files.
    /// </summary>
    private const int ProbeHeaderSize = 16384;

    /// <summary>Track index reported for the single track of an elementary audio stream.</summary>
    private const int ElementaryAudioTrackIndex = 0;

    /// <summary>
    /// An elementary VIDEO stream is also one track, and also track zero: a file is one or
    /// the other, never both.
    /// </summary>
    private const int ElementaryVideoTrackIndex = 0;

    private readonly Stream fileStream;
    private readonly bool ownsStream;
    private readonly DecodePipelineOptions options;

    // Demuxer (exactly one is non-null)
    private readonly Mp4Demuxer? mp4Demuxer;
    private readonly MatroskaDemuxer? mkvDemuxer;
    private readonly AviDemuxer? aviDemuxer;
    private readonly MpegPsDemuxer? psDemuxer;
    private readonly TsDemuxer? tsDemuxer;
    private readonly FlvDemuxer? flvDemuxer;
    private readonly OggDemuxer? oggDemuxer;
    private readonly AsfDemuxer? asfDemuxer;

    /// <summary>
    /// The clock each Ogg track's timestamps are counted in, by track id. An Ogg packet's time
    /// is in the codec's own sample clock, and the two streams in one file need not share it.
    /// </summary>
    private readonly Dictionary<int, int> oggSampleClocks = new();

    /// <summary>
    /// The last timestamp each program-stream track carried. A program stream puts a timestamp
    /// only on the packet that starts an access unit, so the packets in between have none;
    /// handing those out as zero made every frame after the first of an access unit claim to
    /// belong at the start of the file.
    /// </summary>
    private readonly Dictionary<int, long> psLastTimestamp = new();
    private readonly IElementaryAudioSource? elementaryAudio;
    private readonly IElementaryVideoSource? elementaryVideo;
    private readonly IMultiTrackSource? multiTrack;
    private readonly DemuxerType demuxerType;

    // Decoders
    private IVideoDecoder? videoDecoder;
    private IAudioDecoder? audioDecoder;

    // Track routing
    private int videoTrackIndex = -1;
    private int audioTrackIndex = -1;

    // Output channels
    private readonly Channel<VideoFrame> videoChannel;
    private readonly Channel<AudioFrame> audioChannel;

    // Lifecycle
    private readonly CancellationTokenSource pipelineCts = new();
    private DecodePipelineState state = DecodePipelineState.Created;
    private Exception? faultException;

    // Read buffer for demuxer payload reads
    private byte[] readBuffer;

    // Track info
    private readonly List<DemuxerTrackInfo> trackInfos = new();

    // Progress
    private long packetsRead;
    private long videoFramesDecoded;
    private long audioFramesDecoded;
    private long currentPositionTicks;
    private long totalDurationTicks;

    // ── Accurate-seek trim state ────────────────────────────────────────
    // One set per stream, because trim.c keeps one TrimContext per filter and ffmpeg inserts
    // one on each input stream.
    private long videoFramesTrimmed;
    private long audioSamplesTrimmed;
    private long videoTrimFirstPts = long.MinValue;
    private long audioTrimFirstSample = long.MinValue;
    private bool videoTrimEnded;
    private bool audioTrimEnded;

    /// <summary>
    /// Where the next audio frame starts, in ticks. A packet's own timestamp resets it; each
    /// frame emitted advances it by that frame's duration.
    /// </summary>
    /// <remarks>
    /// A packet can carry several audio frames - an MPEG audio frame is about 26 ms and a PES
    /// packet a good deal more - and the pipeline used to hand the PACKET's timestamp to every
    /// frame that came out of it, so frames 2..N each claimed to begin where frame 1 did; the
    /// drain pass handed the decoder a literal 0, so the frames it had been holding claimed to
    /// belong at the start of the file. Nothing read an audio timestamp closely enough to
    /// notice until the sample-accurate trim did, and the symptom then would have been audio
    /// quietly dropped or kept on the wrong side of a <c>-ss</c>. This is <c>atrim</c>'s own
    /// rule for a frame carrying no timestamp (<c>pts = s-&gt;next_pts</c>), applied one layer
    /// up where the frame count is actually known.
    /// </remarks>
    private long audioNextPts;

    /// <summary>
    /// The same clock as <see cref="audioNextPts"/>, in samples at
    /// <see cref="audioClockRate"/>, which is the unit the trim and the container's skip both
    /// work in. Ticks are derived from this and never the other way round.
    /// </summary>
    private long audioNextSample;

    /// <summary>The rate <see cref="audioNextSample"/> counts in, zero until the first frame.</summary>
    private int audioClockRate;

    /// <summary>
    /// The clock the container states its timestamps on - 90 kHz for the MPEG containers, and
    /// ticks for the ones whose demuxers here report ticks directly. It decides how much a
    /// stated timestamp can be rounded by, which is what <see cref="RescaleDelta"/> needs.
    /// </summary>
    private long containerTimestampRate = TimeSpan.TicksPerSecond;

    /// <summary>
    /// Whether the packet just read carried a timestamp of its own, rather than one carried
    /// forward from an earlier packet. Set by <see cref="ReadNextDemuxerPacket"/>.
    /// </summary>
    private bool packetTimestampIsFresh = true;

    /// <summary>
    /// The container's own start time in ticks - <c>AVFormatContext.start_time</c>. Added to
    /// <see cref="DecodePipelineOptions.StartTime"/> wherever that is compared with a packet or
    /// a frame, because <c>-ss</c> counts from the start of the stream and not from zero. See
    /// <see cref="DiscoverStreamStart"/>.
    /// </summary>
    private long streamStartTicks;

    /// <summary>
    /// Where the window starts on the container's own clock, or null when none was asked for.
    /// </summary>
    private long? TrimStartTicks
        => options.StartTime.HasValue ? options.StartTime.Value.Ticks + streamStartTicks : null;

    // ── The container's own discards: an edit list, not the caller's window ─────────────
    // ffmpeg carries these on the packet - AV_PKT_DATA_SKIP_SAMPLES side data and
    // AV_PKT_FLAG_DISCARD - and applies them inside the decoder (discard_samples, decode.c).
    // There is no side-data channel into the decoders here, so the skip is a counter the emit
    // path spends and the discard is matched back to its frame by timestamp.

    /// <summary>
    /// <c>avci-&gt;skip_samples</c>: audio samples per channel still to be thrown away from the
    /// front of the decoded stream, seeded from the container and spent as frames come out.
    /// </summary>
    private long audioSkipSamples;

    private long audioSamplesSkipped;
    private long videoFramesDiscarded;

    /// <summary>
    /// Whether the packet just read is one the container's edit list does not present.
    /// </summary>
    private bool packetIsDiscard;

    /// <summary>
    /// The presentation timestamps of video packets flagged discard, waiting for the frames
    /// they produce. A video frame cannot be dropped as its packet arrives: with B-frames the
    /// decoder returns frames in a different order and several packets later, so the flag has
    /// to travel with the timestamp. Empty for every file without an edit list.
    /// </summary>
    private readonly HashSet<long> discardedVideoPts = new();

    private DecodePipeline(
        Stream stream,
        bool ownsStream,
        Mp4Demuxer? mp4,
        MatroskaDemuxer? mkv,
        AviDemuxer? avi,
        MpegPsDemuxer? ps,
        TsDemuxer? ts,
        FlvDemuxer? flv,
        OggDemuxer? ogg,
        AsfDemuxer? asf,
        IElementaryAudioSource? elementary,
        IElementaryVideoSource? elementaryVideoSource,
        IMultiTrackSource? multiTrackSource,
        DemuxerType type,
        DecodePipelineOptions options)
    {
        fileStream = stream;
        this.ownsStream = ownsStream;
        mp4Demuxer = mp4;
        mkvDemuxer = mkv;
        aviDemuxer = avi;
        psDemuxer = ps;
        tsDemuxer = ts;
        flvDemuxer = flv;
        oggDemuxer = ogg;
        asfDemuxer = asf;
        elementaryAudio = elementary;
        elementaryVideo = elementaryVideoSource;
        multiTrack = multiTrackSource;
        demuxerType = type;
        this.options = options;
        readBuffer = ArrayPool<byte>.Shared.Rent(options.DemuxerReadBufferSize);

        videoChannel = Channel.CreateBounded<VideoFrame>(
            new BoundedChannelOptions(options.MaxVideoFrameBuffer)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });

        audioChannel = Channel.CreateBounded<AudioFrame>(
            new BoundedChannelOptions(options.MaxAudioFrameBuffer)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
    }

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>Decoded video frames output. Read from this channel.</summary>
    public ChannelReader<VideoFrame> VideoFrames => videoChannel.Reader;

    /// <summary>Decoded audio frames output.</summary>
    public ChannelReader<AudioFrame> AudioFrames => audioChannel.Reader;


    // These six counters are written by the decode thread and read by whoever is watching, so
    // every read is a Volatile.Read. A plain read is allowed to be hoisted out of a polling
    // loop, and was: DecoderAllocationTests' measurement loop saw VideoDecoderCalls as 0 on
    // every one of 6681 iterations of a WMV file and then 6681 once the loop had finished, so
    // its warm-up window never opened and it reported a whole-file average instead of the
    // windowed one. Only the decode thread writes them, so the increments stay plain.

    /// <summary>
    /// Bytes allocated inside <c>IVideoDecoder.Decode</c>, summed over every call.
    /// </summary>
    /// <remarks>
    /// Measured with <see cref="GC.GetAllocatedBytesForCurrentThread"/> around the call, which
    /// is exact for the decode thread and unaffected by anything else running. Process-wide
    /// counters cannot answer the question this exists to answer - whether a decoder allocates
    /// per frame at all - because any other thread's work lands in them too.
    /// </remarks>
    public long VideoDecoderAllocatedBytes => Volatile.Read(ref videoDecoderAllocatedBytes);

    /// <summary>Number of <c>Decode</c> calls the figure above covers.</summary>
    public long VideoDecoderCalls => Volatile.Read(ref videoDecoderCalls);

    /// <summary>Stopwatch ticks spent inside the video decoder's Decode call.</summary>
    public long VideoDecodeTicks => Volatile.Read(ref videoDecodeTicks);

    /// <summary>
    /// Bytes allocated inside <c>IAudioDecoder.Decode</c>, summed over every call, measured the
    /// same way as <see cref="VideoDecoderAllocatedBytes"/>.
    /// </summary>
    /// <remarks>
    /// This exists because the audio half of the pipeline had no such figure and the video half
    /// did, so "the decoders allocate nothing per frame" was a measured claim for video and an
    /// unmeasured one for audio. It covers the decoder and its adapter together, which is the
    /// right boundary: the adapter is what the pipeline calls.
    /// </remarks>
    public long AudioDecoderAllocatedBytes => Volatile.Read(ref audioDecoderAllocatedBytes);

    /// <summary>Number of audio <c>Decode</c> calls the figure above covers.</summary>
    public long AudioDecoderCalls => Volatile.Read(ref audioDecoderCalls);

    /// <summary>
    /// Bytes allocated inside the demuxer's packet read, summed over every call.
    /// </summary>
    /// <remarks>
    /// The decoder figures above say nothing about the demuxer, and a demuxer that allocates per
    /// packet drops frames exactly as readily as a decoder that does.
    /// </remarks>
    public long DemuxAllocatedBytes => demuxAllocatedBytes;

    /// <summary>Number of packet reads the figure above covers.</summary>
    public long DemuxCalls => demuxCalls;

    /// <summary>
    /// Frames that can be in flight beyond the channel's capacity: the one being decoded, the
    /// one the consumer is holding, the ones in transit between them, and whatever the decoder
    /// is still holding on to. Measured rather than reasoned: with three, a 200 ms consumer
    /// stall still forced one new buffer; with six, stalls of 50, 200, 500 and 1000 ms all
    /// force none. It saturates because the channel is bounded - a longer stall cannot put
    /// more frames in flight than the channel can hold.
    /// </summary>
    private const int VideoFramePoolSlack = 6;

    /// <summary>Set once the frame buffer pool has been filled for this stream's frame size.</summary>
    private bool videoFramePoolWarmed;

    /// <summary>
    /// Fills the frame buffer pool with as many buffers as can be in flight at once, the first
    /// time a frame establishes how big one is.
    /// </summary>
    /// <remarks>
    /// The pool fills lazily, so its working set is however many buffers have ever been needed
    /// at the same moment. In steady state the consumer keeps up, the bounded channel stays
    /// nearly empty, and two or three buffers is the whole working set - until the consumer
    /// stalls for a few milliseconds, the channel fills, and the decoder asks for buffers that
    /// have never existed. Each one is a whole frame: 1.38 MB at 720p, 12.4 MB at 4K. That is
    /// an allocation, and then a collection, at the exact moment the machine is already behind,
    /// which drops more frames and causes the next stall.
    /// <para>
    /// A 20 ms stall measured one such allocation and a 50 ms stall measured seven, against
    /// zero with no stall at all. The memory is not optional - the channel can hold that many
    /// frames by construction - so the only question is whether it is paid for here or during
    /// playback. <see cref="DecodePipelineOptions.MaxVideoFrameBuffer"/> is the knob: the cost
    /// is that many frame buffers plus <see cref="VideoFramePoolSlack"/>.
    /// </para>
    /// </remarks>
    private void WarmVideoFramePool(System.Buffers.ArrayPool<byte>? pool, int length)
    {
        if (videoFramePoolWarmed)
            return;

        videoFramePoolWarmed = true;

        int count = options.MaxVideoFrameBuffer + VideoFramePoolSlack;
        if (length <= 0 || count <= 0)
            return;

        var target = pool ?? System.Buffers.ArrayPool<byte>.Shared;

        // Every buffer is rented before any is returned. Renting and returning one at a time
        // would hand back the same buffer each round and leave the pool holding one.
        var warm = new byte[count][];
        for (int i = 0; i < count; i++)
            warm[i] = target.Rent(length);
        for (int i = 0; i < count; i++)
            target.Return(warm[i]);
    }

    // Written only by the decode loop, which is a single thread; read after it has been
    // cancelled and joined, or read approximately while it runs.
    private long videoDecoderAllocatedBytes;
    private long videoDecodeTicks;
    private long videoDecoderCalls;
    private long audioDecoderAllocatedBytes;
    private long audioDecoderCalls;
    private long demuxAllocatedBytes;
    private long demuxCalls;

    /// <summary>Current pipeline state.</summary>
    public DecodePipelineState State => state;

    /// <summary>Track information for the opened media.</summary>
    public IReadOnlyList<DemuxerTrackInfo> Tracks => trackInfos;

    /// <summary>
    /// TrackId of the video track being decoded (-1 if none). This is the demuxer's own id, not a
    /// position in <see cref="Tracks"/>; the two coincide often enough that callers indexing
    /// Tracks with it appeared to work. Match on <see cref="DemuxerTrackInfo.TrackId"/> instead.
    /// </summary>
    public int VideoTrackId => videoTrackIndex;

    /// <summary>TrackId of the audio track being decoded (-1 if none). See <see cref="VideoTrackId"/>.</summary>
    public int AudioTrackId => audioTrackIndex;

    /// <summary>
    /// Whether a video decoder was actually created. A track can be present with no decoder for
    /// its codec, in which case no video frame will ever arrive.
    /// </summary>
    public bool HasVideoDecoder => videoDecoder != null;

    /// <summary>
    /// Whether an audio decoder was actually created. A player that starts its audio thread on the
    /// track alone waits forever on frames that never come, and anything driving a clock from those
    /// frames stalls with it.
    /// </summary>
    public bool HasAudioDecoder => audioDecoder != null;

    /// <summary>Exception if the pipeline faulted.</summary>
    public Exception? FaultException => faultException;

    /// <summary>Event raised periodically with decode progress.</summary>
    public event Action<DecodeProgress>? ProgressChanged;

    /// <summary>
    /// Gets the current decode progress snapshot.
    /// </summary>
    public DecodeProgress GetProgress() => new()
    {
        PacketsRead = Volatile.Read(ref packetsRead),
        VideoFramesDecoded = Volatile.Read(ref videoFramesDecoded),
        AudioFramesDecoded = Volatile.Read(ref audioFramesDecoded),
        VideoFramesTrimmed = Volatile.Read(ref videoFramesTrimmed),
        AudioSamplesTrimmed = Volatile.Read(ref audioSamplesTrimmed),
        AudioSamplesSkipped = Volatile.Read(ref audioSamplesSkipped),
        VideoFramesDiscarded = Volatile.Read(ref videoFramesDiscarded),
        CurrentPositionTicks = Volatile.Read(ref currentPositionTicks),
        TotalDurationTicks = Volatile.Read(ref totalDurationTicks),
    };

    // ── Factory ─────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a media file and creates a decode pipeline.
    /// Probes the format, initializes the demuxer, discovers tracks, and creates decoders.
    /// </summary>
    public static DecodePipeline Open(string filePath, DecodePipelineOptions? options = null)
    {
        options ??= new DecodePipelineOptions();
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);

        try
        {
            return Open(stream, ownsStream: true, options);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a stream and creates a decode pipeline.
    /// </summary>
    public static DecodePipeline Open(Stream stream, bool ownsStream = false, DecodePipelineOptions? options = null)
    {
        options ??= new DecodePipelineOptions();

        // A live source - a radio stream, a pipe - cannot rewind, but probing and demuxer start-up
        // both need to. Buffering a window costs little and lets everything below work unchanged.
        if (!stream.CanSeek)
        {
            stream = new RewindBufferStream(stream, ownsSource: ownsStream);
            ownsStream = true;
        }

        // Read header for format probing. This used to be 16 bytes, which is too short for
        // several probes to do their job: MPEG-TS needs to see the sync byte repeat at a 188-byte
        // stride, and an elementary audio stream needs a chain of frame headers.
        byte[] header = new byte[ProbeHeaderSize];
        long startPos = stream.Position;
        int read = 0;
        while (read < header.Length)
        {
            int got = stream.Read(header, read, header.Length - read);
            if (got == 0) break;
            read += got;
        }
        stream.Position = startPos;

        var probe = DemuxerFactory.ProbeByMagic(header[..read]);
        if (!probe.Success)
            throw new NotSupportedException("Could not detect media format");

        Mp4Demuxer? mp4 = null;
        MatroskaDemuxer? mkv = null;
        AviDemuxer? avi = null;
        MpegPsDemuxer? ps = null;
        TsDemuxer? ts = null;
        FlvDemuxer? flv = null;
        OggDemuxer? ogg = null;
        AsfDemuxer? asf = null;
        IElementaryAudioSource? elementary = null;
        IElementaryVideoSource? elementaryVideo = null;
        IMultiTrackSource? multiTrack = null;

        switch (probe.Type)
        {
            case DemuxerType.Mp4:
                mp4 = new Mp4Demuxer(stream);
                if (!mp4.Initialize())
                    throw new InvalidDataException("Failed to initialize MP4 demuxer");
                break;

            case DemuxerType.Matroska:
                mkv = new MatroskaDemuxer(stream);
                if (!mkv.Initialize())
                    throw new InvalidDataException("Failed to initialize Matroska demuxer");
                break;

            case DemuxerType.Avi:
                avi = new AviDemuxer(stream);
                avi.Initialize();
                break;

            case DemuxerType.MpegPs:
                ps = new MpegPsDemuxer();
                if (!ps.Initialize(stream))
                    throw new InvalidDataException("Failed to initialize MPEG program stream demuxer");
                break;

            case DemuxerType.MpegTs:
                ts = new TsDemuxer(stream);
                if (!ts.Initialize())
                    throw new InvalidDataException("Failed to initialize MPEG transport stream demuxer");
                break;

            case DemuxerType.Flv:
                flv = new FlvDemuxer(stream);
                if (!flv.Initialize())
                    throw new InvalidDataException("Failed to initialize FLV demuxer");
                break;

            case DemuxerType.Ogg:
                ogg = new OggDemuxer(stream);
                if (!ogg.Initialize())
                    throw new InvalidDataException("Failed to initialize Ogg demuxer");
                break;

            case DemuxerType.Asf:
                asf = new AsfDemuxer(stream, leaveOpen: true);
                if (!asf.Parse().IsValid)
                    throw new InvalidDataException("Failed to initialize ASF demuxer");
                break;

            case DemuxerType.Mp3:
            {
                var mpegAudio = new MpegAudioDemuxer(stream);
                if (!mpegAudio.Parse())
                    throw new InvalidDataException("Failed to initialize MPEG audio demuxer");
                elementary = new MpegAudioSource(mpegAudio);
                break;
            }

            case DemuxerType.Adts:
            {
                var adts = new AacDemuxer(stream);
                if (!adts.Parse())
                    throw new InvalidDataException("Failed to initialize ADTS demuxer");
                elementary = new AdtsAudioSource(adts);
                break;
            }

            case DemuxerType.Ac3:
            {
                var ac3 = new Ac3Demuxer(stream);
                if (!ac3.Parse())
                    throw new InvalidDataException("Failed to initialize AC-3 demuxer");
                elementary = new Ac3AudioSource(ac3);
                break;
            }

            // A WAV or AIFF is an elementary PCM stream with a header on it: the format chunk
            // says what the samples are and the data chunk is nothing but samples. The codec
            // id the source reports is the ffmpeg name - pcm_s16le, pcm_s24be, pcm_alaw - so
            // the registry picks the right one of pcm.c's formats rather than a default.
            case DemuxerType.Wav:
            {
                var wav = new WavDemuxer(stream);
                if (!wav.Initialize())
                    throw new InvalidDataException("Failed to initialize WAV demuxer");
                elementary = new WavAudioSource(wav);
                break;
            }

            case DemuxerType.Au:
            {
                var au = new AuDemuxer(stream);
                if (!au.InitializeAsync().GetAwaiter().GetResult())
                    throw new InvalidDataException("Failed to initialize AU demuxer");
                elementary = new AuAudioSource(au);
                break;
            }

            case DemuxerType.Aiff:
            {
                var aiff = new AiffDemuxer(stream, leaveOpen: true);
                // AiffDemuxer only offers InitializeAsync, and Open is synchronous for every
                // other container. Blocking here reads a few hundred bytes of chunk headers
                // off a stream that is already open, which is what the other demuxers do on
                // this same call.
                if (!aiff.InitializeAsync().AsTask().GetAwaiter().GetResult())
                    throw new InvalidDataException("Failed to initialize AIFF demuxer");
                elementary = new AiffAudioSource(aiff, aiff.Info);
                break;
            }

            // A native FLAC file: the header chain, then nothing but frames. Finding where one
            // frame ends is the entire difficulty, because the format records neither a frame
            // length nor an index - see FlacParser, which FlacAudioSource drives.
            case DemuxerType.Flac:
            {
                var flac = new FlacDemuxer(stream, leaveOpen: true);
                var file = flac.Parse()
                    ?? throw new InvalidDataException("Failed to parse the FLAC header chain");
                elementary = new FlacAudioSource(stream, file);
                break;
            }

            // A raw DTS elementary stream. Unlike FLAC this needs no parser: a core frame
            // header states its own byte size, so the next frame is at a known offset.
            case DemuxerType.Dts:
                elementary = new DtsAudioSource(stream);
                break;

            case DemuxerType.DtsHd:
                elementary = new DtsHdAudioSource(stream);
                break;

            // Apple's CAF. Unlike the elementary formats above this is a real container - a
            // chunk chain, a magic cookie and a packet table - but it holds exactly one audio
            // track, so it reaches the pipeline the same way.
            case DemuxerType.Caf:
            {
                var caf = new CafDec(stream);
                caf.ReadHeader();
                elementary = new CafAudioSource(caf);
                break;
            }

            // Creative's VOC. It has no header worth the name - the format arrives in the first
            // data block - so the source reads that block in its constructor and hands it back
            // on the first ReadFrame.
            case DemuxerType.Voc:
            {
                var voc = new VocDec(stream);
                voc.ReadHeader();
                elementary = new VocAudioSource(voc);
                break;
            }

            // Maxis XA: a 24-byte header and then nothing but fixed-size EA ADPCM blocks.
            case DemuxerType.Xa:
            {
                var xa = new XaDec(stream);
                xa.ReadHeader();
                elementary = new XaAudioSource(xa);
                break;
            }

            // True Audio. The 22-byte file header is the decoder's extradata as well as the
            // demuxer's, because a TTA frame says nothing about the format.
            case DemuxerType.Tta:
            {
                var tta = new TtaDec(stream);
                tta.ReadHeader();
                elementary = new TtaAudioSource(tta);
                break;
            }

            // THE VIDEO HALF OF THE ELEMENTARY PATH. Each of these had a working demuxer
            // and no way into this method, so the codec behind it was unplayable: IVF holds
            // every VP8, VP9 and AV1 test vector in this tree, and VC-1 and MPEG-4 Part 2
            // are both FATE-conformant. See ElementaryVideoSource.cs.
            case DemuxerType.Ivf:
            {
                var ivf = new IvfDemuxer(stream);
                if (!ivf.Parse())
                    throw new InvalidDataException("Failed to initialize IVF demuxer");
                elementaryVideo = new IvfVideoSource(ivf);
                break;
            }

            case DemuxerType.Mpeg4Video:
                elementaryVideo = new Mpeg4VideoSource(
                    new Codecs.Mpeg4Video.Mpeg4AccessUnitReader(stream, leaveOpen: true));
                break;

            case DemuxerType.Vc1Es:
                elementaryVideo = new Vc1ElementarySource(
                    new Codecs.Vc1Video.Vc1AccessUnitReader(stream, leaveOpen: true));
                break;

            case DemuxerType.Vc1Rcv:
            {
                var rcv = new Vc1TestDemuxer(stream, leaveOpen: true);
                if (!rcv.Parse())
                    throw new InvalidDataException("Failed to initialize VC-1 RCV demuxer");
                elementaryVideo = new Vc1TestVideoSource(rcv);
                break;
            }

            // Y4M. The headerless raw case is deliberately absent: without a header there is
            // nothing to say what the bytes are, and this project will not guess a geometry.
            case DemuxerType.RawVideo:
            {
                // CreateFromY4M, not the constructor: the constructor takes the geometry as
                // a configuration, which is what a HEADERLESS raw stream needs and cannot be
                // known here, while a Y4M states all of it in a text line of its own. Only
                // Y4M reaches this case, because the probe is the YUV4MPEG2 magic.
                RawVideoDemuxer raw = RawVideoDemuxer.CreateFromY4M(stream, leaveOpen: true)
                    ?? throw new InvalidDataException("Failed to read the Y4M header");
                RawVideoInfo info = raw.Parse();
                if (!info.IsValid)
                    throw new InvalidDataException("Failed to initialize raw video demuxer");
                elementaryVideo = new RawVideoSource(raw, info);
                break;
            }

            // The two containers that hold a video track and an audio track and no index.
            // See MultiTrackSource.cs; both demuxers have had passing gates of their own since
            // they were translated, and neither could be reached from here.
            case DemuxerType.Smjpeg:
            {
                var smjpeg = new SmjpegDemuxer(stream, leaveOpen: true);
                if (!smjpeg.ReadHeader())
                    throw new InvalidDataException("Failed to read the SMJPEG header");
                multiTrack = new SmjpegSource(smjpeg);
                break;
            }

            case DemuxerType.Thp:
            {
                var thp = new ThpDemuxer(stream, leaveOpen: true);
                if (!thp.ReadHeader())
                    throw new InvalidDataException("Failed to read the THP header");
                multiTrack = new ThpSource(thp);
                break;
            }

            default:
                throw new NotSupportedException($"Demuxer type {probe.Type} is not yet supported in the decode pipeline");
        }

        var pipeline = new DecodePipeline(stream, ownsStream, mp4, mkv, avi, ps, ts, flv, ogg, asf, elementary, elementaryVideo, multiTrack, probe.Type, options);
        pipeline.DiscoverTracks();
        pipeline.CreateDecoders();
        return pipeline;
    }

    // ── Run ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the decode loop. Returns when all frames are decoded or the pipeline is cancelled.
    /// The caller should read from VideoFrames/AudioFrames concurrently.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellation = default)
    {
        if (state != DecodePipelineState.Created)
            throw new InvalidOperationException($"Pipeline cannot be started in state {state}");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(pipelineCts.Token, cancellation);
        state = DecodePipelineState.Running;

        try
        {
            await Task.Run(() => DecodeLoop(linked.Token), linked.Token).ConfigureAwait(false);

            // A short file can finish decoding in the window between Cancel() and the loop
            // noticing, which would otherwise report Completed for a run the caller cancelled.
            // Once cancellation has been requested the terminal state is Cancelled, so callers
            // (and tests) get a deterministic answer rather than one that depends on timing.
            state = linked.Token.IsCancellationRequested
                ? DecodePipelineState.Cancelled
                : DecodePipelineState.Completed;
        }
        catch (OperationCanceledException)
        {
            state = DecodePipelineState.Cancelled;
        }
        catch (Exception ex)
        {
            faultException = ex;
            state = DecodePipelineState.Faulted;
            throw;
        }
        finally
        {
            videoChannel.Writer.TryComplete();
            audioChannel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Cancels the running pipeline.
    /// </summary>
    public void Cancel()
    {
        pipelineCts.Cancel();
    }

    // ── Core decode loop ────────────────────────────────────────────────

    private void DecodeLoop(CancellationToken ct)
    {
        // Seek to start time if specified, EARLIER than asked by SeekPreRoll - see the note on
        // the constant. The trim below discards the difference to the sample, so the extra
        // costs decode time and changes no output.
        if (options.StartTime.HasValue)
        {
            double target = Math.Max(
                0,
                (TrimStartTicks!.Value / (double)TimeSpan.TicksPerMillisecond)
                    - SeekPreRoll.TotalMilliseconds);
            SeekDemuxer(target);
        }

        // The container's priming, read after the seek because a seek into the middle of it has
        // already passed part of it - `sti->skip_samples = mov_get_skip_samples(st, sample)`.
        // ffmpeg hands this to the decoder as packet side data; here the emit path spends it.
        audioSkipSamples = ContainerSkipSamples();

        int progressCounter = 0;

        while (!ct.IsCancellationRequested)
        {
            long demuxAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            bool gotPacket = ReadNextDemuxerPacket(
                    out int trackIndex, out long pts, out long dts, out bool isKeyframe,
                    out long payloadPosition, out int payloadSize, out var payload,
                    out int endTrimSamples, out bool endOfStream);
            demuxAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - demuxAllocatedBefore;
            demuxCalls++;

            if (!gotPacket)
            {
                if (endOfStream) break;
                continue; // packet from unrecognized track or error
            }

            Interlocked.Increment(ref packetsRead);

            // Stop once every stream being decoded has passed the end of the requested window.
            // THIS USED TO BE `if (pts > endTimeTicks) break`, a cutoff on the PACKET, which
            // cannot be right for a codec with B-frames: packets arrive in decode order, so the
            // packet whose pts crosses the boundary is nowhere near the last frame still inside
            // it, and the reorder buffer was left holding frames the drain did not recover.
            // `-t 2` on Video-H264-AAC.mp4 gave 55 video frames where ffmpeg gives 60. The
            // window is a property of the frames, so it is enforced on the frames, and the
            // demuxer stops when no stream needs another packet - which is how ffmpeg does it
            // too: the trim filters set EOF on their own links and the demux loop ends when
            // every output has finished.
            // The `|| is null` arms let a file with only one of the two streams finish, but both
            // being null must not count as "everything finished" - that would end the loop on
            // the first packet of a container whose codecs we cannot decode.
            if ((videoDecoder is not null || audioDecoder is not null) &&
                (videoDecoder is null || videoTrimEnded) &&
                (audioDecoder is null || audioTrimEnded))
                break;

            // Update position
            Volatile.Write(ref currentPositionTicks, pts);

            // Route packet to appropriate decoder
            if (trackIndex == videoTrackIndex && videoDecoder != null)
            {
                var videoPayload = payload.IsEmpty
                    ? ReadPayloadIntoBuffer(payloadPosition, payloadSize)
                    : payload.Span;

                // Remembered by timestamp, not acted on here: the frame this packet produces
                // may be several packets away and out of order. See discardedVideoPts.
                if (packetIsDiscard)
                    discardedVideoPts.Add(pts);

                // Bracketed on the decode thread, so the figure is the decoder's own allocation
                // and not whatever else the process is doing. Two thread-local reads per frame.
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var frame = videoDecoder.Decode(
                    videoPayload,
                    packetTimestampIsFresh ? pts : DecodedAudioFrame.NoTimestamp,
                    isKeyframe);
                videoDecodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - startedAt;
                videoDecoderAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                videoDecoderCalls++;

                if (frame.HasValue)
                {
                    var decoded = frame.Value;
                    WarmVideoFramePool(decoded.BufferPool, decoded.BufferLength);
                    var videoFrame = VideoFrame.TakeOwnership(ref decoded);
                    videoFrame.Pts = GuessVideoTimestamp(videoFrame.Pts);
                    if (discardedVideoPts.Count > 0 && discardedVideoPts.Remove(videoFrame.Pts))
                    {
                        // The edit list does not present this frame. It was decoded because the
                        // frames that are presented may refer to it.
                        videoFrame.Dispose();
                        Interlocked.Increment(ref videoFramesDiscarded);
                    }
                    else if (KeepVideoFrame(videoFrame.Pts))
                    {
                        // Synchronous write with wait — respects backpressure
                        videoChannel.Writer.WriteAsync(videoFrame, ct).AsTask().GetAwaiter().GetResult();
                        Interlocked.Increment(ref videoFramesDecoded);
                    }
                    else
                    {
                        // Decoded because the frames between the keyframe and the seek point
                        // are references for the ones after it; discarded because they are
                        // outside what was asked for.
                        videoFrame.Dispose();
                        Interlocked.Increment(ref videoFramesTrimmed);
                    }
                }
            }
            else if (trackIndex == audioTrackIndex && audioDecoder != null)
            {
                var audioPayload = payload.IsEmpty
                    ? ReadPayloadIntoBuffer(payloadPosition, payloadSize)
                    : payload.Span;
                // One packet can hold several audio frames - an MPEG audio frame is about
                // 26 ms and a PES packet a good deal more - so the decoder is pumped until it
                // has nothing left rather than once per packet. A decoder that cannot buffer
                // returns null on the first empty call and the loop costs it one call.
                // A packet's own timestamp restarts the audio clock; the frames that come out
                // after the first advance it themselves. See the note on audioNextPts.
                //
                // The test is freshness, NOT `pts >= 0`. Using the sign for this broke Ogg
                // Opus: an Ogg page says only where its last packet ends, so the first
                // packet's time is the page's granule position less the durations of every
                // packet on it, and for the first page of a file that is NEGATIVE - -65000
                // ticks on the gate's generated Opus stream. Treating that as "no timestamp"
                // put the start of the file at zero and the playback-ogg row caught it. A
                // demuxer that means "absent" says so by clearing the flag.
                // The container's timestamp, or the absence of one, is handed to the decoder as
                // it stands; which FRAME it belongs to is the decoder's to decide, because only
                // a decoder that frames its own input knows where each frame starts. One that
                // does not echoes it back and the first frame out of the packet carries it,
                // which is the same thing for a container that packs one frame per packet.
                long packetPts = packetTimestampIsFresh ? pts : DecodedAudioFrame.NoTimestamp;

                long audioAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var decodedAudio = audioDecoder.Decode(audioPayload, packetPts);
                audioDecoderAllocatedBytes +=
                    GC.GetAllocatedBytesForCurrentThread() - audioAllocatedBefore;
                audioDecoderCalls++;

                for (int emitted = 0; decodedAudio != null && emitted < 64; emitted++)
                {
                    // The last packet of a stream can decode to more audio than the file
                    // actually holds, because the encoder padded the frame out; the container
                    // says how much to drop. Only the first frame out of a packet carries that,
                    // and the copy costs nothing worth avoiding because it happens once.
                    // This runs BEFORE the trim, the order ffmpeg uses: the decoder applies the
                    // container's skip_samples and the filter graph trims what is left.
                    int available = decodedAudio.SampleCount - (emitted == 0 ? endTrimSamples : 0);

                    EmitAudioFrame(decodedAudio, available, ct);

                    audioAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();

                    // Nothing new arrived, so there is no new container timestamp either: a
                    // frame the decoder still had in hand continues from where the last one
                    // ended unless the decoder says otherwise.
                    decodedAudio = audioDecoder.Decode(
                        ReadOnlySpan<byte>.Empty, DecodedAudioFrame.NoTimestamp);
                    audioDecoderAllocatedBytes +=
                        GC.GetAllocatedBytesForCurrentThread() - audioAllocatedBefore;
                    audioDecoderCalls++;
                }
            }

            if (fileEnded)
                break;

            // Progress reporting (every 100 packets)
            if (++progressCounter >= 100)
            {
                progressCounter = 0;
                ProgressChanged?.Invoke(GetProgress());
            }
        }

        // Drain remaining frames from B-frame reorder buffer
        state = DecodePipelineState.Draining;
        DrainDecoders(ct);

        // Final progress
        ProgressChanged?.Invoke(GetProgress());
    }

    /// <summary>
    /// How far before the requested position the demuxer is actually seeked, so that stateful
    /// decoders are primed by the time the window starts.
    /// </summary>
    /// <remarks>
    /// A transform audio decoder's output does not depend on its current frame alone: it
    /// overlap-adds with the previous one, and most carry an output delay on top. Start decoding
    /// at the window and the first samples are missing the other half of their window.
    /// <para>
    /// It is not enough to seek to "the sample at or before the position". Every AAC sample is a
    /// sync sample, so <see cref="Demuxers.Mp4Demuxer.Seek"/> - which puts each track on its own
    /// preceding sync sample - lands the audio track essentially on the boundary itself.
    /// <c>-ss 10 -t 2</c> on Video-H264-AAC.mp4 then had its first 253 sample frames wrong, by
    /// up to 404 of 32768, with everything after them correct. ffmpeg does not hit this because
    /// <c>avformat_seek_file</c> seeks by FILE POSITION from one stream's index and then reads
    /// every stream from there, so its audio starts at the video keyframe - roughly half a
    /// second early on this file, by accident of the interleave.
    /// </para>
    /// This is that priming made deliberate rather than incidental. It has to cover the deepest
    /// audio decoder state here: AAC is 1024 samples of overlap plus 1024 of delay (46 ms at
    /// 44.1 kHz), HE-AAC adds SBR's own history, MPEG audio is a granule plus the 528-sample
    /// filterbank delay, and Opus declares a pre-skip of up to 80 ms. Half a second clears all
    /// of them with room to spare, and costs about fifteen video frames that are decoded and
    /// thrown away - which the trim counters report rather than hide.
    /// </remarks>
    private static readonly TimeSpan SeekPreRoll = TimeSpan.FromMilliseconds(500);

    // ── Accurate-seek trim ──────────────────────────────────────────────
    // Translated from libavfilter/trim.c. fftools/ffmpeg_filter.c inserts a `trim` on every
    // input video stream and an `atrim` on every input audio stream whenever -ss or -t is given
    // and accurate_seek is on, which it is by default:
    //
    //     opts->trim_start_us = ((d->f.start_time == AV_NOPTS_VALUE) || !d->accurate_seek) ?
    //                           AV_NOPTS_VALUE : tsoffset;
    //     opts->trim_end_us   = d->recording_time;      -- ffmpeg_demux.c:1128
    //
    // Without them a seek is only as accurate as the nearest keyframe, which is what this
    // pipeline did: `-ss 10 -t 2` on Video-H264-AAC.mp4 emitted 170 frames of a two-second
    // window, all of them real, the first 110 of them from before the ten-second mark.

    /// <summary>
    /// The container's priming for the audio track being decoded: how many samples per channel
    /// to throw away before the first one the timeline presents.
    /// </summary>
    /// <remarks>
    /// Read from the demuxer rather than carried on a packet, which is where ffmpeg puts it
    /// (<c>AV_PKT_DATA_SKIP_SAMPLES</c> on the first packet, and again after every seek). Only
    /// MP4 states it, in its edit list; every other demuxer here reports zero.
    /// </remarks>
    private long ContainerSkipSamples()
    {
        if (mp4Demuxer is null || audioTrackIndex < 0 || audioTrackIndex >= mp4Demuxer.Tracks.Count)
            return 0;

        // Indexed, not searched: DiscoverMp4Tracks numbers its tracks by position and
        // Mp4Packet.TrackIndex is that same position, which is why the decode loop can compare
        // the two directly. See the note on VideoTrackId for containers where they differ.
        return mp4Demuxer.Tracks[audioTrackIndex].SkipSamples;
    }

    /// <summary>
    /// <c>discard_samples</c> (libavcodec/decode.c), the skip path: takes the container's
    /// priming off the front of the decoded audio, whole frames at a time until the one the
    /// real audio starts inside.
    /// </summary>
    /// <returns>Samples per channel to drop from the front of this frame.</returns>
    private int DiscardContainerSkipSamples(int available, int sampleRate,
                                            ref long frameStartSample)
    {
        if (audioSkipSamples <= 0 || available <= 0)
            return 0;

        // "skip whole frame, skip left: %d"
        if (available <= audioSkipSamples)
        {
            audioSkipSamples -= available;
            Interlocked.Add(ref audioSamplesSkipped, available);
            return available;
        }

        int skip = (int)audioSkipSamples;
        audioSkipSamples = 0;
        Interlocked.Add(ref audioSamplesSkipped, skip);

        // The C moves the frame's own timestamps forward by what it dropped; the stream clock
        // is unaffected, because the samples were really there.
        frameStartSample += skip;

        return skip;
    }

    /// <summary>
    /// Whether a decoded video frame is inside the requested window. Translated from
    /// <c>trim_filter_frame</c>: whole frames only, since half a picture is not a picture.
    /// </summary>
    /// <summary>
    /// The timestamp of the last picture handed out, for the one after it to count from.
    /// </summary>
    private long lastVideoPts = DecodedAudioFrame.NoTimestamp;

    /// <summary>The interval between the last two stamped pictures, or zero before there were two.</summary>
    private long videoIntervalTicks;

    /// <summary>
    /// <c>guess_correct_pts</c>: a picture that states no timestamp gets the one after the
    /// picture before it.
    /// </summary>
    /// <remarks>
    /// Containers do not stamp every picture. <c>Video-MPG-MPG.mpg</c> states none at all on
    /// the P picture of its first group, and ffmpeg fills that in - its frame list has it at
    /// 826.822 ms, one frame interval after the picture before it, which is what
    /// <c>AVFrame.best_effort_timestamp</c> means.
    /// <para>
    /// This runs after the decoder has returned, which is where the C does it too, and so it
    /// runs in DISPLAY order: the interval is added to the frame that was shown last, not to
    /// whatever was decoded last.
    /// </para>
    /// </remarks>
    private long GuessVideoTimestamp(long ptsTicks)
    {
        if (ptsTicks != DecodedAudioFrame.NoTimestamp)
        {
            // Two real timestamps in a row measure the interval, which is what the next
            // unstamped picture is placed by. Measuring beats asking: the MPEG program stream
            // demuxer states no frame rate at all, and a 25 fps guess put a 29.97 fps picture
            // 7 ms past where ffmpeg has it.
            if (lastVideoPts != DecodedAudioFrame.NoTimestamp && ptsTicks > lastVideoPts)
                videoIntervalTicks = ptsTicks - lastVideoPts;

            lastVideoPts = ptsTicks;
            return ptsTicks;
        }

        if (lastVideoPts == DecodedAudioFrame.NoTimestamp)
        {
            // Nothing has been shown yet, so there is nothing to count from. Zero is where a
            // stream starts unless something says otherwise.
            lastVideoPts = 0;
            return 0;
        }

        lastVideoPts += videoIntervalTicks > 0 ? videoIntervalTicks : VideoFrameIntervalTicks;
        return lastVideoPts;
    }

    /// <summary>
    /// One frame of the video track, in ticks, or a 25 fps guess when the container states no
    /// frame rate.
    /// </summary>
    private long VideoFrameIntervalTicks
    {
        get
        {
            foreach (var track in trackInfos)
            {
                if (track.TrackType == DemuxerTrackType.Video && track.TrackId == VideoTrackId
                    && track.FrameRate > 0)
                {
                    return (long)Math.Round(TimeSpan.TicksPerSecond / track.FrameRate);
                }
            }

            return TimeSpan.TicksPerSecond / 25;
        }
    }

    private bool KeepVideoFrame(long ptsTicks)
    {
        if (videoTrimEnded)
            return false;

        if (TrimStartTicks is { } startTicks && ptsTicks < startTicks)
            return false;

        if (videoTrimFirstPts == long.MinValue)
            videoTrimFirstPts = ptsTicks;

        // `s->duration_tb && frame->pts - s->first_pts < s->duration_tb` - measured from the
        // first frame KEPT, not from the requested start, so a start that falls between two
        // frames does not cost the window a frame at the end.
        if (options.Duration.HasValue
            && ptsTicks - videoTrimFirstPts >= options.Duration.Value.Ticks)
        {
            videoTrimEnded = true;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The half-open sample range of a decoded audio frame that is inside the requested window.
    /// Translated from <c>atrim_filter_frame</c>, which trims to the sample rather than
    /// dropping whole frames.
    /// </summary>
    /// <returns>
    /// The first sample to keep; <paramref name="lastSample"/> receives one past the last.
    /// <c>first >= last</c> means keep nothing.
    /// </returns>
    private int KeepAudioSamples(long framePts, int sampleCount, int sampleRate,
                                 out int lastSample)
    {
        lastSample = 0;
        if (audioTrimEnded || sampleCount <= 0 || sampleRate <= 0)
            return 0;

        // atrim works in sample units at the link's rate, and so does this: `framePts` arrives
        // as a sample index on the audio clock rather than as ticks. Converting each frame's
        // tick timestamp separately is what this replaced, and it cost a sample: 100 ns units
        // cannot hold 1/44100 of a second exactly, so one frame's conversion landed a sample
        // below where the accumulation had reached and the window's last frame was one sample
        // long - 88,201 samples for a two-second window where ffmpeg gives 88,200.
        int startSample = 0;

        if (TrimStartTicks is { } startTicks)
        {
            long startPts = TicksToSamples(startTicks, sampleRate);

            // "check if at least a part of the frame is after the start time"
            if (framePts + sampleCount <= startPts)
                return 0;

            startSample = (int)Math.Clamp(startPts - framePts, 0, sampleCount);
        }

        if (audioTrimFirstSample == long.MinValue)
            audioTrimFirstSample = framePts + startSample;

        int endSample = sampleCount;

        if (options.Duration.HasValue)
        {
            long endPts = audioTrimFirstSample
                        + TicksToSamples(options.Duration.Value.Ticks, sampleRate);

            if (framePts >= endPts)
            {
                audioTrimEnded = true;
                return 0;
            }

            endSample = (int)Math.Clamp(endPts - framePts, 0, sampleCount);
        }

        lastSample = endSample;
        return startSample;
    }

    /// <summary>
    /// Applies the trim to one decoded audio frame and writes what survives to the audio
    /// channel. Advances <see cref="audioNextPts"/> past the frame whether it survived or not.
    /// </summary>
    /// <param name="available">
    /// Samples per channel the frame really holds, after the container's end trim.
    /// </param>
    private void EmitAudioFrame(DecodedAudioFrame decoded, int available, CancellationToken ct)
    {
        int rate = decoded.SampleRate;

        // `pts = s->next_pts` when the frame has none of its own - atrim's own rule, and the
        // demuxer's: a frame that carries a timestamp resynchronises the clock, and one that
        // does not continues from where the last frame ended.
        //
        // The clock itself is kept in SAMPLES, which is the time base an audio stream has in
        // ffmpeg and the unit atrim does all its arithmetic in. Ticks cannot hold 1/44100 of a
        // second, so a clock kept in ticks and converted per frame drifts under the rounding
        // and lands a sample out at the window's edge.
        if (decoded.HasTimestamp)
        {
            bool accumulating = audioClockRate == rate;
            audioClockRate = rate;
            audioNextSample = RescaleDelta(decoded.PresentationTimeTicks, rate,
                                           audioNextSample, accumulating);
        }
        else if (rate != audioClockRate)
        {
            // The rate changed under the clock, so the clock's unit changed with it.
            audioNextSample = TicksToSamples(audioNextPts, rate);
            audioClockRate = rate;
        }

        long frameStartSample = audioNextSample;

        if (available > 0)
            audioNextSample += available;

        audioNextPts = SamplesToTicks(audioNextSample, rate);

        // The container's priming comes off BEFORE the window, the order ffmpeg uses: the
        // decoder applies skip_samples and the filter graph trims what is left of the frame.
        int skipped = DiscardContainerSkipSamples(available, rate, ref frameStartSample);
        available -= skipped;

        if (available <= 0)
            return;

        int first = KeepAudioSamples(frameStartSample, available, rate, out int last);

        if (first >= last)
        {
            Interlocked.Add(ref audioSamplesTrimmed, Math.Max(0, available));
            return;
        }

        Interlocked.Add(ref audioSamplesTrimmed, available - (last - first));

        int channels = decoded.Channels;
        var audioFrame = skipped == 0 && first == 0 && last == decoded.SampleCount
            ? AudioFrame.TakeOwnership(decoded)
            : AudioFrame.Create(
                  decoded.Samples.Span[((skipped + first) * channels)..((skipped + last) * channels)],
                  last - first, channels, rate, 0);

        // Set here rather than taken from the decoder, which only echoes back whatever it was
        // handed: the timestamp is this loop's to compute, and a trimmed frame starts later
        // than the frame it came from.
        audioFrame.Pts = SamplesToTicks(frameStartSample + first, rate);

        audioChannel.Writer.WriteAsync(audioFrame, ct).AsTask().GetAwaiter().GetResult();
        Interlocked.Increment(ref audioFramesDecoded);
    }

    /// <summary>
    /// A tick count as a sample index, rounding the way <c>av_rescale_q</c> does by default:
    /// to nearest, halves away from zero.
    /// </summary>
    /// <summary>
    /// A timestamp in some other rate as ticks, rounding the way <c>av_rescale_q</c> does by
    /// default: to nearest, halves away from zero.
    /// </summary>
    /// <remarks>
    /// Truncating here costs a sample at a window's edge. A 90 kHz timestamp is not a whole
    /// number of 100 ns ticks, and the audio clock resynchronises to a container timestamp
    /// whenever one arrives, so a conversion that always rounds down puts the resynchronised
    /// clock a sample below where the frames had accumulated to - and a two-second window then
    /// comes out 88,201 samples where ffmpeg gives 88,200.
    /// </remarks>
    private static long RescaleToTicks(long value, long rate)
    {
        if (rate <= 0)
            return 0;

        long half = rate / 2;
        return value >= 0
            ? (value * TimeSpan.TicksPerSecond + half) / rate
            : -((-value * TimeSpan.TicksPerSecond + half) / rate);
    }

    /// <summary>
    /// <c>av_rescale_delta</c>: where a frame begins, in samples, given the timestamp its
    /// container states and where the frames before it have accumulated to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A container states its timestamps on its own clock - 90 kHz for an MPEG program stream -
    /// and an MP2 frame of 1152 samples at 44100 Hz is 2351.02 of those units long, so the
    /// stated timestamps cannot tile the frames exactly and jitter by a sample as they round.
    /// Taking each one literally makes consecutive frames overlap or leave a gap, and a window
    /// measured over them then comes out a sample long or a sample short: two seconds of
    /// <c>Video-MPG-MPG.mpg</c> gave 88,201 samples against ffmpeg's 88,200 at every
    /// <c>-ss</c> tried.
    /// </para>
    /// <para>
    /// The C's answer, through <c>audio_ts_process</c>: work out the range of sample positions
    /// the stated timestamp could mean - it is a rounded figure, so it means an interval, not a
    /// point - and keep the accumulated position while it is inside that range. Only a
    /// timestamp that disagrees by more than the rounding can mean is a real discontinuity, and
    /// only that resets the clock.
    /// </para>
    /// <code>
    /// a =  av_rescale_q_rnd(2*in_ts-1, in_tb, fs_tb, AV_ROUND_DOWN)   >>1;
    /// b = (av_rescale_q_rnd(2*in_ts+1, in_tb, fs_tb, AV_ROUND_UP  )+1)>>1;
    /// if (*last &lt; 2*a - b || *last &gt; 2*b - a)
    ///     goto simple_round;
    /// this = av_clip64(*last, a, b);               // libavutil/mathematics.c
    /// </code>
    /// </remarks>
    private long RescaleDelta(long timestampTicks, int sampleRate, long accumulated,
                              bool accumulating)
    {
        long containerRate = containerTimestampRate;

        // The stated timestamp back on the container's own clock, which is where its precision
        // - and so the width of the interval it stands for - comes from.
        long stated = RescaleFromTicks(timestampTicks, containerRate);
        long rounded = AvRescale(stated, sampleRate, containerRate);

        if (!accumulating || containerRate >= TimeSpan.TicksPerSecond)
            return rounded;

        long denominator = 2 * containerRate;
        long low = FloorDiv((2 * stated - 1) * sampleRate, denominator);
        long high = CeilingDiv((2 * stated + 1) * sampleRate, denominator);

        if (accumulated < 2 * low - high || accumulated > 2 * high - low)
            return rounded;

        return Math.Clamp(accumulated, low, high);
    }

    /// <summary>A tick count on some other clock, rounded to nearest.</summary>
    private static long RescaleFromTicks(long ticks, long rate)
    {
        long half = TimeSpan.TicksPerSecond / 2;
        return ticks >= 0
            ? (ticks * rate + half) / TimeSpan.TicksPerSecond
            : -((-ticks * rate + half) / TimeSpan.TicksPerSecond);
    }

    /// <summary><c>av_rescale</c>: to nearest, halves away from zero.</summary>
    private static long AvRescale(long value, long multiplier, long divisor)
    {
        if (divisor == 0)
            return 0;

        long half = divisor / 2;
        return value >= 0
            ? (value * multiplier + half) / divisor
            : -((-value * multiplier + half) / divisor);
    }

    private static long FloorDiv(long value, long divisor)
        => value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    private static long CeilingDiv(long value, long divisor)
        => value >= 0 ? (value + divisor - 1) / divisor : -((-value) / divisor);

    private static long SamplesToTicks(long samples, int sampleRate)
        => sampleRate > 0 ? samples * TimeSpan.TicksPerSecond / sampleRate : 0;

    private static long TicksToSamples(long ticks, int sampleRate)
    {
        long half = TimeSpan.TicksPerSecond / 2;
        return ticks >= 0
            ? (ticks * sampleRate + half) / TimeSpan.TicksPerSecond
            : -((-ticks * sampleRate + half) / TimeSpan.TicksPerSecond);
    }

    private void DrainDecoders(CancellationToken ct)
    {
        if (videoDecoder != null)
        {
            videoDecoder.DrainPendingFrames();
            // Continue decoding empty spans to flush
            for (int i = 0; i < 128 && !ct.IsCancellationRequested; i++)
            {
                var frame = videoDecoder.Decode(ReadOnlySpan<byte>.Empty, 0, false);
                if (!frame.HasValue) break;
                var decoded = frame.Value;
                var videoFrame = VideoFrame.TakeOwnership(ref decoded);

                // The window applies to the reorder buffer's tail as well. The frames coming
                // out here are the last ones of the stream in presentation order, so with a
                // -t they are exactly the ones most likely to be past the end.
                if (discardedVideoPts.Count > 0 && discardedVideoPts.Remove(videoFrame.Pts))
                {
                    videoFrame.Dispose();
                    Interlocked.Increment(ref videoFramesDiscarded);
                }
                else if (KeepVideoFrame(videoFrame.Pts))
                {
                    videoChannel.Writer.WriteAsync(videoFrame, ct).AsTask().GetAwaiter().GetResult();
                    Interlocked.Increment(ref videoFramesDecoded);
                }
                else
                {
                    videoFrame.Dispose();
                    Interlocked.Increment(ref videoFramesTrimmed);
                }
            }
        }

        if (audioDecoder != null)
        {
            audioDecoder.DrainPendingFrames();

            // An audio decoder that frames its own input holds the tail of the last packet
            // until something asks for it. Without this the last few frames of every MPEG
            // program stream were simply missing.
            for (int i = 0; i < 128 && !ct.IsCancellationRequested; i++)
            {
                // NoTimestamp, not 0. This loop used to pass a literal zero, so every frame
                // the decoder had been holding claimed to belong at the very start of the
                // file - invisible while nothing read an audio timestamp, and it would have
                // put the tail of every stream on the wrong side of a -ss. There is no new
                // container timestamp here, so the frames carry on from the running clock.
                var decoded = audioDecoder.Decode(
                    ReadOnlySpan<byte>.Empty, DecodedAudioFrame.NoTimestamp);
                if (decoded is null) break;

                EmitAudioFrame(decoded, decoded.SampleCount, ct);
            }
        }
    }

    // ── Demuxer abstraction layer ───────────────────────────────────────
    // Each demuxer has its own packet type. These methods abstract the differences.

    /// <param name="endTrimSamples">
    /// Samples to drop from the end of the frame this packet decodes to, or zero. Only an Ogg
    /// stream's last packet ever asks for it.
    /// </param>
    private bool ReadNextDemuxerPacket(
        out int trackIndex, out long pts, out long dts,
        out bool isKeyframe, out long payloadPosition, out int payloadSize,
        out ReadOnlyMemory<byte> payload, out int endTrimSamples, out bool endOfStream)
    {
        // Every demuxer here puts a real timestamp on every packet; only the program stream
        // leaves them off mid-access-unit, and only it clears this. See the note where it does.
        packetTimestampIsFresh = true;

        // Only a container with an edit list ever sets this, and only the MP4 demuxer reads one.
        packetIsDiscard = false;

        payload = default;
        trackIndex = -1;
        pts = 0;
        dts = 0;
        isKeyframe = false;
        payloadPosition = 0;
        payloadSize = 0;
        endTrimSamples = 0;
        endOfStream = false;

        if (elementaryAudio != null)
        {
            var frame = elementaryAudio.ReadFrame();
            if (frame is null)
            {
                endOfStream = true;
                return false;
            }
            trackIndex = ElementaryAudioTrackIndex;
            pts = frame.Value.PresentationTimestampTicks;
            dts = frame.Value.DecodeTimestampTicks;
            isKeyframe = true; // every frame of these formats is independently decodable
            // The payload travels with the packet, so the decode loop must not seek for it.
            payload = frame.Value.Data;
            payloadSize = payload.Length;
            endTrimSamples = frame.Value.EndTrimSamples;
            return true;
        }

        if (multiTrack != null)
        {
            var frame = multiTrack.ReadFrame();
            if (frame is null)
            {
                endOfStream = true;
                return false;
            }
            trackIndex = frame.Value.TrackId;
            pts = frame.Value.PresentationTimestampTicks;
            dts = frame.Value.DecodeTimestampTicks;
            isKeyframe = frame.Value.IsKeyframe;
            payload = frame.Value.Data;
            payloadSize = payload.Length;
            return true;
        }

        if (elementaryVideo != null)
        {
            var frame = elementaryVideo.ReadFrame();
            if (frame is null)
            {
                endOfStream = true;
                return false;
            }
            trackIndex = ElementaryVideoTrackIndex;
            pts = frame.Value.PresentationTimestampTicks;
            dts = frame.Value.DecodeTimestampTicks;
            isKeyframe = frame.Value.IsKeyframe;
            // The payload travels with the packet, so the decode loop must not seek for it.
            payload = frame.Value.Data;
            payloadSize = payload.Length;
            return true;
        }

        if (mp4Demuxer != null)
        {
            if (!mp4Demuxer.ReadNextPacket(out var pkt))
            {
                endOfStream = true;
                return false;
            }
            trackIndex = pkt.TrackIndex;
            pts = pkt.PresentationTimeTicks;
            dts = pkt.DecodeTimeTicks;
            isKeyframe = pkt.IsKeyframe;
            packetIsDiscard = pkt.IsDiscard;
            payloadPosition = pkt.PayloadPosition;
            payloadSize = pkt.PayloadSize;
            return true;
        }

        if (mkvDemuxer != null)
        {
            if (!mkvDemuxer.ReadNextPacket(out var pkt))
            {
                endOfStream = true;
                return false;
            }
            trackIndex = pkt.TrackNumber;
            pts = pkt.TimestampTicks;
            dts = pkt.TimestampTicks; // Matroska doesn't have separate DTS
            isKeyframe = pkt.IsKeyframe;
            payloadPosition = pkt.PayloadPosition;
            payloadSize = (int)pkt.PayloadSize;
            // Set when the demuxer had to rebuild the frame - a track using Matroska header
            // stripping stores its frames without a prefix that the decoder needs back, so the
            // whole frame exists only in memory.
            if (!pkt.Data.IsEmpty)
                payload = pkt.Data;
            return true;
        }

        if (aviDemuxer != null)
        {
            if (!aviDemuxer.ReadNextPacket(out var pkt))
            {
                endOfStream = true;
                return false;
            }
            trackIndex = pkt.TrackIndex;

            // AVI has no presentation timestamps - avi_read_packet sets pkt->dts and leaves
            // pkt->pts unset, which is what ffprobe prints as N/A for every video packet of an
            // AVI. The decode timestamp is the best a caller can have, and for a stream with
            // no B pictures the two are the same thing.
            pts = pkt.DecodeTimestampTicks;
            dts = pkt.DecodeTimestampTicks;
            isKeyframe = pkt.IsKeyframe;
            payloadPosition = pkt.PayloadPosition;
            payloadSize = pkt.PayloadSize;
            return true;
        }

        if (psDemuxer != null)
        {
            var pkt = psDemuxer.ReadNextPacket();
            if (pkt is null)
            {
                endOfStream = true;
                return false;
            }

            trackIndex = pkt.TrackId;

            // A program stream carries 90 kHz timestamps, and only on the packets that start an
            // access unit; -1 means "same as the one before", which the decoder sees as zero.
            if (pkt.PresentationTimestamp >= 0)
            {
                pts = RescaleToTicks(pkt.PresentationTimestamp, 90_000);
                psLastTimestamp[trackIndex] = pts;
            }
            else
            {
                psLastTimestamp.TryGetValue(trackIndex, out pts);

                // A repeat is not a timestamp. ffmpeg's convention is AV_NOPTS_VALUE and a
                // consumer that carries on from where it was: for audio that is audioNextPts,
                // and for video it is GuessVideoTimestamp below, which is where
                // guess_correct_pts lives in the C as well - in decode.c, after the decoder has
                // returned, not inside the codec.
                //
                // Repeating the previous packet's figure instead is what put an MPEG-2 P
                // picture at 659 ms where ffmpeg has it at 826.8: that picture states no
                // timestamp at all in this file, and the decoder's reorder then carried the
                // wrong one to the wrong frame.
                packetTimestampIsFresh = false;
            }

            dts = pkt.DecodingTimestamp >= 0
                ? RescaleToTicks(pkt.DecodingTimestamp, 90_000) : pts;

            // The demuxer unwraps PES packets into its own buffer, so the payload travels with
            // the packet and the decode loop must not seek the file for it.
            payload = pkt.Data;
            payloadSize = pkt.Data.Length;

            // A program stream marks no keyframes; the video decoder finds its own sequence and
            // group headers. Claiming every packet is one would be a lie a seek would act on.
            isKeyframe = false;
            return true;
        }

        if (asfDemuxer != null)
        {
            if (!asfDemuxer.ReadNextFrame(out var pkt))
            {
                endOfStream = true;
                return false;
            }

            trackIndex = pkt.StreamNumber;
            pts = pkt.PresentationMilliseconds * TimeSpan.TicksPerMillisecond;
            dts = pts;
            isKeyframe = pkt.IsKeyframe;
            payload = pkt.Data;
            payloadSize = pkt.Data.Length;
            return true;
        }

        if (oggDemuxer != null)
        {
            if (!oggDemuxer.ReadNextPacket(out var pkt))
            {
                endOfStream = true;
                return false;
            }

            trackIndex = pkt.StreamIndex;
            int clock = oggSampleClocks.TryGetValue(trackIndex, out int rate) && rate > 0 ? rate : 48000;
            pts = pkt.PresentationSamples * TimeSpan.TicksPerSecond / clock;
            dts = pts;

            // Every Ogg packet is independently decodable for the codecs carried here.
            isKeyframe = true;
            payload = pkt.Data;
            payloadSize = pkt.Data.Length;
            endTrimSamples = pkt.EndTrimSamples;
            return true;
        }

        if (flvDemuxer != null)
        {
            if (!flvDemuxer.ReadNextPacket(out var pkt))
            {
                endOfStream = true;
                return false;
            }

            trackIndex = pkt.TrackIndex;
            dts = pkt.TimestampMs * TimeSpan.TicksPerMillisecond;
            pts = pkt.PresentationTimestampMs * TimeSpan.TicksPerMillisecond;
            isKeyframe = pkt.IsKeyframe;

            // The payload travels with the packet. An FLV file is read strictly forwards, so
            // seeking this same stream to fetch it would leave the demuxer mid-frame.
            payload = pkt.PayloadData;
            payloadSize = pkt.PayloadSize;
            return true;
        }

        if (tsDemuxer != null)
        {
            if (!tsDemuxer.ReadNextPacket(out var pkt))
            {
                endOfStream = true;
                return false;
            }

            trackIndex = pkt.TrackIndex;
            pts = pkt.PresentationTimeTicks;
            dts = pkt.DecodeTimeTicks >= 0 ? pkt.DecodeTimeTicks : pts;

            // TsDemuxer uses -1 for "this PES carried no PTS", so that is absence rather than a
            // timestamp of -1. The distinction matters because a NEGATIVE timestamp is
            // legitimate elsewhere: an Ogg page says only where its last packet ends, so the
            // first packet of a file lands before zero, and an MP4 edit list puts the first
            // decode timestamp before zero too.
            if (pts < 0)
                packetTimestampIsFresh = false;

            payload = pkt.PayloadData;
            payloadSize = pkt.PayloadData.Length;
            isKeyframe = pkt.IsKeyframe;
            return true;
        }

        endOfStream = true;
        return false;
    }

    private long truncatedPackets;

    /// <summary>
    /// Set when a payload read found nothing at all where the index said a chunk was, which
    /// is the real end of a truncated file.
    /// </summary>
    private bool fileEnded;

    /// <summary>
    /// How many packets were shorter on disk than the container's index claimed - which is
    /// what a file cut mid-chunk looks like. Zero for a whole file.
    /// </summary>
    public long TruncatedPacketCount => Interlocked.Read(ref truncatedPackets);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> ReadPayloadIntoBuffer(long position, int size)
    {
        // Grow buffer if needed
        if (size > readBuffer.Length)
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            readBuffer = ArrayPool<byte>.Shared.Rent(size);
        }

        fileStream.Position = position;
        int totalRead = 0;
        while (totalRead < size)
        {
            int bytesRead = fileStream.Read(readBuffer, totalRead, size - totalRead);
            if (bytesRead == 0)
                break;
            totalRead += bytesRead;
        }

        // A SHORT READ IS A TRUNCATED FILE, not a fault in the index: the sample table says
        // how big the chunk was when the file was whole, and a file cut mid-chunk - which
        // every "-partial" conformance sample is, and every interrupted recording - still
        // has a decodable prefix. ffmpeg truncates the packet here ("Truncating packet of
        // size %d to %d" in av_get_packet) and decodes what there is. Throwing instead meant
        // one truncated chunk ended playback of the whole file, which is what the Cinepak
        // pipeline gate found.
        //
        // NOTHING AT ALL AT THIS POSITION IS A DIFFERENT THING and has to end the demux loop,
        // not be handed on as an empty packet: the index of a truncated file goes on naming
        // chunks long past the end of it, and that sample names 371,011 more. Reading them
        // all as empty packets finishes - which is the worse failure of the two, because it
        // looks like success.
        //
        // The truncation count is exposed rather than swallowed, because a resilience path
        // that repairs a real fault silently is worse than the fault: a gate can assert on
        // it.
        if (totalRead == 0 && size > 0)
            fileEnded = true;
        else if (totalRead < size)
            Interlocked.Increment(ref truncatedPackets);

        return new ReadOnlySpan<byte>(readBuffer, 0, totalRead);
    }

    private void SeekDemuxer(double timestampMs)
    {
        if (mp4Demuxer != null) mp4Demuxer.Seek(timestampMs);
        else if (mkvDemuxer != null) mkvDemuxer.Seek(timestampMs);
        else if (psDemuxer != null) psDemuxer.Seek((long)(timestampMs * 1000.0));
        else if (tsDemuxer != null) tsDemuxer.Seek(timestampMs);
        else if (flvDemuxer != null) flvDemuxer.Seek(timestampMs);
        else if (oggDemuxer != null) SeekOgg(timestampMs);
        else if (asfDemuxer != null) asfDemuxer.SeekToTime((long)(timestampMs * TimeSpan.TicksPerMillisecond));
        // AVI seeking handled differently — omit for now
    }

    // ── Track discovery ─────────────────────────────────────────────────

    private void DiscoverTracks()
    {
        if (mp4Demuxer != null) DiscoverMp4Tracks();
        else if (mkvDemuxer != null) DiscoverMkvTracks();
        else if (aviDemuxer != null) DiscoverAviTracks();
        else if (psDemuxer != null) DiscoverMpegPsTracks();
        else if (tsDemuxer != null) DiscoverTsTracks();
        else if (flvDemuxer != null) DiscoverFlvTracks();
        else if (oggDemuxer != null) DiscoverOggTracks();
        else if (asfDemuxer != null) DiscoverAsfTracks();
        else if (elementaryAudio != null) DiscoverElementaryAudioTrack();
        else if (elementaryVideo != null) DiscoverElementaryVideoTrack();
        else if (multiTrack != null) trackInfos.AddRange(multiTrack.Tracks);

        // Set total duration
        double durationMs = 0;
        if (mp4Demuxer != null) durationMs = mp4Demuxer.DurationMilliseconds;
        else if (mkvDemuxer != null) durationMs = mkvDemuxer.DurationMilliseconds;
        // AviDemuxer has computed this from the main header's frame count and frame interval
        // since it was written; nothing asked it for the figure, so every .avi opened with a
        // duration of zero and a seek bar with nothing to seek within.
        else if (aviDemuxer != null) durationMs = aviDemuxer.DurationMilliseconds;
        else if (psDemuxer != null) durationMs = psDemuxer.DurationMicroseconds / 1000.0;
        else if (tsDemuxer != null) durationMs = tsDemuxer.DurationMicroseconds / 1000.0;
        else if (flvDemuxer != null) durationMs = flvDemuxer.DurationMilliseconds;
        else if (oggDemuxer != null) durationMs = oggDemuxer.DurationMilliseconds;
        else if (asfDemuxer != null) durationMs = asfTotalMilliseconds;
        else if (elementaryAudio != null) durationMs = elementaryAudio.Duration.TotalMilliseconds;
        else if (elementaryVideo != null) durationMs = elementaryVideo.Duration.TotalMilliseconds;
        else if (multiTrack != null) durationMs = multiTrack.Duration.TotalMilliseconds;

        totalDurationTicks = (long)(durationMs * TimeSpan.TicksPerMillisecond);
        streamStartTicks = DiscoverStreamStart();

        // The MPEG containers state 90 kHz timestamps; every other demuxer here hands this
        // pipeline ticks, and a tick is fine enough that a timestamp in them stands for one
        // sample position and not an interval.
        containerTimestampRate = psDemuxer != null || tsDemuxer != null
            ? 90_000
            : TimeSpan.TicksPerSecond;
    }

    /// <summary>
    /// <c>AVFormatContext.start_time</c>: the timestamp the earliest stream begins at, which is
    /// not always zero.
    /// </summary>
    /// <remarks>
    /// <c>-ss</c> counts from the start of the stream, not from timestamp zero, which is why
    /// ffmpeg adds this to the figure before it seeks or trims:
    /// <code>
    /// timestamp = (start_time == AV_NOPTS_VALUE) ? 0 : start_time;
    /// /* add the stream start time */
    /// if (!o->seek_timestamp &amp;&amp; ic->start_time != AV_NOPTS_VALUE)
    ///     timestamp += ic->start_time;                    // ffmpeg_demux.c:2033
    /// </code>
    /// <para>
    /// An MPEG program stream is where this shows: <c>Video-MPG-MPG.mpg</c> starts at 0.660 s,
    /// a perfectly ordinary thing for a captured stream to do, and <c>-ss 20</c> on it was
    /// taking the audio from 19.340 s - the right length of window, the right content for the
    /// timestamp asked for, and 0.66 s away from what ffmpeg gives. Containers that start at
    /// zero are unaffected, which is all of the others here: an MP4's edit list puts the first
    /// presented frame at zero by construction. A transport stream is the other one that needs
    /// it, and needs it harder: ffmpeg's own muxer offsets the first timestamp, so remuxing
    /// this same clip with <c>-c copy</c> produces a file starting at 1.433 s. The demuxer
    /// finds its own start time there (<c>TsDemuxer.ProbeTimings</c>) because a transport
    /// stream has no header to state one.
    /// </para>
    /// </remarks>
    private long DiscoverStreamStart()
    {
        // ffmpeg takes the minimum over the streams that have one.
        long earliest = long.MaxValue;

        if (psDemuxer != null)
        {
            foreach (var track in psDemuxer.Tracks.Values)
            {
                if (track.FirstPresentationTimestamp >= 0)
                    earliest = Math.Min(earliest, track.FirstPresentationTimestamp);
            }
        }
        else if (tsDemuxer != null)
        {
            if (tsDemuxer.StartTimestamp >= 0)
                earliest = tsDemuxer.StartTimestamp;
        }
        else
        {
            return 0;
        }

        // 90 kHz, as every MPEG system timestamp is.
        return earliest == long.MaxValue ? 0 : RescaleToTicks(earliest, 90_000);
    }

    /// <summary>
    /// An elementary stream has no track table - it is one audio track by definition.
    /// </summary>
    private void DiscoverElementaryAudioTrack()
    {
        trackInfos.Add(new DemuxerTrackInfo
        {
            TrackId = ElementaryAudioTrackIndex,
            TrackType = DemuxerTrackType.Audio,
            CodecId = elementaryAudio!.CodecId,
            IsDefault = true,
            SampleRate = elementaryAudio.SampleRate,
            ChannelCount = elementaryAudio.ChannelCount,
            BlockAlign = elementaryAudio.BlockAlign,
            BitsPerCodedSample = elementaryAudio.BitsPerCodedSample,
            // FLAC's decoder needs the file's header chain to know the channel count and bit
            // depth; a FLAC frame header can leave all three of those as "see STREAMINFO".
            // CAF carries the same kind of thing for ALAC and Opus in its magic cookie.
            CodecPrivateData = elementaryAudio.CodecPrivate,
        });
    }

    /// <summary>
    /// An elementary video stream has no track table either - it is one video track by
    /// definition.
    /// </summary>
    private void DiscoverElementaryVideoTrack()
    {
        trackInfos.Add(new DemuxerTrackInfo
        {
            TrackId = ElementaryVideoTrackIndex,
            TrackType = DemuxerTrackType.Video,
            CodecId = elementaryVideo!.CodecId,
            IsDefault = true,
            Width = elementaryVideo.Width,
            Height = elementaryVideo.Height,
            FrameRate = elementaryVideo.FrameRate,
            // An RCV file's four bytes of sequence header, which VC-1 Simple and Main
            // Profile have nowhere else; and the Y4M fourcc, which is the only thing that
            // says what raw bytes are.
            CodecPrivateData = elementaryVideo.CodecPrivate,
            CodecTag = elementaryVideo.CodecTag,
        });
    }

    private void DiscoverMp4Tracks()
    {
        for (int i = 0; i < mp4Demuxer!.Tracks.Count; i++)
        {
            var track = mp4Demuxer.Tracks[i];
            double frameRate = 0;
            if (track.Width > 0 && track.Height > 0 && track.MediaDuration > 0 && track.SampleCount > 0)
            {
                // Derive frame rate from duration and sample count
                double durationSeconds = (double)track.MediaDuration / track.Timescale;
                frameRate = track.SampleCount / durationSeconds;
            }
            var info = new DemuxerTrackInfo
            {
                TrackId = i,
                TrackType = MapMp4TrackType(track),
                // GetCodecName resolves the sample entry the way mov.c does - a raw PCM
                // entry says its width in a 'pcmC' box or in the tag itself - and hands
                // out FFmpeg's own names. The four-character code alone names no decoder.
                CodecId = track.GetCodecName(),
                IsDefault = i == 0,
                CodecPrivateData = track.CodecPrivate,
                Width = track.Width,
                Height = track.Height,
                FrameRate = frameRate,
                SampleRate = (int)track.AudioSampleRate,
                ChannelCount = track.AudioChannels,
                BitsPerSample = track.AudioBitsPerSample,
                // A paletted QuickTime track's colour table, and the depth that selected it.
                BitsPerCodedSample = track.BitsPerCodedSample,
                Palette = track.Palette,
                // The sample entry's own type. rawdec.c reads 'raw ' and 'NO16' before it
                // reads anything else, and sends their depth through QuickTime's table
                // rather than AVI's - the two disagree about 16, 24 and 32 bits.
                //
                // BYTE ORDER: an ISO box type is read big-endian, so Mp4BoxType.RawPcm is
                // 0x72617720 with 'r' in the HIGH byte, while MKTAG - which is what every
                // codec_tag comparison in ffmpeg is written against, and what an AVI's
                // biCompression already is - puts 'r' in the low byte. Carrying it
                // unreversed makes 'raw ' arrive as ' war' and match nothing.
                CodecTag = System.Buffers.Binary.BinaryPrimitives
                    .ReverseEndianness(track.CodecFourCC),
                IsQuickTime = true,
            };
            trackInfos.Add(info);
        }
    }

    private void DiscoverMkvTracks()
    {
        for (int i = 0; i < mkvDemuxer!.Tracks.Count; i++)
        {
            var track = mkvDemuxer.Tracks[i];
            var info = new DemuxerTrackInfo
            {
                TrackId = track.TrackNumber,
                TrackType = track.TrackType switch
                {
                    MatroskaTrackType.Video => DemuxerTrackType.Video,
                    MatroskaTrackType.Audio => DemuxerTrackType.Audio,
                    MatroskaTrackType.Subtitle => DemuxerTrackType.Subtitle,
                    _ => DemuxerTrackType.Unknown,
                },
                // matroska.c maps the codec id string to a codec, and for PCM the width
                // comes from BitDepth rather than the string; the raw id names no decoder.
                CodecId = track.GetCodecFourCC(),
                Name = track.Name,
                Language = track.Language,
                IsDefault = track.IsDefault,
                CodecPrivateData = track.CodecPrivate,
                Width = track.VideoWidth,
                Height = track.VideoHeight,
                FrameRate = track.DefaultDurationNs > 0 ? 1_000_000_000.0 / track.DefaultDurationNs : 0,
                SampleRate = (int)track.AudioSampleRate,
                ChannelCount = track.AudioChannels,
                BitsPerSample = track.AudioBitDepth,
            };
            trackInfos.Add(info);
        }
    }

    private void DiscoverAviTracks()
    {
        for (int i = 0; i < aviDemuxer!.Tracks.Count; i++)
        {
            var track = aviDemuxer.Tracks[i];
            var info = new DemuxerTrackInfo
            {
                TrackId = i,
                TrackType = track.TrackType switch
                {
                    AviTrackType.Video => DemuxerTrackType.Video,
                    AviTrackType.Audio => DemuxerTrackType.Audio,
                    AviTrackType.Subtitle => DemuxerTrackType.Subtitle,
                    _ => DemuxerTrackType.Unknown,
                },
                // GetCodecName is ff_codec_bmp_tags for video and whatever ff_get_wav_header
                // resolved for audio. The raw biCompression this used to hand out names no
                // decoder, and an audio track has no four-character code at all.
                CodecId = track.GetCodecName(),
                Width = track.Width,
                Height = track.Height,
                SampleRate = (int)track.AudioSampleRate,
                ChannelCount = track.AudioChannels,
                BitsPerSample = track.AudioBitsPerSample,
                // Raw PCM and ADPCM carry no configuration of their own, so the WAVEFORMAT's
                // block align and width are the only things that say how to read the bytes.
                BlockAlign = track.AudioBlockAlign,
                // For a video track this is biBitCount, which with biCompression zero is
                // the only thing that names the pixel format.
                BitsPerCodedSample = track.TrackType == AviTrackType.Video
                    ? track.BitsPerPixel
                    : track.AudioBitsPerSample,
                CodecTag = track.TrackType == AviTrackType.Video ? track.FourCC : 0,
                CodecPrivateData = track.CodecPrivate,
            };
            trackInfos.Add(info);
        }
    }

    /// <summary>
    /// Builds the track list for a program stream, whose tracks are keyed by their PES stream
    /// id rather than numbered from zero - which is why the id is carried through rather than
    /// the position in the list.
    /// </summary>
    private void DiscoverMpegPsTracks()
    {
        foreach (var entry in psDemuxer!.Tracks)
        {
            var track = entry.Value;
            var info = new DemuxerTrackInfo
            {
                TrackId = entry.Key,
                TrackType = track.Type switch
                {
                    Demuxers.TrackType.Video => DemuxerTrackType.Video,
                    Demuxers.TrackType.Audio => DemuxerTrackType.Audio,
                    Demuxers.TrackType.Subtitle => DemuxerTrackType.Subtitle,
                    _ => DemuxerTrackType.Unknown,
                },
                CodecId = track.Codec switch
                {
                    Demuxers.CodecType.Mpeg1Video or Demuxers.CodecType.Mpeg2Video => "mpeg2video",
                    Demuxers.CodecType.H264 => "h264",
                    Demuxers.CodecType.Hevc => "hevc",
                    // Layers I, II and III all arrive labelled this way, and the layer is
                    // in every frame header, so one decoder takes all three.
                    Demuxers.CodecType.MpegAudio => "mp2",
                    Demuxers.CodecType.Ac3 => "ac3",
                    Demuxers.CodecType.Eac3 => "eac3",
                    Demuxers.CodecType.Aac => "aac",
                    Demuxers.CodecType.Dts => "dts",
                    // mpeg.c names a private_stream_1 LPCM substream AV_CODEC_ID_PCM_DVD.
                    // "pcm" was not a codec id anywhere - pcm.c declares one codec per
                    // sample format and the registry is keyed by those - so this named a
                    // decoder that cannot exist. There is no pcm_dvd decoder here either,
                    // but the track is now named what it is and skipped, rather than named
                    // something imaginary and skipped.
                    Demuxers.CodecType.Lpcm => "pcm_dvd",
                    // Anything else keeps a name no decoder answers to, so the track is listed
                    // and skipped rather than silently decoded as something it is not.
                    _ => track.Codec.ToString().ToLowerInvariant(),
                },
                Width = track.VideoWidth,
                Height = track.VideoHeight,
                SampleRate = track.AudioSampleRate,
                ChannelCount = track.AudioChannels,
                BitsPerSample = track.AudioBitsPerSample,
            };
            trackInfos.Add(info);
        }
    }

    /// <summary>
    /// A transport stream names its tracks by PMT stream type. The names the demuxer hands out
    /// are its own ("mpeg2", "mp3"); they are translated here to the codec ids CreateDecoders
    /// answers to, the same way the program stream's are.
    /// </summary>
    private void DiscoverTsTracks()
    {
        foreach (var track in tsDemuxer!.Tracks)
        {
            trackInfos.Add(new DemuxerTrackInfo
            {
                TrackId = trackInfos.Count,
                TrackType = track.TrackType switch
                {
                    TsTrackType.Video => DemuxerTrackType.Video,
                    TsTrackType.Audio => DemuxerTrackType.Audio,
                    TsTrackType.Subtitle => DemuxerTrackType.Subtitle,
                    _ => DemuxerTrackType.Unknown,
                },
                CodecId = track.CodecName switch
                {
                    // One decoder reads both, and the sequence header says which.
                    "mpeg1" or "mpeg2" => "mpeg2video",
                    // Layers I, II and III all arrive under stream type 3 or 4, and the layer is
                    // in every frame header, so one decoder takes all three.
                    "mp3" => "mp2",
                    // Anything else keeps the demuxer's name. A name no decoder answers to leaves
                    // the track listed and skipped, rather than decoded as something it is not.
                    string other => other,
                },
                Language = track.Language,
            });
        }
    }

    /// <summary>
    /// An FLV file carries at most one audio and one video track, named by the four-bit codec
    /// field in the first byte of every tag. The configuration record each track's first tag
    /// carried becomes its codec private data, which is what an H.264 or AAC decoder is
    /// initialised from.
    /// </summary>
    private void DiscoverFlvTracks()
    {
        for (int i = 0; i < flvDemuxer!.Tracks.Count; i++)
        {
            var track = flvDemuxer.Tracks[i];
            trackInfos.Add(new DemuxerTrackInfo
            {
                TrackId = i,
                TrackType = track.TrackType switch
                {
                    FlvTrackType.Video => DemuxerTrackType.Video,
                    FlvTrackType.Audio => DemuxerTrackType.Audio,
                    _ => DemuxerTrackType.Unknown,
                },
                // GetCodecName hands out FFmpeg's own names ("h264", "aac", "mp3"), and a codec
                // with no decoder keeps a name nothing answers to rather than being decoded as
                // something it is not.
                CodecId = track.GetCodecName(),
                CodecPrivateData = track.CodecPrivateData,
                Width = track.Width,
                Height = track.Height,
                SampleRate = track.AudioSampleRate,
                ChannelCount = track.AudioChannels,
                BitsPerSample = track.AudioBitsPerSample,
            });
        }
    }

    /// <summary>
    /// An Ogg file is a bundle of logical streams, each named by the magic at the start of its
    /// first packet.
    /// </summary>
    /// <remarks>
    /// Only Vorbis and Opus are named here as codecs a decoder answers to. The rest are listed
    /// under a name nothing matches, so the track is visible and skipped rather than decoded as
    /// something it is not - Ogg FLAC in particular wraps its STREAMINFO in a mapping header, so
    /// handing that blob to the FLAC decoder as though it came out of a .flac file would be a
    /// guess, and there is no sample here to settle it.
    /// </remarks>
    private void DiscoverOggTracks()
    {
        foreach (var stream in oggDemuxer!.Streams)
        {
            oggSampleClocks[stream.StreamIndex] = OggDemuxer.SampleClock(stream);

            trackInfos.Add(new DemuxerTrackInfo
            {
                TrackId = stream.StreamIndex,
                TrackType = stream.CodecType switch
                {
                    OggCodecType.Vorbis or OggCodecType.Opus or OggCodecType.Flac
                        or OggCodecType.Speex => DemuxerTrackType.Audio,
                    OggCodecType.Theora or OggCodecType.Vp8 or OggCodecType.Daala
                        => DemuxerTrackType.Video,
                    _ => DemuxerTrackType.Unknown,
                },
                CodecId = stream.CodecType switch
                {
                    OggCodecType.Vorbis => "vorbis",
                    OggCodecType.Opus => "opus",
                    OggCodecType other => "ogg-" + other.ToString().ToLowerInvariant(),
                },
                CodecPrivateData = OggCodecPrivate(stream),
                SampleRate = (int)stream.SampleRate,
                ChannelCount = stream.Channels,
                Width = stream.Width,
                Height = stream.Height,
            });
        }
    }

    /// <summary>
    /// The extradata the decoder expects, which is not the same shape as the header packets
    /// came in.
    /// </summary>
    /// <remarks>
    /// A Vorbis decoder is initialised from the three header packets Xiph-laced together, the
    /// form a Matroska file stores them in and the form the adapter already reads. Opus is
    /// initialised from its identification header alone.
    /// </remarks>
    private static byte[] OggCodecPrivate(OggStream stream)
    {
        if (stream.CodecType == OggCodecType.Opus)
            return stream.HeaderPackets.Count > 0 ? stream.HeaderPackets[0] : [];

        if (stream.CodecType != OggCodecType.Vorbis || stream.HeaderPackets.Count < 3)
            return [];

        byte[] ident = stream.HeaderPackets[0];
        byte[] comment = stream.HeaderPackets[1];
        byte[] setup = stream.HeaderPackets[2];

        // [2][ident length as a run of 255s then the remainder][comment length the same][data]
        int laceLength = 1 + ident.Length / 255 + 1 + comment.Length / 255 + 1;
        var laced = new byte[laceLength + ident.Length + comment.Length + setup.Length];

        int at = 0;
        laced[at++] = 2;
        at = WriteXiphLength(laced, at, ident.Length);
        at = WriteXiphLength(laced, at, comment.Length);
        ident.CopyTo(laced, at);
        at += ident.Length;
        comment.CopyTo(laced, at);
        at += comment.Length;
        setup.CopyTo(laced, at);
        return laced;

        static int WriteXiphLength(byte[] buffer, int at, int length)
        {
            while (length >= 255)
            {
                buffer[at++] = 255;
                length -= 255;
            }
            buffer[at++] = (byte)length;
            return at;
        }
    }

    /// <summary>
    /// Ogg seeking is a binary search over page granule positions, so it needs the stream whose
    /// clock the target is in.
    /// </summary>
    private void SeekOgg(double timestampMs)
    {
        if (oggDemuxer!.Streams.Count == 0)
            return;

        var stream = oggDemuxer.Streams[0];
        int clock = OggDemuxer.SampleClock(stream);
        if (clock <= 0)
            return;

        oggDemuxer.SeekToGranule(stream.SerialNumber, (long)(timestampMs * clock / 1000.0));
    }

    private double asfTotalMilliseconds;

    /// <summary>
    /// ASF names a codec by its Windows registration: a two-byte wave format tag for audio, a
    /// four character code for video.
    /// </summary>
    /// <remarks>
    /// Only the ones with a decoder here are named as such. Windows Media Audio and Windows
    /// Media Video, which is what an ordinary .wmv or .wma holds, keep a name nothing answers
    /// to - the track is listed and skipped rather than decoded as something it is not, and
    /// the file opens so a caller can say what it contains.
    /// </remarks>
    private void DiscoverAsfTracks()
    {
        var info = asfDemuxer!.Parse();
        asfTotalMilliseconds = info.PlayDuration / (double)TimeSpan.TicksPerMillisecond
                               - info.PrerollMilliseconds;

        foreach (var track in info.Streams)
        {
            bool isVideo = track.Type == AsfStreamType.Video;

            trackInfos.Add(new DemuxerTrackInfo
            {
                TrackId = track.StreamNumber,
                TrackType = track.Type switch
                {
                    AsfStreamType.Video => DemuxerTrackType.Video,
                    AsfStreamType.Audio => DemuxerTrackType.Audio,
                    _ => DemuxerTrackType.Unknown,
                },
                CodecId = isVideo ? AsfVideoCodecId(track.CodecId)
                                  : AsfAudioCodecId(track.CodecId, track.BitsPerSample),
                Width = track.Width,
                Height = track.Height,
                SampleRate = track.SampleRate,
                ChannelCount = track.Channels,
                BitsPerSample = track.BitsPerSample,

                // For audio this is the whole WAVEFORMATEX, not just the extradata after it.
                // WMA needs the bit rate and the block alignment that live in its fixed part:
                // ff_wma_init derives the band layout, the coded range and the coefficient code
                // set from the bit rate, and none of that is in the codec-specific blob. It is
                // also how Matroska stores WMA, in an A_MS/ACM track.
                //
                // For video it is what follows the BITMAPINFOHEADER, which is where a WMV3 or
                // VC-1 sequence header lives. This was a flat null until VC-1 landed, because
                // no video codec reachable from ASF had needed it - and the failure it caused
                // was silent in both halves: Vc1VideoDecoder accepts empty extradata and waits
                // for an in-band sequence header, which a WMV3 packet never carries, so the
                // pipeline reported a video decoder, called it 6681 times for a seven-minute
                // clip, and emitted no frames at all.
                CodecPrivateData = isVideo
                    ? (track.CodecPrivate.IsEmpty ? null : track.CodecPrivate.ToArray())
                    : (track.TypeSpecificData.IsEmpty ? null : track.TypeSpecificData.ToArray()),
            });
        }
    }

    /// <summary>The wave format tag in a stream's WAVEFORMATEX.</summary>
    /// <summary>
    /// asfdec_f.c hands an audio stream's type-specific data to <c>ff_get_wav_header</c>, so
    /// the tag is resolved out of <c>ff_codec_wav_tags</c> - the same 84-row table AVI and WAV
    /// use - rather than out of a list of the tags somebody had met.
    /// </summary>
    /// <remarks>
    /// What this replaces named six tags and called everything else "asf-audio-xxxx". One of
    /// its six, 0x1601, is in no table in ffmpeg; it is dropped here rather than kept as a
    /// guess, which is the same reasoning that deleted the reconstructions in Phase 7. A tag
    /// the table does not have keeps a traceable name and finds no decoder.
    /// </remarks>
    private static string AsfAudioCodecId(uint waveFormatTag, int bitsPerSample)
        => Demuxers.RiffTags.WavCodecGetId(waveFormatTag, bitsPerSample)
           ?? $"asf-audio-{waveFormatTag:x4}";

    /// <summary>The four character code in a stream's BITMAPINFOHEADER.</summary>
    /// <summary>
    /// asfdec_f.c's video arm: <c>ff_codec_get_id(ff_codec_bmp_tags, tag1)</c>, then the
    /// unofficial table - the same two AVI uses, since both carry a BITMAPINFOHEADER.
    /// </summary>
    /// <remarks>
    /// What this replaced knew H.264 and HEVC and prefixed everything else with
    /// "asf-video-", so an ordinary .wmv reported "asf-video-wmv3". The prefix was there to
    /// make sure no decoder answered to it; nothing answers to "wmv3" either, and a track
    /// named what it is can be reported to a user as an unsupported codec rather than as a
    /// string nobody has seen before.
    /// </remarks>
    private static string AsfVideoCodecId(uint fourCc)
    {
        string? id = Demuxers.RiffTags.CodecGetId(Demuxers.RiffTags.CodecBmpTags, fourCc)
                  ?? Demuxers.RiffTags.CodecGetId(Demuxers.RiffTags.CodecBmpTagsUnofficial, fourCc);
        if (id is not null)
            return id;

        Span<char> code = stackalloc char[4];
        for (int i = 0; i < 4; i++)
            code[i] = (char)((fourCc >> (8 * i)) & 0xFF);
        return new string(code).ToLowerInvariant();
    }

    // ── Decoder creation ────────────────────────────────────────────────

    private void CreateDecoders()
    {
        // Select tracks to decode
        SelectTracks();

        // Create video decoder
        if (videoTrackIndex >= 0)
        {
            var videoTrack = FindTrackInfo(videoTrackIndex);
            if (videoTrack != null)
            {
                videoDecoder = CreateVideoDecoder(videoTrack);
            }
        }

        // Create audio decoder
        if (audioTrackIndex >= 0)
        {
            var audioTrack = FindTrackInfo(audioTrackIndex);
            if (audioTrack != null)
            {
                audioDecoder = CreateAudioDecoder(audioTrack);
            }
        }
    }

    private void SelectTracks()
    {
        if (options.SelectedTrackIndices != null)
        {
            foreach (int idx in options.SelectedTrackIndices)
            {
                var track = FindTrackInfo(idx);
                if (track == null) continue;
                if (track.TrackType == DemuxerTrackType.Video && videoTrackIndex < 0)
                    videoTrackIndex = idx;
                else if (track.TrackType == DemuxerTrackType.Audio && audioTrackIndex < 0)
                    audioTrackIndex = idx;
            }
        }
        else
        {
            // Auto-select: first video + first audio
            foreach (var track in trackInfos)
            {
                if (track.TrackType == DemuxerTrackType.Video && videoTrackIndex < 0)
                    videoTrackIndex = track.TrackId;
                else if (track.TrackType == DemuxerTrackType.Audio && audioTrackIndex < 0)
                    audioTrackIndex = track.TrackId;
            }
        }
    }

    private DemuxerTrackInfo? FindTrackInfo(int trackIndex)
    {
        foreach (var t in trackInfos)
            if (t.TrackId == trackIndex) return t;
        return null;
    }

    private IVideoDecoder? CreateVideoDecoder(DemuxerTrackInfo track)
    {
        string codecId = NormalizeCodecId(track.CodecId);

        // Use the codec registry's built-in lookup
        var decoder = CodecRegistry.CreateDecoder(codecId) as IVideoDecoder;
        if (decoder == null) return null;

        if (options.VideoFilterThreads > 0 && decoder is Codecs.H265.HevcDecoder hevc)
            hevc.SaoWorkerThreads = options.VideoFilterThreads;

        ConfigureVideoDecoder(decoder, track);

        byte[] codecPrivate = track.CodecPrivateData ?? [];
        if (!decoder.Initialize(codecPrivate))
        {
            decoder.Dispose();
            return null;
        }

        return decoder;
    }

    /// <summary>
    /// Hands a decoder the configuration its bitstream does not carry, before it is
    /// initialised.
    /// </summary>
    /// <remarks>
    /// VC-1 is the only one so far. A Simple or Main Profile stream codes its picture size
    /// nowhere - not in the frame headers and not in the sequence header the extradata holds -
    /// so the container's size is the only one there is, and the layout of the extradata is
    /// decided by the container's tag rather than by its bytes. FFmpeg reads both off
    /// AVCodecContext, which this project's decoder interface has no equivalent of.
    /// </remarks>
    private static void ConfigureVideoDecoder(IVideoDecoder decoder, DemuxerTrackInfo track)
    {
        if (decoder is Codecs.Mjpeg.MjpegVideoDecoder mjpeg)
        {
            // An interlaced Motion JPEG's images are single FIELDS and say so nowhere; the
            // container's height is twice what SOF declares, and that is the only tell.
            mjpeg.CodedHeight = track.Height;

            // And the fourcc decides the FIELD ORDER when the container declares none,
            // which is ffmpeg ticket 3229: an MJPG tag with no stated order means bottom
            // field first. The FrameCrc harness has set this since the Motion JPEG work and
            // this did not, so the gated path and the played path disagreed about which
            // field came first on exactly the files the ticket is about.
            mjpeg.CodecTag = track.CodecTag;
            return;
        }

        // The rest of the MJPEG family, which needs the same thing for the same reason -
        // their packets carry no SOF at all, so the picture size exists only in the
        // container. All three were configured by the FrameCrc harness and by nothing here,
        // so they were bit-exact in their own gates and produced NO FRAMES through the
        // pipeline: Media 100 is how the playability sweep found this.
        if (decoder is Codecs.Mjpeg.MjpegbVideoDecoder mjpegb)
        {
            mjpegb.CodedHeight = track.Height;
            return;
        }

        if (decoder is Codecs.Mjpeg.AmvVideoDecoder amv)
        {
            amv.CodedWidth = track.Width;
            amv.CodedHeight = track.Height;
            return;
        }

        if (decoder is Codecs.Mjpeg.Media100VideoDecoder media100)
        {
            media100.CodedWidth = track.Width;
            media100.CodedHeight = track.Height;
            return;
        }

        if (decoder is Codecs.H264.H264Decoder h264)
        {
            // init_dimensions' container cropping arm: an H.264 SPS can describe a coded
            // picture taller than the container's visual size - 1088 where the container
            // says 1080 - with no cropping of its own, and then the container is the only
            // thing that says where the picture ends.
            h264.ContainerWidth = track.Width;
            h264.ContainerHeight = track.Height;
            return;
        }

        if (decoder is Codecs.RawVideo.RawVideoDecoder raw)
        {
            // An uncompressed track carries NOTHING in band - no header, no start code, no
            // sync word - so the fourcc, the depth and the geometry are the whole of its
            // configuration, and a wrong one decodes a plausible wrong picture.
            raw.CodedWidth = track.Width;
            raw.CodedHeight = track.Height;
            raw.CodecTag = track.CodecTag;
            raw.BitsPerCodedSample = track.BitsPerCodedSample;
            raw.IsQuickTime = track.IsQuickTime;
            return;
        }

        if (decoder is Codecs.Cinepak.CinepakVideoDecoder cinepak)
        {
            // Cinepak was bit-exact over all four FATE references and could not be reached
            // THROUGH THIS METHOD: it is registered, but nothing configured it, so
            // Initialize returned false for want of a width and CreateVideoDecoder handed
            // back null. A Cinepak frame header carries strip coordinates and not the
            // picture size, and a paletted track's colours are in the sample description.
            cinepak.CodedWidth = track.Width;
            cinepak.CodedHeight = track.Height;
            cinepak.BitsPerCodedSample = track.BitsPerCodedSample;
            cinepak.Palette = track.Palette;
            return;
        }

        if (decoder is not Codecs.Vc1Video.Vc1VideoDecoder vc1)
            return;

        vc1.CodedWidth = track.Width;
        vc1.CodedHeight = track.Height;
        vc1.StreamCodecId = track.CodecId.ToLowerInvariant() is "wmv3" or "wmv9"
            ? Codecs.Vc1Video.Vc1CodecId.Wmv3
            : Codecs.Vc1Video.Vc1CodecId.Vc1;
    }

    private IAudioDecoder? CreateAudioDecoder(DemuxerTrackInfo track)
    {
        string codecId = NormalizeCodecId(track.CodecId);

        // Use the codec registry's built-in lookup
        var decoder = CodecRegistry.CreateDecoder(codecId) as IAudioDecoder;
        if (decoder == null) return null;

        // Raw PCM carries no configuration of its own; see IAudioDecoder.ConfigureAudio.
        decoder.ConfigureAudio(
            track.SampleRate, track.ChannelCount, track.BlockAlign, track.BitsPerCodedSample);

        byte[] codecPrivate = track.CodecPrivateData ?? [];
        if (decoder.Initialize(codecPrivate))
            return decoder;

        decoder.Dispose();
        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a track this demuxer named <paramref name="codecId"/> has a decoder here.
    /// </summary>
    /// <remarks>
    /// This is the registry lookup the pipeline itself does, name mapping included, exposed
    /// because a player needs to know before it opens anything and because a GATE needs it:
    /// three decoders in this tree have been bit-exact and unreachable, and the only way to
    /// catch the fourth is to ask "which tracks do we claim to decode" and then check that
    /// each one actually produced frames. Answering it with a second copy of the mapping
    /// would answer a different question - see the note on a mapper nothing calls.
    /// </remarks>
    public static bool CanDecode(string codecId) =>
        !string.IsNullOrEmpty(codecId)
        && Codecs.CodecRegistry.GetBestCodec(NormalizeCodecId(codecId)) is not null;

    private static string NormalizeCodecId(string codecId)
    {
        string lower = codecId.ToLowerInvariant();

        // Every pcm_* name is already a registry key, one per sample format, exactly as
        // pcm.c declares one codec per format. Mapping them to a single "pcm" would throw
        // away the one thing that says how to read the bytes.
        if (lower.StartsWith("pcm_", StringComparison.Ordinal)) return lower;

        // Map container-specific codec IDs to canonical names
        return lower switch
        {
            // H.264 variants
            "avc1" or "avc3" or "h264" or "h.264" or "v_mpeg4/iso/avc" => "h264",
            // HEVC variants
            "hev1" or "hvc1" or "hevc" or "h265" or "h.265" or "v_mpegh/iso/hevc" => "hevc",
            // AAC variants
            "mp4a" or "aac" or "a_aac" or "a_aac/mpeg4/lc" or "a_aac/mpeg2/lc" => "aac",
            // MP3
            "mp3" or ".mp3" or "a_mpeg/l3" => "mp3",
            // Opus
            "opus" or "a_opus" => "opus",
            // Vorbis
            "vorbis" or "a_vorbis" => "vorbis",
            // FLAC
            "flac" or "a_flac" => "flac",
            // AC-3
            "ac3" or "ac-3" or "a_ac3" or "a_ac3/bsid9" or "a_ac3/bsid10" => "ac3",
            // DTS. "dtse" is deliberately absent: an MP4 DTS Express sample entry is LBR with
            // no core substream, so naming it "dts" would claim a decoder that yields silence.
            // "dtsl" and "a_dts/lossless" are DTS-HD Master Audio, which does carry a core and
            // plays as that core.
            "dts" or "dca" or "a_dts" or "a_dts/lossless" or "dtsc" or "dtsh" or "dtsl" => "dts",
            // VP8
            "vp8" or "v_vp8" => "vp8",
            // VP9
            "vp9" or "vp09" or "v_vp9" => "vp9",
            // AV1
            "av1" or "av01" or "v_av1" => "av1",
            // Motion JPEG. ff_codec_bmp_tags names a dozen fourccs for it and the RIFF
            // table already maps them, so this row is for the ones a container may hand over
            // unmapped - an MP4 'jpeg' sample entry among them.
            "mjpeg" or "mjpg" or "jpeg" or "ljpg" or "avrn" or "avdj" or "dmb1" => "mjpeg",
            // MPEG-4 Part 2. The RIFF and Matroska tables already name every fourcc and
            // codec id as "mpeg4", so this row exists for the raw fourccs a container may hand
            // over unmapped - an MP4 'mp4v' sample entry, and the DivX-era AVI fourccs if one
            // ever reaches here without going through ff_codec_bmp_tags.
            "mpeg4" or "mp4v" or "m4s2" or "divx" or "dx50" or "xvid" or "xvix" or "fmp4"
                => "mpeg4",
            // VC-1 and WMV3 share one decoder, as they do in FFmpeg - four AV_CODEC_IDs over
            // one vc1dec.c. The two layouts the decoder has to tell apart are not decided
            // here, because this returns a registry key and not a configuration: see
            // Vc1VideoDecoder.StreamCodecId and ConfigureVideoDecoder below.
            "vc1" or "vc-1" or "wvc1" or "wmva" or "wmv3" or "wmv9" => "vc1",
            // ITU-T H.263. The RIFF table already names its fourccs, so this row is for
            // the ones a container hands over unmapped - 's263' is the 3GP sample entry.
            // 'h263i' is NOT here: Intel's H.263 is a different bitstream with its own
            // decoder in FFmpeg, and mapping it to this one would decode noise.
            "h263" or "s263" => "h263",
            // MPEG-1 and MPEG-2 video share one decoder, as they do in FFmpeg
            "mpeg1video" or "mpeg2video" or "mpeg2" or "mpg2" or "mp2v" or "v_mpeg2"
                => "mpeg2video",
            // Pass through
            _ => codecId.ToLowerInvariant(),
        };
    }

    private static DemuxerTrackType MapMp4TrackType(Mp4Track track)
    {
        return track.TrackType switch
        {
            Mp4TrackType.Video => DemuxerTrackType.Video,
            Mp4TrackType.Audio => DemuxerTrackType.Audio,
            Mp4TrackType.Subtitle => DemuxerTrackType.Subtitle,
            _ => DemuxerTrackType.Unknown,
        };
    }

    private static string FourCCToString(uint fourcc)
    {
        if (fourcc == 0) return "unknown";
        Span<char> chars = stackalloc char[4];
        chars[0] = (char)((fourcc >> 24) & 0xFF);
        chars[1] = (char)((fourcc >> 16) & 0xFF);
        chars[2] = (char)((fourcc >> 8) & 0xFF);
        chars[3] = (char)(fourcc & 0xFF);
        return new string(chars).Trim('\0').ToLowerInvariant();
    }

    // ── Disposal ─────────────────────────────────────────────────────────

    private bool disposed;

    /// <summary>
    /// Releases the demuxers, decoders and buffered frames. Safe to call more than once.
    /// </summary>
    /// <remarks>
    /// The guard is not tidiness. Without it a second call cancelled an already-disposed token
    /// source, which throws and masks whatever the caller was really disposing for - and, worse,
    /// returned the read buffer to the array pool a second time, which hands the same array to two
    /// owners.
    /// </remarks>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        pipelineCts.Cancel();
        pipelineCts.Dispose();
        videoDecoder?.Dispose();
        audioDecoder?.Dispose();
        mp4Demuxer?.Dispose();
        mkvDemuxer?.Dispose();
        aviDemuxer?.Dispose();
        psDemuxer?.Dispose();
        tsDemuxer?.Dispose();
        flvDemuxer?.Dispose();
        oggDemuxer?.Dispose();
        asfDemuxer?.Dispose();
        elementaryAudio?.Dispose();
        elementaryVideo?.Dispose();
        multiTrack?.Dispose();

        // Drain channels to dispose any buffered frames
        while (videoChannel.Reader.TryRead(out var frame)) frame.Dispose();
        while (audioChannel.Reader.TryRead(out var audio)) audio.Dispose();

        ArrayPool<byte>.Shared.Return(readBuffer);
        if (ownsStream) fileStream.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

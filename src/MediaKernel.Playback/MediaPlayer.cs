using System.Diagnostics;
using MediaKernel.Core;
using MediaKernel.Core.Audio;
using MediaKernel.Core.Demuxers;
using MediaKernel.Core.Video;

namespace MediaKernel.Playback;

/// <summary>
/// One open file, playing: the decode pipeline, the audio device, the clock the two are
/// synchronised to, and the pictures that come out of it.
/// </summary>
/// <remarks>
/// This is the part between a decoder and a window. <see cref="DecodePipeline"/> hands out
/// decoded frames as fast as it can make them; a player has to decide WHEN each one is shown,
/// keep it in step with the sound, put pictures back in order when a decoder hands them over out
/// of order, drop what is too late to be worth showing, and do all of that without holding up
/// the thread that draws. Every application that plays a file needs exactly this and it is the
/// same work every time, so it is here rather than in each of them.
/// <para>
/// THREE THREADS, and which one does what is the design:
/// </para>
/// <list type="bullet">
/// <item>The AUDIO thread feeds the device and is the clock master. It sets the clock from each
/// frame's timestamp less whatever is still queued in the device, so the clock says what is
/// AUDIBLE rather than what has been handed over - setting it to the frame just written puts the
/// clock a whole device buffer into the future and makes every picture look late.</item>
/// <item>The PRESENTER thread takes decoded pictures, puts them back into timestamp order,
/// converts them to RGBA, scales them to the box they will be drawn in, and publishes the one
/// that is due.</item>
/// <item>The HOST's own thread - the UI thread - calls <see cref="TryTakePicture"/> whenever it
/// is ready to draw, and does no media work at all. Converting a 1920x1080 frame costs several
/// milliseconds and 8 MB; doing that on a UI thread is what makes a 1080p file stutter and the
/// sound break up.</item>
/// </list>
/// <para>
/// Seeking re-opens the pipeline at the target rather than seeking an open one, which is what
/// keeps the trim exact, and happens on the presenter thread because it takes long enough to be
/// seen if it happens on the host's.
/// </para>
/// </remarks>
public sealed class MediaPlayer : IDisposable
{
    /// <summary>
    /// What a host does with a finished picture: copy it into a texture, a bitmap, a window.
    /// </summary>
    /// <param name="pixels">
    /// RGBA8, tightly packed, <paramref name="width"/> * <paramref name="height"/> * 4 bytes.
    /// Valid only for the duration of the call: it is the player's own buffer and the next
    /// picture overwrites it.
    /// </param>
    /// <param name="width">The picture's width, which is not the video's if it was scaled.</param>
    /// <param name="height">The picture's height.</param>
    /// <param name="presentationTimeTicks">Where this picture sits on the file's timeline.</param>
    public delegate void PictureHandler(ReadOnlySpan<byte> pixels, int width, int height,
                                        long presentationTimeTicks);

    /// <summary>
    /// How late a picture may be and still be shown rather than dropped. One 24 fps frame.
    /// </summary>
    private static readonly long LateFrameLimitTicks = TimeSpan.TicksPerSecond / 24;

    /// <summary>
    /// How many pictures in a row may be dropped for being late before one is shown anyway.
    /// </summary>
    /// <remarks>
    /// Without this a player can stop showing video altogether and never recover, which is not a
    /// theory: a 1080p HEVC file reported "0 shown, 388 dropped" with a frozen picture over a
    /// running clock. Once the clock is ahead of the decoder - because decoding is slower than
    /// real time, or because the clock ran while a file was being re-opened for a seek - EVERY
    /// frame is late, and a rule that drops late frames while another is queued drops all of
    /// them, for ever. Showing one in four late is a player running behind; showing none is a
    /// player that looks broken.
    /// </remarks>
    private const int MaxConsecutiveDrops = 3;

    private readonly MediaPlayerOptions options;
    private readonly IAudioOutput? audioOutput;
    private readonly bool ownsAudioOutput;
    private readonly MasterClock clock = new();

    /// <summary>Guards the handover of a finished picture from the presenter to the host.</summary>
    private readonly object handover = new();

    /// <summary>Frames held back, waiting for their turn in timestamp order. Presenter only.</summary>
    private readonly List<VideoFrame> pending = [];

    private readonly Thread presenter;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Stopwatch decodeClock = new();

    private volatile Engine engine;
    private volatile bool paused;
    private volatile bool ended;
    private volatile Exception? fault;
    private volatile int displayWidth;
    private volatile int displayHeight;

    /// <summary>The position a seek has asked for, waiting for the presenter to carry it out.</summary>
    private long requestedSeekTicks = -1;

    // The published picture: the presenter writes it, the host takes it, and the two never touch
    // the same buffer at the same time because readyLength is zero while one of them owns it.
    private byte[] readyPixels = [];
    private int readyWidth;
    private int readyHeight;
    private long readyPts;
    private bool readyWaiting;

    private float volume;
    private long framesTaken;

    // Presenter-thread scratch, reused so that a 1080p frame does not allocate 8 MB a time.
    private byte[] convertBuffer = [];
    private byte[] scaleBuffer = [];
    private int consecutiveDrops;

    private MediaPlayer(string path, MediaPlayerOptions options, Engine engine,
                        IAudioOutput? audioOutput, bool ownsAudioOutput,
                        string videoCodec, string audioCodec, double frameRate,
                        bool hasVideo, bool hasAudio, long durationTicks)
    {
        FilePath = path;
        this.options = options;
        this.engine = engine;
        this.audioOutput = audioOutput;
        this.ownsAudioOutput = ownsAudioOutput;
        volume = options.Volume;
        VideoCodec = videoCodec;
        AudioCodec = audioCodec;
        FrameRate = frameRate;
        HasVideo = hasVideo;
        HasAudio = hasAudio;
        DurationTicks = durationTicks;

        presenter = new Thread(PresenterLoop)
        {
            IsBackground = true,
            Name = "mediakernel-presenter",
        };
    }

    /// <summary>The file being played.</summary>
    public string FilePath { get; }

    /// <summary>The video codec the container named, or an empty string.</summary>
    public string VideoCodec { get; }

    /// <summary>The audio codec the container named, or an empty string.</summary>
    public string AudioCodec { get; }

    /// <summary>The frame rate the container stated, or zero when it stated none.</summary>
    public double FrameRate { get; }

    /// <summary>True when a video track has a decoder behind it.</summary>
    public bool HasVideo { get; }

    /// <summary>True when an audio track has a decoder behind it.</summary>
    public bool HasAudio { get; }

    /// <summary>
    /// The size of the pictures actually being decoded, and the rate and channel count of the
    /// samples actually being played.
    /// </summary>
    /// <remarks>
    /// Taken from the frames rather than from the container, because not every container states
    /// them: an MPEG program stream states no picture size at all, and the only place it is
    /// written is the elementary stream's own sequence header.
    /// </remarks>
    public int VideoWidth { get; private set; }

    /// <inheritdoc cref="VideoWidth"/>
    public int VideoHeight { get; private set; }

    /// <inheritdoc cref="VideoWidth"/>
    public int SampleRate { get; private set; }

    /// <inheritdoc cref="VideoWidth"/>
    public int Channels { get; private set; }

    /// <summary>Where playback has reached.</summary>
    public TimeSpan Position => TimeSpan.FromTicks(paused ? PositionTicks
                                                         : clock.GetCurrentStreamTime());

    /// <summary>Where playback has reached, in ticks of the file's own timeline.</summary>
    public long PositionTicks { get; private set; }

    /// <summary>How long the file is, or zero when the container states no length.</summary>
    public TimeSpan Duration => TimeSpan.FromTicks(DurationTicks);

    /// <inheritdoc cref="Duration"/>
    public long DurationTicks { get; private set; }

    /// <summary>Pictures shown.</summary>
    public int FramesPresented { get; private set; }

    /// <summary>Pictures thrown away for arriving late.</summary>
    public int FramesDropped { get; private set; }

    /// <summary>
    /// Pictures that arrived with a timestamp earlier than the one before them, which a
    /// decoder's output should never do. See <see cref="MediaPlayerOptions.ReorderDepth"/>.
    /// </summary>
    public int FramesOutOfOrder { get; private set; }

    /// <summary>How far behind the clock the last picture shown was, in milliseconds.</summary>
    public double SkewMilliseconds { get; private set; }

    /// <summary>
    /// How many pictures a second the decoder is actually producing, measured from the first one
    /// taken. Zero until there is enough to measure.
    /// </summary>
    /// <remarks>
    /// Pictures are taken from the pipeline only as fast as they are shown, so a player in step
    /// reads the source frame rate however much headroom there is. What this is good for is the
    /// other case: a decoder that cannot keep up reads below it, which is the difference between
    /// "this player paces badly" and "this machine cannot decode this file in time".
    /// </remarks>
    public double DecodeRateFps
    {
        get
        {
            double elapsed = decodeClock.Elapsed.TotalSeconds;
            return elapsed > 1 ? framesTaken / elapsed : 0;
        }
    }

    /// <summary>
    /// True when the picture has fallen behind the sound and stayed there, which is what a
    /// decoder that cannot keep up looks like from outside.
    /// </summary>
    public bool RunningBehind => SkewMilliseconds > 150;

    /// <summary>True once the file has run out and the last picture has been shown.</summary>
    public bool Ended => ended;

    /// <summary>Whether playback is stopped where it stands.</summary>
    public bool IsPaused => paused;

    /// <summary>The decode fault that stopped playback, if one did.</summary>
    public Exception? Fault => fault;

    /// <summary>Playback volume, 0 to 1, applied to the device as it changes.</summary>
    public float Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0f, 1f);

            if (audioOutput is not null)
                audioOutput.Volume = volume;
        }
    }

    /// <summary>
    /// Opens a file and starts playing it.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The file opened and no track in it has a decoder behind it. The message names the tracks,
    /// because "nothing happened" is the least useful thing a player can do with a file it
    /// cannot play.
    /// </exception>
    public static MediaPlayer Open(string path, MediaPlayerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        options ??= new MediaPlayerOptions();

        var engine = Engine.Create(path, TimeSpan.Zero, frameRate: null);
        var pipeline = engine.Pipeline;

        try
        {
            bool hasVideo = pipeline.HasVideoDecoder;
            bool hasAudio = pipeline.HasAudioDecoder;

            if (!hasVideo && !hasAudio)
            {
                string names = string.Join(", ",
                    pipeline.Tracks.Select(t => $"{t.TrackType} {t.CodecId}"));

                throw new NotSupportedException(
                    names.Length == 0
                        ? "The file opened but holds no tracks."
                        : $"No decoder for any track in this file: {names}.");
            }

            string videoCodec = "";
            string audioCodec = "";
            double frameRate = 0;

            foreach (var track in pipeline.Tracks)
            {
                if (track.TrackType == DemuxerTrackType.Video && track.TrackId == pipeline.VideoTrackId)
                {
                    videoCodec = track.CodecId ?? "";
                    frameRate = track.FrameRate;
                }
                else if (track.TrackType == DemuxerTrackType.Audio && track.TrackId == pipeline.AudioTrackId)
                {
                    audioCodec = track.CodecId ?? "";
                }
            }

            if (frameRate > 0)
                engine.FrameRate = frameRate;

            (IAudioOutput? output, bool owned) = ResolveAudioOutput(options, hasAudio);

            var player = new MediaPlayer(
                path, options, engine, output, owned, videoCodec, audioCodec, frameRate,
                hasVideo, hasAudio, pipeline.GetProgress().TotalDurationTicks);

            if (output is not null)
                output.Volume = options.Volume;

            engine.Start(player);
            player.presenter.Start();
            return player;
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The device to play through: the caller's, the platform's, or none at all.
    /// </summary>
    /// <remarks>
    /// A player with no device still decodes and still shows pictures, timed by the first one
    /// rather than by the sound. That is what a test harness wants, and it is what a platform
    /// without an output here gets rather than a refusal to open the file.
    /// </remarks>
    private static (IAudioOutput? Output, bool Owned) ResolveAudioOutput(
        MediaPlayerOptions options, bool hasAudio)
    {
        if (!hasAudio || options.Silent)
            return (null, false);

        if (options.AudioOutput is { } supplied)
            return (supplied, false);

        if (OperatingSystem.IsWindows())
            return (new Core.Audio.Wasapi.WasapiAudioOutput(), true);

        return (null, false);
    }

    /// <summary>
    /// Takes the picture the presenter has made ready, if there is one, and hands it to
    /// <paramref name="handler"/>. Called by the host whenever it is ready to draw; it never
    /// blocks and does no media work.
    /// </summary>
    /// <returns>True when a picture was handed over.</returns>
    public bool TryTakePicture(PictureHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (handover)
        {
            if (!readyWaiting)
                return false;

            handler(readyPixels.AsSpan(0, readyWidth * readyHeight * 4),
                    readyWidth, readyHeight, readyPts);

            readyWaiting = false;
        }

        return true;
    }

    /// <summary>
    /// Reads the clock. Call it once per host frame beside <see cref="TryTakePicture"/>; it is
    /// what moves <see cref="Position"/>.
    /// </summary>
    /// <returns>True when the position changed since the last call.</returns>
    public bool Tick()
    {
        if (paused)
            return false;

        long position = clock.GetCurrentStreamTime();

        if (position == PositionTicks)
            return false;

        PositionTicks = position;
        return true;
    }

    /// <summary>
    /// The box the picture will be drawn in, so that a frame larger than it is scaled down
    /// before it is handed over rather than after.
    /// </summary>
    /// <remarks>
    /// At 1920x1080 an RGBA frame is 8.3 MB. A host that draws it into a 1000 pixel window pays
    /// for every one of those bytes - converted, copied and uploaded - to throw three quarters
    /// of them away. Scaling first cuts it by the square of the ratio.
    /// </remarks>
    public void SetDisplaySize(int width, int height)
    {
        displayWidth = width;
        displayHeight = height;
    }

    /// <summary>Stops where it stands, or starts again from there.</summary>
    public void TogglePause()
    {
        if (ended)
        {
            // Play again from the top rather than sitting on the last frame doing nothing.
            SeekTo(TimeSpan.Zero);
            return;
        }

        paused = !paused;

        if (paused)
        {
            clock.Pause();
            audioOutput?.Pause();
        }
        else
        {
            audioOutput?.Resume();
            clock.Resume();
        }
    }

    /// <summary>Starts playing, if it was stopped.</summary>
    public void Play()
    {
        if (paused)
            TogglePause();
    }

    /// <summary>Stops where it stands.</summary>
    public void Pause()
    {
        if (!paused)
            TogglePause();
    }

    /// <summary>
    /// Asks for playback to continue from <paramref name="position"/>. The work happens on the
    /// presenter thread; this returns at once.
    /// </summary>
    public void SeekTo(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        // The clock holds the target and does NOT run until the first picture after the seek
        // arrives to anchor it. Letting it run through the re-open leaves every frame at the new
        // position looking seconds late, so all of them are dropped and the picture never moves.
        clock.SetReference(position.Ticks);
        clock.Pause();

        PositionTicks = position.Ticks;
        ended = false;
        paused = false;

        Interlocked.Exchange(ref requestedSeekTicks, position.Ticks);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lifetime.Cancel();

        if (presenter.IsAlive)
            presenter.Join(TimeSpan.FromSeconds(3));

        engine.Dispose();
        ClearReorderBuffer();

        if (ownsAudioOutput)
        {
            audioOutput?.Stop();
            audioOutput?.Dispose();
        }

        lifetime.Dispose();
    }

    // ── The presenter thread ─────────────────────────────────────────────

    private void PresenterLoop()
    {
        var ct = lifetime.Token;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                long target = Interlocked.Exchange(ref requestedSeekTicks, -1);

                if (target >= 0)
                {
                    PerformSeek(TimeSpan.FromTicks(target));
                    continue;
                }

                if (paused || !HasVideo)
                {
                    ObserveEnd();
                    Thread.Sleep(10);
                    continue;
                }

                if (!PresentDueFrame())
                {
                    ObserveEnd();
                    Thread.Sleep(2);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A decoder that throws stops playback and says why, rather than leaving a still
                // picture over a running clock with no explanation.
                fault ??= ex;
                ended = true;
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Notices that the file has run out, once everything decoded has been shown.</summary>
    private void ObserveEnd()
    {
        var current = engine;
        fault ??= current.Pipeline.FaultException;

        if (ended || current.Pipeline.State is not (DecodePipelineState.Completed
                                                 or DecodePipelineState.Faulted))
        {
            return;
        }

        // Completed means the decoder has stopped, not that the frames it buffered have been
        // shown; the file has ended when there is nothing left to show either.
        bool videoDrained = !HasVideo || (current.Pipeline.VideoFrames.Count == 0 && pending.Count == 0);
        bool audioDrained = !HasAudio || current.Pipeline.AudioFrames.Count == 0;

        if (videoDrained && audioDrained)
        {
            ended = true;
            paused = true;
            clock.Pause();
        }
    }

    /// <summary>Shows the next picture if its time has come. True when one was published.</summary>
    private bool PresentDueFrame()
    {
        var current = engine;
        long now = clock.GetCurrentStreamTime();

        while (true)
        {
            FillReorderBuffer(current);

            if (pending.Count == 0)
                return false;

            // Nothing is shown until the buffer is full, so that a picture which has not arrived
            // yet still gets its turn - unless the decoder has finished, in which case what is
            // held is all there will ever be.
            bool draining = current.Pipeline.State is DecodePipelineState.Completed
                                                   or DecodePipelineState.Faulted
                         && current.Pipeline.VideoFrames.Count == 0;

            if (pending.Count < options.ReorderDepth && !draining)
                return false;

            int earliest = EarliestPending();

            // The first picture starts the clock when there is no audio to start it - and when
            // there is audio that has not reached the device yet, and after every seek.
            if (clock.IsPaused)
            {
                clock.SetReference(pending[earliest].Pts);
                now = pending[earliest].Pts;
            }

            if (pending[earliest].Pts > now)
                return false;

            var frame = pending[earliest];
            pending.RemoveAt(earliest);

            try
            {
                bool tooLate = now - frame.Pts > LateFrameLimitTicks;

                if (tooLate && pending.Count > 0 && consecutiveDrops < MaxConsecutiveDrops)
                {
                    consecutiveDrops++;
                    FramesDropped++;
                    continue;
                }

                consecutiveDrops = 0;
                Publish(ref frame, now);
                return true;
            }
            finally
            {
                frame.Dispose();
            }
        }
    }

    /// <summary>Converts a picture and makes it ready for the host.</summary>
    private void Publish(ref VideoFrame frame, long now)
    {
        VideoWidth = frame.Width;
        VideoHeight = frame.Height;

        // How far behind the clock this picture was when it was shown. Zero means the video is
        // exactly where the sound is; a steady positive figure means the picture is late.
        SkewMilliseconds = (now - frame.Pts) / (double)TimeSpan.TicksPerMillisecond;

        convertBuffer = VideoFrameRgba.Convert(ref frame, convertBuffer);

        int width = frame.Width;
        int height = frame.Height;
        byte[] pixels = convertBuffer;

        if (ShouldScale(width, height, out int scaledWidth, out int scaledHeight))
        {
            int needed = scaledWidth * scaledHeight * 4;

            if (scaleBuffer.Length != needed)
                scaleBuffer = new byte[needed];

            VideoScaler.ScaleBgra(
                convertBuffer.AsSpan(0, width * height * 4), width, height,
                scaleBuffer, scaledWidth, scaledHeight);

            pixels = scaleBuffer;
            width = scaledWidth;
            height = scaledHeight;
        }

        lock (handover)
        {
            int needed = width * height * 4;

            if (readyPixels.Length != needed)
                readyPixels = new byte[needed];

            pixels.AsSpan(0, needed).CopyTo(readyPixels);
            readyWidth = width;
            readyHeight = height;
            readyPts = frame.Pts;

            // A picture the host never took is replaced rather than queued: it is already out of
            // date, and showing it after the one that replaced it would be a step backwards.
            readyWaiting = true;
        }

        FramesPresented++;
    }

    /// <summary>
    /// Whether the picture is bigger than the box it will be drawn in, and what size to scale it
    /// to. Upscaling is left to whatever draws it, which does that for nothing.
    /// </summary>
    private bool ShouldScale(int width, int height, out int scaledWidth, out int scaledHeight)
    {
        scaledWidth = 0;
        scaledHeight = 0;

        int boxWidth = displayWidth;
        int boxHeight = displayHeight;

        if (boxWidth <= 0 || boxHeight <= 0 || width <= 0 || height <= 0)
            return false;

        double scale = Math.Min(boxWidth / (double)width, boxHeight / (double)height);

        // A few percent of difference is not worth a resampling pass.
        if (scale >= 0.95)
            return false;

        scaledWidth = Math.Max(2, (int)Math.Round(width * scale));
        scaledHeight = Math.Max(2, (int)Math.Round(height * scale));
        return true;
    }

    /// <summary>
    /// Takes frames from the pipeline until the reorder buffer is full, counting the ones that
    /// arrive with a timestamp earlier than the frame before them.
    /// </summary>
    private void FillReorderBuffer(Engine current)
    {
        while (pending.Count < options.ReorderDepth
               && current.Pipeline.VideoFrames.TryRead(out var frame))
        {
            if (pending.Count > 0 && frame.Pts < pending[^1].Pts)
                FramesOutOfOrder++;

            if (framesTaken++ == 0)
                decodeClock.Restart();

            pending.Add(frame);
        }
    }

    /// <summary>The held frame with the earliest timestamp: the one to show next.</summary>
    private int EarliestPending()
    {
        int earliest = 0;

        for (int i = 1; i < pending.Count; i++)
        {
            if (pending[i].Pts < pending[earliest].Pts)
                earliest = i;
        }

        return earliest;
    }

    /// <summary>Throws away every held frame, for a seek or for shutting down.</summary>
    private void ClearReorderBuffer()
    {
        foreach (var frame in pending)
            frame.Dispose();

        pending.Clear();
    }

    /// <summary>
    /// Re-opens the file at the target and plays on from there. On the presenter thread:
    /// stopping a decode run takes long enough to be seen if it happens on the host's.
    /// </summary>
    private void PerformSeek(TimeSpan position)
    {
        var replaced = engine;

        replaced.Dispose();
        ClearReorderBuffer();
        audioOutput?.Flush();

        consecutiveDrops = 0;
        framesTaken = 0;
        decodeClock.Reset();
        fault = null;

        var started = Engine.Create(FilePath, position, replaced.FrameRate);
        engine = started;
        started.Start(this);

        audioOutput?.Resume();
    }

    // ── One decode run ───────────────────────────────────────────────────

    /// <summary>
    /// One run of the decode pipeline from one starting position: the pipeline, its decode
    /// thread and the thread that feeds the audio device. A seek replaces the whole of it.
    /// </summary>
    private sealed class Engine : IDisposable
    {
        private readonly CancellationTokenSource cts = new();
        private Thread? decodeThread;
        private Thread? audioThread;

        private Engine(DecodePipeline pipeline) => Pipeline = pipeline;

        public DecodePipeline Pipeline { get; }

        /// <summary>The source frame rate, carried across a seek.</summary>
        public double FrameRate { get; set; } = 25.0;

        public static Engine Create(string path, TimeSpan position, double? frameRate)
        {
            var options = new DecodePipelineOptions
            {
                StartTime = position == TimeSpan.Zero ? null : position,
            };

            var engine = new Engine(DecodePipeline.Open(path, options));

            if (frameRate.HasValue)
                engine.FrameRate = frameRate.Value;

            return engine;
        }

        /// <summary>Starts decoding, and feeding the audio device if there is one.</summary>
        /// <remarks>
        /// Real threads rather than <c>Task.Run</c>, and the audio one above normal priority.
        /// The decode pipeline keeps the thread pool busy, and an audio thread queued behind it
        /// arrives late at a device that will not wait - which is what "the audio was barely
        /// keeping up" sounds like.
        /// </remarks>
        public void Start(MediaPlayer player)
        {
            decodeThread = new Thread(() =>
            {
                try
                {
                    Pipeline.RunAsync(cts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
            })
            {
                IsBackground = true,
                Name = "mediakernel-decode",
            };

            decodeThread.Start();

            if (player.audioOutput is { } output)
            {
                audioThread = new Thread(() => AudioLoop(output, player, cts.Token))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                    Name = "mediakernel-audio",
                };

                audioThread.Start();
            }
            else if (player.HasAudio)
            {
                // Nobody is draining the audio channel, and it is bounded: left alone it fills
                // and the decode loop stops with it. A silent player still has to consume.
                audioThread = new Thread(() => DiscardLoop(cts.Token))
                {
                    IsBackground = true,
                    Name = "mediakernel-audio-discard",
                };

                audioThread.Start();
            }
        }

        private void DiscardLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (Pipeline.AudioFrames.TryRead(out var frame))
                {
                    frame.Dispose();
                    continue;
                }

                if (Pipeline.State is DecodePipelineState.Completed or DecodePipelineState.Faulted)
                    break;

                Thread.Sleep(2);
            }
        }

        private void AudioLoop(IAudioOutput output, MediaPlayer player, CancellationToken ct)
        {
            bool initialized = false;
            long writtenSamples = 0;

            while (!ct.IsCancellationRequested)
            {
                if (player.paused)
                {
                    // Nothing is consumed while paused, so the clock's reference is not moved
                    // and the device keeps what it already holds.
                    Thread.Sleep(10);
                    continue;
                }

                if (!Pipeline.AudioFrames.TryRead(out var frame))
                {
                    if (Pipeline.State is DecodePipelineState.Completed
                                       or DecodePipelineState.Faulted)
                    {
                        break;
                    }

                    Thread.Sleep(2);
                    continue;
                }

                try
                {
                    player.SampleRate = frame.SampleRate;
                    player.Channels = frame.Channels;

                    if (!initialized && frame.SampleCount > 0)
                    {
                        var format = new AudioFormat(frame.SampleRate, frame.Channels,
                                                     AudioSampleFormat.Float32);
                        initialized = output.Initialize(format);

                        if (initialized)
                        {
                            output.Volume = player.Volume;
                            output.Start();
                        }
                    }

                    if (!initialized)
                        continue;

                    // Audio is the clock master, and the clock has to say what is AUDIBLE, not
                    // what has been handed over: the samples already in the device's buffer play
                    // before this frame does, so the sound being heard now is this frame's
                    // timestamp minus everything queued ahead of it.
                    player.clock.SetReference(
                        frame.Pts - BufferedTicks(output, writtenSamples, frame.SampleRate));

                    writtenSamples += frame.SampleCount;
                    Write(output, ref frame, ct);
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }

        /// <summary>
        /// How much sound is sitting in the device waiting to be played, in ticks: what has been
        /// written, less what the device's own clock says it has played.
        /// </summary>
        /// <remarks>
        /// Clamped to a quarter of a second, which is more than any sane shared-mode buffer: a
        /// device that will not report its position returns zero, and without the clamp that
        /// reads as "everything ever written is still queued" and walks the clock backwards for
        /// the whole file.
        /// </remarks>
        private static long BufferedTicks(IAudioOutput output, long writtenSamples, int sampleRate)
        {
            if (sampleRate <= 0)
                return 0;

            long playedSamples = output.GetPositionMicroseconds() * sampleRate / 1_000_000;
            long buffered = Math.Clamp(writtenSamples - playedSamples, 0, sampleRate / 4);

            return buffered * TimeSpan.TicksPerSecond / sampleRate;
        }

        private static unsafe void Write(IAudioOutput output, ref AudioFrame frame,
                                         CancellationToken ct)
        {
            var format = new AudioFormat(frame.SampleRate, frame.Channels,
                                         AudioSampleFormat.Float32);

            fixed (float* samples = frame.Samples.Span)
            {
                int remaining = frame.SampleCount;

                while (remaining > 0 && !ct.IsCancellationRequested)
                {
                    int offset = (frame.SampleCount - remaining) * frame.Channels;
                    var buffer = new MediaKernel.Core.Audio.AudioBuffer((nint)(samples + offset), remaining,
                                                 frame.Pts, format);

                    int written = output.Write(in buffer);
                    remaining -= written;

                    if (remaining > 0)
                    {
                        // The device's buffer is full: let it drain rather than spin on it.
                        Thread.Sleep(5);
                    }
                }
            }
        }

        public void Dispose()
        {
            cts.Cancel();

            // Bounded waits: a decode thread blocked on a slow read must not hold up a seek, and
            // a leaked thread on an engine being thrown away stops on its own.
            audioThread?.Join(TimeSpan.FromSeconds(2));
            decodeThread?.Join(TimeSpan.FromSeconds(2));

            Pipeline.Dispose();
            cts.Dispose();
        }
    }
}

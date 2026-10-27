namespace MediaKernel.Core.Codecs;

/// <summary>
/// Marks a class as a codec decoder for automatic registration.
/// The source generator discovers types with this attribute and generates
/// an AOT-compatible static registry.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CodecAttribute : Attribute
{
    /// <summary>
    /// The codec identifier (e.g., "h264", "hevc", "aac", "opus").
    /// </summary>
    public string CodecId { get; }

    /// <summary>
    /// The codec type (video, audio, subtitle).
    /// </summary>
    public CodecType Type { get; }

    /// <summary>
    /// Priority for codec selection (higher = preferred). Default is 10.
    /// Hardware decoders should use 100+.
    /// </summary>
    public int Priority { get; set; } = 10;

    /// <summary>
    /// Human-readable display name for the codec.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Whether this is a hardware-accelerated decoder.
    /// </summary>
    public bool IsHardwareAccelerated { get; set; }

    public CodecAttribute(string codecId, CodecType type)
    {
        CodecId = codecId ?? throw new ArgumentNullException(nameof(codecId));
        Type = type;
    }
}

/// <summary>
/// Codec type classification.
/// </summary>
public enum CodecType
{
    Video,
    Audio,
    Subtitle
}

/// <summary>
/// Base interface for all decoders.
/// </summary>
public interface IDecoder : IDisposable
{
    /// <summary>Gets the codec identifier.</summary>
    string CodecId { get; }

    /// <summary>Gets whether this decoder is hardware accelerated.</summary>
    bool IsHardwareAccelerated { get; }

    /// <summary>
    /// Initializes the decoder with codec-specific configuration data.
    /// </summary>
    /// <param name="codecPrivate">Codec configuration (e.g., SPS/PPS for H.264, AudioSpecificConfig for AAC).</param>
    /// <returns>True if initialization succeeded.</returns>
    bool Initialize(ReadOnlySpan<byte> codecPrivate);

    /// <summary>
    /// Flushes the decoder state (e.g., after a seek).
    /// </summary>
    void Flush();
}

/// <summary>
/// Video decoder interface.
/// </summary>
public interface IVideoDecoder : IDecoder
{
    /// <summary>Gets the decoded video width.</summary>
    int Width { get; }

    /// <summary>Gets the decoded video height.</summary>
    int Height { get; }

    /// <summary>Gets the pixel format of decoded frames.</summary>
    PixelFormat OutputFormat { get; }

    /// <summary>
    /// Decodes a compressed video frame.
    /// </summary>
    /// <param name="data">Compressed frame data.</param>
    /// <param name="presentationTimeTicks">Presentation timestamp in ticks.</param>
    /// <param name="isKeyframe">Whether this is a keyframe.</param>
    /// <returns>Decoded frame, or null if more data is needed.</returns>
    DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks, bool isKeyframe);

    /// <summary>
    /// Signals end of stream and drains internally buffered frames (e.g. B-frame reorder buffer).
    /// Remaining frames can be retrieved by subsequent Decode calls with empty data.
    /// </summary>
    void DrainPendingFrames();
}

/// <summary>
/// Audio decoder interface.
/// </summary>
public interface IAudioDecoder : IDecoder
{
    /// <summary>Gets the sample rate in Hz.</summary>
    int SampleRate { get; }

    /// <summary>Gets the number of channels.</summary>
    int Channels { get; }

    /// <summary>Gets the bits per sample.</summary>
    int BitsPerSample { get; }

    /// <summary>
    /// Decodes a compressed audio frame.
    /// </summary>
    /// <param name="data">Compressed audio data.</param>
    /// <param name="presentationTimeTicks">Presentation timestamp in ticks.</param>
    /// <returns>Decoded audio samples, or null if more data is needed.</returns>
    DecodedAudioFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks);

    /// <summary>
    /// Signals that no more data will arrive, so a decoder holding part of a frame can decide
    /// what to do with it. Remaining frames are retrieved by further <see cref="Decode"/> calls
    /// with empty data, as with video.
    /// </summary>
    /// <remarks>
    /// Only a decoder that frames its own input needs this, which is why it has a default: a
    /// decoder handed whole frames by its container has nothing held back. It matters for
    /// MPEG audio in a program stream, where the last frame of a file is commonly truncated and
    /// FFmpeg decodes it anyway - without this the soundtrack is one frame short of ffmpeg's.
    /// </remarks>
    void DrainPendingFrames() { }

    /// <summary>
    /// Tells a decoder the sample rate and channel count the container declared, before
    /// <see cref="IDecoder.Initialize"/>.
    /// </summary>
    /// <remarks>
    /// It has a default because almost every audio codec here is self-describing: an MP3 frame
    /// header, an AAC AudioSpecificConfig or a FLAC STREAMINFO says what the stream is. Raw PCM
    /// says nothing at all - the bytes are the samples - so a WAV or AIFF decoder has no other
    /// way to learn its own format, and the alternative was smuggling it through
    /// <c>codecPrivate</c>, which is meant for a codec's own configuration.
    /// </remarks>
    void ConfigureAudio(
        int sampleRate, int channels, int blockAlign = 0, int bitsPerCodedSample = 0) { }
}

// ── Encoder Interfaces ─────────────────────────────────────────────────

/// <summary>
/// Rate control mode for encoders.
/// </summary>
public enum RateControlMode
{
    /// <summary>Constant Rate Factor — quality-targeted VBR.</summary>
    Crf,
    /// <summary>Constant Bit Rate.</summary>
    Cbr,
    /// <summary>Variable Bit Rate with target average.</summary>
    Vbr,
    /// <summary>Lossless encoding (if supported).</summary>
    Lossless,
}

/// <summary>
/// Configuration for encoder initialization.
/// </summary>
public sealed class EncoderConfig
{
    /// <summary>Codec identifier (e.g., "h264", "hevc", "aac").</summary>
    public required string CodecId { get; init; }

    /// <summary>Rate control mode.</summary>
    public RateControlMode RateControl { get; init; } = RateControlMode.Crf;

    /// <summary>Target bitrate in bits/sec (for CBR/VBR).</summary>
    public long TargetBitrate { get; init; }

    /// <summary>Maximum bitrate in bits/sec (for VBR).</summary>
    public long MaxBitrate { get; init; }

    /// <summary>CRF value (0 = lossless, 51 = worst; typical range 18-28).</summary>
    public int Crf { get; init; } = 23;

    /// <summary>Encoder preset (maps to speed/quality tradeoff). 0=slowest/best, 10=fastest.</summary>
    public int Preset { get; init; } = 5;

    /// <summary>Keyframe interval in frames (0 = auto).</summary>
    public int KeyframeInterval { get; init; }

    /// <summary>Maximum number of B-frames between non-B (0 = disabled).</summary>
    public int MaxBFrames { get; init; }

    /// <summary>Number of threads for encoding (0 = auto).</summary>
    public int Threads { get; init; }

    /// <summary>Codec-specific parameters (key=value pairs).</summary>
    public Dictionary<string, string>? Parameters { get; init; }

    // Video-specific
    public int Width { get; init; }
    public int Height { get; init; }
    public PixelFormat InputFormat { get; init; }
    public (int Num, int Den) FrameRate { get; init; }

    // Audio-specific
    public int SampleRate { get; init; }
    public int Channels { get; init; }
}

/// <summary>
/// An encoded packet produced by an encoder, ready for muxing.
/// </summary>
public readonly struct EncodedPacket
{
    /// <summary>Compressed bitstream data.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>Presentation timestamp in ticks.</summary>
    public long PresentationTimestampTicks { get; init; }

    /// <summary>Decode timestamp in ticks.</summary>
    public long DecodeTimestampTicks { get; init; }

    /// <summary>Duration in ticks.</summary>
    public long DurationTicks { get; init; }

    /// <summary>Whether this is a keyframe / sync point.</summary>
    public bool IsKeyframe { get; init; }
}

/// <summary>
/// Base interface for all encoders.
/// </summary>
public interface IEncoder : IDisposable
{
    /// <summary>Gets the codec identifier.</summary>
    string CodecId { get; }

    /// <summary>Gets whether this encoder is hardware accelerated.</summary>
    bool IsHardwareAccelerated { get; }

    /// <summary>
    /// Initializes the encoder with the specified configuration.
    /// </summary>
    bool Initialize(EncoderConfig config);

    /// <summary>
    /// Gets codec-specific private data (e.g., SPS/PPS) after initialization.
    /// Required by muxers for the stream header.
    /// </summary>
    ReadOnlySpan<byte> GetCodecPrivateData();

    /// <summary>
    /// Signals end of input and flushes buffered frames.
    /// Call Encode with empty data after this to retrieve remaining packets.
    /// </summary>
    void Flush();
}

/// <summary>
/// Video encoder interface.
/// </summary>
public interface IVideoEncoder : IEncoder
{
    /// <summary>Configured output width.</summary>
    int Width { get; }

    /// <summary>Configured output height.</summary>
    int Height { get; }

    /// <summary>
    /// Encodes a raw video frame.
    /// </summary>
    /// <param name="frame">Input video frame with YUV plane data.</param>
    /// <returns>Encoded packet, or null if the encoder is buffering (B-frame lookahead).</returns>
    EncodedPacket? Encode(in VideoFrame frame);
}

/// <summary>
/// Audio encoder interface.
/// </summary>
public interface IAudioEncoder : IEncoder
{
    /// <summary>Sample rate in Hz.</summary>
    int SampleRate { get; }

    /// <summary>Number of channels.</summary>
    int Channels { get; }

    /// <summary>
    /// Encodes raw audio samples.
    /// </summary>
    /// <param name="frame">Input audio frame with interleaved float samples.</param>
    /// <returns>Encoded packet, or null if more samples are needed.</returns>
    EncodedPacket? Encode(in AudioFrame frame);
}

// PixelFormat is defined in MediaKernel.Core.MediaTypes — single canonical location.
// All codec code resolves it via namespace chain: Core.Codecs → Core.

/// <summary>
/// Decoded video frame with pixel data.
/// </summary>
/// <summary>A decoded video frame and the pooled buffer its planes point into.</summary>
/// <remarks>
/// A struct, so producing a frame allocates nothing. As a class this was the last per-frame
/// allocation left on every decoder's path once their internals stopped allocating. VideoFrame
/// alongside it is already a disposable struct owning a pooled buffer, so this follows the same
/// shape: the buffer field is the disposal guard, exchanged for null so the buffer goes back to
/// its pool exactly once per copy that owns it.
/// </remarks>
public struct DecodedVideoFrame : IDisposable
{
    private byte[]? rentedBuffer;

    public int Width { get; }
    public int Height { get; }
    public PixelFormat Format { get; }
    public long PresentationTimeTicks { get; }

    // Plane data for planar formats
    public ReadOnlyMemory<byte> YPlane { get; }
    public ReadOnlyMemory<byte> UPlane { get; }
    public ReadOnlyMemory<byte> VPlane { get; }

    // Strides for each plane
    public int YStride { get; }
    public int UStride { get; }
    public int VStride { get; }

    /// <summary>
    /// The pool <see cref="rentedBuffer"/> came from, so it goes back where it belongs. Null
    /// means the shared pool, which is what decoders that have not moved to a frame pool use.
    /// </summary>
    private readonly System.Buffers.ArrayPool<byte>? bufferPool;

    public DecodedVideoFrame(
        int width, int height, PixelFormat format, long pts,
        byte[] buffer,
        int yOffset, int yStride,
        int uOffset, int uStride,
        int vOffset, int vStride,
        System.Buffers.ArrayPool<byte>? bufferPool = null)
    {
        this.bufferPool = bufferPool;
        Width = width;
        Height = height;
        Format = format;
        PresentationTimeTicks = pts;
        rentedBuffer = buffer;
        YStride = yStride;
        UStride = uStride;
        VStride = vStride;

        int ySize = yStride * height;

        // 4:2:0 halves chroma height; 4:2:2 and 4:4:4 keep it. Listed exhaustively per bit depth —
        // omitting Yuv420P12 here previously over-sized the chroma planes for 12-bit 4:2:0.
        bool isChromaHeightHalved = format is PixelFormat.Yuv420P
            or PixelFormat.Yuv420P10
            or PixelFormat.Yuv420P12
            or PixelFormat.Nv12
            or PixelFormat.Nv21
            or PixelFormat.P010;
        int uvHeight = isChromaHeightHalved ? (height + 1) / 2 : height;
        int uSize = uStride * uvHeight;
        int vSize = vStride * uvHeight;

        YPlane = new ReadOnlyMemory<byte>(buffer, yOffset, ySize);
        UPlane = new ReadOnlyMemory<byte>(buffer, uOffset, uSize);
        VPlane = new ReadOnlyMemory<byte>(buffer, vOffset, vSize);
    }

    public void Dispose()
    {
        var buffer = System.Threading.Interlocked.Exchange(ref rentedBuffer, null);
        if (buffer is null || H264.H264Decoder.BypassArrayPool)
            return;

        if (ReferenceEquals(bufferPool, Buffers.FrameBufferPool<byte>.Instance))
            Buffers.FrameBufferPool<byte>.Return(buffer);
        else
            (bufferPool ?? System.Buffers.ArrayPool<byte>.Shared).Return(buffer);
    }

    /// <summary>The pool this frame's buffer must be returned to, for handing on with ownership.</summary>
    public System.Buffers.ArrayPool<byte>? BufferPool => bufferPool;

    /// <summary>
    /// Length of the rented buffer, or 0 once it has been detached. The pipeline reads this
    /// from the first decoded frame to learn how big a frame buffer is, so it can fill the
    /// pool to the number that can be in flight at once before playback rather than during it.
    /// </summary>
    public int BufferLength => rentedBuffer?.Length ?? 0;

    /// <summary>
    /// Transfers ownership of the underlying buffer to the caller.
    /// After calling this, the DecodedVideoFrame will NOT return the buffer to the pool on Dispose.
    /// Used by VideoFrame.TakeOwnership to create a zero-copy pipeline frame.
    /// </summary>
    public byte[] DetachBuffer()
        => System.Threading.Interlocked.Exchange(ref rentedBuffer, null)
           ?? throw new InvalidOperationException("This frame's buffer has already been taken or returned.");
}

/// <summary>
/// Decoded audio frame with sample data.
/// </summary>
/// <remarks>
/// <b>Lifetime:</b> a decoder may hand back the same instance, over the same buffer, on every
/// call. Everything here - <see cref="Samples"/> included - is valid only until the next
/// <c>Decode</c> call on the decoder that produced it. A caller that needs the samples for longer
/// copies them; <see cref="MediaKernel.Core.AudioFrame.TakeOwnership"/> is what does that for the
/// pipeline.
/// <para>
/// This used to be a fresh object over a fresh array per frame, which made the contract moot and
/// cost 2-8 KB of garbage per frame on every audio format. A collection during playback is a
/// dropped frame, so that was a defect rather than a tidiness question; it went unnoticed because
/// the pipeline measured its video decoder's allocation and not its audio decoder's.
/// </para>
/// </remarks>
public sealed class DecodedAudioFrame
{
    /// <summary>
    /// <c>AV_NOPTS_VALUE</c>: this frame carries no timestamp of its own and continues from
    /// wherever the previous one ended.
    /// </summary>
    /// <remarks>
    /// A container timestamp belongs to the frame that STARTS at it, which in a packet-framed
    /// stream is not every frame: an MPEG program stream's PES packets each hold two or three
    /// MP2 frames and the first of them usually began in the packet before. FFmpeg's parsers
    /// hand out <c>AV_NOPTS_VALUE</c> for those (<c>ff_fetch_timestamp</c> claims each packet's
    /// timestamp once) and the demuxer interpolates from the stream's running clock. Without the
    /// distinction the running clock is reset to the new packet's timestamp for a frame that
    /// began earlier, which moves that frame forward by part of a frame: on
    /// <c>Video-MPG-MPG.mpg</c> ffmpeg's fourth audio frame sits at 66452 and ours sat at
    /// 68802, and a two-second window then came out one frame short at 87,048 samples against
    /// 88,200.
    /// </remarks>
    public const long NoTimestamp = long.MinValue;

    public int SampleCount { get; private set; }
    public int Channels { get; private set; }
    public int SampleRate { get; private set; }
    public long PresentationTimeTicks { get; private set; }

    /// <summary>Whether <see cref="PresentationTimeTicks"/> is a timestamp at all.</summary>
    public bool HasTimestamp => PresentationTimeTicks != NoTimestamp;

    /// <summary>
    /// Interleaved sample data (float samples for all channels). See the lifetime note on the
    /// type: this is a window onto the decoder's own buffer, not a copy.
    /// </summary>
    public ReadOnlyMemory<float> Samples { get; private set; }

    public DecodedAudioFrame(int sampleCount, int channels, int sampleRate, long pts, float[] samples)
        : this(sampleCount, channels, sampleRate, pts, (ReadOnlyMemory<float>)samples)
    {
    }

    public DecodedAudioFrame(int sampleCount, int channels, int sampleRate, long pts,
                             ReadOnlyMemory<float> samples)
    {
        Set(sampleCount, channels, sampleRate, pts, samples);
    }

    /// <summary>
    /// Points this frame at a new window of samples. Decoders reuse one instance per stream; see
    /// the lifetime note on the type.
    /// </summary>
    internal void Set(int sampleCount, int channels, int sampleRate, long pts,
                      ReadOnlyMemory<float> samples)
    {
        SampleCount = sampleCount;
        Channels = channels;
        SampleRate = sampleRate;
        PresentationTimeTicks = pts;
        Samples = samples;
    }
}

/// <summary>
/// Factory delegate for creating decoder instances.
/// Used by the generated codec registry.
/// </summary>
public delegate IDecoder DecoderFactory();

/// <summary>
/// Codec registration entry for the generated registry.
/// </summary>
public readonly struct CodecRegistration
{
    public readonly string CodecId;
    public readonly CodecType Type;
    public readonly int Priority;
    public readonly string? DisplayName;
    public readonly bool IsHardwareAccelerated;
    public readonly DecoderFactory Factory;

    public CodecRegistration(
        string codecId, CodecType type, int priority,
        string? displayName, bool isHardwareAccelerated,
        DecoderFactory factory)
    {
        CodecId = codecId;
        Type = type;
        Priority = priority;
        DisplayName = displayName;
        IsHardwareAccelerated = isHardwareAccelerated;
        Factory = factory;
    }
}

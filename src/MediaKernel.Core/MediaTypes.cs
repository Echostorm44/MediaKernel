namespace MediaKernel.Core;

/// <summary>
/// Represents a raw packet of compressed media data extracted from a container.
/// This is a lightweight struct designed for zero-allocation in the demuxer hot path.
/// </summary>
/// <remarks>
/// MediaPacket does not own its data - it holds a reference to memory managed
/// by the pipeline's buffer pool. The packet is valid only within its processing scope.
/// </remarks>
public readonly ref struct MediaPacket
{
    /// <summary>
    /// The compressed payload data (NAL units, audio frames, etc.).
    /// </summary>
    public readonly ReadOnlySpan<byte> Payload;

    /// <summary>
    /// Presentation timestamp in TimeSpan ticks.
    /// Indicates when this packet should be presented relative to the stream start.
    /// </summary>
    public readonly long PresentationTimestamp;

    /// <summary>
    /// Decode timestamp in TimeSpan ticks.
    /// For codecs with B-frames, this may differ from PresentationTimestamp.
    /// </summary>
    public readonly long DecodeTimestamp;

    /// <summary>
    /// Duration of this packet in TimeSpan ticks.
    /// </summary>
    public readonly long Duration;

    /// <summary>
    /// The track number this packet belongs to.
    /// Used to route packets to the correct decoder.
    /// </summary>
    public readonly int TrackNumber;

    /// <summary>
    /// Indicates whether this packet starts with a keyframe (IDR for H.264).
    /// Seeking operations target keyframe packets.
    /// </summary>
    public readonly bool IsKeyframe;

    public MediaPacket(
        ReadOnlySpan<byte> payload,
        long presentationTimestamp,
        long decodeTimestamp,
        long duration,
        int trackNumber,
        bool isKeyframe)
    {
        Payload = payload;
        PresentationTimestamp = presentationTimestamp;
        DecodeTimestamp = decodeTimestamp;
        Duration = duration;
        TrackNumber = trackNumber;
        IsKeyframe = isKeyframe;
    }

    /// <summary>
    /// Simplified constructor when decode timestamp equals presentation timestamp.
    /// </summary>
    public MediaPacket(
        ReadOnlySpan<byte> payload,
        long presentationTimestamp,
        bool isKeyframe)
        : this(payload, presentationTimestamp, presentationTimestamp, 0, 0, isKeyframe)
    {
    }
}

/// <summary>
/// Represents a decoded video frame in the pipeline.
/// Uses Memory&lt;byte&gt; planes for safe cross-thread passing via channels,
/// and supports both CPU-decoded frames and GPU hardware surfaces.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>For CPU frames: YPlane/UPlane/VPlane contain pixel data backed by ArrayPool.</item>
/// <item>For GPU frames: HardwareSurface holds the native texture handle, planes are empty.</item>
/// <item>Call Dispose() to return the rented buffer to the pool.</item>
/// <item>Use VideoFrameHandle (in MediaPipeline) for automatic pool-return in channels.</item>
/// </list>
/// </remarks>
public struct VideoFrame : IDisposable
{
    /// <summary>Frame width in pixels.</summary>
    public int Width;

    /// <summary>Frame height in pixels.</summary>
    public int Height;

    /// <summary>The pixel format of this frame's data.</summary>
    public PixelFormat Format;

    /// <summary>Presentation timestamp in TimeSpan ticks.</summary>
    public long Pts;

    /// <summary>Duration this frame should be displayed, in TimeSpan ticks.</summary>
    public long Duration;

    // ── Plane data (CPU frames) ─────────────────────────────────────
    /// <summary>Y (luma) plane data. For packed formats (RGBA, BGRA), this holds all pixel data.</summary>
    public Memory<byte> YPlane;

    /// <summary>U (Cb) chroma plane data. Empty for packed formats.</summary>
    public Memory<byte> UPlane;

    /// <summary>V (Cr) chroma plane data. Empty for packed formats.</summary>
    public Memory<byte> VPlane;

    /// <summary>Stride (bytes per row) of the Y plane.</summary>
    public int YStride;

    /// <summary>Stride (bytes per row) of the U plane.</summary>
    public int UStride;

    /// <summary>Stride (bytes per row) of the V plane.</summary>
    public int VStride;

    // ── Hardware surface (GPU frames) ───────────────────────────────
    /// <summary>Native handle to a GPU texture surface. Zero for CPU frames.</summary>
    public nint HardwareSurface;

    // ── Pool management ─────────────────────────────────────────────
    private byte[]? rentedBuffer;

    /// <summary>
    /// The pool <see cref="rentedBuffer"/> came from. Null means the shared pool. A frame
    /// outlives the decode call that produced it, so it has to carry this rather than assume.
    /// </summary>
    private System.Buffers.ArrayPool<byte>? bufferPool;

    /// <summary>
    /// Returns the rented buffer to the pool. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        var buf = System.Threading.Interlocked.Exchange(ref rentedBuffer, null);
        if (buf is not null)
        {
            if (ReferenceEquals(bufferPool, Buffers.FrameBufferPool<byte>.Instance))
                Buffers.FrameBufferPool<byte>.Return(buf);
            else
                (bufferPool ?? System.Buffers.ArrayPool<byte>.Shared).Return(buf);
        }
    }

    /// <summary>
    /// Creates a planar YUV 4:2:0 frame from a single rented buffer with contiguous planes.
    /// The frame takes ownership of the buffer and returns it to ArrayPool on Dispose.
    /// </summary>
    public static VideoFrame CreatePlanar420(
        int width, int height,
        byte[] buffer,
        int yOffset, int yStride,
        int uOffset, int uStride,
        int vOffset, int vStride,
        long pts, long duration,
        bool is10Bit)
    {
        int ySize = yStride * height;
        int uvHeight = height / 2;
        int uSize = uStride * uvHeight;
        int vSize = vStride * uvHeight;

        return new VideoFrame
        {
            Width = width,
            Height = height,
            Format = is10Bit ? PixelFormat.Yuv420P10 : PixelFormat.Yuv420P,
            Pts = pts,
            Duration = duration,
            YPlane = new Memory<byte>(buffer, yOffset, ySize),
            UPlane = new Memory<byte>(buffer, uOffset, uSize),
            VPlane = new Memory<byte>(buffer, vOffset, vSize),
            YStride = yStride,
            UStride = uStride,
            VStride = vStride,
            rentedBuffer = buffer,
        };
    }

    /// <summary>
    /// Creates a hardware surface frame (no CPU pixel data).
    /// </summary>
    public static VideoFrame CreateHardwareSurface(
        int width, int height, nint surface, long pts, long duration) => new()
    {
        Width = width,
        Height = height,
        Format = PixelFormat.HardwareSurface,
        Pts = pts,
        Duration = duration,
        HardwareSurface = surface,
    };

    /// <summary>
    /// Allocates a new YUV 4:2:0 frame with ArrayPool backing.
    /// Strides are set to width (Y) and width/2 (UV) with no padding.
    /// </summary>
    public static VideoFrame AllocatePlanar420(int width, int height, long pts)
    {
        int yStride = width;
        int uvStride = width / 2;
        int uvHeight = height / 2;
        int ySize = yStride * height;
        int uSize = uvStride * uvHeight;
        int vSize = uvStride * uvHeight;
        int totalSize = ySize + uSize + vSize;

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(totalSize);

        return new VideoFrame
        {
            Width = width,
            Height = height,
            Format = PixelFormat.Yuv420P,
            Pts = pts,
            YPlane = new Memory<byte>(buffer, 0, ySize),
            UPlane = new Memory<byte>(buffer, ySize, uSize),
            VPlane = new Memory<byte>(buffer, ySize + uSize, vSize),
            YStride = yStride,
            UStride = uvStride,
            VStride = uvStride,
            rentedBuffer = buffer,
        };
    }

    /// <summary>
    /// Takes ownership of a DecodedVideoFrame's buffer, creating a zero-copy VideoFrame.
    /// The source DecodedVideoFrame must not be disposed separately after this call.
    /// </summary>
    /// <remarks>
    /// Takes <paramref name="source"/> by reference so the detach is visible to the caller: a
    /// DecodedVideoFrame is a struct, and detaching from a by-value copy would leave the caller's
    /// frame still holding a buffer it no longer owns, which a later Dispose would hand back to the
    /// pool a second time. The planes are read before the detach rather than after - they describe
    /// the buffer, and reading them from a frame that has given it up reads state mid-teardown.
    /// </remarks>
    public static VideoFrame TakeOwnership(ref Core.Codecs.DecodedVideoFrame source)
    {
        var yPlaneSource = source.YPlane;
        var uPlaneSource = source.UPlane;
        var vPlaneSource = source.VPlane;

        var buffer = source.DetachBuffer();

        // Reconstruct writable Memory<byte> slices from the ReadOnlyMemory planes.
        // This is safe because we now own the buffer.
        var yPlane = RecoverWritableSlice(yPlaneSource, buffer);
        var uPlane = RecoverWritableSlice(uPlaneSource, buffer);
        var vPlane = RecoverWritableSlice(vPlaneSource, buffer);

        return new VideoFrame
        {
            Width = source.Width,
            Height = source.Height,
            Format = source.Format,
            Pts = source.PresentationTimeTicks,
            YPlane = yPlane,
            UPlane = uPlane,
            VPlane = vPlane,
            YStride = source.YStride,
            UStride = source.UStride,
            VStride = source.VStride,
            rentedBuffer = buffer,
            // Ownership moves with the buffer, and so must the pool it has to go back to.
            bufferPool = source.BufferPool,
        };
    }

    private static Memory<byte> RecoverWritableSlice(ReadOnlyMemory<byte> readOnly, byte[] buffer)
    {
        if (readOnly.Length == 0) return Memory<byte>.Empty;
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(readOnly, out var segment)
            && ReferenceEquals(segment.Array, buffer))
        {
            return new Memory<byte>(buffer, segment.Offset, segment.Count);
        }
        // Fallback: copy (should not happen for DecodedVideoFrame)
        var copy = readOnly.ToArray();
        return copy.AsMemory();
    }
}

/// <summary>
/// Decoded audio samples ready for playback.
/// </summary>
public readonly struct AudioBuffer
{
    /// <summary>
    /// Pointer to interleaved 32-bit float samples.
    /// </summary>
    public readonly nint Samples;

    /// <summary>
    /// Number of samples per channel.
    /// </summary>
    public readonly int SampleCount;

    /// <summary>
    /// Number of audio channels.
    /// </summary>
    public readonly int ChannelCount;

    /// <summary>
    /// Sample rate in Hz.
    /// </summary>
    public readonly int SampleRate;

    /// <summary>
    /// Presentation timestamp for the first sample.
    /// </summary>
    public readonly long PresentationTimestamp;

    public AudioBuffer(
        nint samples,
        int sampleCount,
        int channelCount,
        int sampleRate,
        long presentationTimestamp)
    {
        Samples = samples;
        SampleCount = sampleCount;
        ChannelCount = channelCount;
        SampleRate = sampleRate;
        PresentationTimestamp = presentationTimestamp;
    }
}

/// <summary>
/// Poolable audio frame backed by Memory&lt;float&gt;.
/// Interleaved float samples, returned to ArrayPool on Dispose.
/// Analogous to VideoFrame for the decode pipeline.
/// </summary>
public struct AudioFrame : IDisposable
{
    /// <summary>Interleaved float samples (all channels).</summary>
    public Memory<float> Samples;

    /// <summary>Number of samples per channel.</summary>
    public int SampleCount;

    /// <summary>Number of audio channels.</summary>
    public int Channels;

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate;

    /// <summary>Presentation timestamp in ticks.</summary>
    public long Pts;

    private float[]? rentedBuffer;

    /// <summary>
    /// Total number of float elements: SampleCount × Channels.
    /// </summary>
    public readonly int TotalSampleElements => SampleCount * Channels;

    /// <summary>
    /// Duration of this frame in ticks.
    /// </summary>
    public readonly long DurationTicks => SampleRate > 0
        ? (long)SampleCount * TimeSpan.TicksPerSecond / SampleRate
        : 0;

    /// <summary>
    /// Creates an AudioFrame from interleaved float samples, renting from ArrayPool.
    /// </summary>
    public static AudioFrame Create(
        ReadOnlySpan<float> interleavedSamples,
        int sampleCount,
        int channels,
        int sampleRate,
        long pts)
    {
        int totalElements = sampleCount * channels;
        var buffer = System.Buffers.ArrayPool<float>.Shared.Rent(totalElements);
        interleavedSamples[..totalElements].CopyTo(buffer);

        return new AudioFrame
        {
            Samples = new Memory<float>(buffer, 0, totalElements),
            SampleCount = sampleCount,
            Channels = channels,
            SampleRate = sampleRate,
            Pts = pts,
            rentedBuffer = buffer,
        };
    }

    /// <summary>
    /// Copies a <see cref="Core.Codecs.DecodedAudioFrame"/> into a pooled buffer this frame owns.
    /// </summary>
    /// <remarks>
    /// It used to try to alias the decoder's array and fall back to a copy, with the comment "we
    /// don't own this array (decoder may reuse it)" on the aliasing branch. That branch was
    /// unsafe the moment a decoder actually did reuse its buffer, and the pipeline makes it
    /// certain: after handing a frame to its channel it pumps the same decoder again for the next
    /// one, so an aliased frame would be overwritten before any reader saw it. The copy is into
    /// an <c>ArrayPool</c> buffer returned by <see cref="Dispose"/>, so it costs one memcpy and
    /// no allocation - which is the trade this takes deliberately, because the alternative is a
    /// buffer-ownership protocol across eleven decoders for the sake of a few kilobytes of
    /// copying per frame.
    /// </remarks>
    public static AudioFrame TakeOwnership(Core.Codecs.DecodedAudioFrame decoded)
        => Create(decoded.Samples.Span, decoded.SampleCount, decoded.Channels,
                  decoded.SampleRate, decoded.PresentationTimeTicks);

    /// <summary>
    /// Returns the rented buffer to the pool. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        var buf = Interlocked.Exchange(ref rentedBuffer, null);
        if (buf != null)
        {
            System.Buffers.ArrayPool<float>.Shared.Return(buf);
        }
    }
}

/// <summary>
/// Canonical pixel format enumeration for all video data in the pipeline.
/// Names follow FFmpeg conventions (yuv420p, yuv422p, etc.) for familiarity.
/// Hardware-specific pixel formats are tracked separately by HardwarePixelFormat.
/// </summary>
public enum PixelFormat
{
    /// <summary>Unknown / not yet determined.</summary>
    Unknown,

    // ── Planar YUV 8-bit ─────────────────────────────────────────────
    /// <summary>Planar YUV 4:2:0, 8-bit per component (most common).</summary>
    Yuv420P,
    /// <summary>Planar YUV 4:2:2, 8-bit per component.</summary>
    Yuv422P,
    /// <summary>Planar YUV 4:4:4, 8-bit per component.</summary>
    Yuv444P,

    // ── Planar YUV 10-bit ────────────────────────────────────────────
    /// <summary>Planar YUV 4:2:0, 10-bit per component (HDR).</summary>
    Yuv420P10,
    /// <summary>Planar YUV 4:2:2, 10-bit per component.</summary>
    Yuv422P10,
    /// <summary>Planar YUV 4:4:4, 10-bit per component.</summary>
    Yuv444P10,

    // ── Planar YUV 12-bit ────────────────────────────────────────────
    /// <summary>Planar YUV 4:2:0, 12-bit per component (professional).</summary>
    Yuv420P12,
    /// <summary>Planar YUV 4:2:2, 12-bit per component.</summary>
    Yuv422P12,
    /// <summary>Planar YUV 4:4:4, 12-bit per component.</summary>
    Yuv444P12,

    // ── Semi-planar YUV ──────────────────────────────────────────────
    /// <summary>Semi-planar YUV 4:2:0: Y plane + interleaved UV (common for hardware).</summary>
    Nv12,
    /// <summary>Semi-planar YUV 4:2:0: Y plane + interleaved VU.</summary>
    Nv21,
    /// <summary>10-bit semi-planar YUV 4:2:0 (HDR hardware surfaces).</summary>
    P010,

    // ── Packed RGB/BGR ───────────────────────────────────────────────
    /// <summary>Packed RGB, 24 bits per pixel (8:8:8).</summary>
    Rgb24,
    /// <summary>Packed BGR, 24 bits per pixel (8:8:8).</summary>
    Bgr24,
    /// <summary>Packed RGBA, 32 bits per pixel (8:8:8:8).</summary>
    Rgba,
    /// <summary>Packed BGRA, 32 bits per pixel (8:8:8:8).</summary>
    Bgra,

    // ── Packed YUV ───────────────────────────────────────────────────
    /// <summary>Packed YUV 4:2:2 (Y0 U Y1 V per 2 pixels).</summary>
    Yuyv422,
    /// <summary>Packed YUV 4:2:2 (U Y0 V Y1 per 2 pixels).</summary>
    Uyvy422,

    // ── Grayscale ────────────────────────────────────────────────────
    /// <summary>8-bit grayscale (single luminance plane).</summary>
    Gray8,
    /// <summary>16-bit grayscale (little-endian).</summary>
    Gray16,

    // ── Planar RGB (professional formats) ────────────────────────────
    /// <summary>
    /// Planar GBR 8-bit - three full-resolution planes in green, blue, red order (FFV1,
    /// ProRes, and an RGB JPEG, whose components are coded R, G, B and whose planes are
    /// rotated into this order at the end of the decode).
    /// </summary>
    Gbrp,
    /// <summary>Planar GBR 10-bit (little-endian).</summary>
    Gbrp10,

    // ── Special ──────────────────────────────────────────────────────
    /// <summary>Opaque hardware surface handle (GPU texture). Consult HardwarePixelFormat for details.</summary>
    HardwareSurface,
}

// ─── Pipeline Packet Infrastructure ────────────────────────────────────────
// Unlike the ref struct MediaPacket (which is a temporary view for inline processing),
// PipelinePacket is a storable struct that can cross async boundaries, be queued
// in channels, and be safely returned to a buffer pool.

/// <summary>
/// Flags for pipeline packets indicating special properties.
/// </summary>
[Flags]
public enum PipelinePacketFlags
{
    None = 0,
    Keyframe = 1 << 0,
    EndOfStream = 1 << 1,
    Corrupted = 1 << 2,
    Discard = 1 << 3,
}

/// <summary>
/// A storable media packet backed by pooled memory.
/// Can be stored in channels, queues, and passed across async boundaries.
/// Call <see cref="Dispose"/> to return the underlying buffer to the pool.
/// </summary>
/// <remarks>
/// This complements the <see cref="MediaPacket"/> ref struct: use MediaPacket
/// for inline zero-alloc processing within a single method scope, and
/// PipelinePacket when the packet must be stored or queued.
/// </remarks>
public struct PipelinePacket : IDisposable
{
    /// <summary>Index of the stream (track) this packet belongs to.</summary>
    public int StreamIndex;

    /// <summary>Presentation timestamp in TimeSpan ticks.</summary>
    public long Pts;

    /// <summary>Decode timestamp in TimeSpan ticks.</summary>
    public long Dts;

    /// <summary>Duration of this packet in TimeSpan ticks.</summary>
    public long Duration;

    /// <summary>Packet flags (keyframe, end-of-stream, etc.).</summary>
    public PipelinePacketFlags Flags;

    /// <summary>The actual payload length within the rented buffer.</summary>
    public int PayloadLength;

    // Backing storage — rented from ArrayPool, returned on Dispose()
    private byte[]? rentedBuffer;

    /// <summary>
    /// Gets the compressed payload data as a read-only span.
    /// </summary>
    public readonly ReadOnlySpan<byte> Payload =>
        rentedBuffer is not null ? new ReadOnlySpan<byte>(rentedBuffer, 0, PayloadLength) : default;

    /// <summary>
    /// Gets the compressed payload as Memory for async operations.
    /// </summary>
    public readonly ReadOnlyMemory<byte> PayloadMemory =>
        rentedBuffer is not null ? new ReadOnlyMemory<byte>(rentedBuffer, 0, PayloadLength) : default;

    /// <summary>Whether this packet contains a keyframe.</summary>
    public readonly bool IsKeyframe => (Flags & PipelinePacketFlags.Keyframe) != 0;

    /// <summary>Whether this is an end-of-stream sentinel.</summary>
    public readonly bool IsEndOfStream => (Flags & PipelinePacketFlags.EndOfStream) != 0;

    /// <summary>
    /// Creates a PipelinePacket by renting a buffer from ArrayPool and copying data into it.
    /// </summary>
    public static PipelinePacket Create(
        ReadOnlySpan<byte> payload,
        int streamIndex,
        long pts,
        long dts,
        long duration,
        PipelinePacketFlags flags)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(buffer);

        return new PipelinePacket
        {
            StreamIndex = streamIndex,
            Pts = pts,
            Dts = dts,
            Duration = duration,
            Flags = flags,
            PayloadLength = payload.Length,
            rentedBuffer = buffer,
        };
    }

    /// <summary>
    /// Creates an end-of-stream sentinel packet (no payload).
    /// </summary>
    public static PipelinePacket CreateEndOfStream(int streamIndex) => new()
    {
        StreamIndex = streamIndex,
        Flags = PipelinePacketFlags.EndOfStream,
    };

    /// <summary>
    /// Returns the rented buffer to the pool. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        var buf = System.Threading.Interlocked.Exchange(ref rentedBuffer, null);
        if (buf is not null)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        }
    }
}

// MJPEG as the rest of the project sees it: an IVideoDecoder the codec registry can hand a
// stream to.
//
// There is no access-unit assembly. Every container that carries Motion JPEG - AVI, MOV, SMJPEG
// - stores one coded picture per sample, and a picture is SOI..EOI, which the decoder finds for
// itself. An interlaced Motion JPEG frame is TWO SOI..EOI images in one sample, and the decoder
// handles that too: the first field does not end the picture.

using System;
using System.Buffers;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Mjpeg;

/// <summary>Motion JPEG and still JPEG, registered for the codec registry.</summary>
/// <remarks>
/// <para>
/// Every frame is a key frame and nothing is reordered, so one packet in is one frame out -
/// with one exception that is not a reorder: an EMPTY packet. AVI uses a zero-length chunk to
/// say "show the previous frame again", and six of the nine chunks in FFmpeg's own
/// <c>mjpeg_field_order.avi</c> are empty. Those must produce nothing, which is why this
/// returns null for them rather than decoding an empty buffer.
/// </para>
/// <para>
/// REQUIRED BEFORE <see cref="Initialize"/> for an interlaced stream: <see cref="CodedHeight"/>.
/// A Motion JPEG field carries its own height in SOF and nothing says it is a field - the only
/// way to notice is that the container declares twice it. FFmpeg uses
/// <c>avctx-&gt;coded_height</c> for exactly this, and without it an interlaced stream decodes
/// as a sequence of half-height pictures.
/// </para>
/// </remarks>
[Codec("mjpeg", CodecType.Video, DisplayName = "Motion JPEG Software Decoder", Priority = 5)]
public sealed class MjpegVideoDecoder : IVideoDecoder
{
    private readonly MJpegDecodeContext context = new();
    private bool initialized;
    private byte[] packetBuffer = [];

    /// <inheritdoc/>
    public string CodecId => "mjpeg";

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <summary>
    /// The height the container declares, which is the only way to tell an interlaced Motion
    /// JPEG's fields from half-height frames. Set before <see cref="Initialize"/>.
    /// </summary>
    public int CodedHeight { get; set; }

    /// <summary>
    /// The container's FourCC, little-endian. A Motion JPEG tagged <c>MJPG</c> whose container
    /// declares no field order is bottom-field-first - ffmpeg ticket 3229 - and nothing in the
    /// bitstream says so.
    /// </summary>
    public uint CodecTag { get; set; }

    /// <summary>An <see cref="MjpegFieldOrder"/> value from the container, when it has one.</summary>
    public int FieldOrder { get; set; } = MjpegFieldOrder.Unknown;

    /// <summary>
    /// Read the Huffman tables from the codec private data - FFmpeg's <c>extern_huff</c>.
    /// </summary>
    /// <remarks>
    /// Off by default, as the AVOption is, and for the same reason: an AVI MJPG track whose
    /// stream header holds a DHT segment looks exactly like one whose header holds something
    /// else, so turning this on is a decision the caller makes about a particular file. With
    /// it on, <see cref="Initialize"/> hands the codec private data to the decoder as a DHT
    /// segment body; a blob that does not parse falls back to the standard tables, which is
    /// what the C does.
    /// </remarks>
    public bool ExternHuff { get; set; }

    /// <inheritdoc/>
    public int Width => context.Width;

    /// <inheritdoc/>
    public int Height => context.FrameHeight;

    /// <inheritdoc/>
    public PixelFormat OutputFormat => context.PixelFormat;

    /// <summary>Whether a picture has been decoded and the format is known.</summary>
    public bool IsReady => context.GotPicture != 0;

    /// <inheritdoc/>
    /// <remarks>
    /// JPEG carries everything in band, so the codec private data is normally unused. The
    /// exception is an AVI MJPG track that holds its Huffman tables in the stream header and
    /// omits DHT from every frame - see <see cref="ExternHuff"/>, which is what reads them.
    /// </remarks>
    public bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        MjpegDec.MjpegDecodeInit(context, CodedHeight, FieldOrder, CodecTag,
                                 ExternHuff ? codecPrivate : default);
        initialized = true;
        return true;
    }

    /// <inheritdoc/>
    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks,
                                     bool isKeyframe)
    {
        if (!initialized)
            throw new InvalidOperationException("MJPEG: Decode before Initialize.");

        // An empty packet is a container saying "nothing new here", not an end of stream and
        // not a frame.
        if (data.IsEmpty)
            return null;

        byte[] packet = GrowPacket(data.Length);
        data.CopyTo(packet);

        context.GotPicture = 0;
        if (MjpegDec.DecodeImage(context, packet, data.Length) < 0)
            return null;
        if (context.GotPicture == 0)
            return null;

        return Extract(presentationTimeTicks);
    }

    /// <summary>
    /// Nothing is held back: every JPEG is complete in its own packet, so there is no reorder
    /// buffer to drain.
    /// </summary>
    public void DrainPendingFrames()
    {
    }

    /// <summary>
    /// Nothing carries across a seek either - no reference frames, no predictor state that
    /// outlives a picture.
    /// </summary>
    public void Flush()
    {
    }

    /// <inheritdoc/>
    public void Dispose() => packetBuffer = [];

    /// <summary>
    /// The decoder's planes, copied into a frame the caller owns.
    /// </summary>
    /// <remarks>
    /// The decoder reuses its picture buffer for the next frame, so the frame handed out has
    /// to be a copy - the same shape as <c>Mpeg2VideoDecoder.Collect</c>.
    /// </remarks>
    private DecodedVideoFrame Extract(long pts)
    {
        int width = context.Width;
        int height = context.FrameHeight;
        int chromaWidth = context.ChromaWidth;

        // ChromaHeight is the FIELD's, because that is what the scan tests against; the plane
        // handed out is the frame's.
        int chromaHeight = context.Interlaced != 0
            ? context.ChromaHeight * 2
            : context.ChromaHeight;

        int lumaSize = width * height;
        int chromaSize = chromaWidth * chromaHeight;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(lumaSize + 2 * chromaSize);

        CopyPlane(0, width, height, buffer, 0);
        CopyPlane(1, chromaWidth, chromaHeight, buffer, lumaSize);
        CopyPlane(2, chromaWidth, chromaHeight, buffer, lumaSize + chromaSize);

        return new DecodedVideoFrame(
            width, height, context.PixelFormat, pts,
            buffer,
            0, width,
            lumaSize, chromaWidth,
            lumaSize + chromaSize, chromaWidth);
    }

    private void CopyPlane(int plane, int width, int height, byte[] destination, int at)
    {
        int source = context.PlaneOffset[plane];

        // The context's line size is the one the SCAN walks, which is doubled for a field
        // picture; the plane's own stride is half of it.
        int stride = context.Interlaced != 0
            ? context.Linesize[plane] >> 1
            : context.Linesize[plane];

        for (int y = 0; y < height; y++)
        {
            context.Picture.AsSpan(source + y * stride, width)
                   .CopyTo(destination.AsSpan(at + y * width));
        }
    }

    private byte[] GrowPacket(int length)
    {
        int needed = length + MjpegDec.InputBufferPaddingSize;
        if (packetBuffer.Length < needed)
            packetBuffer = new byte[needed];

        // The padding exists so a bit reader that runs past the end reads zeroes, and it has
        // to be zeroes on every packet rather than only the first.
        Array.Clear(packetBuffer, length, packetBuffer.Length - length);
        return packetBuffer;
    }
}

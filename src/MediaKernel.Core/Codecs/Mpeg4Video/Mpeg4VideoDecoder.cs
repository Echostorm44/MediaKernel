// MPEG-4 Part 2 as the rest of the project sees it: an IVideoDecoder the codec registry can hand
// a track to. The decoder itself has been bit-exact against ffmpeg over five streams since the
// `mpeg4` gate landed, and was reachable from nothing - no [Codec] attribute, so a correctly
// named mpeg4 track in an AVI, an MP4 or a Matroska file found no decoder and played nothing.
//
// Two things this has to get right that the conformance tests did not have to:
//
// 1. A container hands over whole packets, and a DivX-era packed packet holds a P picture and
//    the B that displays before it. The conformance tests split the elementary stream on VOP
//    start codes themselves, so the packed path - ff_mpeg4_frame_end's stash and
//    mpeg4_decode_picture_header's consumption of it - never ran. It runs here.
// 2. That stash points INTO the packet it was given and does not copy it, exactly as the C's
//    av_buffer_replace(&ctx->bitstream_buffer, pkt->buf) takes a reference on the caller's
//    buffer. So a packet must stay unmodified until the call after it. The pipeline hands over a
//    span it owns and refills, so the bytes are copied here - into two buffers used alternately,
//    which is the cheapest way to honour that contract without allocating per packet.

using System;
using System.Buffers;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Mpeg4Video;

/// <summary>MPEG-4 Part 2 (Simple and Advanced Simple profile), for the codec registry.</summary>
/// <remarks>
/// <para>
/// One picture comes out per call at most, and often none: an N-VOP codes "the previous picture
/// again" and produces nothing, and a reordered stream holds its first picture back. The last
/// one is collected by <see cref="DrainPendingFrames"/>, which is what an empty packet means to
/// <c>ff_h263_decode_frame</c>.
/// </para>
/// <para>
/// KNOWN LIMITATION, the same one MPEG-2 states: the timestamp handed to <see cref="Decode"/> is
/// attached to the picture that call outputs, which for a reordered stream is the previous one.
/// Frame order is correct; the timestamps lag by one picture until the container's PTS/DTS pair
/// is carried through the reorder the way FFmpeg's parser does.
/// </para>
/// </remarks>
[Codec("mpeg4", CodecType.Video, DisplayName = "MPEG-4 Part 2 Software Decoder", Priority = 5)]
public sealed class Mpeg4VideoDecoder : IVideoDecoder
{
    /// <summary>
    /// <c>AV_INPUT_BUFFER_PADDING_SIZE</c>. FFmpeg requires every packet handed to a decoder to
    /// be followed by this many zero bytes, because its bit readers read in words and may read
    /// past the last byte they use. A buffer reused between packets would otherwise leave the
    /// tail of the longer previous packet there, which is not zero.
    /// </summary>
    private const int InputBufferPadding = 64;

    private readonly Mpeg4DecContext ctx = new();

    /// <summary>
    /// The last two packets, used alternately. <c>ff_mpeg4_frame_end</c> keeps a reference to
    /// the packet it was handed and reads the next picture out of it, so the one before the
    /// current must still hold its bytes.
    /// </summary>
    private byte[] packetA = [];
    private byte[] packetB = [];
    private bool useB;

    private PixelFormat outputFormat = PixelFormat.Yuv420P;

    public Mpeg4VideoDecoder() => Mpeg4VideoDec.Mpeg4DecodeInit(ctx);

    /// <inheritdoc/>
    public string CodecId => "mpeg4";

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <inheritdoc/>
    public int Width => ctx.C.Width;

    /// <inheritdoc/>
    public int Height => ctx.C.Height;

    /// <inheritdoc/>
    public PixelFormat OutputFormat => outputFormat;

    /// <summary>
    /// MPEG-4 Part 2 carries its configuration in the visual object sequence, object and layer
    /// headers, which are in band at the head of the first packet. An MP4 or Matroska file also
    /// repeats them out of band, and feeding that copy in would decode the VOL header twice;
    /// the in-band one is the copy the C reads, so this ignores it.
    /// </summary>
    public bool Initialize(ReadOnlySpan<byte> codecPrivate) => true;

    /// <inheritdoc/>
    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks, bool isKeyframe)
    {
        byte[] packet;
        if (data.IsEmpty)
        {
            // An empty call is the flush, and must not disturb either held packet: the stash may
            // still point at one of them.
            packet = [];
        }
        else
        {
            packet = Take(data.Length);
            data.CopyTo(packet);
            packet.AsSpan(data.Length, InputBufferPadding).Clear();
        }

        if (!H263Dec.H263DecodeFrame(ctx, packet, data.Length))
            return null;

        return ExtractOutputFrame(presentationTimeTicks);
    }

    /// <inheritdoc/>
    public void DrainPendingFrames()
    {
        // Nothing is held here - the decoder frames its own input and the one picture a
        // reordered stream owes is collected by the empty Decode call that follows.
    }

    /// <summary>
    /// <c>mpeg4_flush</c>: drops the three reference pictures and the packed-bitstream stash.
    /// Anything already handed to the caller is theirs.
    /// </summary>
    public void Flush()
    {
        Mpeg4VideoDec.Mpeg4Flush(ctx);
        useB = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Flush();
        packetA = [];
        packetB = [];
    }

    /// <summary>
    /// The buffer this packet goes in - the one the packet before it did not use.
    /// </summary>
    private byte[] Take(int length)
    {
        useB = !useB;
        ref byte[] slot = ref useB ? ref packetB : ref packetA;

        if (slot.Length < length + InputBufferPadding)
        {
            slot = new byte[Math.Max(length + InputBufferPadding,
                                     Math.Max(slot.Length * 2, 1 << 16))];
        }

        return slot;
    }

    /// <summary>
    /// Copies the picture the decoder just output into a frame the caller owns. The decoder's
    /// own planes are padded and pooled and the slot is reused as soon as the next picture needs
    /// it, which is why this is a copy - the same shape as MPEG-2's and VP8's.
    /// </summary>
    private DecodedVideoFrame ExtractOutputFrame(long presentationTimeTicks)
    {
        ref MpegEncContext s = ref ctx.C;
        ref MPVWorkPicture pic = ref ctx.OutputPic;

        outputFormat = (s.ChromaXShift, s.ChromaYShift) switch
        {
            (1, 1) => PixelFormat.Yuv420P,
            (1, 0) => PixelFormat.Yuv422P,
            (0, 0) => PixelFormat.Yuv444P,
            // Unknown input fails loudly: a wrong guess hands the renderer chroma planes of the
            // wrong size, which looks like a decode bug rather than a format one.
            _ => throw new InvalidOperationException(
                $"MPEG-4: unsupported chroma subsampling {s.ChromaXShift}/{s.ChromaYShift}."),
        };

        int width = s.Width;
        int height = s.Height;
        int chromaWidth = width >> s.ChromaXShift;
        int chromaHeight = height >> s.ChromaYShift;

        int lumaSize = width * height;
        int chromaSize = chromaWidth * chromaHeight;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(lumaSize + 2 * chromaSize);

        CopyPlane(in pic, 0, buffer, 0, width, height);
        CopyPlane(in pic, 1, buffer, lumaSize, chromaWidth, chromaHeight);
        CopyPlane(in pic, 2, buffer, lumaSize + chromaSize, chromaWidth, chromaHeight);

        return new DecodedVideoFrame(
            width, height, outputFormat, presentationTimeTicks,
            buffer,
            0, width,
            lumaSize, chromaWidth,
            lumaSize + chromaSize, chromaWidth);
    }

    private static void CopyPlane(
        in MPVWorkPicture pic, int plane, byte[] destination, int destinationOffset,
        int planeWidth, int planeHeight)
    {
        byte[] frame = pic.Frame!;
        int source = pic.Data[plane];
        int stride = (int)pic.Linesize[plane];

        for (int y = 0; y < planeHeight; y++)
            frame.AsSpan(source + y * stride, planeWidth)
                 .CopyTo(destination.AsSpan(destinationOffset + y * planeWidth));
    }
}

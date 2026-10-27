// ITU-T H.263 as the rest of the project sees it.
//
// The decoder itself has been gated since the H.263 work - two QCIF streams, 60 of 60
// pictures bit-exact - and was reachable from nothing: no [Codec] attribute, so a container
// naming a track 'h263' found no decoder at all. This is that registration, and it is the
// same shape as Mpeg4VideoDecoder because the two share ff_h263_decode_frame.

using System;
using System.Buffers;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Mpeg4Video;

/// <summary>ITU-T H.263, for the codec registry.</summary>
/// <remarks>
/// <para>
/// One packet is one picture. That is true of H.263 in a container - AVI, 3GP and RTP all
/// frame it - and it is why there is no parser here: a RAW .h263 elementary stream has to be
/// split on its picture start codes by whatever is reading it, the same way a raw H.264
/// stream needs an access-unit reader.
/// </para>
/// <para>
/// H.263 is low delay. <c>H263DecodeInit</c> sets <c>low_delay = 1</c>, so nothing is ever
/// held back and the picture a call outputs is the picture it was given.
/// </para>
/// </remarks>
[Codec("h263", CodecType.Video, DisplayName = "H.263 Software Decoder", Priority = 5)]
public sealed class H263VideoDecoder : IVideoDecoder
{
    /// <summary><c>AV_INPUT_BUFFER_PADDING_SIZE</c>.</summary>
    private const int InputBufferPadding = 64;

    private readonly H263DecContext ctx = new();
    private byte[] packetBuffer = [];

    /// <summary>Creates a decoder, initialised as <c>ff_h263_decode_init</c> does.</summary>
    public H263VideoDecoder() => H263Dec.H263DecodeInit(ctx);

    /// <inheritdoc/>
    public string CodecId => "h263";

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <inheritdoc/>
    public int Width => ctx.C.Width;

    /// <inheritdoc/>
    public int Height => ctx.C.Height;

    /// <inheritdoc/>
    public PixelFormat OutputFormat => PixelFormat.Yuv420P;

    /// <inheritdoc/>
    /// <remarks>H.263 carries everything in band; there is no codec private data.</remarks>
    public bool Initialize(ReadOnlySpan<byte> codecPrivate) => true;

    /// <inheritdoc/>
    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks,
                                     bool isKeyframe)
    {
        if (data.IsEmpty)
            return null;

        if (packetBuffer.Length < data.Length + InputBufferPadding)
            packetBuffer = new byte[data.Length + InputBufferPadding];

        data.CopyTo(packetBuffer);
        packetBuffer.AsSpan(data.Length, InputBufferPadding).Clear();

        if (!H263Dec.H263DecodeFrame(ctx, packetBuffer, data.Length))
            return null;

        return Extract(presentationTimeTicks);
    }

    /// <summary>Nothing is held back: H.263 is low delay.</summary>
    public void DrainPendingFrames()
    {
    }

    /// <summary>
    /// <c>ff_mpeg_flush</c>, which is what h263dec.c registers. It drops the reference
    /// pictures: a seek lands on a picture whose predecessor was never decoded, and keeping
    /// the old reference would predict from the wrong picture. The MPEG-4 decoder's flush
    /// is a different function, because it also has a packed-bitstream stash to clear.
    /// </summary>
    public void Flush() => MpegVideoDec.MpegFlush(ref ctx.C);

    /// <inheritdoc/>
    public void Dispose() => packetBuffer = [];

    /// <summary>
    /// Copies the picture out. The decoder's planes are padded and pooled and the slot is
    /// reused as soon as the next picture needs it, which is why this is a copy - the same
    /// shape as MPEG-4's and MPEG-2's.
    /// </summary>
    private DecodedVideoFrame Extract(long presentationTimeTicks)
    {
        ref MpegEncContext s = ref ctx.C;
        ref MPVWorkPicture pic = ref ctx.OutputPic;

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
            width, height, PixelFormat.Yuv420P, presentationTimeTicks,
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

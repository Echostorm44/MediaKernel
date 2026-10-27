// Nintendo GameCube THP video as the rest of the project sees it.
//
// THP video is ordinary JPEG with the escaping removed, which is why ff_thp_decoder in
// mjpegdec.c is a codec entry and not a decoder: it points at ff_mjpeg_decode_frame and the
// three places that behave differently are codec-id tests inside the shared code. Those are
// two flags on the context here.

using System;
using System.Buffers;

namespace MediaKernel.Core.Codecs.Mjpeg;

/// <summary>Nintendo GameCube THP video, registered for the codec registry.</summary>
/// <remarks>
/// <para>
/// The two flags are what this adapter exists to set. <c>InputIsUnescaped</c> says the
/// entropy data carries no <c>0xFF 0x00</c> stuffing, so the scan's buffer is the data
/// verbatim - the same flag MJPEG-B needs. <c>AlignedRestarts</c> says a restart marker needs
/// the bit reader put back on a byte boundary rather than another unescape pass, which is
/// THP's alone.
/// </para>
/// <para>
/// A THP frame is a complete JPEG with markers, so unlike MJPEG-B and AMV there is nothing to
/// reassemble: the ordinary marker loop reads it.
/// </para>
/// </remarks>
[Codec("thp", CodecType.Video, DisplayName = "THP Video Software Decoder", Priority = 5)]
public sealed class ThpVideoDecoder : IVideoDecoder
{
    private readonly MJpegDecodeContext context = new();
    private bool initialized;
    private byte[] packetBuffer = [];

    /// <inheritdoc/>
    public string CodecId => "thp";

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <inheritdoc/>
    public int Width => context.Width;

    /// <inheritdoc/>
    public int Height => context.FrameHeight;

    /// <inheritdoc/>
    public PixelFormat OutputFormat => context.PixelFormat;

    /// <inheritdoc/>
    public bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        MjpegDec.MjpegDecodeInit(context, codedHeight: 0);
        context.InputIsUnescaped = true;
        context.AlignedRestarts = true;
        initialized = true;
        return true;
    }

    /// <inheritdoc/>
    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks,
                                     bool isKeyframe)
    {
        if (!initialized)
            throw new InvalidOperationException("THP: Decode before Initialize.");

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

    /// <summary>One packet is one frame; nothing is held back.</summary>
    public void DrainPendingFrames()
    {
    }

    /// <summary>Nothing carries across a seek.</summary>
    public void Flush()
    {
    }

    /// <inheritdoc/>
    public void Dispose() => packetBuffer = [];

    private DecodedVideoFrame Extract(long pts)
    {
        int width = context.Width;
        int height = context.FrameHeight;
        int chromaWidth = context.ChromaWidth;
        int chromaHeight = context.ChromaHeight;

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
        int stride = context.Linesize[plane];

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

        Array.Clear(packetBuffer, length, packetBuffer.Length - length);
        return packetBuffer;
    }
}

// Media 100 as the rest of the project sees it.
//
// ff_media100_decoder in the C is ff_mjpegb_decoder with `.bsfs = "media100_to_mjpegb"` and
// no decoder code of its own, so this is that pairing: the filter rebuilds the MJPEG-B frame
// structure the format leaves out, and MjpegbDec decodes the result.

using System;
using System.Buffers;

namespace MediaKernel.Core.Codecs.Mjpeg;

/// <summary>Media 100, registered for the codec registry.</summary>
/// <remarks>
/// REQUIRED BEFORE <see cref="Initialize"/>: <see cref="CodedWidth"/> and
/// <see cref="CodedHeight"/>. A Media 100 packet carries no SOF, so the filter has to be told
/// the picture size - and it writes HALF the height into the SOF it builds, because a packet
/// is two fields.
/// </remarks>
[Codec("media100", CodecType.Video, DisplayName = "Media 100 Software Decoder", Priority = 5)]
public sealed class Media100VideoDecoder : IVideoDecoder
{
    private readonly MJpegDecodeContext context = new();
    private bool initialized;
    private byte[] rewritten = [];

    /// <inheritdoc/>
    public string CodecId => "media100";

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <summary>The width the container declares.</summary>
    public int CodedWidth { get; set; }

    /// <summary>The height the container declares, of the whole frame.</summary>
    public int CodedHeight { get; set; }

    /// <inheritdoc/>
    public int Width => context.Width;

    /// <inheritdoc/>
    public int Height => context.FrameHeight;

    /// <inheritdoc/>
    public PixelFormat OutputFormat => context.PixelFormat;

    /// <inheritdoc/>
    public bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        if (CodedWidth <= 0 || CodedHeight <= 0)
            return false;

        // The frame the filter builds declares half the height per field, so the interlace
        // test needs the container's full height to recognise it - exactly as for MJPEG-B.
        MjpegDec.MjpegDecodeInit(context, CodedHeight);
        initialized = true;
        return true;
    }

    /// <inheritdoc/>
    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks,
                                     bool isKeyframe)
    {
        if (!initialized)
            throw new InvalidOperationException("Media 100: Decode before Initialize.");

        if (data.IsEmpty)
            return null;

        int size = Media100ToMjpegb.Filter(data, CodedWidth, CodedHeight, ref rewritten);
        if (size < 0)
            return null;

        if (MjpegbDec.MjpegbDecodeFrame(context, rewritten, size) < 0)
            return null;
        if (context.GotPicture == 0)
            return null;

        DecodedVideoFrame frame = Extract(presentationTimeTicks);
        context.GotPicture = 0;
        return frame;
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
    public void Dispose() => rewritten = [];

    private DecodedVideoFrame Extract(long pts)
    {
        int width = context.Width;
        int height = context.FrameHeight;
        int chromaWidth = context.ChromaWidth;
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
        int stride = context.Interlaced != 0
            ? context.Linesize[plane] >> 1
            : context.Linesize[plane];

        for (int y = 0; y < height; y++)
        {
            context.Picture.AsSpan(source + y * stride, width)
                   .CopyTo(destination.AsSpan(at + y * width));
        }
    }
}

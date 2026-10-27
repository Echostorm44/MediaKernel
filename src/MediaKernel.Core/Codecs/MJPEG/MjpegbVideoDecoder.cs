// Apple MJPEG-B as the rest of the project sees it.
//
// Almost everything here is the same as MjpegVideoDecoder, because almost everything about
// MJPEG-B is the same as MJPEG: the only difference is that a packet is read through
// MjpegbDec's offset header instead of by scanning for markers, and that a second field lives
// at an offset in the same packet rather than being a second SOI..EOI.

using System;
using System.Buffers;

namespace MediaKernel.Core.Codecs.Mjpeg;

/// <summary>Apple MJPEG-B, registered for the codec registry.</summary>
/// <remarks>
/// Interlacing here does NOT need the container's height, unlike ordinary Motion JPEG. An
/// MJPEG-B frame carries the offset of its second field in its own header, so the decoder can
/// see that there are two; a Motion JPEG field carries nothing that distinguishes it from a
/// short frame. <see cref="CodedHeight"/> is still accepted, because SOF in each field states
/// the FIELD height and the interlace test in the shared code is the same one.
/// </remarks>
[Codec("mjpegb", CodecType.Video, DisplayName = "Apple MJPEG-B Software Decoder", Priority = 5)]
public sealed class MjpegbVideoDecoder : IVideoDecoder
{
    private readonly MJpegDecodeContext context = new();
    private bool initialized;
    private byte[] packetBuffer = [];

    /// <inheritdoc/>
    public string CodecId => "mjpegb";

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <summary>The height the container declares.</summary>
    public int CodedHeight { get; set; }

    /// <summary>An <see cref="MjpegFieldOrder"/> value from the container, when it has one.</summary>
    public int FieldOrder { get; set; } = MjpegFieldOrder.Unknown;

    /// <inheritdoc/>
    public int Width => context.Width;

    /// <inheritdoc/>
    public int Height => context.FrameHeight;

    /// <inheritdoc/>
    public PixelFormat OutputFormat => context.PixelFormat;

    /// <inheritdoc/>
    public bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        // The codec tag is deliberately NOT passed. ffmpeg ticket 3229's rule - an MJPG tag
        // with no declared field order means bottom field first - is about AVI Motion JPEG;
        // an MJPEG-B track is tagged mjpb and states its field order in the QuickTime sample
        // description when it has one.
        MjpegDec.MjpegDecodeInit(context, CodedHeight, FieldOrder);
        initialized = true;
        return true;
    }

    /// <inheritdoc/>
    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks,
                                     bool isKeyframe)
    {
        if (!initialized)
            throw new InvalidOperationException("MJPEG-B: Decode before Initialize.");

        if (data.IsEmpty)
            return null;

        byte[] packet = GrowPacket(data.Length);
        data.CopyTo(packet);

        if (MjpegbDec.MjpegbDecodeFrame(context, packet, data.Length) < 0)
            return null;
        if (context.GotPicture == 0)
            return null;

        DecodedVideoFrame frame = Extract(presentationTimeTicks);
        context.GotPicture = 0;
        return frame;
    }

    /// <summary>Nothing is held back: one packet is one frame.</summary>
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

        // The context's line size is the one the SCAN walks, doubled for a field picture.
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

        Array.Clear(packetBuffer, length, packetBuffer.Length - length);
        return packetBuffer;
    }
}

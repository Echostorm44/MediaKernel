// AMV Video as the rest of the project sees it.
//
// AMV is the video half of the format cheap MP3/MP4 players used: a JPEG with every marker
// stripped out, wrapped in an AVI. Sp5xDec puts the markers back; this is the registry entry
// and the plane handling.

using System;
using System.Buffers;

namespace MediaKernel.Core.Codecs.Mjpeg;

/// <summary>AMV Video, registered for the codec registry.</summary>
/// <remarks>
/// <para>
/// REQUIRED BEFORE <see cref="Initialize"/>: <see cref="CodedWidth"/> and
/// <see cref="CodedHeight"/>. An AMV packet carries no SOF and therefore no picture size at
/// all - the size comes from the AVI stream header and nowhere else - and the C refuses the
/// frame outright when it is unset rather than guessing.
/// </para>
/// <para>
/// Every AMV picture is stored bottom-up. The C says so with a bare
/// <c>if (codec_id == AV_CODEC_ID_AMV) s-&gt;flipped = 1;</c> at the end of init, which is
/// the same flag two encoders set through a COM comment in ordinary Motion JPEG.
/// </para>
/// </remarks>
[Codec("amv", CodecType.Video, DisplayName = "AMV Video Software Decoder", Priority = 5)]
public sealed class AmvVideoDecoder : IVideoDecoder
{
    private readonly MJpegDecodeContext context = new();
    private bool initialized;
    private byte[] packetBuffer = [];
    private byte[] recodeBuffer = [];

    /// <inheritdoc/>
    public string CodecId => "amv";

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <summary>The width the container declares. An AMV packet carries none.</summary>
    public int CodedWidth { get; set; }

    /// <summary>The height the container declares.</summary>
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

        // codedHeight is NOT passed through as the interlace hint here. That test compares
        // the SOF height against the container's, and an AMV SOF height IS the container's
        // by construction - Sp5xDec writes it in - so the comparison can only ever say
        // "progressive", and passing it would imply a check that is not happening.
        MjpegDec.MjpegDecodeInit(context, codedHeight: 0);
        context.Flipped = 1;
        initialized = true;
        return true;
    }

    /// <inheritdoc/>
    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks,
                                     bool isKeyframe)
    {
        if (!initialized)
            throw new InvalidOperationException("AMV: Decode before Initialize.");

        if (data.IsEmpty)
            return null;

        byte[] packet = GrowPacket(data.Length);
        data.CopyTo(packet);

        context.GotPicture = 0;
        int ret = Sp5xDec.Sp5xDecodeFrame(context, packet, data.Length,
                                          CodedWidth, CodedHeight, isAmv: true,
                                          ref recodeBuffer);
        if (ret < 0 || context.GotPicture == 0)
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
    public void Dispose()
    {
        packetBuffer = [];
        recodeBuffer = [];
    }

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

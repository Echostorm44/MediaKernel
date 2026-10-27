// Cinepak as the rest of the project sees it.

using System;
using System.Buffers;

namespace MediaKernel.Core.Codecs.Cinepak;

/// <summary>Cinepak, registered for the codec registry.</summary>
/// <remarks>
/// <para>
/// REQUIRED BEFORE <see cref="Initialize"/>: <see cref="CodedWidth"/> and
/// <see cref="CodedHeight"/>. A Cinepak frame header carries strip coordinates but not the
/// picture size, so the size comes from the container.
/// </para>
/// <para>
/// <see cref="BitsPerCodedSample"/> decides the pixel format, and EXACTLY 8 means paletted.
/// The C tests it the same way round, so a container that leaves the field unset gives RGB24
/// rather than a palette lookup into nothing.
/// </para>
/// <para>
/// Cinepak is an INTER codec with no motion compensation - a block that did not change is
/// simply not coded - so the decoder keeps its picture between calls and a frame handed out
/// is a copy.
/// </para>
/// </remarks>
[Codec("cinepak", CodecType.Video, DisplayName = "Cinepak Software Decoder", Priority = 5)]
public sealed class CinepakVideoDecoder : IVideoDecoder
{
    private readonly CinepakContext context = new();
    private bool initialized;

    /// <inheritdoc/>
    public string CodecId => "cinepak";

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <summary>The width the container declares.</summary>
    public int CodedWidth { get; set; }

    /// <summary>The height the container declares.</summary>
    public int CodedHeight { get; set; }

    /// <summary>
    /// <c>codecpar-&gt;bits_per_coded_sample</c>. Exactly 8 selects the paletted format.
    /// </summary>
    public int BitsPerCodedSample { get; set; }

    /// <inheritdoc/>
    public int Width => context.DisplayWidth;

    /// <inheritdoc/>
    public int Height => context.DisplayHeight;

    /// <inheritdoc/>
    /// <remarks>
    /// Always RGB24, even for a paletted stream. DIVERGENCE: FFmpeg hands out PAL8 with the
    /// palette as a second plane and lets a filter convert - <c>fate-cvid-palette</c> is run
    /// with <c>-pix_fmt rgb24 -vf scale</c> for exactly that reason. This project has no PAL8
    /// pixel format and no palette plumbing through <c>DecodedVideoFrame</c>, and a paletted
    /// frame nobody can convert is a frame nobody can show, so the lookup happens here.
    /// </remarks>
    public PixelFormat OutputFormat => PixelFormat.Rgb24;

    /// <summary>
    /// Whether the stream is paletted, which <see cref="BitsPerCodedSample"/> decides at
    /// <see cref="Initialize"/>. The output is RGB24 either way, so this is the only way to
    /// see which path a stream took.
    /// </summary>
    public bool IsPaletted => context.PaletteVideo;

    /// <summary>Whether the last decoded frame declared itself a key frame.</summary>
    public bool LastFrameWasKey => context.KeyFrame;

    /// <summary>
    /// The palette a paletted stream needs, as 256 packed 0xAARRGGBB entries. Set by the
    /// caller from the container before <see cref="Initialize"/>; a QuickTime track carries
    /// it in the video sample description and nowhere else.
    /// </summary>
    public uint[]? Palette { get; set; }

    /// <inheritdoc/>
    public bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        if (CodedWidth <= 0 || CodedHeight <= 0)
            return false;

        CinepakDec.CinepakDecodeInit(context, CodedWidth, CodedHeight, BitsPerCodedSample);
        initialized = true;
        return true;
    }

    /// <inheritdoc/>
    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks,
                                     bool isKeyframe)
    {
        if (!initialized)
            throw new InvalidOperationException("Cinepak: Decode before Initialize.");

        if (data.IsEmpty)
            return null;

        int ret = CinepakDec.CinepakDecodeFrame(context, data, out bool gotFrame);
        if (ret < 0)
            return null;

        // A frame with no strips changed nothing, and the C still hands the previous
        // picture out - the display has to show something, and it is the same something.
        _ = gotFrame;

        return Extract(presentationTimeTicks);
    }

    /// <summary>One packet is one frame; nothing is held back.</summary>
    public void DrainPendingFrames()
    {
    }

    /// <summary>
    /// Nothing carries across a seek EXCEPT the picture, which is the whole point of an
    /// inter codec - and a seek to a non-key frame is a seek to a picture built on one that
    /// was never decoded. The caller is expected to seek to a key frame.
    /// </summary>
    public void Flush()
    {
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    /// <summary>
    /// The picture, copied out. RGB24 is PACKED, so it is one plane of
    /// <c>width * 3</c> bytes a row and the chroma planes are empty.
    /// </summary>
    private DecodedVideoFrame Extract(long pts)
    {
        int width = context.DisplayWidth;
        int height = context.DisplayHeight;
        int rowBytes = width * 3;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(rowBytes * height);

        if (!context.PaletteVideo)
        {
            for (int y = 0; y < height; y++)
            {
                context.Frame.AsSpan(y * context.LineSize, rowBytes)
                       .CopyTo(buffer.AsSpan(y * rowBytes));
            }
        }
        else
        {
            uint[] palette = Palette ?? EmptyPalette;
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> source = context.Frame.AsSpan(y * context.LineSize, width);
                Span<byte> destination = buffer.AsSpan(y * rowBytes, rowBytes);
                for (int x = 0; x < width; x++)
                {
                    uint argb = palette[source[x]];
                    destination[x * 3 + 0] = (byte)(argb >> 16);
                    destination[x * 3 + 1] = (byte)(argb >> 8);
                    destination[x * 3 + 2] = (byte)argb;
                }
            }
        }

        return new DecodedVideoFrame(
            width, height, PixelFormat.Rgb24, pts,
            buffer,
            0, rowBytes,
            0, 0,
            0, 0);
    }

    /// <summary>
    /// What a paletted stream gets when nobody supplied a palette: all black. The C's
    /// behaviour is the same - sc->palette is zeroed and ff_copy_palette copies the zeros -
    /// and a black picture of the right size is at least honest about having no colours.
    /// </summary>
    private static readonly uint[] EmptyPalette = new uint[256];
}

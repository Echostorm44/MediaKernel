// Raw video as the rest of the project sees it.
//
// This replaces a 773-line RawVideoDecoder.cs written from VLC's rawvideo.c, which was
// registered nowhere and gated by nothing - the same position the old ADPCM and ALAC decoders
// were in before they were replaced by translations of ffmpeg's. See RawDec.cs.

using System;
using System.Buffers;

namespace MediaKernel.Core.Codecs.RawVideo;

/// <summary>Uncompressed video, registered for the codec registry.</summary>
/// <remarks>
/// <para>
/// REQUIRED BEFORE <see cref="Initialize"/>: <see cref="CodedWidth"/>,
/// <see cref="CodedHeight"/>, and <see cref="CodecTag"/> or
/// <see cref="BitsPerCodedSample"/>. An uncompressed track carries no header, no start code
/// and no sync word, so every one of those comes from the container - which is also why a
/// wrong one decodes a plausible wrong picture rather than failing.
/// </para>
/// <para>
/// <see cref="OutputFormat"/> is not known until <see cref="Initialize"/>: the fourcc
/// decides it, and there are 22 possibilities.
/// </para>
/// </remarks>
[Codec("rawvideo", CodecType.Video, DisplayName = "Raw Video", Priority = 5)]
public sealed class RawVideoDecoder : IVideoDecoder
{
    private readonly RawVideoDec decoder = new();
    private bool initialized;

    /// <inheritdoc/>
    public string CodecId => "rawvideo";

    /// <inheritdoc/>
    public bool IsHardwareAccelerated => false;

    /// <summary>The width the container declares.</summary>
    public int CodedWidth { get; set; }

    /// <summary>The height the container declares.</summary>
    public int CodedHeight { get; set; }

    /// <summary>
    /// The container's four-character code - <c>biCompression</c> in an AVI, the sample
    /// entry's type in a MOV. Zero when the container has none.
    /// </summary>
    public uint CodecTag { get; set; }

    /// <summary>
    /// <c>bits_per_coded_sample</c> - <c>biBitCount</c> in an AVI, the sound or image depth
    /// in a MOV. This is what names the format when the fourcc does not.
    /// </summary>
    public int BitsPerCodedSample { get; set; }

    /// <summary>
    /// Whether the track came from a QuickTime-family container, which selects
    /// <c>pix_fmt_bps_mov</c> over <c>pix_fmt_bps_avi</c> for a depth-only lookup.
    /// </summary>
    public bool IsQuickTime { get; set; }

    /// <inheritdoc/>
    public int Width => decoder.Width;

    /// <inheritdoc/>
    public int Height => decoder.Height;

    /// <inheritdoc/>
    /// <remarks>Decided by the fourcc at <see cref="Initialize"/>, not before.</remarks>
    public PixelFormat OutputFormat => decoder.Format;

    /// <summary>
    /// The ffmpeg name of the pixel format this track turned out to be - the only way to see
    /// which of the 267 table rows matched.
    /// </summary>
    public string AvPixFmt => decoder.AvPixFmt;

    /// <summary>Whether the rows arrive bottom-up and are being flipped.</summary>
    public bool Flipped => decoder.Flipped;

    /// <inheritdoc/>
    /// <remarks>
    /// The codec private data is read only for the nine-byte "BottomUp" trailer, which
    /// <c>avidec.c</c> appends itself when an AVI's <c>biCompression</c> is zero.
    /// </remarks>
    public bool Initialize(ReadOnlySpan<byte> codecPrivate)
    {
        if (CodedWidth <= 0 || CodedHeight <= 0)
            return false;
        if (CodecTag == 0 && BitsPerCodedSample == 0)
            return false;

        decoder.RawInitDecoder(
            CodedWidth, CodedHeight, CodecTag, BitsPerCodedSample, codecPrivate, IsQuickTime);
        initialized = true;
        return true;
    }

    /// <inheritdoc/>
    public DecodedVideoFrame? Decode(ReadOnlySpan<byte> data, long presentationTimeTicks,
                                     bool isKeyframe)
    {
        if (!initialized)
            throw new InvalidOperationException("rawvideo: Decode before Initialize.");

        if (data.IsEmpty)
            return null;

        RawVideoDec.RawFrameLayout layout = decoder.RawDecode(data, out byte[] picture);

        // The decoder's buffer is reused on the next call, so the frame is a copy - the same
        // shape as the other decoders here, and the contract DecodedVideoFrame documents.
        return Extract(in layout, picture, presentationTimeTicks);
    }

    /// <summary>One packet is one frame; nothing is held back.</summary>
    public void DrainPendingFrames()
    {
    }

    /// <summary>
    /// Nothing carries across a seek: every packet is a whole picture and there is no state
    /// at all. Uncompressed video is the one codec for which that is true.
    /// </summary>
    public void Flush()
    {
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    /// <summary>
    /// Copies the picture out of the decoder's buffer, dropping the row padding: a
    /// consumer's stride is its own, and an AVI's padded stride is not something the rest of
    /// the pipeline should have to know about.
    /// </summary>
    private DecodedVideoFrame Extract(
        in RawVideoDec.RawFrameLayout layout, byte[] picture, long pts)
    {
        int width = decoder.Width;
        int height = decoder.Height;

        PlaneGeometry g = GeometryOf(decoder.Format, width, height);

        int total = g.YRowBytes * height
                  + (g.Planes >= 2 ? g.URowBytes * g.ChromaHeight : 0)
                  + (g.Planes == 3 ? g.URowBytes * g.ChromaHeight : 0);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(total);

        CopyPlane(picture, layout.YOffset, layout.YStride, buffer, 0, g.YRowBytes, height);

        int uOffset = g.YRowBytes * height;
        int vOffset = uOffset + (g.Planes == 3 ? g.URowBytes * g.ChromaHeight : 0);

        if (g.Planes >= 2)
        {
            CopyPlane(picture, layout.UOffset, layout.UStride,
                      buffer, uOffset, g.URowBytes, g.ChromaHeight);
        }
        if (g.Planes == 3)
        {
            CopyPlane(picture, layout.VOffset, layout.VStride,
                      buffer, vOffset, g.URowBytes, g.ChromaHeight);
        }

        return new DecodedVideoFrame(
            width, height, decoder.Format, pts,
            buffer,
            0, g.YRowBytes,
            g.Planes >= 2 ? uOffset : 0, g.Planes >= 2 ? g.URowBytes : 0,
            g.Planes == 3 ? vOffset : 0, g.Planes == 3 ? g.URowBytes : 0);
    }

    private static void CopyPlane(
        byte[] source, int sourceOffset, int sourceStride,
        byte[] destination, int destinationOffset, int rowBytes, int rows)
    {
        for (int y = 0; y < rows; y++)
        {
            source.AsSpan(sourceOffset + y * sourceStride, rowBytes)
                  .CopyTo(destination.AsSpan(destinationOffset + y * rowBytes, rowBytes));
        }
    }

    private readonly record struct PlaneGeometry(
        int Planes, int YRowBytes, int URowBytes, int ChromaHeight);

    /// <summary>
    /// The unpadded plane geometry of one of the formats <c>RawDec</c> supports. A format
    /// not listed here cannot be reached: <c>RawInitDecoder</c> has already refused it.
    /// </summary>
    private static PlaneGeometry GeometryOf(PixelFormat format, int width, int height) =>
        format switch
        {
            PixelFormat.Yuv420P => new(3, width, (width + 1) / 2, (height + 1) / 2),
            PixelFormat.Yuv422P => new(3, width, (width + 1) / 2, height),
            PixelFormat.Yuv444P => new(3, width, width, height),
            PixelFormat.Yuv420P10 or PixelFormat.Yuv420P12 =>
                new(3, width * 2, (width + 1) / 2 * 2, (height + 1) / 2),
            PixelFormat.Yuv422P10 or PixelFormat.Yuv422P12 =>
                new(3, width * 2, (width + 1) / 2 * 2, height),
            PixelFormat.Yuv444P10 or PixelFormat.Yuv444P12 =>
                new(3, width * 2, width * 2, height),
            PixelFormat.Gbrp => new(3, width, width, height),
            PixelFormat.Gbrp10 => new(3, width * 2, width * 2, height),
            PixelFormat.Nv12 or PixelFormat.Nv21 =>
                new(2, width, (width + 1) / 2 * 2, (height + 1) / 2),
            PixelFormat.Gray8 => new(1, width, 0, 0),
            PixelFormat.Gray16 => new(1, width * 2, 0, 0),
            // 4 bytes per CHROMA sample, so a 99-pixel row is 200 bytes and not 198.
            // image_get_linesize's shift, which ffmpeg's own rawvideo output also has.
            PixelFormat.Yuyv422 or PixelFormat.Uyvy422 =>
                new(1, (width + 1) / 2 * 4, 0, 0),
            PixelFormat.Rgb24 or PixelFormat.Bgr24 => new(1, width * 3, 0, 0),
            PixelFormat.Rgba or PixelFormat.Bgra => new(1, width * 4, 0, 0),
            _ => throw new NotSupportedException(
                     $"rawvideo: no plane geometry for {format}; RawInitDecoder should have "
                   + "refused this stream."),
        };
}

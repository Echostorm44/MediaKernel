// Translated from ffmpeg-master/libavcodec/rawdec.c and raw.c, with the fourcc table
// generated from raw_pix_fmt_tags.h by scripts/gen_raw_pix_fmt_tags.py.
//
// WHY THIS EXISTS. Riff.cs already names "rawvideo" for sixty-odd fourccs - an uncompressed
// AVI is what capture cards, screen recorders and most frame-serving tools write - and
// nothing was registered under that name, so such a file opened, listed its track correctly
// and played nothing. Same shape as H.263 and MPEG-4 Part 2 before it. The 773-line
// RawVideoDecoder.cs this replaces was written from VLC's rawvideo.c, registered nowhere and
// gated by nothing; it is gone, for the same reason the reverse-engineered ADPCM and ALAC
// decoders went.
//
// AN UNCOMPRESSED FRAME IS NOT JUST A BUFFER, which is the thing worth reading rawdec.c for.
// Three of its arms are what a real file needs and none of them is guessable:
//
//   ROW PADDING. An AVI pads each row of a packed format to a 4-byte boundary, so a 99-pixel
//   BGR24 row is 300 bytes and not 297. rawdec.c aligns linesize[0] up to 4 (or 16 for the
//   paletted arms) whenever the packet is big enough to hold the padded rows, and reading
//   the unpadded stride shears the picture one byte further every row.
//   BOTTOM-UP. 'WRAW', 'cyuv', tag 3 and an extradata trailer of "BottomUp" mean row zero is
//   at the END of the buffer.
//   PLANE ORDER. 'YV12', 'YV16', 'YV24' and 'YVU9' are I420/422/444/410 with U and V
//   swapped, and nothing in the bitstream says so - the fourcc is the only tell. Decoding
//   one as I420 gives a perfectly sharp picture with the colours wrong.
//
// SCOPE, STATED RATHER THAN IMPLIED. The fourcc table has all 267 rows and 173 distinct
// pixel formats, and this project's PixelFormat covers 22 of them. A tag that names one of
// the other 151 is REFUSED BY ITS FFMPEG NAME rather than decoded as something close: that is
// the "unknown input fails loudly" rule, and for this codec in particular a near miss
// produces a confident, plausible, wrong picture. The arms that need a pixel format this
// project does not have - PAL8 and the 1, 2 and 4 bits-per-pixel paletted packings, the
// monochrome pair, and the sub-16-bit scaling path - throw and are recorded as todo in
// the port checklist with that reason.

using System;
using System.Buffers;

namespace MediaKernel.Core.Codecs.RawVideo;

/// <summary>Which of <c>raw.c</c>'s three tables a lookup uses.</summary>
public enum PixelFormatTagList
{
    /// <summary><c>PIX_FMT_LIST_RAW</c> - the fourcc table.</summary>
    Raw,

    /// <summary><c>PIX_FMT_LIST_AVI</c> - by bits per coded sample, AVI's assignments.</summary>
    Avi,

    /// <summary><c>PIX_FMT_LIST_MOV</c> - by bits per coded sample, QuickTime's.</summary>
    Mov,
}

/// <summary>
/// The part of <c>AVPixFmtDescriptor</c> and <c>av_image_fill_arrays</c> this decoder needs:
/// enough to lay a packet out as planes.
/// </summary>
/// <param name="Format">This project's equivalent format.</param>
/// <param name="Planes">
/// How many planes the layout has: 1 for a packed format or a single gray plane, 2 for the
/// semi-planar pair, 3 for planar YUV and planar RGB.
/// </param>
/// <param name="YStep">
/// <c>max_step[0]</c> - the largest byte step any component of plane 0 has. 3 for RGB24, 1
/// for everything planar, and 4 for the packed 4:2:2 pair, whose chroma components step four
/// bytes at a time even though its pixels are two.
/// </param>
/// <param name="YStepIsChromaComponent">
/// Whether <c>max_step_comp[0]</c> is a CHROMA component, which decides whether plane 0's
/// width is shifted down by <paramref name="Log2ChromaW"/> before the step is applied. True
/// only for the packed 4:2:2 pair, and it is the whole reason a 99-pixel YUYV row is 200
/// bytes and not 198: <c>image_get_linesize</c> computes 4 * ceil(99 / 2).
/// </param>
/// <param name="Log2ChromaW">How far the chroma planes' width is shifted down.</param>
/// <param name="Log2ChromaH">How far the chroma planes' height is shifted down.</param>
/// <param name="ChromaStep">
/// <c>max_step[1]</c>: 1 for 8-bit planar, 2 for 10- and 12-bit, and 2 for the semi-planar
/// pair because its single chroma plane interleaves both components.
/// </param>
/// <param name="BitsPerPixel">
/// <c>av_get_bits_per_pixel</c>, which the <c>is_lt_16bpp</c> test needs.
/// </param>
public readonly record struct RawPixFmtDesc(
    PixelFormat Format, int Planes, int YStep, bool YStepIsChromaComponent,
    int Log2ChromaW, int Log2ChromaH, int ChromaStep, int BitsPerPixel);

/// <summary>Raw (uncompressed) video, as <c>rawdec.c</c> decodes it.</summary>
/// <remarks>
/// <para>
/// REQUIRED BEFORE <see cref="RawInitDecoder"/>: the width, the height and the container's
/// fourcc or bits per coded sample. An uncompressed track carries NOTHING in band - there is
/// no header, no start code and no sync word - so every one of those comes from the
/// container and a wrong one decodes a plausible wrong picture without ever failing.
/// </para>
/// </remarks>
public sealed class RawVideoDec
{
    /// <summary>
    /// The formats this project can carry, by their ffmpeg name. Everything else in
    /// <see cref="RawPixFmtTags.Table"/> is refused by name.
    /// </summary>
    /// <remarks>
    /// The yuvj* names are in here because, as raw_pix_fmt_tags.h says of them itself, they
    /// "are deprecated hacks specific to libav*, they are identical to yuvXXX" - the same
    /// bytes with a full-range colour tag.
    /// </remarks>
    private static readonly System.Collections.Generic.Dictionary<string, RawPixFmtDesc>
        Supported = new(StringComparer.Ordinal)
    {
        ["YUV420P"] = new(PixelFormat.Yuv420P, 3, 1, false, 1, 1, 1, 12),
        ["YUVJ420P"] = new(PixelFormat.Yuv420P, 3, 1, false, 1, 1, 1, 12),
        ["YUV422P"] = new(PixelFormat.Yuv422P, 3, 1, false, 1, 0, 1, 16),
        ["YUVJ422P"] = new(PixelFormat.Yuv422P, 3, 1, false, 1, 0, 1, 16),
        ["YUV444P"] = new(PixelFormat.Yuv444P, 3, 1, false, 0, 0, 1, 24),
        ["YUVJ444P"] = new(PixelFormat.Yuv444P, 3, 1, false, 0, 0, 1, 24),

        ["YUV420P10LE"] = new(PixelFormat.Yuv420P10, 3, 2, false, 1, 1, 2, 24),
        ["YUV422P10LE"] = new(PixelFormat.Yuv422P10, 3, 2, false, 1, 0, 2, 32),
        ["YUV444P10LE"] = new(PixelFormat.Yuv444P10, 3, 2, false, 0, 0, 2, 48),
        ["YUV420P12LE"] = new(PixelFormat.Yuv420P12, 3, 2, false, 1, 1, 2, 24),
        ["YUV422P12LE"] = new(PixelFormat.Yuv422P12, 3, 2, false, 1, 0, 2, 32),
        ["YUV444P12LE"] = new(PixelFormat.Yuv444P12, 3, 2, false, 0, 0, 2, 48),

        ["NV12"] = new(PixelFormat.Nv12, 2, 1, false, 1, 1, 2, 12),
        ["NV21"] = new(PixelFormat.Nv21, 2, 1, false, 1, 1, 2, 12),

        ["GRAY8"] = new(PixelFormat.Gray8, 1, 1, false, 0, 0, 0, 8),
        ["GRAY16LE"] = new(PixelFormat.Gray16, 1, 2, false, 0, 0, 0, 16),

        // The packed 4:2:2 pair: max_step[0] is 4, from the chroma components, and
        // max_step_comp[0] is one of them - so the width is halved before the step is
        // applied and a 99-pixel row is 200 bytes.
        ["YUYV422"] = new(PixelFormat.Yuyv422, 1, 4, true, 1, 0, 0, 16),
        ["UYVY422"] = new(PixelFormat.Uyvy422, 1, 4, true, 1, 0, 0, 16),

        ["RGB24"] = new(PixelFormat.Rgb24, 1, 3, false, 0, 0, 0, 24),
        ["BGR24"] = new(PixelFormat.Bgr24, 1, 3, false, 0, 0, 0, 24),
        ["RGBA"] = new(PixelFormat.Rgba, 1, 4, false, 0, 0, 0, 32),
        ["BGRA"] = new(PixelFormat.Bgra, 1, 4, false, 0, 0, 0, 32),

        ["GBRP"] = new(PixelFormat.Gbrp, 3, 1, false, 0, 0, 1, 24),
        ["GBRP10LE"] = new(PixelFormat.Gbrp10, 3, 2, false, 0, 0, 2, 30),
    };

    /// <summary><c>pix_fmt_bps_avi</c>.</summary>
    private static readonly PixelFormatTag[] PixFmtBpsAvi =
    [
        new("PAL8", 1),
        new("PAL8", 2),
        new("PAL8", 4),
        new("PAL8", 8),
        new("RGB444LE", 12),
        new("RGB555LE", 15),
        new("RGB555LE", 16),
        new("BGR24", 24),
        new("BGRA", 32),
    ];

    /// <summary><c>pix_fmt_bps_mov</c>.</summary>
    private static readonly PixelFormatTag[] PixFmtBpsMov =
    [
        new("PAL8", 1),
        new("PAL8", 2),
        new("PAL8", 4),
        new("PAL8", 8),
        new("RGB555BE", 16),
        new("RGB24", 24),
        new("ARGB", 32),
        new("PAL8", 33),
    ];

    // -- Context (RawVideoContext) ---------------------------------------

    private int width;
    private int height;
    private uint codecTag;
    private int bitsPerCodedSample;
    private string avPixFmt = "";
    private RawPixFmtDesc desc;

    /// <summary>size of the frame in bytes</summary>
    private int frameSize;
    private bool flipped;
    private bool isMono;
    private bool isPal8;
    private bool isNutMono;
    private bool isNutPal8;
    private bool isYuv2;
    private bool isLt16Bpp;

    private byte[] frameBuffer = [];

    /// <summary>The ffmpeg name of the pixel format this stream was found to be.</summary>
    public string AvPixFmt => avPixFmt;

    /// <summary>This project's equivalent of <see cref="AvPixFmt"/>.</summary>
    public PixelFormat Format => desc.Format;

    /// <summary>Whether the rows arrive bottom-up. <c>context-&gt;flip</c>.</summary>
    public bool Flipped => flipped;

    /// <summary><c>av_image_get_buffer_size</c> for this stream's geometry.</summary>
    public int FrameSize => frameSize;

    /// <summary>The picture width.</summary>
    public int Width => width;

    /// <summary>The picture height.</summary>
    public int Height => height;

    /// <summary><c>MKTAG</c>.</summary>
    public static uint MkTag(char a, char b, char c, char d) =>
        (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

    /// <summary><c>MKTAG</c>, for the tags whose bytes are not printable.</summary>
    public static uint MkTag(int a, int b, int c, int d) =>
        (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

    /// <summary>A fourcc as four characters, for an error message that can be matched.</summary>
    private static string TagText(uint tag)
    {
        Span<char> s = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            int b = (int)((tag >> (8 * i)) & 0xFF);
            s[i] = b is >= 0x20 and < 0x7F ? (char)b : '.';
        }
        return new string(s);
    }

    /// <summary><c>find_pix_fmt</c> over one of the three tables. <c>avpriv_pix_fmt_find</c>.</summary>
    /// <returns>The ffmpeg format name, or null when the table has no row for the key.</returns>
    public static string? AvprivPixFmtFind(PixelFormatTagList list, uint fourcc)
    {
        PixelFormatTag[] tags = list switch
        {
            PixelFormatTagList.Raw => RawPixFmtTags.Table,
            PixelFormatTagList.Avi => PixFmtBpsAvi,
            PixelFormatTagList.Mov => PixFmtBpsMov,
            _ => throw new ArgumentOutOfRangeException(nameof(list), list,
                     "raw.c has three tables and no default."),
        };

        foreach (PixelFormatTag tag in tags)
        {
            if (tag.FourCc == fourcc)
                return tag.AvPixFmt;
        }
        return null;
    }

    /// <summary>
    /// <c>raw_init_decoder</c>. Everything here comes from the container, because an
    /// uncompressed track carries nothing in band.
    /// </summary>
    /// <param name="codedWidth">The container's width.</param>
    /// <param name="codedHeight">The container's height.</param>
    /// <param name="tag">The fourcc, or 0 when the container has none.</param>
    /// <param name="bits">
    /// <c>bits_per_coded_sample</c>, which is what names the format when the fourcc does not.
    /// </param>
    /// <param name="extradata">
    /// The codec private data, read only for the nine-byte "BottomUp" trailer AVI writes.
    /// </param>
    /// <param name="isQuickTime">
    /// True for a QuickTime or MP4 track, which selects <c>pix_fmt_bps_mov</c> for the
    /// bits-per-coded-sample lookup. The C reads the 'raw ' and 'NO16' tags for this; both
    /// are in the table, so this flag only covers a MOV track whose tag did not survive.
    /// </param>
    /// <exception cref="NotSupportedException">
    /// The tag names no format, or names one this project has no PixelFormat for. Both are
    /// refusals rather than fallbacks: there is no format to guess from the bitstream.
    /// </exception>
    public void RawInitDecoder(
        int codedWidth, int codedHeight, uint tag, int bits,
        ReadOnlySpan<byte> extradata, bool isQuickTime = false)
    {
        width = codedWidth;
        height = codedHeight;
        codecTag = tag;
        bitsPerCodedSample = bits;

        string? found;
        if (tag == MkTag('r', 'a', 'w', ' ') || tag == MkTag('N', 'O', '1', '6'))
            found = AvprivPixFmtFind(PixelFormatTagList.Mov, (uint)bits);
        else if (tag == MkTag('W', 'R', 'A', 'W'))
            found = AvprivPixFmtFind(PixelFormatTagList.Avi, (uint)bits);
        else if (tag != 0 && (tag & 0xFFFFFF) != MkTag('B', 'I', 'T', 0))
            found = AvprivPixFmtFind(PixelFormatTagList.Raw, tag);
        else if (bits != 0)
            found = AvprivPixFmtFind(
                isQuickTime ? PixelFormatTagList.Mov : PixelFormatTagList.Avi, (uint)bits);
        else
            found = null;

        if (found is null)
        {
            throw new NotSupportedException(
                $"rawvideo: fourcc '{TagText(tag)}' (0x{tag:X8}) with "
              + $"{bits} bits per coded sample names no pixel format in "
              + "raw_pix_fmt_tags.h. An uncompressed track carries no format in band, so "
              + "there is nothing to fall back on.");
        }

        avPixFmt = found;

        if (!Supported.TryGetValue(found, out desc))
        {
            throw new NotSupportedException(
                $"rawvideo: fourcc '{TagText(tag)}' is AV_PIX_FMT_{found}, which this "
              + "project has no PixelFormat for. Decoding it as something close gives a "
              + $"sharp picture with the wrong colours, so it is refused. {Supported.Count} "
              + "of raw_pix_fmt_tags.h's 173 formats are supported.");
        }

        // "BottomUp" is nine bytes INCLUDING its NUL, which is why the comparison is nine
        // and not eight. It is read from the END of the extradata because avidec.c APPENDS
        // it to whatever was already there - a palette, usually. No file in this tree has
        // both, so reading it from the start is indistinguishable here: the uncompressed
        // AVIs the gate makes have no other extradata at all, so their extradata IS the
        // nine bytes and the two reads are the same bytes. The arm that matters is whether
        // the trailer is appended at all, and that one is red.
        flipped = (extradata.Length >= 9
                   && extradata[^9] == (byte)'B' && extradata[^8] == (byte)'o'
                   && extradata[^7] == (byte)'t' && extradata[^6] == (byte)'t'
                   && extradata[^5] == (byte)'o' && extradata[^4] == (byte)'m'
                   && extradata[^3] == (byte)'U' && extradata[^2] == (byte)'p'
                   && extradata[^1] == 0)
                  || tag == MkTag('c', 'y', 'u', 'v')
                  || tag == MkTag(3, 0, 0, 0)
                  || tag == MkTag('W', 'R', 'A', 'W');

        isMono = found is "MONOWHITE" or "MONOBLACK";
        isPal8 = found == "PAL8";

        isNutMono = tag == MkTag('B', '1', 'W', '0') || tag == MkTag('B', '0', 'W', '1');
        isNutPal8 = tag == MkTag('P', 'A', 'L', 8);

        isYuv2 = tag == MkTag('y', 'u', 'v', '2') && found == "YUYV422";
    }

    /// <summary>
    /// <c>av_image_get_buffer_size</c> with align 1, for the formats
    /// <see cref="Supported"/> holds.
    /// </summary>
    private int AvImageGetBufferSize()
    {
        int luma = YLinesize() * height;
        if (desc.Planes == 1)
            return luma;

        int chromaHeight = AvCeilRShift(height, desc.Log2ChromaH);
        int chroma = ChromaLinesize() * chromaHeight;

        // The semi-planar pair has ONE chroma plane holding both components, which is why
        // ChromaBytesPerPixel is 2 for it and this is not multiplied by the plane count.
        return desc.Planes == 2 ? luma + chroma : luma + 2 * chroma;
    }

    /// <summary><c>FFALIGN</c>.</summary>
    private static int FfAlign(int x, int a) => (x + a - 1) & ~(a - 1);

    /// <summary><c>AV_CEIL_RSHIFT</c>.</summary>
    private static int AvCeilRShift(int a, int b) => (a + (1 << b) - 1) >> b;

    /// <summary>
    /// <c>image_get_linesize</c> for plane 0: the step times the width, with the width
    /// shifted down first when the step came from a chroma component.
    /// </summary>
    private int YLinesize() =>
        desc.YStep * AvCeilRShift(width, desc.YStepIsChromaComponent ? desc.Log2ChromaW : 0);

    /// <summary>
    /// <c>image_get_linesize</c> for planes 1 and 2, whose max_step_comp is always a chroma
    /// component and so always shifts.
    /// </summary>
    private int ChromaLinesize() => desc.ChromaStep * AvCeilRShift(width, desc.Log2ChromaW);

    /// <summary>
    /// The layout of one decoded picture: where each plane starts in the buffer and how many
    /// bytes a row of it is. <c>frame-&gt;data</c> and <c>frame-&gt;linesize</c>.
    /// </summary>
    public readonly record struct RawFrameLayout(
        int YOffset, int YStride, int UOffset, int UStride, int VOffset, int VStride);

    /// <summary>
    /// <c>raw_decode</c>. The returned layout points into <paramref name="frame"/>, which is
    /// the decoder's own buffer and is valid until the next call.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The stream needs one of the arms that wants a pixel format this project does not
    /// have; see the scope note at the top of the file.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The packet is too small to hold a picture of the container's geometry. The C logs
    /// "Packet too small" and returns AVERROR_INVALIDDATA.
    /// </exception>
    public RawFrameLayout RawDecode(ReadOnlySpan<byte> packet, out byte[] frame)
    {
        if (width <= 0)
            throw new InvalidOperationException("rawvideo: width is not set.");
        if (height <= 0)
            throw new InvalidOperationException("rawvideo: height is not set.");

        int linesizeAlign = 4;

        int stride = isNutMono ? width / 8 + ((width & 7) != 0 ? 1 : 0)
                   : isNutPal8 ? width
                   : packet.Length / height;

        if (stride == 0 || packet.Length < stride * height)
        {
            throw new ArgumentException(
                $"rawvideo: packet too small ({packet.Length} bytes for {width}x{height} "
              + $"{avPixFmt}, stride {stride}).", nameof(packet));
        }

        if ((bitsPerCodedSample is 8 or 4 or 2 or 1
             || (bitsPerCodedSample == 0 && (isNutPal8 || isMono)))
            && (isMono || isPal8)
            && (codecTag == 0 || codecTag == MkTag('r', 'a', 'w', ' ')
                || isNutMono || isNutPal8))
        {
            // is_1_2_4_8_bpp. Every path through it produces PAL8 or monochrome, and this
            // project has neither format nor any palette plumbing through
            // DecodedVideoFrame. Recorded as todo rather than approximated.
            throw new NotSupportedException(
                $"rawvideo: AV_PIX_FMT_{avPixFmt} at {bitsPerCodedSample} bits per coded "
              + "sample is rawdec.c's paletted/monochrome packing, which needs a PAL8 pixel "
              + "format this project does not have.");
        }

        isLt16Bpp = desc.BitsPerPixel == 16 && bitsPerCodedSample is > 8 and < 16;
        if (isLt16Bpp)
        {
            throw new NotSupportedException(
                $"rawvideo: AV_PIX_FMT_{avPixFmt} with {bitsPerCodedSample} bits per coded "
              + "sample is rawdec.c's sub-16-bit scaling path (scale16be/scale16le), which "
              + "no sample in this tree reaches.");
        }

        frameSize = AvImageGetBufferSize();

        // need_copy is unconditional here: the C can reference the packet's own buffer when
        // none of its rewriting arms fires, and this port always owns its picture, because
        // DecodedVideoFrame's contract is that the frame survives the next Decode call.
        if (frameBuffer.Length < Math.Max(frameSize, packet.Length))
            frameBuffer = new byte[Math.Max(frameSize, packet.Length)];
        packet.CopyTo(frameBuffer);
        frame = frameBuffer;

        int bufStart = 0;
        int bufSize = packet.Length;

        if (codecTag == MkTag('A', 'V', '1', 'x') || codecTag == MkTag('A', 'V', 'u', 'p'))
            bufStart += bufSize - frameSize;

        if (bufSize < frameSize)
        {
            throw new ArgumentException(
                $"rawvideo: invalid buffer size, packet size {bufSize} < expected frame_size "
              + $"{frameSize}.", nameof(packet));
        }

        RawFrameLayout layout = AvImageFillArrays(bufStart);

        // ROW PADDING. This is the arm a real AVI needs: the packet holds rows padded up to
        // a 4-byte boundary, and the test is whether it is big enough for them.
        //
        // THIS ARM AND THE BGR24 ONE BELOW ARE REDUNDANT on everything ffmpeg's AVI muxer
        // writes, which a perturbation sweep established: BGR24 is the only format in the
        // gate whose rows are actually padded (a 99-pixel row is 297 bytes and the muxer
        // writes 300), GRAY8's are not padded and BGRA's are already aligned - so deleting
        // either arm leaves the other setting the same stride and the gate stays green. Both
        // are kept because both are in the C, and this note is here so nobody has to
        // establish that twice.
        if ((desc.Format is PixelFormat.Rgb24 or PixelFormat.Bgr24 or PixelFormat.Gray8)
            && FfAlign(layout.YStride, linesizeAlign) * height <= bufSize)
        {
            layout = layout with { YStride = FfAlign(layout.YStride, linesizeAlign) };
        }

        if (desc.Format == PixelFormat.Nv12 && codecTag == MkTag('N', 'V', '1', '2')
            && FfAlign(layout.YStride, linesizeAlign) * height
               + FfAlign(layout.UStride, linesizeAlign) * ((height + 1) / 2) <= bufSize)
        {
            int la0 = FfAlign(layout.YStride, linesizeAlign);
            layout = layout with
            {
                UOffset = layout.UOffset + (la0 - layout.YStride) * height,
                YStride = la0,
                UStride = FfAlign(layout.UStride, linesizeAlign),
            };
        }

        // The second of the two redundant arms; see the note above.
        if (desc.Format == PixelFormat.Bgr24
            && ((layout.YStride + 3) & ~3) * height <= bufSize)
        {
            layout = layout with { YStride = (layout.YStride + 3) & ~3 };
        }

        if (flipped)
            layout = Flip(layout);

        // PLANE ORDER. The fourcc is the only thing that says the chroma planes are the
        // other way round; the bytes are identical to I420's.
        if (codecTag == MkTag('Y', 'V', '1', '2') || codecTag == MkTag('Y', 'V', '1', '6')
            || codecTag == MkTag('Y', 'V', '2', '4') || codecTag == MkTag('Y', 'V', 'U', '9'))
        {
            layout = layout with
            {
                UOffset = layout.VOffset,
                UStride = layout.VStride,
                VOffset = layout.UOffset,
                VStride = layout.UStride,
            };
        }

        // An I420 of odd width or height, written by something that rounded the luma plane
        // up and the chroma planes with it.
        if (codecTag == MkTag('I', '4', '2', '0')
            && (width + 1) * (height + 1) * 3 / 2 == bufSize)
        {
            int slack = (width + 1) * (height + 1) - width * height;
            layout = layout with
            {
                UOffset = layout.UOffset + slack,
                VOffset = layout.VOffset + slack * 5 / 4,
            };
        }

        // 'yuv2' is YUYV422 with SIGNED chroma, so every second byte needs its sign bit
        // flipped to become the unsigned chroma everything else uses.
        if (isYuv2)
        {
            for (int y = 0; y < height; y++)
            {
                int line = layout.YOffset + y * layout.YStride;
                for (int x = 0; x < width; x++)
                    frameBuffer[line + 2 * x + 1] ^= 0x80;
            }
        }

        return layout;
    }

    /// <summary>
    /// <c>flip</c>. DIVERGENCE: the C moves data[0] to the last row and negates linesize[0];
    /// this reverses the rows in place, because <see cref="DecodedVideoFrame"/> has no way to
    /// carry a negative stride and a consumer that multiplied one out would read before the
    /// buffer. Plane 0 only, as in the C - the tags that set the flag are packed or gray.
    /// </summary>
    private RawFrameLayout Flip(RawFrameLayout layout)
    {
        int rowBytes = Math.Min(layout.YStride, YLinesize());
        byte[] scratch = ArrayPool<byte>.Shared.Rent(rowBytes);
        try
        {
            for (int y = 0; y < height / 2; y++)
            {
                int top = layout.YOffset + y * layout.YStride;
                int bottom = layout.YOffset + (height - 1 - y) * layout.YStride;
                frameBuffer.AsSpan(top, rowBytes).CopyTo(scratch);
                frameBuffer.AsSpan(bottom, rowBytes).CopyTo(frameBuffer.AsSpan(top, rowBytes));
                scratch.AsSpan(0, rowBytes).CopyTo(frameBuffer.AsSpan(bottom, rowBytes));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
        return layout;
    }

    /// <summary><c>av_image_fill_arrays</c> with align 1.</summary>
    private RawFrameLayout AvImageFillArrays(int start)
    {
        int yStride = YLinesize();
        if (desc.Planes == 1)
            return new RawFrameLayout(start, yStride, 0, 0, 0, 0);

        int chromaHeight = AvCeilRShift(height, desc.Log2ChromaH);
        int chromaStride = ChromaLinesize();
        int uOffset = start + yStride * height;

        if (desc.Planes == 2)
            return new RawFrameLayout(start, yStride, uOffset, chromaStride, 0, 0);

        return new RawFrameLayout(
            start, yStride,
            uOffset, chromaStride,
            uOffset + chromaStride * chromaHeight, chromaStride);
    }
}

// Shared MPEG-video tables - a translation of ffmpeg-master/libavcodec/mpegvideodata.c.
//
// Generated from the C source. The two alternate scan tables are 64 arbitrary values each,
// which is exactly where a silent transposition enters a hand-typed port.
//
// These are all uint8_t, so the span-returning properties use `new byte[] { ... }` directly:
// byte-sized element types are folded into the assembly's data section, so nothing is
// allocated. The mpeg12data.c tables need a static readonly array instead, because ushort,
// float and struct element types are not folded and would allocate on every access.

using System;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>Tables shared by the MPEG-video decoders (<c>mpegvideodata.c</c>).</summary>
public static class MpegVideoData
{
    /// <summary><c>ff_default_chroma_qscale_table[32]</c>. Identity: chroma qscale equals luma qscale for MPEG-1/2. H.263 is the one that differs.</summary>
    public static ReadOnlySpan<byte> DefaultChromaQscaleTable => new byte[]
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
    };

    /// <summary><c>ff_mpeg2_non_linear_qscale[32]</c>. MPEG-2's non-linear quantiser scale, selected by q_scale_type in the picture coding extension.</summary>
    public static ReadOnlySpan<byte> Mpeg2NonLinearQscale => new byte[]
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 18, 20, 22,
        24, 28, 32, 36, 40, 44, 48, 52, 56, 64, 72, 80, 88, 96, 104, 112,
    };

    /// <summary><c>ff_mpeg12_dc_scale_table[4][32]</c>. Indexed by intra_dc_precision: 8, 4, 2, 1. Flattened, so FFmpeg's [p][q] is [p * 32 + q].</summary>
    public static ReadOnlySpan<byte> Mpeg12DcScaleTable => new byte[]
    {
        8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
        8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    };

    /// <summary>
    /// <c>ff_mpeg1_dc_scale_table</c> - row zero of <see cref="Mpeg12DcScaleTable"/>, which the
    /// C declares as a pointer into it. H.263 needs it as an array of its own, because the
    /// context holds a table rather than a table and a row index.
    /// </summary>
    public static readonly byte[] Mpeg1DcScaleTable =
    [
        8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
        8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
    ];

    /// <summary><c>ff_alternate_horizontal_scan[64]</c>. MPEG-2 alternate scan, horizontal variant, selected by alternate_scan.</summary>
    public static ReadOnlySpan<byte> AlternateHorizontalScan => new byte[]
    {
        0, 1, 2, 3, 8, 9, 16, 17, 10, 11, 4, 5, 6, 7, 15, 14,
        13, 12, 19, 18, 24, 25, 32, 33, 26, 27, 20, 21, 22, 23, 28, 29,
        30, 31, 34, 35, 40, 41, 48, 49, 42, 43, 36, 37, 38, 39, 44, 45,
        46, 47, 50, 51, 56, 57, 58, 59, 52, 53, 54, 55, 60, 61, 62, 63,
    };

    /// <summary><c>ff_alternate_vertical_scan[64]</c>. MPEG-2 alternate scan, vertical variant.</summary>
    /// <summary>
    /// <see cref="AlternateVerticalScan"/> as an array, shared by every caller - see
    /// <c>IdctDsp.ZigzagDirectArray</c> for why this is not a ToArray() at the call site.
    /// </summary>
    public static readonly byte[] AlternateVerticalScanArray = AlternateVerticalScan.ToArray();

    public static ReadOnlySpan<byte> AlternateVerticalScan => new byte[]
    {
        0, 8, 16, 24, 1, 9, 2, 10, 17, 25, 32, 40, 48, 56, 57, 49,
        41, 33, 26, 18, 3, 11, 4, 12, 19, 27, 34, 42, 50, 58, 35, 43,
        51, 59, 20, 28, 5, 13, 6, 14, 21, 29, 36, 44, 52, 60, 37, 45,
        53, 61, 22, 30, 7, 15, 23, 31, 38, 46, 54, 62, 39, 47, 55, 63,
    };
}

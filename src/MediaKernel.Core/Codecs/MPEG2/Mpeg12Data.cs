// MPEG-1/2 tables - a translation of ffmpeg-master/libavcodec/mpeg12data.c.
//
// Generated from the C source rather than retyped: there are about nine hundred numbers here
// and a single wrong digit would surface as a FATE mismatch a long way from its cause. Names are
// the C names with the ff_ prefix dropped, so the mapping reverses by eye.
//
// Each table is a static readonly array exposed as a ReadOnlySpan. The span-returning property
// cannot be written as a collection expression over a non-byte type - only byte-sized element
// types are folded into the assembly's data section, so `new ushort[] { ... }` in a property body
// would allocate on every single access. One static array, handed out as a span, allocates once.

using System;
using System.Numerics;

namespace MediaKernel.Core.Codecs.Mpeg2;

/// <summary>
/// A rational number, as libavutil's <c>AVRational</c>. Lives here because the MPEG-1/2 frame-rate
/// and aspect-ratio tables are the first thing in this port to need one; promote it to a shared
/// file when a second codec does.
/// </summary>
public readonly record struct AvRational(int Num, int Den);

/// <summary>
/// The parts of libavutil's <c>rational.c</c> and <c>mathematics.c</c> that MPEG-1/2 needs, which
/// is everything <c>mpeg_decode_postinit</c> uses to turn an aspect code and a frame-rate index
/// into the numbers a player reads.
/// </summary>
/// <remarks>
/// These live beside <see cref="AvRational"/> rather than in a file of their own because a type
/// and its arithmetic belong together, and the struct is already here for the tables below.
/// Promote both when a second codec needs them.
/// </remarks>
public static class AvRationalMath
{
    /// <summary><c>av_gcd</c> - binary GCD, translated as written.</summary>
    public static long Gcd(long a, long b)
    {
        if (a == 0) return b;
        if (b == 0) return a;

        int za = BitOperations.TrailingZeroCount((ulong)a);
        int zb = BitOperations.TrailingZeroCount((ulong)b);
        int k = Math.Min(za, zb);
        long u = Math.Abs(a >> za);
        long v = Math.Abs(b >> zb);

        while (u != v)
        {
            if (u > v) (u, v) = (v, u);
            v -= u;
            v >>= BitOperations.TrailingZeroCount((ulong)v);
        }

        return (long)((ulong)u << k);
    }

    /// <summary>
    /// <c>av_reduce</c> - reduce a fraction to its lowest terms with both parts at most
    /// <paramref name="max"/>, by continued fractions when an exact reduction will not fit.
    /// </summary>
    /// <returns>True when the result is exact.</returns>
    // The C declares x as uint64_t, so its arithmetic below is unsigned. Here it is signed,
    // which is equivalent over the range this is reachable with: num and den are both made
    // non-negative above, a0 and a1 are bounded by max, and max is INT_MAX at every call site,
    // so no product here approaches the point where the two would wrap differently.
    public static bool Reduce(out int dstNum, out int dstDen, long num, long den, long max)
    {
        long a0Num = 0, a0Den = 1, a1Num = 1, a1Den = 0;
        bool sign = (num < 0) ^ (den < 0);
        long gcd = Gcd(Math.Abs(num), Math.Abs(den));

        if (gcd != 0)
        {
            num = Math.Abs(num) / gcd;
            den = Math.Abs(den) / gcd;
        }

        if (num <= max && den <= max)
        {
            a1Num = num;
            a1Den = den;
            den = 0;
        }

        while (den != 0)
        {
            long x = num / den;
            long nextDen = num - den * x;
            long a2n = x * a1Num + a0Num;
            long a2d = x * a1Den + a0Den;

            if (a2n > max || a2d > max)
            {
                if (a1Num != 0) x = (max - a0Num) / a1Num;
                if (a1Den != 0) x = Math.Min(x, (max - a0Den) / a1Den);

                if (den * (2 * x * a1Den + a0Den) > num * a1Den)
                {
                    a1Num = x * a1Num + a0Num;
                    a1Den = x * a1Den + a0Den;
                }
                break;
            }

            a0Num = a1Num; a0Den = a1Den;
            a1Num = a2n; a1Den = a2d;
            num = den;
            den = nextDen;
        }

        dstNum = (int)(sign ? -a1Num : a1Num);
        dstDen = (int)a1Den;
        return den == 0;
    }

    /// <summary><c>av_mul_q</c>.</summary>
    public static AvRational Mul(AvRational b, AvRational c)
    {
        Reduce(out int num, out int den, (long)b.Num * c.Num, (long)b.Den * c.Den, int.MaxValue);
        return new AvRational(num, den);
    }

    /// <summary><c>av_div_q</c> - multiply by the reciprocal, as the C does.</summary>
    public static AvRational Div(AvRational b, AvRational c) => Mul(b, new AvRational(c.Den, c.Num));

    /// <summary>
    /// <c>av_cmp_q</c>. Returns 0 when equal, and <see cref="int.MinValue"/> when the two are
    /// not comparable - which is the C's way of reporting 0/0, and is deliberately not an error.
    /// </summary>
    public static int Cmp(AvRational a, AvRational b)
    {
        long tmp = (long)a.Num * b.Den - (long)b.Num * a.Den;

        if (tmp != 0) return (int)((tmp ^ a.Den ^ b.Den) >> 63) | 1;
        if (b.Den != 0 && a.Den != 0) return 0;
        if (a.Num != 0 && b.Num != 0) return (a.Num >> 31) - (b.Num >> 31);
        return int.MinValue;
    }

    /// <summary><c>av_d2q</c> - the nearest rational with both parts at most <paramref name="max"/>.</summary>
    public static AvRational FromDouble(double d, int max)
    {
        if (double.IsNaN(d)) return new AvRational(0, 0);
        if (Math.Abs(d) > int.MaxValue + 3L) return new AvRational(d < 0 ? -1 : 1, 0);

        // frexp(d, &exponent): C# has no frexp, and ILogB(d) is frexp's exponent minus one,
        // so FFMAX(exponent - 1, 0) is FFMAX(ILogB(d), 0). ILogB(0) is int.MinValue, which
        // clamps to 0 and gives den = 1 << 62, exactly as frexp's exponent of 0 would.
        int exponent = Math.Max(double.ILogB(d), 0);
        long den = 1L << (62 - exponent);

        Reduce(out int num, out int outDen, (long)Math.Floor(d * den + 0.5), den, max);
        return new AvRational(num, outDen);
    }
}

/// <summary>MPEG-1/2 constant tables (<c>mpeg12data.c</c>).</summary>
public static class Mpeg12Data
{
    /// <summary><c>ff_mpeg1_default_intra_matrix[256]</c>.</summary>
    /// <remarks>FFmpeg lists 64 values in an array declared [256]. The remaining 192 are implicitly zero and are kept, because code that indexes past the listed values would otherwise read out of bounds.</remarks>
    public static ReadOnlySpan<ushort> Mpeg1DefaultIntraMatrix => Mpeg1DefaultIntraMatrixData;

    private static readonly ushort[] Mpeg1DefaultIntraMatrixData =
    [
        8, 16, 19, 22, 26, 27, 29, 34, 16, 16, 22, 24, 27, 29, 34, 37,
        19, 22, 26, 27, 29, 34, 34, 38, 22, 22, 26, 27, 29, 34, 37, 40,
        22, 26, 27, 29, 32, 35, 40, 48, 26, 27, 29, 32, 35, 40, 48, 58,
        26, 27, 29, 34, 38, 46, 56, 69, 27, 29, 35, 38, 46, 56, 69, 83,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    /// <summary><c>ff_mpeg1_default_non_intra_matrix[64]</c>.</summary>
    public static ReadOnlySpan<ushort> Mpeg1DefaultNonIntraMatrix => Mpeg1DefaultNonIntraMatrixData;

    private static readonly ushort[] Mpeg1DefaultNonIntraMatrixData =
    [
        16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16,
        16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16,
        16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16,
        16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16,
    ];

    /// <summary><c>ff_mpeg12_vlc_dc_lum_code[12]</c>.</summary>
    public static ReadOnlySpan<ushort> Mpeg12VlcDcLumCode => Mpeg12VlcDcLumCodeData;

    private static readonly ushort[] Mpeg12VlcDcLumCodeData =
    [
        4, 0, 1, 5, 6, 14, 30, 62, 126, 254, 510, 511,
    ];

    /// <summary><c>ff_mpeg12_vlc_dc_lum_bits[12]</c>.</summary>
    public static ReadOnlySpan<byte> Mpeg12VlcDcLumBits => Mpeg12VlcDcLumBitsData;

    private static readonly byte[] Mpeg12VlcDcLumBitsData =
    [
        3, 2, 2, 3, 3, 4, 5, 6, 7, 8, 9, 9,
    ];

    /// <summary><c>ff_mpeg12_vlc_dc_chroma_code[12]</c>.</summary>
    public static ReadOnlySpan<ushort> Mpeg12VlcDcChromaCode => Mpeg12VlcDcChromaCodeData;

    private static readonly ushort[] Mpeg12VlcDcChromaCodeData =
    [
        0, 1, 2, 6, 14, 30, 62, 126, 254, 510, 1022, 1023,
    ];

    /// <summary><c>ff_mpeg12_vlc_dc_chroma_bits[12]</c>.</summary>
    public static ReadOnlySpan<byte> Mpeg12VlcDcChromaBits => Mpeg12VlcDcChromaBitsData;

    private static readonly byte[] Mpeg12VlcDcChromaBitsData =
    [
        2, 2, 2, 3, 4, 5, 6, 7, 8, 9, 10, 10,
    ];

    /// <summary><c>ff_mpeg1_vlc_table[113][2]</c>.</summary>
    /// <remarks>Flattened: FFmpeg's <c>[i][0]</c> and <c>[i][1]</c> are <c>[i * 2]</c> and <c>[i * 2 + 1]</c>.</remarks>
    public static ReadOnlySpan<ushort> Mpeg1VlcTable => Mpeg1VlcTableData;

    private static readonly ushort[] Mpeg1VlcTableData =
    [
        3, 2, 4, 4, 5, 5, 6, 7, 38, 8, 33, 8, 10, 10, 29, 12,
        24, 12, 19, 12, 16, 12, 26, 13, 25, 13, 24, 13, 23, 13, 31, 14,
        30, 14, 29, 14, 28, 14, 27, 14, 26, 14, 25, 14, 24, 14, 23, 14,
        22, 14, 21, 14, 20, 14, 19, 14, 18, 14, 17, 14, 16, 14, 24, 15,
        23, 15, 22, 15, 21, 15, 20, 15, 19, 15, 18, 15, 17, 15, 16, 15,
        3, 3, 6, 6, 37, 8, 12, 10, 27, 12, 22, 13, 21, 13, 31, 15,
        30, 15, 29, 15, 28, 15, 27, 15, 26, 15, 25, 15, 19, 16, 18, 16,
        17, 16, 16, 16, 5, 4, 4, 7, 11, 10, 20, 12, 20, 13, 7, 5,
        36, 8, 28, 12, 19, 13, 6, 5, 15, 10, 18, 12, 7, 6, 9, 10,
        18, 13, 5, 6, 30, 12, 20, 16, 4, 6, 21, 12, 7, 7, 17, 12,
        5, 7, 17, 13, 39, 8, 16, 13, 35, 8, 26, 16, 34, 8, 25, 16,
        32, 8, 24, 16, 14, 10, 23, 16, 13, 10, 22, 16, 8, 10, 21, 16,
        31, 12, 26, 12, 25, 12, 23, 12, 22, 12, 31, 13, 30, 13, 29, 13,
        28, 13, 27, 13, 31, 16, 30, 16, 29, 16, 28, 16, 27, 16, 1, 6,
        2, 2,
    ];

    /// <summary><c>ff_mpeg2_vlc_table[113][2]</c>.</summary>
    /// <remarks>Flattened: FFmpeg's <c>[i][0]</c> and <c>[i][1]</c> are <c>[i * 2]</c> and <c>[i * 2 + 1]</c>.</remarks>
    public static ReadOnlySpan<ushort> Mpeg2VlcTable => Mpeg2VlcTableData;

    private static readonly ushort[] Mpeg2VlcTableData =
    [
        2, 2, 6, 3, 7, 4, 28, 5, 29, 5, 5, 6, 4, 6, 123, 7,
        124, 7, 35, 8, 34, 8, 250, 8, 251, 8, 254, 8, 255, 8, 31, 14,
        30, 14, 29, 14, 28, 14, 27, 14, 26, 14, 25, 14, 24, 14, 23, 14,
        22, 14, 21, 14, 20, 14, 19, 14, 18, 14, 17, 14, 16, 14, 24, 15,
        23, 15, 22, 15, 21, 15, 20, 15, 19, 15, 18, 15, 17, 15, 16, 15,
        2, 3, 6, 5, 121, 7, 39, 8, 32, 8, 22, 13, 21, 13, 31, 15,
        30, 15, 29, 15, 28, 15, 27, 15, 26, 15, 25, 15, 19, 16, 18, 16,
        17, 16, 16, 16, 5, 5, 7, 7, 252, 8, 12, 10, 20, 13, 7, 5,
        38, 8, 28, 12, 19, 13, 6, 6, 253, 8, 18, 12, 7, 6, 4, 9,
        18, 13, 6, 7, 30, 12, 20, 16, 4, 7, 21, 12, 5, 7, 17, 12,
        120, 7, 17, 13, 122, 7, 16, 13, 33, 8, 26, 16, 37, 8, 25, 16,
        36, 8, 24, 16, 5, 9, 23, 16, 7, 9, 22, 16, 13, 10, 21, 16,
        31, 12, 26, 12, 25, 12, 23, 12, 22, 12, 31, 13, 30, 13, 29, 13,
        28, 13, 27, 13, 31, 16, 30, 16, 29, 16, 28, 16, 27, 16, 1, 6,
        6, 4,
    ];

    /// <summary><c>ff_mpeg12_level[111]</c>.</summary>
    public static ReadOnlySpan<sbyte> Mpeg12Level => Mpeg12LevelData;

    private static readonly sbyte[] Mpeg12LevelData =
    [
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        33, 34, 35, 36, 37, 38, 39, 40, 1, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 1, 2, 3, 4, 5, 1,
        2, 3, 4, 1, 2, 3, 1, 2, 3, 1, 2, 3, 1, 2, 1, 2,
        1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    ];

    /// <summary><c>ff_mpeg12_run[111]</c>.</summary>
    public static ReadOnlySpan<sbyte> Mpeg12Run => Mpeg12RunData;

    private static readonly sbyte[] Mpeg12RunData =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 3,
        3, 3, 3, 4, 4, 4, 5, 5, 5, 6, 6, 6, 7, 7, 8, 8,
        9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
    ];

    /// <summary><c>ff_mpeg12_mbAddrIncrTable[36][2]</c>.</summary>
    /// <remarks>Flattened: FFmpeg's <c>[i][0]</c> and <c>[i][1]</c> are <c>[i * 2]</c> and <c>[i * 2 + 1]</c>.</remarks>
    public static ReadOnlySpan<byte> Mpeg12MbAddrIncrTable => Mpeg12MbAddrIncrTableData;

    private static readonly byte[] Mpeg12MbAddrIncrTableData =
    [
        1, 1, 3, 3, 2, 3, 3, 4, 2, 4, 3, 5, 2, 5, 7, 7,
        6, 7, 11, 8, 10, 8, 9, 8, 8, 8, 7, 8, 6, 8, 23, 10,
        22, 10, 21, 10, 20, 10, 19, 10, 18, 10, 35, 11, 34, 11, 33, 11,
        32, 11, 31, 11, 30, 11, 29, 11, 28, 11, 27, 11, 26, 11, 25, 11,
        24, 11, 8, 11, 15, 11, 0, 8,
    ];

    /// <summary><c>ff_mpeg12_mbPatTable[64][2]</c>.</summary>
    /// <remarks>Flattened: FFmpeg's <c>[i][0]</c> and <c>[i][1]</c> are <c>[i * 2]</c> and <c>[i * 2 + 1]</c>.</remarks>
    public static ReadOnlySpan<byte> Mpeg12MbPatTable => Mpeg12MbPatTableData;

    private static readonly byte[] Mpeg12MbPatTableData =
    [
        1, 9, 11, 5, 9, 5, 13, 6, 13, 4, 23, 7, 19, 7, 31, 8,
        12, 4, 22, 7, 18, 7, 30, 8, 19, 5, 27, 8, 23, 8, 19, 8,
        11, 4, 21, 7, 17, 7, 29, 8, 17, 5, 25, 8, 21, 8, 17, 8,
        15, 6, 15, 8, 13, 8, 3, 9, 15, 5, 11, 8, 7, 8, 7, 9,
        10, 4, 20, 7, 16, 7, 28, 8, 14, 6, 14, 8, 12, 8, 2, 9,
        16, 5, 24, 8, 20, 8, 16, 8, 14, 5, 10, 8, 6, 8, 6, 9,
        18, 5, 26, 8, 22, 8, 18, 8, 13, 5, 9, 8, 5, 8, 5, 9,
        12, 5, 8, 8, 4, 8, 4, 9, 7, 3, 10, 5, 8, 5, 12, 6,
    ];

    /// <summary><c>ff_mpeg12_mbMotionVectorTable[17][2]</c>.</summary>
    /// <remarks>Flattened: FFmpeg's <c>[i][0]</c> and <c>[i][1]</c> are <c>[i * 2]</c> and <c>[i * 2 + 1]</c>.</remarks>
    public static ReadOnlySpan<byte> Mpeg12MbMotionVectorTable => Mpeg12MbMotionVectorTableData;

    private static readonly byte[] Mpeg12MbMotionVectorTableData =
    [
        1, 1, 1, 2, 1, 3, 1, 4, 3, 6, 5, 7, 4, 7, 3, 7,
        11, 9, 10, 9, 9, 9, 17, 10, 16, 10, 15, 10, 14, 10, 13, 10,
        12, 10,
    ];

    /// <summary><c>ff_mpeg12_frame_rate_tab[16]</c> - the frame rate index in a sequence header.</summary>
    /// <remarks>
    /// Indices 0, 14 and 15 are {0,0}: the standard defines none of them, and FFmpeg leaves
    /// them as a zero rational rather than a guess. Indices 9 to 13 are not in the standard either -
    /// they are Xing's 15fps and libmpeg3's "unofficial economy rates", kept because real
    /// files use them.
    /// </remarks>
    public static ReadOnlySpan<AvRational> Mpeg12FrameRateTab => Mpeg12FrameRateTabData;

    private static readonly AvRational[] Mpeg12FrameRateTabData =
    [
        new(0, 0),
        new(24000, 1001),
        new(24, 1),
        new(25, 1),
        new(30000, 1001),
        new(30, 1),
        new(50, 1),
        new(60000, 1001),
        new(60, 1),
        // Xing's 15fps: (9)
        new(15, 1),
        // libmpeg3's "Unofficial economy rates": (10-13)
        new(5, 1),
        new(10, 1),
        new(12, 1),
        new(15, 1),
        new(0, 0),
        // Declared [16] with fifteen initialisers, so the last entry is C's zero fill.
        new(0, 0),
    ];

    /// <summary><c>ff_mpeg2_frame_rate_tab[63][2]</c>.</summary>
    public static ReadOnlySpan<AvRational> Mpeg2FrameRateTab => Mpeg2FrameRateTabData;

    private static readonly AvRational[] Mpeg2FrameRateTabData =
    [
        new(1, 1), new(2, 1), new(3, 1),
        new(4, 1), new(5, 1), new(6, 1),
        new(8, 1), new(9, 1), new(10, 1),
        new(12, 1), new(15, 1), new(16, 1),
        new(18, 1), new(20, 1), new(24, 1),
        new(25, 1), new(30, 1), new(32, 1),
        new(36, 1), new(40, 1), new(45, 1),
        new(48, 1), new(50, 1), new(60, 1),
        new(72, 1), new(75, 1), new(80, 1),
        new(90, 1), new(96, 1), new(100, 1),
        new(120, 1), new(150, 1), new(180, 1),
        new(200, 1), new(240, 1), new(750, 1001),
        new(800, 1001), new(960, 1001), new(1000, 1001),
        new(1200, 1001), new(1250, 1001), new(1500, 1001),
        new(1600, 1001), new(1875, 1001), new(2000, 1001),
        new(2400, 1001), new(2500, 1001), new(3000, 1001),
        new(3750, 1001), new(4000, 1001), new(4800, 1001),
        new(5000, 1001), new(6000, 1001), new(7500, 1001),
        new(8000, 1001), new(10000, 1001), new(12000, 1001),
        new(15000, 1001), new(20000, 1001), new(24000, 1001),
        new(30000, 1001), new(60000, 1001), new(0, 0),
    ];

    /// <summary><c>ff_mpeg1_aspect[16]</c>.</summary>
    /// <remarks>FFmpeg lists 15 values in an array declared [16]. The remaining 1 are implicitly zero and are kept, because code that indexes past the listed values would otherwise read out of bounds.</remarks>
    public static ReadOnlySpan<float> Mpeg1Aspect => Mpeg1AspectData;

    private static readonly float[] Mpeg1AspectData =
    [
        0.0000f, 1.0000f, 0.6735f, 0.7031f, 0.7615f, 0.8055f, 0.8437f, 0.8935f, 0.9157f, 0.9815f, 1.0255f, 1.0695f, 1.0950f, 1.1575f, 1.2015f, 0f,
    ];

    /// <summary><c>ff_mpeg2_aspect[16][2]</c>.</summary>
    public static ReadOnlySpan<AvRational> Mpeg2Aspect => Mpeg2AspectData;

    private static readonly AvRational[] Mpeg2AspectData =
    [
        new(0, 1), new(1, 1), new(4, 3),
        new(16, 9), new(221, 100), new(0, 1),
        new(0, 1), new(0, 1), new(0, 1),
        new(0, 1), new(0, 1), new(0, 1),
        new(0, 1), new(0, 1), new(0, 1),
        new(0, 1),
    ];

}

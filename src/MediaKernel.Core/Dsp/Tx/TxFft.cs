// libavutil/tx_template.c's split-radix complex FFT, and tx.c's permutation.
//
// This is a translation, not an FFT. Any correct FFT of a given length computes the same
// transform to within rounding, and rounding is the point: AAC's residual against the ISO
// reference decoder is float rounding in the IMDCT, and the only way to close it is to perform
// the same operations in the same order as av_tx. So the butterfly macros are written out
// literally, including the places where the C subtracts a negated value rather than adding, and
// the order of every assignment is FFmpeg's.
//
// The codelets are "_ns" - no shuffle. They expect their input already permuted by
// split_radix_permutation and write their output in natural order, which is why the MDCT above
// carries the permutation in its own index map.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Dsp.Tx;

/// <summary>One complex sample, laid out as FFmpeg's <c>TXComplex</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TxComplex
{
    public float Re;
    public float Im;
}

/// <summary>The split-radix FFT codelets and the index map they expect.</summary>
internal static class TxFft
{
    /// <summary><c>BF(x, y, a, b)</c>: the difference first, then the sum.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Bf(out float x, out float y, float a, float b)
    {
        x = a - b;
        y = a + b;
    }

    /// <summary>
    /// <c>BUTTERFLIES(a0, a1, a2, a3)</c>, with the four temporaries the macro reads from its
    /// caller passed in.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Butterflies(ref TxComplex a0, ref TxComplex a1,
                                    ref TxComplex a2, ref TxComplex a3,
                                    float t1, float t2, float t5, float t6)
    {
        float r0 = a0.Re;
        float i0 = a0.Im;
        float r1 = a1.Re;
        float i1 = a1.Im;

        Bf(out float t3, out t5, t5, t1);
        Bf(out a2.Re, out a0.Re, r0, t5);
        Bf(out a3.Im, out a1.Im, i1, t3);
        Bf(out float t4, out t6, t2, t6);
        Bf(out a3.Re, out a1.Re, r1, t4);
        Bf(out a2.Im, out a0.Im, i0, t6);
    }

    /// <summary><c>TRANSFORM(a0, a1, a2, a3, wre, wim)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transform(ref TxComplex a0, ref TxComplex a1,
                                  ref TxComplex a2, ref TxComplex a3,
                                  float wre, float wim)
    {
        // CMUL(t1, t2, a2.re, a2.im, wre, -wim)
        float t1 = a2.Re * wre - a2.Im * -wim;
        float t2 = a2.Re * -wim + a2.Im * wre;
        // CMUL(t5, t6, a3.re, a3.im, wre, wim)
        float t5 = a3.Re * wre - a3.Im * wim;
        float t6 = a3.Re * wim + a3.Im * wre;

        Butterflies(ref a0, ref a1, ref a2, ref a3, t1, t2, t5, t6);
    }

    /// <summary><c>ff_tx_fft_sr_combine</c>: <c>z[0..8n-1]</c> against the length's table.</summary>
    public static void SrCombine(Span<TxComplex> z, float[] cos, int len)
    {
        int o1 = 2 * len;
        int o2 = 4 * len;
        int o3 = 6 * len;
        int wim = o1 - 7;       // index into cos, walked backwards
        int c = 0;              // index into cos, walked forwards
        int zi = 0;

        for (int i = 0; i < len; i += 4)
        {
            Transform(ref z[zi + 0], ref z[zi + o1 + 0], ref z[zi + o2 + 0], ref z[zi + o3 + 0],
                      cos[c + 0], cos[wim + 7]);
            Transform(ref z[zi + 2], ref z[zi + o1 + 2], ref z[zi + o2 + 2], ref z[zi + o3 + 2],
                      cos[c + 2], cos[wim + 5]);
            Transform(ref z[zi + 4], ref z[zi + o1 + 4], ref z[zi + o2 + 4], ref z[zi + o3 + 4],
                      cos[c + 4], cos[wim + 3]);
            Transform(ref z[zi + 6], ref z[zi + o1 + 6], ref z[zi + o2 + 6], ref z[zi + o3 + 6],
                      cos[c + 6], cos[wim + 1]);

            Transform(ref z[zi + 1], ref z[zi + o1 + 1], ref z[zi + o2 + 1], ref z[zi + o3 + 1],
                      cos[c + 1], cos[wim + 6]);
            Transform(ref z[zi + 3], ref z[zi + o1 + 3], ref z[zi + o2 + 3], ref z[zi + o3 + 3],
                      cos[c + 3], cos[wim + 4]);
            Transform(ref z[zi + 5], ref z[zi + o1 + 5], ref z[zi + o2 + 5], ref z[zi + o3 + 5],
                      cos[c + 5], cos[wim + 2]);
            Transform(ref z[zi + 7], ref z[zi + o1 + 7], ref z[zi + o2 + 7], ref z[zi + o3 + 7],
                      cos[c + 7], cos[wim + 0]);

            zi += 2 * 4;
            c += 2 * 4;
            wim -= 2 * 4;
        }
    }

    /// <summary><c>ff_tx_fft2_ns</c>.</summary>
    private static void Fft2(Span<TxComplex> dst, ReadOnlySpan<TxComplex> src)
    {
        TxComplex tmp;
        Bf(out tmp.Re, out dst[0].Re, src[0].Re, src[1].Re);
        Bf(out tmp.Im, out dst[0].Im, src[0].Im, src[1].Im);
        dst[1] = tmp;
    }

    /// <summary><c>ff_tx_fft4_ns</c>.</summary>
    private static void Fft4(Span<TxComplex> dst, ReadOnlySpan<TxComplex> src)
    {
        Bf(out float t3, out float t1, src[0].Re, src[1].Re);
        Bf(out float t8, out float t6, src[3].Re, src[2].Re);
        Bf(out dst[2].Re, out dst[0].Re, t1, t6);
        Bf(out float t4, out float t2, src[0].Im, src[1].Im);
        Bf(out float t7, out float t5, src[2].Im, src[3].Im);
        Bf(out dst[3].Im, out dst[1].Im, t4, t8);
        Bf(out dst[3].Re, out dst[1].Re, t3, t7);
        Bf(out dst[2].Im, out dst[0].Im, t2, t5);
    }

    /// <summary><c>ff_tx_fft8_ns</c>.</summary>
    private static void Fft8(Span<TxComplex> dst, ReadOnlySpan<TxComplex> src)
    {
        float cos = TxTables.SrTable(8)[1];

        Fft4(dst, src);

        Bf(out float t1, out dst[5].Re, src[4].Re, -src[5].Re);
        Bf(out float t2, out dst[5].Im, src[4].Im, -src[5].Im);
        Bf(out float t5, out dst[7].Re, src[6].Re, -src[7].Re);
        Bf(out float t6, out dst[7].Im, src[6].Im, -src[7].Im);

        Butterflies(ref dst[0], ref dst[2], ref dst[4], ref dst[6], t1, t2, t5, t6);
        Transform(ref dst[1], ref dst[3], ref dst[5], ref dst[7], cos, cos);
    }

    /// <summary><c>ff_tx_fft16_ns</c>.</summary>
    private static void Fft16(Span<TxComplex> dst, ReadOnlySpan<TxComplex> src)
    {
        float[] cos = TxTables.SrTable(16);
        float cos16_1 = cos[1];
        float cos16_2 = cos[2];
        float cos16_3 = cos[3];

        Fft8(dst, src);
        Fft4(dst[8..], src[8..]);
        Fft4(dst[12..], src[12..]);

        float t1 = dst[8].Re;
        float t2 = dst[8].Im;
        float t5 = dst[12].Re;
        float t6 = dst[12].Im;
        Butterflies(ref dst[0], ref dst[4], ref dst[8], ref dst[12], t1, t2, t5, t6);

        Transform(ref dst[2], ref dst[6], ref dst[10], ref dst[14], cos16_2, cos16_2);
        Transform(ref dst[1], ref dst[5], ref dst[9], ref dst[13], cos16_1, cos16_3);
        Transform(ref dst[3], ref dst[7], ref dst[11], ref dst[15], cos16_3, cos16_1);
    }

    /// <summary>
    /// The split-radix codelet for any power-of-two length: half-length, then two
    /// quarter-lengths, then the combine.
    /// </summary>
    /// <remarks>
    /// <c>DECL_SR_CODELET(n, n2, n4)</c> generates seventeen of these in the C, each calling the
    /// next two down by name. The recursion here is that macro expansion, with the same three
    /// calls in the same order against the same table - not a re-derivation of the algorithm.
    /// </remarks>
    public static void FftNs(Span<TxComplex> dst, ReadOnlySpan<TxComplex> src, int n)
    {
        switch (n)
        {
            case 2: Fft2(dst, src); return;
            case 4: Fft4(dst, src); return;
            case 8: Fft8(dst, src); return;
            case 16: Fft16(dst, src); return;
        }

        if (n < 32 || (n & (n - 1)) != 0)
            throw new ArgumentOutOfRangeException(
                nameof(n),
                $"tx: no split-radix codelet for length {n}. The codelets cover powers of two "
              + "from 2 to 131072; anything else needs the prime-factor path, which is not "
              + "translated.");

        int n4 = n >> 2;
        FftNs(dst, src, n >> 1);
        FftNs(dst[(n4 * 2)..], src[(n4 * 2)..], n4);
        FftNs(dst[(n4 * 3)..], src[(n4 * 3)..], n4);
        SrCombine(dst, TxTables.SrTable(n), n4 >> 1);
    }

    /// <summary><c>split_radix_permutation</c>.</summary>
    public static int SplitRadixPermutation(int i, int len, bool inverse)
    {
        len >>= 1;
        if (len <= 1)
            return i & 1;
        if ((i & len) == 0)
            return SplitRadixPermutation(i, len, inverse) * 2;

        len >>= 1;
        return SplitRadixPermutation(i, len, inverse) * 4
             + 1 - 2 * (((i & len) == 0 ? 1 : 0) ^ (inverse ? 1 : 0));
    }

    /// <summary>
    /// <c>ff_tx_gen_ptwo_revtab</c>. <paramref name="scatter"/> is the C's
    /// <c>FF_TX_MAP_SCATTER</c>; the MDCT uses gather for the inverse and scatter for the
    /// forward direction.
    /// </summary>
    public static int[] GeneratePowerOfTwoRevTab(int len, bool inverse, bool scatter)
    {
        var map = new int[len];

        if (scatter)
        {
            for (int i = 0; i < len; i++)
                map[-SplitRadixPermutation(i, len, inverse) & (len - 1)] = i;
        }
        else
        {
            for (int i = 0; i < len; i++)
                map[i] = -SplitRadixPermutation(i, len, inverse) & (len - 1);
        }

        return map;
    }
}

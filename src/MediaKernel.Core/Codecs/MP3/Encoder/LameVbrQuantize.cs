// Translated from lame-3.100/libmp3lame/vbrquantize.c - the VBR scalefactor search.
//
// This is the quantiser behind vbr_mtrh and vbr_mt. Where quantize.c's outer_loop starts from
// one global gain and amplifies bands until the noise fits, this one works the other way: for
// every scalefactor band it binary-searches the largest scalefactor whose quantisation noise
// still fits under l3_xmin, then constrains that per-band wish list into what the format can
// actually store - a global gain, a scalefac_scale, a preflag, three subblock gains and
// scalefactors inside their max_range - and finally spends whatever bits are left flattening
// the distribution until the frame fits.
//
// Three details of the C that the types here have to keep:
//
//  * algo_t carries two function pointers, `alloc` and `find`. They are enums here and the
//    call sites switch, because a delegate would allocate per granule and the project does not
//    allocate per frame. The C's two choices for each are all there are.
//  * `x[4]` inside k_34_4 and calc_sfb_noise_x34 is DOUBLEX, which is `double` under
//    TAKEHIRO_IEEE754_HACK. The products that fill it are float * float, so they form in float
//    and widen; the differences that refill it are float too. Only the squares and their sum
//    are evaluated in double, and the running total narrows back to float on every store.
//  * calc_noise_cache did_it[256] is cleared on every call to find_scalefac_x34. That is 2 KB
//    of zeroing per scalefactor band, and it is what makes the cache correct rather than fast.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>algo_t.alloc</c> - which constrain function this granule uses.</summary>
internal enum VbrAlloc
{
    /// <summary><c>long_block_constrain</c>.</summary>
    LongBlock,

    /// <summary><c>short_block_constrain</c>.</summary>
    ShortBlock,
}

/// <summary><c>algo_t.find</c> - which scalefactor search this granule uses.</summary>
internal enum VbrFind
{
    /// <summary><c>find_scalefac_x34</c>, the binary search.</summary>
    Search,

    /// <summary><c>guess_scalefac_x34</c>, the closed form, for <c>full_outer_loop &lt; 0</c>.</summary>
    Guess,
}

/// <summary><c>struct algo_s</c>.</summary>
/// <remarks>
/// <c>cod_info</c> is a pointer into <c>gfc-&gt;l3_side.tt</c> in the C and is held here as the
/// granule and channel it points at, because a C# struct cannot carry a <c>ref</c> field unless
/// it is a <c>ref struct</c>, and <c>that_[2][2]</c> is an array.
/// </remarks>
internal struct VbrAlgo
{
    public VbrAlloc Alloc;
    public VbrFind Find;
    public LameInternalFlags Gfc;

    /// <summary><c>xr34orig</c> - the offset of this granule's 576 values in the flat array.</summary>
    public int Xr34Base;

    public int Gr;
    public int Ch;
    public int MingainL;
    public GrInfo.Int3 MingainS;

    /// <summary><c>that-&gt;cod_info</c>.</summary>
    public readonly ref GrInfo CodInfo => ref Gfc.L3Side.Tt[Gr * 2 + Ch];
}

/// <summary><c>vbrquantize.c</c>.</summary>
internal static class LameVbrQuantize
{
    private const int MagicInt = 0x4b000000;
    private const double MagicFloat = 65536.0 * 128;

    /// <summary><c>calc_noise_cache</c>.</summary>
    private struct CalcNoiseCache
    {
        public int Valid;
        public float Value;
    }

    [InlineArray(256)]
    private struct NoiseCache256 { private CalcNoiseCache element0; }

    /// <summary><c>int sf_temp[SFBMAX]</c> and the other SFBMAX stack arrays.</summary>
    [InlineArray(LameConstants.SfbMax)]
    private struct IntSfb { private int element0; }

    /// <summary><c>max_range_short[SBMAX_s * 3]</c>.</summary>
    private static ReadOnlySpan<byte> MaxRangeShort =>
    [
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
        7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        0, 0, 0,
    ];

    /// <summary><c>max_range_long[SBMAX_l]</c>.</summary>
    private static ReadOnlySpan<byte> MaxRangeLong =>
    [
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 0,
    ];

    /// <summary><c>max_range_long_lsf_pretab[SBMAX_l]</c>.</summary>
    private static ReadOnlySpan<byte> MaxRangeLongLsfPretab =>
    [
        7, 7, 7, 7, 7, 7, 3, 3, 3, 3, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    /// <summary><c>vec_max_c</c> - the largest of <paramref name="bw"/> values.</summary>
    /// <remarks>
    /// The C unrolls by four and falls through a <c>switch</c> for the remainder. Comparing in
    /// a different order would still find the maximum, so the unrolling is kept only because
    /// the structure is the C's.
    /// </remarks>
    public static float VecMaxC(ReadOnlySpan<float> xr34, int bw)
    {
        float xfsf = 0;
        int i = bw >> 2;
        int remaining = bw & 0x03;
        int p = 0;

        while (i-- > 0)
        {
            if (xfsf < xr34[p + 0]) xfsf = xr34[p + 0];
            if (xfsf < xr34[p + 1]) xfsf = xr34[p + 1];
            if (xfsf < xr34[p + 2]) xfsf = xr34[p + 2];
            if (xfsf < xr34[p + 3]) xfsf = xr34[p + 3];
            p += 4;
        }
        switch (remaining)
        {
            case 3:
                if (xfsf < xr34[p + 2]) xfsf = xr34[p + 2];
                goto case 2;
            case 2:
                if (xfsf < xr34[p + 1]) xfsf = xr34[p + 1];
                goto case 1;
            case 1:
                if (xfsf < xr34[p + 0]) xfsf = xr34[p + 0];
                break;
            default:
                break;
        }
        return xfsf;
    }

    /// <summary>
    /// <c>find_lowest_scalefac</c> - the smallest scalefactor that keeps the band in range.
    /// </summary>
    public static byte FindLowestScalefac(float xr34)
    {
        byte sfOk = 255;
        byte sf = 128, delsf = 64;
        const float ixmaxVal = LameQuantizeConstants.IxmaxVal;

        for (int i = 0; i < 8; ++i)
        {
            float xfsf = LameQuantizePvt.Ipow20[sf] * xr34;
            if (xfsf <= ixmaxVal)
            {
                sfOk = sf;
                sf -= delsf;
            }
            else
            {
                sf += delsf;
            }
            delsf >>= 1;
        }
        return sfOk;
    }

    /// <summary><c>k_34_4</c> - quantise four values through the IEEE754 rounding trick.</summary>
    /// <remarks>
    /// The same trick as <c>quantize_lines_xrpow</c>, and the same discipline: the sums stay
    /// double right up to the store into the float, because that store is the rounding.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void K344(Span<double> x, Span<int> l3)
    {
        x[0] += MagicFloat;
        int i0 = BitConverter.SingleToInt32Bits((float)x[0]);
        x[1] += MagicFloat;
        int i1 = BitConverter.SingleToInt32Bits((float)x[1]);
        x[2] += MagicFloat;
        int i2 = BitConverter.SingleToInt32Bits((float)x[2]);
        x[3] += MagicFloat;
        int i3 = BitConverter.SingleToInt32Bits((float)x[3]);

        i0 = BitConverter.SingleToInt32Bits((float)(x[0] + LameQuantizePvt.Adj43[i0 - MagicInt]));
        i1 = BitConverter.SingleToInt32Bits((float)(x[1] + LameQuantizePvt.Adj43[i1 - MagicInt]));
        i2 = BitConverter.SingleToInt32Bits((float)(x[2] + LameQuantizePvt.Adj43[i2 - MagicInt]));
        i3 = BitConverter.SingleToInt32Bits((float)(x[3] + LameQuantizePvt.Adj43[i3 - MagicInt]));

        l3[0] = i0 - MagicInt;
        l3[1] = i1 - MagicInt;
        l3[2] = i2 - MagicInt;
        l3[3] = i3 - MagicInt;
    }

    /// <summary>
    /// <c>calc_sfb_noise_x34</c> - the quantisation noise this band would get at scalefactor
    /// <paramref name="sf"/>.
    /// </summary>
    public static float CalcSfbNoiseX34(
        ReadOnlySpan<float> xr, ReadOnlySpan<float> xr34, int bw, byte sf)
    {
        Span<double> x = stackalloc double[4];
        Span<int> l3 = stackalloc int[4];
        float sfpow = LameQuantizePvt.Pow20[sf + LameQuantizeConstants.QMax2];
        float sfpow34 = LameQuantizePvt.Ipow20[sf];

        float xfsf = 0;
        int i = bw >> 2;
        int remaining = bw & 0x03;
        int p = 0;

        while (i-- > 0)
        {
            x[0] = sfpow34 * xr34[p + 0];
            x[1] = sfpow34 * xr34[p + 1];
            x[2] = sfpow34 * xr34[p + 2];
            x[3] = sfpow34 * xr34[p + 3];

            K344(x, l3);

            x[0] = Math.Abs(xr[p + 0]) - sfpow * LameQuantizePvt.Pow43[l3[0]];
            x[1] = Math.Abs(xr[p + 1]) - sfpow * LameQuantizePvt.Pow43[l3[1]];
            x[2] = Math.Abs(xr[p + 2]) - sfpow * LameQuantizePvt.Pow43[l3[2]];
            x[3] = Math.Abs(xr[p + 3]) - sfpow * LameQuantizePvt.Pow43[l3[3]];
            xfsf = (float)(xfsf + ((x[0] * x[0] + x[1] * x[1]) + (x[2] * x[2] + x[3] * x[3])));

            p += 4;
        }
        if (remaining != 0)
        {
            x[0] = x[1] = x[2] = x[3] = 0;
            switch (remaining)
            {
                case 3: x[2] = sfpow34 * xr34[p + 2]; goto case 2;
                case 2: x[1] = sfpow34 * xr34[p + 1]; goto case 1;
                case 1: x[0] = sfpow34 * xr34[p + 0]; break;
            }

            K344(x, l3);
            x[0] = x[1] = x[2] = x[3] = 0;

            switch (remaining)
            {
                case 3: x[2] = Math.Abs(xr[p + 2]) - sfpow * LameQuantizePvt.Pow43[l3[2]]; goto case 2;
                case 2: x[1] = Math.Abs(xr[p + 1]) - sfpow * LameQuantizePvt.Pow43[l3[1]]; goto case 1;
                case 1: x[0] = Math.Abs(xr[p + 0]) - sfpow * LameQuantizePvt.Pow43[l3[0]]; break;
            }
            xfsf = (float)(xfsf + ((x[0] * x[0] + x[1] * x[1]) + (x[2] * x[2] + x[3] * x[3])));
        }
        return xfsf;
    }

    /// <summary>
    /// <c>tri_calc_sfb_noise_x34</c> - is this scalefactor, or either neighbour, distorted?
    /// </summary>
    private static byte TriCalcSfbNoiseX34(
        ReadOnlySpan<float> xr, ReadOnlySpan<float> xr34, float l3Xmin, int bw, byte sf,
        Span<CalcNoiseCache> didIt)
    {
        if (didIt[sf].Valid == 0)
        {
            didIt[sf].Valid = 1;
            didIt[sf].Value = CalcSfbNoiseX34(xr, xr34, bw, sf);
        }
        if (l3Xmin < didIt[sf].Value) return 1;

        if (sf < 255)
        {
            byte sfX = (byte)(sf + 1);
            if (didIt[sfX].Valid == 0)
            {
                didIt[sfX].Valid = 1;
                didIt[sfX].Value = CalcSfbNoiseX34(xr, xr34, bw, sfX);
            }
            if (l3Xmin < didIt[sfX].Value) return 1;
        }
        if (sf > 0)
        {
            byte sfX = (byte)(sf - 1);
            if (didIt[sfX].Valid == 0)
            {
                didIt[sfX].Valid = 1;
                didIt[sfX].Value = CalcSfbNoiseX34(xr, xr34, bw, sfX);
            }
            if (l3Xmin < didIt[sfX].Value) return 1;
        }
        return 0;
    }

    /// <summary><c>calc_scalefac</c> - the step size the allowed masking implies.</summary>
    /// <remarks>
    /// Every operation here is single precision in the C, including <c>log10f</c>, and the
    /// truncation is of the whole expression. Only <c>guess_scalefac_x34</c> calls it, so it is
    /// reached only when <c>full_outer_loop</c> is negative.
    /// </remarks>
    public static int CalcScalefac(float l3Xmin, int bw)
    {
        const float c = 5.799142446f; /* 10 * 10^(2/3) * log10(4/3) */
        return 210 + (int)(c * MathF.Log10(l3Xmin / bw) - .5f);
    }

    /// <summary><c>guess_scalefac_x34</c>.</summary>
    public static byte GuessScalefacX34(
        ReadOnlySpan<float> xr, ReadOnlySpan<float> xr34, float l3Xmin, int bw, byte sfMin)
    {
        int guess = CalcScalefac(l3Xmin, bw);
        if (guess < sfMin) return sfMin;
        if (guess >= 255) return 255;
        return (byte)guess;
    }

    /// <summary>
    /// <c>find_scalefac_x34</c> - the largest scalefactor that introduces no audible distortion.
    /// </summary>
    public static byte FindScalefacX34(
        ReadOnlySpan<float> xr, ReadOnlySpan<float> xr34, float l3Xmin, int bw, byte sfMin)
    {
        NoiseCache256 cache = default;
        Span<CalcNoiseCache> didIt = cache;
        byte sf = 128, sfOk = 255, delsf = 128, seenGoodOne = 0;

        for (int i = 0; i < 8; ++i)
        {
            delsf >>= 1;
            if (sf <= sfMin)
            {
                sf += delsf;
            }
            else
            {
                byte bad = TriCalcSfbNoiseX34(xr, xr34, l3Xmin, bw, sf, didIt);
                if (bad != 0)
                {
                    sf -= delsf;        // distortion; try a smaller scalefactor
                }
                else
                {
                    sfOk = sf;
                    sf += delsf;
                    seenGoodOne = 1;
                }
            }
        }
        // returning a scalefac without distortion, if possible
        if (seenGoodOne > 0) sf = sfOk;
        if (sf <= sfMin) sf = sfMin;
        return sf;
    }

    /// <summary><c>block_sf</c> - the per-band wish list, and the largest of it.</summary>
    public static int BlockSf(
        ref VbrAlgo that, ReadOnlySpan<float> l3Xmin, Span<int> vbrsf, Span<int> vbrsfmin,
        ReadOnlySpan<float> xr34Orig)
    {
        ref GrInfo codInfo = ref that.CodInfo;
        ReadOnlySpan<float> xr = codInfo.Xr;
        int maxNonzeroCoeff = codInfo.MaxNonzeroCoeff;
        byte maxsf = 0;
        int sfb = 0, mO = -1;
        int j = 0, i = 0;
        int psymax = codInfo.Psymax;

        that.MingainL = 0;
        that.MingainS[0] = 0;
        that.MingainS[1] = 0;
        that.MingainS[2] = 0;

        while (j <= maxNonzeroCoeff)
        {
            int w = codInfo.Width[sfb];
            int m = maxNonzeroCoeff - j + 1;
            int l = w;
            byte m1, m2;
            if (l > m) l = m;

            float maxXr34 = VecMaxC(xr34Orig[j..], l);

            m1 = FindLowestScalefac(maxXr34);
            vbrsfmin[sfb] = m1;
            if (that.MingainL < m1) that.MingainL = m1;
            if (that.MingainS[i] < m1) that.MingainS[i] = m1;
            if (++i > 2) i = 0;

            // mpeg2.5 at 8 kHz doesn't use all scalefactors; the unused have width 2
            if (sfb < psymax && w > 2)
            {
                if (codInfo.EnergyAboveCutoff[sfb] != 0)
                {
                    m2 = that.Find == VbrFind.Guess
                        ? GuessScalefacX34(xr[j..], xr34Orig[j..], l3Xmin[sfb], l, m1)
                        : FindScalefacX34(xr[j..], xr34Orig[j..], l3Xmin[sfb], l, m1);

                    if (maxsf < m2) maxsf = m2;
                    if (mO < m2 && m2 < 255) mO = m2;
                }
                else
                {
                    m2 = 255;
                    maxsf = 255;
                }
            }
            else
            {
                if (maxsf < m1) maxsf = m1;
                m2 = maxsf;
            }
            vbrsf[sfb] = m2;
            ++sfb;
            j += w;
        }
        for (; sfb < LameConstants.SfbMax; ++sfb)
        {
            vbrsf[sfb] = maxsf;
            vbrsfmin[sfb] = 0;
        }
        if (mO > -1)
        {
            maxsf = (byte)mO;
            for (sfb = 0; sfb < LameConstants.SfbMax; ++sfb)
                if (vbrsf[sfb] == 255)
                    vbrsf[sfb] = mO;
        }
        return maxsf;
    }

    /// <summary><c>quantize_x34</c> - quantise the granule with the scalefactors now chosen.</summary>
    public static void QuantizeX34(ref VbrAlgo that, ReadOnlySpan<float> xr34Orig)
    {
        Span<double> x = stackalloc double[4];
        Span<int> tmpL3 = stackalloc int[4];
        ref GrInfo codInfo = ref that.CodInfo;
        int ifqstep = codInfo.ScalefacScale == 0 ? 2 : 4;
        Span<int> l3 = codInfo.L3Enc;
        int l3p = 0, xp = 0;
        int j = 0, sfb = 0;
        int maxNonzeroCoeff = codInfo.MaxNonzeroCoeff;

        while (j <= maxNonzeroCoeff)
        {
            int s = (codInfo.Scalefac[sfb] + (codInfo.Preflag != 0 ? LameQuantizePvt.Pretab[sfb] : 0))
                        * ifqstep
                  + codInfo.SubblockGain[codInfo.Window[sfb]] * 8;
            byte sfac = (byte)(codInfo.GlobalGain - s);
            float sfpow34 = LameQuantizePvt.Ipow20[sfac];
            int w = codInfo.Width[sfb];
            int m = maxNonzeroCoeff - j + 1;

            j += w;
            ++sfb;

            int i = w <= m ? w : m;
            int remaining = i & 0x03;
            i >>= 2;

            while (i-- > 0)
            {
                x[0] = sfpow34 * xr34Orig[xp + 0];
                x[1] = sfpow34 * xr34Orig[xp + 1];
                x[2] = sfpow34 * xr34Orig[xp + 2];
                x[3] = sfpow34 * xr34Orig[xp + 3];

                K344(x, l3[l3p..]);

                l3p += 4;
                xp += 4;
            }
            if (remaining != 0)
            {
                x[0] = x[1] = x[2] = x[3] = 0;
                switch (remaining)
                {
                    case 3: x[2] = sfpow34 * xr34Orig[xp + 2]; goto case 2;
                    case 2: x[1] = sfpow34 * xr34Orig[xp + 1]; goto case 1;
                    case 1: x[0] = sfpow34 * xr34Orig[xp + 0]; break;
                }

                K344(x, tmpL3);

                switch (remaining)
                {
                    case 3: l3[l3p + 2] = tmpL3[2]; goto case 2;
                    case 2: l3[l3p + 1] = tmpL3[1]; goto case 1;
                    case 1: l3[l3p + 0] = tmpL3[0]; break;
                }

                l3p += remaining;
                xp += remaining;
            }
        }
    }

    /// <summary><c>set_subblock_gain</c>.</summary>
    public static void SetSubblockGain(ref GrInfo codInfo, in GrInfo.Int3 mingainS, Span<int> sf)
    {
        const int maxrange1 = 15, maxrange2 = 7;
        int ifqstepShift = codInfo.ScalefacScale == 0 ? 1 : 2;
        Span<int> sbg = codInfo.SubblockGain;
        int psymax = codInfo.Psymax;
        int psydiv = 18;
        int minSbg = 7;

        if (psydiv > psymax) psydiv = psymax;

        for (int i = 0; i < 3; ++i)
        {
            int maxsf1 = 0, maxsf2 = 0, minsf = 1000;
            int sfb;

            for (sfb = i; sfb < psydiv; sfb += 3)        // part 1
            {
                int v = -sf[sfb];
                if (maxsf1 < v) maxsf1 = v;
                if (minsf > v) minsf = v;
            }
            for (; sfb < LameConstants.SfbMax; sfb += 3) // part 2
            {
                int v = -sf[sfb];
                if (maxsf2 < v) maxsf2 = v;
                if (minsf > v) minsf = v;
            }

            // boost subblock gain as little as possible, so that maxsf1 is still reachable
            // with scalefactors: 8*sbg >= maxsf1
            {
                int m1 = maxsf1 - (maxrange1 << ifqstepShift);
                int m2 = maxsf2 - (maxrange2 << ifqstepShift);
                maxsf1 = Math.Max(m1, m2);
            }
            sbg[i] = minsf > 0 ? minsf >> 3 : 0;
            if (maxsf1 > 0)
            {
                int m1 = sbg[i];
                int m2 = (maxsf1 + 7) >> 3;
                sbg[i] = Math.Max(m1, m2);
            }
            if (sbg[i] > 0 && mingainS[i] > codInfo.GlobalGain - sbg[i] * 8)
                sbg[i] = (codInfo.GlobalGain - mingainS[i]) >> 3;
            if (sbg[i] > 7) sbg[i] = 7;
            if (minSbg > sbg[i]) minSbg = sbg[i];
        }

        int sbg0 = sbg[0] * 8, sbg1 = sbg[1] * 8, sbg2 = sbg[2] * 8;
        for (int sfb = 0; sfb < LameConstants.SfbMax; sfb += 3)
        {
            sf[sfb + 0] += sbg0;
            sf[sfb + 1] += sbg1;
            sf[sfb + 2] += sbg2;
        }
        if (minSbg > 0)
        {
            for (int i = 0; i < 3; ++i) sbg[i] -= minSbg;
            codInfo.GlobalGain -= minSbg * 8;
        }
    }

    /// <summary><c>set_scalefacs</c>.</summary>
    public static void SetScalefacs(
        ref GrInfo codInfo, ReadOnlySpan<int> vbrsfmin, Span<int> sf, ReadOnlySpan<byte> maxRange)
    {
        int ifqstep = codInfo.ScalefacScale == 0 ? 2 : 4;
        int ifqstepShift = codInfo.ScalefacScale == 0 ? 1 : 2;
        Span<int> scalefac = codInfo.Scalefac;
        int sfbmax = codInfo.Sfbmax;
        ReadOnlySpan<int> sbg = codInfo.SubblockGain;
        ReadOnlySpan<int> window = codInfo.Window;
        int preflag = codInfo.Preflag;
        int sfb;

        if (preflag != 0)
            for (sfb = 11; sfb < sfbmax; ++sfb)
                sf[sfb] += LameQuantizePvt.Pretab[sfb] * ifqstep;

        for (sfb = 0; sfb < sfbmax; ++sfb)
        {
            int gain = codInfo.GlobalGain - sbg[window[sfb]] * 8
                     - (preflag != 0 ? LameQuantizePvt.Pretab[sfb] : 0) * ifqstep;

            if (sf[sfb] < 0)
            {
                int m = gain - vbrsfmin[sfb];
                // ifqstep*scalefac >= -sf[sfb], so round UP
                scalefac[sfb] = (ifqstep - 1 - sf[sfb]) >> ifqstepShift;

                if (scalefac[sfb] > maxRange[sfb]) scalefac[sfb] = maxRange[sfb];
                if (scalefac[sfb] > 0 && (scalefac[sfb] << ifqstepShift) > m)
                    scalefac[sfb] = m >> ifqstepShift;
            }
            else
            {
                scalefac[sfb] = 0;
            }
        }
        for (; sfb < LameConstants.SfbMax; ++sfb)
            scalefac[sfb] = 0;      // sfb21
    }

    /// <summary><c>checkScalefactor</c> - the debug-build invariant of the two constrainers.</summary>
    /// <remarks>
    /// The C guards this with <c>#ifndef NDEBUG</c> and calls it only from <c>assert</c>, so a
    /// release build never runs it. <see cref="Debug.Assert(bool)"/> here keeps that exactly:
    /// the call disappears from the release build the gate measures.
    /// </remarks>
    public static bool CheckScalefactor(in GrInfo codInfo, ReadOnlySpan<int> vbrsfmin)
    {
        int ifqstep = codInfo.ScalefacScale == 0 ? 2 : 4;
        for (int sfb = 0; sfb < codInfo.Psymax; ++sfb)
        {
            int s = (codInfo.Scalefac[sfb]
                      + (codInfo.Preflag != 0 ? LameQuantizePvt.Pretab[sfb] : 0)) * ifqstep
                  + codInfo.SubblockGain[codInfo.Window[sfb]] * 8;

            if (codInfo.GlobalGain - s < vbrsfmin[sfb]) return false;
        }
        return true;
    }

    /// <summary><c>short_block_constrain</c>.</summary>
    public static void ShortBlockConstrain(
        ref VbrAlgo that, ReadOnlySpan<int> vbrsf, ReadOnlySpan<int> vbrsfmin, int vbrmax)
    {
        ref GrInfo codInfo = ref that.CodInfo;
        ref SessionConfig cfg = ref that.Gfc.Cfg;
        int maxminsfb = that.MingainL;
        int mover, maxover0 = 0, maxover1 = 0, delta = 0;
        int psymax = codInfo.Psymax;

        for (int sfb = 0; sfb < psymax; ++sfb)
        {
            int v = vbrmax - vbrsf[sfb];
            if (delta < v) delta = v;
            int v0 = v - (4 * 14 + 2 * MaxRangeShort[sfb]);
            int v1 = v - (4 * 14 + 4 * MaxRangeShort[sfb]);
            if (maxover0 < v0) maxover0 = v0;
            if (maxover1 < v1) maxover1 = v1;
        }
        // noise_shaping == 2 allows scalefac_scale = 1
        mover = cfg.NoiseShaping == 2 ? Math.Min(maxover0, maxover1) : maxover0;

        if (delta > mover) delta = mover;
        vbrmax -= delta;
        maxover0 -= mover;
        maxover1 -= mover;

        if (maxover0 == 0) codInfo.ScalefacScale = 0;
        else if (maxover1 == 0) codInfo.ScalefacScale = 1;

        if (vbrmax < maxminsfb) vbrmax = maxminsfb;
        codInfo.GlobalGain = vbrmax;

        if (codInfo.GlobalGain < 0) codInfo.GlobalGain = 0;
        else if (codInfo.GlobalGain > 255) codInfo.GlobalGain = 255;

        {
            IntSfb sfTempStore = default;
            Span<int> sfTemp = sfTempStore;
            for (int sfb = 0; sfb < LameConstants.SfbMax; ++sfb)
                sfTemp[sfb] = vbrsf[sfb] - vbrmax;

            SetSubblockGain(ref codInfo, in that.MingainS, sfTemp);
            SetScalefacs(ref codInfo, vbrsfmin, sfTemp, MaxRangeShort);
        }
        Debug.Assert(CheckScalefactor(in codInfo, vbrsfmin));
    }

    /// <summary><c>long_block_constrain</c>.</summary>
    public static void LongBlockConstrain(
        ref VbrAlgo that, ReadOnlySpan<int> vbrsf, ReadOnlySpan<int> vbrsfmin, int vbrmax)
    {
        ref GrInfo codInfo = ref that.CodInfo;
        ref SessionConfig cfg = ref that.Gfc.Cfg;
        int maxminsfb = that.MingainL;
        int psymax = codInfo.Psymax;
        int delta = 0, vm0p = 1, vm1p = 1;
        int sfb;

        ReadOnlySpan<byte> maxRangep =
            cfg.ModeGr == 2 ? MaxRangeLong : MaxRangeLongLsfPretab;

        int maxover0 = 0, maxover1 = 0, maxover0p = 0, maxover1p = 0;

        for (sfb = 0; sfb < psymax; ++sfb)
        {
            int v = vbrmax - vbrsf[sfb];
            if (delta < v) delta = v;
            int v0 = v - 2 * MaxRangeLong[sfb];
            int v1 = v - 4 * MaxRangeLong[sfb];
            int v0p = v - 2 * (maxRangep[sfb] + LameQuantizePvt.Pretab[sfb]);
            int v1p = v - 4 * (maxRangep[sfb] + LameQuantizePvt.Pretab[sfb]);
            if (maxover0 < v0) maxover0 = v0;
            if (maxover1 < v1) maxover1 = v1;
            if (maxover0p < v0p) maxover0p = v0p;
            if (maxover1p < v1p) maxover1p = v1p;
        }
        if (vm0p == 1)
        {
            int gain = vbrmax - maxover0p;
            if (gain < maxminsfb) gain = maxminsfb;
            for (sfb = 0; sfb < psymax; ++sfb)
            {
                int a = gain - vbrsfmin[sfb] - 2 * LameQuantizePvt.Pretab[sfb];
                if (a <= 0) { vm0p = 0; vm1p = 0; break; }
            }
        }
        if (vm1p == 1)
        {
            int gain = vbrmax - maxover1p;
            if (gain < maxminsfb) gain = maxminsfb;
            for (sfb = 0; sfb < psymax; ++sfb)
            {
                int b = gain - vbrsfmin[sfb] - 4 * LameQuantizePvt.Pretab[sfb];
                if (b <= 0) { vm1p = 0; break; }
            }
        }
        if (vm0p == 0) maxover0p = maxover0;
        if (vm1p == 0) maxover1p = maxover1;
        if (cfg.NoiseShaping != 2)
        {
            maxover1 = maxover0;
            maxover1p = maxover0p;
        }
        int mover = Math.Min(maxover0, maxover0p);
        mover = Math.Min(mover, maxover1);
        mover = Math.Min(mover, maxover1p);

        if (delta > mover) delta = mover;
        vbrmax -= delta;
        if (vbrmax < maxminsfb) vbrmax = maxminsfb;
        maxover0 -= mover;
        maxover0p -= mover;
        maxover1 -= mover;
        maxover1p -= mover;

        if (maxover0 == 0)
        {
            codInfo.ScalefacScale = 0;
            codInfo.Preflag = 0;
            maxRangep = MaxRangeLong;
        }
        else if (maxover0p == 0)
        {
            codInfo.ScalefacScale = 0;
            codInfo.Preflag = 1;
        }
        else if (maxover1 == 0)
        {
            codInfo.ScalefacScale = 1;
            codInfo.Preflag = 0;
            maxRangep = MaxRangeLong;
        }
        else if (maxover1p == 0)
        {
            codInfo.ScalefacScale = 1;
            codInfo.Preflag = 1;
        }
        else
        {
            // The C asserts here. One of the four has to be zero once mover is subtracted, so
            // reaching this means the arithmetic above is wrong, not the input.
            throw new InvalidOperationException(
                "LAME VBR: long_block_constrain found no usable (scalefac_scale, preflag) "
              + $"pair; maxover0 {maxover0}, maxover0p {maxover0p}, maxover1 {maxover1}, "
              + $"maxover1p {maxover1p}.");
        }

        codInfo.GlobalGain = vbrmax;
        if (codInfo.GlobalGain < 0) codInfo.GlobalGain = 0;
        else if (codInfo.GlobalGain > 255) codInfo.GlobalGain = 255;

        {
            IntSfb sfTempStore = default;
            Span<int> sfTemp = sfTempStore;
            for (sfb = 0; sfb < LameConstants.SfbMax; ++sfb)
                sfTemp[sfb] = vbrsf[sfb] - vbrmax;

            SetScalefacs(ref codInfo, vbrsfmin, sfTemp, maxRangep);
        }
        Debug.Assert(CheckScalefactor(in codInfo, vbrsfmin));
    }

    /// <summary><c>that-&gt;alloc</c> - the dispatch the C does through a function pointer.</summary>
    private static void Alloc(
        ref VbrAlgo that, ReadOnlySpan<int> sf, ReadOnlySpan<int> vbrsfmin, int vbrmax)
    {
        switch (that.Alloc)
        {
            case VbrAlloc.ShortBlock: ShortBlockConstrain(ref that, sf, vbrsfmin, vbrmax); break;
            case VbrAlloc.LongBlock: LongBlockConstrain(ref that, sf, vbrsfmin, vbrmax); break;
            default:
                throw new InvalidOperationException($"LAME VBR: unknown alloc {that.Alloc}.");
        }
    }

    /// <summary><c>bitcount</c>.</summary>
    public static void Bitcount(ref VbrAlgo that)
    {
        int rc = LameTakehiro.ScaleBitcount(that.Gfc, ref that.CodInfo);
        if (rc == 0) return;

        // The C prints and calls exit(-1). The scalefactors were chosen so that this cannot
        // happen, so if it does the state is already wrong and there is nothing to salvage.
        throw new InvalidOperationException(
            "LAME VBR: scale_bitcount rejected the scalefactors chosen by the VBR search "
          + $"(returned {rc}). This is the C's 'INTERNAL ERROR IN VBR NEW CODE (986)'.");
    }

    /// <summary><c>quantizeAndCountBits</c>.</summary>
    public static int QuantizeAndCountBits(ref VbrAlgo that, ReadOnlySpan<float> xr34Orig)
    {
        QuantizeX34(ref that, xr34Orig);
        that.CodInfo.Part23Length = LameTakehiro.NoquantCountBits(that.Gfc, ref that.CodInfo, null);
        return that.CodInfo.Part23Length;
    }

    /// <summary><c>tryGlobalStepsize</c>.</summary>
    public static int TryGlobalStepsize(
        ref VbrAlgo that, ReadOnlySpan<int> sfwork, ReadOnlySpan<int> vbrsfmin, int delta,
        ReadOnlySpan<float> xr34Orig)
    {
        float xrpowMax = that.CodInfo.XrpowMax;
        IntSfb sftempStore = default;
        Span<int> sftemp = sftempStore;
        int vbrmax = 0;

        for (int i = 0; i < LameConstants.SfbMax; ++i)
        {
            int gain = sfwork[i] + delta;
            if (gain < vbrsfmin[i]) gain = vbrsfmin[i];
            if (gain > 255) gain = 255;
            if (vbrmax < gain) vbrmax = gain;
            sftemp[i] = gain;
        }
        Alloc(ref that, sftemp, vbrsfmin, vbrmax);
        Bitcount(ref that);
        int nbits = QuantizeAndCountBits(ref that, xr34Orig);
        that.CodInfo.XrpowMax = xrpowMax;
        return nbits;
    }

    /// <summary><c>searchGlobalStepsizeMax</c>.</summary>
    public static void SearchGlobalStepsizeMax(
        ref VbrAlgo that, ReadOnlySpan<int> sfwork, ReadOnlySpan<int> vbrsfmin, int target,
        ReadOnlySpan<float> xr34Orig)
    {
        int gain = that.CodInfo.GlobalGain;
        int curr = gain;
        int gainOk = 1024;
        int l = gain, r = 512;

        while (l <= r)
        {
            curr = (l + r) >> 1;
            int nbits = TryGlobalStepsize(ref that, sfwork, vbrsfmin, curr - gain, xr34Orig);
            if (nbits == 0 || nbits + that.CodInfo.Part2Length < target)
            {
                r = curr - 1;
                gainOk = curr;
            }
            else
            {
                l = curr + 1;
                if (gainOk == 1024) gainOk = curr;
            }
        }
        if (gainOk != curr)
        {
            curr = gainOk;
            TryGlobalStepsize(ref that, sfwork, vbrsfmin, curr - gain, xr34Orig);
        }
    }

    /// <summary><c>sfDepth</c> - how far the lowest scalefactor is from the ceiling.</summary>
    public static int SfDepth(ReadOnlySpan<int> sfwork)
    {
        int m = 0;
        for (int i = 0; i < LameConstants.SfbMax; ++i)
        {
            int di = 255 - sfwork[i];
            if (m < di) m = di;
        }
        return m;
    }

    /// <summary><c>cutDistribution</c>.</summary>
    public static void CutDistribution(ReadOnlySpan<int> sfwork, Span<int> sfOut, int cut)
    {
        for (int i = 0; i < LameConstants.SfbMax; ++i)
        {
            int x = sfwork[i];
            sfOut[i] = x < cut ? x : cut;
        }
    }

    /// <summary><c>flattenDistribution</c>.</summary>
    public static int FlattenDistribution(
        ReadOnlySpan<int> sfwork, Span<int> sfOut, int dm, int k, int p)
    {
        int sfmax = 0;
        if (dm > 0)
        {
            for (int i = 0; i < LameConstants.SfbMax; ++i)
            {
                int di = p - sfwork[i];
                int x = sfwork[i] + k * di / dm;
                if (x < 0) x = 0;
                else if (x > 255) x = 255;
                sfOut[i] = x;
                if (sfmax < x) sfmax = x;
            }
        }
        else
        {
            for (int i = 0; i < LameConstants.SfbMax; ++i)
            {
                int x = sfwork[i];
                sfOut[i] = x;
                if (sfmax < x) sfmax = x;
            }
        }
        return sfmax;
    }

    /// <summary><c>tryThatOne</c>.</summary>
    public static int TryThatOne(
        ref VbrAlgo that, ReadOnlySpan<int> sftemp, ReadOnlySpan<int> vbrsfmin, int vbrmax,
        ReadOnlySpan<float> xr34Orig)
    {
        float xrpowMax = that.CodInfo.XrpowMax;
        Alloc(ref that, sftemp, vbrsfmin, vbrmax);
        Bitcount(ref that);
        int nbits = QuantizeAndCountBits(ref that, xr34Orig);
        nbits += that.CodInfo.Part2Length;
        that.CodInfo.XrpowMax = xrpowMax;
        return nbits;
    }

    /// <summary><c>outOfBitsStrategy</c> - flatten the distribution until the granule fits.</summary>
    public static void OutOfBitsStrategy(
        ref VbrAlgo that, ReadOnlySpan<int> sfwork, ReadOnlySpan<int> vbrsfmin, int target,
        ReadOnlySpan<float> xr34Orig)
    {
        IntSfb wrkStore = default;
        Span<int> wrk = wrkStore;
        int dm = SfDepth(sfwork);
        int p = that.CodInfo.GlobalGain;

        // PART 1
        {
            int bi = dm / 2;
            int biOk = -1;
            int bu = 0;
            int bo = dm;
            for (; ; )
            {
                int sfmax = FlattenDistribution(sfwork, wrk, dm, bi, p);
                int nbits = TryThatOne(ref that, wrk, vbrsfmin, sfmax, xr34Orig);
                if (nbits <= target) { biOk = bi; bo = bi - 1; }
                else { bu = bi + 1; }
                if (bu <= bo) bi = (bu + bo) / 2;
                else break;
            }
            if (biOk >= 0)
            {
                if (bi != biOk)
                {
                    int sfmax = FlattenDistribution(sfwork, wrk, dm, biOk, p);
                    TryThatOne(ref that, wrk, vbrsfmin, sfmax, xr34Orig);
                }
                return;
            }
        }

        // PART 2
        {
            int bi = (255 + p) / 2;
            int biOk = -1;
            int bu = p;
            int bo = 255;
            for (; ; )
            {
                int sfmax = FlattenDistribution(sfwork, wrk, dm, dm, bi);
                int nbits = TryThatOne(ref that, wrk, vbrsfmin, sfmax, xr34Orig);
                if (nbits <= target) { biOk = bi; bo = bi - 1; }
                else { bu = bi + 1; }
                if (bu <= bo) bi = (bu + bo) / 2;
                else break;
            }
            if (biOk >= 0)
            {
                if (bi != biOk)
                {
                    int sfmax = FlattenDistribution(sfwork, wrk, dm, dm, biOk);
                    TryThatOne(ref that, wrk, vbrsfmin, sfmax, xr34Orig);
                }
                return;
            }
        }

        // fall back to the old code; the C notes this is likely never called
        SearchGlobalStepsizeMax(ref that, wrk, vbrsfmin, target, xr34Orig);
    }

    /// <summary><c>reduce_bit_usage</c>.</summary>
    public static int ReduceBitUsage(LameInternalFlags gfc, int gr, int ch)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref GrInfo codInfo = ref gfc.L3Side.Tt[gr * 2 + ch];

        // try some better scalefac storage
        LameTakehiro.BestScalefacStore(gfc, gr, ch, ref gfc.L3Side);

        // best_huffman_divide may save some bits too
        if (cfg.UseBestHuffman == 1)
            LameTakehiro.BestHuffmanDivide(gfc, ref codInfo);

        return codInfo.Part23Length + codInfo.Part2Length;
    }

    /// <summary><c>VBR_encode_frame</c>.</summary>
    /// <param name="xr34orig"><c>FLOAT xr34orig[2][2][576]</c>, flat, granule-major.</param>
    /// <param name="l3Xmin"><c>FLOAT l3_xmin[2][2][SFBMAX]</c>, flat, granule-major.</param>
    /// <param name="maxBits"><c>int max_bits[2][2]</c>, flat, granule-major.</param>
    /// <param name="scratch">The C's <c>sfwork_</c>, <c>vbrsfmin_</c> and <c>that_</c>.</param>
    public static int VbrEncodeFrame(
        LameInternalFlags gfc, float[] xr34orig, float[] l3Xmin, ReadOnlySpan<int> maxBits,
        LameVbrScratch scratch)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        int ngr = cfg.ModeGr;
        int nch = cfg.ChannelsOut;

        int[] sfwork = scratch.Sfwork;
        int[] vbrsfmin = scratch.Vbrsfmin;
        VbrAlgo[] that = scratch.That;

        Span<int> maxNbitsCh = stackalloc int[4];
        Span<int> maxNbitsGr = stackalloc int[2];
        Span<int> useNbitsCh = stackalloc int[4];
        Span<int> useNbitsGr = stackalloc int[2];
        int maxNbitsFr = 0;
        int useNbitsFr;
        int gr, ch;
        bool ok;
        int sumFr;

        maxNbitsCh.Clear();
        maxNbitsGr.Clear();
        useNbitsCh.Clear();
        useNbitsGr.Clear();

        // set up some encoding parameters
        for (gr = 0; gr < ngr; ++gr)
        {
            maxNbitsGr[gr] = 0;
            for (ch = 0; ch < nch; ++ch)
            {
                int k = gr * 2 + ch;
                maxNbitsCh[k] = maxBits[k];
                useNbitsCh[k] = 0;
                maxNbitsGr[gr] += maxBits[k];
                maxNbitsFr += maxBits[k];

                ref VbrAlgo a = ref that[k];
                a.Find = cfg.FullOuterLoop < 0 ? VbrFind.Guess : VbrFind.Search;
                a.Gfc = gfc;
                a.Gr = gr;
                a.Ch = ch;
                a.Xr34Base = k * 576;
                a.Alloc = gfc.L3Side.Tt[k].BlockType == LameBlockType.Short
                    ? VbrAlloc.ShortBlock
                    : VbrAlloc.LongBlock;
            }
        }

        // search scalefactors
        for (gr = 0; gr < ngr; ++gr)
        {
            for (ch = 0; ch < nch; ++ch)
            {
                int k = gr * 2 + ch;
                if (maxBits[k] <= 0) continue;   // xr has no energy; l3_enc stays zero

                ref VbrAlgo a = ref that[k];
                Span<int> sf = sfwork.AsSpan(k * LameConstants.SfbMax, LameConstants.SfbMax);
                Span<int> sfmin = vbrsfmin.AsSpan(k * LameConstants.SfbMax, LameConstants.SfbMax);

                int vbrmax = BlockSf(
                    ref a,
                    l3Xmin.AsSpan(k * LameConstants.SfbMax, LameConstants.SfbMax),
                    sf, sfmin,
                    xr34orig.AsSpan(a.Xr34Base, 576));

                Alloc(ref a, sf, sfmin, vbrmax);
                Bitcount(ref a);
            }
        }

        // encode 'as is'
        useNbitsFr = 0;
        for (gr = 0; gr < ngr; ++gr)
        {
            useNbitsGr[gr] = 0;
            for (ch = 0; ch < nch; ++ch)
            {
                int k = gr * 2 + ch;
                ref VbrAlgo a = ref that[k];
                if (maxBits[k] > 0)
                {
                    ((Span<int>)a.CodInfo.L3Enc).Clear();
                    QuantizeAndCountBits(ref a, xr34orig.AsSpan(a.Xr34Base, 576));
                }
                useNbitsCh[k] = ReduceBitUsage(gfc, gr, ch);
                useNbitsGr[gr] += useNbitsCh[k];
            }
            useNbitsFr += useNbitsGr[gr];
        }

        // check bit constraints
        if (useNbitsFr <= maxNbitsFr)
        {
            ok = true;
            for (gr = 0; gr < ngr; ++gr)
            {
                // every granule must use no more than MAX_BITS_PER_GRANULE
                if (useNbitsGr[gr] > LameQuantizeConstants.MaxBitsPerGranule) ok = false;

                for (ch = 0; ch < nch; ++ch)
                {
                    // and every granule/channel no more than MAX_BITS_PER_CHANNEL. The ISO
                    // docs do not say so outright, but part2_3_length has only 12 bits.
                    if (useNbitsCh[gr * 2 + ch] > LameQuantizeConstants.MaxBitsPerChannel)
                        ok = false;
                }
            }
            if (ok) return useNbitsFr;
        }

        // We are in trouble, and have to decide how many bits each granule gets.
        {
            ok = true;
            sumFr = 0;

            for (gr = 0; gr < ngr; ++gr)
            {
                maxNbitsGr[gr] = 0;
                for (ch = 0; ch < nch; ++ch)
                {
                    int k = gr * 2 + ch;
                    maxNbitsCh[k] = useNbitsCh[k] > LameQuantizeConstants.MaxBitsPerChannel
                        ? LameQuantizeConstants.MaxBitsPerChannel
                        : useNbitsCh[k];
                    maxNbitsGr[gr] += maxNbitsCh[k];
                }
                if (maxNbitsGr[gr] > LameQuantizeConstants.MaxBitsPerGranule)
                {
                    Span<float> f = stackalloc float[2] { 0.0f, 0.0f };
                    float s = 0.0f;
                    for (ch = 0; ch < nch; ++ch)
                    {
                        if (maxNbitsCh[gr * 2 + ch] > 0)
                        {
                            f[ch] = (float)Math.Sqrt(Math.Sqrt(maxNbitsCh[gr * 2 + ch]));
                            s += f[ch];
                        }
                        else
                        {
                            f[ch] = 0;
                        }
                    }
                    for (ch = 0; ch < nch; ++ch)
                        maxNbitsCh[gr * 2 + ch] = s > 0
                            ? (int)(LameQuantizeConstants.MaxBitsPerGranule * f[ch] / s)
                            : 0;

                    if (nch > 1)
                    {
                        if (maxNbitsCh[gr * 2 + 0] > useNbitsCh[gr * 2 + 0] + 32)
                        {
                            maxNbitsCh[gr * 2 + 1] += maxNbitsCh[gr * 2 + 0];
                            maxNbitsCh[gr * 2 + 1] -= useNbitsCh[gr * 2 + 0] + 32;
                            maxNbitsCh[gr * 2 + 0] = useNbitsCh[gr * 2 + 0] + 32;
                        }
                        if (maxNbitsCh[gr * 2 + 1] > useNbitsCh[gr * 2 + 1] + 32)
                        {
                            maxNbitsCh[gr * 2 + 0] += maxNbitsCh[gr * 2 + 1];
                            maxNbitsCh[gr * 2 + 0] -= useNbitsCh[gr * 2 + 1] + 32;
                            maxNbitsCh[gr * 2 + 1] = useNbitsCh[gr * 2 + 1] + 32;
                        }
                        if (maxNbitsCh[gr * 2 + 0] > LameQuantizeConstants.MaxBitsPerChannel)
                            maxNbitsCh[gr * 2 + 0] = LameQuantizeConstants.MaxBitsPerChannel;
                        if (maxNbitsCh[gr * 2 + 1] > LameQuantizeConstants.MaxBitsPerChannel)
                            maxNbitsCh[gr * 2 + 1] = LameQuantizeConstants.MaxBitsPerChannel;
                    }
                    maxNbitsGr[gr] = 0;
                    for (ch = 0; ch < nch; ++ch)
                        maxNbitsGr[gr] += maxNbitsCh[gr * 2 + ch];
                }
                sumFr += maxNbitsGr[gr];
            }

            if (sumFr > maxNbitsFr)
            {
                {
                    Span<float> f = stackalloc float[2] { 0.0f, 0.0f };
                    float s = 0.0f;
                    for (gr = 0; gr < ngr; ++gr)
                    {
                        if (maxNbitsGr[gr] > 0)
                        {
                            f[gr] = (float)Math.Sqrt(maxNbitsGr[gr]);
                            s += f[gr];
                        }
                        else
                        {
                            f[gr] = 0;
                        }
                    }
                    for (gr = 0; gr < ngr; ++gr)
                        maxNbitsGr[gr] = s > 0 ? (int)(maxNbitsFr * f[gr] / s) : 0;
                }
                if (ngr > 1)
                {
                    if (maxNbitsGr[0] > useNbitsGr[0] + 125)
                    {
                        maxNbitsGr[1] += maxNbitsGr[0];
                        maxNbitsGr[1] -= useNbitsGr[0] + 125;
                        maxNbitsGr[0] = useNbitsGr[0] + 125;
                    }
                    if (maxNbitsGr[1] > useNbitsGr[1] + 125)
                    {
                        maxNbitsGr[0] += maxNbitsGr[1];
                        maxNbitsGr[0] -= useNbitsGr[1] + 125;
                        maxNbitsGr[1] = useNbitsGr[1] + 125;
                    }
                    for (gr = 0; gr < ngr; ++gr)
                        if (maxNbitsGr[gr] > LameQuantizeConstants.MaxBitsPerGranule)
                            maxNbitsGr[gr] = LameQuantizeConstants.MaxBitsPerGranule;
                }
                for (gr = 0; gr < ngr; ++gr)
                {
                    Span<float> f = stackalloc float[2] { 0.0f, 0.0f };
                    float s = 0.0f;
                    for (ch = 0; ch < nch; ++ch)
                    {
                        if (maxNbitsCh[gr * 2 + ch] > 0)
                        {
                            f[ch] = (float)Math.Sqrt(maxNbitsCh[gr * 2 + ch]);
                            s += f[ch];
                        }
                        else
                        {
                            f[ch] = 0;
                        }
                    }
                    for (ch = 0; ch < nch; ++ch)
                        maxNbitsCh[gr * 2 + ch] = s > 0
                            ? (int)(maxNbitsGr[gr] * f[ch] / s)
                            : 0;

                    if (nch > 1)
                    {
                        if (maxNbitsCh[gr * 2 + 0] > useNbitsCh[gr * 2 + 0] + 32)
                        {
                            maxNbitsCh[gr * 2 + 1] += maxNbitsCh[gr * 2 + 0];
                            maxNbitsCh[gr * 2 + 1] -= useNbitsCh[gr * 2 + 0] + 32;
                            maxNbitsCh[gr * 2 + 0] = useNbitsCh[gr * 2 + 0] + 32;
                        }
                        if (maxNbitsCh[gr * 2 + 1] > useNbitsCh[gr * 2 + 1] + 32)
                        {
                            maxNbitsCh[gr * 2 + 0] += maxNbitsCh[gr * 2 + 1];
                            maxNbitsCh[gr * 2 + 0] -= useNbitsCh[gr * 2 + 1] + 32;
                            maxNbitsCh[gr * 2 + 1] = useNbitsCh[gr * 2 + 1] + 32;
                        }
                        for (ch = 0; ch < nch; ++ch)
                            if (maxNbitsCh[gr * 2 + ch] > LameQuantizeConstants.MaxBitsPerChannel)
                                maxNbitsCh[gr * 2 + ch] = LameQuantizeConstants.MaxBitsPerChannel;
                    }
                }
            }

            // sanity check
            sumFr = 0;
            for (gr = 0; gr < ngr; ++gr)
            {
                int sumGr = 0;
                for (ch = 0; ch < nch; ++ch)
                {
                    sumGr += maxNbitsCh[gr * 2 + ch];
                    if (maxNbitsCh[gr * 2 + ch] > LameQuantizeConstants.MaxBitsPerChannel)
                        ok = false;
                }
                sumFr += sumGr;
                if (sumGr > LameQuantizeConstants.MaxBitsPerGranule) ok = false;
            }
            if (sumFr > maxNbitsFr) ok = false;

            if (!ok)
            {
                // we must have done something wrong; fall back to the 'on_pe' constraint
                for (gr = 0; gr < ngr; ++gr)
                    for (ch = 0; ch < nch; ++ch)
                        maxNbitsCh[gr * 2 + ch] = maxBits[gr * 2 + ch];
            }
        }

        // best_scalefac_store has already run, so some state has to be reset before it can
        // run again.
        for (ch = 0; ch < nch; ++ch)
        {
            gfc.L3Side.Scfsi[ch * 4 + 0] = 0;
            gfc.L3Side.Scfsi[ch * 4 + 1] = 0;
            gfc.L3Side.Scfsi[ch * 4 + 2] = 0;
            gfc.L3Side.Scfsi[ch * 4 + 3] = 0;
        }
        for (gr = 0; gr < ngr; ++gr)
            for (ch = 0; ch < nch; ++ch)
                gfc.L3Side.Tt[gr * 2 + ch].ScalefacCompress = 0;

        // alter the encoded data until it fits the target bitrate
        useNbitsFr = 0;
        for (gr = 0; gr < ngr; ++gr)
        {
            useNbitsGr[gr] = 0;
            for (ch = 0; ch < nch; ++ch)
            {
                int k = gr * 2 + ch;
                ref VbrAlgo a = ref that[k];
                useNbitsCh[k] = 0;
                if (maxBits[k] > 0)
                {
                    Span<int> sf = sfwork.AsSpan(k * LameConstants.SfbMax, LameConstants.SfbMax);
                    ReadOnlySpan<int> sfmin =
                        vbrsfmin.AsSpan(k * LameConstants.SfbMax, LameConstants.SfbMax);
                    CutDistribution(sf, sf, a.CodInfo.GlobalGain);
                    OutOfBitsStrategy(
                        ref a, sf, sfmin, maxNbitsCh[k], xr34orig.AsSpan(a.Xr34Base, 576));
                }
                useNbitsCh[k] = ReduceBitUsage(gfc, gr, ch);
                useNbitsGr[gr] += useNbitsCh[k];
            }
            useNbitsFr += useNbitsGr[gr];
        }

        if (useNbitsFr <= maxNbitsFr) return useNbitsFr;

        throw new InvalidOperationException(
            "LAME VBR: the frame still does not fit after outOfBitsStrategy; "
          + $"maxbits {maxNbitsFr}, usedbits {useNbitsFr}. This is the C's 'INTERNAL ERROR IN "
          + "VBR NEW CODE (1313)'.");
    }
}

/// <summary>The C stack arrays of <c>VBR_encode_frame</c>, hoisted out.</summary>
internal sealed class LameVbrScratch
{
    /// <summary><c>int sfwork_[2][2][SFBMAX]</c>, flat, granule-major.</summary>
    public readonly int[] Sfwork = new int[4 * LameConstants.SfbMax];

    /// <summary><c>int vbrsfmin_[2][2][SFBMAX]</c>, flat, granule-major.</summary>
    public readonly int[] Vbrsfmin = new int[4 * LameConstants.SfbMax];

    /// <summary><c>algo_t that_[2][2]</c>, flat, granule-major.</summary>
    public readonly VbrAlgo[] That = new VbrAlgo[4];
}

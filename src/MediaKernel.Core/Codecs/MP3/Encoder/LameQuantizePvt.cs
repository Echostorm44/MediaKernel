// Translated from lame-3.100/libmp3lame/quantize_pvt.c - the ATH in MDCT units, the
// precomputed power tables, bit allocation across the two channels, the allowed distortion per
// scalefactor band, and the noise measurement the quantisation loop minimises.
//
// set_pinfo and set_frame_pinfo are recorded as diverged: both exist only to fill
// plotting_data for LAME's frame analyser, which libmp3lame.c never enables.
//
// THE GLOBAL TABLES. pow43, adj43asm, pow20 and ipow20 are file-scope globals in the C, filled
// once by the first encoder's iteration_init and shared by every encoder in the process. They
// depend on nothing but constants, so they are static readonly here and built by a static
// initialiser - the same once-only fill, without the C's guard flag and without a race.
//
// TAKEHIRO_IEEE754_HACK is defined by configure for x86_64, so the table built is adj43asm and
// not adj43; the two are different functions, not two spellings of one. See LameUtil, where the
// same configure branch decides USE_FAST_LOG.
//
// PRECISION. pow43, ipow20 and pow20 are filled by pow() in double and rounded once on store
// into a FLOAT array; written with MathF they would differ. The dB-to-energy conversions inside
// iteration_init are powf in the C and stay float here.

using System;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>quantize_pvt.h</c>'s constants.</summary>
internal static class LameQuantizeConstants
{
    /// <summary><c>IXMAX_VAL</c> - <c>ix</c> is always at most 8191 + 15; see count_bits.</summary>
    public const int IxmaxVal = 8206;

    /// <summary><c>PRECALC_SIZE</c>.</summary>
    public const int PrecalcSize = IxmaxVal + 2;

    /// <summary><c>Q_MAX</c>.</summary>
    public const int QMax = 256 + 1;

    /// <summary>
    /// <c>Q_MAX2</c> - the smallest possible value of
    /// <c>-global_gain + ((scalefac + pretab) &lt;&lt; (scalefac_scale + 1)) + subblock_gain * 8</c>:
    /// 72 for a long block, 116 for a short one.
    /// </summary>
    public const int QMax2 = 116;

    /// <summary><c>LARGE_BITS</c>.</summary>
    public const int LargeBits = 100000;

    /// <summary><c>NSATHSCALE</c> - assuming 96 dB of dynamic range, this should be 92.</summary>
    public const int NsAthScale = 100;

    /// <summary><c>MAX_BITS_PER_CHANNEL</c> (<c>util.h</c>).</summary>
    public const int MaxBitsPerChannel = 4095;

    /// <summary><c>MAX_BITS_PER_GRANULE</c> (<c>util.h</c>).</summary>
    public const int MaxBitsPerGranule = 7680;

    /// <summary>
    /// <c>TAKEHIRO_IEEE754_HACK</c> - defined by configure for x86_64, amd64, *86 and powerpc.
    /// </summary>
    public const bool TakehiroIeee754Hack = true;

    /// <summary>
    /// <c>HAVE_XMMINTRIN_H</c> and <c>CPU_features.SSE</c> together, which is what
    /// <c>init_xrpow_core_init</c> tests before it points <c>gfc-&gt;init_xrpow_core</c> at
    /// <c>init_xrpow_core_sse</c>.
    /// </summary>
    /// <remarks>
    /// Every x86-64 CPU has SSE, so on the platform this port targets the reference build
    /// always takes the vector path, and the two paths are not interchangeable: the C computes
    /// <c>sqrt(tmp * sqrt(tmp))</c> in double and rounds once at the store, while the vector
    /// code does both square roots and the multiply in float. They agree to the last bit almost
    /// everywhere, which is exactly why this was expensive to find - one coefficient in 41,796
    /// bytes of 320 kbps mono came out 1169 against libmp3lame's 1168, because the float chain
    /// gives a product of exactly 1168.5 where the double chain gives one ulp more, and the
    /// quantiser's round-half-to-even then goes the other way.
    /// </remarks>
    public const bool HaveSseXrpow = true;
}

/// <summary><c>calc_noise_result</c> - what <c>calc_noise</c> reports about a granule.</summary>
internal struct CalcNoiseResult
{
    /// <summary><c>over_noise</c> - the sum of quantisation noise above the masking threshold.</summary>
    public float OverNoise;

    /// <summary><c>tot_noise</c> - the sum of all quantisation noise.</summary>
    public float TotNoise;

    /// <summary><c>max_noise</c>.</summary>
    public float MaxNoise;

    /// <summary><c>over_count</c> - how many bands are above their threshold.</summary>
    public int OverCount;

    /// <summary><c>over_SSD</c> - an SSD-like cost of the distorted bands.</summary>
    public int OverSsd;

    /// <summary><c>bits</c>.</summary>
    public int Bits;
}

/// <summary><c>calc_noise_data</c> - lets <c>calc_noise</c> reuse what it computed last time.</summary>
internal sealed class CalcNoiseData
{
    /// <summary><c>global_gain</c>.</summary>
    public int GlobalGain;

    /// <summary><c>sfb_count1</c>.</summary>
    public int SfbCount1;

    /// <summary><c>step[39]</c>.</summary>
    public readonly int[] Step = new int[39];

    /// <summary><c>noise[39]</c>.</summary>
    public readonly float[] Noise = new float[39];

    /// <summary><c>noise_log[39]</c>.</summary>
    public readonly float[] NoiseLog = new float[39];
}

/// <summary><c>quantize_pvt.c</c>.</summary>
internal static class LameQuantizePvt
{
    /// <summary>
    /// <c>nr_of_sfb_block[6][3][4]</c> - the MPEG-2 scalefactor partitioning of IS 2.4.3.2,
    /// indexed <c>[table_number][row_in_table][column of nr_of_sfb]</c>.
    /// </summary>
    public static readonly int[][][] NrOfSfbBlock =
    [
        [[6, 5, 5, 5], [9, 9, 9, 9], [6, 9, 9, 9]],
        [[6, 5, 7, 3], [9, 9, 12, 6], [6, 9, 12, 6]],
        [[11, 10, 0, 0], [18, 18, 0, 0], [15, 18, 0, 0]],
        [[7, 7, 7, 0], [12, 12, 12, 0], [6, 15, 12, 0]],
        [[6, 6, 6, 3], [12, 9, 9, 6], [6, 12, 9, 6]],
        [[8, 8, 5, 0], [15, 12, 9, 0], [6, 18, 9, 0]],
    ];

    /// <summary><c>pretab[SBMAX_l]</c> - Table B.6, layer III preemphasis.</summary>
    public static readonly int[] Pretab =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 3, 2, 0,
    ];

    /// <summary><c>payload_long[2][4]</c>.</summary>
    private static readonly float[][] payloadLong =
    [
        [-0.000f, -0.000f, -0.000f, +0.000f],
        [-0.500f, -0.250f, -0.025f, +0.500f],
    ];

    /// <summary><c>payload_short[2][4]</c>.</summary>
    private static readonly float[][] payloadShort =
    [
        [-0.000f, -0.000f, -0.000f, +0.000f],
        [-2.000f, -1.000f, -0.050f, +0.500f],
    ];

    /// <summary><c>pow43[PRECALC_SIZE]</c>.</summary>
    public static readonly float[] Pow43 = BuildPow43();

    /// <summary>
    /// <c>adj43asm[PRECALC_SIZE]</c> under TAKEHIRO_IEEE754_HACK, <c>adj43</c> otherwise.
    /// </summary>
    /// <remarks>
    /// The two are different functions, not two spellings of one: <c>adj43asm[i]</c> is
    /// <c>i - 0.5 - pow(0.5 * (pow43[i-1] + pow43[i]), 0.75)</c> while <c>adj43[i]</c> is
    /// <c>(i + 1) - pow(0.5 * (pow43[i] + pow43[i+1]), 0.75)</c> with a 0.5 at the end. Which
    /// one exists is decided by the same configure branch that decides USE_FAST_LOG.
    /// </remarks>
    public static readonly float[] Adj43 = BuildAdj43();

    /// <summary><c>ipow20[Q_MAX]</c>.</summary>
    public static readonly float[] Ipow20 = BuildIpow20();

    /// <summary><c>pow20[Q_MAX + Q_MAX2 + 1]</c>.</summary>
    public static readonly float[] Pow20 = BuildPow20();

    private static float[] BuildPow43()
    {
        var t = new float[LameQuantizeConstants.PrecalcSize];
        t[0] = 0.0f;
        for (int i = 1; i < LameQuantizeConstants.PrecalcSize; i++)
            t[i] = (float)Math.Pow(i, 4.0 / 3.0);
        return t;
    }

    private static float[] BuildAdj43()
    {
        var t = new float[LameQuantizeConstants.PrecalcSize];
        if (LameQuantizeConstants.TakehiroIeee754Hack)
        {
            t[0] = 0.0f;
            for (int i = 1; i < LameQuantizeConstants.PrecalcSize; i++)
                t[i] = (float)(i - 0.5 - Math.Pow(0.5 * (Pow43[i - 1] + Pow43[i]), 0.75));
        }
        else
        {
            int i;
            for (i = 0; i < LameQuantizeConstants.PrecalcSize - 1; i++)
                t[i] = (float)((i + 1) - Math.Pow(0.5 * (Pow43[i] + Pow43[i + 1]), 0.75));
            t[i] = 0.5f;
        }
        return t;
    }

    private static float[] BuildIpow20()
    {
        var t = new float[LameQuantizeConstants.QMax];
        for (int i = 0; i < LameQuantizeConstants.QMax; i++)
            t[i] = (float)Math.Pow(2.0, (i - 210) * -0.1875);
        return t;
    }

    private static float[] BuildPow20()
    {
        var t = new float[LameQuantizeConstants.QMax + LameQuantizeConstants.QMax2 + 1];
        for (int i = 0; i <= LameQuantizeConstants.QMax + LameQuantizeConstants.QMax2; i++)
            t[i] = (float)Math.Pow(2.0, (i - 210 - LameQuantizeConstants.QMax2) * 0.25);
        return t;
    }

    /// <summary><c>POW20(x)</c> - <c>machine.h</c>.</summary>
    public static float Pow20Of(int x) => Pow20[x + LameQuantizeConstants.QMax2];

    /// <summary><c>IPOW20(x)</c> - <c>machine.h</c>.</summary>
    public static float Ipow20Of(int x) => Ipow20[x];

    /// <summary>
    /// <c>ATHmdct</c> - the absolute threshold of hearing in the MDCT's energy units.
    /// </summary>
    private static float AthMdct(ref SessionConfig cfg, float f)
    {
        float ath = LameUtil.AthFormula(ref cfg, f);

        if (cfg.AthFixpoint > 0)
            ath -= cfg.AthFixpoint;
        else
            ath -= LameQuantizeConstants.NsAthScale;

        ath += cfg.AthOffsetDb;

        // modify the MDCT scaling for the ATH and convert to energy
        return MathF.Pow(10.0f, ath * 0.1f);
    }

    /// <summary><c>compute_ath</c> - the ATH per scalefactor band.</summary>
    private static void ComputeAth(LameInternalFlags gfc)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        float[] athL = gfc.Ath.L!;
        float[] athPsfb21 = gfc.Ath.Psfb21!;
        float[] athS = gfc.Ath.S!;
        float[] athPsfb12 = gfc.Ath.Psfb12!;
        int sfb, i, start, end;
        float athF;
        float sampFreq = cfg.SamplerateOut;

        for (sfb = 0; sfb < LameConstants.SbMaxL; sfb++)
        {
            start = gfc.ScalefacBand.L[sfb];
            end = gfc.ScalefacBand.L[sfb + 1];
            athL[sfb] = float.MaxValue;
            for (i = start; i < end; i++)
            {
                float freq = i * sampFreq / (2 * 576);
                athF = AthMdct(ref cfg, freq);
                athL[sfb] = Math.Min(athL[sfb], athF);
            }
        }

        for (sfb = 0; sfb < LameConstants.Psfb21; sfb++)
        {
            start = gfc.ScalefacBand.Psfb21[sfb];
            end = gfc.ScalefacBand.Psfb21[sfb + 1];
            athPsfb21[sfb] = float.MaxValue;
            for (i = start; i < end; i++)
            {
                float freq = i * sampFreq / (2 * 576);
                athF = AthMdct(ref cfg, freq);
                athPsfb21[sfb] = Math.Min(athPsfb21[sfb], athF);
            }
        }

        for (sfb = 0; sfb < LameConstants.SbMaxS; sfb++)
        {
            start = gfc.ScalefacBand.S[sfb];
            end = gfc.ScalefacBand.S[sfb + 1];
            athS[sfb] = float.MaxValue;
            for (i = start; i < end; i++)
            {
                float freq = i * sampFreq / (2 * 192);
                athF = AthMdct(ref cfg, freq);
                athS[sfb] = Math.Min(athS[sfb], athF);
            }
            athS[sfb] *= gfc.ScalefacBand.S[sfb + 1] - gfc.ScalefacBand.S[sfb];
        }

        for (sfb = 0; sfb < LameConstants.Psfb12; sfb++)
        {
            start = gfc.ScalefacBand.Psfb12[sfb];
            end = gfc.ScalefacBand.Psfb12[sfb + 1];
            athPsfb12[sfb] = float.MaxValue;
            for (i = start; i < end; i++)
            {
                float freq = i * sampFreq / (2 * 192);
                athF = AthMdct(ref cfg, freq);
                athPsfb12[sfb] = Math.Min(athPsfb12[sfb], athF);
            }
            // the C is not sure about the following either
            athPsfb12[sfb] *= gfc.ScalefacBand.S[13] - gfc.ScalefacBand.S[12];
        }

        // no-ATH mode: reduce the ATH to -200 dB
        if (cfg.NoAth != 0)
        {
            for (sfb = 0; sfb < LameConstants.SbMaxL; sfb++) athL[sfb] = 1E-20f;
            for (sfb = 0; sfb < LameConstants.Psfb21; sfb++) athPsfb21[sfb] = 1E-20f;
            for (sfb = 0; sfb < LameConstants.SbMaxS; sfb++) athS[sfb] = 1E-20f;
            for (sfb = 0; sfb < LameConstants.Psfb12; sfb++) athPsfb12[sfb] = 1E-20f;
        }

        // work in progress, the C says; don't rely on it too much
        gfc.Ath.Floor = (float)(10.0 * Math.Log10(AthMdct(ref cfg, -1.0f)));
    }

    /// <summary><c>iteration_init</c> - initialisation for <c>iteration_loop</c>.</summary>
    /// <remarks>
    /// The four power tables the C fills here are process-wide globals and are static readonly
    /// in this port; see the file header. <c>init_xrpow_core_init</c> only chose between a C and
    /// an SSE kernel through a function pointer and is recorded as diverged.
    /// </remarks>
    public static void IterationInit(LameInternalFlags gfc)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref IiiSideInfo l3Side = ref gfc.L3Side;
        float adjust, db;
        int i, sel;

        if (gfc.IterationInitInit == 0)
        {
            gfc.IterationInitInit = 1;

            l3Side.MainDataBegin = 0;
            ComputeAth(gfc);

            // pow43, adj43, ipow20 and pow20 are built by their static initialisers.

            LameTakehiro.HuffmanInit(gfc);

            // RH: all modes behave like vbr-new here, so this is not read from cfg->vbr.
            sel = 1;

            // long
            db = cfg.AdjustBassDb + payloadLong[sel][0];
            adjust = MathF.Pow(10.0f, db * 0.1f);
            for (i = 0; i <= 6; ++i) gfc.SvQnt.Longfact![i] = adjust;

            db = cfg.AdjustAltoDb + payloadLong[sel][1];
            adjust = MathF.Pow(10.0f, db * 0.1f);
            for (; i <= 13; ++i) gfc.SvQnt.Longfact![i] = adjust;

            db = cfg.AdjustTrebleDb + payloadLong[sel][2];
            adjust = MathF.Pow(10.0f, db * 0.1f);
            for (; i <= 20; ++i) gfc.SvQnt.Longfact![i] = adjust;

            db = cfg.AdjustSfb21Db + payloadLong[sel][3];
            adjust = MathF.Pow(10.0f, db * 0.1f);
            for (; i < LameConstants.SbMaxL; ++i) gfc.SvQnt.Longfact![i] = adjust;

            // short
            db = cfg.AdjustBassDb + payloadShort[sel][0];
            adjust = MathF.Pow(10.0f, db * 0.1f);
            for (i = 0; i <= 2; ++i) gfc.SvQnt.Shortfact![i] = adjust;

            db = cfg.AdjustAltoDb + payloadShort[sel][1];
            adjust = MathF.Pow(10.0f, db * 0.1f);
            for (; i <= 6; ++i) gfc.SvQnt.Shortfact![i] = adjust;

            db = cfg.AdjustTrebleDb + payloadShort[sel][2];
            adjust = MathF.Pow(10.0f, db * 0.1f);
            for (; i <= 11; ++i) gfc.SvQnt.Shortfact![i] = adjust;

            db = cfg.AdjustSfb21Db + payloadShort[sel][3];
            adjust = MathF.Pow(10.0f, db * 0.1f);
            for (; i < LameConstants.SbMaxS; ++i) gfc.SvQnt.Shortfact![i] = adjust;
        }
    }

    /// <summary>
    /// <c>on_pe</c> - allocate bits between the two channels from the perceptual entropy.
    /// </summary>
    /// <param name="pe">The C's <c>pe[2][2]</c>; index as <c>[gr * 2 + ch]</c>.</param>
    public static int OnPe(
        LameInternalFlags gfc, ReadOnlySpan<float> pe, Span<int> targBits,
        int meanBits, int gr, int cbr)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        int extraBits = 0, tbits, bits;
        Span<int> addBits = [0, 0];
        int maxBits;
        int ch;

        LameReservoir.ResvMaxBits(gfc, meanBits, out tbits, out extraBits, cbr);
        maxBits = tbits + extraBits;
        if (maxBits > LameQuantizeConstants.MaxBitsPerGranule)
            maxBits = LameQuantizeConstants.MaxBitsPerGranule;

        for (bits = 0, ch = 0; ch < cfg.ChannelsOut; ++ch)
        {
            targBits[ch] = Math.Min(
                LameQuantizeConstants.MaxBitsPerChannel, tbits / cfg.ChannelsOut);

            addBits[ch] = (int)(targBits[ch] * pe[gr * 2 + ch] / 700.0 - targBits[ch]);

            // at most increase bits by 1.5 * average
            if (addBits[ch] > meanBits * 3 / 4) addBits[ch] = meanBits * 3 / 4;
            if (addBits[ch] < 0) addBits[ch] = 0;

            if (addBits[ch] + targBits[ch] > LameQuantizeConstants.MaxBitsPerChannel)
                addBits[ch] = Math.Max(
                    0, LameQuantizeConstants.MaxBitsPerChannel - targBits[ch]);

            bits += addBits[ch];
        }
        if (bits > extraBits && bits > 0)
        {
            for (ch = 0; ch < cfg.ChannelsOut; ++ch)
                addBits[ch] = extraBits * addBits[ch] / bits;
        }

        for (ch = 0; ch < cfg.ChannelsOut; ++ch)
        {
            targBits[ch] += addBits[ch];
            extraBits -= addBits[ch];
        }

        for (bits = 0, ch = 0; ch < cfg.ChannelsOut; ++ch)
            bits += targBits[ch];

        if (bits > LameQuantizeConstants.MaxBitsPerGranule)
        {
            for (ch = 0; ch < cfg.ChannelsOut; ++ch)
            {
                targBits[ch] *= LameQuantizeConstants.MaxBitsPerGranule;
                targBits[ch] /= bits;
            }
        }

        return maxBits;
    }

    /// <summary><c>reduce_side</c> - move bits from the side channel to the mid channel.</summary>
    public static void ReduceSide(
        Span<int> targBits, float msEnerRatio, int meanBits, int maxBits)
    {
        int moveBits;
        float fac;

        // ms_ener_ratio = 0 allocates 66/33 mid/side, fac = .33; 0.5 allocates 50/50, fac = 0.
        fac = (float)(.33 * (.5 - msEnerRatio) / .5);
        if (fac < 0) fac = 0;
        if (fac > .5f) fac = .5f;

        // number of bits to move from the side channel to the mid channel
        moveBits = (int)(fac * .5 * (targBits[0] + targBits[1]));

        if (moveBits > LameQuantizeConstants.MaxBitsPerChannel - targBits[0])
            moveBits = LameQuantizeConstants.MaxBitsPerChannel - targBits[0];
        if (moveBits < 0) moveBits = 0;

        if (targBits[1] >= 125)
        {
            // don't reduce the side channel below 125 bits
            if (targBits[1] - moveBits > 125)
            {
                // if the mid channel already has twice the average, don't bother
                if (targBits[0] < meanBits) targBits[0] += moveBits;
                targBits[1] -= moveBits;
            }
            else
            {
                targBits[0] += targBits[1] - 125;
                targBits[1] = 125;
            }
        }

        moveBits = targBits[0] + targBits[1];
        if (moveBits > maxBits)
        {
            targBits[0] = maxBits * targBits[0] / moveBits;
            targBits[1] = maxBits * targBits[1] / moveBits;
        }
    }

    /// <summary>
    /// <c>athAdjust</c> - Robert Hegemann 2001-04-27, adjusts the ATH while keeping the
    /// original noise floor, affecting high frequencies more than low ones.
    /// </summary>
    public static float AthAdjust(float a, float x, float athFloor, float athFixpoint)
    {
        const float o = 90.30873362f;
        float p = athFixpoint < 1.0f ? 94.82444863f : athFixpoint;
        float u = (float)LameUtil.FastLog10X(x, 10.0f);
        float v = a * a;
        float w = 0.0f;
        u -= athFloor;                                      // undo scaling
        if (v > 1E-20f) w = (float)(1.0 + LameUtil.FastLog10X(v, 10.0f / o));
        if (w < 0) w = 0.0f;
        u *= w;
        u += athFloor + o - p;                              // redo scaling

        return MathF.Pow(10.0f, 0.1f * u);
    }

    /// <summary>
    /// <c>calc_xmin</c> - the distortion the psychoacoustic model allows in each scalefactor
    /// band, <c>xmin(sb) = ratio(sb) * en(sb) / bw(sb)</c>.
    /// </summary>
    /// <returns>The number of scalefactor bands whose energy is above the ATH.</returns>
    public static int CalcXmin(
        LameInternalFlags gfc, in IiiPsyRatio ratio, ref GrInfo codInfo, Span<float> pxmin)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        int sfb, gsfb, j = 0, athOver = 0, k;
        int maxNonzero;
        int outIndex = 0;

        // DBL_EPSILON, which the C falls back to as a literal where float.h does not define it.
        const float dblEpsilon = 2.2204460492503131e-016f;

        for (gsfb = 0; gsfb < codInfo.PsyLmax; gsfb++)
        {
            float en0, xmin;
            float rh1, rh2, rh3;
            int width, l;

            xmin = AthAdjust(
                gfc.Ath.AdjustFactor, gfc.Ath.L![gsfb], gfc.Ath.Floor, cfg.AthFixpoint);
            xmin *= gfc.SvQnt.Longfact![gsfb];

            width = codInfo.Width[gsfb];
            rh1 = xmin / width;
            rh2 = dblEpsilon;
            en0 = 0.0f;
            for (l = 0; l < width; ++l)
            {
                float xa = codInfo.Xr[j++];
                float x2 = xa * xa;
                en0 += x2;
                rh2 += x2 < rh1 ? x2 : rh1;
            }
            if (en0 > xmin) athOver++;

            if (en0 < xmin) rh3 = en0;
            else if (rh2 < xmin) rh3 = xmin;
            else rh3 = rh2;
            xmin = rh3;
            {
                float e = ratio.En.L[gsfb];
                if (e > 1e-12f)
                {
                    float x;
                    x = en0 * ratio.Thm.L[gsfb] / e;
                    x *= gfc.SvQnt.Longfact[gsfb];
                    if (xmin < x) xmin = x;
                }
            }
            xmin = Math.Max(xmin, dblEpsilon);
            codInfo.EnergyAboveCutoff[gsfb] = (byte)(en0 > xmin + 1e-14f ? 1 : 0);
            pxmin[outIndex++] = xmin;
        }

        // the highest non-zero coefficient
        maxNonzero = 0;
        for (k = 575; k > 0; --k)
        {
            if (Math.Abs(codInfo.Xr[k]) > 1e-12f)
            {
                maxNonzero = k;
                break;
            }
        }
        if (codInfo.BlockType != LameBlockType.Short)
        {
            maxNonzero |= 1;        // only odd numbers
        }
        else
        {
            maxNonzero /= 6;        // 3 short blocks
            maxNonzero *= 6;
            maxNonzero += 5;
        }

        if (gfc.SvQnt.Sfb21Extra == 0 && cfg.SamplerateOut < 44000)
        {
            int sfbL = cfg.SamplerateOut <= 8000 ? 17 : 21;
            int sfbS = cfg.SamplerateOut <= 8000 ? 9 : 12;
            int limit;
            if (codInfo.BlockType != LameBlockType.Short)
                limit = gfc.ScalefacBand.L[sfbL] - 1;
            else
                limit = 3 * gfc.ScalefacBand.S[sfbS] - 1;
            if (maxNonzero > limit) maxNonzero = limit;
        }
        codInfo.MaxNonzeroCoeff = maxNonzero;

        for (sfb = codInfo.SfbSmin; gsfb < codInfo.Psymax; sfb++, gsfb += 3)
        {
            int width, b, l;
            float tmpAth;

            tmpAth = AthAdjust(
                gfc.Ath.AdjustFactor, gfc.Ath.S![sfb], gfc.Ath.Floor, cfg.AthFixpoint);
            tmpAth *= gfc.SvQnt.Shortfact![sfb];

            width = codInfo.Width[gsfb];
            for (b = 0; b < 3; b++)
            {
                float en0 = 0.0f, xmin;
                float rh1, rh2, rh3;

                rh1 = tmpAth / width;
                rh2 = dblEpsilon;
                for (l = 0; l < width; ++l)
                {
                    float xa = codInfo.Xr[j++];
                    float x2 = xa * xa;
                    en0 += x2;
                    rh2 += x2 < rh1 ? x2 : rh1;
                }
                if (en0 > tmpAth) athOver++;

                if (en0 < tmpAth) rh3 = en0;
                else if (rh2 < tmpAth) rh3 = tmpAth;
                else rh3 = rh2;
                xmin = rh3;
                {
                    float e = ratio.En.S[sfb * 3 + b];
                    if (e > 1e-12f)
                    {
                        float x;
                        x = en0 * ratio.Thm.S[sfb * 3 + b] / e;
                        x *= gfc.SvQnt.Shortfact[sfb];
                        if (xmin < x) xmin = x;
                    }
                }
                xmin = Math.Max(xmin, dblEpsilon);
                codInfo.EnergyAboveCutoff[gsfb + b] = (byte)(en0 > xmin + 1e-14f ? 1 : 0);
                pxmin[outIndex++] = xmin;
            }
            if (cfg.UseTemporalMaskingEffect != 0)
            {
                // the C indexes backwards from the advanced pointer: pxmin[-3 + n]
                int back = outIndex - 3;
                if (pxmin[back] > pxmin[back + 1])
                    pxmin[back + 1] += (pxmin[back] - pxmin[back + 1]) * gfc.CdPsy!.Decay;
                if (pxmin[back + 1] > pxmin[back + 2])
                    pxmin[back + 2] += (pxmin[back + 1] - pxmin[back + 2]) * gfc.CdPsy!.Decay;
            }
        }

        return athOver;
    }

    /// <summary>
    /// <c>calc_noise_core_c</c> - the quantisation noise of one run of coefficients.
    /// </summary>
    /// <remarks>
    /// The three branches are the three coding regions: above <c>count1</c> nothing was coded,
    /// so the noise is the signal; between <c>big_values</c> and <c>count1</c> the values are 0
    /// or 1; below that the quantised value is reconstructed through <c>pow43</c>.
    /// </remarks>
    private static float CalcNoiseCoreC(in GrInfo codInfo, ref int startline, int l, float step)
    {
        float noise = 0;
        int j = startline;

        if (j > codInfo.Count1)
        {
            while (l-- != 0)
            {
                float temp;
                temp = codInfo.Xr[j];
                j++;
                noise += temp * temp;
                temp = codInfo.Xr[j];
                j++;
                noise += temp * temp;
            }
        }
        else if (j > codInfo.BigValues)
        {
            Span<float> ix01 = [0, step];
            while (l-- != 0)
            {
                float temp;
                temp = Math.Abs(codInfo.Xr[j]) - ix01[codInfo.L3Enc[j]];
                j++;
                noise += temp * temp;
                temp = Math.Abs(codInfo.Xr[j]) - ix01[codInfo.L3Enc[j]];
                j++;
                noise += temp * temp;
            }
        }
        else
        {
            while (l-- != 0)
            {
                float temp;
                temp = Math.Abs(codInfo.Xr[j]) - Pow43[codInfo.L3Enc[j]] * step;
                j++;
                noise += temp * temp;
                temp = Math.Abs(codInfo.Xr[j]) - Pow43[codInfo.L3Enc[j]] * step;
                j++;
                noise += temp * temp;
            }
        }

        startline = j;
        return noise;
    }

    /// <summary>
    /// <c>calc_noise</c> - the quantisation noise per band, relative to the allowed distortion.
    /// </summary>
    /// <remarks>
    /// The values it returns are in dB relative to the masking threshold: -oo dB is -1.00,
    /// 0 dB is 0.00, +3 dB is +1.68, +10 dB is +6.45.
    /// </remarks>
    public static int CalcNoise(
        in GrInfo codInfo, ReadOnlySpan<float> l3Xmin, Span<float> distort,
        ref CalcNoiseResult res, CalcNoiseData? prevNoise)
    {
        int sfb, l, over = 0;
        float overNoiseDb = 0;
        float totNoiseDb = 0;       // 0 dB relative to masking
        float maxNoise = -20.0f;    // -200 dB relative to masking
        int j = 0;

        res.OverSsd = 0;

        for (sfb = 0; sfb < codInfo.Psymax; sfb++)
        {
            int s = codInfo.GlobalGain
                  - ((codInfo.Scalefac[sfb] + (codInfo.Preflag != 0 ? Pretab[sfb] : 0))
                     << (codInfo.ScalefacScale + 1))
                  - codInfo.SubblockGain[codInfo.Window[sfb]] * 8;
            float rL3Xmin = 1.0f / l3Xmin[sfb];
            float distortValue = 0.0f;
            float noise = 0.0f;

            if (prevNoise is not null && prevNoise.Step[sfb] == s)
            {
                // use previously computed values
                j += codInfo.Width[sfb];
                distortValue = rL3Xmin * prevNoise.Noise[sfb];

                noise = prevNoise.NoiseLog[sfb];
            }
            else
            {
                float step = Pow20Of(s);
                l = codInfo.Width[sfb] >> 1;

                if (j + codInfo.Width[sfb] > codInfo.MaxNonzeroCoeff)
                {
                    int usefullsize = codInfo.MaxNonzeroCoeff - j + 1;
                    l = usefullsize > 0 ? usefullsize >> 1 : 0;
                }

                noise = CalcNoiseCoreC(codInfo, ref j, l, step);

                if (prevNoise is not null)
                {
                    prevNoise.Step[sfb] = s;
                    prevNoise.Noise[sfb] = noise;
                }

                distortValue = rL3Xmin * noise;

                // multiplying here is adding in dB, but can overflow
                noise = (float)LameUtil.FastLog10(Math.Max(distortValue, 1E-20f));

                if (prevNoise is not null) prevNoise.NoiseLog[sfb] = noise;
            }
            distort[sfb] = distortValue;

            if (prevNoise is not null) prevNoise.GlobalGain = codInfo.GlobalGain;

            totNoiseDb += noise;

            if (noise > 0.0f)
            {
                int tmp = Math.Max((int)(noise * 10 + .5f), 1);
                res.OverSsd += tmp * tmp;

                over++;
                // multiplying here is adding in dB, but can overflow
                overNoiseDb += noise;
            }
            maxNoise = Math.Max(maxNoise, noise);
        }

        res.OverCount = over;
        res.TotNoise = totNoiseDb;
        res.OverNoise = overNoiseDb;
        res.MaxNoise = maxNoise;

        return over;
    }
}

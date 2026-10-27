// Translated from lame-3.100/libmp3lame/psymodel.c - the psychoacoustic model.
//
// Only the VBR model is here. libmp3lame.c reaches L3psycho_anal_vbr for every mode it offers,
// CBR included, because lame.c's encoder loop calls that one and never L3psycho_anal_ns; the
// latter is recorded as diverged in the port checklist rather than translated.
//
// WHAT IS LEFT OUT, AND WHY. Every `if (plt)` block writes into plotting_data for LAME's frame
// analyser GUI (lame-analysis.h), reached only when cfg->analysis is set, which libmp3lame.c
// never does. Those blocks are omitted and noted at each site. Nothing else is omitted.
//
// PRECISION. LAME's FLOAT is a 32-bit float. The literals in this file are mostly doubles -
// NS_PREECHO_ATT0 is 0.8, not 0.8f - so a statement mixing them evaluates in double and rounds
// once on assignment, and is written here as (float)(...) around the whole expression. Where a
// statement is all FLOAT in the C it is left alone. FAST_LOG10 is a double expression in the C
// too, because fast_log2 returns a float that is immediately multiplied by LOG2/LOG10; see
// LameUtil, where it correspondingly returns double.
//
// SCRATCH. L3psycho_anal_vbr declares about 20 KB of FLOAT arrays as C stack locals -
// wsamp_L[2][1024], wsamp_S[2][3][256], eb[4][64], thr[4][64] and the rest. They are gathered
// into LamePsyScratch, allocated once per encoder, which is a divergence recorded in the
// registry: the alternative is either a 20 KB stack frame in a method the JIT then refuses to
// tier up, or an allocation on every granule, and this project treats per-frame allocation as a
// defect.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>psymodel.h</c>'s tuning constants.</summary>
internal static class LamePsyConstants
{
    /// <summary><c>rpelev</c>.</summary>
    public const float Rpelev = 2;

    /// <summary><c>rpelev2</c>.</summary>
    public const float Rpelev2 = 16;

    /// <summary><c>DELBARK</c> - the width of a partition band, in barks.</summary>
    public const double Delbark = .34;

    /// <summary><c>VO_SCALE</c> - tuned for output level, and sensitive to the energy scale.</summary>
    public const double VoScale = 1.0 / (14752.0 * 14752.0) / (LameConstants.BlkSize / 2);

    /// <summary><c>temporalmask_sustain_sec</c>.</summary>
    public const double TemporalmaskSustainSec = 0.01;

    /// <summary><c>NS_PREECHO_ATT0</c>.</summary>
    public const double NsPreechoAtt0 = 0.8;

    /// <summary><c>NS_PREECHO_ATT1</c>.</summary>
    public const double NsPreechoAtt1 = 0.6;

    /// <summary><c>NS_PREECHO_ATT2</c>.</summary>
    public const double NsPreechoAtt2 = 0.3;

    /// <summary><c>NS_MSFIX</c>.</summary>
    public const double NsMsfix = 3.5;

    /// <summary><c>NSATTACKTHRE</c>.</summary>
    public const float NsAttackThre = 4.4f;

    /// <summary><c>NSATTACKTHRE_S</c>.</summary>
    public const float NsAttackThreS = 25;

    /// <summary><c>NSFIRLEN</c>.</summary>
    public const int NsFirLen = 21;

    /// <summary><c>LN_TO_LOG10</c> - <c>M_LN10 / 10</c>.</summary>
    public const double LnToLog10 = LameUtil.Log10Constant / 10;
}

/// <summary>
/// The C stack locals of <c>L3psycho_anal_vbr</c> and the helpers it calls, hoisted out.
/// </summary>
/// <remarks>
/// See the file header. One instance per encoder; nothing here survives a call, so its contents
/// are written before they are read on every granule exactly as the C's stack is.
/// </remarks>
internal sealed class LamePsyScratch
{
    /// <summary><c>wsamp_L[2][BLKSIZE]</c> - index as <c>[row * BLKSIZE + j]</c>.</summary>
    public readonly float[] WsampL = new float[2 * LameConstants.BlkSize];

    /// <summary>
    /// <c>wsamp_S[2][3][BLKSIZE_s]</c> - index as <c>[(row * 3 + sblock) * BLKSIZE_s + j]</c>.
    /// </summary>
    public readonly float[] WsampS = new float[2 * 3 * LameConstants.BlkSizeS];

    /// <summary><c>fftenergy[HBLKSIZE]</c>.</summary>
    public readonly float[] FftEnergy = new float[LameConstants.HBlkSize];

    /// <summary><c>fftenergy_s[3][HBLKSIZE_s]</c> - index as <c>[sblock * HBLKSIZE_s + j]</c>.</summary>
    public readonly float[] FftEnergyS = new float[3 * LameConstants.HBlkSizeS];

    /// <summary><c>eb[4][CBANDS]</c> - index as <c>[chn * CBANDS + b]</c>.</summary>
    public readonly float[] Eb = new float[4 * LameConstants.CBands];

    /// <summary><c>thr[4][CBANDS]</c>.</summary>
    public readonly float[] Thr = new float[4 * LameConstants.CBands];

    /// <summary><c>ns_hpfsmpl[2][576]</c>.</summary>
    public readonly float[] NsHpfSmpl = new float[2 * 576];

    /// <summary><c>sub_short_factor[4][3]</c>.</summary>
    public readonly float[] SubShortFactor = new float[4 * 3];

    /// <summary><c>ns_attacks[4][4]</c>.</summary>
    public readonly int[] NsAttacks = new int[4 * 4];

    /// <summary><c>uselongblock[2]</c>.</summary>
    public readonly int[] UseLongBlock = new int[2];

    /// <summary><c>last_thm[4]</c>.</summary>
    public readonly IiiPsyXmin[] LastThm = new IiiPsyXmin[4];

    /// <summary><c>max[CBANDS]</c>, shared by the two masking functions, which never overlap.</summary>
    public readonly float[] Max = new float[LameConstants.CBands];

    /// <summary><c>avg[CBANDS]</c>.</summary>
    public readonly float[] Avg = new float[LameConstants.CBands];

    /// <summary><c>mask_idx_l[CBANDS + 2]</c>, and <c>mask_idx_s[CBANDS]</c>.</summary>
    public readonly byte[] MaskIdx = new byte[LameConstants.CBands + 2];
}

/// <summary><c>psymodel.c</c>.</summary>
internal static class LamePsyModel
{
    /// <summary><c>I1LIMIT</c>, as <c>pow(10, (I1LIMIT + 1) / 16.0)</c>.</summary>
    private const float MaMaxI1 = 3.6517412725483771f;

    /// <summary><c>I2LIMIT</c>, as <c>pow(10, (I2LIMIT + 1) / 16.0)</c>.</summary>
    private const float MaMaxI2 = 31.622776601683793f;

    /// <summary>
    /// <c>tab[]</c> - the masking table, running from 0 dB (TMN) to 9.3 dB (NMT).
    /// </summary>
    private static readonly float[] tab =
    [
        1.0f,     // pow(10, -0)
        0.79433f, // pow(10, -0.1)
        0.63096f, // pow(10, -0.2)
        0.63096f,
        0.63096f,
        0.63096f,
        0.63096f,
        0.25119f, // pow(10, -0.6)
        0.11749f, // pow(10, -0.93)
    ];

    /// <summary><c>tab_mask_add_delta[]</c>.</summary>
    private static readonly int[] tabMaskAddDelta = [2, 2, 2, 1, 1, 1, 0, 0, -1];

    /// <summary><c>table2[]</c> of <c>vbrpsy_mask_add</c>; the products fold in double.</summary>
    private static readonly float[] table2 =
    [
        (float)(1.33352 * 1.33352), (float)(1.35879 * 1.35879),
        (float)(1.38454 * 1.38454), (float)(1.39497 * 1.39497),
        (float)(1.40548 * 1.40548), (float)(1.3537 * 1.3537),
        (float)(1.30382 * 1.30382), (float)(1.22321 * 1.22321),
        (float)(1.14758 * 1.14758),
        1,
    ];

    /// <summary><c>fircoef[]</c> of <c>vbrpsy_attack_detection</c> - the fs/4 high pass.</summary>
    private static readonly float[] fircoef =
    [
        (float)(-8.65163e-18 * 2), (float)(-0.00851586 * 2),
        (float)(-6.74764e-18 * 2), (float)(0.0209036 * 2),
        (float)(-3.36639e-17 * 2), (float)(-0.0438162 * 2),
        (float)(-1.54175e-17 * 2), (float)(0.0931738 * 2),
        (float)(-5.52212e-17 * 2), (float)(-0.313819 * 2),
    ];

    /// <summary><c>regcoef_s[]</c> of <c>pecalc_s</c>, tuned for 44.1 kHz only.</summary>
    private static readonly float[] regcoefS =
    [
        11.8f, 13.6f, 17.2f, 32f, 46.5f, 51.3f, 57.5f, 67.1f, 71.5f, 84.6f, 97.6f, 130f,
        // 255.8 is commented out in the C.
    ];

    /// <summary><c>regcoef_l[]</c> of <c>pecalc_l</c>.</summary>
    private static readonly float[] regcoefL =
    [
        6.8f, 5.8f, 5.8f, 6.4f, 6.5f, 9.9f, 12.1f, 14.4f, 15f, 18.9f, 21.6f, 26.9f, 34.2f,
        40.2f, 46.8f, 56.5f, 60.7f, 73.9f, 85.7f, 93.4f, 126.1f,
        // 241.3 is commented out in the C.
    ];

    /// <summary><c>sk[]</c> of <c>psymodel_init</c>'s masking_lower setup.</summary>
    private static readonly float[] sk =
        [-7.4f, -7.4f, -7.4f, -9.5f, -7.4f, -6.1f, -5.5f, -4.7f, -4.7f, -4.7f, -4.7f];

    /// <summary><c>psycho_loudness_approx</c> - a loudness-squared approximation.</summary>
    /// <remarks>
    /// Calibrated so that binary white noise at full scale approaches 3.
    /// </remarks>
    private static float PsychoLoudnessApprox(ReadOnlySpan<float> energy, ReadOnlySpan<float> eqlW)
    {
        float loudnessPower = 0.0f;
        for (int i = 0; i < LameConstants.BlkSize / 2; ++i)
            loudnessPower += energy[i] * eqlW[i];
        return (float)(loudnessPower * LamePsyConstants.VoScale);
    }

    /// <summary><c>mask_add_delta</c>.</summary>
    private static int MaskAddDelta(int i) => tabMaskAddDelta[i];

    /// <summary><c>vbrpsy_mask_add</c> - addition of simultaneous masking, Naoki Shibata 2000.</summary>
    private static float VbrpsyMaskAdd(float m1, float m2, int b, int delta)
    {
        float ratio;

        if (m1 < 0) m1 = 0;
        if (m2 < 0) m2 = 0;
        if (m1 <= 0) return m2;
        if (m2 <= 0) return m1;

        ratio = m2 > m1 ? m2 / m1 : m1 / m2;

        if (Math.Abs(b) <= delta)
        {
            // approximately, 1 bark = 3 partitions; originally 'if (i > 8)'
            if (ratio >= MaMaxI1)
            {
                return m1 + m2;
            }
            else
            {
                int i = (int)LameUtil.FastLog10X(ratio, 16.0f);
                return (m1 + m2) * table2[i];
            }
        }
        if (ratio < MaMaxI2) return m1 + m2;
        if (m1 < m2) m1 = m2;
        return m1;
    }

    /// <summary>
    /// <c>convert_partition2scalefac</c> - split the partition band straddling a scalefactor
    /// band boundary between the two.
    /// </summary>
    private static void ConvertPartition2Scalefac(
        in PsyConstCb2Sb gd, ReadOnlySpan<float> eb, ReadOnlySpan<float> thr,
        Span<float> ennOut, Span<float> thmOut)
    {
        float enn, thmm;
        int sb, b, n = gd.NSb;
        int[] bo = gd.Bo!;
        float[] boWeight = gd.BoWeight!;
        enn = thmm = 0.0f;

        for (sb = b = 0; sb < n; ++b, ++sb)
        {
            int boSb = bo[sb];
            int npart = gd.Npart;
            int bLim = boSb < npart ? boSb : npart;
            while (b < bLim)
            {
                enn += eb[b];
                thmm += thr[b];
                b++;
            }
            if (b >= npart)
            {
                ennOut[sb] = enn;
                thmOut[sb] = thmm;
                ++sb;
                break;
            }
            {
                // at transition sfb -> sfb+1
                float wCurr = boWeight[sb];
                float wNext = 1.0f - wCurr;
                enn += wCurr * eb[b];
                thmm += wCurr * thr[b];
                ennOut[sb] = enn;
                thmOut[sb] = thmm;
                enn = wNext * eb[b];
                thmm = wNext * thr[b];
            }
        }
        for (; sb < n; ++sb)
        {
            ennOut[sb] = 0;
            thmOut[sb] = 0;
        }
    }

    /// <summary><c>convert_partition2scalefac_s</c>.</summary>
    private static void ConvertPartition2ScalefacS(
        LameInternalFlags gfc, ReadOnlySpan<float> eb, ReadOnlySpan<float> thr, int chn, int sblock)
    {
        ref PsyStateVar psv = ref gfc.SvPsy;
        FloatSbMaxS ennStorage = default, thmStorage = default;
        Span<float> enn = ennStorage;
        Span<float> thm = thmStorage;

        ConvertPartition2Scalefac(gfc.CdPsy!.S, eb, thr, enn, thm);
        for (int sb = 0; sb < LameConstants.SbMaxS; ++sb)
        {
            psv.En![chn].S[sb * 3 + sblock] = enn[sb];
            psv.Thm![chn].S[sb * 3 + sblock] = thm[sb];
        }
    }

    /// <summary><c>convert_partition2scalefac_l</c>.</summary>
    private static void ConvertPartition2ScalefacL(
        LameInternalFlags gfc, ReadOnlySpan<float> eb, ReadOnlySpan<float> thr, int chn)
    {
        ref PsyStateVar psv = ref gfc.SvPsy;
        ConvertPartition2Scalefac(gfc.CdPsy!.L, eb, thr, psv.En![chn].L, psv.Thm![chn].L);
    }

    /// <summary><c>convert_partition2scalefac_l_to_s</c>.</summary>
    private static void ConvertPartition2ScalefacLToS(
        LameInternalFlags gfc, ReadOnlySpan<float> eb, ReadOnlySpan<float> thr, int chn)
    {
        ref PsyStateVar psv = ref gfc.SvPsy;
        FloatSbMaxS ennStorage = default, thmStorage = default;
        Span<float> enn = ennStorage;
        Span<float> thm = thmStorage;

        ConvertPartition2Scalefac(gfc.CdPsy!.LToS, eb, thr, enn, thm);
        for (int sb = 0; sb < LameConstants.SbMaxS; ++sb)
        {
            const float scale = (float)(1.0 / 64.0f);
            float tmpEnn = enn[sb];
            float tmpThm = thm[sb] * scale;
            for (int sblock = 0; sblock < 3; ++sblock)
            {
                psv.En![chn].S[sb * 3 + sblock] = tmpEnn;
                psv.Thm![chn].S[sb * 3 + sblock] = tmpThm;
            }
        }
    }

    /// <summary><c>NS_INTERP</c> - what <c>pow(x, r) * pow(y, 1 - r)</c> was.</summary>
    private static float NsInterp(float x, float y, float r)
    {
        if (r >= 1.0f) return x;    // 99.7% of the time
        if (r <= 0.0f) return y;
        if (y > 0.0f) return MathF.Pow(x / y, r) * y;
        return 0.0f;                // never happens
    }

    /// <summary><c>pecalc_s</c> - perceptual entropy of a short-block granule.</summary>
    private static float PecalcS(in IiiPsyRatio mr, float maskingLower)
    {
        float peS = 1236.28f / 4;

        for (int sb = 0; sb < LameConstants.SbMaxS - 1; sb++)
        {
            for (int sblock = 0; sblock < 3; sblock++)
            {
                float thm = mr.Thm.S[sb * 3 + sblock];
                if (thm > 0.0f)
                {
                    float x = thm * maskingLower;
                    float en = mr.En.S[sb * 3 + sblock];
                    if (en > x)
                    {
                        if (en > x * 1e10f)
                            peS = (float)(peS + regcoefS[sb] * (10.0f * LameUtil.Log10Constant));
                        else
                            peS = (float)(peS + regcoefS[sb] * LameUtil.FastLog10(en / x));
                    }
                }
            }
        }
        return peS;
    }

    /// <summary><c>pecalc_l</c> - perceptual entropy of a long-block granule.</summary>
    private static float PecalcL(in IiiPsyRatio mr, float maskingLower)
    {
        float peL = 1124.23f / 4;

        for (int sb = 0; sb < LameConstants.SbMaxL - 1; sb++)
        {
            float thm = mr.Thm.L[sb];
            if (thm > 0.0f)
            {
                float x = thm * maskingLower;
                float en = mr.En.L[sb];
                if (en > x)
                {
                    if (en > x * 1e10f)
                        peL = (float)(peL + regcoefL[sb] * (10.0f * LameUtil.Log10Constant));
                    else
                        peL = (float)(peL + regcoefL[sb] * LameUtil.FastLog10(en / x));
                }
            }
        }
        return peL;
    }

    /// <summary><c>calc_energy</c> - energy, peak and mean per partition band.</summary>
    private static void CalcEnergy(
        in PsyConstCb2Sb l, ReadOnlySpan<float> fftenergy,
        Span<float> eb, Span<float> max, Span<float> avg)
    {
        int[] numlines = l.Numlines!;
        float[] rnumlines = l.Rnumlines!;

        for (int b = 0, j = 0; b < l.Npart; ++b)
        {
            float ebb = 0, m = 0;
            for (int i = 0; i < numlines[b]; ++i, ++j)
            {
                float el = fftenergy[j];
                ebb += el;
                if (m < el) m = el;
            }
            eb[b] = ebb;
            max[b] = m;
            avg[b] = ebb * rnumlines[b];
        }
    }

    /// <summary>
    /// The body shared by <c>calc_mask_index_l</c> and <c>vbrpsy_calc_mask_index_s</c>.
    /// </summary>
    /// <remarks>
    /// The two are the same function in the C, written out twice against <c>gfc->cd_psy->l</c>
    /// and <c>-&gt;s</c>; they are byte for byte identical apart from that and two asserts. They
    /// are kept as two named methods below, because the registry maps a C function to a C#
    /// method, with the body here so that the duplication cannot drift.
    /// </remarks>
    private static void CalcMaskIndex(
        in PsyConstCb2Sb gd, ReadOnlySpan<float> max, ReadOnlySpan<float> avg, Span<byte> maskIdx)
    {
        int[] numlines = gd.Numlines!;
        int lastTabEntry = tab.Length - 1;
        float m, a;
        int b, k;

        b = 0;
        a = avg[b] + avg[b + 1];
        if (a > 0.0f)
        {
            m = max[b];
            if (m < max[b + 1]) m = max[b + 1];
            a = 20.0f * (m * 2.0f - a) / (a * (numlines[b] + numlines[b + 1] - 1));
            k = (int)a;
            if (k > lastTabEntry) k = lastTabEntry;
            maskIdx[b] = (byte)k;
        }
        else
        {
            maskIdx[b] = 0;
        }

        for (b = 1; b < gd.Npart - 1; b++)
        {
            a = avg[b - 1] + avg[b] + avg[b + 1];
            if (a > 0.0f)
            {
                m = max[b - 1];
                if (m < max[b]) m = max[b];
                if (m < max[b + 1]) m = max[b + 1];
                a = 20.0f * (m * 3.0f - a)
                  / (a * (numlines[b - 1] + numlines[b] + numlines[b + 1] - 1));
                k = (int)a;
                if (k > lastTabEntry) k = lastTabEntry;
                maskIdx[b] = (byte)k;
            }
            else
            {
                maskIdx[b] = 0;
            }
        }

        a = avg[b - 1] + avg[b];
        if (a > 0.0f)
        {
            m = max[b - 1];
            if (m < max[b]) m = max[b];
            a = 20.0f * (m * 2.0f - a) / (a * (numlines[b - 1] + numlines[b] - 1));
            k = (int)a;
            if (k > lastTabEntry) k = lastTabEntry;
            maskIdx[b] = (byte)k;
        }
        else
        {
            maskIdx[b] = 0;
        }
    }

    /// <summary><c>calc_mask_index_l</c>.</summary>
    private static void CalcMaskIndexL(
        LameInternalFlags gfc, ReadOnlySpan<float> max, ReadOnlySpan<float> avg, Span<byte> maskIdx)
        => CalcMaskIndex(gfc.CdPsy!.L, max, avg, maskIdx);

    /// <summary><c>vbrpsy_calc_mask_index_s</c>.</summary>
    private static void VbrpsyCalcMaskIndexS(
        LameInternalFlags gfc, ReadOnlySpan<float> max, ReadOnlySpan<float> avg, Span<byte> maskIdx)
        => CalcMaskIndex(gfc.CdPsy!.S, max, avg, maskIdx);

    /// <summary><c>vbrpsy_compute_fft_l</c>.</summary>
    /// <param name="wsampL">The C's <c>wsamp_l</c>, as a row index into <c>wsamp_L[2][BLKSIZE]</c>.</param>
    /// <remarks>The <c>plt</c> block, which saves the spectrum for the analyser, is omitted.</remarks>
    private static void VbrpsyComputeFftL(
        LameInternalFlags gfc, SampleBuffers buffer, int chn, int grOut,
        Span<float> fftenergy, float[] wsamp, int wsampL)
    {
        const int blkSize = LameConstants.BlkSize;
        ref PsyStateVar psv = ref gfc.SvPsy;
        int j;

        if (chn < 2)
        {
            LameFft.FftLong(gfc, wsamp.AsSpan(wsampL * blkSize, blkSize), chn, buffer);
        }
        else if (chn == 2)
        {
            float sqrt2Half = (float)(LameUtil.Sqrt2 * 0.5f);
            // FFT data for mid and side channel is derived from L & R
            for (j = blkSize - 1; j >= 0; --j)
            {
                float l = wsamp[(wsampL + 0) * blkSize + j];
                float r = wsamp[(wsampL + 1) * blkSize + j];
                wsamp[(wsampL + 0) * blkSize + j] = (l + r) * sqrt2Half;
                wsamp[(wsampL + 1) * blkSize + j] = (l - r) * sqrt2Half;
            }
        }

        // compute energies
        fftenergy[0] = wsamp[wsampL * blkSize];
        fftenergy[0] *= fftenergy[0];

        for (j = blkSize / 2 - 1; j >= 0; --j)
        {
            float re = wsamp[wsampL * blkSize + blkSize / 2 - j];
            float im = wsamp[wsampL * blkSize + blkSize / 2 + j];
            fftenergy[blkSize / 2 - j] = (re * re + im * im) * 0.5f;
        }

        // total energy
        {
            float totalenergy = 0.0f;
            for (j = 11; j < LameConstants.HBlkSize; j++)
                totalenergy += fftenergy[j];

            psv.TotEner[chn] = totalenergy;
        }

        _ = grOut; // only the omitted plt block uses it
    }

    /// <summary><c>vbrpsy_compute_fft_s</c>.</summary>
    private static void VbrpsyComputeFftS(
        LameInternalFlags gfc, SampleBuffers buffer, int chn, int sblock,
        Span<float> fftenergyS, float[] wsamp, int wsampS)
    {
        const int blkSizeS = LameConstants.BlkSizeS;
        const int hBlkSizeS = LameConstants.HBlkSizeS;
        int j;

        if (sblock == 0 && chn < 2)
            LameFft.FftShort(gfc, wsamp.AsSpan(wsampS * 3 * blkSizeS, 3 * blkSizeS), chn, buffer);

        if (chn == 2)
        {
            float sqrt2Half = (float)(LameUtil.Sqrt2 * 0.5f);
            for (j = blkSizeS - 1; j >= 0; --j)
            {
                float l = wsamp[((wsampS + 0) * 3 + sblock) * blkSizeS + j];
                float r = wsamp[((wsampS + 1) * 3 + sblock) * blkSizeS + j];
                wsamp[((wsampS + 0) * 3 + sblock) * blkSizeS + j] = (l + r) * sqrt2Half;
                wsamp[((wsampS + 1) * 3 + sblock) * blkSizeS + j] = (l - r) * sqrt2Half;
            }
        }

        // compute energies
        int w = (wsampS * 3 + sblock) * blkSizeS;
        fftenergyS[sblock * hBlkSizeS] = wsamp[w];
        fftenergyS[sblock * hBlkSizeS] *= fftenergyS[sblock * hBlkSizeS];
        for (j = blkSizeS / 2 - 1; j >= 0; --j)
        {
            float re = wsamp[w + blkSizeS / 2 - j];
            float im = wsamp[w + blkSizeS / 2 + j];
            fftenergyS[sblock * hBlkSizeS + blkSizeS / 2 - j] = (re * re + im * im) * 0.5f;
        }
    }

    /// <summary><c>vbrpsy_compute_loudness_approximation_l</c>.</summary>
    private static void VbrpsyComputeLoudnessApproximationL(
        LameInternalFlags gfc, int grOut, int chn, ReadOnlySpan<float> fftenergy)
    {
        ref PsyStateVar psv = ref gfc.SvPsy;
        if (chn < 2)
        {
            // no loudness for mid/side ch
            gfc.OvPsy.LoudnessSq[grOut * 2 + chn] = psv.LoudnessSqSave[chn];
            psv.LoudnessSqSave[chn] = PsychoLoudnessApprox(fftenergy, gfc.Ath.EqlW!);
        }
    }

    /// <summary>
    /// <c>vbrpsy_attack_detection</c> - apply an fs/4 high pass and decide long against short.
    /// </summary>
    /// <remarks>The <c>plt</c> block, which saves the attack intensity, is omitted.</remarks>
    private static void VbrpsyAttackDetection(
        LameInternalFlags gfc, SampleBuffers buffer, int grOut,
        Span<IiiPsyRatio> maskingRatio, Span<IiiPsyRatio> maskingMsRatio,
        Span<float> energy, LamePsyScratch scratch)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref PsyStateVar psv = ref gfc.SvPsy;
        float[] nsHpfSmpl = scratch.NsHpfSmpl;
        float[] subShortFactor = scratch.SubShortFactor;
        int[] nsAttacks = scratch.NsAttacks;
        int[] uselongblock = scratch.UseLongBlock;

        int nChnOut = cfg.ChannelsOut;
        // chn=2 and 3 = Mid and Side channels
        int nChnPsy = cfg.Mode == MpegMode.JointStereo ? 4 : nChnOut;
        int chn, i, j;

        Array.Clear(nsHpfSmpl);

        for (chn = 0; chn < nChnOut; chn++)
        {
            // apply high pass filter of fs/4
            ReadOnlySpan<float> channel = buffer[chn];
            int firbuf = 576 - 350 - LamePsyConstants.NsFirLen + 192;
            for (i = 0; i < 576; i++)
            {
                float sum1, sum2;
                sum1 = channel[firbuf + i + 10];
                sum2 = 0.0f;
                for (j = 0; j < ((LamePsyConstants.NsFirLen - 1) / 2) - 1; j += 2)
                {
                    sum1 += fircoef[j]
                          * (channel[firbuf + i + j]
                           + channel[firbuf + i + LamePsyConstants.NsFirLen - j]);
                    sum2 += fircoef[j + 1]
                          * (channel[firbuf + i + j + 1]
                           + channel[firbuf + i + LamePsyConstants.NsFirLen - j - 1]);
                }
                nsHpfSmpl[chn * 576 + i] = sum1 + sum2;
            }
            maskingRatio[grOut * 2 + chn].En = psv.En![chn];
            maskingRatio[grOut * 2 + chn].Thm = psv.Thm![chn];
            if (nChnPsy > 2)
            {
                // MS maskings
                maskingMsRatio[grOut * 2 + chn].En = psv.En[chn + 2];
                maskingMsRatio[grOut * 2 + chn].Thm = psv.Thm[chn + 2];
            }
        }

        for (chn = 0; chn < nChnPsy; chn++)
        {
            Float12 attackIntensityStorage = default, enSubshortStorage = default;
            Float4 enShortStorage = default;
            Span<float> attackIntensity = attackIntensityStorage;
            Span<float> enSubshort = enSubshortStorage;
            Span<float> enShort = enShortStorage;
            int pf = (chn & 1) * 576;
            int nsUselongblock = 1;

            if (chn == 2)
            {
                for (i = 0, j = 576; j > 0; ++i, --j)
                {
                    float l = nsHpfSmpl[i];
                    float r = nsHpfSmpl[576 + i];
                    nsHpfSmpl[i] = l + r;
                    nsHpfSmpl[576 + i] = l - r;
                }
            }

            // determine the block type (window type): energies of each sub-shortblock
            for (i = 0; i < 3; i++)
            {
                enSubshort[i] = psv.LastEnSubshort![chn * 9 + i + 6];
                attackIntensity[i] = enSubshort[i] / psv.LastEnSubshort[chn * 9 + i + 4];
                enShort[0] += enSubshort[i];
            }

            for (i = 0; i < 9; i++)
            {
                int pfe = pf + 576 / 9;
                float p = 1.0f;
                for (; pf < pfe; pf++)
                    if (p < Math.Abs(nsHpfSmpl[pf]))
                        p = Math.Abs(nsHpfSmpl[pf]);

                psv.LastEnSubshort![chn * 9 + i] = enSubshort[i + 3] = p;
                enShort[1 + i / 3] += p;
                if (p > enSubshort[i + 3 - 2])
                {
                    p = p / enSubshort[i + 3 - 2];
                }
                else if (enSubshort[i + 3 - 2] > p * 10.0f)
                {
                    p = enSubshort[i + 3 - 2] / (p * 10.0f);
                }
                else
                {
                    p = 0.0f;
                }
                attackIntensity[i + 3] = p;
            }

            // pulse like signal detection for fatboy.wav and so on
            for (i = 0; i < 3; ++i)
            {
                float enn = enSubshort[i * 3 + 3] + enSubshort[i * 3 + 4] + enSubshort[i * 3 + 5];
                float factor = 1.0f;
                if (enSubshort[i * 3 + 5] * 6 < enn)
                {
                    factor *= 0.5f;
                    if (enSubshort[i * 3 + 4] * 6 < enn)
                        factor *= 0.5f;
                }
                subShortFactor[chn * 3 + i] = factor;
            }

            // compare energies between sub-shortblocks
            {
                float x = gfc.CdPsy!.AttackThreshold![chn];
                for (i = 0; i < 12; i++)
                {
                    if (nsAttacks[chn * 4 + i / 3] == 0)
                    {
                        if (attackIntensity[i] > x)
                            nsAttacks[chn * 4 + i / 3] = (i % 3) + 1;
                    }
                }
            }

            // Should have an energy change between short blocks, to avoid periodic signals.
            // GB tuned (1) to avoid too many short blocks for TRUMPET; RH tuned (2) to let
            // enough through for FSOL and SNAPS.
            for (i = 1; i < 4; i++)
            {
                float u = enShort[i - 1];
                float v = enShort[i];
                float m = Math.Max(u, v);
                if (m < 40000)
                {
                    if (u < 1.7f * v && v < 1.7f * u)
                    {
                        if (i == 1 && nsAttacks[chn * 4 + 0] <= nsAttacks[chn * 4 + i])
                            nsAttacks[chn * 4 + 0] = 0;
                        nsAttacks[chn * 4 + i] = 0;
                    }
                }
            }

            if (nsAttacks[chn * 4 + 0] <= psv.LastAttacks[chn])
                nsAttacks[chn * 4 + 0] = 0;

            if (psv.LastAttacks[chn] == 3
                || nsAttacks[chn * 4 + 0] + nsAttacks[chn * 4 + 1]
                 + nsAttacks[chn * 4 + 2] + nsAttacks[chn * 4 + 3] != 0)
            {
                nsUselongblock = 0;

                if (nsAttacks[chn * 4 + 1] != 0 && nsAttacks[chn * 4 + 0] != 0)
                    nsAttacks[chn * 4 + 1] = 0;
                if (nsAttacks[chn * 4 + 2] != 0 && nsAttacks[chn * 4 + 1] != 0)
                    nsAttacks[chn * 4 + 2] = 0;
                if (nsAttacks[chn * 4 + 3] != 0 && nsAttacks[chn * 4 + 2] != 0)
                    nsAttacks[chn * 4 + 3] = 0;
            }

            if (chn < 2)
            {
                uselongblock[chn] = nsUselongblock;
            }
            else
            {
                if (nsUselongblock == 0)
                    uselongblock[0] = uselongblock[1] = 0;
            }

            // There is a one granule delay; the maskings computed last call were copied into
            // masking_ratio above to return to the calling program.
            energy[chn] = psv.TotEner[chn];
        }
    }

    /// <summary><c>vbrpsy_skip_masking_s</c>.</summary>
    private static void VbrpsySkipMaskingS(LameInternalFlags gfc, int chn, int sblock)
    {
        if (sblock == 0)
        {
            float[] nbs2 = gfc.SvPsy.NbS2!;
            float[] nbs1 = gfc.SvPsy.NbS1!;
            int n = gfc.CdPsy!.S.Npart;
            for (int b = 0; b < n; b++)
                nbs2[chn * LameConstants.CBands + b] = nbs1[chn * LameConstants.CBands + b];
        }
    }

    /// <summary><c>vbrpsy_compute_masking_s</c>.</summary>
    private static void VbrpsyComputeMaskingS(
        LameInternalFlags gfc, ReadOnlySpan<float> fftenergyS,
        Span<float> eb, Span<float> thr, int chn, int sblock, LamePsyScratch scratch)
    {
        const int cbands = LameConstants.CBands;
        ref PsyStateVar psv = ref gfc.SvPsy;
        ref PsyConstCb2Sb gds = ref gfc.CdPsy!.S;
        Span<float> max = scratch.Max;
        Span<float> avg = scratch.Avg;
        Span<byte> maskIdxS = scratch.MaskIdx;
        int[] numlines = gds.Numlines!;
        float[] rnumlines = gds.Rnumlines!;
        int[] s3ind = gds.S3Ind!;
        float[] s3 = gds.S3!;
        int i, j, b;

        max.Clear();
        avg.Clear();

        for (b = j = 0; b < gds.Npart; ++b)
        {
            float ebb = 0, m = 0;
            int n = numlines[b];
            for (i = 0; i < n; ++i, ++j)
            {
                float el = fftenergyS[sblock * LameConstants.HBlkSizeS + j];
                ebb += el;
                if (m < el) m = el;
            }
            eb[b] = ebb;
            max[b] = m;
            avg[b] = ebb * rnumlines[b];
        }

        VbrpsyCalcMaskIndexS(gfc, max, avg, maskIdxS);

        for (j = b = 0; b < gds.Npart; b++)
        {
            int kk = s3ind[b * 2 + 0];
            int last = s3ind[b * 2 + 1];
            int delta = MaskAddDelta(maskIdxS[b]);
            int dd, ddN;
            float x, ecb, avgMask;
            float maskingLower = gds.MaskingLower![b] * gfc.SvQnt.MaskingLower;

            dd = maskIdxS[kk];
            ddN = 1;
            ecb = s3[j] * eb[kk] * tab[maskIdxS[kk]];
            ++j; ++kk;
            while (kk <= last)
            {
                dd += maskIdxS[kk];
                ddN += 1;
                x = s3[j] * eb[kk] * tab[maskIdxS[kk]];
                ecb = VbrpsyMaskAdd(ecb, x, kk - b, delta);
                ++j; ++kk;
            }
            dd = (1 + 2 * dd) / (2 * ddN);
            avgMask = tab[dd] * 0.5f;
            ecb *= avgMask;

            // The C's pre-echo control for short blocks is #if 0'd out here and done later.
            thr[b] = ecb;

            psv.NbS2![chn * cbands + b] = psv.NbS1![chn * cbands + b];
            psv.NbS1[chn * cbands + b] = ecb;
            {
                // If THR exceeds EB the quantisation routines take the difference from other
                // bands, which on strongly tonal samples is heavy distortion; so limit it.
                x = max[b];
                x *= gds.Minval![b];
                x *= avgMask;
                if (thr[b] > x) thr[b] = x;
            }
            if (maskingLower > 1) thr[b] *= maskingLower;
            if (thr[b] > eb[b]) thr[b] = eb[b];
            if (maskingLower < 1) thr[b] *= maskingLower;
        }
        for (; b < cbands; ++b)
        {
            eb[b] = 0;
            thr[b] = 0;
        }
    }

    /// <summary><c>vbrpsy_compute_masking_l</c>.</summary>
    private static void VbrpsyComputeMaskingL(
        LameInternalFlags gfc, ReadOnlySpan<float> fftenergy,
        Span<float> ebL, Span<float> thr, int chn, LamePsyScratch scratch)
    {
        const int cbands = LameConstants.CBands;
        ref PsyStateVar psv = ref gfc.SvPsy;
        ref PsyConstCb2Sb gdl = ref gfc.CdPsy!.L;
        Span<float> max = scratch.Max;
        Span<float> avg = scratch.Avg;
        Span<byte> maskIdxL = scratch.MaskIdx;
        int[] s3ind = gdl.S3Ind!;
        float[] s3 = gdl.S3!;
        int k, b;

        // Calculate the energy and the tonality of each partition.
        CalcEnergy(gdl, fftenergy, ebL, max, avg);
        CalcMaskIndexL(gfc, max, avg, maskIdxL);

        // Convolve the partitioned energy with the spreading function s3_l[b][k].
        k = 0;
        for (b = 0; b < gdl.Npart; b++)
        {
            float x, ecb, avgMask, t;
            float maskingLower = gdl.MaskingLower![b] * gfc.SvQnt.MaskingLower;
            int kk = s3ind[b * 2 + 0];
            int last = s3ind[b * 2 + 1];
            int delta = MaskAddDelta(maskIdxL[b]);
            int dd, ddN = 0;

            dd = maskIdxL[kk];
            ddN += 1;
            ecb = s3[k] * ebL[kk] * tab[maskIdxL[kk]];
            ++k; ++kk;
            while (kk <= last)
            {
                dd += maskIdxL[kk];
                ddN += 1;
                x = s3[k] * ebL[kk] * tab[maskIdxL[kk]];
                t = VbrpsyMaskAdd(ecb, x, kk - b, delta);
                ecb = t;
                ++k; ++kk;
            }
            dd = (1 + 2 * dd) / (2 * ddN);
            avgMask = tab[dd] * 0.5f;
            ecb *= avgMask;

            // Long block pre-echo control. Do not use it if the previous granule was a short
            // block, to avoid: frame0 quiet, frame1 a surge that triggers short blocks, frame2
            // regular - which looks like pre-echo against frame0 although all of it was in
            // frame1. chn 0,1 are L and R; chn 2,3 are M and S.
            if (psv.BlocktypeOld[chn & 0x01] == LameBlockType.Short)
            {
                float ecbLimit = LamePsyConstants.Rpelev * psv.NbL1![chn * cbands + b];
                if (ecbLimit > 0)
                {
                    thr[b] = Math.Min(ecb, ecbLimit);
                }
                else
                {
                    // Robert 071209: because long block psy is not calculated when the granule
                    // is known to be short, there is no clue what the granule before would have
                    // looked like as a long block, so this END_TYPE block is a guess. The speed
                    // is worth it.
                    thr[b] = Math.Min(ecb, (float)(ebL[b] * LamePsyConstants.NsPreechoAtt2));
                }
            }
            else
            {
                float ecbLimit2 = LamePsyConstants.Rpelev2 * psv.NbL2![chn * cbands + b];
                float ecbLimit1 = LamePsyConstants.Rpelev * psv.NbL1![chn * cbands + b];
                float ecbLimit;
                if (ecbLimit2 <= 0) ecbLimit2 = ecb;
                if (ecbLimit1 <= 0) ecbLimit1 = ecb;
                if (psv.BlocktypeOld[chn & 0x01] == LameBlockType.Norm)
                    ecbLimit = Math.Min(ecbLimit1, ecbLimit2);
                else
                    ecbLimit = ecbLimit1;
                thr[b] = Math.Min(ecb, ecbLimit);
            }
            psv.NbL2![chn * cbands + b] = psv.NbL1![chn * cbands + b];
            psv.NbL1[chn * cbands + b] = ecb;
            {
                x = max[b];
                x *= gdl.Minval![b];
                x *= avgMask;
                if (thr[b] > x) thr[b] = x;
            }
            if (maskingLower > 1) thr[b] *= maskingLower;
            if (thr[b] > ebL[b]) thr[b] = ebL[b];
            if (maskingLower < 1) thr[b] *= maskingLower;
        }
        for (; b < cbands; ++b)
        {
            ebL[b] = 0;
            thr[b] = 0;
        }
    }

    /// <summary><c>vbrpsy_compute_block_type</c>.</summary>
    private static void VbrpsyComputeBlockType(ref SessionConfig cfg, Span<int> uselongblock)
    {
        // Force both channels to the same block type. This is necessary if the frame is to be
        // encoded in ms_stereo, and FhG does it even without.
        if (cfg.ShortBlocks == ShortBlock.Coupled
            && !(uselongblock[0] != 0 && uselongblock[1] != 0))
            uselongblock[0] = uselongblock[1] = 0;

        for (int chn = 0; chn < cfg.ChannelsOut; chn++)
        {
            if (cfg.ShortBlocks == ShortBlock.Dispensed) uselongblock[chn] = 1;
            if (cfg.ShortBlocks == ShortBlock.Forced) uselongblock[chn] = 0;
        }
    }

    /// <summary><c>vbrpsy_apply_block_type</c>.</summary>
    private static void VbrpsyApplyBlockType(
        ref PsyStateVar psv, int nch, ReadOnlySpan<int> uselongblock, Span<int> blocktypeD)
    {
        // Update the block type of the previous granule, since it depends on what happened in
        // this one.
        for (int chn = 0; chn < nch; chn++)
        {
            int blocktype = LameBlockType.Norm;

            if (uselongblock[chn] != 0)
            {
                // no attack : use long blocks
                if (psv.BlocktypeOld[chn] == LameBlockType.Short)
                    blocktype = LameBlockType.Stop;
            }
            else
            {
                // attack : use short blocks
                blocktype = LameBlockType.Short;
                if (psv.BlocktypeOld[chn] == LameBlockType.Norm)
                    psv.BlocktypeOld[chn] = LameBlockType.Start;
                if (psv.BlocktypeOld[chn] == LameBlockType.Stop)
                    psv.BlocktypeOld[chn] = LameBlockType.Short;
            }

            blocktypeD[chn] = psv.BlocktypeOld[chn]; // value returned to calling program
            psv.BlocktypeOld[chn] = blocktype;       // save for next call to l3psy_anal
        }
    }

    /// <summary>
    /// <c>vbrpsy_compute_MS_thresholds</c> - Johnston and Ferreira, ICASSP 1992.
    /// </summary>
    private static void VbrpsyComputeMsThresholds(
        ReadOnlySpan<float> eb, Span<float> thr, ReadOnlySpan<float> cbMld,
        ReadOnlySpan<float> athCb, float athlower, float msfix, int n)
    {
        const int cbands = LameConstants.CBands;
        float msfix2 = msfix * 2.0f;
        float rside, rmid;

        for (int b = 0; b < n; ++b)
        {
            float ebM = eb[2 * cbands + b];
            float ebS = eb[3 * cbands + b];
            float thmL = thr[0 * cbands + b];
            float thmR = thr[1 * cbands + b];
            float thmM = thr[2 * cbands + b];
            float thmS = thr[3 * cbands + b];

            // Use this fix if L and R masking differ by 2 dB or less.
            if (thmL <= 1.58f * thmR && thmR <= 1.58f * thmL)
            {
                float mldM = cbMld[b] * ebS;
                float mldS = cbMld[b] * ebM;
                float tmpM = Math.Min(thmS, mldM);
                float tmpS = Math.Min(thmM, mldS);
                rmid = Math.Max(thmM, tmpM);
                rside = Math.Max(thmS, tmpS);
            }
            else
            {
                rmid = thmM;
                rside = thmS;
            }
            if (msfix > 0.0f)
            {
                // Adjust M/S maskings if the user set "msfix" - Naoki Shibata 2000.
                float thmLr, thmMs;
                float ath = athCb[b] * athlower;
                float tmpL = Math.Max(thmL, ath);
                float tmpR = Math.Max(thmR, ath);
                thmLr = Math.Min(tmpL, tmpR);
                thmM = Math.Max(rmid, ath);
                thmS = Math.Max(rside, ath);
                thmMs = thmM + thmS;
                if (thmMs > 0.0f && thmLr * msfix2 < thmMs)
                {
                    float f = thmLr * msfix2 / thmMs;
                    thmM *= f;
                    thmS *= f;
                }
                rmid = Math.Min(thmM, rmid);
                rside = Math.Min(thmS, rside);
            }
            if (rmid > ebM) rmid = ebM;
            if (rside > ebS) rside = ebS;
            thr[2 * cbands + b] = rmid;
            thr[3 * cbands + b] = rside;
        }
    }

    /// <summary>
    /// <c>L3psycho_anal_vbr</c> - the whole model for one granule.
    /// </summary>
    /// <remarks>
    /// The C notes that the bitrate reduction from the inter-channel masking effect is small
    /// compared with the chance of audible artefacts, and this function does not use it.
    /// The <c>plt</c> block at the end, which records PE for the analyser, is omitted.
    /// </remarks>
    public static int L3PsychoAnalVbr(
        LameInternalFlags gfc, SampleBuffers buffer, int grOut,
        Span<IiiPsyRatio> maskingRatio, Span<IiiPsyRatio> maskingMsRatio,
        Span<float> percepEntropy, Span<float> percepMsEntropy,
        Span<float> energy, Span<int> blocktypeD)
    {
        const int cbands = LameConstants.CBands;
        ref SessionConfig cfg = ref gfc.Cfg;
        ref PsyStateVar psv = ref gfc.SvPsy;
        PsyConstT cd = gfc.CdPsy!;
        LamePsyScratch scratch = gfc.PsyScratch;

        float[] fftenergy = scratch.FftEnergy;
        float[] fftenergyS = scratch.FftEnergyS;
        float[] wsampL = scratch.WsampL;
        float[] wsampS = scratch.WsampS;
        float[] eb = scratch.Eb;
        float[] thr = scratch.Thr;
        float[] subShortFactor = scratch.SubShortFactor;
        int[] nsAttacks = scratch.NsAttacks;
        int[] uselongblock = scratch.UseLongBlock;
        IiiPsyXmin[] lastThm = scratch.LastThm;

        float thmm;
        const float pcfact = 0.6f;
        float athFactor = cfg.Msfix > 0.0f ? cfg.AthOffsetFactor * gfc.Ath.AdjustFactor : 1.0f;

        int chn, sb, sblock;

        // chn=2 and 3 = Mid and Side channels
        int nChnPsy = cfg.Mode == MpegMode.JointStereo ? 4 : cfg.ChannelsOut;

        Array.Clear(nsAttacks);
        psv.Thm.AsSpan(0, 4).CopyTo(lastThm);

        VbrpsyAttackDetection(gfc, buffer, grOut, maskingRatio, maskingMsRatio, energy, scratch);

        VbrpsyComputeBlockType(ref cfg, uselongblock);

        // LONG BLOCK CASE
        {
            for (chn = 0; chn < nChnPsy; chn++)
            {
                int ch01 = chn & 0x01;

                VbrpsyComputeFftL(gfc, buffer, chn, grOut, fftenergy, wsampL, ch01);
                VbrpsyComputeLoudnessApproximationL(gfc, grOut, chn, fftenergy);
                VbrpsyComputeMaskingL(
                    gfc, fftenergy,
                    eb.AsSpan(chn * cbands, cbands), thr.AsSpan(chn * cbands, cbands),
                    chn, scratch);
            }
            if (cfg.Mode == MpegMode.JointStereo)
            {
                if (uselongblock[0] + uselongblock[1] == 2)
                    VbrpsyComputeMsThresholds(
                        eb, thr, cd.L.MldCb!, gfc.Ath.CbL!, athFactor, cfg.Msfix, cd.L.Npart);
            }
            for (chn = 0; chn < nChnPsy; chn++)
            {
                ConvertPartition2ScalefacL(
                    gfc, eb.AsSpan(chn * cbands, cbands), thr.AsSpan(chn * cbands, cbands), chn);
                ConvertPartition2ScalefacLToS(
                    gfc, eb.AsSpan(chn * cbands, cbands), thr.AsSpan(chn * cbands, cbands), chn);
            }
        }

        // SHORT BLOCKS CASE
        {
            int forceShortBlockCalc = cd.ForceShortBlockCalc;
            for (sblock = 0; sblock < 3; sblock++)
            {
                for (chn = 0; chn < nChnPsy; ++chn)
                {
                    int ch01 = chn & 0x01;
                    if (uselongblock[ch01] != 0 && forceShortBlockCalc == 0)
                    {
                        VbrpsySkipMaskingS(gfc, chn, sblock);
                    }
                    else
                    {
                        // compute masking thresholds for short blocks
                        VbrpsyComputeFftS(gfc, buffer, chn, sblock, fftenergyS, wsampS, ch01);
                        VbrpsyComputeMaskingS(
                            gfc, fftenergyS,
                            eb.AsSpan(chn * cbands, cbands), thr.AsSpan(chn * cbands, cbands),
                            chn, sblock, scratch);
                    }
                }
                if (cfg.Mode == MpegMode.JointStereo)
                {
                    if (uselongblock[0] + uselongblock[1] == 0)
                        VbrpsyComputeMsThresholds(
                            eb, thr, cd.S.MldCb!, gfc.Ath.CbS!, athFactor, cfg.Msfix, cd.S.Npart);
                }
                for (chn = 0; chn < nChnPsy; ++chn)
                {
                    int ch01 = chn & 0x01;
                    if (uselongblock[ch01] == 0 || forceShortBlockCalc != 0)
                        ConvertPartition2ScalefacS(
                            gfc, eb.AsSpan(chn * cbands, cbands),
                            thr.AsSpan(chn * cbands, cbands), chn, sblock);
                }
            }

            // short block pre-echo control
            for (chn = 0; chn < nChnPsy; chn++)
            {
                for (sb = 0; sb < LameConstants.SbMaxS; sb++)
                {
                    Float3 newThmmStorage = default;
                    Span<float> newThmm = newThmmStorage;
                    float prevThm, t1, t2;
                    for (sblock = 0; sblock < 3; sblock++)
                    {
                        thmm = psv.Thm![chn].S[sb * 3 + sblock];
                        thmm = (float)(thmm * LamePsyConstants.NsPreechoAtt0);

                        t1 = t2 = thmm;

                        if (sblock > 0)
                            prevThm = newThmm[sblock - 1];
                        else
                            prevThm = lastThm[chn].S[sb * 3 + 2];

                        if (nsAttacks[chn * 4 + sblock] >= 2 || nsAttacks[chn * 4 + sblock + 1] == 1)
                            t1 = NsInterp(prevThm, thmm,
                                          (float)(LamePsyConstants.NsPreechoAtt1 * pcfact));

                        thmm = Math.Min(t1, thmm);
                        if (nsAttacks[chn * 4 + sblock] == 1)
                        {
                            t2 = NsInterp(prevThm, thmm,
                                          (float)(LamePsyConstants.NsPreechoAtt2 * pcfact));
                        }
                        else if ((sblock == 0 && psv.LastAttacks[chn] == 3)
                              || (sblock > 0 && nsAttacks[chn * 4 + sblock - 1] == 3))
                        {
                            // 2nd preceeding block
                            switch (sblock)
                            {
                                case 0: prevThm = lastThm[chn].S[sb * 3 + 1]; break;
                                case 1: prevThm = lastThm[chn].S[sb * 3 + 2]; break;
                                case 2: prevThm = newThmm[0]; break;
                            }
                            t2 = NsInterp(prevThm, thmm,
                                          (float)(LamePsyConstants.NsPreechoAtt2 * pcfact));
                        }

                        thmm = Math.Min(t1, thmm);
                        thmm = Math.Min(t2, thmm);

                        // pulse like signal detection for fatboy.wav and so on
                        thmm *= subShortFactor[chn * 3 + sblock];

                        newThmm[sblock] = thmm;
                    }
                    for (sblock = 0; sblock < 3; sblock++)
                        psv.Thm![chn].S[sb * 3 + sblock] = newThmm[sblock];
                }
            }
        }
        for (chn = 0; chn < nChnPsy; chn++)
            psv.LastAttacks[chn] = nsAttacks[chn * 4 + 2];

        // determine final block type
        VbrpsyApplyBlockType(ref psv, cfg.ChannelsOut, uselongblock, blocktypeD);

        // compute the value of PE to return - no delay and advance
        for (chn = 0; chn < nChnPsy; chn++)
        {
            Span<float> ppe;
            int ppeIndex;
            int type;
            IiiPsyRatio mr;

            if (chn > 1)
            {
                // the C writes ppe = percep_MS_entropy - 2 and then indexes with chn
                ppe = percepMsEntropy;
                ppeIndex = chn - 2;
                type = LameBlockType.Norm;
                if (blocktypeD[0] == LameBlockType.Short || blocktypeD[1] == LameBlockType.Short)
                    type = LameBlockType.Short;
                mr = maskingMsRatio[grOut * 2 + chn - 2];
            }
            else
            {
                ppe = percepEntropy;
                ppeIndex = chn;
                type = blocktypeD[chn];
                mr = maskingRatio[grOut * 2 + chn];
            }
            if (type == LameBlockType.Short)
                ppe[ppeIndex] = PecalcS(mr, gfc.SvQnt.MaskingLower);
            else
                ppe[ppeIndex] = PecalcL(mr, gfc.SvQnt.MaskingLower);
        }
        return 0;
    }

    /// <summary><c>s3_func</c> - the spreading function, in units of energy.</summary>
    private static float S3Func(float bark)
    {
        double tempx, x, tempy, temp;
        tempx = bark;
        if (tempx >= 0)
            tempx *= 3;
        else
            tempx *= 1.5;

        if (tempx >= 0.5 && tempx <= 2.5)
        {
            temp = tempx - 0.5;
            x = 8.0 * (temp * temp - 2.0 * temp);
        }
        else
        {
            x = 0.0;
        }
        tempx += 0.474;
        tempy = 15.811389 + 7.5 * tempx - 17.5 * Math.Sqrt(1.0 + tempx * tempx);

        if (tempy <= -60.0) return 0.0f;

        tempx = Math.Exp((x + tempy) * LamePsyConstants.LnToLog10);

        // Normalised so that the integral of s3 over bark is 1.
        tempx /= .6609193;
        return (float)tempx;
    }

    /// <summary><c>stereo_demask</c> - reverse engineered from a plot in the paper.</summary>
    private static float StereoDemask(double f)
    {
        double arg = LameUtil.Freq2Bark((float)f);
        arg = Math.Min(arg, 15.5) / 15.5;
        return (float)Math.Pow(10.0, 1.25 * (1 - Math.Cos(Math.PI * arg)) - 2.5);
    }

    /// <summary><c>init_numline</c> - numlines, bo, bm, bo_weight and mld.</summary>
    private static void InitNumline(
        ref PsyConstCb2Sb gd, float sfreq, int fftSize, int mdctSize, int sbmax,
        ReadOnlySpan<int> scalepos)
    {
        var bFrq = new float[LameConstants.CBands + 1];
        float mdctFreqFrac = sfreq / (2.0f * mdctSize);
        float deltafreq = fftSize / (2.0f * mdctSize);
        var partition = new int[LameConstants.HBlkSize];
        int i, j, ni;
        int sfb;

        int[] numlines = gd.Numlines!;
        float[] rnumlines = gd.Rnumlines!;

        sfreq /= fftSize;
        j = 0;
        ni = 0;

        // Compute numlines, the number of spectral lines in each partition band; each band
        // should be about DELBARK wide.
        for (i = 0; i < LameConstants.CBands; i++)
        {
            float bark1;
            int j2, nl;
            bark1 = LameUtil.Freq2Bark(sfreq * j);

            bFrq[i] = sfreq * j;

            for (j2 = j;
                 LameUtil.Freq2Bark(sfreq * j2) - bark1 < LamePsyConstants.Delbark
                 && j2 <= fftSize / 2;
                 j2++)
            {
            }

            nl = j2 - j;
            numlines[i] = nl;
            rnumlines[i] = nl > 0 ? 1.0f / nl : 0;

            ni = i + 1;

            while (j < j2)
                partition[j++] = i;

            if (j > fftSize / 2)
            {
                j = fftSize / 2;
                ++i;
                break;
            }
        }
        bFrq[i] = sfreq * j;

        gd.NSb = sbmax;
        gd.Npart = ni;

        {
            float[] mldCb = gd.MldCb!;
            j = 0;
            for (i = 0; i < gd.Npart; i++)
            {
                int nl = numlines[i];
                float freq = sfreq * (j + nl / 2);
                mldCb[i] = StereoDemask(freq);
                j += nl;
            }
            for (; i < LameConstants.CBands; ++i)
                mldCb[i] = 1;
        }

        int[] bm = gd.Bm!;
        int[] bo = gd.Bo!;
        float[] boWeight = gd.BoWeight!;
        float[] mld = gd.Mld!;

        for (sfb = 0; sfb < sbmax; sfb++)
        {
            int i1, i2, boIdx;
            int start = scalepos[sfb];
            int end = scalepos[sfb + 1];

            i1 = (int)Math.Floor(.5 + deltafreq * (start - .5));
            if (i1 < 0) i1 = 0;
            i2 = (int)Math.Floor(.5 + deltafreq * (end - .5));

            if (i2 > fftSize / 2) i2 = fftSize / 2;

            boIdx = partition[i2];
            bm[sfb] = (partition[i1] + partition[i2]) / 2;
            bo[sfb] = boIdx;

            // How much of this band belongs to the current scalefactor band.
            {
                float fTmp = mdctFreqFrac * end;
                float boW = (fTmp - bFrq[boIdx]) / (bFrq[boIdx + 1] - bFrq[boIdx]);
                if (boW < 0) boW = 0;
                else if (boW > 1) boW = 1;
                boWeight[sfb] = boW;
            }
            mld[sfb] = StereoDemask(mdctFreqFrac * start);
        }
    }

    /// <summary><c>compute_bark_values</c>.</summary>
    private static void ComputeBarkValues(
        in PsyConstCb2Sb gd, float sfreq, int fftSize, Span<float> bval, Span<float> bvalWidth)
    {
        int k, j = 0, ni = gd.Npart;
        int[] numlines = gd.Numlines!;
        sfreq /= fftSize;
        for (k = 0; k < ni; k++)
        {
            int w = numlines[k];
            float bark1, bark2;

            bark1 = LameUtil.Freq2Bark(sfreq * j);
            bark2 = LameUtil.Freq2Bark(sfreq * (j + w - 1));
            bval[k] = (float)(.5 * (bark1 + bark2));

            bark1 = LameUtil.Freq2Bark((float)(sfreq * (j - .5)));
            bark2 = LameUtil.Freq2Bark((float)(sfreq * (j + w - .5)));
            bvalWidth[k] = bark2 - bark1;
            j += w;
        }
    }

    /// <summary><c>init_s3_values</c> - build the ragged spreading table and its index.</summary>
    /// <remarks>
    /// <c>s3</c> is allocated to exactly the number of non-zero entries, which is why the C
    /// cannot declare its bound; the two returned through <c>p</c> and <c>s3ind</c> are written
    /// into the band's own fields here.
    /// </remarks>
    private static int InitS3Values(
        ref PsyConstCb2Sb gd, int npart,
        ReadOnlySpan<float> bval, ReadOnlySpan<float> bvalWidth, ReadOnlySpan<float> norm)
    {
        const int cbands = LameConstants.CBands;
        var s3 = new float[cbands * cbands];
        int[] s3ind = gd.S3Ind!;
        int i, j, k;
        int numberOfNoneZero = 0;

        // s3[i][j] is the spreading function centred at band j (masker) for band i (maskee).
        // i and j are used the opposite way round from the ISO documents.
        for (i = 0; i < npart; i++)
        {
            for (j = 0; j < npart; j++)
            {
                float v = S3Func(bval[i] - bval[j]) * bvalWidth[j];
                s3[i * cbands + j] = v * norm[i];
            }
        }
        for (i = 0; i < npart; i++)
        {
            for (j = 0; j < npart; j++)
                if (s3[i * cbands + j] > 0.0f)
                    break;
            s3ind[i * 2 + 0] = j;

            for (j = npart - 1; j > 0; j--)
                if (s3[i * cbands + j] > 0.0f)
                    break;
            s3ind[i * 2 + 1] = j;
            numberOfNoneZero += s3ind[i * 2 + 1] - s3ind[i * 2 + 0] + 1;
        }

        gd.S3 = new float[numberOfNoneZero];

        k = 0;
        for (i = 0; i < npart; i++)
            for (j = s3ind[i * 2 + 0]; j <= s3ind[i * 2 + 1]; j++)
                gd.S3[k++] = s3[i * cbands + j];

        return 0;
    }

    /// <summary><c>psymodel_init</c>.</summary>
    public static int PsymodelInit(LameGlobalFlags gfp)
    {
        const int cbands = LameConstants.CBands;
        LameInternalFlags gfc = gfp.InternalFlags!;
        ref SessionConfig cfg = ref gfc.Cfg;
        ref PsyStateVar psv = ref gfc.SvPsy;
        PsyConstT gd;
        int i, j, b, sb, k;
        float bvlA = 13, bvlB = 24;
        float snrLA = 0, snrLB = 0;
        float snrSA = -8.25f, snrSB = -4.5f;

        var bval = new float[cbands];
        var bvalWidth = new float[cbands];
        var norm = new float[cbands];
        float sfreq = cfg.SamplerateOut;

        float xav = 10, xbv = 12;
        float minvalLow = 0.0f - cfg.Minval;

        if (gfc.CdPsy is not null)
            return 0;

        gd = new PsyConstT();
        gd.Allocate();
        gfc.CdPsy = gd;

        gd.ForceShortBlockCalc = gfp.ExperimentalZ;

        // the vbr header is long blocks
        psv.BlocktypeOld[0] = psv.BlocktypeOld[1] = LameBlockType.Norm;

        for (i = 0; i < 4; ++i)
        {
            for (j = 0; j < cbands; ++j)
            {
                psv.NbL1![i * cbands + j] = 1e20f;
                psv.NbL2![i * cbands + j] = 1e20f;
                psv.NbS1![i * cbands + j] = psv.NbS2![i * cbands + j] = 1.0f;
            }
            for (sb = 0; sb < LameConstants.SbMaxL; sb++)
            {
                psv.En![i].L[sb] = 1e20f;
                psv.Thm![i].L[sb] = 1e20f;
            }
            for (j = 0; j < 3; ++j)
            {
                for (sb = 0; sb < LameConstants.SbMaxS; sb++)
                {
                    psv.En![i].S[sb * 3 + j] = 1e20f;
                    psv.Thm![i].S[sb * 3 + j] = 1e20f;
                }
                psv.LastAttacks[i] = 0;
            }
            for (j = 0; j < 9; j++)
                psv.LastEnSubshort![i * 9 + j] = 10.0f;
        }

        // init. for loudness approx. -jd 2001 mar 27
        psv.LoudnessSqSave[0] = psv.LoudnessSqSave[1] = 0.0f;

        // Now compute the psychoacoustic model's own constants: numlines, bo, bm, bval,
        // bval_width and mld.
        InitNumline(ref gd.L, sfreq, LameConstants.BlkSize, 576, LameConstants.SbMaxL,
                    gfc.ScalefacBand.L);
        ComputeBarkValues(gd.L, sfreq, LameConstants.BlkSize, bval, bvalWidth);

        // the spreading function
        for (i = 0; i < gd.L.Npart; i++)
        {
            double snr = snrLA;
            if (bval[i] >= bvlA)
            {
                snr = snrLB * (bval[i] - bvlA) / (bvlB - bvlA)
                    + snrLA * (bvlB - bval[i]) / (bvlB - bvlA);
            }
            norm[i] = (float)Math.Pow(10.0, snr / 10.0);
        }
        i = InitS3Values(ref gd.L, gd.L.Npart, bval, bvalWidth, norm);
        if (i != 0) return i;

        // long block specific values, ATH and MINVAL
        j = 0;
        for (i = 0; i < gd.L.Npart; i++)
        {
            double x;

            // ATH
            x = float.MaxValue;
            for (k = 0; k < gd.L.Numlines![i]; k++, j++)
            {
                float freq = (float)(sfreq * j / (1000.0 * LameConstants.BlkSize));
                float level;
                // scale to FFT units; the returned value is in dB
                level = LameUtil.AthFormula(ref cfg, freq * 1000) - 20;
                level = (float)Math.Pow(10.0, 0.1 * level); // dB -> energy
                level *= gd.L.Numlines[i];
                if (x > level) x = level;
            }
            gfc.Ath.CbL![i] = (float)x;

            // MINVAL. For low frequencies the strength of the masking is limited by minval;
            // this is an ISO MPEG1 thing and it is not clear it is really needed. It does
            // reduce low-frequency problems in S53-Wind-Sax and lead-voice samples, at a cost
            // of some 3 kbps of bloat.
            x = 20.0 * (bval[i] / xav - 1.0);
            if (x > 6) x = 30;
            if (x < minvalLow) x = minvalLow;
            if (cfg.SamplerateOut < 44000) x = 30;
            x -= 8.0;
            gd.L.Minval![i] = (float)(Math.Pow(10.0, x / 10.0) * gd.L.Numlines[i]);
        }

        // the same things for short blocks
        InitNumline(ref gd.S, sfreq, LameConstants.BlkSizeS, 192, LameConstants.SbMaxS,
                    gfc.ScalefacBand.S);
        ComputeBarkValues(gd.S, sfreq, LameConstants.BlkSizeS, bval, bvalWidth);

        // SNR formula; the short block is normalised by SNR - is that still right?
        j = 0;
        for (i = 0; i < gd.S.Npart; i++)
        {
            double x;
            double snr = snrSA;
            if (bval[i] >= bvlA)
            {
                snr = snrSB * (bval[i] - bvlA) / (bvlB - bvlA)
                    + snrSA * (bvlB - bval[i]) / (bvlB - bvlA);
            }
            norm[i] = (float)Math.Pow(10.0, snr / 10.0);

            // ATH
            x = float.MaxValue;
            for (k = 0; k < gd.S.Numlines![i]; k++, j++)
            {
                float freq = (float)(sfreq * j / (1000.0 * LameConstants.BlkSizeS));
                float level;
                level = LameUtil.AthFormula(ref cfg, freq * 1000) - 20;
                level = (float)Math.Pow(10.0, 0.1 * level);
                level *= gd.S.Numlines[i];
                if (x > level) x = level;
            }
            gfc.Ath.CbS![i] = (float)x;

            // MINVAL
            x = 7.0 * (bval[i] / xbv - 1.0);
            if (bval[i] > xbv) x *= 1 + Math.Log(1 + x) * 3.1;
            if (bval[i] < xbv) x *= 1 + Math.Log(1 - x) * 2.3;
            if (x > 6) x = 30;
            if (x < minvalLow) x = minvalLow;
            if (cfg.SamplerateOut < 44000) x = 30;
            x -= 8;
            gd.S.Minval![i] = (float)(Math.Pow(10.0, x / 10) * gd.S.Numlines[i]);
        }

        i = InitS3Values(ref gd.S, gd.S.Npart, bval, bvalWidth, norm);
        if (i != 0) return i;

        // init_mask_add_max_values is an assert-only function in the C and has no body here.
        LameFft.InitFft(gfc);

        // temporal masking
        gd.Decay = (float)Math.Exp(-1.0 * LameUtil.Log10Constant
                   / (LamePsyConstants.TemporalmaskSustainSec * sfreq / 192.0));

        {
            float msfix;
            msfix = (float)LamePsyConstants.NsMsfix;
            if (cfg.UseSafeJointStereo != 0) msfix = 1.0f;
            if (Math.Abs(cfg.Msfix) > 0.0) msfix = cfg.Msfix;
            cfg.Msfix = msfix;

            // Spread only from npart_l bands. Normally the spreading function convolves from
            // npart_l down to npart_l bands.
            for (b = 0; b < gd.L.Npart; b++)
                if (gd.L.S3Ind![b * 2 + 1] > gd.L.Npart - 1)
                    gd.L.S3Ind[b * 2 + 1] = gd.L.Npart - 1;
        }

        // Prepare for ATH auto adjustment: decrease the ATH by 12 dB per second.
        {
            double frameDuration = 576.0 * cfg.ModeGr / sfreq;
            gfc.Ath.Decay = (float)Math.Pow(10.0, -12.0 / 10.0 * frameDuration);
            gfc.Ath.AdjustFactor = 0.01f; // minimum, for leading low loudness
            gfc.Ath.AdjustLimit = 1.0f;   // on lead, allow adjust up to maximum
        }

        if (cfg.AthType != -1)
        {
            // compute equal loudness weights (eql_w)
            float freq;
            float freqInc = cfg.SamplerateOut / (float)LameConstants.BlkSize;
            double eqlBalance = 0.0;
            float[] eqlW = gfc.Ath.EqlW!;
            freq = 0.0f;
            for (i = 0; i < LameConstants.BlkSize / 2; ++i)
            {
                // convert ATH dB to relative power (not dB) to determine eql_w
                freq += freqInc;
                eqlW[i] = (float)(1.0 / Math.Pow(10, LameUtil.AthFormula(ref cfg, freq) / 10));
                eqlBalance += eqlW[i];
            }
            eqlBalance = 1.0 / eqlBalance;
            for (i = LameConstants.BlkSize / 2; --i >= 0;)
                eqlW[i] = (float)(eqlW[i] * eqlBalance);
        }

        // short block attack threshold
        {
            float x = gfp.Attackthre;
            float y = gfp.AttackthreS;
            if (x < 0) x = LamePsyConstants.NsAttackThre;
            if (y < 0) y = LamePsyConstants.NsAttackThreS;
            gd.AttackThreshold![0] = gd.AttackThreshold[1] = gd.AttackThreshold[2] = x;
            gd.AttackThreshold[3] = y;
        }
        {
            float skS, skL;
            if (gfp.VbrQ < 4)
            {
                skL = skS = sk[0];
            }
            else
            {
                skL = skS = sk[gfp.VbrQ] + gfp.VbrQFrac * (sk[gfp.VbrQ] - sk[gfp.VbrQ + 1]);
            }
            b = 0;
            for (; b < gd.S.Npart; b++)
            {
                float m = (float)(gd.S.Npart - b) / gd.S.Npart;
                gd.S.MaskingLower![b] = MathF.Pow(10.0f, skS * m * 0.1f);
            }
            for (; b < cbands; ++b)
                gd.S.MaskingLower![b] = 1.0f;
            b = 0;
            for (; b < gd.L.Npart; b++)
            {
                float m = (float)(gd.L.Npart - b) / gd.L.Npart;
                gd.L.MaskingLower![b] = MathF.Pow(10.0f, skL * m * 0.1f);
            }
            for (; b < cbands; ++b)
                gd.L.MaskingLower![b] = 1.0f;
        }

        gd.LToS.CopyFrom(gd.L);
        InitNumline(ref gd.LToS, sfreq, LameConstants.BlkSize, 192, LameConstants.SbMaxS,
                    gfc.ScalefacBand.S);
        return 0;
    }

    // The C's small stack arrays. Inline array structs rather than stackalloc, because a method
    // that contains a localloc never tiers up past Tier0 and these sit on the per-granule path.

    /// <summary><c>FLOAT enn[SBMAX_s]</c> and <c>thm[SBMAX_s]</c>.</summary>
    [InlineArray(LameConstants.SbMaxS)] private struct FloatSbMaxS { private float element0; }

    /// <summary><c>FLOAT attack_intensity[12]</c> and <c>en_subshort[12]</c>.</summary>
    [InlineArray(12)] private struct Float12 { private float element0; }

    /// <summary><c>FLOAT en_short[4]</c>.</summary>
    [InlineArray(4)] private struct Float4 { private float element0; }

    /// <summary><c>FLOAT new_thmm[3]</c>.</summary>
    [InlineArray(3)] private struct Float3 { private float element0; }
}

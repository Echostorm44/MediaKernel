// Translated from lame-3.100/libmp3lame/quantize.c - the iteration loops that choose the
// quantiser step and the scalefactors, and the CBR, ABR and VBR drivers around them.
//
// VBR_new_iteration_loop is the one function here that is not yet translated: it calls
// VBR_encode_frame, which lives in vbrquantize.c. It stays todo in the port checklist
// until that file arrives, rather than being written against a method that does not exist.
//
// init_xrpow_core_init only chose between a C and an SSE kernel through a function pointer,
// and is recorded as diverged; init_xrpow_core_c is called directly.
//
// SCRATCH. outer_loop declares two 576-float arrays and an SFBMAX one as C stack locals, and
// is called once per granule per channel; VBR_encode_granule adds a whole gr_info and another
// 576 floats. They are gathered into LameQuantizeScratch, allocated once per encoder, for the
// same two reasons as LamePsyScratch: a large frame stops the JIT tiering the method up, and a
// per-granule allocation is a defect by this project's rules.

using System;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>encoder.h</c>'s <c>mode_ext</c> values.</summary>
internal static class LameModeExtValue
{
    /// <summary><c>MPG_MD_LR_LR</c>.</summary>
    public const int LrLr = 0;

    /// <summary><c>MPG_MD_LR_I</c>.</summary>
    public const int LrI = 1;

    /// <summary><c>MPG_MD_MS_LR</c>.</summary>
    public const int MsLr = 2;

    /// <summary><c>MPG_MD_MS_I</c>.</summary>
    public const int MsI = 3;
}

/// <summary>The C stack locals of the iteration loops, hoisted out. See the file header.</summary>
internal sealed class LameQuantizeScratch
{
    /// <summary><c>FLOAT xrpow[576]</c> of the CBR and ABR loops.</summary>
    public readonly float[] Xrpow = new float[576];

    /// <summary><c>FLOAT l3_xmin[SFBMAX]</c> of the CBR and ABR loops.</summary>
    public readonly float[] L3Xmin = new float[LameConstants.SfbMax];

    /// <summary><c>FLOAT save_xrpow[576]</c> of outer_loop.</summary>
    public readonly float[] SaveXrpow = new float[576];

    /// <summary><c>FLOAT distort[SFBMAX]</c> of outer_loop.</summary>
    public readonly float[] Distort = new float[LameConstants.SfbMax];

    /// <summary><c>FLOAT distort[SFBMAX]</c> of trancate_smallspectrums.</summary>
    public readonly float[] TruncateDistort = new float[LameConstants.SfbMax];

    /// <summary><c>FLOAT work[576]</c>, the sort buffer of trancate_smallspectrums.</summary>
    public readonly float[] Work = new float[576];

    /// <summary><c>FLOAT ixwork[576]</c> of init_outer_loop's short-block reordering.</summary>
    public readonly float[] Ixwork = new float[576];

    /// <summary><c>FLOAT bst_xrpow[576]</c> of VBR_encode_granule.</summary>
    public readonly float[] BstXrpow = new float[576];

    /// <summary><c>FLOAT l3_xmin[2][2][SFBMAX]</c> of the VBR loops.</summary>
    public readonly float[] VbrL3Xmin = new float[2 * 2 * LameConstants.SfbMax];

    /// <summary><c>FLOAT xrpow[576]</c> of VBR_old_iteration_loop.</summary>
    public readonly float[] VbrXrpow = new float[576];

    /// <summary><c>FLOAT xrpow[2][2][576]</c> of VBR_new_iteration_loop.</summary>
    public readonly float[] VbrNewXrpow = new float[4 * 576];

    /// <summary><c>sfwork_</c>, <c>vbrsfmin_</c> and <c>that_</c> of VBR_encode_frame.</summary>
    public readonly LameVbrScratch Vbr = new();

    /// <summary><c>int frameBits[15]</c>, declared as [16] where the C passes it that way.</summary>
    public readonly int[] FrameBits = new int[16];

    /// <summary><c>int min_bits[2][2]</c>, <c>max_bits[2][2]</c>, <c>bands[2][2]</c>.</summary>
    public readonly int[] MinBits = new int[4];

    /// <summary><c>int max_bits[2][2]</c>.</summary>
    public readonly int[] MaxBits = new int[4];

    /// <summary><c>int bands[2][2]</c>.</summary>
    public readonly int[] Bands = new int[4];

    /// <summary><c>int targ_bits[2][2]</c> of calc_target_bits.</summary>
    public readonly int[] TargBits2 = new int[4];

    /// <summary><c>calc_noise_data prev_noise</c> of outer_loop.</summary>
    public readonly CalcNoiseData PrevNoise = new();
}

/// <summary><c>quantize.c</c>.</summary>
internal static class LameQuantize
{
    /// <summary><c>EQ(a, b)</c> - <c>machine.h</c>'s relative float comparison.</summary>
    private static bool Eq(float a, float b) =>
        Math.Abs(a) > Math.Abs(b)
            ? Math.Abs(a - b) <= Math.Abs(a) * 1e-6f
            : Math.Abs(a - b) <= Math.Abs(b) * 1e-6f;

    /// <summary><c>NEQ(a, b)</c>.</summary>
    private static bool Neq(float a, float b) => !Eq(a, b);

    /// <summary><c>ms_convert</c> - L/R to mid/side.</summary>
    public static void MsConvert(ref IiiSideInfo l3Side, int gr)
    {
        for (int i = 0; i < 576; ++i)
        {
            float l, r;
            l = l3Side.Tt[gr * 2 + 0].Xr[i];
            r = l3Side.Tt[gr * 2 + 1].Xr[i];
            l3Side.Tt[gr * 2 + 0].Xr[i] = (l + r) * (float)(LameUtil.Sqrt2 * 0.5);
            l3Side.Tt[gr * 2 + 1].Xr[i] = (l - r) * (float)(LameUtil.Sqrt2 * 0.5);
        }
    }

    /// <summary><c>init_xrpow_core_c</c> - <c>|xr|^(3/4)</c>, and the sum of <c>|xr|</c>.</summary>
    private static void InitXrpowCoreC(
        ref GrInfo codInfo, Span<float> xrpow, int upper, out float sum)
    {
        float tmp;
        sum = 0;
        for (int i = 0; i <= upper; ++i)
        {
            tmp = Math.Abs(codInfo.Xr[i]);
            sum += tmp;
            xrpow[i] = (float)Math.Sqrt(tmp * Math.Sqrt(tmp));

            if (xrpow[i] > codInfo.XrpowMax)
                codInfo.XrpowMax = xrpow[i];
        }
    }

    /// <summary>
    /// <c>init_xrpow_core_sse</c> (<c>vector/xmm_quantize_sub.c</c>) - the path the reference
    /// build takes.
    /// </summary>
    /// <remarks>
    /// Translated as scalar float rather than as <c>Vector128</c>: <c>_mm_mul_ps</c> and
    /// <c>_mm_sqrt_ps</c> are lane-wise IEEE operations, so four scalar floats give bit-identical
    /// results, and the loop is not hot enough to pay for the shuffles.
    ///
    /// Two things here are the vector code's behaviour rather than the C's, and both are
    /// deliberate:
    /// <list type="bullet">
    /// <item>the whole chain is float, where <c>init_xrpow_core_c</c> evaluates
    /// <c>sqrt(tmp * sqrt(tmp))</c> in double and narrows once;</item>
    /// <item>it writes <c>xrpow[0 .. upper-1]</c>, while the C loop runs <c>i &lt;= upper</c>.
    /// The element at <c>upper</c> keeps the zero <c>init_xrpow</c> left there. That is a bug
    /// in LAME, and it is almost always invisible because <c>max_nonzero_coeff</c> is rounded
    /// up to an odd index, one past the last coefficient above 1e-12.</item>
    /// </list>
    /// </remarks>
    private static void InitXrpowCoreSse(
        ref GrInfo codInfo, Span<float> xrpow, int upper, out float sum)
    {
        int upper4 = upper / 4 * 4;
        int rest = upper - upper4;

        // Inline arrays rather than stackalloc: a method with localloc never reaches Tier1.
        Float4 maxLanes = default, sumLanes = default, tmpLanes = default;
        Span<float> vecXrpowMax = maxLanes;
        Span<float> vecSum = sumLanes;
        Span<float> vecTmp = tmpLanes;

        for (int i = 0; i < upper4; i += 4)
            for (int k = 0; k < 4; k++)
            {
                float t = Math.Abs(codInfo.Xr[i + k]);
                vecSum[k] += t;
                t = (float)Math.Sqrt((float)Math.Sqrt(t) * t);
                if (t > vecXrpowMax[k]) vecXrpowMax[k] = t;
                xrpow[i + k] = t;
            }

        if (rest > 0)
        {
            vecTmp.Clear();
            for (int k = 0; k < rest; k++) vecTmp[k] = codInfo.Xr[upper4 + k];
            for (int k = 0; k < 4; k++)
            {
                float t = Math.Abs(vecTmp[k]);
                vecSum[k] += t;
                t = (float)Math.Sqrt((float)Math.Sqrt(t) * t);
                if (t > vecXrpowMax[k]) vecXrpowMax[k] = t;
                vecTmp[k] = t;
            }
            for (int k = 0; k < rest; k++) xrpow[upper4 + k] = vecTmp[k];
        }

        sum = vecSum[0] + vecSum[1] + vecSum[2] + vecSum[3];

        float ma = vecXrpowMax[0] > vecXrpowMax[1] ? vecXrpowMax[0] : vecXrpowMax[1];
        float mb = vecXrpowMax[2] > vecXrpowMax[3] ? vecXrpowMax[2] : vecXrpowMax[3];
        codInfo.XrpowMax = ma > mb ? ma : mb;
    }

    /// <summary><c>init_xrpow</c>.</summary>
    /// <returns>1 if there is energy to quantise, 0 if the granule is silent.</returns>
    private static int InitXrpow(LameInternalFlags gfc, ref GrInfo codInfo, Span<float> xrpow)
    {
        float sum;
        int i;
        int upper = codInfo.MaxNonzeroCoeff;

        codInfo.XrpowMax = 0;

        xrpow[upper..576].Clear();

        // init_xrpow_core_init picks the implementation once; see HaveSseXrpow.
        if (LameQuantizeConstants.HaveSseXrpow)
            InitXrpowCoreSse(ref codInfo, xrpow, upper, out sum);
        else
            InitXrpowCoreC(ref codInfo, xrpow, upper, out sum);

        if (sum > 1E-20f)
        {
            int j = 0;
            if ((gfc.SvQnt.SubstepShaping & 2) != 0) j = 1;

            for (i = 0; i < codInfo.Psymax; i++)
                gfc.SvQnt.Pseudohalf![i] = j;

            return 1;
        }

        ((Span<int>)codInfo.L3Enc).Clear();
        return 0;
    }

    /// <summary>
    /// <c>psfb21_analogsilence</c> - Gabriel Bouvigne 2003, zero the coefficients below the
    /// ATH from the top of sfb21 (or sfb12 for short blocks) down to the first one above it.
    /// </summary>
    private static void Psfb21AnalogSilence(LameInternalFlags gfc, ref GrInfo codInfo)
    {
        Span<float> xr = codInfo.Xr;

        if (codInfo.BlockType != LameBlockType.Short)
        {
            bool stop = false;
            for (int gsfb = LameConstants.Psfb21 - 1; gsfb >= 0 && !stop; gsfb--)
            {
                int start = gfc.ScalefacBand.Psfb21[gsfb];
                int end = gfc.ScalefacBand.Psfb21[gsfb + 1];
                float ath21 = LameQuantizePvt.AthAdjust(
                    gfc.Ath.AdjustFactor, gfc.Ath.Psfb21![gsfb], gfc.Ath.Floor, 0);

                if (gfc.SvQnt.Longfact![21] > 1e-12f)
                    ath21 *= gfc.SvQnt.Longfact[21];

                for (int j = end - 1; j >= start; j--)
                {
                    if (Math.Abs(xr[j]) < ath21)
                    {
                        xr[j] = 0;
                    }
                    else
                    {
                        stop = true;
                        break;
                    }
                }
            }
        }
        else
        {
            // short block coefficients are reordered
            for (int block = 0; block < 3; block++)
            {
                bool stop = false;
                for (int gsfb = LameConstants.Psfb12 - 1; gsfb >= 0 && !stop; gsfb--)
                {
                    int start = gfc.ScalefacBand.S[12] * 3
                              + (gfc.ScalefacBand.S[13] - gfc.ScalefacBand.S[12]) * block
                              + (gfc.ScalefacBand.Psfb12[gsfb] - gfc.ScalefacBand.Psfb12[0]);
                    int end = start
                            + (gfc.ScalefacBand.Psfb12[gsfb + 1] - gfc.ScalefacBand.Psfb12[gsfb]);
                    float ath12 = LameQuantizePvt.AthAdjust(
                        gfc.Ath.AdjustFactor, gfc.Ath.Psfb12![gsfb], gfc.Ath.Floor, 0);

                    if (gfc.SvQnt.Shortfact![12] > 1e-12f)
                        ath12 *= gfc.SvQnt.Shortfact[12];

                    for (int j = end - 1; j >= start; j--)
                    {
                        if (Math.Abs(xr[j]) < ath12)
                        {
                            xr[j] = 0;
                        }
                        else
                        {
                            stop = true;
                            break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// <c>init_outer_loop</c> - reset <c>cod_info</c>, the scalefactors and the band widths.
    /// </summary>
    /// <remarks>
    /// For a short block this also reorders the spectrum, which Takehiro Tominaga added:
    /// within a scalefactor band the data arrives window by window, and the encoder wants all
    /// of window 0's lines together, then window 1's, then window 2's.
    /// </remarks>
    public static void InitOuterLoop(LameInternalFlags gfc, ref GrInfo codInfo)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        int sfb, j;

        codInfo.Part23Length = 0;
        codInfo.BigValues = 0;
        codInfo.Count1 = 0;
        codInfo.GlobalGain = 210;
        codInfo.ScalefacCompress = 0;
        // mixed_block_flag and block_type were set in psymodel.c
        codInfo.TableSelect[0] = 0;
        codInfo.TableSelect[1] = 0;
        codInfo.TableSelect[2] = 0;
        codInfo.SubblockGain[0] = 0;
        codInfo.SubblockGain[1] = 0;
        codInfo.SubblockGain[2] = 0;
        codInfo.SubblockGain[3] = 0;    // this one is always 0
        codInfo.Region0Count = 0;
        codInfo.Region1Count = 0;
        codInfo.Preflag = 0;
        codInfo.ScalefacScale = 0;
        codInfo.Count1TableSelect = 0;
        codInfo.Part2Length = 0;
        if (cfg.SamplerateOut <= 8000)
        {
            codInfo.SfbLmax = 17;
            codInfo.SfbSmin = 9;
            codInfo.PsyLmax = 17;
        }
        else
        {
            codInfo.SfbLmax = LameConstants.SbPsyL;
            codInfo.SfbSmin = LameConstants.SbPsyS;
            codInfo.PsyLmax = gfc.SvQnt.Sfb21Extra != 0
                ? LameConstants.SbMaxL
                : LameConstants.SbPsyL;
        }
        codInfo.Psymax = codInfo.PsyLmax;
        codInfo.Sfbmax = codInfo.SfbLmax;
        codInfo.Sfbdivide = 11;
        for (sfb = 0; sfb < LameConstants.SbMaxL; sfb++)
        {
            codInfo.Width[sfb] = gfc.ScalefacBand.L[sfb + 1] - gfc.ScalefacBand.L[sfb];
            codInfo.Window[sfb] = 3;    // which is always 0
        }
        if (codInfo.BlockType == LameBlockType.Short)
        {
            float[] ixwork = gfc.QuantizeScratch.Ixwork;

            codInfo.SfbSmin = 0;
            codInfo.SfbLmax = 0;
            if (codInfo.MixedBlockFlag != 0)
            {
                //  MPEG-1:     sfbs 0-7 long block, 3-12 short blocks
                //  MPEG-2(.5): sfbs 0-5 long block, 3-12 short blocks
                codInfo.SfbSmin = 3;
                codInfo.SfbLmax = cfg.ModeGr * 2 + 4;
            }
            if (cfg.SamplerateOut <= 8000)
            {
                codInfo.Psymax = codInfo.SfbLmax + 3 * (9 - codInfo.SfbSmin);
                codInfo.Sfbmax = codInfo.SfbLmax + 3 * (9 - codInfo.SfbSmin);
            }
            else
            {
                codInfo.Psymax = codInfo.SfbLmax
                    + 3 * ((gfc.SvQnt.Sfb21Extra != 0
                            ? LameConstants.SbMaxS
                            : LameConstants.SbPsyS) - codInfo.SfbSmin);
                codInfo.Sfbmax = codInfo.SfbLmax
                    + 3 * (LameConstants.SbPsyS - codInfo.SfbSmin);
            }
            codInfo.Sfbdivide = codInfo.Sfbmax - 18;
            codInfo.PsyLmax = codInfo.SfbLmax;

            // Re-order the short blocks, by Takehiro Tominaga.
            int ix = gfc.ScalefacBand.L[codInfo.SfbLmax];
            ((ReadOnlySpan<float>)codInfo.Xr).CopyTo(ixwork);
            for (sfb = codInfo.SfbSmin; sfb < LameConstants.SbMaxS; sfb++)
            {
                int start = gfc.ScalefacBand.S[sfb];
                int end = gfc.ScalefacBand.S[sfb + 1];
                for (int window = 0; window < 3; window++)
                    for (int l = start; l < end; l++)
                        codInfo.Xr[ix++] = ixwork[3 * l + window];
            }

            j = codInfo.SfbLmax;
            for (sfb = codInfo.SfbSmin; sfb < LameConstants.SbMaxS; sfb++)
            {
                codInfo.Width[j] = codInfo.Width[j + 1] = codInfo.Width[j + 2]
                    = gfc.ScalefacBand.S[sfb + 1] - gfc.ScalefacBand.S[sfb];
                codInfo.Window[j] = 0;
                codInfo.Window[j + 1] = 1;
                codInfo.Window[j + 2] = 2;
                j += 3;
            }
        }

        codInfo.Count1Bits = 0;
        codInfo.SfbPartitionTableRow = 0 * 3 + 0;   // nr_of_sfb_block[0][0]
        codInfo.Slen[0] = 0;
        codInfo.Slen[1] = 0;
        codInfo.Slen[2] = 0;
        codInfo.Slen[3] = 0;

        codInfo.MaxNonzeroCoeff = 575;

        ((Span<int>)codInfo.Scalefac).Clear();

        if (cfg.Vbr != VbrMode.Mt && cfg.Vbr != VbrMode.Mtrh
            && cfg.Vbr != VbrMode.Abr && cfg.Vbr != VbrMode.Off)
        {
            Psfb21AnalogSilence(gfc, ref codInfo);
        }
    }

    /// <summary><c>binsearchDirection_t</c>.</summary>
    private enum BinsearchDirection { None, Up, Down }

    /// <summary><c>bin_search_StepSize</c> - a starting quantiser step for the outer loop.</summary>
    private static int BinSearchStepSize(
        LameInternalFlags gfc, ref GrInfo codInfo, int desiredRate, int ch,
        ReadOnlySpan<float> xrpow)
    {
        int nBits;
        int currentStep = gfc.SvQnt.CurrentStep[ch];
        int flagGoneOver = 0;
        int start = gfc.SvQnt.OldValue[ch];
        BinsearchDirection direction = BinsearchDirection.None;
        codInfo.GlobalGain = start;
        desiredRate -= codInfo.Part2Length;

        for (; ; )
        {
            int step;
            nBits = LameTakehiro.CountBits(gfc, xrpow, ref codInfo, null);

            if (currentStep == 1 || nBits == desiredRate)
                break;      // nothing to adjust any more

            if (nBits > desiredRate)
            {
                // increase the quantiser step size
                if (direction == BinsearchDirection.Down) flagGoneOver = 1;
                if (flagGoneOver != 0) currentStep /= 2;
                direction = BinsearchDirection.Up;
                step = currentStep;
            }
            else
            {
                // decrease the quantiser step size
                if (direction == BinsearchDirection.Up) flagGoneOver = 1;
                if (flagGoneOver != 0) currentStep /= 2;
                direction = BinsearchDirection.Down;
                step = -currentStep;
            }
            codInfo.GlobalGain += step;
            if (codInfo.GlobalGain < 0)
            {
                codInfo.GlobalGain = 0;
                flagGoneOver = 1;
            }
            if (codInfo.GlobalGain > 255)
            {
                codInfo.GlobalGain = 255;
                flagGoneOver = 1;
            }
        }

        while (nBits > desiredRate && codInfo.GlobalGain < 255)
        {
            codInfo.GlobalGain++;
            nBits = LameTakehiro.CountBits(gfc, xrpow, ref codInfo, null);
        }
        gfc.SvQnt.CurrentStep[ch] = start - codInfo.GlobalGain >= 4 ? 4 : 2;
        gfc.SvQnt.OldValue[ch] = codInfo.GlobalGain;
        codInfo.Part23Length = nBits;
        return nBits;
    }

    /// <summary>
    /// <c>trancate_smallspectrums</c> - Takehiro Tominaga 2002, zero the smallest values in a
    /// band while the noise budget allows.
    /// </summary>
    /// <remarks>
    /// The C's <c>qsort</c> with <c>floatcompare</c> is <see cref="Array.Sort(float[], int, int)"/>
    /// here. That is a recorded difference in kind rather than in result: both leave the band's
    /// magnitudes in ascending order, which is all the algorithm reads, and the values compared
    /// are distinguished only by the <c>NEQ</c> run detection below.
    /// </remarks>
    private static void TrancateSmallspectrums(
        LameInternalFlags gfc, ref GrInfo gi, ReadOnlySpan<float> l3Xmin, Span<float> work)
    {
        int sfb, j, width;
        Span<float> distort = gfc.QuantizeScratch.TruncateDistort;
        CalcNoiseResult dummy = default;

        if ((((gfc.SvQnt.SubstepShaping & 4) == 0) && gi.BlockType == LameBlockType.Short)
            || (gfc.SvQnt.SubstepShaping & 0x80) != 0)
            return;

        LameQuantizePvt.CalcNoise(gi, l3Xmin, distort, ref dummy, null);
        for (j = 0; j < 576; j++)
        {
            float xr = 0.0f;
            if (gi.L3Enc[j] != 0) xr = Math.Abs(gi.Xr[j]);
            work[j] = xr;
        }

        j = 0;
        sfb = 8;
        if (gi.BlockType == LameBlockType.Short) sfb = 6;
        do
        {
            float allowedNoise, trancateThreshold;
            int nsame, start;

            width = gi.Width[sfb];
            j += width;
            if (distort[sfb] >= 1.0f) continue;

            work.Slice(j - width, width).Sort();
            if (Eq(work[j - 1], 0.0f)) continue;    // all zero sfb

            allowedNoise = (1.0f - distort[sfb]) * l3Xmin[sfb];
            trancateThreshold = 0.0f;
            start = 0;
            do
            {
                float noise;
                for (nsame = 1; start + nsame < width; nsame++)
                    if (Neq(work[start + j - width], work[start + j + nsame - width]))
                        break;

                noise = work[start + j - width] * work[start + j - width] * nsame;
                if (allowedNoise < noise)
                {
                    if (start != 0) trancateThreshold = work[start + j - width - 1];
                    break;
                }
                allowedNoise -= noise;
                start += nsame;
            } while (start < width);
            if (Eq(trancateThreshold, 0.0f)) continue;

            do
            {
                if (Math.Abs(gi.Xr[j - width]) <= trancateThreshold)
                    gi.L3Enc[j - width] = 0;
            } while (--width > 0);
        } while (++sfb < gi.Psymax);

        gi.Part23Length = LameTakehiro.NoquantCountBits(gfc, ref gi, null);
    }

    /// <summary>
    /// <c>loop_break</c> - zero if some scalefactor is still unamplified, one if all are.
    /// </summary>
    private static int LoopBreak(in GrInfo codInfo)
    {
        for (int sfb = 0; sfb < codInfo.Sfbmax; sfb++)
            if (codInfo.Scalefac[sfb] + codInfo.SubblockGain[codInfo.Window[sfb]] == 0)
                return 0;

        return 1;
    }

    /// <summary><c>penalties</c>.</summary>
    private static double Penalties(double noise)
        => LameUtil.FastLog10((float)(0.368 + 0.632 * noise * noise * noise));

    /// <summary><c>get_klemm_noise</c>.</summary>
    private static double GetKlemmNoise(ReadOnlySpan<float> distort, in GrInfo gi)
    {
        double klemmNoise = 1E-37;
        for (int sfb = 0; sfb < gi.Psymax; sfb++)
            klemmNoise += Penalties(distort[sfb]);

        return Math.Max(1e-20, klemmNoise);
    }

    /// <summary>
    /// <c>quant_compare</c> - is this quantisation better than the best one so far?
    /// </summary>
    /// <remarks>
    /// Noise is in decibels relative to the masking threshold. Case 8 falls through into case
    /// 1 in the C after replacing <c>max_noise</c>; C# has no fallthrough, so that assignment
    /// is made and both labels share the same arm.
    /// </remarks>
    /// <remarks>
    /// Public rather than private only so that <c>mp3enc-quantize</c> can drive it: it takes a
    /// span, which reflection cannot pass. It is <c>inline static</c> in the C.
    /// </remarks>
    public static bool QuantCompare(
        int quantComp, in CalcNoiseResult best, ref CalcNoiseResult calc,
        in GrInfo gi, ReadOnlySpan<float> distort)
    {
        bool better;

        switch (quantComp)
        {
            case 0:
                better = calc.OverCount < best.OverCount
                    || (calc.OverCount == best.OverCount && calc.OverNoise < best.OverNoise)
                    || (calc.OverCount == best.OverCount
                        && Eq(calc.OverNoise, best.OverNoise)
                        && calc.TotNoise < best.TotNoise);
                break;

            case 8:
                calc.MaxNoise = (float)GetKlemmNoise(distort, gi);
                goto case 1;
            case 1:
                better = calc.MaxNoise < best.MaxNoise;
                break;
            case 2:
                better = calc.TotNoise < best.TotNoise;
                break;
            case 3:
                better = calc.TotNoise < best.TotNoise && calc.MaxNoise < best.MaxNoise;
                break;
            case 4:
                better = (calc.MaxNoise <= 0.0f && best.MaxNoise > 0.2f)
                    || (calc.MaxNoise <= 0.0f
                        && best.MaxNoise < 0.0f
                        && best.MaxNoise > calc.MaxNoise - 0.2f
                        && calc.TotNoise < best.TotNoise)
                    || (calc.MaxNoise <= 0.0f
                        && best.MaxNoise > 0.0f
                        && best.MaxNoise > calc.MaxNoise - 0.2f
                        && calc.TotNoise < best.TotNoise + best.OverNoise)
                    || (calc.MaxNoise > 0.0f
                        && best.MaxNoise > -0.05f
                        && best.MaxNoise > calc.MaxNoise - 0.1f
                        && calc.TotNoise + calc.OverNoise < best.TotNoise + best.OverNoise)
                    || (calc.MaxNoise > 0.0f
                        && best.MaxNoise > -0.1f
                        && best.MaxNoise > calc.MaxNoise - 0.15f
                        && calc.TotNoise + calc.OverNoise + calc.OverNoise
                           < best.TotNoise + best.OverNoise + best.OverNoise);
                break;
            case 5:
                better = calc.OverNoise < best.OverNoise
                    || (Eq(calc.OverNoise, best.OverNoise) && calc.TotNoise < best.TotNoise);
                break;
            case 6:
                better = calc.OverNoise < best.OverNoise
                    || (Eq(calc.OverNoise, best.OverNoise)
                        && (calc.MaxNoise < best.MaxNoise
                            || (Eq(calc.MaxNoise, best.MaxNoise)
                                && calc.TotNoise <= best.TotNoise)));
                break;
            case 7:
                better = calc.OverCount < best.OverCount || calc.OverNoise < best.OverNoise;
                break;
            default:
            case 9:
                if (best.OverCount > 0)
                {
                    // there are distorted scalefactor bands
                    better = calc.OverSsd <= best.OverSsd;
                    if (calc.OverSsd == best.OverSsd)
                        better = calc.Bits < best.Bits;
                }
                else
                {
                    better = calc.MaxNoise < 0
                          && calc.MaxNoise * 10 + calc.Bits <= best.MaxNoise * 10 + best.Bits;
                }
                break;
        }

        if (best.OverCount == 0)
        {
            // With no distorted bands, only take this quantisation if it is better AND uses
            // fewer bits. part2_3_length is sometimes a poor estimate at low bitrates.
            better = better && calc.Bits < best.Bits;
        }

        return better;
    }

    /// <summary>
    /// <c>amp_scalefac_bands</c> - amplify the bands that break the masking threshold,
    /// ISO 11172-3 C.1.5.4.3.5.
    /// </summary>
    /// <remarks>
    /// Three algorithms, selected by <c>noise_shaping_amp</c>: 0 amplifies every band with
    /// distortion above 1 (the ISO one), 1 everything within 50% of the maximum on the dB
    /// scale, 2 exactly one band. For 0 and 1, if the maximum is below 1 the trigger drops to
    /// 95% of it so that at least one band is always amplified.
    /// </remarks>
    private static void AmpScalefacBands(
        LameInternalFlags gfc, ref GrInfo codInfo, ReadOnlySpan<float> distort,
        Span<float> xrpow, int bRefine)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        int j, sfb;
        float ifqstep34, trigger;
        int noiseShapingAmp;

        if (codInfo.ScalefacScale == 0)
            ifqstep34 = 1.29683955465100964055f;    // 2^(0.75 * 0.5)
        else
            ifqstep34 = 1.68179283050742922612f;    // 2^(0.75 * 1)

        trigger = 0;
        for (sfb = 0; sfb < codInfo.Sfbmax; sfb++)
            if (trigger < distort[sfb])
                trigger = distort[sfb];

        noiseShapingAmp = cfg.NoiseShapingAmp;
        if (noiseShapingAmp == 3)
            noiseShapingAmp = bRefine == 1 ? 2 : 1;

        switch (noiseShapingAmp)
        {
            case 2:
                // amplify exactly one band
                break;

            case 1:
                // amplify bands within 50% of the maximum, on the dB scale
                if (trigger > 1.0f)
                    trigger = (float)Math.Pow(trigger, .5);
                else
                    trigger = (float)(trigger * .95);
                break;

            case 0:
            default:
                // the ISO algorithm: amplify every band with distortion above 1
                if (trigger > 1.0f)
                    trigger = 1.0f;
                else
                    trigger = (float)(trigger * .95);
                break;
        }

        j = 0;
        for (sfb = 0; sfb < codInfo.Sfbmax; sfb++)
        {
            int width = codInfo.Width[sfb];
            int l;
            j += width;
            if (distort[sfb] < trigger) continue;

            if ((gfc.SvQnt.SubstepShaping & 2) != 0)
            {
                gfc.SvQnt.Pseudohalf![sfb] = gfc.SvQnt.Pseudohalf[sfb] != 0 ? 0 : 1;
                if (gfc.SvQnt.Pseudohalf[sfb] == 0 && cfg.NoiseShapingAmp == 2)
                    return;
            }
            codInfo.Scalefac[sfb]++;
            for (l = -width; l < 0; l++)
            {
                xrpow[j + l] *= ifqstep34;
                if (xrpow[j + l] > codInfo.XrpowMax)
                    codInfo.XrpowMax = xrpow[j + l];
            }

            if (cfg.NoiseShapingAmp == 2) return;
        }
    }

    /// <summary><c>inc_scalefac_scale</c> - Takehiro Tominaga, turn on scalefac_scale.</summary>
    private static void IncScalefacScale(ref GrInfo codInfo, Span<float> xrpow)
    {
        int l, j, sfb;
        const float ifqstep34 = 1.29683955465100964055f;

        j = 0;
        for (sfb = 0; sfb < codInfo.Sfbmax; sfb++)
        {
            int width = codInfo.Width[sfb];
            int s = codInfo.Scalefac[sfb];
            if (codInfo.Preflag != 0) s += LameQuantizePvt.Pretab[sfb];
            j += width;
            if ((s & 1) != 0)
            {
                s++;
                for (l = -width; l < 0; l++)
                {
                    xrpow[j + l] *= ifqstep34;
                    if (xrpow[j + l] > codInfo.XrpowMax)
                        codInfo.XrpowMax = xrpow[j + l];
                }
            }
            codInfo.Scalefac[sfb] = s >> 1;
        }
        codInfo.Preflag = 0;
        codInfo.ScalefacScale = 1;
    }

    /// <summary><c>inc_subblock_gain</c> - Takehiro Tominaga.</summary>
    private static int IncSubblockGain(
        LameInternalFlags gfc, ref GrInfo codInfo, Span<float> xrpow)
    {
        int sfb, window;

        // subblock_gain can do nothing in the long block region
        for (sfb = 0; sfb < codInfo.SfbLmax; sfb++)
            if (codInfo.Scalefac[sfb] >= 16)
                return 1;

        for (window = 0; window < 3; window++)
        {
            int s1, s2, l, j;
            s1 = s2 = 0;

            for (sfb = codInfo.SfbLmax + window; sfb < codInfo.Sfbdivide; sfb += 3)
                if (s1 < codInfo.Scalefac[sfb])
                    s1 = codInfo.Scalefac[sfb];

            for (; sfb < codInfo.Sfbmax; sfb += 3)
                if (s2 < codInfo.Scalefac[sfb])
                    s2 = codInfo.Scalefac[sfb];

            if (s1 < 16 && s2 < 8) continue;

            if (codInfo.SubblockGain[window] >= 7) return 1;

            // Even though sfb12 has no scalefactor, subblock gain affects the upper
            // frequencies too, which is why this runs up to SBMAX_s.
            codInfo.SubblockGain[window]++;
            j = gfc.ScalefacBand.L[codInfo.SfbLmax];
            for (sfb = codInfo.SfbLmax + window; sfb < codInfo.Sfbmax; sfb += 3)
            {
                float amp;
                int width = codInfo.Width[sfb];
                int s = codInfo.Scalefac[sfb];
                s -= 4 >> codInfo.ScalefacScale;
                if (s >= 0)
                {
                    codInfo.Scalefac[sfb] = s;
                    j += width * 3;
                    continue;
                }

                codInfo.Scalefac[sfb] = 0;
                {
                    int gain = 210 + (s << (codInfo.ScalefacScale + 1));
                    amp = LameQuantizePvt.Ipow20Of(gain);
                }
                j += width * (window + 1);
                for (l = -width; l < 0; l++)
                {
                    xrpow[j + l] *= amp;
                    if (xrpow[j + l] > codInfo.XrpowMax)
                        codInfo.XrpowMax = xrpow[j + l];
                }
                j += width * (3 - window - 1);
            }

            {
                float amp = LameQuantizePvt.Ipow20Of(202);
                j += codInfo.Width[sfb] * (window + 1);
                for (l = -codInfo.Width[sfb]; l < 0; l++)
                {
                    xrpow[j + l] *= amp;
                    if (xrpow[j + l] > codInfo.XrpowMax)
                        codInfo.XrpowMax = xrpow[j + l];
                }
            }
        }
        return 0;
    }

    /// <summary>
    /// <c>balance_noise</c> - amplify the bands, and back off if that went too far.
    /// </summary>
    /// <returns>0 if every band is already amplified or the scalefactors will not fit.</returns>
    private static int BalanceNoise(
        LameInternalFlags gfc, ref GrInfo codInfo, ReadOnlySpan<float> distort,
        Span<float> xrpow, int bRefine)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        int status;

        AmpScalefacBands(gfc, ref codInfo, distort, xrpow, bRefine);

        // loop_break returns 0 if some scalefactor is unamplified; scale_bitcount returns 0 if
        // no scalefactor is too large.
        status = LoopBreak(codInfo);

        if (status != 0) return 0;      // all bands amplified

        status = LameTakehiro.ScaleBitcount(gfc, ref codInfo);

        if (status == 0) return 1;      // amplified some bands without exceeding the limits

        // Some scalefactors are too large; try setting scalefac_scale.
        if (cfg.NoiseShaping > 1)
        {
            Array.Clear(gfc.SvQnt.Pseudohalf!);
            if (codInfo.ScalefacScale == 0)
            {
                IncScalefacScale(ref codInfo, xrpow);
                status = 0;
            }
            else
            {
                if (codInfo.BlockType == LameBlockType.Short && cfg.SubblockGain > 0)
                {
                    status = IncSubblockGain(gfc, ref codInfo, xrpow) != 0
                          || LoopBreak(codInfo) != 0 ? 1 : 0;
                }
            }
        }

        if (status == 0)
            status = LameTakehiro.ScaleBitcount(gfc, ref codInfo);

        return status == 0 ? 1 : 0;
    }

    /// <summary>
    /// <c>outer_loop</c> - the masking conditions of every scalefactor band, and so the best
    /// scalefactors and global gain.
    /// </summary>
    /// <returns><c>over_count</c> of the best quantisation found.</returns>
    public static int OuterLoop(
        LameInternalFlags gfc, ref GrInfo codInfo, ReadOnlySpan<float> l3Xmin,
        Span<float> xrpow, int ch, int targBits)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        LameQuantizeScratch scratch = gfc.QuantizeScratch;
        GrInfo codInfoW;
        Span<float> saveXrpow = scratch.SaveXrpow;
        Span<float> distort = scratch.Distort;
        CalcNoiseResult bestNoiseInfo = default;
        int huffBits;
        bool better;
        int age;
        CalcNoiseData prevNoise = scratch.PrevNoise;
        int bestPart23Length = 9999999;
        bool bEndOfSearch = false;
        int bRefine = 0;
        int bestGgainPass1 = 0;

        BinSearchStepSize(gfc, ref codInfo, targBits, ch, xrpow);

        if (cfg.NoiseShaping == 0)
            return 100;     // fast mode, no noise shaping; 100 is the default over_count

        Array.Clear(prevNoise.Step);
        Array.Clear(prevNoise.Noise);
        Array.Clear(prevNoise.NoiseLog);
        prevNoise.GlobalGain = 0;
        prevNoise.SfbCount1 = 0;

        // the distortion of this quantisation, coefficients and thresholds both L/R or both M/S
        LameQuantizePvt.CalcNoise(codInfo, l3Xmin, distort, ref bestNoiseInfo, prevNoise);
        bestNoiseInfo.Bits = codInfo.Part23Length;

        codInfoW = codInfo;
        age = 0;
        xrpow[..576].CopyTo(saveXrpow);

        while (!bEndOfSearch)
        {
            do
            {
                CalcNoiseResult noiseInfo = default;
                int searchLimit;
                int maxggain = 255;

                // When a quantisation with no distorted band is found, allow this many
                // unsuccessful tries in a row; more than 3 is only slower.
                searchLimit = (gfc.SvQnt.SubstepShaping & 2) != 0 ? 20 : 3;

                // If the last scalefactor band is distorted, VBR cannot get rid of it, so quit
                // now and let VBR try again with more bits. Robert Hegemann measured a 10%
                // speed increase with binary identical output, 2000/05/20.
                if (gfc.SvQnt.Sfb21Extra != 0)
                {
                    if (distort[codInfoW.Sfbmax] > 1.0f) break;
                    if (codInfoW.BlockType == LameBlockType.Short
                        && (distort[codInfoW.Sfbmax + 1] > 1.0f
                            || distort[codInfoW.Sfbmax + 2] > 1.0f))
                        break;
                }

                // try a new scalefactor combination on cod_info_w
                if (BalanceNoise(gfc, ref codInfoW, distort, xrpow, bRefine) == 0) break;
                if (codInfoW.ScalefacScale != 0) maxggain = 254;

                // The inner loop starts with the step computed above and raises it until the
                // bits fit, so it matters that the initial step is not too large.
                huffBits = targBits - codInfoW.Part2Length;
                if (huffBits <= 0) break;

                while ((codInfoW.Part23Length =
                            LameTakehiro.CountBits(gfc, xrpow, ref codInfoW, prevNoise)) > huffBits
                       && codInfoW.GlobalGain <= maxggain)
                    codInfoW.GlobalGain++;

                if (codInfoW.GlobalGain > maxggain) break;

                if (bestNoiseInfo.OverCount == 0)
                {
                    while ((codInfoW.Part23Length =
                                LameTakehiro.CountBits(gfc, xrpow, ref codInfoW, prevNoise))
                            > bestPart23Length
                           && codInfoW.GlobalGain <= maxggain)
                        codInfoW.GlobalGain++;

                    if (codInfoW.GlobalGain > maxggain) break;
                }

                LameQuantizePvt.CalcNoise(codInfoW, l3Xmin, distort, ref noiseInfo, prevNoise);
                noiseInfo.Bits = codInfoW.Part23Length;

                int comparison = codInfo.BlockType != LameBlockType.Short
                    ? cfg.QuantComp
                    : cfg.QuantCompShort;

                better = QuantCompare(
                    comparison, bestNoiseInfo, ref noiseInfo, codInfoW, distort);

                if (better)
                {
                    bestPart23Length = codInfo.Part23Length;
                    bestNoiseInfo = noiseInfo;
                    codInfo = codInfoW;
                    age = 0;
                    xrpow[..576].CopyTo(saveXrpow);
                }
                else
                {
                    // early stop?
                    if (cfg.FullOuterLoop == 0)
                    {
                        if (++age > searchLimit && bestNoiseInfo.OverCount == 0) break;
                        if (cfg.NoiseShapingAmp == 3 && bRefine != 0 && age > 30) break;
                        if (cfg.NoiseShapingAmp == 3 && bRefine != 0
                            && codInfoW.GlobalGain - bestGgainPass1 > 15) break;
                    }
                }
            }
            while (codInfoW.GlobalGain + codInfoW.ScalefacScale < 255);

            if (cfg.NoiseShapingAmp == 3)
            {
                if (bRefine == 0)
                {
                    // refine the search
                    codInfoW = codInfo;
                    saveXrpow.CopyTo(xrpow[..576]);
                    age = 0;
                    bestGgainPass1 = codInfoW.GlobalGain;

                    bRefine = 1;
                }
                else
                {
                    bEndOfSearch = true;
                }
            }
            else
            {
                bEndOfSearch = true;
            }
        }

        if (cfg.Vbr == VbrMode.Rh || cfg.Vbr == VbrMode.Mtrh || cfg.Vbr == VbrMode.Mt)
            saveXrpow.CopyTo(xrpow[..576]);     // restore for reuse on the next try
        else if ((gfc.SvQnt.SubstepShaping & 1) != 0)
            TrancateSmallspectrums(gfc, ref codInfo, l3Xmin, xrpow);

        return bestNoiseInfo.OverCount;
    }

    /// <summary>
    /// <c>iteration_finish_one</c> - Robert Hegemann 2000, tidy up after the final quantisation.
    /// </summary>
    private static void IterationFinishOne(LameInternalFlags gfc, int gr, int ch)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref IiiSideInfo l3Side = ref gfc.L3Side;

        // a better way to store the scalefactors
        LameTakehiro.BestScalefacStore(gfc, gr, ch, ref l3Side);

        // best_huffman_divide may save a few more bits
        if (cfg.UseBestHuffman == 1)
            LameTakehiro.BestHuffmanDivide(gfc, ref l3Side.Tt[gr * 2 + ch]);

        LameReservoir.ResvAdjust(gfc, l3Side.Tt[gr * 2 + ch]);
    }

    /// <summary><c>VBR_encode_granule</c> - Robert Hegemann 2000.</summary>
    private static void VbrEncodeGranule(
        LameInternalFlags gfc, ref GrInfo codInfo, ReadOnlySpan<float> l3Xmin,
        Span<float> xrpow, int ch, int minBits, int maxBits)
    {
        GrInfo bstCodInfo = default;
        Span<float> bstXrpow = gfc.QuantizeScratch.BstXrpow;
        int maxBitsLimit = maxBits;
        int realBits = maxBits + 1;
        int thisBits = (maxBits + minBits) / 2;
        int dbits, over, found = 0;
        int sfb21Extra = gfc.SvQnt.Sfb21Extra;

        ((Span<int>)bstCodInfo.L3Enc).Clear();

        // search within about 40 bits of optimal
        do
        {
            gfc.SvQnt.Sfb21Extra = thisBits > maxBitsLimit - 42 ? 0 : sfb21Extra;

            over = OuterLoop(gfc, ref codInfo, l3Xmin, xrpow, ch, thisBits);

            // is the quantisation as good as we are looking for - no band distorted?
            if (over <= 0)
            {
                found = 1;
                // now we know it can be done with real_bits, and may skip some iterations
                realBits = codInfo.Part23Length;

                bstCodInfo = codInfo;
                xrpow[..576].CopyTo(bstXrpow);

                // try with fewer bits
                maxBits = realBits - 32;
                dbits = maxBits - minBits;
                thisBits = (maxBits + minBits) / 2;
            }
            else
            {
                // try with more bits
                minBits = thisBits + 32;
                dbits = maxBits - minBits;
                thisBits = (maxBits + minBits) / 2;

                if (found != 0)
                {
                    found = 2;
                    // start again from the best quantisation so far
                    codInfo = bstCodInfo;
                    bstXrpow.CopyTo(xrpow[..576]);
                }
            }
        } while (dbits > 12);

        gfc.SvQnt.Sfb21Extra = sfb21Extra;

        // found 0: nothing found, use the last one. 1: just found the best and left the loop.
        // 2: restored a good one and now have l3_enc to restore too.
        if (found == 2)
            ((ReadOnlySpan<int>)bstCodInfo.L3Enc).CopyTo(codInfo.L3Enc);
    }

    /// <summary>
    /// <c>get_framebits</c> - Robert Hegemann 2000, how many bits each bitrate would give.
    /// </summary>
    private static void GetFramebits(LameInternalFlags gfc, Span<int> frameBits)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncResult eov = ref gfc.OvEnc;
        int i;

        // always use at least this many bits per granule per channel, unless analog silence
        eov.BitrateIndex = cfg.VbrMinBitrateIndex;
        LameBitstream.GetFrameBits(gfc);

        // bits for analog silence
        eov.BitrateIndex = 1;
        LameBitstream.GetFrameBits(gfc);

        for (i = 1; i <= cfg.VbrMaxBitrateIndex; i++)
        {
            eov.BitrateIndex = i;
            frameBits[i] = LameReservoir.ResvFrameBegin(gfc, out _);
        }
    }

    /// <summary>
    /// <c>VBR_old_prepare</c> - Robert Hegemann 2000. The C notes it needs an overhaul.
    /// </summary>
    private static int VbrOldPrepare(
        LameInternalFlags gfc, ReadOnlySpan<float> pe, ReadOnlySpan<float> msEnerRatio,
        ReadOnlySpan<IiiPsyRatio> ratio, Span<float> l3Xmin, Span<int> frameBits,
        Span<int> minBits, Span<int> maxBits, Span<int> bands)
    {
        const int sfbMax = LameConstants.SfbMax;
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncResult eov = ref gfc.OvEnc;

        float maskingLowerDb, adjust;
        int gr, ch;
        int analogSilence = 1;
        int avg, mxb, bits = 0;

        eov.BitrateIndex = cfg.VbrMaxBitrateIndex;
        avg = LameReservoir.ResvFrameBegin(gfc, out _) / cfg.ModeGr;

        GetFramebits(gfc, frameBits);

        for (gr = 0; gr < cfg.ModeGr; gr++)
        {
            mxb = LameQuantizePvt.OnPe(gfc, pe, maxBits.Slice(gr * 2, 2), avg, gr, 0);
            if (gfc.OvEnc.ModeExt == LameModeExtValue.MsLr)
            {
                MsConvert(ref gfc.L3Side, gr);
                LameQuantizePvt.ReduceSide(
                    maxBits.Slice(gr * 2, 2), msEnerRatio[gr], avg, mxb);
            }
            for (ch = 0; ch < cfg.ChannelsOut; ++ch)
            {
                ref GrInfo codInfo = ref gfc.L3Side.Tt[gr * 2 + ch];

                if (codInfo.BlockType != LameBlockType.Short)
                {
                    adjust = (float)(1.28 / (1 + Math.Exp(3.5 - pe[gr * 2 + ch] / 300.0)) - 0.05);
                    maskingLowerDb = gfc.SvQnt.MaskAdjust - adjust;
                }
                else
                {
                    adjust = (float)(2.56 / (1 + Math.Exp(3.5 - pe[gr * 2 + ch] / 300.0)) - 0.14);
                    maskingLowerDb = gfc.SvQnt.MaskAdjustShort - adjust;
                }
                gfc.SvQnt.MaskingLower = (float)Math.Pow(10.0, maskingLowerDb * 0.1);

                InitOuterLoop(gfc, ref codInfo);
                bands[gr * 2 + ch] = LameQuantizePvt.CalcXmin(
                    gfc, ratio[gr * 2 + ch], ref codInfo,
                    l3Xmin.Slice((gr * 2 + ch) * sfbMax, sfbMax));
                if (bands[gr * 2 + ch] != 0) analogSilence = 0;

                minBits[gr * 2 + ch] = 126;

                bits += maxBits[gr * 2 + ch];
            }
        }
        for (gr = 0; gr < cfg.ModeGr; gr++)
        {
            for (ch = 0; ch < cfg.ChannelsOut; ch++)
            {
                if (bits > frameBits[cfg.VbrMaxBitrateIndex] && bits > 0)
                {
                    maxBits[gr * 2 + ch] *= frameBits[cfg.VbrMaxBitrateIndex];
                    maxBits[gr * 2 + ch] /= bits;
                }
                if (minBits[gr * 2 + ch] > maxBits[gr * 2 + ch])
                    minBits[gr * 2 + ch] = maxBits[gr * 2 + ch];
            }
        }

        return analogSilence;
    }

    /// <summary><c>bitpressure_strategy</c>.</summary>
    private static void BitpressureStrategy(
        LameInternalFlags gfc, Span<float> l3Xmin, ReadOnlySpan<int> minBits, Span<int> maxBits)
    {
        const int sfbMax = LameConstants.SfbMax;
        ref SessionConfig cfg = ref gfc.Cfg;
        int gr, ch, sfb;

        for (gr = 0; gr < cfg.ModeGr; gr++)
        {
            for (ch = 0; ch < cfg.ChannelsOut; ch++)
            {
                ref GrInfo gi = ref gfc.L3Side.Tt[gr * 2 + ch];
                int pxmin = (gr * 2 + ch) * sfbMax;
                for (sfb = 0; sfb < gi.PsyLmax; sfb++)
                    l3Xmin[pxmin++] *= (float)(1.0 + .029 * sfb * sfb
                        / LameConstants.SbMaxL / LameConstants.SbMaxL);

                if (gi.BlockType == LameBlockType.Short)
                {
                    for (sfb = gi.SfbSmin; sfb < LameConstants.SbMaxS; sfb++)
                    {
                        float f = (float)(1.0 + .029 * sfb * sfb
                            / LameConstants.SbMaxS / LameConstants.SbMaxS);
                        l3Xmin[pxmin++] *= f;
                        l3Xmin[pxmin++] *= f;
                        l3Xmin[pxmin++] *= f;
                    }
                }
                maxBits[gr * 2 + ch] = Math.Max(
                    minBits[gr * 2 + ch], (int)(0.9 * maxBits[gr * 2 + ch]));
            }
        }
    }

    /// <summary>
    /// <c>VBR_old_iteration_loop</c> - find how many bits each granule and channel needs, then
    /// pick a bitrate that holds them. rh 8/99, rewritten 2000-09-06.
    /// </summary>
    public static void VbrOldIterationLoop(
        LameInternalFlags gfc, ReadOnlySpan<float> pe, ReadOnlySpan<float> msEnerRatio,
        ReadOnlySpan<IiiPsyRatio> ratio)
    {
        const int sfbMax = LameConstants.SfbMax;
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncResult eov = ref gfc.OvEnc;
        LameQuantizeScratch scratch = gfc.QuantizeScratch;

        Span<float> l3Xmin = scratch.VbrL3Xmin;
        Span<float> xrpow = scratch.VbrXrpow;
        Span<int> bands = scratch.Bands;
        Span<int> frameBits = scratch.FrameBits;
        int usedBits;
        int bits;
        Span<int> minBits = scratch.MinBits;
        Span<int> maxBits = scratch.MaxBits;
        int meanBits = 0;
        int ch, gr, analogSilence;

        analogSilence = VbrOldPrepare(
            gfc, pe, msEnerRatio, ratio, l3Xmin, frameBits, minBits, maxBits, bands);

        for (; ; )
        {
            // quantise the granules with the lowest possible number of bits
            usedBits = 0;

            for (gr = 0; gr < cfg.ModeGr; gr++)
            {
                for (ch = 0; ch < cfg.ChannelsOut; ch++)
                {
                    int ret;
                    ref GrInfo codInfo = ref gfc.L3Side.Tt[gr * 2 + ch];

                    ret = InitXrpow(gfc, ref codInfo, xrpow);
                    if (ret == 0 || maxBits[gr * 2 + ch] == 0)
                    {
                        // xr holds no energy, so l3_enc quantises to zero
                        continue;
                    }

                    VbrEncodeGranule(
                        gfc, ref codInfo, l3Xmin.Slice((gr * 2 + ch) * sfbMax, sfbMax), xrpow,
                        ch, minBits[gr * 2 + ch], maxBits[gr * 2 + ch]);

                    if ((gfc.SvQnt.SubstepShaping & 1) != 0)
                        TrancateSmallspectrums(
                            gfc, ref codInfo,
                            l3Xmin.Slice((gr * 2 + ch) * sfbMax, sfbMax), xrpow);

                    ret = codInfo.Part23Length + codInfo.Part2Length;
                    usedBits += ret;
                }
            }

            // find the lowest bitrate that holds the bits used
            if (analogSilence != 0 && cfg.EnforceMinBitrate == 0)
                eov.BitrateIndex = 1;
            else
                eov.BitrateIndex = cfg.VbrMinBitrateIndex;

            for (; eov.BitrateIndex < cfg.VbrMaxBitrateIndex; eov.BitrateIndex++)
                if (usedBits <= frameBits[eov.BitrateIndex])
                    break;

            bits = LameReservoir.ResvFrameBegin(gfc, out meanBits);

            if (usedBits <= bits) break;

            BitpressureStrategy(gfc, l3Xmin, minBits, maxBits);
        }

        for (gr = 0; gr < cfg.ModeGr; gr++)
            for (ch = 0; ch < cfg.ChannelsOut; ch++)
                IterationFinishOne(gfc, gr, ch);

        LameReservoir.ResvFrameEnd(gfc, meanBits);
    }

    /// <summary><c>VBR_new_prepare</c>.</summary>
    /// <param name="maxResv">The C's <c>*max_resv</c>.</param>
    /// <returns>1 if the whole frame is analog silence.</returns>
    /// <remarks>
    /// Shorter than <see cref="VbrOldPrepare"/> in one telling way: it sets
    /// <c>masking_lower</c> straight from <c>mask_adjust</c>, with none of the old loop's
    /// perceptual-entropy adjustment, and it never calls <c>reduce_side</c>. The VBR-new
    /// search decides per band what it can afford, so there is nothing for those to do.
    /// </remarks>
    private static int VbrNewPrepare(
        LameInternalFlags gfc, ReadOnlySpan<float> pe, ReadOnlySpan<IiiPsyRatio> ratio,
        Span<float> l3Xmin, Span<int> frameBits, Span<int> maxBits, out int maxResv)
    {
        const int sfbMax = LameConstants.SfbMax;
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncResult eov = ref gfc.OvEnc;

        int gr, ch;
        int analogSilence = 1;
        int avg, bits = 0;
        int maximumFramebits;

        if (cfg.FreeFormat == 0)
        {
            eov.BitrateIndex = cfg.VbrMaxBitrateIndex;
            LameReservoir.ResvFrameBegin(gfc, out avg);
            maxResv = gfc.SvEnc.ResvMax;

            GetFramebits(gfc, frameBits);
            maximumFramebits = frameBits[cfg.VbrMaxBitrateIndex];
        }
        else
        {
            eov.BitrateIndex = 0;
            maximumFramebits = LameReservoir.ResvFrameBegin(gfc, out avg);
            frameBits[0] = maximumFramebits;
            maxResv = gfc.SvEnc.ResvMax;
        }

        for (gr = 0; gr < cfg.ModeGr; gr++)
        {
            LameQuantizePvt.OnPe(gfc, pe, maxBits.Slice(gr * 2, 2), avg, gr, 0);
            if (gfc.OvEnc.ModeExt == LameModeExtValue.MsLr)
                MsConvert(ref gfc.L3Side, gr);

            for (ch = 0; ch < cfg.ChannelsOut; ++ch)
            {
                ref GrInfo codInfo = ref gfc.L3Side.Tt[gr * 2 + ch];

                gfc.SvQnt.MaskingLower = (float)Math.Pow(10.0, gfc.SvQnt.MaskAdjust * 0.1);

                InitOuterLoop(gfc, ref codInfo);
                if (LameQuantizePvt.CalcXmin(
                        gfc, ratio[gr * 2 + ch], ref codInfo,
                        l3Xmin.Slice((gr * 2 + ch) * sfbMax, sfbMax)) != 0)
                    analogSilence = 0;

                bits += maxBits[gr * 2 + ch];
            }
        }
        for (gr = 0; gr < cfg.ModeGr; gr++)
        {
            for (ch = 0; ch < cfg.ChannelsOut; ch++)
            {
                if (bits > maximumFramebits && bits > 0)
                {
                    maxBits[gr * 2 + ch] *= maximumFramebits;
                    maxBits[gr * 2 + ch] /= bits;
                }
            }
        }
        if (analogSilence != 0) maxResv = 0;
        return analogSilence;
    }

    /// <summary><c>VBR_new_iteration_loop</c> - the vbr_mtrh and vbr_mt loop.</summary>
    /// <remarks>
    /// <c>ms_ener_ratio</c> is taken and ignored, exactly as in the C: the VBR-new path does
    /// no side-channel reduction.
    /// </remarks>
    public static void VbrNewIterationLoop(
        LameInternalFlags gfc, ReadOnlySpan<float> pe, ReadOnlySpan<float> msEnerRatio,
        ReadOnlySpan<IiiPsyRatio> ratio)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncResult eov = ref gfc.OvEnc;
        LameQuantizeScratch scratch = gfc.QuantizeScratch;

        float[] l3Xmin = scratch.VbrL3Xmin;
        float[] xrpow = scratch.VbrNewXrpow;
        Span<int> frameBits = scratch.FrameBits;
        Span<int> maxBits = scratch.MaxBits;
        int usedBits;
        int ch, gr, analogSilence, pad;

        Array.Clear(xrpow);

        analogSilence = VbrNewPrepare(gfc, pe, ratio, l3Xmin, frameBits, maxBits, out pad);

        for (gr = 0; gr < cfg.ModeGr; gr++)
        {
            for (ch = 0; ch < cfg.ChannelsOut; ch++)
            {
                ref GrInfo codInfo = ref gfc.L3Side.Tt[gr * 2 + ch];

                // init_outer_loop has already set up cod_info, scalefac and xrpow
                if (InitXrpow(gfc, ref codInfo, xrpow.AsSpan((gr * 2 + ch) * 576, 576)) == 0)
                    maxBits[gr * 2 + ch] = 0;   // a silent granule needs no bits
            }
        }

        // quantise the granules with the lowest possible number of bits
        usedBits = LameVbrQuantize.VbrEncodeFrame(gfc, xrpow, l3Xmin, maxBits, scratch.Vbr);

        if (cfg.FreeFormat == 0)
        {
            int i, j;

            // find the lowest bitrate able to hold the bits used
            if (analogSilence != 0 && cfg.EnforceMinBitrate == 0)
            {
                // analog silence, and no hard frame size was asked for, so start with the
                // smallest frame there is
                i = 1;
            }
            else
            {
                i = cfg.VbrMinBitrateIndex;
            }

            for (; i < cfg.VbrMaxBitrateIndex; i++)
                if (usedBits <= frameBits[i])
                    break;

            if (i > cfg.VbrMaxBitrateIndex) i = cfg.VbrMaxBitrateIndex;

            if (pad > 0)
            {
                for (j = cfg.VbrMaxBitrateIndex; j > i; --j)
                {
                    int unused = frameBits[j] - usedBits;
                    if (unused <= pad) break;
                }
                eov.BitrateIndex = j;
            }
            else
            {
                eov.BitrateIndex = i;
            }
        }
        else
        {
            eov.BitrateIndex = 0;
        }

        if (usedBits > frameBits[eov.BitrateIndex])
            throw new InvalidOperationException(
                $"LAME VBR: {usedBits} bits used against {frameBits[eov.BitrateIndex]} "
              + $"available at bitrate index {eov.BitrateIndex}. This is the C's 'INTERNAL "
              + "ERROR IN VBR NEW CODE'.");

        // update the reservoir
        LameReservoir.ResvFrameBegin(gfc, out int meanBits);
        for (gr = 0; gr < cfg.ModeGr; gr++)
            for (ch = 0; ch < cfg.ChannelsOut; ch++)
                LameReservoir.ResvAdjust(gfc, in gfc.L3Side.Tt[gr * 2 + ch]);

        LameReservoir.ResvFrameEnd(gfc, meanBits);
    }

    /// <summary>
    /// <c>calc_target_bits</c> - mt 2000, the target bits for ABR.
    /// </summary>
    private static void CalcTargetBits(
        LameInternalFlags gfc, ReadOnlySpan<float> pe, ReadOnlySpan<float> msEnerRatio,
        Span<int> targBits, out int analogSilenceBits, out int maxFrameBits)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncResult eov = ref gfc.OvEnc;
        float resFactor;
        int gr, ch, totbits, meanBits;
        int framesize = 576 * cfg.ModeGr;

        eov.BitrateIndex = cfg.VbrMaxBitrateIndex;
        maxFrameBits = LameReservoir.ResvFrameBegin(gfc, out meanBits);

        eov.BitrateIndex = 1;
        meanBits = LameBitstream.GetFrameBits(gfc) - cfg.SideinfoLen * 8;
        analogSilenceBits = meanBits / (cfg.ModeGr * cfg.ChannelsOut);

        meanBits = cfg.VbrAvgBitrateKbps * framesize * 1000;
        if ((gfc.SvQnt.SubstepShaping & 1) != 0) meanBits = (int)(meanBits * 1.09);
        meanBits /= cfg.SamplerateOut;
        meanBits -= cfg.SideinfoLen * 8;
        meanBits /= cfg.ModeGr * cfg.ChannelsOut;

        // res_factor is the share of the target bitrate to spend on average; the rest goes to
        // the reservoir for difficult frames. 1.0 at 256 kbps, .93 at 128 kbps, interpolated.
        resFactor = (float)(.93 + .07 * (11.0 - cfg.CompressionRatio) / (11.0 - 5.5));
        if (resFactor < .90f) resFactor = .90f;
        if (resFactor > 1.00f) resFactor = 1.00f;

        for (gr = 0; gr < cfg.ModeGr; gr++)
        {
            int sum = 0;
            for (ch = 0; ch < cfg.ChannelsOut; ch++)
            {
                targBits[gr * 2 + ch] = (int)(resFactor * meanBits);

                if (pe[gr * 2 + ch] > 700)
                {
                    int addBits = (int)((pe[gr * 2 + ch] - 700) / 1.4);

                    ref GrInfo codInfo = ref gfc.L3Side.Tt[gr * 2 + ch];
                    targBits[gr * 2 + ch] = (int)(resFactor * meanBits);

                    // short blocks use a little extra, whatever the pe
                    if (codInfo.BlockType == LameBlockType.Short)
                        if (addBits < meanBits / 2)
                            addBits = meanBits / 2;

                    // at most increase bits by 1.5 * average
                    if (addBits > meanBits * 3 / 2)
                        addBits = meanBits * 3 / 2;
                    else if (addBits < 0)
                        addBits = 0;

                    targBits[gr * 2 + ch] += addBits;
                }
                if (targBits[gr * 2 + ch] > LameQuantizeConstants.MaxBitsPerChannel)
                    targBits[gr * 2 + ch] = LameQuantizeConstants.MaxBitsPerChannel;
                sum += targBits[gr * 2 + ch];
            }
            if (sum > LameQuantizeConstants.MaxBitsPerGranule)
            {
                for (ch = 0; ch < cfg.ChannelsOut; ++ch)
                {
                    targBits[gr * 2 + ch] *= LameQuantizeConstants.MaxBitsPerGranule;
                    targBits[gr * 2 + ch] /= sum;
                }
            }
        }

        if (gfc.OvEnc.ModeExt == LameModeExtValue.MsLr)
            for (gr = 0; gr < cfg.ModeGr; gr++)
                LameQuantizePvt.ReduceSide(
                    targBits.Slice(gr * 2, 2), msEnerRatio[gr],
                    meanBits * cfg.ChannelsOut, LameQuantizeConstants.MaxBitsPerGranule);

        totbits = 0;
        for (gr = 0; gr < cfg.ModeGr; gr++)
        {
            for (ch = 0; ch < cfg.ChannelsOut; ch++)
            {
                if (targBits[gr * 2 + ch] > LameQuantizeConstants.MaxBitsPerChannel)
                    targBits[gr * 2 + ch] = LameQuantizeConstants.MaxBitsPerChannel;
                totbits += targBits[gr * 2 + ch];
            }
        }

        // repartition if needed
        if (totbits > maxFrameBits && totbits > 0)
        {
            for (gr = 0; gr < cfg.ModeGr; gr++)
            {
                for (ch = 0; ch < cfg.ChannelsOut; ch++)
                {
                    targBits[gr * 2 + ch] *= maxFrameBits;
                    targBits[gr * 2 + ch] /= totbits;
                }
            }
        }
    }

    /// <summary><c>ABR_iteration_loop</c> - mt 2000, encode a frame at a desired average rate.</summary>
    public static void AbrIterationLoop(
        LameInternalFlags gfc, ReadOnlySpan<float> pe, ReadOnlySpan<float> msEnerRatio,
        ReadOnlySpan<IiiPsyRatio> ratio)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncResult eov = ref gfc.OvEnc;
        LameQuantizeScratch scratch = gfc.QuantizeScratch;
        Span<float> l3Xmin = scratch.L3Xmin;
        Span<float> xrpow = scratch.Xrpow;
        Span<int> targBits = scratch.TargBits2;
        int meanBits, maxFrameBits;
        int ch, gr, athOver;
        int analogSilenceBits;

        meanBits = 0;

        CalcTargetBits(gfc, pe, msEnerRatio, targBits, out analogSilenceBits, out maxFrameBits);
        _ = maxFrameBits;

        for (gr = 0; gr < cfg.ModeGr; gr++)
        {
            if (gfc.OvEnc.ModeExt == LameModeExtValue.MsLr)
                MsConvert(ref gfc.L3Side, gr);

            for (ch = 0; ch < cfg.ChannelsOut; ch++)
            {
                float adjust, maskingLowerDb;
                ref GrInfo codInfo = ref gfc.L3Side.Tt[gr * 2 + ch];

                if (codInfo.BlockType != LameBlockType.Short)
                {
                    adjust = 0;
                    maskingLowerDb = gfc.SvQnt.MaskAdjust - adjust;
                }
                else
                {
                    adjust = 0;
                    maskingLowerDb = gfc.SvQnt.MaskAdjustShort - adjust;
                }
                gfc.SvQnt.MaskingLower = (float)Math.Pow(10.0, maskingLowerDb * 0.1);

                InitOuterLoop(gfc, ref codInfo);
                if (InitXrpow(gfc, ref codInfo, xrpow) != 0)
                {
                    athOver = LameQuantizePvt.CalcXmin(
                        gfc, ratio[gr * 2 + ch], ref codInfo, l3Xmin);
                    if (athOver == 0)       // analog silence
                        targBits[gr * 2 + ch] = analogSilenceBits;

                    OuterLoop(gfc, ref codInfo, l3Xmin, xrpow, ch, targBits[gr * 2 + ch]);
                }
                IterationFinishOne(gfc, gr, ch);
            }
        }

        // find a bitrate that can refill the reservoir to a positive size
        for (eov.BitrateIndex = cfg.VbrMinBitrateIndex;
             eov.BitrateIndex <= cfg.VbrMaxBitrateIndex;
             eov.BitrateIndex++)
        {
            if (LameReservoir.ResvFrameBegin(gfc, out meanBits) >= 0) break;
        }

        LameReservoir.ResvFrameEnd(gfc, meanBits);
    }

    /// <summary><c>CBR_iteration_loop</c> - one frame at a constant bitrate.</summary>
    public static void CbrIterationLoop(
        LameInternalFlags gfc, ReadOnlySpan<float> pe, ReadOnlySpan<float> msEnerRatio,
        ReadOnlySpan<IiiPsyRatio> ratio)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        LameQuantizeScratch scratch = gfc.QuantizeScratch;
        Span<float> l3Xmin = scratch.L3Xmin;
        Span<float> xrpow = scratch.Xrpow;
        Span<int> targBits = scratch.TargBits2.AsSpan(0, 2);
        int meanBits, maxBits;
        int gr, ch;

        LameReservoir.ResvFrameBegin(gfc, out meanBits);

        for (gr = 0; gr < cfg.ModeGr; gr++)
        {
            maxBits = LameQuantizePvt.OnPe(gfc, pe, targBits, meanBits, gr, gr);

            if (gfc.OvEnc.ModeExt == LameModeExtValue.MsLr)
            {
                MsConvert(ref gfc.L3Side, gr);
                LameQuantizePvt.ReduceSide(targBits, msEnerRatio[gr], meanBits, maxBits);
            }

            for (ch = 0; ch < cfg.ChannelsOut; ch++)
            {
                float adjust, maskingLowerDb;
                ref GrInfo codInfo = ref gfc.L3Side.Tt[gr * 2 + ch];

                if (codInfo.BlockType != LameBlockType.Short)
                {
                    adjust = 0;
                    maskingLowerDb = gfc.SvQnt.MaskAdjust - adjust;
                }
                else
                {
                    adjust = 0;
                    maskingLowerDb = gfc.SvQnt.MaskAdjustShort - adjust;
                }
                gfc.SvQnt.MaskingLower = (float)Math.Pow(10.0, maskingLowerDb * 0.1);

                InitOuterLoop(gfc, ref codInfo);
                if (InitXrpow(gfc, ref codInfo, xrpow) != 0)
                {
                    LameQuantizePvt.CalcXmin(gfc, ratio[gr * 2 + ch], ref codInfo, l3Xmin);
                    OuterLoop(gfc, ref codInfo, l3Xmin, xrpow, ch, targBits[ch]);
                }

                IterationFinishOne(gfc, gr, ch);
            }
        }

        LameReservoir.ResvFrameEnd(gfc, meanBits);
    }
}

/// <summary>Four floats, standing in for the <c>__m128</c> lanes of the vector path.</summary>
[System.Runtime.CompilerServices.InlineArray(4)]
internal struct Float4 { private float element0; }

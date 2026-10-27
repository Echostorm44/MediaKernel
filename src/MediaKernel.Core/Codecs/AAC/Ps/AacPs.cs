// aacps.c - Parametric Stereo, the HE-AACv2 tool that turns one decoded channel into two.
//
// It runs entirely in SBR's QMF domain, between X generation and the synthesis filterbank, in
// four stages:
//
//   hybrid analysis   splits the lowest QMF bands into sub-subbands, so the low frequencies where
//                     the ear places a stereo image get the resolution the parameters describe;
//   decorrelation     builds a side signal from the mid by a chain of fractional-delay all-pass
//                     sections, scaled by a transient detector so it does not smear attacks;
//   stereo processing mixes mid and side through a 2x2 matrix per parameter band, interpolated
//                     slot by slot between envelopes;
//   hybrid synthesis  sums the sub-subbands back to QMF bands, now twice.
//
// The parameter bands come in 10, 20 or 34, the sub-subbands in 71 or 91, and the tables that map
// between them change when the stream switches mode mid-stream - which is why so much of this
// file is remapping, and why the mapping of the PREVIOUS frame's matrices (map_val_20_to_34 and
// its twin) matters as much as the current frame's parameters.

using System;

namespace MediaKernel.Core.Codecs.AAC.Ps;

/// <summary>The float Parametric Stereo decoder, <c>aacps.c</c>.</summary>
internal static class AacPs
{
    // Inline arrays rather than stackalloc, for the same reason PsDsp uses them: a method
    // containing localloc never tiers up, and these run once per slot of every frame.
    [System.Runtime.CompilerServices.InlineArray(12)]
    private struct Float12 { private float element0; }

    /// <summary>All-pass filter decay slope, <c>DECAY_SLOPE</c>.</summary>
    private const float DecaySlope = 0.05f;

    /// <summary>Bands addressable by the parameter index b(k).</summary>
    private static ReadOnlySpan<int> NrParBands => [20, 34];

    private static ReadOnlySpan<int> NrIpdOpdBands => [11, 17];

    /// <summary>Bands addressable by the sub-subband index k.</summary>
    private static ReadOnlySpan<int> NrBands => [71, 91];

    /// <summary>First band of the all-pass decay slope.</summary>
    private static ReadOnlySpan<int> DecayCutoff => [10, 32];

    private static ReadOnlySpan<int> NrAllpassBands => [30, 50];

    /// <summary>First stereo band using the one-slot delay.</summary>
    private static ReadOnlySpan<int> ShortDelayBand => [42, 62];

    /// <summary><c>ipdopd_reset</c>.</summary>
    private static void IpdOpdReset(sbyte[] ipdHist, sbyte[] opdHist)
    {
        for (int i = 0; i < PsLimits.MaxNrIpdOpd; i++)
        {
            opdHist[i] = 0;
            ipdHist[i] = 0;
        }
    }

    /// <summary>
    /// <c>hybrid2_re</c>: split one subband into two with a symmetric real filter whose
    /// non-centre even taps are zero.
    /// </summary>
    private static void Hybrid2Re(float[][] input, float[][][] output, int outputBand,
                                  float[] filter, int len, int reverse)
    {
        for (int i = 0; i < len; i++)
        {
            float reIn = filter[6] * input[i + 6][0];
            float imIn = filter[6] * input[i + 6][1];
            float reOp = 0.0f;
            float imOp = 0.0f;

            for (int j = 0; j < 6; j += 2)
            {
                reOp += filter[j + 1] * (input[i + j + 1][0] + input[i + 12 - j - 1][0]);
                imOp += filter[j + 1] * (input[i + j + 1][1] + input[i + 12 - j - 1][1]);
            }

            output[outputBand + reverse][i][0] = reIn + reOp;
            output[outputBand + reverse][i][1] = imIn + imOp;
            output[outputBand + (reverse ^ 1)][i][0] = reIn - reOp;
            output[outputBand + (reverse ^ 1)][i][1] = imIn - imOp;
        }
    }

    /// <summary><c>hybrid6_cx</c>: split one subband into six with a complex filter.</summary>
    private static void Hybrid6Cx(PsContext ps, float[][] input, float[][][] output,
                                  int outputBand, float[][][] filter, int len)
    {
        const int n = 8;
        float[][] temp = ps.HybridTemp;
        Float12 reStore = default, imStore = default;
        Span<float> re = reStore;
        Span<float> im = imStore;

        for (int i = 0; i < len; i++)
        {
            PsDsp.HybridAnalysis(input, i, filter, n, re, im);
            for (int t = 0; t < n; t++)
            {
                temp[t][0] = re[t];
                temp[t][1] = im[t];
            }

            output[outputBand + 0][i][0] = temp[6][0];
            output[outputBand + 0][i][1] = temp[6][1];
            output[outputBand + 1][i][0] = temp[7][0];
            output[outputBand + 1][i][1] = temp[7][1];
            output[outputBand + 2][i][0] = temp[0][0];
            output[outputBand + 2][i][1] = temp[0][1];
            output[outputBand + 3][i][0] = temp[1][0];
            output[outputBand + 3][i][1] = temp[1][1];
            output[outputBand + 4][i][0] = temp[2][0] + temp[5][0];
            output[outputBand + 4][i][1] = temp[2][1] + temp[5][1];
            output[outputBand + 5][i][0] = temp[3][0] + temp[4][0];
            output[outputBand + 5][i][1] = temp[3][1] + temp[4][1];
        }
    }

    /// <summary>
    /// <c>hybrid4_8_12_cx</c>: the 34-band mode's splitters. The C's stride of 32 writes
    /// successive filter outputs into successive sub-subbands at the same slot.
    /// </summary>
    private static void Hybrid4_8_12Cx(float[][] input, float[][][] output, int outputBand,
                                       float[][][] filter, int n, int len)
    {
        Float12 reStore = default, imStore = default;
        Span<float> re = reStore;
        Span<float> im = imStore;

        for (int i = 0; i < len; i++)
        {
            PsDsp.HybridAnalysis(input, i, filter, n, re, im);
            for (int m = 0; m < n; m++)
            {
                output[outputBand + m][i][0] = re[m];
                output[outputBand + m][i][1] = im[m];
            }
        }
    }

    /// <summary><c>hybrid_analysis</c>.</summary>
    private static void HybridAnalysis(PsContext ps, float[][][] output, float[][][] left,
                                       bool is34, int len)
    {
        float[][][] inBuf = ps.InBuf;

        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 38; j++)
            {
                inBuf[i][j + 6][0] = left[0][j][i];
                inBuf[i][j + 6][1] = left[1][j][i];
            }

        if (is34)
        {
            Hybrid4_8_12Cx(inBuf[0], output, 0, PsTables.F34_0_12, 12, len);
            Hybrid4_8_12Cx(inBuf[1], output, 12, PsTables.F34_1_8, 8, len);
            Hybrid4_8_12Cx(inBuf[2], output, 20, PsTables.F34_2_4, 4, len);
            Hybrid4_8_12Cx(inBuf[3], output, 24, PsTables.F34_2_4, 4, len);
            Hybrid4_8_12Cx(inBuf[4], output, 28, PsTables.F34_2_4, 4, len);
            PsDsp.HybridAnalysisIleave(output, 27, left, 5, len);
        }
        else
        {
            Hybrid6Cx(ps, inBuf[0], output, 0, PsTables.F20_0_8, len);
            Hybrid2Re(inBuf[1], output, 6, PsTables.G1Q2, len, 1);
            Hybrid2Re(inBuf[2], output, 8, PsTables.G1Q2, len, 0);
            PsDsp.HybridAnalysisIleave(output, 7, left, 3, len);
        }

        // Carry the last six slots into the next frame's history.
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 6; j++)
            {
                inBuf[i][j][0] = inBuf[i][j + 32][0];
                inBuf[i][j][1] = inBuf[i][j + 32][1];
            }
    }

    /// <summary><c>hybrid_synthesis</c>.</summary>
    private static void HybridSynthesis(float[][][] output, float[][][] input, bool is34, int len)
    {
        if (is34)
        {
            for (int n = 0; n < len; n++)
            {
                for (int k = 0; k < 5; k++)
                {
                    output[0][n][k] = 0;
                    output[1][n][k] = 0;
                }

                for (int i = 0; i < 12; i++)
                {
                    output[0][n][0] += input[i][n][0];
                    output[1][n][0] += input[i][n][1];
                }
                for (int i = 0; i < 8; i++)
                {
                    output[0][n][1] += input[12 + i][n][0];
                    output[1][n][1] += input[12 + i][n][1];
                }
                for (int i = 0; i < 4; i++)
                {
                    output[0][n][2] += input[20 + i][n][0];
                    output[1][n][2] += input[20 + i][n][1];
                    output[0][n][3] += input[24 + i][n][0];
                    output[1][n][3] += input[24 + i][n][1];
                    output[0][n][4] += input[28 + i][n][0];
                    output[1][n][4] += input[28 + i][n][1];
                }
            }

            PsDsp.HybridSynthesisDeint(output, input, 27, 5, len);
        }
        else
        {
            for (int n = 0; n < len; n++)
            {
                output[0][n][0] = input[0][n][0] + input[1][n][0] + input[2][n][0] +
                                  input[3][n][0] + input[4][n][0] + input[5][n][0];
                output[1][n][0] = input[0][n][1] + input[1][n][1] + input[2][n][1] +
                                  input[3][n][1] + input[4][n][1] + input[5][n][1];
                output[0][n][1] = input[6][n][0] + input[7][n][0];
                output[1][n][1] = input[6][n][1] + input[7][n][1];
                output[0][n][2] = input[8][n][0] + input[9][n][0];
                output[1][n][2] = input[8][n][1] + input[9][n][1];
            }

            PsDsp.HybridSynthesisDeint(output, input, 7, 3, len);
        }
    }

    /// <summary>Table 8.46: <c>map_idx_10_to_20</c>.</summary>
    private static void MapIdx10To20(sbyte[] mapped, sbyte[] par, bool full)
    {
        int b;
        if (full)
        {
            b = 9;
        }
        else
        {
            b = 4;
            mapped[10] = 0;
        }

        for (; b >= 0; b--)
            mapped[2 * b + 1] = mapped[2 * b] = par[b];
    }

    private static void MapIdx34To20(sbyte[] mapped, sbyte[] par, bool full)
    {
        mapped[0] = (sbyte)((2 * par[0] + par[1]) / 3);
        mapped[1] = (sbyte)((par[1] + 2 * par[2]) / 3);
        mapped[2] = (sbyte)((2 * par[3] + par[4]) / 3);
        mapped[3] = (sbyte)((par[4] + 2 * par[5]) / 3);
        mapped[4] = (sbyte)((par[6] + par[7]) / 2);
        mapped[5] = (sbyte)((par[8] + par[9]) / 2);
        mapped[6] = par[10];
        mapped[7] = par[11];
        mapped[8] = (sbyte)((par[12] + par[13]) / 2);
        mapped[9] = (sbyte)((par[14] + par[15]) / 2);
        mapped[10] = par[16];
        if (full)
        {
            mapped[11] = par[17];
            mapped[12] = par[18];
            mapped[13] = par[19];
            mapped[14] = (sbyte)((par[20] + par[21]) / 2);
            mapped[15] = (sbyte)((par[22] + par[23]) / 2);
            mapped[16] = (sbyte)((par[24] + par[25]) / 2);
            mapped[17] = (sbyte)((par[26] + par[27]) / 2);
            mapped[18] = (sbyte)((par[28] + par[29] + par[30] + par[31]) / 4);
            mapped[19] = (sbyte)((par[32] + par[33]) / 2);
        }
    }

    /// <summary><c>map_val_34_to_20</c>, applied in place to a previous frame's matrix row.</summary>
    private static void MapVal34To20(float[] par)
    {
        par[0] = (2 * par[0] + par[1]) * 0.33333333f;
        par[1] = (par[1] + 2 * par[2]) * 0.33333333f;
        par[2] = (2 * par[3] + par[4]) * 0.33333333f;
        par[3] = (par[4] + 2 * par[5]) * 0.33333333f;
        par[4] = (par[6] + par[7]) * 0.5f;
        par[5] = (par[8] + par[9]) * 0.5f;
        par[6] = par[10];
        par[7] = par[11];
        par[8] = (par[12] + par[13]) * 0.5f;
        par[9] = (par[14] + par[15]) * 0.5f;
        par[10] = par[16];
        par[11] = par[17];
        par[12] = par[18];
        par[13] = par[19];
        par[14] = (par[20] + par[21]) * 0.5f;
        par[15] = (par[22] + par[23]) * 0.5f;
        par[16] = (par[24] + par[25]) * 0.5f;
        par[17] = (par[26] + par[27]) * 0.5f;
        par[18] = (par[28] + par[29] + par[30] + par[31]) * 0.25f;
        par[19] = (par[32] + par[33]) * 0.5f;
    }

    private static void MapIdx10To34(sbyte[] mapped, sbyte[] par, bool full)
    {
        if (full)
        {
            mapped[33] = par[9];
            mapped[32] = par[9];
            mapped[31] = par[9];
            mapped[30] = par[9];
            mapped[29] = par[9];
            mapped[28] = par[9];
            mapped[27] = par[8];
            mapped[26] = par[8];
            mapped[25] = par[8];
            mapped[24] = par[8];
            mapped[23] = par[7];
            mapped[22] = par[7];
            mapped[21] = par[7];
            mapped[20] = par[7];
            mapped[19] = par[6];
            mapped[18] = par[6];
            mapped[17] = par[5];
            mapped[16] = par[5];
        }
        else
        {
            mapped[16] = 0;
        }

        mapped[15] = par[4];
        mapped[14] = par[4];
        mapped[13] = par[4];
        mapped[12] = par[4];
        mapped[11] = par[3];
        mapped[10] = par[3];
        mapped[9] = par[2];
        mapped[8] = par[2];
        mapped[7] = par[2];
        mapped[6] = par[2];
        mapped[5] = par[1];
        mapped[4] = par[1];
        mapped[3] = par[1];
        mapped[2] = par[0];
        mapped[1] = par[0];
        mapped[0] = par[0];
    }

    private static void MapIdx20To34(sbyte[] mapped, sbyte[] par, bool full)
    {
        if (full)
        {
            mapped[33] = par[19];
            mapped[32] = par[19];
            mapped[31] = par[18];
            mapped[30] = par[18];
            mapped[29] = par[18];
            mapped[28] = par[18];
            mapped[27] = par[17];
            mapped[26] = par[17];
            mapped[25] = par[16];
            mapped[24] = par[16];
            mapped[23] = par[15];
            mapped[22] = par[15];
            mapped[21] = par[14];
            mapped[20] = par[14];
            mapped[19] = par[13];
            mapped[18] = par[12];
            mapped[17] = par[11];
        }

        mapped[16] = par[10];
        mapped[15] = par[9];
        mapped[14] = par[9];
        mapped[13] = par[8];
        mapped[12] = par[8];
        mapped[11] = par[7];
        mapped[10] = par[6];
        mapped[9] = par[5];
        mapped[8] = par[5];
        mapped[7] = par[4];
        mapped[6] = par[4];
        mapped[5] = par[3];
        mapped[4] = (sbyte)((par[2] + par[3]) / 2);
        mapped[3] = par[2];
        mapped[2] = par[1];
        mapped[1] = (sbyte)((par[0] + par[1]) / 2);
        mapped[0] = par[0];
    }

    /// <summary><c>map_val_20_to_34</c>, applied in place and backwards so it can overlap.</summary>
    private static void MapVal20To34(float[] par)
    {
        par[33] = par[19];
        par[32] = par[19];
        par[31] = par[18];
        par[30] = par[18];
        par[29] = par[18];
        par[28] = par[18];
        par[27] = par[17];
        par[26] = par[17];
        par[25] = par[16];
        par[24] = par[16];
        par[23] = par[15];
        par[22] = par[15];
        par[21] = par[14];
        par[20] = par[14];
        par[19] = par[13];
        par[18] = par[12];
        par[17] = par[11];
        par[16] = par[10];
        par[15] = par[9];
        par[14] = par[9];
        par[13] = par[8];
        par[12] = par[8];
        par[11] = par[7];
        par[10] = par[6];
        par[9] = par[5];
        par[8] = par[5];
        par[7] = par[4];
        par[6] = par[4];
        par[5] = par[3];
        par[4] = (par[2] + par[3]) * 0.5f;
        par[3] = par[2];
        par[2] = par[1];
        par[1] = (par[0] + par[1]) * 0.5f;
    }

    /// <summary>
    /// <c>decorrelation</c>: build the side signal, band by band, with a transient detector
    /// deciding how much decorrelated energy each slot may carry.
    /// </summary>
    private static void Decorrelation(PsContext ps, float[][][] output, float[][][] s, bool is34)
    {
        int mode = is34 ? 1 : 0;
        float[][] power = ps.Power;
        float[][] transientGain = ps.TransientGain;
        float[] peakDecayNrg = ps.PeakDecayNrg;
        float[] powerSmooth = ps.PowerSmooth;
        float[] peakDecayDiffSmooth = ps.PeakDecayDiffSmooth;
        float[][][] delay = ps.Delay;
        float[][][][] apDelay = ps.ApDelay;
        const float transientImpact = 1.5f;
        const float aSmooth = 0.25f;
        ReadOnlySpan<sbyte> kToI = is34 ? PsData.KToI34 : PsData.KToI20;
        const int n0 = 0, nL = 32;
        const float peakDecayFactor = 0.76592833836465f;

        foreach (float[] row in power) Array.Clear(row);

        if (is34 != ps.Common.Is34BandsOld)
        {
            Array.Clear(ps.PeakDecayNrg);
            Array.Clear(ps.PowerSmooth);
            Array.Clear(ps.PeakDecayDiffSmooth);
            foreach (float[][] band in ps.Delay) foreach (float[] slot in band) Array.Clear(slot);
            foreach (float[][][] band in ps.ApDelay)
                foreach (float[][] link in band)
                    foreach (float[] slot in link) Array.Clear(slot);
        }

        for (int k = 0; k < NrBands[mode]; k++)
            PsDsp.AddSquares(power[kToI[k]], s[k], nL - n0);

        // Transient detection
        for (int i = 0; i < NrParBands[mode]; i++)
        {
            for (int n = n0; n < nL; n++)
            {
                float decayedPeak = peakDecayFactor * peakDecayNrg[i];
                peakDecayNrg[i] = Math.Max(decayedPeak, power[i][n]);
                powerSmooth[i] += aSmooth * (power[i][n] - powerSmooth[i]);
                peakDecayDiffSmooth[i] +=
                    aSmooth * (peakDecayNrg[i] - power[i][n] - peakDecayDiffSmooth[i]);
                float denom = transientImpact * peakDecayDiffSmooth[i];
                transientGain[i][n] = denom > powerSmooth[i] ? powerSmooth[i] / denom : 1.0f;
            }
        }

        // Decorrelation and transient reduction: a cascade of three fractional-delay all-pass
        // sections below NR_ALLPASS_BANDS, a plain 14-slot delay up to SHORT_DELAY_BAND, and a
        // one-slot delay above it.
        int k2 = 0;
        for (; k2 < NrAllpassBands[mode]; k2++)
        {
            int b = kToI[k2];
            float gDecaySlope = 1.0f - DecaySlope * (k2 - DecayCutoff[mode]);
            gDecaySlope = Math.Clamp(gDecaySlope, 0.0f, 1.0f);

            ShiftDelay(delay[k2], s[k2], nL);
            for (int m = 0; m < PsLimits.ApLinks; m++)
                for (int i = 0; i < 5; i++)
                {
                    apDelay[k2][m][i][0] = apDelay[k2][m][i + PsLimits.NumQmfSlots][0];
                    apDelay[k2][m][i][1] = apDelay[k2][m][i + PsLimits.NumQmfSlots][1];
                }

            PsDsp.Decorrelate(output[k2], delay[k2], PsLimits.MaxDelay - 2, apDelay[k2],
                              PsTables.PhiFract[mode][k2], PsTables.QFractAllpass[mode][k2],
                              transientGain[b], gDecaySlope, nL - n0);
        }

        for (; k2 < ShortDelayBand[mode]; k2++)
        {
            int i = kToI[k2];
            ShiftDelay(delay[k2], s[k2], nL);
            PsDsp.MulPairSingle(output[k2], delay[k2], PsLimits.MaxDelay - 14,
                                transientGain[i], nL - n0);
        }

        for (; k2 < NrBands[mode]; k2++)
        {
            int i = kToI[k2];
            ShiftDelay(delay[k2], s[k2], nL);
            PsDsp.MulPairSingle(output[k2], delay[k2], PsLimits.MaxDelay - 1,
                                transientGain[i], nL - n0);
        }
    }

    /// <summary>
    /// The two memcpys every band of <c>decorrelation</c> starts with: carry the delay line's
    /// tail to the front, then append this frame's slots.
    /// </summary>
    private static void ShiftDelay(float[][] delay, float[][] source, int nL)
    {
        for (int i = 0; i < PsLimits.MaxDelay; i++)
        {
            delay[i][0] = delay[i + nL][0];
            delay[i][1] = delay[i + nL][1];
        }

        for (int i = 0; i < PsLimits.NumQmfSlots; i++)
        {
            delay[PsLimits.MaxDelay + i][0] = source[i][0];
            delay[PsLimits.MaxDelay + i][1] = source[i][1];
        }
    }

    /// <summary><c>remap34</c>. Returns the rows to read, which may be the input unchanged.</summary>
    private static sbyte[][] Remap34(sbyte[][] mapped, sbyte[][] par, int numPar, int numEnv,
                                     bool full)
    {
        if (numPar == 20 || numPar == 11)
        {
            for (int e = 0; e < numEnv; e++) MapIdx20To34(mapped[e], par[e], full);
            return mapped;
        }

        if (numPar == 10 || numPar == 5)
        {
            for (int e = 0; e < numEnv; e++) MapIdx10To34(mapped[e], par[e], full);
            return mapped;
        }

        return par;
    }

    /// <summary><c>remap20</c>.</summary>
    private static sbyte[][] Remap20(sbyte[][] mapped, sbyte[][] par, int numPar, int numEnv,
                                     bool full)
    {
        if (numPar == 34 || numPar == 17)
        {
            for (int e = 0; e < numEnv; e++) MapIdx34To20(mapped[e], par[e], full);
            return mapped;
        }

        if (numPar == 10 || numPar == 5)
        {
            for (int e = 0; e < numEnv; e++) MapIdx10To20(mapped[e], par[e], full);
            return mapped;
        }

        return par;
    }

    /// <summary><c>stereo_processing</c>: the 2x2 mix, interpolated across each envelope.</summary>
    private static void StereoProcessing(PsContext ps, float[][][] l, float[][][] r, bool is34)
    {
        int mode = is34 ? 1 : 0;
        PsCommonContext ps2 = ps.Common;
        float[][][] h11 = ps.H11, h12 = ps.H12, h21 = ps.H21, h22 = ps.H22;
        sbyte[] opdHist = ps.OpdHist, ipdHist = ps.IpdHist;
        ReadOnlySpan<sbyte> kToI = is34 ? PsData.KToI34 : PsData.KToI20;
        float[][][] hLut = PsLimits.Baseline || ps2.IccMode < 3 ? PsTables.HA : PsTables.HB;

        sbyte[][] iidMapped, iccMapped, ipdMapped = ps.IpdMapped, opdMapped = ps.OpdMapped;

        // The matrices carry over: envelope 0 of this frame is the last envelope of the previous.
        if (ps2.NumEnvOld != 0)
        {
            int last = ps2.NumEnvOld;
            Array.Copy(h11[0][last], h11[0][0], PsLimits.MaxNrIidIcc);
            Array.Copy(h11[1][last], h11[1][0], PsLimits.MaxNrIidIcc);
            Array.Copy(h12[0][last], h12[0][0], PsLimits.MaxNrIidIcc);
            Array.Copy(h12[1][last], h12[1][0], PsLimits.MaxNrIidIcc);
            Array.Copy(h21[0][last], h21[0][0], PsLimits.MaxNrIidIcc);
            Array.Copy(h21[1][last], h21[1][0], PsLimits.MaxNrIidIcc);
            Array.Copy(h22[0][last], h22[0][0], PsLimits.MaxNrIidIcc);
            Array.Copy(h22[1][last], h22[1][0], PsLimits.MaxNrIidIcc);
        }

        if (is34)
        {
            iidMapped = Remap34(ps.IidMapped, ps2.IidPar, ps2.NrIidPar, ps2.NumEnv, true);
            iccMapped = Remap34(ps.IccMapped, ps2.IccPar, ps2.NrIccPar, ps2.NumEnv, true);
            if (ps2.EnableIpdOpd)
            {
                ipdMapped = Remap34(ps.IpdMapped, ps2.IpdPar, ps2.NrIpdOpdPar, ps2.NumEnv, false);
                opdMapped = Remap34(ps.OpdMapped, ps2.OpdPar, ps2.NrIpdOpdPar, ps2.NumEnv, false);
            }

            if (!ps2.Is34BandsOld)
            {
                MapVal20To34(h11[0][0]);
                MapVal20To34(h11[1][0]);
                MapVal20To34(h12[0][0]);
                MapVal20To34(h12[1][0]);
                MapVal20To34(h21[0][0]);
                MapVal20To34(h21[1][0]);
                MapVal20To34(h22[0][0]);
                MapVal20To34(h22[1][0]);
                IpdOpdReset(ipdHist, opdHist);
            }
        }
        else
        {
            iidMapped = Remap20(ps.IidMapped, ps2.IidPar, ps2.NrIidPar, ps2.NumEnv, true);
            iccMapped = Remap20(ps.IccMapped, ps2.IccPar, ps2.NrIccPar, ps2.NumEnv, true);
            if (ps2.EnableIpdOpd)
            {
                ipdMapped = Remap20(ps.IpdMapped, ps2.IpdPar, ps2.NrIpdOpdPar, ps2.NumEnv, false);
                opdMapped = Remap20(ps.OpdMapped, ps2.OpdPar, ps2.NrIpdOpdPar, ps2.NumEnv, false);
            }

            if (ps2.Is34BandsOld)
            {
                MapVal34To20(h11[0][0]);
                MapVal34To20(h11[1][0]);
                MapVal34To20(h12[0][0]);
                MapVal34To20(h12[1][0]);
                MapVal34To20(h21[0][0]);
                MapVal34To20(h21[1][0]);
                MapVal34To20(h22[0][0]);
                MapVal34To20(h22[1][0]);
                IpdOpdReset(ipdHist, opdHist);
            }
        }

        bool ipdOpd = !PsLimits.Baseline && ps2.EnableIpdOpd;
        float[][] h = ps.H;
        float[][] hStep = ps.HStep;

        for (int e = 0; e < ps2.NumEnv; e++)
        {
            for (int b = 0; b < NrParBands[mode]; b++)
            {
                int row = iidMapped[e][b] + 7 + 23 * ps2.IidQuant;
                int column = iccMapped[e][b];
                float h11v = hLut[row][column][0];
                float h12v = hLut[row][column][1];
                float h21v = hLut[row][column][2];
                float h22v = hLut[row][column][3];

                if (ipdOpd && b < NrIpdOpdBands[mode])
                {
                    // The spec runs this smoother only when enable_ipdopd is set; the reference
                    // decoder runs it constantly, and FFmpeg follows the reference decoder.
                    int opdIdx = opdHist[b] * 8 + opdMapped[e][b];
                    int ipdIdx = ipdHist[b] * 8 + ipdMapped[e][b];
                    float opdRe = PsTables.PdReSmooth[opdIdx];
                    float opdIm = PsTables.PdImSmooth[opdIdx];
                    float ipdRe = PsTables.PdReSmooth[ipdIdx];
                    float ipdIm = PsTables.PdImSmooth[ipdIdx];
                    opdHist[b] = (sbyte)(opdIdx & 0x3F);
                    ipdHist[b] = (sbyte)(ipdIdx & 0x3F);

                    float ipdAdjRe = opdRe * ipdRe + opdIm * ipdIm;
                    float ipdAdjIm = opdIm * ipdRe - opdRe * ipdIm;
                    float h11i = h11v * opdIm;
                    h11v *= opdRe;
                    float h12i = h12v * ipdAdjIm;
                    h12v *= ipdAdjRe;
                    float h21i = h21v * opdIm;
                    h21v *= opdRe;
                    float h22i = h22v * ipdAdjIm;
                    h22v *= ipdAdjRe;
                    h11[1][e + 1][b] = h11i;
                    h12[1][e + 1][b] = h12i;
                    h21[1][e + 1][b] = h21i;
                    h22[1][e + 1][b] = h22i;
                }

                h11[0][e + 1][b] = h11v;
                h12[0][e + 1][b] = h12v;
                h21[0][e + 1][b] = h21v;
                h22[0][e + 1][b] = h22v;
            }

            for (int k = 0; k < NrBands[mode]; k++)
            {
                int start = ps2.BorderPosition[e];
                int stop = ps2.BorderPosition[e + 1];
                float width = 1.0f / (stop - start != 0 ? stop - start : 1);
                int b = kToI[k];

                h[0][0] = h11[0][e][b];
                h[0][1] = h12[0][e][b];
                h[0][2] = h21[0][e][b];
                h[0][3] = h22[0][e][b];

                if (ipdOpd)
                {
                    // FFmpeg's own comment asks whether this sign flip is necessary; it is kept
                    // because ps_04_new is unchanged by it and the reference decoder does it.
                    if ((is34 && k <= 13 && k >= 9) || (!is34 && k <= 1))
                    {
                        h[1][0] = -h11[1][e][b];
                        h[1][1] = -h12[1][e][b];
                        h[1][2] = -h21[1][e][b];
                        h[1][3] = -h22[1][e][b];
                    }
                    else
                    {
                        h[1][0] = h11[1][e][b];
                        h[1][1] = h12[1][e][b];
                        h[1][2] = h21[1][e][b];
                        h[1][3] = h22[1][e][b];
                    }
                }

                // AAC_MSUB31_V3(x, y, z) is (x - y) * z.
                hStep[0][0] = (h11[0][e + 1][b] - h[0][0]) * width;
                hStep[0][1] = (h12[0][e + 1][b] - h[0][1]) * width;
                hStep[0][2] = (h21[0][e + 1][b] - h[0][2]) * width;
                hStep[0][3] = (h22[0][e + 1][b] - h[0][3]) * width;

                if (ipdOpd)
                {
                    hStep[1][0] = (h11[1][e + 1][b] - h[1][0]) * width;
                    hStep[1][1] = (h12[1][e + 1][b] - h[1][1]) * width;
                    hStep[1][2] = (h21[1][e + 1][b] - h[1][2]) * width;
                    hStep[1][3] = (h22[1][e + 1][b] - h[1][3]) * width;
                }

                if (stop - start != 0)
                {
                    if (ipdOpd)
                        PsDsp.StereoInterpolateIpdOpd(l[k], 1 + start, r[k], 1 + start,
                                                      h, hStep, stop - start);
                    else
                        PsDsp.StereoInterpolate(l[k], 1 + start, r[k], 1 + start,
                                                h, hStep, stop - start);
                }
            }
        }
    }

    /// <summary>
    /// <c>ff_ps_apply</c>: turn the single QMF spectrum in <paramref name="left"/> into a stereo
    /// pair, writing the second channel into <paramref name="right"/>.
    /// </summary>
    /// <param name="top">The highest QMF band SBR produced, <c>kx[1] + m[1]</c>.</param>
    public static void Apply(PsContext ps, float[][][] left, float[][][] right, int top)
    {
        const int len = 32;
        bool is34 = ps.Common.Is34Bands;
        int mode = is34 ? 1 : 0;

        top += NrBands[mode] - 64;
        if (top < 0)
            throw new InvalidOperationException(
                $"PS: SBR reported a top band of {top - NrBands[mode] + 64}, which is below the "
              + "lowest band PS can start at. The C indexes its delay line from here and would "
              + "write behind it.");

        for (int k = top; k < NrBands[mode]; k++)
            foreach (float[] slot in ps.Delay[k]) Array.Clear(slot);

        if (top < NrAllpassBands[mode])
            for (int k = top; k < NrAllpassBands[mode]; k++)
                foreach (float[][] link in ps.ApDelay[k])
                    foreach (float[] slot in link) Array.Clear(slot);

        HybridAnalysis(ps, ps.LBuf, left, is34, len);
        Decorrelation(ps, ps.RBuf, ps.LBuf, is34);
        StereoProcessing(ps, ps.LBuf, ps.RBuf, is34);
        HybridSynthesis(left, ps.LBuf, is34, len);
        HybridSynthesis(right, ps.RBuf, is34, len);
    }
}

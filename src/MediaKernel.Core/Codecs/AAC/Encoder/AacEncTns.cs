// MediaKernel - AAC temporal noise shaping, encoder side
//
// A translation of libavcodec/aacenc_tns.c. TNS runs a linear predictor along the FREQUENCY
// axis, which shapes quantisation noise in time: without it, a transient's noise is spread
// evenly over the frame and is audible as a pre-echo ahead of the attack.
//
// Two details here are easy to get wrong in a way nothing catches until a frame differs:
// the AR filter reads its history from pcoeffs, the spectrum BEFORE the filter, while writing
// into coeffs - using the values it is writing would be a different filter and not invertible
// by the decoder. And compute_lpc_coefs runs in FLOAT here, where FLAC includes the same C
// with LPC_TYPE set to double.

using System;
using MediaKernel.Core.Codecs.Audio.Flac;

namespace MediaKernel.Core.Codecs.AAC.Encoder;

/// <summary><c>aacenc_tns.c</c>: deciding, applying and writing the TNS filters.</summary>
internal static class AacEncTns
{
    /// <summary>Coefficient resolution in long windows. <c>TNS_Q_BITS</c>.</summary>
    private const int TnsQBits = 4;

    /// <summary>Coefficient resolution in short windows. <c>TNS_Q_BITS_IS8</c>.</summary>
    private const int TnsQBitsIs8 = 4;

    /// <summary>TNS is used only when the LPC gain is inside these. <c>TNS_GAIN_THRESHOLD_LOW</c>.</summary>
    private const float GainThresholdLow = 1.4f;

    /// <summary><c>TNS_GAIN_THRESHOLD_HIGH</c>.</summary>
    private const float GainThresholdHigh = 1.16f * GainThresholdLow;

    /// <summary>The largest order the format allows. <c>TNS_MAX_ORDER</c>.</summary>
    private const int TnsMaxOrder = 20;

    /// <summary>
    /// <c>compress_coeffs</c>: drop the high bit of every coefficient index when none of them
    /// needs it.
    /// </summary>
    /// <remarks>
    /// FFmpeg's own comment says this compression "is simply not lossless as it should be on
    /// any decoder tested" - it is enabled anyway, by a <c>#define</c> that is always set, and
    /// so it is translated as it stands rather than as it is described.
    /// </remarks>
    public static int CompressCoeffs(Span<int> coef, int order, int cBits)
    {
        int lowIdx = cBits != 0 ? 4 : 2;
        int shiftVal = cBits != 0 ? 8 : 4;
        int highIdx = cBits != 0 ? 11 : 5;

        for (int i = 0; i < order; i++)
        {
            if (coef[i] >= lowIdx && coef[i] <= highIdx)
                return 0;
        }
        for (int i = 0; i < order; i++)
            coef[i] -= coef[i] > highIdx ? shiftVal : 0;
        return 1;
    }

    /// <summary><c>ff_aac_encode_tns_info</c>: write the filters into the bitstream.</summary>
    public static void EncodeTnsInfo(PutBitContext pb, AacSingleChannelElement sce)
    {
        AacTemporalNoiseShaping tns = sce.Tns;
        int is8 = sce.Ics.WindowSequence[0] == AacWindowSequence.EightShort ? 1 : 0;
        int cBits = is8 != 0 ? (TnsQBitsIs8 == 4 ? 1 : 0) : (TnsQBits == 4 ? 1 : 0);

        if (tns.Present == 0)
            return;

        for (int i = 0; i < sce.Ics.NumWindows; i++)
        {
            pb.PutBits(2 - is8, (uint)tns.NFilt[i]);
            if (tns.NFilt[i] == 0)
                continue;

            pb.PutBits(1, (uint)cBits);
            for (int filt = 0; filt < tns.NFilt[i]; filt++)
            {
                int at = i * AacTemporalNoiseShaping.MaxFilters + filt;
                pb.PutBits(6 - 2 * is8, (uint)tns.Length[at]);
                pb.PutBits(5 - 2 * is8, (uint)tns.Order[at]);
                if (tns.Order[at] == 0)
                    continue;

                pb.PutBits(1, (uint)tns.Direction[at]);
                int coefCompress = CompressCoeffs(tns.CoefIdxOf(i, filt), tns.Order[at], cBits);
                pb.PutBits(1, (uint)coefCompress);
                int coefLen = cBits + 3 - coefCompress;
                for (int w = 0; w < tns.Order[at]; w++)
                    pb.PutBits(coefLen, (uint)tns.CoefIdxOf(i, filt)[w]);
            }
        }
    }

    /// <summary><c>ff_aac_apply_tns</c>: run the filters over the spectrum.</summary>
    public static void ApplyTns(AacSingleChannelElement sce)
    {
        AacTemporalNoiseShaping tns = sce.Tns;
        AacIndividualChannelStream ics = sce.Ics;
        int mmm = Math.Min(ics.TnsMaxBands, ics.MaxSfb);
        Span<float> lpc = stackalloc float[TnsMaxOrder];

        for (int w = 0; w < ics.NumWindows; w++)
        {
            int bottom = ics.NumSwb;
            for (int filt = 0; filt < tns.NFilt[w]; filt++)
            {
                int at = w * AacTemporalNoiseShaping.MaxFilters + filt;
                int top = bottom;
                bottom = Math.Max(0, top - tns.Length[at]);
                int order = tns.Order[at];
                if (order == 0)
                    continue;

                // tns_decode_coef. In FLOAT: aacenc_tns.c includes lpc_functions.h without
                // LPC_USE_DOUBLE, so this recursion runs at single precision, unlike FLAC's.
                float unused = 0;
                Lpc.ComputeLpcCoefs<float>(tns.CoefOf(w, filt), 0, 0, order, lpc, 0, 0,
                                           fail: false, normalize: false, ref unused,
                                           hasErrPtr: false);

                int start = ics.SwbOffset[Math.Min(bottom, mmm)];
                int end = ics.SwbOffset[Math.Min(top, mmm)];
                int size = end - start;
                if (size <= 0)
                    continue;

                int inc;
                if (tns.Direction[at] != 0)
                {
                    inc = -1;
                    start = end - 1;
                }
                else
                {
                    inc = 1;
                }
                start += w * 128;

                // The AR filter. Its history comes from pcoeffs - the spectrum as it was
                // before this filter started writing into coeffs.
                for (int m = 0; m < size; m++, start += inc)
                {
                    for (int i = 1; i <= Math.Min(m, order); i++)
                        sce.Coeffs[start] += lpc[i - 1] * sce.PCoeffs[start - i * inc];
                }
            }
        }
    }

    /// <summary>
    /// <c>quantize_coefs</c>: each reflection coefficient to the nearest table entry.
    /// </summary>
    public static void QuantizeCoefs(ReadOnlySpan<double> coef, Span<int> idx, Span<float> lpc,
                                     int order, int cBits)
    {
        ReadOnlySpan<float> quantArr = AacTnsDecoder.TnsCoefficientTable(cBits);
        for (int i = 0; i < order; i++)
        {
            idx[i] = AacEncUtils.QuantArrayIdx((float)coef[i], quantArr, cBits != 0 ? 16 : 8);
            lpc[i] = quantArr[idx[i]];
        }
    }

    /// <summary>
    /// <c>ff_aac_search_for_tns</c>: decide whether this channel gets TNS, and with what.
    /// </summary>
    /// <param name="lpcContext">The encoder's own LPC context, reused across frames.</param>
    /// <param name="sce">The channel.</param>
    /// <param name="psyBands">
    /// This channel's psychoacoustic bands, indexed <c>w * 16 + g</c>, whose energies decide
    /// which way each filter points.
    /// </param>
    /// <param name="sampleRateIndex"><c>s->samplerate_index</c>.</param>
    public static void SearchForTns(Lpc lpcContext, AacSingleChannelElement sce,
                                    ReadOnlySpan<AacPsyBand> psyBands, int sampleRateIndex)
    {
        AacTemporalNoiseShaping tns = sce.Tns;
        int count = 0;
        Span<double> coefs = stackalloc double[Lpc.MaxLpcOrder];

        int mmm = Math.Min(sce.Ics.TnsMaxBands, sce.Ics.MaxSfb);
        int is8 = sce.Ics.WindowSequence[0] == AacWindowSequence.EightShort ? 1 : 0;
        int cBits = is8 != 0 ? (TnsQBitsIs8 == 4 ? 1 : 0) : (TnsQBits == 4 ? 1 : 0);
        int sfbStart = Math.Clamp(AacEncTables.TnsMinSfb(is8)[sampleRateIndex], 0, mmm);
        int sfbEnd = Math.Clamp(sce.Ics.NumSwb, 0, mmm);
        int order = is8 != 0 ? 7 : 12;
        int slant = sce.Ics.WindowSequence[0] == AacWindowSequence.LongStop ? 1
                  : sce.Ics.WindowSequence[0] == AacWindowSequence.LongStart ? 0
                  : 2;
        int sfbLen = sfbEnd - sfbStart;
        int coefLen = sce.Ics.SwbOffset[sfbEnd] - sce.Ics.SwbOffset[sfbStart];
        int nFilt = is8 != 0 ? 1 : order != TnsMaxOrder ? 2 : 3;

        if (coefLen <= 0 || sfbLen <= 0)
        {
            tns.Present = 0;
            return;
        }

        for (int w = 0; w < sce.Ics.NumWindows; w++)
        {
            Span<float> en = stackalloc float[4] { 0.0f, 0.0f, 0.0f, 0.0f };
            int ocStart = 0;
            int coefStart = sce.Ics.SwbOffset[sfbStart];

            if (nFilt == 2)
            {
                for (int g = sfbStart; g < sce.Ics.NumSwb && g <= sfbEnd; g++)
                {
                    AacPsyBand band = psyBands[w * 16 + g];
                    if (g > sfbStart + sfbLen / 2)
                        en[1] += band.Energy;      // End
                    else
                        en[0] += band.Energy;      // Start
                }
                en[2] = en[0];
            }
            else
            {
                for (int g = sfbStart; g < sce.Ics.NumSwb && g <= sfbEnd; g++)
                {
                    AacPsyBand band = psyBands[w * 16 + g];
                    if (g > sfbStart + sfbLen / 2 + sfbLen / 4)
                        en[2] += band.Energy;      // End
                    else if (g > sfbStart + sfbLen / 2 - sfbLen / 4)
                        en[1] += band.Energy;      // Middle
                    else
                        en[0] += band.Energy;      // Start
                }
                en[3] = en[0];
            }

            double gain = lpcContext.CalcRefCoefsF(
                sce.Coeffs.AsSpan(w * 128 + coefStart), coefLen, order, coefs);

            if (order == 0 || !double.IsFinite(gain)
                || gain < GainThresholdLow || gain > GainThresholdHigh)
                continue;

            tns.NFilt[w] = nFilt;
            for (int g = 0; g < tns.NFilt[w]; g++)
            {
                int at = w * AacTemporalNoiseShaping.MaxFilters + g;
                tns.Direction[at] = slant != 2 ? slant : (en[g] < en[g + 1] ? 1 : 0);
                tns.Order[at] = order / tns.NFilt[w];
                tns.Length[at] = sfbLen / tns.NFilt[w];
                QuantizeCoefs(coefs[ocStart..], tns.CoefIdxOf(w, g), tns.CoefOf(w, g),
                              tns.Order[at], cBits);
                ocStart += tns.Order[at];
            }
            count++;
        }

        tns.Present = count != 0 ? 1 : 0;
    }
}

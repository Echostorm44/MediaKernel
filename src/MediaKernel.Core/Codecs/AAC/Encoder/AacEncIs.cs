// MediaKernel - AAC intensity stereo, encoder side
//
// A translation of libavcodec/aacenc_is.c. Above about 6 kHz the ear locates sound by envelope
// rather than by waveform, so a band can be sent once with a per-channel scale instead of
// twice. The search costs both ways round - the sum and the difference - and keeps whichever
// is cheaper, if either is cheaper than coding the two channels separately.
//
// Every square root in here is the DOUBLE one applied to floats, because that is what the C
// does: `sqrt()` on a float argument promotes. Using MathF.Sqrt would be a different scale
// factor in the last place, and the scale is written into the bitstream.

using System;

namespace MediaKernel.Core.Codecs.AAC.Encoder;

/// <summary><c>aacenc_is.c</c>: deciding which bands become intensity stereo.</summary>
internal static class AacEncIs
{
    /// <summary>The lowest frequency intensity stereo is allowed at. <c>INT_STEREO_LOW_LIMIT</c>.</summary>
    private const int LowLimitHz = 6100;

    /// <summary><c>struct AACISError</c>: what costing one band one way round produced.</summary>
    internal struct AacIsError
    {
        /// <summary>Set when the intensity-coded band costs no more than the two channels.</summary>
        public int Pass;

        /// <summary>-1 or +1.</summary>
        public int Phase;

        /// <summary><c>dist2 - dist1</c>.</summary>
        public float Error;

        /// <summary>The cost of coding both channels.</summary>
        public float Dist1;

        /// <summary>The cost of coding one and scaling it.</summary>
        public float Dist2;

        /// <summary>The energy of the sum or the difference, whichever this phase used.</summary>
        public float Ener01;
    }

    /// <summary>
    /// <c>aac_is_encoding_err</c>: what one band costs coded as intensity stereo at one phase,
    /// against what it costs coded as two channels.
    /// </summary>
    public static AacIsError AacIsEncodingErr(AacCoder.BandCostScratch scratch,
                                              AacChannelElement cpe, ReadOnlySpan<AacPsyBand> psy0,
                                              ReadOnlySpan<AacPsyBand> psy1, float lambda,
                                              int start, int w, int g, float ener0, float ener1,
                                              float ener01, int phase)
    {
        AacSingleChannelElement sce0 = cpe.Ch[0];
        AacSingleChannelElement sce1 = cpe.Ch[1];
        float[] l = sce0.Coeffs;
        float[] r = sce1.Coeffs;

        // The C carves the encoder's one scratch buffer into four 256-float windows.
        Span<float> l34 = scratch.Scoefs.AsSpan(256 * 0, 256);
        Span<float> r34 = scratch.Scoefs.AsSpan(256 * 1, 256);
        Span<float> isCoeffs = scratch.Scoefs.AsSpan(256 * 2, 256);
        Span<float> i34 = scratch.Scoefs.AsSpan(256 * 3, 256);

        float dist1 = 0.0f, dist2 = 0.0f;
        var isError = default(AacIsError);

        if (ener01 <= 0 || ener0 <= 0)
        {
            isError.Pass = 0;
            return isError;
        }

        for (int w2 = 0; w2 < sce0.Ics.GroupLen[w]; w2++)
        {
            AacPsyBand band0 = psy0[(w + w2) * 16 + g];
            AacPsyBand band1 = psy1[(w + w2) * 16 + g];
            int isSfIdx = Math.Max(1, sce0.SfIdx[w * 16 + g] - 4);
            float e0134 = phase * AacEncUtils.PosPow34(ener1 / ener0);
            float distSpecErr = 0.0f;
            float minthr = Math.Min(band0.Threshold, band1.Threshold);
            int swbSize = sce0.Ics.SwbSizes[g];

            for (int i = 0; i < swbSize; i++)
            {
                // Double sqrt, as the C's promotion gives, and the product with it is computed
                // in double before it lands back in a float.
                isCoeffs[i] = (float)((l[start + (w + w2) * 128 + i]
                                       + phase * r[start + (w + w2) * 128 + i])
                                      * Math.Sqrt(ener0 / ener01));
            }

            AacEncDsp.AbsPow34V(l34, l.AsSpan(start + (w + w2) * 128), swbSize);
            AacEncDsp.AbsPow34V(r34, r.AsSpan(start + (w + w2) * 128), swbSize);
            AacEncDsp.AbsPow34V(i34, isCoeffs, swbSize);

            float maxval = AacEncUtils.FindMaxVal(1, swbSize, i34);
            int isBandType = AacEncUtils.FindMinBook(maxval, isSfIdx);

            dist1 += AacCoder.QuantizeBandCost(scratch, l.AsSpan(start + (w + w2) * 128), l34,
                swbSize, sce0.SfIdx[w * 16 + g], sce0.BandType[w * 16 + g],
                lambda / band0.Threshold, float.PositiveInfinity, out _, out _);
            dist1 += AacCoder.QuantizeBandCost(scratch, r.AsSpan(start + (w + w2) * 128), r34,
                sce1.Ics.SwbSizes[g], sce1.SfIdx[w * 16 + g], sce1.BandType[w * 16 + g],
                lambda / band1.Threshold, float.PositiveInfinity, out _, out _);
            dist2 += AacCoder.QuantizeBandCost(scratch, isCoeffs, i34, swbSize,
                isSfIdx, isBandType, lambda / minthr, float.PositiveInfinity, out _, out _);

            for (int i = 0; i < swbSize; i++)
            {
                distSpecErr += (l34[i] - i34[i]) * (l34[i] - i34[i]);
                distSpecErr += (r34[i] - i34[i] * e0134) * (r34[i] - i34[i] * e0134);
            }
            distSpecErr *= lambda / minthr;
            dist2 += distSpecErr;
        }

        isError.Pass = dist2 <= dist1 ? 1 : 0;
        isError.Phase = phase;
        isError.Error = dist2 - dist1;
        isError.Dist1 = dist1;
        isError.Dist2 = dist2;
        isError.Ener01 = ener01;

        return isError;
    }

    /// <summary>
    /// <c>ff_aac_search_for_is</c>: mark the bands that are cheaper as intensity stereo.
    /// </summary>
    public static void SearchForIs(AacCoder.BandCostScratch scratch, AacChannelElement cpe,
                                   ReadOnlySpan<AacPsyBand> psy0, ReadOnlySpan<AacPsyBand> psy1,
                                   float lambda, int sampleRate)
    {
        AacSingleChannelElement sce0 = cpe.Ch[0];
        AacSingleChannelElement sce1 = cpe.Ch[1];
        int count = 0, prevSf1 = -1, prevBt = -1, prevIs = 0;
        float freqMult = sampleRate / (1024.0f / sce0.Ics.NumWindows) / 2.0f;
        Span<byte> nextband1 = stackalloc byte[128];

        if (cpe.CommonWindow == 0)
            return;

        // Scout out the next non-zero bands.
        AacEncUtils.InitNextbandMap(sce1.Ics.NumWindows, sce1.Ics.NumSwb, sce1.Ics.GroupLen,
                                    sce1.Zeroes, sce1.BandType, nextband1);

        for (int w = 0; w < sce0.Ics.NumWindows; w += sce0.Ics.GroupLen[w])
        {
            int start = 0;
            for (int g = 0; g < sce0.Ics.NumSwb; g++)
            {
                if (start * freqMult > LowLimitHz * (lambda / 170.0f)
                    && cpe.Ch[0].BandType[w * 16 + g] != (int)AacBandType.Noise
                    && cpe.Ch[0].Zeroes[w * 16 + g] == 0
                    && cpe.Ch[1].BandType[w * 16 + g] != (int)AacBandType.Noise
                    && cpe.Ch[1].Zeroes[w * 16 + g] == 0
                    && AacEncUtils.SfdeltaCanRemoveBand(sce1.SfIdx, nextband1, prevSf1, w * 16 + g))
                {
                    float ener0 = 0.0f, ener1 = 0.0f, ener01 = 0.0f, ener01p = 0.0f;
                    for (int w2 = 0; w2 < sce0.Ics.GroupLen[w]; w2++)
                    {
                        for (int i = 0; i < sce0.Ics.SwbSizes[g]; i++)
                        {
                            float coef0 = sce0.Coeffs[start + (w + w2) * 128 + i];
                            float coef1 = sce1.Coeffs[start + (w + w2) * 128 + i];
                            ener0 += coef0 * coef0;
                            ener1 += coef1 * coef1;
                            ener01 += (coef0 + coef1) * (coef0 + coef1);
                            ener01p += (coef0 - coef1) * (coef0 - coef1);
                        }
                    }

                    AacIsError phErr1 = AacIsEncodingErr(scratch, cpe, psy0, psy1, lambda,
                        start, w, g, ener0, ener1, ener01p, -1);
                    AacIsError phErr2 = AacIsEncodingErr(scratch, cpe, psy0, psy1, lambda,
                        start, w, g, ener0, ener1, ener01, +1);

                    AacIsError best = phErr1.Pass != 0 && phErr1.Error < phErr2.Error
                        ? phErr1 : phErr2;

                    if (best.Pass != 0)
                    {
                        cpe.IsMask[w * 16 + g] = 1;
                        cpe.MsMask[w * 16 + g] = 0;
                        cpe.Ch[0].IsEner[w * 16 + g] = (float)Math.Sqrt(ener0 / best.Ener01);
                        cpe.Ch[1].IsEner[w * 16 + g] = ener0 / ener1;
                        cpe.Ch[1].BandType[w * 16 + g] = (byte)(best.Phase > 0
                            ? AacBandType.Intensity : AacBandType.Intensity2);

                        if (prevIs != 0 && prevBt != cpe.Ch[1].BandType[w * 16 + g])
                        {
                            // Flip the M/S mask and pick the other codebook, which codes the
                            // same band more efficiently once the phase has changed.
                            cpe.MsMask[w * 16 + g] = 1;
                            cpe.Ch[1].BandType[w * 16 + g] = (byte)(best.Phase > 0
                                ? AacBandType.Intensity2 : AacBandType.Intensity);
                        }

                        prevBt = cpe.Ch[1].BandType[w * 16 + g];
                        count++;
                    }
                }

                if (sce1.Zeroes[w * 16 + g] == 0
                    && sce1.BandType[w * 16 + g] < (int)AacBandType.Reserved)
                    prevSf1 = sce1.SfIdx[w * 16 + g];

                prevIs = cpe.IsMask[w * 16 + g];
                start += sce0.Ics.SwbSizes[g];
            }
        }

        cpe.IsMode = count != 0 ? 1 : 0;
    }
}

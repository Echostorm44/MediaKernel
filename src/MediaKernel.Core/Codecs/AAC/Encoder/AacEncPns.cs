// MediaKernel - AAC perceptual noise substitution, encoder side
//
// A translation of search_for_pns and mark_pns from libavcodec/aaccoder.c, and of
// ff_pns_bits from aaccoder_twoloop.h.
//
// A band that is noise-like above about 4 kHz can be sent as a single level instead of as
// coefficients, and the decoder fills it with its own random numbers. The encoder's job is to
// decide where that is inaudible - the band has to be spread out in energy, close to the
// masking threshold, and steady across the windows of a short group, because PNS destroys a
// transient. search_for_pns then COSTS the substitution against coding the band properly and
// only takes it when it also wins on rate.
//
// The level it sends is measured from noise the encoder generates itself, with the same LCG
// the decoder uses, so the energy it writes compensates for the quantisation error of that
// particular noise rather than of noise in general. That makes the search depend on the
// random state, which is why the state is threaded through rather than hidden.

using System;

namespace MediaKernel.Core.Codecs.AAC.Encoder;

/// <summary>Noise substitution: which bands can take it, and which ones should.</summary>
internal static class AacEncPns
{
    /// <summary><c>NOISE_LOW_LIMIT</c>: the lowest frequency PNS is allowed at, in Hz.</summary>
    private const int NoiseLowLimit = 4000;

    /// <summary><c>NOISE_SPREAD_THRESHOLD</c>.</summary>
    private const float NoiseSpreadThreshold = 0.9f;

    /// <summary><c>NOISE_LAMBDA_REPLACE</c>.</summary>
    private const float NoiseLambdaReplace = 1.948f;

    /// <summary>
    /// <c>ff_pns_bits</c>: what a noise band costs to switch to. Nine bits for a codebook and
    /// a scalefactor, five when the band before it was noise too and only the delta is sent.
    /// </summary>
    public static int PnsBits(AacSingleChannelElement sce, int w, int g) =>
        g == 0 || sce.Zeroes[w * 16 + g - 1] == 0 || sce.CanPns[w * 16 + g - 1] == 0 ? 9 : 5;

    /// <summary>
    /// The band index at which the spectrum is cut off, which both PNS functions derive the
    /// same way. <c>AAC_CUTOFF_FROM_BITRATE</c> and the arithmetic around it in the C.
    /// </summary>
    /// <remarks>
    /// The C's comment says to keep this in sync with twoloop's cutoff selection; it is
    /// repeated verbatim in three places there, so it is one method here and the divergence is
    /// recorded rather than the duplication.
    /// </remarks>
    private static int CutoffBin(AacEncoderSettings settings, float lambda, int wlen)
    {
        // bit_rate * 1024.0 is a double in the C, and the result truncates to int.
        int refbits = (int)(settings.BitRate * 1024.0 / settings.SampleRate
                            / (settings.QScale ? 2.0f : settings.Channels)
                            * (lambda / 120.0f));

        // Keep this in sync with twoloop's cutoff selection.
        const float rateBandwidthMultiplier = 1.5f;
        int frameBitRate = settings.QScale
            ? (int)(refbits * rateBandwidthMultiplier * settings.SampleRate / 1024)
            : (int)(settings.BitRate / settings.Channels);

        frameBitRate = (int)(frameBitRate * 1.15f);

        int bandwidth = settings.Cutoff > 0
            ? settings.Cutoff
            : Math.Max(3000, CutoffFromBitRate(frameBitRate, 1, settings.SampleRate));

        return bandwidth * 2 * wlen / settings.SampleRate;
    }

    /// <summary><c>AAC_CUTOFF_FROM_BITRATE</c>, which is integer arithmetic throughout.</summary>
    private static int CutoffFromBitRate(int bitRate, int channels, int sampleRate)
    {
        if (bitRate == 0)
            return sampleRate / 2;

        int a = Math.Max(bitRate / channels / 5, bitRate / channels * 15 / 32 - 5500);
        int b = 3000 + bitRate / channels / 4;
        int c = 12000 + bitRate / channels / 16;
        return Math.Min(Math.Min(Math.Min(a, Math.Min(b, c)), 22000), sampleRate / 2);
    }

    /// <summary>
    /// <c>mark_pns</c>: which bands are ALLOWED to be noise. Nothing is costed here - this
    /// only sets <c>can_pns</c>, which the two-loop search reads when it decides what a band
    /// would cost to zero.
    /// </summary>
    public static void MarkPns(AacSingleChannelElement sce, ReadOnlySpan<AacPsyBand> psy,
                               AacEncoderSettings settings, float lambda)
    {
        int wlen = 1024 / sce.Ics.NumWindows;
        float freqMult = settings.SampleRate * 0.5f / wlen;
        float spreadThreshold = MathF.Min(0.75f,
            NoiseSpreadThreshold * MathF.Max(0.5f, lambda / 100.0f));
        float pnsTransientEnergyR = MathF.Min(0.7f, lambda / 140.0f);
        int cutoff = CutoffBin(settings, lambda, wlen);

        sce.BandType.CopyTo(sce.BandAlt, 0);

        for (int w = 0; w < sce.Ics.NumWindows; w += sce.Ics.GroupLen[w])
        {
            for (int g = 0; g < sce.Ics.NumSwb; g++)
            {
                float sfbEnergy = 0.0f, threshold = 0.0f, spread = 2.0f;
                float minEnergy = -1.0f, maxEnergy = 0.0f;
                int start = sce.Ics.SwbOffset[g];
                float freq = start * freqMult;
                float freqBoost = MathF.Max(0.88f * freq / NoiseLowLimit, 1.0f);

                if (freq < NoiseLowLimit || start >= cutoff)
                {
                    sce.CanPns[w * 16 + g] = 0;
                    continue;
                }

                for (int w2 = 0; w2 < sce.Ics.GroupLen[w]; w2++)
                {
                    AacPsyBand band = psy[(w + w2) * 16 + g];
                    sfbEnergy += band.Energy;
                    spread = MathF.Min(spread, band.Spread);
                    threshold += band.Threshold;
                    if (w2 == 0)
                    {
                        minEnergy = maxEnergy = band.Energy;
                    }
                    else
                    {
                        minEnergy = MathF.Min(minEnergy, band.Energy);
                        maxEnergy = MathF.Max(maxEnergy, band.Energy);
                    }
                }

                // Noise-like, near enough the threshold to be masked, and steady across the
                // group - a transient would be flattened by substitution.
                sce.PnsEner[w * 16 + g] = sfbEnergy;
                sce.CanPns[w * 16 + g] =
                    sfbEnergy < threshold * MathF.Sqrt(1.5f / freqBoost)
                    || spread < spreadThreshold
                    || minEnergy < pnsTransientEnergyR * maxEnergy ? (byte)0 : (byte)1;
            }
        }
    }

    /// <summary>
    /// <c>search_for_pns</c>: which bands actually become noise. Same three admissibility
    /// tests as <see cref="MarkPns"/>, then a rate/distortion comparison against coding the
    /// band for real.
    /// </summary>
    /// <param name="randomState">
    /// <c>s->random_state</c>. The noise is generated here, so the state advances, and the
    /// level the band is coded at depends on which numbers came out.
    /// </param>
    public static void SearchForPns(AacCoder.BandCostScratch scratch,
                                    AacSingleChannelElement sce, ReadOnlySpan<AacPsyBand> psy,
                                    AacEncoderSettings settings, float lambda,
                                    ref int randomState)
    {
        int wlen = 1024 / sce.Ics.NumWindows;
        Span<float> pns = scratch.Scoefs.AsSpan(128 * 0, 128);
        Span<float> pns34 = scratch.Scoefs.AsSpan(128 * 1, 128);
        Span<float> nor34 = scratch.Scoefs.AsSpan(128 * 3, 128);
        Span<byte> nextband = stackalloc byte[128];

        float freqMult = settings.SampleRate * 0.5f / wlen;
        float thrMult = NoiseLambdaReplace * (100.0f / lambda);
        float spreadThreshold = MathF.Min(0.75f,
            NoiseSpreadThreshold * MathF.Max(0.5f, lambda / 100.0f));
        float distBias = Math.Clamp(4.0f * 120 / lambda, 0.25f, 4.0f);
        float pnsTransientEnergyR = MathF.Min(0.7f, lambda / 140.0f);
        int cutoff = CutoffBin(settings, lambda, wlen);

        int prev = -1000, prevSf = -1;

        sce.BandType.CopyTo(sce.BandAlt, 0);
        AacEncUtils.InitNextbandMap(sce.Ics.NumWindows, sce.Ics.NumSwb, sce.Ics.GroupLen,
                                    sce.Zeroes, sce.BandType, nextband);

        for (int w = 0; w < sce.Ics.NumWindows; w += sce.Ics.GroupLen[w])
        {
            int wstart = w * 128;
            for (int g = 0; g < sce.Ics.NumSwb; g++)
            {
                float dist1 = 0.0f, dist2 = 0.0f;
                float pnsEnergy = 0.0f;
                float sfbEnergy = 0.0f, threshold = 0.0f, spread = 2.0f;
                float minEnergy = -1.0f, maxEnergy = 0.0f;
                int start = wstart + sce.Ics.SwbOffset[g];
                float freq = (start - wstart) * freqMult;
                float freqBoost = MathF.Max(0.88f * freq / NoiseLowLimit, 1.0f);

                if (freq < NoiseLowLimit || start - wstart >= cutoff)
                {
                    if (sce.Zeroes[w * 16 + g] == 0)
                        prevSf = sce.SfIdx[w * 16 + g];
                    continue;
                }

                for (int w2 = 0; w2 < sce.Ics.GroupLen[w]; w2++)
                {
                    AacPsyBand band = psy[(w + w2) * 16 + g];
                    sfbEnergy += band.Energy;
                    spread = MathF.Min(spread, band.Spread);
                    threshold += band.Threshold;
                    if (w2 == 0)
                    {
                        minEnergy = maxEnergy = band.Energy;
                    }
                    else
                    {
                        minEnergy = MathF.Min(minEnergy, band.Energy);
                        maxEnergy = MathF.Max(maxEnergy, band.Energy);
                    }
                }

                // Ramps down at ~8000 Hz and loosens the distortion threshold.
                float distThresh = Math.Clamp(2.5f * NoiseLowLimit / freq, 0.5f, 2.5f) * distBias;

                // The same three tests as mark_pns, except that the near-threshold one is
                // relaxed for a band the quantiser has already zeroed: filling a hole with
                // noise beats leaving it empty.
                if ((sce.Zeroes[w * 16 + g] == 0
                     && !AacEncUtils.SfdeltaCanRemoveBand(sce.SfIdx, nextband, prevSf, w * 16 + g))
                    || ((sce.Zeroes[w * 16 + g] != 0 || sce.BandAlt[w * 16 + g] == 0)
                        && sfbEnergy < threshold * MathF.Sqrt(1.0f / freqBoost))
                    || spread < spreadThreshold
                    || (sce.Zeroes[w * 16 + g] == 0 && sce.BandAlt[w * 16 + g] != 0
                        && sfbEnergy > threshold * thrMult * freqBoost)
                    || minEnergy < pnsTransientEnergyR * maxEnergy)
                {
                    sce.PnsEner[w * 16 + g] = sfbEnergy;
                    if (sce.Zeroes[w * 16 + g] == 0)
                        prevSf = sce.SfIdx[w * 16 + g];
                    continue;
                }

                float pnsTgtEnergy = sfbEnergy * MathF.Min(1.0f, spread * spread);
                int noiseSfi = Math.Clamp((int)MathF.Round(MathF.Log2(pnsTgtEnergy) * 2), -100, 155);
                float noiseAmp = -AacUsacTables.Pow2SfTab[noiseSfi + AacEncUtils.PowSf2Zero];

                if (prev != -1000)
                {
                    int noiseSfdiff = noiseSfi - prev + AacEncUtils.ScaleDiffZero;
                    if (noiseSfdiff < 0 || noiseSfdiff > 2 * AacEncUtils.ScaleMaxDiff)
                    {
                        if (sce.Zeroes[w * 16 + g] == 0)
                            prevSf = sce.SfIdx[w * 16 + g];
                        continue;
                    }
                }

                int swbSize = sce.Ics.SwbSizes[g];
                for (int w2 = 0; w2 < sce.Ics.GroupLen[w]; w2++)
                {
                    int startC = (w + w2) * 128 + sce.Ics.SwbOffset[g];
                    AacPsyBand band = psy[(w + w2) * 16 + g];

                    for (int i = 0; i < swbSize; i++)
                    {
                        randomState = AacEncUtils.LcgRandom((uint)randomState);
                        pns[i] = randomState;
                    }

                    float bandEnergy = ScalarProduct(pns, pns, swbSize);
                    float scale = noiseAmp / MathF.Sqrt(bandEnergy);
                    for (int i = 0; i < swbSize; i++)
                        pns[i] = pns[i] * scale;
                    pnsEnergy += ScalarProduct(pns, pns, swbSize);

                    AacEncDsp.AbsPow34V(nor34, sce.Coeffs.AsSpan(startC), swbSize);
                    AacEncDsp.AbsPow34V(pns34, pns, swbSize);

                    dist1 += AacCoder.QuantizeBandCost(scratch, sce.Coeffs.AsSpan(startC), nor34,
                        swbSize, sce.SfIdx[(w + w2) * 16 + g], sce.BandAlt[(w + w2) * 16 + g],
                        lambda / band.Threshold, float.PositiveInfinity, out _, out _);

                    // Estimated as 5 bits for the scalefactor and 4 for the codebook, plus the
                    // spread energy weighted by lambda over the threshold.
                    dist2 += band.Energy / (band.Spread * band.Spread) * lambda * distThresh
                             / band.Threshold;
                }

                dist2 += g != 0 && sce.BandType[w * 16 + g - 1] == (int)AacBandType.Noise ? 5 : 9;

                // The level written compensates for the quantisation error of the noise that
                // was actually drawn, not of noise in general.
                float energyRatio = pnsTgtEnergy / pnsEnergy;
                sce.PnsEner[w * 16 + g] = energyRatio * pnsTgtEnergy;

                if (sce.Zeroes[w * 16 + g] != 0 || sce.BandAlt[w * 16 + g] == 0
                    || (energyRatio > 0.85f && energyRatio < 1.25f && dist2 < dist1))
                {
                    sce.BandType[w * 16 + g] = (byte)AacBandType.Noise;
                    sce.Zeroes[w * 16 + g] = 0;
                    prev = noiseSfi;
                }
                else if (sce.Zeroes[w * 16 + g] == 0)
                {
                    prevSf = sce.SfIdx[w * 16 + g];
                }
            }
        }
    }

    /// <summary>
    /// <c>scalarproduct_float</c>, summed in order. The C dispatches this through
    /// AVFloatDSPContext, whose SIMD variants sum in a different order and therefore give a
    /// different answer; the scalar one is what a port has to match.
    /// </summary>
    private static float ScalarProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int len)
    {
        float p = 0.0f;
        for (int i = 0; i < len; i++)
            p += a[i] * b[i];
        return p;
    }
}

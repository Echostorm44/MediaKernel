using System;

namespace MediaKernel.Core.Codecs.AAC.Sbr;

/// <summary>
/// High frequency adjustment (14496-3 sp04 p217): scales the generated high band so its energy
/// matches what the encoder measured, and fills in noise and sinusoids where the patched signal
/// cannot supply them.
/// </summary>
/// <remarks>
/// The patched high band has roughly the right shape but the wrong level, and it inherits the low
/// band's tonal structure. This stage fixes both: a gain per band and envelope brings the energy to
/// the transmitted target, a noise floor is mixed in where the copy is too tonal, and an explicit
/// sinusoid is added where the encoder flagged one. The gains are limited per band group and then
/// smoothed over time, which is what stops the result from pumping.
/// </remarks>
internal sealed class SbrEnvelopeAdjustment
{
    private const int MaxEnvelopes = 8;
    private const int MaxBands = 48;
    private const int SmoothingSlots = 42;

    /// <summary>Noise floor scalefactors are offset by this before dequantisation.</summary>
    private const int NoiseFloorOffset = 6;

    /// <summary>Filter applied across time to the gains, when smoothing is enabled.</summary>
    private static ReadOnlySpan<float> SmoothingFilter =>
    [
        0.33333333333333f,
        0.30150283239582f,
        0.21816949906249f,
        0.11516383427084f,
        0.03183050093751f,
    ];

    /// <summary>Gain ceilings per limiter setting: -3 dB, 0 dB, +3 dB, and effectively off.</summary>
    private static ReadOnlySpan<float> LimiterGain => [0.70795f, 1.0f, 1.41254f, 1e10f];

    private readonly float[][] originalEnvelope = Rows(MaxEnvelopes, MaxBands);
    private readonly float[][] mappedNoise = Rows(MaxEnvelopes, MaxBands);
    private readonly byte[][] mappedSine = ByteRows(MaxEnvelopes, MaxBands);
    private readonly float[][] currentEnvelope = Rows(MaxEnvelopes, MaxBands);
    private readonly float[][] gain = Rows(MaxEnvelopes, MaxBands);
    private readonly float[][] noiseLevel = Rows(MaxEnvelopes, MaxBands);
    private readonly float[][] sineLevel = Rows(MaxEnvelopes, MaxBands);

    private readonly float[][] gainHistory = Rows(SmoothingSlots, MaxBands);
    private readonly float[][] noiseHistory = Rows(SmoothingSlots, MaxBands);

    private readonly float[] smoothedGain = new float[MaxBands];
    private readonly float[] smoothedNoise = new float[MaxBands];

    /// <summary>Intermediate results, exposed so each stage can be checked against FFmpeg.</summary>
    public float[][] OriginalEnvelope => originalEnvelope;
    public float[][] MappedNoise => mappedNoise;
    public float[][] CurrentEnvelope => currentEnvelope;
    public float[][] Gain => gain;
    public float[][] NoiseLevel => noiseLevel;
    public float[][] SineLevel => sineLevel;

    /// <summary><c>s_mapped</c>: whether each subband's band carries a sinusoid.</summary>
    public byte[][] MappedSine => mappedSine;

    private static float[][] Rows(int rows, int columns)
    {
        var result = new float[rows][];
        for (int i = 0; i < rows; i++)
            result[i] = new float[columns];
        return result;
    }

    private static byte[][] ByteRows(int rows, int columns)
    {
        var result = new byte[rows][];
        for (int i = 0; i < rows; i++)
            result[i] = new byte[columns];
        return result;
    }

    /// <summary>Per-limiter-band gain diagnostics; null unless set.</summary>
    public System.Collections.Generic.List<string>? GainTrace;

    public void Reset()
    {
        foreach (var row in gainHistory) Array.Clear(row);
        foreach (var row in noiseHistory) Array.Clear(row);
    }

    /// <summary>
    /// Turns the quantised envelope and noise scalefactors into linear levels
    /// (14496-3 sp04 p203) for every channel of an element. Coupled stereo has to be done for the
    /// pair at once, because there the first channel carries a level and the second a pan position,
    /// and neither is meaningful on its own.
    /// </summary>
    public static void Dequantise(SbrContext sbr, int channelCount)
    {
        if (sbr.Coupling && channelCount >= 2)
        {
            DequantiseCoupled(sbr);
            return;
        }

        for (int ch = 0; ch < channelCount && ch < sbr.Channels.Length; ch++)
            Dequantise(sbr.Channels[ch], sbr.NoiseBandCount, sbr.BandCount);
    }

    /// <summary>
    /// The uncoupled case: each band's scalefactor is a level in its own right.
    /// </summary>
    public static void Dequantise(SbrChannelData channel, int noiseBandCount, int[] bandCount)
    {
        for (int e = 1; e <= channel.EnvelopeCount; e++)
        {
            int bands = bandCount[channel.FrequencyResolution[e]];
            for (int k = 0; k < bands; k++)
            {
                int quantised = channel.EnvelopeScalefactors[e][k];
                float value = channel.AmplitudeResolution
                    ? Exp2Fi(quantised + 6)
                    : (float)(Exp2Fi((quantised >> 1) + 6) * HalfStep[quantised & 1]);

                // A scalefactor this large means corrupt data; muting the band is safer than
                // letting an enormous gain through to the output.
                channel.EnvelopeLevels[e][k] = value > 1e20f ? 1f : value;
            }
        }

        for (int e = 1; e <= channel.NoiseFloorCount; e++)
        {
            for (int k = 0; k < noiseBandCount; k++)
                channel.NoiseLevels[e][k] =
                    Exp2Fi(NoiseFloorOffset - channel.NoiseScalefactors[e][k]);
        }
    }

    /// <summary>
    /// The coupled case (14496-3 sp04 p203): channel 0's scalefactor is the summed level of the
    /// pair and channel 1's is a pan position relative to a fixed offset, so the two levels come
    /// out of one division rather than two independent exponentials.
    /// </summary>
    private static void DequantiseCoupled(SbrContext sbr)
    {
        var left = sbr.Channels[0];
        var right = sbr.Channels[1];

        // Both channels share channel 0's grid and amplitude resolution when coupled, so the pan
        // offset is read from there too.
        int panOffset = left.AmplitudeResolution ? 12 : 24;

        for (int e = 1; e <= left.EnvelopeCount; e++)
        {
            int bands = sbr.BandCount[left.FrequencyResolution[e]];
            for (int k = 0; k < bands; k++)
            {
                int level = left.EnvelopeScalefactors[e][k];
                int pan = panOffset - right.EnvelopeScalefactors[e][k];

                float total = left.AmplitudeResolution
                    ? Exp2Fi(level + 7)
                    : (float)(Exp2Fi((level >> 1) + 7) * HalfStep[level & 1]);
                float ratio = left.AmplitudeResolution
                    ? Exp2Fi(pan)
                    : (float)(Exp2Fi(pan >> 1) * HalfStep[pan & 1]);

                // A level this large means corrupt data; muting the band is safer than letting an
                // enormous gain through to the output.
                if (total > 1e20f) total = 1;

                float shared = total / (1.0f + ratio);
                left.EnvelopeLevels[e][k] = shared;
                right.EnvelopeLevels[e][k] = shared * ratio;
            }
        }

        for (int e = 1; e <= left.NoiseFloorCount; e++)
        {
            for (int k = 0; k < sbr.NoiseBandCount; k++)
            {
                float total = Exp2Fi(NoiseFloorOffset - left.NoiseScalefactors[e][k] + 1);
                float ratio = Exp2Fi(12 - right.NoiseScalefactors[e][k]);
                float shared = total / (1.0f + ratio);
                left.NoiseLevels[e][k] = shared;
                right.NoiseLevels[e][k] = shared * ratio;
            }
        }
    }

    /// <summary>
    /// A half-step in the amplitude resolution: <c>exp2_tab</c>, in double as the C has it.
    /// </summary>
    private static ReadOnlySpan<double> HalfStep => [1.0, 1.4142135623730951];

    /// <summary>
    /// <c>exp2fi</c>: two to an integer power, assembled from the float's exponent bits.
    /// </summary>
    /// <remarks>
    /// This was <c>Math.Pow(2.0, exponent)</c>, with the products and quotients around it taken
    /// in double too. The values agree for integer exponents; the arithmetic after them does
    /// not. The C divides and multiplies these in float, and SBR's envelope levels feed the
    /// inverse filter that FFmpeg's own comment calls not numerically stable - so doing the
    /// surrounding arithmetic more precisely than the reference decoder is still doing it
    /// differently, and the difference is amplified rather than damped. The overflow and
    /// subnormal arms matter too: a corrupt scalefactor reaches them, where <c>Math.Pow</c>
    /// would quietly return a denormal double.
    /// </remarks>
    private static float Exp2Fi(int x)
    {
        if (x >= -126 && x <= 128) return BitConverter.Int32BitsToSingle((x + 127) << 23);
        if (x > 128) return float.PositiveInfinity;
        if (x > -150) return BitConverter.Int32BitsToSingle(1 << (x + 149));
        return 0;
    }

    /// <summary>
    /// Spreads the per-band scalefactors across every QMF subband they cover, and works out where
    /// sinusoids belong (14496-3 sp04 p217).
    /// </summary>
    public bool Map(SbrContext sbr, SbrTables tables, SbrChannelData channel)
    {
        // The C is "memset(ch_data->s_indexmapped[1], 0, 7*sizeof(ch_data->s_indexmapped[1]))",
        // and s_indexmapped is [9][48]: that clears SEVEN rows, 1 through 7, not one. Clearing
        // only row 1 leaves rows 2..7 holding the previous frame's flags, and the loop below
        // writes each row at the sinusoid midpoints alone - so every other position in envelope 1
        // and above keeps a stale value. Envelope 0 is correct either way, which is what made
        // this hard to see: it shows up only on a stream that uses add_harmonic across more than
        // one envelope, and then as a wrong gain formula rather than as anything obviously broken.
        for (int row = 1; row <= 7; row++)
            Array.Clear(channel.SineIndexMapped[row]);

        for (int e = 0; e < channel.EnvelopeCount; e++)
        {
            int resolution = channel.FrequencyResolution[e + 1];
            var table = resolution != 0 ? tables.High : tables.Low;
            int bands = sbr.BandCount[resolution];

            // The derived tables must line up with the start subband, or every index below is off.
            if (tables.StartSubband != table[0])
                return false;

            for (int i = 0; i < bands; i++)
            {
                for (int m = table[i]; m < table[i + 1]; m++)
                    originalEnvelope[e][m - tables.StartSubband] = channel.EnvelopeLevels[e + 1][i];
            }

            // With two noise floors, envelopes at or past the second border use the second floor.
            int floor = (channel.NoiseFloorCount > 1 &&
                         channel.EnvelopeBorders[e] >= channel.NoiseBorders[1]) ? 1 : 0;

            for (int i = 0; i < sbr.NoiseBandCount; i++)
            {
                for (int m = tables.Noise[i]; m < tables.Noise[i + 1]; m++)
                    mappedNoise[e][m - tables.StartSubband] = channel.NoiseLevels[floor + 1][i];
            }

            for (int i = 0; i < sbr.BandCount[1]; i++)
            {
                if (!channel.AddHarmonicFlag)
                    continue;

                // A flagged sinusoid sits at the midpoint of its high-resolution band.
                int midpoint = (tables.High[i] + tables.High[i + 1]) >> 1;
                int index = midpoint - tables.StartSubband;
                bool carriedOver = channel.SineIndexMapped[0][index] == 1;
                channel.SineIndexMapped[e + 1][index] = (byte)(channel.AddHarmonic[i] *
                    ((e >= channel.SineStartEnvelope[1] || carriedOver) ? 1 : 0));
            }

            // A band counts as containing a sinusoid if any of its subbands does; the noise floor
            // is suppressed across the whole band in that case.
            for (int i = 0; i < bands; i++)
            {
                byte present = 0;
                for (int m = table[i]; m < table[i + 1]; m++)
                {
                    if (channel.SineIndexMapped[e + 1][m - tables.StartSubband] != 0)
                    {
                        present = 1;
                        break;
                    }
                }

                for (int m = table[i]; m < table[i + 1]; m++)
                    mappedSine[e][m - tables.StartSubband] = present;
            }
        }

        channel.SineIndexMapped[channel.EnvelopeCount].AsSpan().CopyTo(channel.SineIndexMapped[0]);
        return true;
    }

    /// <summary>
    /// Measures the energy actually present in the generated high band, per envelope and band
    /// (14496-3 sp04 p218). This is what the transmitted target is compared against.
    /// </summary>
    public void EstimateEnvelope(SbrContext sbr, SbrTables tables, SbrChannelData channel,
                                 float[][][] highBand)
    {
        int start = tables.StartSubband;

        if (sbr.InterpolationFrequency != 0)
        {
            for (int e = 0; e < channel.EnvelopeCount; e++)
            {
                float reciprocal = 0.5f / (channel.EnvelopeBorders[e + 1] - channel.EnvelopeBorders[e]);
                int lower = channel.EnvelopeBorders[e] * 2 + SbrHighFrequency.EnvelopeAdjustmentOffset;
                int upper = channel.EnvelopeBorders[e + 1] * 2 + SbrHighFrequency.EnvelopeAdjustmentOffset;

                if (lower >= SbrHighFrequency.BufferSlots)
                    return;

                for (int m = 0; m < tables.SubbandCount; m++)
                    currentEnvelope[e][m] = SumSquare(highBand[m + start], lower, upper) * reciprocal;
            }
        }
        else
        {
            for (int e = 0; e < channel.EnvelopeCount; e++)
            {
                int size = 2 * (channel.EnvelopeBorders[e + 1] - channel.EnvelopeBorders[e]);
                int lower = channel.EnvelopeBorders[e] * 2 + SbrHighFrequency.EnvelopeAdjustmentOffset;
                int upper = channel.EnvelopeBorders[e + 1] * 2 + SbrHighFrequency.EnvelopeAdjustmentOffset;
                var table = channel.FrequencyResolution[e + 1] != 0 ? tables.High : tables.Low;

                if (lower >= SbrHighFrequency.BufferSlots)
                    return;

                for (int p = 0; p < sbr.BandCount[channel.FrequencyResolution[e + 1]]; p++)
                {
                    float sum = 0;
                    for (int k = table[p]; k < table[p + 1]; k++)
                        sum += SumSquare(highBand[k], lower, upper);

                    sum /= size * (table[p + 1] - table[p]);
                    for (int k = table[p]; k < table[p + 1]; k++)
                        currentEnvelope[e][k - start] = sum;
                }
            }
        }
    }

    private static float SumSquare(float[][] band, int lower, int upper)
    {
        float sum = 0;
        for (int i = lower; i < upper && i < band.Length; i++)
            sum += band[i][0] * band[i][0] + band[i][1] * band[i][1];
        return sum;
    }

    /// <summary>
    /// Works out the gain, noise and sinusoid level for every band (14496-3 sp04 p219).
    /// </summary>
    /// <remarks>
    /// Two limits apply in turn. A ceiling stops any one band being boosted far beyond its
    /// neighbours, which is what would otherwise turn a quiet patched band into a whistle; then a
    /// boost of at most 4 dB compensates for the energy that ceiling removed.
    /// </remarks>
    public void CalculateGains(SbrContext sbr, SbrTables tables, SbrChannelData channel)
    {
        for (int e = 0; e < channel.EnvelopeCount; e++)
        {
            // The envelope where a sinusoid starts gets no noise added.
            int delta = (e == channel.SineStartEnvelope[1] || e == channel.SineStartEnvelope[0]) ? 0 : 1;

            for (int k = 0; k < tables.LimiterCount; k++)
            {
                int from = tables.Limiter[k] - tables.StartSubband;
                int to = tables.Limiter[k + 1] - tables.StartSubband;

                for (int m = from; m < to && m < MaxBands; m++)
                {
                    float scaled = originalEnvelope[e][m] / (1.0f + mappedNoise[e][m]);
                    noiseLevel[e][m] = MathF.Sqrt(scaled * mappedNoise[e][m]);
                    sineLevel[e][m] = MathF.Sqrt(scaled * channel.SineIndexMapped[e + 1][m]);

                    gain[e][m] = mappedSine[e][m] == 0
                        ? MathF.Sqrt(originalEnvelope[e][m] /
                                     ((1.0f + currentEnvelope[e][m]) * (1.0f + mappedNoise[e][m] * delta)))
                        : MathF.Sqrt(originalEnvelope[e][m] * mappedNoise[e][m] /
                                     ((1.0f + currentEnvelope[e][m]) * (1.0f + mappedNoise[e][m])));

                    gain[e][m] += float.Epsilon;
                }

                float targetSum = 0, actualSum = 0;
                for (int m = from; m < to && m < MaxBands; m++)
                {
                    targetSum += originalEnvelope[e][m];
                    actualSum += currentEnvelope[e][m];
                }

                float maximum = LimiterGain[(int)sbr.LimiterGains] *
                                MathF.Sqrt((float.Epsilon + targetSum) / (float.Epsilon + actualSum));
                maximum = MathF.Min(100000f, maximum);

                for (int m = from; m < to && m < MaxBands; m++)
                {
                    float noiseCeiling = noiseLevel[e][m] * maximum / gain[e][m];
                    noiseLevel[e][m] = MathF.Min(noiseLevel[e][m], noiseCeiling);
                    gain[e][m] = MathF.Min(gain[e][m], maximum);
                }

                targetSum = actualSum = 0;
                for (int m = from; m < to && m < MaxBands; m++)
                {
                    targetSum += originalEnvelope[e][m];
                    actualSum += currentEnvelope[e][m] * gain[e][m] * gain[e][m]
                               + sineLevel[e][m] * sineLevel[e][m]
                               + (delta != 0 && sineLevel[e][m] == 0 ? 1 : 0) * noiseLevel[e][m] * noiseLevel[e][m];
                }

                float boost = MathF.Sqrt((float.Epsilon + targetSum) / (float.Epsilon + actualSum));
                boost = MathF.Min(1.584893192f, boost);

                GainTrace?.Add($"MKGAIN e={e} k={k} lg={sbr.LimiterGains} maximum={maximum:F6} " +
                               $"boost={boost:F6} s0={targetSum:F4} s1={actualSum:F4} from={from} to={to}");

                for (int m = from; m < to && m < MaxBands; m++)
                {
                    gain[e][m] *= boost;
                    noiseLevel[e][m] *= boost;
                    sineLevel[e][m] *= boost;
                }
            }
        }
    }

    /// <summary>
    /// Applies the gains and mixes in noise and sinusoids, producing the adjusted high band
    /// (14496-3 sp04 p220).
    /// </summary>
    public void Assemble(SbrContext sbr, SbrTables tables, SbrChannelData channel,
                         float[][][] highBand, float[][][] output, bool reset)
    {
        int smoothingLength = sbr.SmoothingMode != 0 ? 0 : 4;
        int start = tables.StartSubband;
        int bandCount = tables.SubbandCount;
        int noiseIndex = channel.NoiseIndex;
        int sineIndex = channel.SineIndex;

        // Prime the smoothing history: from this frame's own gains after a reset, otherwise from
        // where the previous frame left off, so the filter does not step at the frame boundary.
        if (reset)
        {
            for (int i = 0; i < smoothingLength; i++)
            {
                gain[0].AsSpan(0, bandCount).CopyTo(gainHistory[i + 2 * channel.EnvelopeBorders[0]]);
                noiseLevel[0].AsSpan(0, bandCount).CopyTo(noiseHistory[i + 2 * channel.EnvelopeBorders[0]]);
            }
        }
        else if (smoothingLength != 0)
        {
            for (int i = 0; i < 4; i++)
            {
                int destination = i + 2 * channel.EnvelopeBorders[0];
                int source = i + 2 * channel.PreviousFinalBorder;
                if (destination < SmoothingSlots && source < SmoothingSlots)
                {
                    gainHistory[source].AsSpan().CopyTo(gainHistory[destination]);
                    noiseHistory[source].AsSpan().CopyTo(noiseHistory[destination]);
                }
            }
        }

        for (int e = 0; e < channel.EnvelopeCount; e++)
        {
            for (int i = 2 * channel.EnvelopeBorders[e]; i < 2 * channel.EnvelopeBorders[e + 1]; i++)
            {
                if (smoothingLength + i >= SmoothingSlots)
                    break;
                gain[e].AsSpan(0, bandCount).CopyTo(gainHistory[smoothingLength + i]);
                noiseLevel[e].AsSpan(0, bandCount).CopyTo(noiseHistory[smoothingLength + i]);
            }
        }

        for (int e = 0; e < channel.EnvelopeCount; e++)
        {
            for (int i = 2 * channel.EnvelopeBorders[e]; i < 2 * channel.EnvelopeBorders[e + 1]; i++)
            {
                if (i >= output.Length || smoothingLength + i >= SmoothingSlots)
                    break;

                float[] gains, noises;
                if (smoothingLength != 0 && e != channel.SineStartEnvelope[0] && e != channel.SineStartEnvelope[1])
                {
                    for (int m = 0; m < bandCount; m++)
                    {
                        smoothedGain[m] = 0;
                        smoothedNoise[m] = 0;
                        for (int j = 0; j <= smoothingLength; j++)
                        {
                            smoothedGain[m] += gainHistory[i + smoothingLength - j][m] * SmoothingFilter[j];
                            smoothedNoise[m] += noiseHistory[i + smoothingLength - j][m] * SmoothingFilter[j];
                        }
                    }
                    gains = smoothedGain;
                    noises = smoothedNoise;
                }
                else
                {
                    gains = gainHistory[i + smoothingLength];
                    noises = noiseHistory[i];
                }

                int slot = i + SbrHighFrequency.EnvelopeAdjustmentOffset;
                for (int m = 0; m < bandCount; m++)
                {
                    output[i][m + start][0] = highBand[m + start][slot][0] * gains[m];
                    output[i][m + start][1] = highBand[m + start][slot][1] * gains[m];
                }

                if (e != channel.SineStartEnvelope[0] && e != channel.SineStartEnvelope[1])
                {
                    ApplyNoise(output[i], sineLevel[e], noises, ref noiseIndex, start, bandCount, sineIndex);
                }
                else
                {
                    // At a sinusoid's onset the noise is left out and only the tone is added, with
                    // the phase rotating through the four quadrants.
                    int odd = sineIndex & 1;
                    int signA = 1 - ((sineIndex + (start & 1)) & 2);
                    int signB = (signA ^ -odd) + odd;

                    for (int m = 0; m < bandCount; m++)
                    {
                        float value = sineLevel[e][m] * ((m & 1) == 0 ? signA : signB);
                        output[i][m + start][odd] += value;
                    }

                    noiseIndex = (noiseIndex + bandCount) & 0x1ff;
                }

                sineIndex = (sineIndex + 1) & 3;
            }
        }

        channel.NoiseIndex = noiseIndex;
        channel.SineIndex = sineIndex;
    }

    /// <summary>
    /// Adds the noise floor, or a sinusoid where one is present. The noise comes from a fixed
    /// table so every decoder produces the same result for the same stream.
    /// </summary>
    private static void ApplyNoise(float[][] slot, float[] sine, float[] noise,
                                   ref int noiseIndex, int start, int bandCount, int sineIndex)
    {
        var table = SbrNoiseTable.Values;

        float phase0 = sineIndex switch { 0 => 1f, 2 => -1f, _ => 0f };
        float phase1 = sineIndex switch
        {
            1 => 1 - 2 * (start & 1),
            3 => -(1 - 2 * (start & 1)),
            _ => 0f,
        };

        for (int m = 0; m < bandCount; m++)
        {
            noiseIndex = (noiseIndex + 1) & 0x1ff;
            if (sine[m] != 0)
            {
                slot[m + start][0] += sine[m] * phase0;
                slot[m + start][1] += sine[m] * phase1;
            }
            else
            {
                slot[m + start][0] += noise[m] * table[noiseIndex * 2];
                slot[m + start][1] += noise[m] * table[noiseIndex * 2 + 1];
            }
            phase1 = -phase1;
        }
    }
}

using System;

namespace MediaKernel.Core.Codecs.AAC.Sbr;

/// <summary>
/// Builds the SBR frequency band tables (14496-3 sp04 p194-197). Port of FFmpeg's
/// sbr_make_f_master, sbr_make_f_derived, sbr_hf_calc_npatches and sbr_make_f_tablelim.
/// </summary>
/// <remarks>
/// These tables decide which QMF subbands each envelope scalefactor covers, so the parser needs
/// them before it can read envelope data at all - the band counts set the loop bounds. They depend
/// only on the header, and are rebuilt whenever it changes.
/// </remarks>
internal static class SbrFrequencyTables
{
    /// <summary>Start frequency offsets per sample rate class (aacsbrdata.h sbr_offset).</summary>
    private static readonly sbyte[][] StartFrequencyOffsets =
    [
        [-8, -7, -6, -5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7],          // 16000
        [-5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 9, 11, 13],           // 22050
        [-5, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 9, 11, 13, 16],           // 24000
        [-6, -4, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 9, 11, 13, 16],           // 32000
        [-4, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 9, 11, 13, 16, 20],           // 44100..64000
        [-2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 9, 11, 13, 16, 20, 24],           // > 64000
    ];

    /// <summary>Limiter band spacing: 2^(0.49/n) for n = 1.2, 2, 3 bands per octave.</summary>
    private static readonly float[] LimiterBandsWarped =
    [
        1.32715174233856803909f,
        1.18509277094158210129f,
        1.11987160404675912501f,
    ];

    /// <summary>
    /// Rebuilds every derived table. Returns false when the header describes a layout the spec
    /// forbids, in which case SBR must be turned off rather than applied with bad bounds.
    /// </summary>
    public static bool Build(SbrContext sbr, SbrTables tables)
    {
        return MakeMaster(sbr, tables) && MakeDerived(sbr, tables);
    }

    private static int SampleRateClass(int sampleRate) => sampleRate switch
    {
        16000 => 0,
        22050 => 1,
        24000 => 2,
        32000 => 3,
        44100 or 48000 or 64000 => 4,
        88200 or 96000 or 128000 or 176400 or 192000 => 5,
        _ => -1,
    };

    /// <summary>Master frequency band table (14496-3 sp04 p194).</summary>
    private static bool MakeMaster(SbrContext sbr, SbrTables tables)
    {
        int rateClass = SampleRateClass(sbr.SampleRate);
        if (rateClass < 0)
            return false;

        var offsets = StartFrequencyOffsets[rateClass];
        var spectrum = sbr.SpectrumParameters;

        int reference = sbr.SampleRate < 32000 ? 3000 : sbr.SampleRate < 64000 ? 4000 : 5000;
        int startMin = ((reference << 7) + (sbr.SampleRate >> 1)) / sbr.SampleRate;
        int stopMin = ((reference << 8) + (sbr.SampleRate >> 1)) / sbr.SampleRate;

        sbr.K[0] = startMin + offsets[spectrum.StartFrequency];

        if (spectrum.StopFrequency < 14)
        {
            Span<short> stopDeltas = stackalloc short[13];
            sbr.K[2] = stopMin;
            MakeBands(stopDeltas, stopMin, 64, 13);
            stopDeltas.Sort();
            for (int k = 0; k < spectrum.StopFrequency; k++)
                sbr.K[2] += stopDeltas[k];
        }
        else if (spectrum.StopFrequency == 14)
        {
            sbr.K[2] = 2 * sbr.K[0];
        }
        else if (spectrum.StopFrequency == 15)
        {
            sbr.K[2] = 3 * sbr.K[0];
        }
        else
        {
            return false;
        }

        sbr.K[2] = Math.Min(64, sbr.K[2]);

        int maxSubbands = sbr.SampleRate <= 32000 ? 48 : sbr.SampleRate == 44100 ? 35 : 32;
        if (sbr.K[2] - sbr.K[0] > maxSubbands)
            return false;

        if (spectrum.FrequencyScale == 0)
        {
            // Linear spacing.
            int dk = spectrum.AlterScale + 1;
            tables.MasterCount = ((sbr.K[2] - sbr.K[0] + (dk & 2)) >> dk) << 1;
            if (!CheckMasterCount(tables.MasterCount, spectrum.CrossoverBand))
                return false;

            for (int k = 1; k <= tables.MasterCount; k++)
                tables.Master[k] = (short)dk;

            int difference = sbr.K[2] - sbr.K[0] - tables.MasterCount * dk;
            if (difference < 0)
            {
                tables.Master[1]--;
                if (difference < -1)
                    tables.Master[2]--;
            }
            else if (difference != 0)
            {
                tables.Master[tables.MasterCount]++;
            }

            tables.Master[0] = (short)sbr.K[0];
            for (int k = 1; k <= tables.MasterCount; k++)
                tables.Master[k] += tables.Master[k - 1];
        }
        else
        {
            // Warped (logarithmic) spacing, optionally split into two regions.
            int halfBands = 7 - spectrum.FrequencyScale;
            bool twoRegions;

            if (49 * sbr.K[2] > 110 * sbr.K[0])
            {
                twoRegions = true;
                sbr.K[1] = 2 * sbr.K[0];
            }
            else
            {
                twoRegions = false;
                sbr.K[1] = sbr.K[2];
            }

            int bands0 = (int)MathF.Round(halfBands * MathF.Log2(sbr.K[1] / (float)sbr.K[0])) * 2;
            if (bands0 <= 0)
                return false;

            Span<short> region0 = stackalloc short[49];
            region0[0] = 0;
            MakeBands(region0[1..], sbr.K[0], sbr.K[1], bands0);
            region0.Slice(1, bands0).Sort();
            int maxDelta0 = region0[bands0];

            region0[0] = (short)sbr.K[0];
            for (int k = 1; k <= bands0; k++)
            {
                if (region0[k] <= 0)
                    return false;
                region0[k] += region0[k - 1];
            }

            if (twoRegions)
            {
                Span<short> region1 = stackalloc short[49];
                float inverseWarp = spectrum.AlterScale != 0 ? 0.76923076923076923077f : 1.0f;
                int bands1 = (int)MathF.Round(halfBands * inverseWarp *
                                              MathF.Log2(sbr.K[2] / (float)sbr.K[1])) * 2;
                if (bands1 <= 0)
                    return false;

                MakeBands(region1[1..], sbr.K[1], sbr.K[2], bands1);

                int minDelta1 = short.MaxValue;
                for (int k = 1; k <= bands1; k++)
                    minDelta1 = Math.Min(minDelta1, region1[k]);

                // Keep the second region's bands from being narrower than the first's.
                if (minDelta1 < maxDelta0)
                {
                    region1.Slice(1, bands1).Sort();
                    int change = Math.Min(maxDelta0 - region1[1], (region1[bands1] - region1[1]) >> 1);
                    region1[1] += (short)change;
                    region1[bands1] -= (short)change;
                }

                region1.Slice(1, bands1).Sort();

                region1[0] = (short)sbr.K[1];
                for (int k = 1; k <= bands1; k++)
                {
                    if (region1[k] <= 0)
                        return false;
                    region1[k] += region1[k - 1];
                }

                tables.MasterCount = bands0 + bands1;
                if (!CheckMasterCount(tables.MasterCount, spectrum.CrossoverBand))
                    return false;

                region0[..(bands0 + 1)].CopyTo(tables.Master);
                region1.Slice(1, bands1).CopyTo(tables.Master.AsSpan(bands0 + 1));
            }
            else
            {
                tables.MasterCount = bands0;
                if (!CheckMasterCount(tables.MasterCount, spectrum.CrossoverBand))
                    return false;
                region0[..(bands0 + 1)].CopyTo(tables.Master);
            }
        }

        return true;
    }

    private static bool CheckMasterCount(int masterCount, int crossoverBand) =>
        masterCount > 0 && crossoverBand < masterCount;

    /// <summary>Derived frequency band tables (14496-3 sp04 p197).</summary>
    private static bool MakeDerived(SbrContext sbr, SbrTables tables)
    {
        int crossover = sbr.SpectrumParameters.CrossoverBand;

        sbr.BandCount[1] = tables.MasterCount - crossover;
        sbr.BandCount[0] = (sbr.BandCount[1] + 1) >> 1;

        tables.Master.AsSpan(crossover, sbr.BandCount[1] + 1).CopyTo(tables.High);
        tables.SubbandCount = tables.High[sbr.BandCount[1]] - tables.High[0];
        tables.StartSubband = tables.High[0];

        if (tables.StartSubband + tables.SubbandCount > 64 || tables.StartSubband > 32)
            return false;

        tables.Low[0] = tables.High[0];
        int odd = sbr.BandCount[1] & 1;
        for (int k = 1; k <= sbr.BandCount[0]; k++)
            tables.Low[k] = tables.High[2 * k - odd];

        sbr.NoiseBandCount = Math.Max(1, (int)MathF.Round(
            sbr.SpectrumParameters.NoiseBands * MathF.Log2(sbr.K[2] / (float)tables.StartSubband)));
        if (sbr.NoiseBandCount > 5)
        {
            sbr.NoiseBandCount = 1;
            return false;
        }

        tables.Noise[0] = tables.Low[0];
        int index = 0;
        for (int k = 1; k <= sbr.NoiseBandCount; k++)
        {
            index += (sbr.BandCount[0] - index) / (sbr.NoiseBandCount + 1 - k);
            tables.Noise[k] = tables.Low[index];
        }

        return CalculatePatches(sbr, tables) && MakeLimiterTable(sbr, tables);
    }

    /// <summary>
    /// Works out which low-band region each high band is copied from (14496-3 sp04 p199).
    /// </summary>
    private static bool CalculatePatches(SbrContext sbr, SbrTables tables)
    {
        int lastK = -1, lastMsb = -1, sb = 0;
        int msb = sbr.K[0];
        int usb = tables.StartSubband;
        int goalSubband = ((1000 << 11) + (sbr.SampleRate >> 1)) / sbr.SampleRate;

        tables.PatchCount = 0;

        int k;
        if (goalSubband < tables.StartSubband + tables.SubbandCount)
        {
            for (k = 0; tables.Master[k] < goalSubband; k++) { }
        }
        else
        {
            k = tables.MasterCount;
        }

        do
        {
            int odd = 0;
            if (k == lastK && msb == lastMsb)
                return false; // no progress: the patch layout cannot be satisfied

            lastK = k;
            lastMsb = msb;

            for (int i = k; i == k || sb > sbr.K[0] - 1 + msb - odd; i--)
            {
                if (i < 0)
                    return false;
                sb = tables.Master[i];
                odd = (sb + sbr.K[0]) & 1;
            }

            if (tables.PatchCount > 5)
                return false;

            tables.PatchSubbandCount[tables.PatchCount] = Math.Max(sb - usb, 0);
            tables.PatchStartSubband[tables.PatchCount] =
                sbr.K[0] - odd - tables.PatchSubbandCount[tables.PatchCount];

            if (tables.PatchSubbandCount[tables.PatchCount] > 0)
            {
                usb = sb;
                msb = sb;
                tables.PatchCount++;
            }
            else
            {
                msb = tables.StartSubband;
            }

            if (tables.Master[k] - sb < 3)
                k = tables.MasterCount;
        }
        while (sb != tables.StartSubband + tables.SubbandCount);

        // A final patch narrower than three subbands is dropped; it would contribute almost
        // nothing and the spec caps the count at five.
        if (tables.PatchCount > 1 && tables.PatchSubbandCount[tables.PatchCount - 1] < 3)
            tables.PatchCount--;

        return true;
    }

    /// <summary>
    /// Builds the gain limiter band table, which stops a single patch boundary producing a large
    /// step in the reconstructed envelope.
    /// </summary>
    /// <summary>
    /// <c>sbr_make_f_tablelim</c>. Called on its own when a header changes only the limiter-band
    /// count, which must not disturb anything else.
    /// </summary>
    public static bool MakeLimiterTable(SbrContext sbr, SbrTables tables)
    {
        if (sbr.LimiterBands == 0)
        {
            tables.Limiter[0] = tables.Low[0];
            tables.Limiter[1] = tables.Low[sbr.BandCount[0]];
            tables.LimiterCount = 1;
            return true;
        }

        float warp = LimiterBandsWarped[sbr.LimiterBands - 1];

        Span<short> patchBorders = stackalloc short[7];
        patchBorders[0] = (short)tables.StartSubband;
        for (int k = 1; k <= tables.PatchCount; k++)
            patchBorders[k] = (short)(patchBorders[k - 1] + tables.PatchSubbandCount[k - 1]);

        tables.Low.AsSpan(0, sbr.BandCount[0] + 1).CopyTo(tables.Limiter);
        if (tables.PatchCount > 1)
            patchBorders.Slice(1, tables.PatchCount - 1).CopyTo(tables.Limiter.AsSpan(sbr.BandCount[0] + 1));

        int total = tables.PatchCount + sbr.BandCount[0];
        tables.Limiter.AsSpan(0, total).Sort();

        tables.LimiterCount = sbr.BandCount[0] + tables.PatchCount - 1;

        // Merge bands that sit closer together than the warp factor allows, preferring to keep
        // borders that coincide with a patch boundary.
        int inIndex = 1, outIndex = 0;
        while (outIndex < tables.LimiterCount)
        {
            short current = tables.Limiter[inIndex];
            short kept = tables.Limiter[outIndex];

            if (current >= kept * warp)
            {
                tables.Limiter[++outIndex] = current;
                inIndex++;
            }
            else if (current == kept || !Contains(patchBorders, tables.PatchCount, current))
            {
                inIndex++;
                tables.LimiterCount--;
            }
            else if (!Contains(patchBorders, tables.PatchCount, kept))
            {
                tables.Limiter[outIndex] = current;
                inIndex++;
                tables.LimiterCount--;
            }
            else
            {
                tables.Limiter[++outIndex] = current;
                inIndex++;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="value"/> appears in table[0..lastIndex] inclusive. The bound is a
    /// last index rather than a count because there are one more patch borders than patches, and
    /// the final border has to count as one - dropping it merges the top limiter band away.
    /// </summary>
    private static bool Contains(ReadOnlySpan<short> table, int lastIndex, short value)
    {
        for (int i = 0; i <= lastIndex; i++)
        {
            if (table[i] == value)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Splits [start, stop) into <paramref name="count"/> geometrically spaced bands, writing each
    /// band's width. Port of FFmpeg's make_bands.
    /// </summary>
    private static void MakeBands(Span<short> bands, int start, int stop, int count)
    {
        float baseRatio = MathF.Pow(stop / (float)start, 1.0f / count);
        float product = start;
        int previous = start;

        for (int k = 0; k < count - 1; k++)
        {
            product *= baseRatio;
            int present = (int)MathF.Round(product);
            bands[k] = (short)(present - previous);
            previous = present;
        }
        bands[count - 1] = (short)(stop - previous);
    }
}

/// <summary>The derived band tables for the current SBR header.</summary>
internal sealed class SbrTables
{
    public int MasterCount;
    public readonly short[] Master = new short[49];

    /// <summary>High and low resolution band borders, in QMF subbands.</summary>
    public readonly short[] High = new short[49];
    public readonly short[] Low = new short[25];

    /// <summary>Noise floor band borders.</summary>
    public readonly short[] Noise = new short[6];

    /// <summary>Gain limiter band borders.</summary>
    public readonly short[] Limiter = new short[30];
    public int LimiterCount;

    /// <summary>kx: the first QMF subband SBR reconstructs.</summary>
    public int StartSubband;

    /// <summary>M: how many QMF subbands SBR covers.</summary>
    public int SubbandCount;

    public int PatchCount;
    public readonly int[] PatchSubbandCount = new int[7];
    public readonly int[] PatchStartSubband = new int[7];
}

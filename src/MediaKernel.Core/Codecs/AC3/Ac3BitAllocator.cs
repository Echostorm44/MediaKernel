// AC-3 Bit Allocation — a faithful port of FFmpeg's ff_ac3_bit_alloc_calc_psd,
// ff_ac3_bit_alloc_calc_mask (libavcodec/ac3.c) and ac3_bit_alloc_calc_bap_c
// (libavcodec/ac3dsp.c), per ATSC A/52 section 7.2.2.
//
// The previous implementation approximated the algorithm and indexed critical bands from 0
// regardless of the starting bin, which is wrong for the coupling channel and produced bit
// allocation pointers that did not match the encoder's. Since the bap values decide how many
// bits each mantissa occupies, wrong values desynchronised the reader partway through the
// first block of essentially every frame.

namespace MediaKernel.Core.Codecs.AC3;

/// <summary>Bit allocation parameters shared across channels within a block.</summary>
public struct Ac3BitAllocParameters
{
    public int SlowDecay;
    public int FastDecay;
    public int SlowGain;
    public int DbPerBit;
    public int Floor;
    public int CouplingFastLeak;
    public int CouplingSlowLeak;
    /// <summary>Sample-rate code (0 = 48 kHz, 1 = 44.1 kHz, 2 = 32 kHz).</summary>
    public int SampleRateCode;
    /// <summary>Shift applied to decay values and the hearing-threshold band index.</summary>
    public int SampleRateShift;
}

/// <summary>
/// AC-3 parametric bit allocation.
/// </summary>
public static class Ac3BitAllocator
{
    public const int CriticalBands = 50;
    public const int MaxCoefficients = 256;

    private static int LowComp1(int a, int b0, int b1, int c)
    {
        if (b0 + 256 == b1)
            a = c;
        else if (b0 > b1)
            a = Math.Max(a - 64, 0);
        return a;
    }

    private static int LowComp(int a, int b0, int b1, int bin)
    {
        if (bin < 7) return LowComp1(a, b0, b1, 384);
        if (bin < 20) return LowComp1(a, b0, b1, 320);
        return Math.Max(a - 128, 0);
    }

    /// <summary>
    /// Maps exponents to a power spectral density and integrates it into critical bands.
    /// </summary>
    public static void CalculatePsd(
        ReadOnlySpan<int> exponents, int start, int end,
        Span<int> psd, Span<int> bandPsd)
    {
        ReadOnlySpan<byte> bandStart = Ac3Tables.BandStartTable;
        ReadOnlySpan<byte> binToBand = Ac3Tables.BinToBandTable;
        ReadOnlySpan<byte> logAdd = Ac3Tables.LogAddTable;

        for (int bin = start; bin < end; bin++)
            psd[bin] = 3072 - (exponents[bin] << 7);

        int b = start;
        int band = binToBand[start];
        do
        {
            int v = psd[b++];
            int bandEnd = Math.Min(bandStart[band + 1], end);
            for (; b < bandEnd; b++)
            {
                int max = Math.Max(v, psd[b]);
                int adr = Math.Min(max - ((v + psd[b] + 1) >> 1), 255);
                v = max + logAdd[adr];
            }
            bandPsd[band++] = v;
        }
        while (end > bandStart[band]);
    }

    /// <summary>
    /// Computes the excitation function and masking curve, applying delta bit allocation.
    /// </summary>
    /// <returns>False if the delta bit allocation data is malformed.</returns>
    public static bool CalculateMask(
        in Ac3BitAllocParameters p, ReadOnlySpan<int> bandPsd,
        int start, int end, int fastGain, bool isLfe,
        int dbaMode, int dbaSegments,
        ReadOnlySpan<byte> dbaOffsets, ReadOnlySpan<byte> dbaLengths, ReadOnlySpan<byte> dbaValues,
        Span<int> mask)
    {
        if (end <= 0)
            return false;

        Span<int> excite = stackalloc int[CriticalBands];
        ReadOnlySpan<byte> binToBand = Ac3Tables.BinToBandTable;
        ReadOnlySpan<ushort> hearing = Ac3Tables.HearingThresholdTable;

        int bandStartIndex = binToBand[start];
        int bandEndIndex = binToBand[end - 1] + 1;

        int begin;
        int fastLeak = 0, slowLeak = 0;

        if (bandStartIndex == 0)
        {
            int lowComp = 0;
            lowComp = LowComp1(lowComp, bandPsd[0], bandPsd[1], 384);
            excite[0] = bandPsd[0] - fastGain - lowComp;
            lowComp = LowComp1(lowComp, bandPsd[1], bandPsd[2], 384);
            excite[1] = bandPsd[1] - fastGain - lowComp;
            begin = 7;

            for (int band = 2; band < 7; band++)
            {
                if (!(isLfe && band == 6))
                    lowComp = LowComp1(lowComp, bandPsd[band], bandPsd[band + 1], 384);
                fastLeak = bandPsd[band] - fastGain;
                slowLeak = bandPsd[band] - p.SlowGain;
                excite[band] = fastLeak - lowComp;
                if (!(isLfe && band == 6) && bandPsd[band] <= bandPsd[band + 1])
                {
                    begin = band + 1;
                    break;
                }
            }

            int end1 = Math.Min(bandEndIndex, 22);
            for (int band = begin; band < end1; band++)
            {
                if (!(isLfe && band == 6))
                    lowComp = LowComp(lowComp, bandPsd[band], bandPsd[band + 1], band);
                fastLeak = Math.Max(fastLeak - p.FastDecay, bandPsd[band] - fastGain);
                slowLeak = Math.Max(slowLeak - p.SlowDecay, bandPsd[band] - p.SlowGain);
                excite[band] = Math.Max(fastLeak - lowComp, slowLeak);
            }
            begin = 22;
        }
        else
        {
            // Coupling channel
            begin = bandStartIndex;
            fastLeak = (p.CouplingFastLeak << 8) + 768;
            slowLeak = (p.CouplingSlowLeak << 8) + 768;
        }

        for (int band = begin; band < bandEndIndex; band++)
        {
            fastLeak = Math.Max(fastLeak - p.FastDecay, bandPsd[band] - fastGain);
            slowLeak = Math.Max(slowLeak - p.SlowDecay, bandPsd[band] - p.SlowGain);
            excite[band] = Math.Max(fastLeak, slowLeak);
        }

        for (int band = bandStartIndex; band < bandEndIndex; band++)
        {
            int tmp = p.DbPerBit - bandPsd[band];
            if (tmp > 0)
                excite[band] += tmp >> 2;

            int threshold = hearing[(band >> p.SampleRateShift) * 3 + p.SampleRateCode];
            mask[band] = Math.Max(threshold, excite[band]);
        }

        // Delta bit allocation (DBA_REUSE = 0, DBA_NEW = 1 in FFmpeg's enum ordering)
        if (dbaMode == 0 || dbaMode == 1)
        {
            if (dbaSegments > 8)
                return false;

            int band = bandStartIndex;
            for (int seg = 0; seg < dbaSegments; seg++)
            {
                band += dbaOffsets[seg];
                if (band >= CriticalBands || dbaLengths[seg] > CriticalBands - band)
                    return false;

                int delta = dbaValues[seg] >= 4
                    ? (dbaValues[seg] - 3) * 128
                    : (dbaValues[seg] - 4) * 128;

                for (int i = 0; i < dbaLengths[seg]; i++)
                    mask[band++] += delta;
            }
        }

        return true;
    }

    /// <summary>
    /// Derives per-coefficient bit allocation pointers from the masking curve and PSD.
    /// </summary>
    public static void CalculateBap(
        ReadOnlySpan<int> mask, ReadOnlySpan<int> psd,
        int start, int end, int snrOffset, int floor, Span<int> bap)
    {
        // A -960 SNR offset means "allocate nothing".
        if (snrOffset == -960)
        {
            bap[..Math.Min(bap.Length, MaxCoefficients)].Clear();
            return;
        }

        ReadOnlySpan<byte> bandStart = Ac3Tables.BandStartTable;
        ReadOnlySpan<byte> binToBand = Ac3Tables.BinToBandTable;
        ReadOnlySpan<byte> bapTab = Ac3Tables.BapTable;

        int bin = start;
        int band = binToBand[start];
        int bandEnd;
        do
        {
            int m = (Math.Max(mask[band] - snrOffset - floor, 0) & 0x1FE0) + floor;
            bandEnd = Math.Min(bandStart[++band], end);

            for (; bin < bandEnd; bin++)
            {
                int address = Math.Clamp((psd[bin] - m) >> 5, 0, 63);
                bap[bin] = bapTab[address];
            }
        }
        while (end > bandEnd);
    }
}

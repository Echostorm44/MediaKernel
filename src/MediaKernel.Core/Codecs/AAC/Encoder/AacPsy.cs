// MediaKernel - the AAC encoder's psychoacoustic model
//
// A translation of libavcodec/aacpsy.c: the 3GPP TS26.403-inspired model that decides how much
// noise each band may carry, and LAME's block-switching decision that decides whether a frame
// is one long window or eight short ones.
//
// This file is where the port's float precision is most exposed. The initialisation computes
// its coefficients with DOUBLE pow, exp, exp2 and exp10 and with FLOAT atan, in that mixture,
// and those coefficients multiply every threshold the encoder ever uses. Each call below is
// translated at the width the C uses, not at the width that reads more naturally: ath() is
// double arithmetic returning a float, calc_bark is float throughout, and ff_exp10 is
// exp2(log2(10) * x) rather than a call to pow.

using System;

namespace MediaKernel.Core.Codecs.AAC.Encoder;

/// <summary><c>AacPsyCoeffs</c>: the model's per-band constants, computed once.</summary>
internal struct AacPsyCoeffs
{
    /// <summary>Absolute threshold of hearing for the band.</summary>
    public float Ath;

    /// <summary>The band's Bark value.</summary>
    public float Barks;

    /// <summary>Low-to-high spreading, [0] for thresholds and [1] for energy.</summary>
    public float SpreadLow0;
    public float SpreadLow1;

    /// <summary>High-to-low spreading, same pair.</summary>
    public float SpreadHi0;
    public float SpreadHi1;

    /// <summary>The smallest signal-to-noise ratio the band may be coded at.</summary>
    public float MinSnr;
}

/// <summary><c>AacPsyChannel</c>: one channel's running state.</summary>
internal sealed class AacPsyChannel
{
    /// <summary><c>band[128]</c>.</summary>
    public readonly AacPsyBandState[] Band = new AacPsyBandState[128];

    /// <summary><c>prev_band[128]</c>.</summary>
    public readonly AacPsyBandState[] PrevBand = new AacPsyBandState[128];

    /// <summary>A sliding average of the channel's energy.</summary>
    public float WinEnergy;

    /// <summary>The high-pass IIR filter's state.</summary>
    public readonly float[] IirState = new float[2];

    /// <summary>The grouping to use next frame, if this one is eight short windows.</summary>
    public byte NextGrouping;

    public AacWindowSequence NextWindowSequence;

    // ── LAME's part ─────────────────────────────────────────────────────────

    public float AttackThreshold;

    /// <summary><c>prev_energy_subshort[8 * 2]</c>.</summary>
    public readonly float[] PrevEnergySubshort = new float[AacPsy.NumBlocksShort * AacPsy.LameNumSubblocks];

    /// <summary>The attack value of the last short block of the previous sequence.</summary>
    public int PrevAttack;

    /// <summary>Whether the next frame's first attack value is zero.</summary>
    public int NextAttack0Zero;
}

/// <summary>
/// <c>AacPsyBand</c>: what the model works out about one band, frame by frame.
/// </summary>
/// <remarks>
/// Not to be confused with <see cref="AacPsyBand"/>, which is FFmpeg's <c>FFPsyBand</c> - the
/// handful of values the model PUBLISHES to the coder. This is the model's own working state,
/// and the C gives the two structs the same name in different translation units.
/// </remarks>
internal struct AacPsyBandState
{
    public float Energy;
    public float Thr;
    public float ThrQuiet;
    public float NzLines;
    public float ActiveLines;
    public float Pe;
    public float PeConst;
    public float NormFac;
    public int AvoidHoles;
}

/// <summary><c>aacpsy.c</c>: the 3GPP model and LAME's window decision.</summary>
internal sealed class AacPsy
{
    // ── 3GPP constants ──────────────────────────────────────────────────────

    public const float ThrSpreadHi = 1.5f;
    public const float ThrSpreadLow = 3.0f;
    public const float EnSpreadHiL1 = 2.0f;
    public const float EnSpreadHiL2 = 1.5f;
    public const float EnSpreadHiS = 1.5f;
    public const float EnSpreadLowL = 3.0f;
    public const float EnSpreadLowS = 2.0f;

    public const float Snr1Db = 7.9432821e-1f;      // -1 dB
    public const float Snr25Db = 3.1622776e-3f;     // -25 dB

    /// <summary><c>ATH_ADD</c>.</summary>
    public const int AthAdd = 4;

    public const int BlockSizeLong = 1024;
    public const int BlockSizeShort = 128;
    public const int NumBlocksShort = 8;
    public const int LameNumSubblocks = 2;

    /// <summary><c>PSY_LAME_FIR_LEN</c>.</summary>
    public const int LameFirLen = 21;

    /// <summary><c>PSY_3GPP_BITS_TO_PE</c>.</summary>
    public static float BitsToPe(float bits) => bits * 1.18f;

    /// <summary><c>PSY_3GPP_PE_TO_BITS</c>.</summary>
    public static float PeToBits(float bits) => bits / 1.18f;

    // ── LAME's presets ──────────────────────────────────────────────────────

    /// <summary><c>psy_abr_map</c>: kbps per channel, then the short threshold.</summary>
    private static ReadOnlySpan<float> AbrQuality =>
        [8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160];

    private static ReadOnlySpan<float> AbrStLrm =>
        [7.60f, 7.60f, 7.60f, 7.60f, 7.60f, 7.60f, 7.60f, 7.40f, 7.00f, 6.60f, 6.20f, 6.20f, 6.20f];

    /// <summary><c>psy_vbr_map</c>: every entry is the same threshold.</summary>
    private static ReadOnlySpan<float> VbrStLrm =>
        [4.20f, 4.20f, 4.20f, 4.20f, 4.20f, 4.20f, 4.20f, 4.20f, 4.20f, 4.20f, 4.20f];

    /// <summary><c>psy_fir_coeffs</c>: LAME's high-pass FIR, already doubled as the C writes it.</summary>
    public static ReadOnlySpan<float> FirCoeffs =>
    [
        -8.65163e-18f * 2, -0.00851586f * 2, -6.74764e-18f * 2, 0.0209036f * 2,
        -3.36639e-17f * 2, -0.0438162f * 2,  -1.54175e-17f * 2, 0.0931738f * 2,
        -5.52212e-17f * 2, -0.313819f * 2
    ];

    // ── state ───────────────────────────────────────────────────────────────

    public int ChanBitrate;
    public int FrameBits;
    public int FillLevel;

    public float PeMin;
    public float PeMax;
    public float PePrevious;
    public float PeCorrection;

    /// <summary><c>psy_coef[2][64]</c>, flattened: index <c>[blockType * 64 + band]</c>.</summary>
    public readonly AacPsyCoeffs[] PsyCoef = new AacPsyCoeffs[2 * 64];

    public AacPsyChannel[] Ch = [];

    public float GlobalQuality;

    /// <summary>The bit reservoir's size, which lives on FFPsyContext in the C.</summary>
    public int BitresSize;

    /// <summary>
    /// <c>lame_calc_attack_threshold</c>: the ABR attack threshold for a bitrate, interpolated
    /// from LAME's table by picking the closer of the two neighbouring entries.
    /// </summary>
    public static float LameCalcAttackThreshold(int bitrate)
    {
        // Assume the top of the table, which is right if the loop never breaks.
        int lowerRange = 12, upperRange = 12;
        float lowerRangeKbps = AbrQuality[12];
        float upperRangeKbps = AbrQuality[12];

        for (int i = 1; i < 13; i++)
        {
            if (MathF.Max(bitrate, AbrQuality[i]) != bitrate)
            {
                upperRange = i;
                upperRangeKbps = AbrQuality[i];
                lowerRange = i - 1;
                lowerRangeKbps = AbrQuality[i - 1];
                break;
            }
        }

        if (upperRangeKbps - bitrate > bitrate - lowerRangeKbps)
            return AbrStLrm[lowerRange];
        return AbrStLrm[upperRange];
    }

    /// <summary><c>lame_window_init</c>: the per-channel part of LAME's model.</summary>
    public void LameWindowInit(int channels, int bitrate, bool constantQuality, int globalQuality)
    {
        for (int i = 0; i < channels; i++)
        {
            AacPsyChannel pch = Ch[i];

            if (constantQuality)
                pch.AttackThreshold = VbrStLrm[Math.Clamp(globalQuality / 118, 0, 10)];
            else
                pch.AttackThreshold = LameCalcAttackThreshold(bitrate / channels / 1000);

            for (int j = 0; j < NumBlocksShort * LameNumSubblocks; j++)
                pch.PrevEnergySubshort[j] = 10.0f;
        }
    }

    /// <summary><c>calc_bark</c>: the Bark value of a frequency. Float throughout.</summary>
    public static float CalcBark(float f) =>
        13.3f * MathF.Atan(0.00076f * f) + 3.5f * MathF.Atan(f / 7500.0f * (f / 7500.0f));

    /// <summary>
    /// <c>ath</c>: the absolute threshold of hearing at a frequency, borrowed from LAME.
    /// </summary>
    /// <remarks>
    /// Every term is DOUBLE in the C - the literals are doubles and pow and exp are the double
    /// ones - and only the return narrows to float. Computing it in float would move the
    /// threshold of every band.
    /// </remarks>
    public static float Ath(float f, float add)
    {
        f /= 1000.0f;
        return (float)(3.64 * Math.Pow(f, -0.8)
                     - 6.8 * Math.Exp(-0.6 * (f - 3.4) * (f - 3.4))
                     + 6.0 * Math.Exp(-0.15 * (f - 8.7) * (f - 8.7))
                     + (0.6 + 0.04 * add) * 0.001 * f * f * f * f);
    }

    /// <summary>
    /// <c>exp2f</c>: the C library's single-precision exponent, as closely as .NET can.
    /// </summary>
    /// <remarks>
    /// THIS IS THE PORT'S ONE IRREDUCIBLE DIFFERENCE FROM FFMPEG, and it is measured rather
    /// than suspected. UCRT's <c>exp2f</c> is not correctly rounded, and nothing in .NET
    /// reproduces it: over 200,000 arguments, <c>float.Exp2</c> differs on 0.080% of them and
    /// computing in double and narrowing - which IS correctly rounded - differs on 0.065%, at
    /// every argument range the model uses. The double route is used because it is the closer
    /// of the two and because it is the defensible one; the residual 0.065% is a floor the
    /// translation cannot remove, and it shows up as an occasional threshold one ulp from
    /// ffmpeg's. Every other transcendental in this file - atanf, log2f, the double pow, exp
    /// and exp2 - matches exactly.
    /// </remarks>
    public static float Exp2F(float x) => (float)double.Exp2(x);

    /// <summary>
    /// <c>ff_exp10</c> from <c>libavutil/ffmath.h</c>: <c>exp2(log2(10) * x)</c>.
    /// </summary>
    /// <remarks>
    /// Not <c>Math.Pow(10, x)</c>. FFmpeg routes every power of ten through exp2 for speed, and
    /// the two disagree in the last place - which here is a spreading factor applied to every
    /// band's threshold.
    /// </remarks>
    public static double Exp10(double x) => double.Exp2(3.32192809488736234787031942948939 * x);

    /// <summary>
    /// <c>psy_3gpp_init</c>: the model's constants for one stream.
    /// </summary>
    /// <param name="sampleRate">The stream's sample rate.</param>
    /// <param name="channels">Channels in the stream.</param>
    /// <param name="bitRate">The stream's total bitrate.</param>
    /// <param name="bandwidth">The cutoff, in Hz.</param>
    /// <param name="numBands">Bands per block type: [0] long, [1] short.</param>
    /// <param name="bandSizesLong">The long block's scalefactor band sizes.</param>
    /// <param name="bandSizesShort">The short block's.</param>
    /// <param name="globalQuality">0 unless the caller asked for constant quality.</param>
    /// <param name="constantQuality">Whether AV_CODEC_FLAG_QSCALE is set.</param>
    public void Init(int sampleRate, int channels, long bitRate, int bandwidth,
                     ReadOnlySpan<int> numBands, ReadOnlySpan<byte> bandSizesLong,
                     ReadOnlySpan<byte> bandSizesShort, int globalQuality, bool constantQuality)
    {
        int chanBitrate = (int)(bitRate / (constantQuality ? 2.0f : channels));
        float numBark = CalcBark(bandwidth);

        if (bandwidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(bandwidth), bandwidth,
                "The psychoacoustic model needs a positive cutoff.");

        GlobalQuality = (globalQuality != 0 ? globalQuality : 120) * 0.01f;

        if (constantQuality)
        {
            // The target average bitrate is what the spread parameters are computed from.
            chanBitrate = (int)(chanBitrate / 120.0 * (globalQuality != 0 ? globalQuality : 120));
        }

        ChanBitrate = chanBitrate;
        FrameBits = Math.Min(2560, chanBitrate * BlockSizeLong / sampleRate);
        PeMin = 8.0f * BlockSizeLong * bandwidth / (sampleRate * 2.0f);
        PeMax = 12.0f * BlockSizeLong * bandwidth / (sampleRate * 2.0f);
        BitresSize = 6144 - FrameBits;
        BitresSize -= BitresSize % 8;
        FillLevel = BitresSize;

        // 3410 - 0.733 * 4 is computed in DOUBLE in the C - 0.733 is a double literal -
        // and only narrows at the call.
        float minath = Ath((float)(3410 - 0.733 * AthAdd), AthAdd);

        for (int j = 0; j < 2; j++)
        {
            Span<AacPsyCoeffs> coeffs = PsyCoef.AsSpan(j * 64, 64);
            ReadOnlySpan<byte> bandSizes = j != 0 ? bandSizesShort : bandSizesLong;
            float lineToFrequency = sampleRate / (j != 0 ? 256.0f : 2048.0f);
            float avgChanBits = chanBitrate * (j != 0 ? 128.0f : 1024.0f) / sampleRate;
            // The reference encoder uses 2.4% here where the spec says 60%.
            float barkPe = 0.024f * BitsToPe(avgChanBits) / numBark;
            float enSpreadLow = j != 0 ? EnSpreadLowS : EnSpreadLowL;
            // Long blocks at 22 kbps a channel or less spread like short blocks.
            float enSpreadHi = j != 0 || chanBitrate <= 22.0f ? EnSpreadHiS : EnSpreadHiL1;

            int i = 0;
            float prev = 0.0f;
            for (int g = 0; g < numBands[j]; g++)
            {
                i += bandSizes[g];
                float bark = CalcBark((i - 1) * lineToFrequency);
                coeffs[g].Barks = (bark + prev) / 2.0f;
                prev = bark;
            }

            for (int g = 0; g < numBands[j] - 1; g++)
            {
                // The C writes `coeffs[g+1].barks - coeffs->barks`, which is band g+1 against
                // band ZERO rather than against band g. Translated as it is: it is what
                // ffmpeg computes, and the spreading factors come out of it.
                float barkWidth = coeffs[g + 1].Barks - coeffs[0].Barks;
                coeffs[g].SpreadLow0 = (float)Exp10(-barkWidth * ThrSpreadLow);
                coeffs[g].SpreadHi0 = (float)Exp10(-barkWidth * ThrSpreadHi);
                coeffs[g].SpreadLow1 = (float)Exp10(-barkWidth * enSpreadLow);
                coeffs[g].SpreadHi1 = (float)Exp10(-barkWidth * enSpreadHi);

                float peMin = barkPe * barkWidth;
                // exp2 is the double one and the subtraction happens before the narrowing,
                // which is the C's `float minsnr = exp2(...) - 1.5f`.
                float minsnr = (float)(double.Exp2(peMin / bandSizes[g]) - 1.5f);
                coeffs[g].MinSnr = Math.Clamp(1.0f / minsnr, Snr25Db, Snr1Db);
            }

            int start = 0;
            for (int g = 0; g < numBands[j]; g++)
            {
                float minscale = Ath(start * lineToFrequency, AthAdd);
                for (int k = 1; k < bandSizes[g]; k++)
                    minscale = MathF.Min(minscale, Ath((start + k) * lineToFrequency, AthAdd));
                coeffs[g].Ath = minscale - minath;
                start += bandSizes[g];
            }
        }

        Ch = new AacPsyChannel[channels];
        for (int i = 0; i < channels; i++) Ch[i] = new AacPsyChannel();
        Entropy = new float[channels];

        LameWindowInit(channels, (int)bitRate, constantQuality, globalQuality);
    }
    /// <summary><c>FFPsyWindowInfo</c>: what the model decides about a frame's windows.</summary>
    internal struct WindowInfo
    {
        /// <summary><c>window_type[3]</c>: this frame's, the previous frame's, the next.</summary>
        public AacWindowSequence Type0;
        public AacWindowSequence Type1;
        public AacWindowSequence Type2;

        /// <summary>Sine or Kaiser-Bessel.</summary>
        public int WindowShape;

        public int NumWindows;

        /// <summary><c>grouping[8]</c>.</summary>
        public GroupingArray Grouping;

        /// <summary><c>clipping[8]</c>: the loudest normalised sample in each window.</summary>
        public ClippingArray Clipping;

        [System.Runtime.CompilerServices.InlineArray(8)]
        internal struct GroupingArray { private int element; }

        [System.Runtime.CompilerServices.InlineArray(8)]
        internal struct ClippingArray { private float element; }
    }

    /// <summary><c>window_grouping</c>: grouping as bits, 0 starts a group and 1 continues it.</summary>
    private static ReadOnlySpan<byte> WindowGrouping =>
        [0xB6, 0x6C, 0xD8, 0xB2, 0x66, 0xC6, 0x96, 0x36, 0x36];

    /// <summary><c>iir_filter</c>: the high-pass the block-switching decision runs on.</summary>
    public static float IirFilter(int input, Span<float> state)
    {
        float ret = 0.7548f * (input - state[0]) + 0.5095f * state[1];
        state[0] = input;
        state[1] = ret;
        return ret;
    }

    /// <summary>
    /// <c>psy_hp_filter</c>: LAME's symmetric FIR high-pass, folded so each tap is applied to
    /// the pair of samples either side of the centre.
    /// </summary>
    /// <remarks>
    /// The scale by 32768 at the end is not a conversion to integers: LAME's model expects its
    /// input in the range of a 16-bit sample, and FFmpeg's comment says tuning it for
    /// normalised floats would be difficult. So the filter runs in LAME's units and every
    /// threshold that reads it is LAME's.
    /// </remarks>
    public static void HpFilter(ReadOnlySpan<float> firbuf, Span<float> hpfsmpl,
                                ReadOnlySpan<float> firCoeffs)
    {
        for (int i = 0; i < BlockSizeLong; i++)
        {
            float sum1 = firbuf[i + (LameFirLen - 1) / 2];
            float sum2 = 0.0f;
            for (int j = 0; j < ((LameFirLen - 1) / 2) - 1; j += 2)
            {
                sum1 += firCoeffs[j] * (firbuf[i + j] + firbuf[i + LameFirLen - j]);
                sum2 += firCoeffs[j + 1] * (firbuf[i + j + 1] + firbuf[i + LameFirLen - j - 1]);
            }
            hpfsmpl[i] = (sum1 + sum2) * 32768.0f;
        }
    }

    /// <summary>
    /// <c>lame_apply_block_type</c>: turn "long or short" into a window sequence, which needs
    /// the previous frame's decision because a short block has to be eased into and out of.
    /// </summary>
    public static void LameApplyBlockType(AacPsyChannel ctx, ref WindowInfo wi, int useLongBlock)
    {
        AacWindowSequence blocktype = AacWindowSequence.OnlyLong;

        if (useLongBlock != 0)
        {
            if (ctx.NextWindowSequence == AacWindowSequence.EightShort)
                blocktype = AacWindowSequence.LongStop;
        }
        else
        {
            blocktype = AacWindowSequence.EightShort;
            if (ctx.NextWindowSequence == AacWindowSequence.OnlyLong)
                ctx.NextWindowSequence = AacWindowSequence.LongStart;
            if (ctx.NextWindowSequence == AacWindowSequence.LongStop)
                ctx.NextWindowSequence = AacWindowSequence.EightShort;
        }

        wi.Type0 = ctx.NextWindowSequence;
        ctx.NextWindowSequence = blocktype;
    }

    /// <summary>
    /// <c>psy_lame_window</c>: one long window or eight short ones, decided by looking for an
    /// attack in the lookahead.
    /// </summary>
    /// <param name="channel">Which channel's state to carry forward.</param>
    /// <param name="lookahead">
    /// <c>la</c>: the samples this decision looks ahead at, or empty when there are none - in
    /// which case the frame simply repeats the previous type.
    /// </param>
    /// <param name="prevType">The previous frame's window sequence.</param>
    public WindowInfo LameWindow(int channel, ReadOnlySpan<float> lookahead,
                                 AacWindowSequence prevType)
    {
        AacPsyChannel pch = Ch[channel];
        int grouping = 0;
        int useLongBlock = 1;
        Span<int> attacks = stackalloc int[NumBlocksShort + 1];
        attacks.Clear();
        var wi = default(WindowInfo);

        if (!lookahead.IsEmpty)
        {
            Span<float> hpfsmpl = stackalloc float[BlockSizeLong];
            Span<float> attackIntensity = stackalloc float[(NumBlocksShort + 1) * LameNumSubblocks];
            Span<float> energySubshort = stackalloc float[(NumBlocksShort + 1) * LameNumSubblocks];
            Span<float> energyShort = stackalloc float[NumBlocksShort + 1];
            energyShort.Clear();

            // The filter reads from before the frame, which is why the window the C passes in
            // starts earlier than the samples being decided on.
            ReadOnlySpan<float> firbuf = lookahead[(BlockSizeShort / 4 - LameFirLen)..];
            int attSum = 0;

            HpFilter(firbuf, hpfsmpl, FirCoeffs);

            // The energies of each sub-short block; the first two are carried over from the
            // previous frame.
            for (int i = 0; i < LameNumSubblocks; i++)
            {
                energySubshort[i] = pch.PrevEnergySubshort[i + (NumBlocksShort - 1) * LameNumSubblocks];
                attackIntensity[i] = energySubshort[i]
                    / pch.PrevEnergySubshort[i + ((NumBlocksShort - 1) * LameNumSubblocks - 2)];
                energyShort[0] += energySubshort[i];
            }

            int pf = 0;
            for (int i = 0; i < NumBlocksShort * LameNumSubblocks; i++)
            {
                int pfe = pf + BlockSizeLong / (NumBlocksShort * LameNumSubblocks);
                float p = 1.0f;
                for (; pf < pfe; pf++)
                    p = MathF.Max(p, MathF.Abs(hpfsmpl[pf]));

                pch.PrevEnergySubshort[i] = energySubshort[i + LameNumSubblocks] = p;
                energyShort[1 + i / LameNumSubblocks] += p;

                // The indices are [i + 3 - 2] in LAME: each sub-block against the one two
                // before it.
                if (p > energySubshort[i + LameNumSubblocks - 2])
                    p /= energySubshort[i + LameNumSubblocks - 2];
                else if (energySubshort[i + LameNumSubblocks - 2] > p * 10.0f)
                    p = energySubshort[i + LameNumSubblocks - 2] / (p * 10.0f);
                else
                    p = 0.0f;

                attackIntensity[i + LameNumSubblocks] = p;
            }

            for (int i = 0; i < (NumBlocksShort + 1) * LameNumSubblocks; i++)
            {
                if (attacks[i / LameNumSubblocks] == 0)
                {
                    if (attackIntensity[i] > pch.AttackThreshold)
                        attacks[i / LameNumSubblocks] = i % LameNumSubblocks + 1;
                }
            }

            // There has to be an energy CHANGE between short blocks, or a periodic signal
            // would be cut into short blocks forever. Both thresholds are LAME's, tuned
            // against particular test samples rather than derived.
            for (int i = 1; i < NumBlocksShort + 1; i++)
            {
                float u = energyShort[i - 1];
                float v = energyShort[i];
                float m = MathF.Max(u, v);
                if (m < 40000)
                {
                    if (u < 2.3f * v && v < 2.3f * u)
                    {
                        if (i == 1 && attacks[0] < attacks[i])
                            attacks[0] = 0;
                        attacks[i] = 0;
                    }
                }
                attSum += attacks[i];
            }

            if (pch.NextAttack0Zero != 0)
                attacks[0] = 0;
            pch.NextAttack0Zero = attacks[NumBlocksShort] == 0 ? 1 : 0;

            if (attacks[0] <= pch.PrevAttack)
                attacks[0] = 0;

            attSum += attacks[0];

            // Short windows if the previous attack was in the last sub-block of the previous
            // sequence, or if there is a new attack.
            if (pch.PrevAttack == LameNumSubblocks || attSum != 0)
            {
                useLongBlock = 0;
                for (int i = 1; i < NumBlocksShort + 1; i++)
                {
                    if (attacks[i] != 0 && attacks[i - 1] != 0)
                        attacks[i] = 0;
                }
            }
        }
        else
        {
            // No lookahead, so the frame keeps the previous sequence's type.
            useLongBlock = prevType == AacWindowSequence.EightShort ? 0 : 1;
        }

        LameApplyBlockType(pch, ref wi, useLongBlock);

        wi.Type1 = prevType;
        if (wi.Type0 != AacWindowSequence.EightShort)
        {
            wi.NumWindows = 1;
            wi.Grouping[0] = 1;
            wi.WindowShape = wi.Type0 == AacWindowSequence.LongStart ? 0 : 1;
        }
        else
        {
            int lastgrp = 0;
            wi.NumWindows = 8;
            wi.WindowShape = 0;
            for (int i = 0; i < 8; i++)
            {
                if (((pch.NextGrouping >> i) & 1) == 0)
                    lastgrp = i;
                wi.Grouping[lastgrp]++;
            }
        }

        // The grouping for the NEXT frame, from where the first attack landed in this one.
        for (int i = 0; i < 9; i++)
        {
            if (attacks[i] != 0)
            {
                grouping = i;
                break;
            }
        }
        pch.NextGrouping = WindowGrouping[grouping];
        pch.PrevAttack = attacks[NumBlocksShort - 1];

        return wi;
    }
    // ── 3GPP analysis constants ─────────────────────────────────────────────

    private const float Rpemin = 0.01f;
    private const float Rpelev = 2.0f;

    private const float C1 = 3.0f;            // log2(8)
    private const float C2 = 1.3219281f;      // log2(2.5)
    private const float C3 = 0.55935729f;     // 1 - C2 / C1

    private const float SaveSlopeL = -0.46666667f;
    private const float SaveSlopeS = -0.36363637f;
    private const float SaveAddL = -0.84285712f;
    private const float SaveAddS = -0.75f;
    private const float SpendSlopeL = 0.66666669f;
    private const float SpendSlopeS = 0.81818181f;
    private const float SpendAddL = -0.35f;
    private const float SpendAddS = -0.26111111f;
    private const float ClipLoL = 0.2f;
    private const float ClipLoS = 0.2f;
    private const float ClipHiL = 0.95f;
    private const float ClipHiS = 0.75f;

    private const float AhThrLong = 0.5f;
    private const float AhThrShort = 0.63f;

    private const int PeForgetSlope = 511;

    /// <summary>Hole avoidance state. The C's anonymous enum.</summary>
    private const int AhNone = 0;
    private const int AhInactive = 1;
    private const int AhActive = 2;

    /// <summary>The bit reservoir's current occupancy, which the bit demand reads.</summary>
    public int BitresBits;

    /// <summary>What the model allocated to this frame.</summary>
    public int BitresAlloc;

    /// <summary>Per-channel perceptual entropy, published to the coder.</summary>
    public float[] Entropy = [];

    /// <summary>
    /// <c>calc_bit_demand</c>: how many bits this frame gets, from the reservoir's level and
    /// the perceptual entropy. 5.6.1.2.
    /// </summary>
    public int CalcBitDemand(float pe, int bits, int size, bool shortWindow)
    {
        float bitsaveSlope = shortWindow ? SaveSlopeS : SaveSlopeL;
        float bitsaveAdd = shortWindow ? SaveAddS : SaveAddL;
        float bitspendSlope = shortWindow ? SpendSlopeS : SpendSlopeL;
        float bitspendAdd = shortWindow ? SpendAddS : SpendAddL;
        float clipLow = shortWindow ? ClipLoS : ClipLoL;
        float clipHigh = shortWindow ? ClipHiS : ClipHiL;

        FillLevel += FrameBits - bits;
        FillLevel = Math.Clamp(FillLevel, 0, size);
        float fillLevel = Math.Clamp((float)FillLevel / size, clipLow, clipHigh);
        float clippedPe = Math.Clamp(pe, PeMin, PeMax);
        float bitSave = (fillLevel + bitsaveAdd) * bitsaveSlope;
        float bitSpend = (fillLevel + bitspendAdd) * bitspendSlope;

        // The bit factor graph in the spec is wrong and the reference encoder's reading of it
        // is wrong differently; this is FFmpeg's, and FFmpeg's is what the bitstream matches.
        float bitFactor = 1.0f - bitSave
            + (bitSpend - bitSave) / (PeMax - PeMin) * (clippedPe - PeMin);

        // The reference encoder centres pe max/min on the current pe. Here pe.min is
        // forgotten slowly while pe stays above the mean, which has the same effect.
        PeMax = MathF.Max(pe, PeMax);
        float forgetfulMinPe = (PeMin * PeForgetSlope
            + MathF.Max(PeMin, pe * (pe / PeMax))) / (PeForgetSlope + 1);
        PeMin = MathF.Min(pe, forgetfulMinPe);

        // At least an eighth of an average frame, or a starved reservoir produces frames
        // with no bits at all.
        return (int)MathF.Min(FrameBits * bitFactor,
                              MathF.Max(FrameBits + size - bits, FrameBits / 8));
    }

    /// <summary>
    /// <c>calc_pe_3gpp</c>: a band's perceptual entropy, and the constant part of it the
    /// reduction steps reuse. 5.6.1.3.1.
    /// </summary>
    public static float CalcPe3gpp(ref AacPsyBandState band)
    {
        band.Pe = 0.0f;
        band.PeConst = 0.0f;
        band.ActiveLines = 0.0f;

        if (band.Energy > band.Thr)
        {
            float a = MathF.Log2(band.Energy);
            float pe = a - MathF.Log2(band.Thr);
            band.ActiveLines = band.NzLines;

            if (pe < C1)
            {
                pe = pe * C3 + C2;
                a = a * C3 + C2;
                band.ActiveLines *= C3;
            }

            band.Pe = pe * band.NzLines;
            band.PeConst = a * band.NzLines;
        }

        return band.Pe;
    }

    /// <summary><c>calc_reduction_3gpp</c>: how far thresholds must come up to hit a target PE.</summary>
    public static float CalcReduction3gpp(float a, float desiredPe, float pe, float activeLines)
    {
        if (activeLines == 0.0f)
            return 0;

        float thrAvg = Exp2F((a - pe) / (4.0f * activeLines));
        float reduction = Exp2F((a - desiredPe) / (4.0f * activeLines)) - thrAvg;

        return MathF.Max(reduction, 0.0f);
    }

    /// <summary>
    /// <c>calc_reduced_thr_3gpp</c>: one band's threshold after a reduction, in the fourth-root
    /// domain the spec works in.
    /// </summary>
    public static float CalcReducedThr3gpp(ref AacPsyBandState band, float minSnr, float reduction)
    {
        float thr = band.Thr;

        if (band.Energy > thr)
        {
            thr = MathF.Sqrt(thr);
            thr = MathF.Sqrt(thr) + reduction;
            thr *= thr;
            thr *= thr;

            // This deviates from the 3GPP spec to match the reference encoder: the clamp
            // applies only to bands with hole avoidance on, and bands with it off always
            // have their threshold reduced.
            if (thr > band.Energy * minSnr && band.AvoidHoles != AhNone)
            {
                thr = MathF.Max(band.Thr, band.Energy * minSnr);
                band.AvoidHoles = AhActive;
            }
        }

        return thr;
    }

    /// <summary>
    /// <c>calc_thr_3gpp</c>: band energies, their initial thresholds, and the non-zero line
    /// count the entropy is measured in. 5.4.2.
    /// </summary>
    public static void CalcThr3gpp(in WindowInfo wi, int numBands, AacPsyChannel pch,
                                   ReadOnlySpan<byte> bandSizes, ReadOnlySpan<float> coefs,
                                   int cutoff)
    {
        int start = 0;
        for (int w = 0; w < wi.NumWindows * 16; w += 16)
        {
            int wstart = 0;
            for (int g = 0; g < numBands; g++)
            {
                ref AacPsyBandState band = ref pch.Band[w + g];

                float formFactor = 0.0f;
                band.Energy = 0.0f;
                if (wstart < cutoff)
                {
                    for (int i = 0; i < bandSizes[g]; i++)
                    {
                        band.Energy += coefs[start + i] * coefs[start + i];
                        formFactor += MathF.Sqrt(MathF.Abs(coefs[start + i]));
                    }
                }

                float temp = band.Energy > 0 ? MathF.Sqrt(bandSizes[g] / band.Energy) : 0;
                band.Thr = band.Energy * 0.001258925f;
                band.NzLines = formFactor * MathF.Sqrt(temp);

                start += bandSizes[g];
                wstart += bandSizes[g];
            }
        }
    }

    /// <summary>
    /// <c>psy_3gpp_analyze_channel</c>: the thresholds for one channel of one frame.
    /// </summary>
    /// <param name="channel">Which channel.</param>
    /// <param name="coefs">Its spectral coefficients.</param>
    /// <param name="wi">The window decision for this frame.</param>
    /// <param name="numBandsLong">Bands in a long block at this rate.</param>
    /// <param name="numBandsShort">Bands in a short block.</param>
    /// <param name="bandSizesLong">The long band sizes.</param>
    /// <param name="bandSizesShort">The short ones.</param>
    /// <param name="sampleRate">The stream's rate, which sets the cutoff in lines.</param>
    /// <param name="bandwidth">The cutoff in Hz.</param>
    /// <param name="constantQuality">Whether AV_CODEC_FLAG_QSCALE is set.</param>
    /// <param name="globalQuality">The requested quality, or 0.</param>
    /// <param name="output">Where the published per-band values go.</param>
    public void AnalyzeChannel(int channel, ReadOnlySpan<float> coefs, in WindowInfo wi,
                               int numBandsLong, int numBandsShort,
                               ReadOnlySpan<byte> bandSizesLong, ReadOnlySpan<byte> bandSizesShort,
                               int sampleRate, int bandwidth, bool constantQuality,
                               int globalQuality, Span<AacPsyBand> output)
    {
        AacPsyChannel pch = Ch[channel];
        Span<float> spreadEn = stackalloc float[128];
        spreadEn.Clear();

        float reduction = float.NaN, deltaPe = 0;
        float a = 0.0f, activeLines = 0.0f, normFac = 0.0f;
        float pe = ChanBitrate > 32000
            ? 0.0f
            : MathF.Max(50.0f, 100.0f - ChanBitrate * 100.0f / 32000.0f);

        bool isShort = wi.NumWindows == 8;
        int numBands = isShort ? numBandsShort : numBandsLong;
        ReadOnlySpan<byte> bandSizes = isShort ? bandSizesShort : bandSizesLong;
        Span<AacPsyCoeffs> coeffs = PsyCoef.AsSpan(isShort ? 64 : 0, 64);
        float avoidHoleThr = isShort ? AhThrShort : AhThrLong;
        int cutoff = bandwidth * 2048 / wi.NumWindows / sampleRate;

        // Energies, initial thresholds and the values derived from them. 5.4.2.
        CalcThr3gpp(wi, numBands, pch, bandSizes, coefs, cutoff);

        // Spreading, threshold in quiet and pre-echo control.
        for (int w = 0; w < wi.NumWindows * 16; w += 16)
        {
            // 5.4.2.3 "Spreading" and 5.4.3 "Spread Energy Calculation". Note that the C
            // writes spread_en[0] here rather than spread_en[w], so every window but the
            // first starts its spread from the PREVIOUS window's last value.
            spreadEn[0] = pch.Band[w + 0].Energy;
            for (int g = 1; g < numBands; g++)
            {
                pch.Band[w + g].Thr = MathF.Max(pch.Band[w + g].Thr,
                    pch.Band[w + g - 1].Thr * coeffs[g].SpreadHi0);
                spreadEn[w + g] = MathF.Max(pch.Band[w + g].Energy,
                    spreadEn[w + g - 1] * coeffs[g].SpreadHi1);
            }
            for (int g = numBands - 2; g >= 0; g--)
            {
                pch.Band[w + g].Thr = MathF.Max(pch.Band[w + g].Thr,
                    pch.Band[w + g + 1].Thr * coeffs[g].SpreadLow0);
                spreadEn[w + g] = MathF.Max(spreadEn[w + g],
                    spreadEn[w + g + 1] * coeffs[g].SpreadLow1);
            }

            // 5.4.2.4 "Threshold in quiet".
            for (int g = 0; g < numBands; g++)
            {
                ref AacPsyBandState band = ref pch.Band[w + g];

                band.ThrQuiet = band.Thr = MathF.Max(band.Thr, coeffs[g].Ath);

                // 5.4.2.5 "Pre-echo control".
                if (!(wi.Type0 == AacWindowSequence.LongStop
                      || (w == 0 && wi.Type1 == AacWindowSequence.LongStart)))
                {
                    band.Thr = MathF.Max(Rpemin * band.Thr,
                        MathF.Min(band.Thr, Rpelev * pch.PrevBand[w + g].ThrQuiet));
                }

                // 5.6.1.3.1 "Preparatory steps of the perceptual entropy calculation".
                pe += CalcPe3gpp(ref band);
                a += band.PeConst;
                activeLines += band.ActiveLines;

                // 5.6.1.3.3 "Selection of the bands for avoidance of holes".
                if (spreadEn[w + g] * avoidHoleThr > band.Energy || coeffs[g].MinSnr > 1.0f)
                    band.AvoidHoles = AhNone;
                else
                    band.AvoidHoles = AhInactive;
            }
        }

        // 5.6.1.3.2 "Calculation of the desired perceptual entropy".
        Entropy[channel] = pe;
        float desiredBits, desiredPe;

        if (constantQuality)
        {
            // 2.5 * 120 is almost transparent, and the room below it is deliberate.
            desiredPe = pe * (globalQuality != 0 ? globalQuality : 120) / (2 * 2.5f * 120.0f);
            desiredBits = MathF.Min(2560, PeToBits(desiredPe));
            desiredPe = BitsToPe(desiredBits);                  // reflect the clipping

            if (BitresBits > 0)
            {
                desiredBits = MathF.Min(2560, PeToBits(desiredPe));
                desiredPe = BitsToPe(desiredBits);
            }

            PeMax = MathF.Max(pe, PeMax);
            PeMin = MathF.Min(pe, PeMin);
        }
        else
        {
            desiredBits = CalcBitDemand(pe, BitresBits, BitresSize, isShort);
            desiredPe = BitsToPe(desiredBits);

            if (BitresBits > 0)
                desiredPe *= Math.Clamp(PePrevious / BitsToPe(BitresBits), 0.85f, 1.15f);
        }

        PePrevious = BitsToPe(desiredBits);
        BitresAlloc = (int)desiredBits;

        if (desiredPe < pe)
        {
            // 5.6.1.3.4 "First Estimation of the reduction value".
            for (int w = 0; w < wi.NumWindows * 16; w += 16)
            {
                reduction = CalcReduction3gpp(a, desiredPe, pe, activeLines);
                pe = 0.0f;
                a = 0.0f;
                activeLines = 0.0f;
                for (int g = 0; g < numBands; g++)
                {
                    ref AacPsyBandState band = ref pch.Band[w + g];
                    band.Thr = CalcReducedThr3gpp(ref band, coeffs[g].MinSnr, reduction);
                    pe += CalcPe3gpp(ref band);
                    a += band.PeConst;
                    activeLines += band.ActiveLines;
                }
            }

            // 5.6.1.3.5 "Second Estimation of the reduction value".
            for (int i = 0; i < 2; i++)
            {
                float peNoAh = 0.0f;
                activeLines = a = 0.0f;
                for (int w = 0; w < wi.NumWindows * 16; w += 16)
                {
                    for (int g = 0; g < numBands; g++)
                    {
                        ref AacPsyBandState band = ref pch.Band[w + g];
                        if (band.AvoidHoles != AhActive)
                        {
                            peNoAh += band.Pe;
                            a += band.PeConst;
                            activeLines += band.ActiveLines;
                        }
                    }
                }

                float desiredPeNoAh = MathF.Max(desiredPe - (pe - peNoAh), 0.0f);
                if (activeLines > 0.0f)
                    reduction = CalcReduction3gpp(a, desiredPeNoAh, peNoAh, activeLines);

                pe = 0.0f;
                for (int w = 0; w < wi.NumWindows * 16; w += 16)
                {
                    for (int g = 0; g < numBands; g++)
                    {
                        ref AacPsyBandState band = ref pch.Band[w + g];

                        if (activeLines > 0.0f)
                            band.Thr = CalcReducedThr3gpp(ref band, coeffs[g].MinSnr, reduction);
                        pe += CalcPe3gpp(ref band);
                        band.NormFac = band.Thr > 0.0f ? band.ActiveLines / band.Thr : 0.0f;
                        normFac += band.NormFac;
                    }
                }

                deltaPe = desiredPe - pe;
                if (MathF.Abs(deltaPe) > 0.05f * desiredPe)
                    break;
            }

            if (pe < 1.15f * desiredPe)
            {
                // 6.6.1.3.6 "Final threshold modification by linearization".
                normFac = normFac != 0 ? 1.0f / normFac : 0;
                for (int w = 0; w < wi.NumWindows * 16; w += 16)
                {
                    for (int g = 0; g < numBands; g++)
                    {
                        ref AacPsyBandState band = ref pch.Band[w + g];

                        if (band.ActiveLines > 0.5f)
                        {
                            float deltaSfbPe = band.NormFac * normFac * deltaPe;
                            float thr = band.Thr;

                            thr *= Exp2F(deltaSfbPe / band.ActiveLines);
                            if (thr > coeffs[g].MinSnr * band.Energy
                                && band.AvoidHoles == AhInactive)
                                thr = MathF.Max(band.Thr, coeffs[g].MinSnr * band.Energy);
                            band.Thr = thr;
                        }
                    }
                }
            }
            else
            {
                // 5.6.1.3.7 "Further perceptual entropy reduction". Note that this MUTATES
                // the shared min_snr coefficients, so a band pushed to -1 dB here stays
                // there for every later frame of the stream.
                int g = numBands;
                while (pe > desiredPe && g-- > 0)
                {
                    for (int w = 0; w < wi.NumWindows * 16; w += 16)
                    {
                        ref AacPsyBandState band = ref pch.Band[w + g];
                        if (band.AvoidHoles != AhNone && coeffs[g].MinSnr < Snr1Db)
                        {
                            coeffs[g].MinSnr = Snr1Db;
                            band.Thr = band.Energy * Snr1Db;
                            pe += band.ActiveLines * 1.5f - band.Pe;
                        }
                    }
                }
            }
        }

        for (int w = 0; w < wi.NumWindows * 16; w += 16)
        {
            for (int g = 0; g < numBands; g++)
            {
                ref AacPsyBandState band = ref pch.Band[w + g];
                ref AacPsyBand psyBand = ref output[w + g];

                psyBand.Threshold = band.Thr;
                psyBand.Energy = band.Energy;
                psyBand.Spread = band.ActiveLines * 2.0f / bandSizes[g];
                psyBand.Bits = (int)PeToBits(band.Pe);
            }
        }

        pch.Band.CopyTo(pch.PrevBand, 0);
    }
}

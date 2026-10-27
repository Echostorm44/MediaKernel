// MPEG-1/2 Audio Layer II - a translation of mp_decode_layer2 from
// ffmpeg-master/libavcodec/mpegaudiodec_template.c, with its tables from mpegaudiodata.c and
// mpegaudiodec_common.c.
//
// It is a translation because the previous implementation was not. That one was written from a
// description of layer II: its synthesis window was a Kaiser approximation carrying the comment
// "in production would use exact values", its allocation tables were described as simplified,
// and - the fault that mattered - it read one sample per codeword where layer II packs THREE
// consecutive samples into one codeword for the grouped quantiser classes. The bit positions
// parted company at the first grouped subband, and the output had a correlation of 0.0005 with
// ffmpeg's: no relationship at all. Its tests had only ever fed it synthetic silent frames, so
// nothing caught it.
//
// Two deliberate differences from the C, both recorded here because they change numbers:
//
//  1. FFmpeg's float build still dequantises in fixed point - l1_unscale and l2_unscale_group
//     return integers scaled by 2^FRAC_BITS - and absorbs the scale into its synthesis window,
//     which it builds as ff_mpa_enwindow / 2^(16 + FRAC_BITS). This project's window is
//     ff_mpa_enwindow / 2^16 and its layer III path feeds natural-scale floats, so the integer
//     result is divided by 2^FRAC_BITS here instead. Same arithmetic, same place in the chain,
//     one multiply moved.
//  2. The synthesis filterbank is Mp3Filterbank's, which is already a translation of
//     ff_mpa_synth_filter_float and is exercised by the MP3 conformance gate. FFmpeg shares one
//     filter across all three layers for exactly this reason.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3;

/// <summary>MPEG-1/2 Audio Layer II decoder (<c>mp_decode_layer2</c>).</summary>
public sealed class Mp2Decoder
{
    /// <summary><c>SBLIMIT</c> - subbands in the filterbank.</summary>
    public const int Subbands = 32;

    /// <summary>Subband samples per frame: 3 scalefactor groups of 12.</summary>
    public const int SamplesPerSubband = 36;

    /// <summary>Granules per scalefactor group.</summary>
    public const int GranulesPerFrame = 12;

    /// <summary>Sample groups a granule is read in.</summary>
    public const int GroupsPerGranule = 3;

    /// <summary>PCM samples per channel per frame.</summary>
    public const int SamplesPerFrame = 1152;

    /// <summary><c>MPA_MAX_CHANNELS</c>.</summary>
    public const int MaxChannels = 2;

    /// <summary><c>FRAC_BITS</c> - the fixed-point scale the unscalers work in.</summary>
    private const int FracBits = 23;

    private const float FracToFloat = 1.0f / (1 << FracBits);

    private readonly Mp3Filterbank[] filterbanks;

    // bit_alloc[ch][sb], scale_code[ch][sb], scale_factors[ch][sb][3] and
    // sb_samples[ch][36][32], flattened. The sample array keeps FFmpeg's index order -
    // time first, then subband - because the synthesis reads 32 subbands of one instant.
    private readonly int[] bitAllocation = new int[MaxChannels * Subbands];
    private readonly int[] scaleCode = new int[MaxChannels * Subbands];
    private readonly int[] scaleFactors = new int[MaxChannels * Subbands * 3];
    private readonly float[] subbandSamples = new float[MaxChannels * SamplesPerSubband * Subbands];

    private int sampleRate;
    private int channelCount;

    /// <summary>Sample rate of the last frame decoded.</summary>
    public int SampleRate => sampleRate;

    /// <summary>Channel count of the last frame decoded.</summary>
    public int ChannelCount => channelCount;

    /// <summary>PCM samples per channel a frame yields.</summary>
    public int OutputSamplesPerFrame => SamplesPerFrame;

    public Mp2Decoder()
    {
        filterbanks = new Mp3Filterbank[MaxChannels];
        for (int ch = 0; ch < MaxChannels; ch++)
            filterbanks[ch] = new Mp3Filterbank();
    }

    /// <summary>Clears the filterbank state, as after a seek.</summary>
    public void Reset()
    {
        foreach (var filterbank in filterbanks)
            filterbank.Reset();
    }

    /// <summary>
    /// Decodes one layer II frame into interleaved PCM.
    /// </summary>
    /// <param name="frameData">The frame, starting at its sync word.</param>
    /// <param name="pcmSamples">Receives <see cref="SamplesPerFrame"/> x channels floats.</param>
    /// <param name="samplesDecoded">Samples per channel produced.</param>
    public bool DecodeFrame(ReadOnlySpan<byte> frameData, Span<float> pcmSamples, out int samplesDecoded)
    {
        samplesDecoded = 0;

        if (frameData.Length < 4) return false;
        if (!Mp3FrameParser.TryParseHeader(frameData, out var header)) return false;
        if (header.Layer != MpegLayer.Layer2) return false;

        sampleRate = header.SampleRate;
        channelCount = header.ChannelCount;

        if (pcmSamples.Length < SamplesPerFrame * channelCount) return false;

        int lsf = header.Version == MpegVersion.Mpeg1 ? 0 : 1;

        int table = SelectTable(header.BitRate, channelCount, header.SampleRate, lsf);
        int sblimit = Mp2Tables.SubbandLimit[table];
        ReadOnlySpan<byte> allocTable = Mp2Tables.AllocationTable(table);

        int bound = header.ChannelMode == Mp3ChannelMode.JointStereo
            ? (header.ModeExtension + 1) * 4
            : sblimit;
        if (bound > sblimit) bound = sblimit;

        // The CRC, when present, sits between the header and the bit allocation.
        var reader = new Mp3BitReader(frameData[(header.HasCrc ? 6 : 4)..]);

        DecodeBitAllocation(ref reader, allocTable, bound, sblimit);
        DecodeScaleCodes(ref reader, sblimit);
        DecodeScaleFactors(ref reader, sblimit);
        DecodeSubbandSamples(ref reader, allocTable, bound, sblimit);

        Synthesize(pcmSamples);

        samplesDecoded = SamplesPerFrame;
        return true;
    }

    /// <summary><c>ff_mpa_l2_select_table</c>.</summary>
    private static int SelectTable(int bitRateKbps, int channels, int frequency, int lsf)
    {
        if (lsf != 0) return 4;

        int channelBitRate = bitRateKbps / channels;

        if ((frequency == 48000 && channelBitRate >= 56) ||
            (channelBitRate >= 56 && channelBitRate <= 80))
            return 0;
        if (frequency != 48000 && channelBitRate >= 96)
            return 1;
        if (frequency != 32000 && channelBitRate <= 48)
            return 2;
        return 3;
    }

    /// <summary>
    /// The bit allocation: how many bits each subband's samples are quantised to.
    /// </summary>
    /// <remarks>
    /// The allocation table is walked rather than indexed. Each subband's entry starts with the
    /// width of its allocation field and is followed by one quantiser index per possible value,
    /// so the next subband's entry is <c>1 &lt;&lt; width</c> further on. That is why the cursor
    /// <c>j</c> has to be carried through every loop that reads allocations or samples, and why
    /// the sample loop below walks it again from the start for each triple of granules.
    /// </remarks>
    private void DecodeBitAllocation(
        ref Mp3BitReader reader, ReadOnlySpan<byte> allocTable, int bound, int sblimit)
    {
        Array.Clear(bitAllocation);

        int j = 0;
        for (int i = 0; i < bound; i++)
        {
            int bits = allocTable[j];
            for (int ch = 0; ch < channelCount; ch++)
                bitAllocation[ch * Subbands + i] = reader.ReadBits(bits);
            j += 1 << bits;
        }

        // Above the bound the two channels share one allocation, which is what joint stereo is.
        for (int i = bound; i < sblimit; i++)
        {
            int bits = allocTable[j];
            int v = reader.ReadBits(bits);
            bitAllocation[i] = v;
            bitAllocation[Subbands + i] = v;
            j += 1 << bits;
        }
    }

    /// <summary><c>scale_code</c> - which of the three scalefactors each group reuses.</summary>
    private void DecodeScaleCodes(ref Mp3BitReader reader, int sblimit)
    {
        for (int i = 0; i < sblimit; i++)
            for (int ch = 0; ch < channelCount; ch++)
                if (bitAllocation[ch * Subbands + i] != 0)
                    scaleCode[ch * Subbands + i] = reader.ReadBits(2);
    }

    /// <summary>
    /// The scalefactors themselves: one, two or three six-bit indices per subband depending on
    /// the scale code, expanded to three.
    /// </summary>
    private void DecodeScaleFactors(ref Mp3BitReader reader, int sblimit)
    {
        for (int i = 0; i < sblimit; i++)
        {
            for (int ch = 0; ch < channelCount; ch++)
            {
                if (bitAllocation[ch * Subbands + i] == 0) continue;

                int at = (ch * Subbands + i) * 3;
                switch (scaleCode[ch * Subbands + i])
                {
                    default:
                    case 0:
                        scaleFactors[at + 0] = reader.ReadBits(6);
                        scaleFactors[at + 1] = reader.ReadBits(6);
                        scaleFactors[at + 2] = reader.ReadBits(6);
                        break;
                    case 2:
                        scaleFactors[at + 0] = reader.ReadBits(6);
                        scaleFactors[at + 1] = scaleFactors[at + 0];
                        scaleFactors[at + 2] = scaleFactors[at + 0];
                        break;
                    case 1:
                        scaleFactors[at + 0] = reader.ReadBits(6);
                        scaleFactors[at + 2] = reader.ReadBits(6);
                        scaleFactors[at + 1] = scaleFactors[at + 0];
                        break;
                    case 3:
                        scaleFactors[at + 0] = reader.ReadBits(6);
                        scaleFactors[at + 2] = reader.ReadBits(6);
                        scaleFactors[at + 1] = scaleFactors[at + 2];
                        break;
                }
            }
        }
    }

    /// <summary>
    /// The quantised samples, in the order layer II stores them: three scalefactor groups, four
    /// triples of granules each, and within a triple every subband of every channel in turn.
    /// </summary>
    /// <remarks>
    /// The three-at-a-time reads are the part that cannot be guessed. For the quantiser classes
    /// with 3, 5 or 9 levels, layer II packs THREE consecutive time samples into one codeword -
    /// 5, 7 or 10 bits for 27, 125 or 729 combinations - and the decoder splits them by
    /// repeated division by the step count. Reading one sample per codeword consumes the wrong
    /// number of bits and desynchronises everything after it.
    /// </remarks>
    private void DecodeSubbandSamples(
        ref Mp3BitReader reader, ReadOnlySpan<byte> allocTable, int bound, int sblimit)
    {
        Array.Clear(subbandSamples);

        for (int k = 0; k < 3; k++)
        {
            for (int l = 0; l < 12; l += 3)
            {
                int j = 0;

                for (int i = 0; i < bound; i++)
                {
                    int allocBits = allocTable[j];
                    for (int ch = 0; ch < channelCount; ch++)
                    {
                        int b = bitAllocation[ch * Subbands + i];
                        if (b == 0) continue;

                        int scale = scaleFactors[(ch * Subbands + i) * 3 + k];
                        int qindex = allocTable[j + b];
                        int bits = Mp2Tables.QuantBits[qindex];

                        if (bits < 0)
                        {
                            int v = reader.ReadBits(-bits);
                            int packed = Mp2Tables.DivisionTable(qindex, v);
                            int steps = Mp2Tables.QuantSteps[qindex];

                            Store(ch, k * 12 + l + 0, i, UnscaleGroup(steps, packed & 15, scale));
                            Store(ch, k * 12 + l + 1, i, UnscaleGroup(steps, (packed >> 4) & 15, scale));
                            Store(ch, k * 12 + l + 2, i, UnscaleGroup(steps, packed >> 8, scale));
                        }
                        else
                        {
                            for (int m = 0; m < 3; m++)
                            {
                                int v = reader.ReadBits(bits);
                                Store(ch, k * 12 + l + m, i, Unscale(bits - 1, v, scale));
                            }
                        }
                    }

                    j += 1 << allocBits;
                }

                // Above the bound one set of mantissas serves both channels, each with its own
                // scalefactor.
                for (int i = bound; i < sblimit; i++)
                {
                    int allocBits = allocTable[j];
                    int b = bitAllocation[i];
                    if (b != 0)
                    {
                        int scale0 = scaleFactors[i * 3 + k];
                        int scale1 = scaleFactors[(Subbands + i) * 3 + k];
                        int qindex = allocTable[j + b];
                        int bits = Mp2Tables.QuantBits[qindex];

                        if (bits < 0)
                        {
                            int v = reader.ReadBits(-bits);
                            int steps = Mp2Tables.QuantSteps[qindex];

                            int mant = v % steps; v /= steps;
                            Store(0, k * 12 + l + 0, i, UnscaleGroup(steps, mant, scale0));
                            Store(1, k * 12 + l + 0, i, UnscaleGroup(steps, mant, scale1));

                            mant = v % steps; v /= steps;
                            Store(0, k * 12 + l + 1, i, UnscaleGroup(steps, mant, scale0));
                            Store(1, k * 12 + l + 1, i, UnscaleGroup(steps, mant, scale1));

                            Store(0, k * 12 + l + 2, i, UnscaleGroup(steps, v, scale0));
                            Store(1, k * 12 + l + 2, i, UnscaleGroup(steps, v, scale1));
                        }
                        else
                        {
                            for (int m = 0; m < 3; m++)
                            {
                                int mant = reader.ReadBits(bits);
                                Store(0, k * 12 + l + m, i, Unscale(bits - 1, mant, scale0));
                                Store(1, k * 12 + l + m, i, Unscale(bits - 1, mant, scale1));
                            }
                        }
                    }

                    j += 1 << allocBits;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Store(int channel, int time, int subband, int fixedPoint)
        => subbandSamples[(channel * SamplesPerSubband + time) * Subbands + subband]
            = fixedPoint * FracToFloat;

    /// <summary>
    /// <c>l1_unscale</c> - an ungrouped mantissa of <paramref name="n"/>+1 bits, scaled.
    /// </summary>
    /// <remarks>
    /// The mantissa is offset by <c>-2^n + 1</c> rather than sign-extended: layer I and II
    /// quantise to a range centred on zero but stored unsigned, and the leading bit is inverted
    /// rather than a sign bit.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Unscale(int n, int mantissa, int scaleFactor)
    {
        int modshift = Mp2Tables.ScaleFactorModshift[scaleFactor];
        int mod = modshift & 3;
        int shift = (modshift >> 2) + n;

        long value = (long)(mantissa + unchecked((int)(-1U << n)) + 1)
                   * Mp2Tables.ScaleFactorMult[(n - 1) * 3 + mod];

        return (int)((value + (1L << (shift - 1))) >> shift);
    }

    /// <summary><c>l2_unscale_group</c> - one sample out of a grouped codeword, scaled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int UnscaleGroup(int steps, int mantissa, int scaleFactor)
    {
        int modshift = Mp2Tables.ScaleFactorModshift[scaleFactor];
        int mod = modshift & 3;
        int shift = modshift >> 2;

        int value = (mantissa - (steps >> 1)) * Mp2Tables.ScaleFactorMult2[(steps >> 2) * 3 + mod];

        if (shift > 0)
            value = (value + (1 << (shift - 1))) >> shift;
        return value;
    }

    /// <summary>Runs the shared polyphase filter over every instant of the frame.</summary>
    private void Synthesize(Span<float> pcmSamples)
    {
        Span<float> pcm32 = stackalloc float[Subbands];

        for (int time = 0; time < SamplesPerSubband; time++)
        {
            for (int ch = 0; ch < channelCount; ch++)
            {
                var instant = subbandSamples.AsSpan(
                    (ch * SamplesPerSubband + time) * Subbands, Subbands);

                filterbanks[ch].SynthesizeSubbands(instant, pcm32);

                int at = time * Subbands * channelCount + ch;
                for (int s = 0; s < Subbands; s++)
                    pcmSamples[at + s * channelCount] = pcm32[s];
            }
        }
    }
}

/// <summary>Layer II tables (<c>mpegaudiodata.c</c>, <c>mpegaudiodec_common.c</c>).</summary>
public static class Mp2Tables
{
    /// <summary><c>ff_mpa_sblimit_table</c>.</summary>
    public static ReadOnlySpan<int> SubbandLimit => [27, 30, 8, 12, 30];

    /// <summary><c>ff_mpa_quant_steps</c>.</summary>
    public static ReadOnlySpan<int> QuantSteps =>
    [
        3, 5, 7, 9, 15,
        31, 63, 127, 255, 511,
        1023, 2047, 4095, 8191, 16383,
        32767, 65535,
    ];

    /// <summary>
    /// <c>ff_mpa_quant_bits</c> - bits per codeword, negative where one codeword carries three
    /// samples.
    /// </summary>
    public static ReadOnlySpan<int> QuantBits =>
    [
        -5, -7, 3, -10, 4,
        5, 6, 7, 8, 9,
        10, 11, 12, 13, 14,
        15, 16,
    ];

    /// <summary><c>alloc_table_1</c> - 48 kHz and high-bitrate 44.1 kHz.</summary>
    private static ReadOnlySpan<byte> AllocTable1 =>
    [
        4,  0,  2,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16,
        4,  0,  2,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16,
        4,  0,  2,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16,
        4,  0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 16,
        4,  0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 16,
        4,  0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 16,
        4,  0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 16,
        4,  0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 16,
        4,  0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 16,
        4,  0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 16,
        4,  0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 16,
        3,  0,  1,  2,  3,  4,  5, 16,
        3,  0,  1,  2,  3,  4,  5, 16,
        3,  0,  1,  2,  3,  4,  5, 16,
        3,  0,  1,  2,  3,  4,  5, 16,
        3,  0,  1,  2,  3,  4,  5, 16,
        3,  0,  1,  2,  3,  4,  5, 16,
        3,  0,  1,  2,  3,  4,  5, 16,
        3,  0,  1,  2,  3,  4,  5, 16,
        3,  0,  1,  2,  3,  4,  5, 16,
        3,  0,  1,  2,  3,  4,  5, 16,
        3,  0,  1,  2,  3,  4,  5, 16,
        3,  0,  1,  2,  3,  4,  5, 16,
        2,  0,  1, 16,
        2,  0,  1, 16,
        2,  0,  1, 16,
        2,  0,  1, 16,
        2,  0,  1, 16,
        2,  0,  1, 16,
        2,  0,  1, 16,
    ];

    /// <summary><c>alloc_table_3</c> - low bitrates.</summary>
    private static ReadOnlySpan<byte> AllocTable3 =>
    [
        4,  0,  1,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15,
        4,  0,  1,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
    ];

    /// <summary><c>alloc_table_4</c> - MPEG-2 low sampling frequencies.</summary>
    private static ReadOnlySpan<byte> AllocTable4 =>
    [
        4,  0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14,
        4,  0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14,
        4,  0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14,
        4,  0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
        3,  0,  1,  3,  4,  5,  6,  7,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
        2,  0,  1,  3,
    ];

    /// <summary><c>ff_mpa_alloc_tables</c> - five entries over three distinct tables.</summary>
    public static ReadOnlySpan<byte> AllocationTable(int index) => index switch
    {
        0 or 1 => AllocTable1,
        2 or 3 => AllocTable3,
        4 => AllocTable4,
        // Unknown input fails loudly: a silent fallback would decode with the wrong field
        // widths and produce noise that looks like a broken filterbank.
        _ => throw new ArgumentOutOfRangeException(
            nameof(index), index, "Layer II allocation table index must be 0-4."),
    };

    /// <summary><c>ff_scale_factor_modshift</c> - the scalefactor's shift and third-octave part.</summary>
    public static ReadOnlySpan<int> ScaleFactorModshift => scaleFactorModshift;

    /// <summary><c>scale_factor_mult[15][3]</c>, flattened.</summary>
    public static ReadOnlySpan<int> ScaleFactorMult => scaleFactorMult;

    /// <summary><c>scale_factor_mult2[3][3]</c>, flattened.</summary>
    public static ReadOnlySpan<int> ScaleFactorMult2 => scaleFactorMult2;

    /// <summary>
    /// <c>ff_division_tabs</c> - a grouped codeword split into its three sample indices, four
    /// bits each.
    /// </summary>
    public static int DivisionTable(int quantIndex, int codeword) => quantIndex switch
    {
        0 => divisionTable3[codeword],
        1 => divisionTable5[codeword],
        3 => divisionTable9[codeword],
        _ => throw new ArgumentOutOfRangeException(
            nameof(quantIndex), quantIndex, "Only quantiser classes 0, 1 and 3 are grouped."),
    };

    private const int FracBits = 23;
    private const long FracOne = 1L << FracBits;

    private static readonly int[] scaleFactorModshift = BuildModshift();
    private static readonly int[] scaleFactorMult = BuildScaleFactorMult();
    private static readonly int[] scaleFactorMult2 = BuildScaleFactorMult2();
    private static readonly short[] divisionTable3 = BuildDivisionTable(0);
    private static readonly short[] divisionTable5 = BuildDivisionTable(1);
    private static readonly short[] divisionTable9 = BuildDivisionTable(3);

    private static int[] BuildModshift()
    {
        // mpegaudiodec_common.c: 1.0 (i = 3) is normalised to 2^FRAC_BITS.
        var table = new int[64];
        for (int i = 0; i < 64; i++)
            table[i] = (i % 3) | ((i / 3) << 2);
        return table;
    }

    private static int[] BuildScaleFactorMult()
    {
        // decode_init_static: norm = 2^n * FRAC_ONE / (2^n - 1), times 2, 2*2^(-1/3), 2*2^(-2/3).
        var table = new int[15 * 3];
        for (int i = 0; i < 15; i++)
        {
            int n = i + 2;
            long norm = ((1L << n) * FracOne) / ((1L << n) - 1);
            table[i * 3 + 0] = (int)(norm * 1.0 * 2.0);
            table[i * 3 + 1] = (int)(norm * 0.7937005259 * 2.0);
            table[i * 3 + 2] = (int)(norm * 0.6299605249 * 2.0);
        }
        return table;
    }

    private static int[] BuildScaleFactorMult2()
    {
        // SCALE_GEN(v) = { FIXR_OLD(1.0 v), FIXR_OLD(0.7937005259 v), FIXR_OLD(0.6299605249 v) }
        // with FIXR_OLD(a) = (int)(a * FRAC_ONE + 0.5), for 3, 5 and 9 steps.
        ReadOnlySpan<double> generators = [4.0 / 3.0, 4.0 / 5.0, 4.0 / 9.0];
        ReadOnlySpan<double> thirds = [1.0, 0.7937005259, 0.6299605249];

        var table = new int[3 * 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                table[i * 3 + j] = (int)(thirds[j] * generators[i] * FracOne + 0.5);
        return table;
    }

    private static short[] BuildDivisionTable(int quantIndex)
    {
        int bits = -QuantBits[quantIndex];
        int steps = QuantSteps[quantIndex];

        var table = new short[1 << (bits + 1)];
        for (int j = 0; j < table.Length; j++)
        {
            int value = j;
            int v1 = value % steps; value /= steps;
            int v2 = value % steps; int v3 = value / steps;
            table[j] = (short)(v1 + (v2 << 4) + (v3 << 8));
        }
        return table;
    }
}

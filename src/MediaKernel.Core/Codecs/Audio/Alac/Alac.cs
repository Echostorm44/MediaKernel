// Translated from ffmpeg-master/libavcodec/alac.c, alacdsp.c and alac_data.c.
//
// WHY THIS EXISTS WHEN AlacDecoder.cs ALREADY DID. The decoder beside this one was written from
// "reverse-engineered format documentation", which is the way the porting rule "Translate, never
// reconstruct" rule exists to prevent, and nothing ever compared its output against ffmpeg. The
// plan's Phase 7 item 4 is to replace it with a translation, and this is that: one method per C
// function, named by PascalCasing the C name, same control flow and same arithmetic.
//
// ALAC is lossless, so the gate is exact equality with ffmpeg's own decode - not a tolerance.
// There is no transform and no rounding anywhere in it; every sample is either right or wrong.
//
// THE ARITHMETIC THAT HAS TO SURVIVE TRANSLATION. Three places rely on C's unsigned wraparound,
// and C# needs them written as unsigned to behave the same way:
//
//   * decorrelate_stereo works in uint32_t: `a -= (int)(b * decorr_left_weight) >> decorr_shift`
//     then `b += a`, with the wrap intended.
//   * lpc_prediction accumulates `error_val` as unsigned and subtracts
//     `(val >> lpc_quant) * (j + 1U)` from it, and its loop condition reads that unsigned value
//     back as a signed int.
//   * append_extra_bits shifts a signed sample left as unsigned before OR-ing the extra bits in.
//
// Each is written here the way the C writes it, with the cast at the same place.

using System;
using System.Runtime.CompilerServices;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Audio.Alac;

/// <summary><c>enum AlacRawDataBlockType</c> of <c>alac_data.h</c>.</summary>
internal enum AlacRawDataBlockType
{
    /// <summary>Single channel element.</summary>
    Sce = 0,

    /// <summary>Channel pair element.</summary>
    Cpe = 1,

    /// <summary>Coupling channel element.</summary>
    Cce = 2,

    /// <summary>Low frequency effects element.</summary>
    Lfe = 3,

    /// <summary>Data stream element.</summary>
    Dse = 4,

    /// <summary>Program config element.</summary>
    Pce = 5,

    /// <summary>Fill element.</summary>
    Fil = 6,

    /// <summary>End of frame.</summary>
    End = 7,
}

/// <summary><c>alac_data.c</c>'s tables.</summary>
internal static class AlacData
{
    /// <summary><c>ALAC_MAX_CHANNELS</c>.</summary>
    public const int MaxChannels = 8;

    /// <summary>
    /// <c>ff_alac_channel_layout_offsets[ALAC_MAX_CHANNELS][ALAC_MAX_CHANNELS]</c> - where each
    /// element's channels land in the output, by total channel count.
    /// </summary>
    /// <remarks>
    /// Flattened; index as <c>[(channels - 1) * 8 + ch]</c>. ALAC orders a 3-channel stream
    /// centre-first, so the first element decodes into output channel 2 and not channel 0;
    /// reading this table as identity swaps the front channels of everything above stereo.
    /// </remarks>
    public static ReadOnlySpan<byte> ChannelLayoutOffsets =>
    [
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 0, 0, 0, 0, 0, 0,
        2, 0, 1, 0, 0, 0, 0, 0,
        2, 0, 1, 3, 0, 0, 0, 0,
        2, 0, 1, 3, 4, 0, 0, 0,
        2, 0, 1, 4, 5, 3, 0, 0,
        2, 0, 1, 4, 5, 6, 3, 0,
        2, 6, 7, 0, 1, 4, 5, 3,
    ];

    /// <summary>
    /// One entry of <see cref="ChannelLayoutOffsets"/>, so a caller that cannot hold a span -
    /// an async test method, for one - can still read the table it is being held to.
    /// </summary>
    public static int Offset(int channels, int element) =>
        ChannelLayoutOffsets[(channels - 1) * 8 + element];
}

/// <summary><c>ALACContext</c> and the whole of <c>alac.c</c>'s decoder.</summary>
public sealed class Alac
{
    /// <summary><c>ALAC_EXTRADATA_SIZE</c>.</summary>
    public const int ExtradataSize = 36;

    private GetBitContext gb;

    /// <summary>
    /// A reusable copy of the packet, because <see cref="GetBitContext"/> reads from a
    /// <c>byte[]</c>. Allocating one per frame is a defect in this project, not an
    /// inefficiency: no per-frame allocation is a cornerstone, and a lossless codec runs this
    /// path for every frame of a whole album.
    /// </summary>
    private byte[] packetBuffer = [];

    /// <summary>
    /// <c>int16_t lpc_coefs[2][32]</c>, which the C declares on the stack per element. Kept as
    /// a field for the same reason as <see cref="packetBuffer"/>; the read loop fills every
    /// entry the predictor uses, so it needs no clearing, exactly as the C's does not.
    /// </summary>
    private readonly short[][] lpcCoefsStore = [new short[32], new short[32]];

    private readonly int[][] predictErrorBuffer = new int[2][];
    private readonly int[][] outputSamplesBuffer = new int[2][];
    private readonly int[][] extraBitsBuffer = new int[2][];

    private uint maxSamplesPerFrame;
    private byte sampleSize;
    private byte riceHistoryMult;
    private byte riceInitialHistory;
    private byte riceLimit;

    private int extraBits;      // number of extra bits beyond 16-bit
    private int nbSamples;      // number of samples in the current frame

    /// <summary><c>alac->channels</c>.</summary>
    public int Channels { get; private set; }

    /// <summary><c>alac->sample_rate</c>.</summary>
    public int SampleRate { get; private set; }

    /// <summary><c>alac->sample_size</c> - 16, 20, 24 or 32.</summary>
    public int SampleSize => sampleSize;

    /// <summary><c>alac->max_samples_per_frame</c>.</summary>
    public int MaxSamplesPerFrame => (int)maxSamplesPerFrame;

    /// <summary>
    /// <c>alac->extra_bit_bug</c> - the reference encoder before a certain version applied the
    /// extra bits on the wrong side of stereo decorrelation, and ffmpeg reproduces that for
    /// files it wrote. FFmpeg sets it from an AVOption which defaults to off.
    /// </summary>
    public bool ExtraBitBug { get; set; }

    /// <summary>
    /// The decoded samples, one array per channel, in output channel order. They are the
    /// codec's own widths: 16-bit values for a 16-bit stream, and 32-bit for 20, 24 and 32,
    /// with 20- and 24-bit samples shifted up as the C shifts them.
    /// </summary>
    public int[][] OutputPlanes { get; private set; } = [];

    /// <summary>Samples per channel in the frame just decoded.</summary>
    public int FrameSamples => nbSamples;

    /// <summary>
    /// <c>decode_scalar</c> - one Rice-coded value, or an escape to a literal when the unary
    /// prefix runs past nine.
    /// </summary>
    /// <remarks>
    /// The <c>(x &lt;&lt; k) - x</c> is the C's own comment's "multiply x by 2^k - 1, as part
    /// of their strange algorithm", and the asymmetric <c>skip_bits(gb, k)</c> against
    /// <c>skip_bits(gb, k - 1)</c> is deliberate: a leading zero in the k extra bits means the
    /// value needed one bit fewer.
    /// </remarks>
    private static uint DecodeScalar(ref GetBitContext gb, int k, int bps)
    {
        uint x = GetUnary09(ref gb);

        if (x > 8)
        {
            // RICE THRESHOLD: use alternative encoding
            x = gb.GetBits(bps);
        }
        else if (k != 1)
        {
            int extrabits = (int)gb.ShowBits(k);

            // multiply x by 2^k - 1, as part of their strange algorithm
            x = (x << k) - x;

            if (extrabits > 1)
            {
                x += (uint)extrabits - 1;
                gb.SkipBits(k);
            }
            else
            {
                gb.SkipBits(k - 1);
            }
        }
        return x;
    }

    /// <summary><c>get_unary_0_9</c> of <c>unary.h</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GetUnary09(ref GetBitContext gb)
    {
        int i;
        for (i = 0; i < 9 && gb.GetBits1() != 0; i++) { }
        return (uint)i;
    }

    /// <summary>
    /// <c>rice_decompress</c> - the residual for one channel, with the adaptive Rice parameter
    /// and the run-of-zeros escape.
    /// </summary>
    /// <returns>False where the C returns AVERROR_INVALIDDATA.</returns>
    private bool RiceDecompress(int[] outputBuffer, int nbSamples, int bps, int riceHistoryMult)
    {
        uint history = riceInitialHistory;
        int signModifier = 0;

        for (int i = 0; i < nbSamples; i++)
        {
            if (gb.GetBitsLeft() <= 0) return false;

            // calculate rice param and decode next value
            int k = Log2((history >> 9) + 3);
            k = Math.Min(k, riceLimit);
            uint x = DecodeScalar(ref gb, k, bps);
            x += (uint)signModifier;
            signModifier = 0;
            outputBuffer[i] = (int)((x >> 1) ^ (uint)-(int)(x & 1));

            // update the history
            if (x > 0xFFFF)
                history = 0xFFFF;
            else
                history += x * (uint)riceHistoryMult
                         - ((history * (uint)riceHistoryMult) >> 9);

            // special case: there may be compressed blocks of 0
            if (history < 128 && i + 1 < nbSamples)
            {
                k = 7 - Log2(history) + (int)((history + 16) >> 6);
                k = Math.Min(k, riceLimit);
                int blockSize = (int)DecodeScalar(ref gb, k, 16);

                if (blockSize > 0)
                {
                    if (blockSize >= nbSamples - i)
                    {
                        // The C logs "invalid zero block size" and carries on with a clamped
                        // size rather than failing the frame.
                        blockSize = nbSamples - i - 1;
                    }
                    Array.Clear(outputBuffer, i + 1, blockSize);
                    i += blockSize;
                }
                if (blockSize <= 0xFFFF) signModifier = 1;
                history = 0;
            }
        }
        return true;
    }

    /// <summary><c>av_log2</c> - the index of the highest set bit, and 0 for 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Log2(uint v) =>
        v == 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount(v);

    /// <summary><c>sign_only</c> - <c>FFDIFFSIGN(v, 0)</c>, so -1, 0 or 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SignOnly(int v) => (v > 0 ? 1 : 0) - (v < 0 ? 1 : 0);

    /// <summary>Two ints, for the per-channel locals of <c>decode_element</c>.</summary>
    [System.Runtime.CompilerServices.InlineArray(2)]
    private struct Int2 { private int element0; }

    /// <summary><c>sign_extend</c> of <c>mathops.h</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SignExtend(int val, int bits)
    {
        int shift = 32 - bits;
        return (val * (1 << shift)) >> shift;
    }

    /// <summary>
    /// <c>lpc_prediction</c> - the adaptive FIR that turns the residual back into samples, and
    /// updates its own coefficients as it goes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coefficient adaptation is the part that must be translated and not reasoned about:
    /// <c>error_val</c> is unsigned, the loop runs while <c>(int)(error_val * error_sign)</c> is
    /// positive - which reads that unsigned product back as signed - and the subtraction
    /// <c>error_val -= (val &gt;&gt; lpc_quant) * (j + 1U)</c> is unsigned. Writing any of it in
    /// signed arithmetic changes where the loop stops on inputs that wrap.
    /// </para>
    /// <para>
    /// <c>pred</c> is the C's second pointer into the same buffer, one element behind the write
    /// cursor; here it is the index <c>i - 1</c>, and <c>pred[j]</c> is
    /// <c>bufferOut[i - 1 + j]</c>.
    /// </para>
    /// </remarks>
    private static void LpcPrediction(
        int[] errorBuffer, int[] bufferOut, int nbSamples, int bps,
        short[]? lpcCoefs, int lpcOrder, int lpcQuant)
    {
        // first sample always copies
        bufferOut[0] = errorBuffer[0];

        if (nbSamples <= 1) return;

        if (lpcOrder == 0)
        {
            Array.Copy(errorBuffer, 1, bufferOut, 1, nbSamples - 1);
            return;
        }

        int i;
        if (lpcOrder == 31)
        {
            // simple 1st-order prediction
            for (i = 1; i < nbSamples; i++)
                bufferOut[i] = SignExtend(bufferOut[i - 1] + errorBuffer[i], bps);
            return;
        }

        // read warm-up samples
        for (i = 1; i <= lpcOrder && i < nbSamples; i++)
            bufferOut[i] = SignExtend(bufferOut[i - 1] + errorBuffer[i], bps);

        int predIndex = 0;
        for (; i < nbSamples; i++)
        {
            int val = 0;
            uint errorVal = (uint)errorBuffer[i];
            int d = bufferOut[predIndex++];

            // LPC prediction
            for (int j = 0; j < lpcOrder; j++)
                val += (bufferOut[predIndex + j] - d) * lpcCoefs![j];
            val = (int)((val + (1L << (lpcQuant - 1))) >> lpcQuant);
            val += d + (int)errorVal;
            bufferOut[i] = SignExtend(val, bps);

            // adapt LPC coefficients
            int errorSign = SignOnly((int)errorVal);
            if (errorSign != 0)
            {
                for (int j = 0; j < lpcOrder && (int)(errorVal * (uint)errorSign) > 0; j++)
                {
                    val = d - bufferOut[predIndex + j];
                    int sign = SignOnly(val) * errorSign;
                    lpcCoefs![j] -= (short)sign;
                    val = (int)((uint)val * (uint)sign);
                    errorVal -= (uint)(val >> lpcQuant) * (uint)(j + 1);
                }
            }
        }
    }

    /// <summary><c>decorrelate_stereo</c> of <c>alacdsp.c</c>.</summary>
    /// <remarks>
    /// Entirely unsigned in the C, and the wraparound is intended: a lossless codec has to undo
    /// exactly what the encoder did, including an overflow. Measured, the alac gate cannot tell
    /// this from the same expression in signed arithmetic - its streams never reach the wrap -
    /// so the unsigned form is here because the C's is, and not because a test caught it.
    /// </remarks>
    private static void DecorrelateStereo(
        int[] left, int[] right, int nbSamples, int decorrShift, int decorrLeftWeight)
    {
        for (int i = 0; i < nbSamples; i++)
        {
            uint a = (uint)left[i];
            uint b = (uint)right[i];

            a -= (uint)((int)(b * (uint)decorrLeftWeight) >> decorrShift);
            b += a;

            left[i] = (int)b;
            right[i] = (int)a;
        }
    }

    /// <summary><c>append_extra_bits</c> of <c>alacdsp.c</c>.</summary>
    private static void AppendExtraBits(
        int[][] buffer, int[][] extra, int extraBits, int channels, int nbSamples)
    {
        for (int ch = 0; ch < channels; ch++)
            for (int i = 0; i < nbSamples; i++)
                buffer[ch][i] = (int)(((uint)buffer[ch][i] << extraBits) | (uint)extra[ch][i]);
    }

    /// <summary>
    /// <c>alac_set_info</c> and <c>alac_decode_init</c> - reads the 36-byte ALACSpecificConfig
    /// and allocates the per-channel buffers.
    /// </summary>
    /// <param name="extradata">The <c>alac</c> atom's payload, at least 36 bytes.</param>
    public void Initialize(ReadOnlySpan<byte> extradata)
    {
        if (extradata.Length < ExtradataSize)
            throw new InvalidOperationException(
                $"ALAC: extradata is {extradata.Length} bytes, and ALACSpecificConfig is "
              + $"{ExtradataSize}.");

        // alac_set_info: size:4, alac:4, version:4 are skipped.
        maxSamplesPerFrame = ReadBe32(extradata[12..]);
        if (maxSamplesPerFrame == 0 || maxSamplesPerFrame > 4096 * 4096)
            throw new InvalidOperationException(
                $"ALAC: max samples per frame invalid: {maxSamplesPerFrame}.");

        // extradata[16] is the compatible version, which the C skips.
        sampleSize = extradata[17];
        riceHistoryMult = extradata[18];
        riceInitialHistory = extradata[19];
        riceLimit = extradata[20];
        Channels = extradata[21];

        // The C reads and drops maxRun (be16 at 22), max coded frame size (be32 at 24) and
        // average bitrate (be32 at 28), which puts the sample rate at 32. Reading it at 28
        // gives the average bitrate instead, and the stream then plays at sixteen times speed
        // while every sample is still correct - which is exactly what happened, and is why the
        // gate asserts the rate and not only the samples.
        SampleRate = (int)ReadBe32(extradata[32..]);

        if (sampleSize is not (16 or 20 or 24 or 32))
            throw new NotSupportedException(
                $"ALAC: sample depth {sampleSize}. The C calls this a missing feature rather "
              + "than invalid data, and so is this.");

        if (Channels < 1)
            throw new InvalidOperationException("ALAC: invalid channel count.");
        if (Channels > AlacData.MaxChannels)
            throw new NotSupportedException($"ALAC: channel count {Channels}.");

        // allocate_buffers. The C allocates two of each whatever the channel count, because an
        // element is never wider than a channel pair.
        for (int ch = 0; ch < 2; ch++)
        {
            predictErrorBuffer[ch] = new int[maxSamplesPerFrame];
            outputSamplesBuffer[ch] = new int[maxSamplesPerFrame];
            extraBitsBuffer[ch] = new int[maxSamplesPerFrame];
        }

        OutputPlanes = new int[Channels][];
        for (int ch = 0; ch < Channels; ch++)
            OutputPlanes[ch] = new int[maxSamplesPerFrame];
    }

    private static uint ReadBe32(ReadOnlySpan<byte> b) =>
        ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    /// <summary>
    /// <c>alac_decode_frame</c> - walks the frame's syntax elements and decodes each into its
    /// place in <see cref="OutputPlanes"/>.
    /// </summary>
    /// <returns>Samples per channel, or 0 when the frame yields none.</returns>
    public int DecodeFrame(ReadOnlySpan<byte> packet)
    {
        if (OutputPlanes.Length == 0)
            throw new InvalidOperationException("ALAC: DecodeFrame before Initialize.");
        if (packet.Length == 0) return 0;

        if (packetBuffer.Length < packet.Length) packetBuffer = new byte[packet.Length];
        packet.CopyTo(packetBuffer);
        gb = default;
        gb.InitGetBits8(packetBuffer, packet.Length);

        bool gotEnd = false;
        nbSamples = 0;
        int ch = 0;

        while (gb.GetBitsLeft() >= 3)
        {
            var element = (AlacRawDataBlockType)gb.GetBits(3);
            if (element == AlacRawDataBlockType.End)
            {
                gotEnd = true;
                break;
            }
            if (element > AlacRawDataBlockType.Cpe && element != AlacRawDataBlockType.Lfe)
                throw new NotSupportedException($"ALAC: syntax element {(int)element}.");

            int channels = element == AlacRawDataBlockType.Cpe ? 2 : 1;
            int offset = AlacData.ChannelLayoutOffsets[(Channels - 1) * 8 + ch];
            if (ch + channels > Channels || offset + channels > Channels)
                throw new InvalidOperationException("ALAC: invalid element channel count.");

            if (!DecodeElement(offset, channels) && gb.GetBitsLeft() != 0)
                throw new InvalidOperationException("ALAC: element decode failed.");

            ch += channels;
        }

        if (!gotEnd)
            throw new InvalidOperationException(
                "ALAC: no end tag found, incomplete packet.");

        // The C warns rather than failing when the channels do not add up.
        return Channels == ch ? nbSamples : 0;
    }

    /// <summary><c>decode_element</c> - one SCE, CPE or LFE.</summary>
    private bool DecodeElement(int chIndex, int channels)
    {
        gb.SkipBits(4);     // element instance tag
        gb.SkipBits(12);    // unused header bits

        // the number of output samples is stored in the frame
        bool hasSize = gb.GetBits1() != 0;

        extraBits = (int)gb.GetBits(2) << 3;
        int bps = sampleSize - extraBits + channels - 1;
        if (bps > 32)
            throw new NotSupportedException($"ALAC: bps {bps}.");
        if (bps < 1) return false;

        // whether the frame is compressed
        bool isCompressed = gb.GetBits1() == 0;

        uint outputSamples = hasSize ? gb.GetBits(32) : maxSamplesPerFrame;
        if (outputSamples == 0 || outputSamples > maxSamplesPerFrame)
            throw new InvalidOperationException(
                $"ALAC: invalid samples per frame: {outputSamples}.");
        if (nbSamples != 0 && outputSamples != nbSamples)
            throw new InvalidOperationException(
                $"ALAC: sample count mismatch: {outputSamples} != {nbSamples}.");
        nbSamples = (int)outputSamples;

        int decorrShift, decorrLeftWeight;

        if (isCompressed)
        {
            short[][] lpcCoefs = lpcCoefsStore;

            // Four pairs of per-channel locals. Inline arrays rather than stackalloc: a method
            // containing localloc never reaches Tier1 or Dynamic PGO, and this one runs per
            // element.
            Int2 lpcOrderStore = default, predictionTypeStore = default;
            Int2 lpcQuantStore = default, riceHistoryMultStore = default;
            Span<int> lpcOrder = lpcOrderStore;
            Span<int> predictionType = predictionTypeStore;
            Span<int> lpcQuant = lpcQuantStore;
            Span<int> riceHistoryMultPerCh = riceHistoryMultStore;

            if (riceLimit == 0)
                throw new NotSupportedException("ALAC: compression with rice limit 0.");

            decorrShift = (int)gb.GetBits(8);
            decorrLeftWeight = (int)gb.GetBits(8);

            if (channels == 2 && decorrLeftWeight != 0 && decorrShift > 31) return false;

            for (int c = 0; c < channels; c++)
            {
                predictionType[c] = (int)gb.GetBits(4);
                lpcQuant[c] = (int)gb.GetBits(4);
                riceHistoryMultPerCh[c] = (int)gb.GetBits(3);
                lpcOrder[c] = (int)gb.GetBits(5);

                if (lpcOrder[c] >= maxSamplesPerFrame || lpcQuant[c] == 0) return false;

                // read the predictor table
                for (int i = lpcOrder[c] - 1; i >= 0; i--)
                    lpcCoefs[c][i] = (short)gb.GetSBits(16);
            }

            if (extraBits != 0)
            {
                if (gb.GetBitsLeft() < (long)nbSamples * channels * extraBits) return false;
                for (int i = 0; i < nbSamples; i++)
                    for (int c = 0; c < channels; c++)
                        extraBitsBuffer[c][i] = (int)gb.GetBits(extraBits);
            }

            for (int c = 0; c < channels; c++)
            {
                if (!RiceDecompress(
                        predictErrorBuffer[c], nbSamples, bps,
                        riceHistoryMultPerCh[c] * riceHistoryMult / 4))
                    return false;

                // adaptive FIR filter
                if (predictionType[c] == 15)
                {
                    // Prediction type 15 runs the adaptive FIR twice: the first pass uses the
                    // special-case coef_num = 31, the second the coefs from the bitstream. The
                    // C notes the reference encoder does not currently use it.
                    LpcPrediction(
                        predictErrorBuffer[c], predictErrorBuffer[c], nbSamples, bps,
                        null, 31, 0);
                }
                // The C logs a warning for any other non-zero prediction type and decodes on.

                LpcPrediction(
                    predictErrorBuffer[c], outputSamplesBuffer[c], nbSamples, bps,
                    lpcCoefs[c], lpcOrder[c], lpcQuant[c]);
            }
        }
        else
        {
            // not compressed, easy case
            if (gb.GetBitsLeft() < (long)nbSamples * channels * sampleSize) return false;
            for (int i = 0; i < nbSamples; i++)
                for (int c = 0; c < channels; c++)
                    outputSamplesBuffer[c][i] = gb.GetSBits(sampleSize);

            extraBits = 0;
            decorrShift = 0;
            decorrLeftWeight = 0;
        }

        if (channels == 2)
        {
            if (extraBits != 0 && ExtraBitBug)
                AppendExtraBits(outputSamplesBuffer, extraBitsBuffer, extraBits, channels, nbSamples);

            if (decorrLeftWeight != 0)
                DecorrelateStereo(
                    outputSamplesBuffer[0], outputSamplesBuffer[1], nbSamples,
                    decorrShift, decorrLeftWeight);

            if (extraBits != 0 && !ExtraBitBug)
                AppendExtraBits(outputSamplesBuffer, extraBitsBuffer, extraBits, channels, nbSamples);
        }
        else if (extraBits != 0)
        {
            AppendExtraBits(outputSamplesBuffer, extraBitsBuffer, extraBits, channels, nbSamples);
        }

        // The C writes 16-bit output straight into the frame and shifts the wider depths up in
        // place; both end up in OutputPlanes here, at the codec's own width.
        switch (sampleSize)
        {
            case 20:
                for (int c = 0; c < channels; c++)
                    for (int i = 0; i < nbSamples; i++)
                        outputSamplesBuffer[c][i] = (int)((uint)outputSamplesBuffer[c][i] << 12);
                break;
            case 24:
                for (int c = 0; c < channels; c++)
                    for (int i = 0; i < nbSamples; i++)
                        outputSamplesBuffer[c][i] = (int)((uint)outputSamplesBuffer[c][i] << 8);
                break;
        }

        // The C's 16-bit arm writes through an int16_t pointer, so it truncates; that is not
        // incidental, because bps is sample_size + channels - 1 and a stereo 16-bit element is
        // decoded at 17 bits before decorrelation brings it back. Copying the int32 values
        // without the truncation leaves samples that are right almost everywhere and 65536 out
        // where the encoder relied on the wrap.
        if (sampleSize == 16)
        {
            for (int c = 0; c < channels; c++)
            {
                int[] plane = OutputPlanes[chIndex + c];
                int[] src = outputSamplesBuffer[c];
                for (int i = 0; i < nbSamples; i++) plane[i] = (short)src[i];
            }
        }
        else
        {
            for (int c = 0; c < channels; c++)
                Array.Copy(outputSamplesBuffer[c], OutputPlanes[chIndex + c], nbSamples);
        }

        return true;
    }
}

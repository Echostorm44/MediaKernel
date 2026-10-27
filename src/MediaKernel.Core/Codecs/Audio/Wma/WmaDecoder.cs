// WMA v1/v2 decoder - a translation of ffmpeg-master/libavcodec/wmadec.c.
//
// Three things in the C are pointers walking over buffers, and they are the parts most easily got
// wrong in a port, so they are made explicit here rather than imitated:
//
//  - `exponents` walks forward through the channel's exponent curve as the band loop advances, and
//    at one point is read *backwards* off its current position:
//    `mult1 = mult * exponents[(-(1 << bsize)) >> esize]`. That is an offset into the array, not a
//    separate value, so the offset is tracked in a variable and the negative index is written as
//    such, with the subtraction visible.
//  - `coefs` and `coefs1` are written and read through post-incremented pointers across several
//    loops in sequence, where each loop's start depends on how far the previous one got.
//  - the exponent run writer is a Duff's device, which writes exactly n values; it is a plain loop
//    here, because the C's form exists to save loop overhead and says nothing else.
//
// Float precision is mirrored statement by statement. The C assigns to `float` locals from
// expressions that promote to double - `sqrt`, `ff_exp10` - so each assignment rounds, and the
// casts here are where those roundings happen. Collapsing them into one double expression changes
// the output.

using System;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Audio.Wma;

/// <summary>
/// A WMA v1 or v2 decoder: one instance per stream.
/// </summary>
public sealed class WmaDecoder
{
    private readonly WmaContext s = new();
    private GetBitContext gb;

    /// <summary>What the C reads past the end of a packet, <c>AV_INPUT_BUFFER_PADDING_SIZE</c>.</summary>
    private const int PacketPadding = 64;

    /// <summary>Reused across packets; see the note in <see cref="DecodeSuperframe"/>.</summary>
    private byte[] packetBuffer = [];

    /// <summary>The samples the last decode produced, one array per channel.</summary>
    public float[][] Samples { get; private set; } = [];

    /// <summary>How many samples per channel <see cref="Samples"/> holds.</summary>
    public int SampleCount { get; private set; }

    /// <summary>
    /// Why the last decode failed, or null. Set where the C logs an error, and for the same
    /// reasons: these are the places a stream can be malformed rather than merely unsupported.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>True when the stream carries frames across packet boundaries.</summary>
    public bool UsesBitReservoir => s.UseBitReservoir;

    /// <summary>True when the stream may switch block sizes within a frame.</summary>
    public bool UsesVariableBlockLen => s.UseVariableBlockLen;

    /// <summary>True when exponents are VLC-coded deltas rather than LSP coefficients.</summary>
    public bool UsesExponentVlc => s.UseExpVlc;

    /// <summary>True when perceptual noise is added to the high bands.</summary>
    public bool UsesNoiseCoding => s.UseNoiseCoding;

    /// <summary>
    /// True when the last block decoded used mid/side stereo, so the gate can assert that its
    /// streams reach the butterflies rather than hoping they do.
    /// </summary>
    public bool IsMidSide => s.MsStereo;

    /// <summary>
    /// Samples of priming the first frames carry, which a player drops: the C sets
    /// <c>avctx-&gt;delay = s-&gt;frame_len * 2</c> and its flush sets the same as skip_samples.
    /// </summary>
    /// <remarks>
    /// Measured as well as declared: aligning this decoder output against ffmpeg -f f32le on the
    /// 16 kHz stream finds the best match at exactly this offset, and at that offset 7,126,016
    /// samples agree to within 6e-7.
    /// </remarks>
    public int Delay => s.FrameLen * 2;

    public int SampleRate => s.SampleRate;

    public int Channels => s.Channels;

    /// <summary>
    /// <c>wma_decode_init</c>.
    /// </summary>
    /// <param name="version">1 for WMAV1 (0x160), 2 for WMAV2 (0x161).</param>
    /// <param name="extradata">The codec's private data, as the container stores it.</param>
    public bool Initialize(int version, int sampleRate, int channels, long bitRate,
                           int blockAlign, ReadOnlySpan<byte> extradata)
    {
        if (blockAlign <= 0)
            return false;

        s.Version = version;
        s.SampleRate = sampleRate;
        s.Channels = channels;
        s.BitRate = bitRate;
        s.BlockAlign = blockAlign;

        // Extract the flag info. WMAV1 keeps flags2 at offset 2 of a four-byte extradata, WMAV2
        // at offset 4 of a six-byte one.
        int flags2 = 0;
        if (version == 1 && extradata.Length >= 4)
            flags2 = extradata[2] | (extradata[3] << 8);
        else if (version == 2 && extradata.Length >= 6)
            flags2 = extradata[4] | (extradata[5] << 8);

        s.UseExpVlc = (flags2 & 0x0001) != 0;
        s.UseBitReservoir = (flags2 & 0x0002) != 0;
        s.UseVariableBlockLen = (flags2 & 0x0004) != 0;

        if (version == 2 && extradata.Length >= 8)
        {
            // The C's comment: "Disabling use_variable_block_len, if this fails contact the ffmpeg
            // developers and send us the file" - it fixes their issue 1503.
            if ((extradata[4] | (extradata[5] << 8)) == 0xd && s.UseVariableBlockLen)
                s.UseVariableBlockLen = false;
        }

        for (int i = 0; i < WmaLimits.MaxChannels; i++)
            s.MaxExponent[i] = 1.0f;

        if (!Wma.Init(s, flags2))
            return false;

        Wma.InitDecoderVlcs(s);

        if (!s.UseExpVlc)
            LspToCurveInit(s.FrameLen);

        Samples = new float[channels][];
        for (int ch = 0; ch < channels; ch++)
            Samples[ch] = new float[s.FrameLen * 16];

        return true;
    }

    /// <summary>
    /// <c>pow_m1_4</c>: x^-0.25, from an exponent table and a linearly interpolated mantissa
    /// table.
    /// </summary>
    /// <remarks>
    /// The C reads the float's bits through a union to split it into exponent and mantissa. The
    /// interpolation scale <c>t</c> is the mantissa with an exponent of 127 forced in, which puts
    /// it in [1, 2).
    /// </remarks>
    private float PowM14(float x)
    {
        uint v = BitConverter.SingleToUInt32Bits(x);
        uint e = v >> 23;
        uint m = (v >> (23 - WmaLimits.LspPowBits)) & ((1u << WmaLimits.LspPowBits) - 1);

        float t = BitConverter.UInt32BitsToSingle(
            ((v << WmaLimits.LspPowBits) & ((1u << 23) - 1)) | (127u << 23));

        float a = s.LspPowMTable1[m];
        float b = s.LspPowMTable2[m];
        return s.LspPowETable[e] * (a + b * t);
    }

    /// <summary><c>wma_lsp_to_curve_init</c>.</summary>
    private void LspToCurveInit(int frameLen)
    {
        double wdel = Math.PI / frameLen;
        for (int i = 0; i < frameLen; i++)
            s.LspCosTable[i] = (float)(2.0f * Math.Cos(wdel * i));

        // Tables for the x^-0.25 computation.
        for (int i = 0; i < 256; i++)
        {
            int e = i - 126;
            s.LspPowETable[i] = (float)Math.Pow(2.0, e * -0.25);
        }

        // The C's note: these two tables are needed to avoid two operations in pow_m1_4.
        double b = 1.0;
        for (int i = (1 << WmaLimits.LspPowBits) - 1; i >= 0; i--)
        {
            int m = (1 << WmaLimits.LspPowBits) + i;
            double a = m * (0.5 / (1 << WmaLimits.LspPowBits));
            a = 1 / Math.Sqrt(Math.Sqrt(a));
            s.LspPowMTable1[i] = (float)(2 * a - b);
            s.LspPowMTable2[i] = (float)(b - a);
            b = a;
        }
    }

    /// <summary>
    /// <c>wma_lsp_to_curve</c>. The C's note: "We use the same code as Vorbis here".
    /// </summary>
    private void LspToCurve(Span<float> output, out float valMaxOut, int n, ReadOnlySpan<float> lsp)
    {
        float valMax = 0;
        for (int i = 0; i < n; i++)
        {
            float p = 0.5f;
            float q = 0.5f;
            float w = s.LspCosTable[i];

            for (int j = 1; j < WmaLimits.NbLspCoefs; j += 2)
            {
                q *= w - lsp[j - 1];
                p *= w - lsp[j];
            }

            p *= p * (2.0f - w);
            q *= q * (2.0f + w);
            float v = p + q;
            v = PowM14(v);
            if (v > valMax)
                valMax = v;
            output[i] = v;
        }
        valMaxOut = valMax;
    }

    /// <summary>
    /// <c>decode_exp_lsp</c>: exponents coded as LSP coefficients, as Vorbis does.
    /// </summary>
    private void DecodeExpLsp(int ch)
    {
        Span<float> lspCoefs = stackalloc float[WmaLimits.NbLspCoefs];

        for (int i = 0; i < WmaLimits.NbLspCoefs; i++)
        {
            int val = (int)gb.GetBits(i == 0 || i >= 8 ? 3 : 4);
            lspCoefs[i] = WmaTables.LspCodebook[i * WmaTables.LspCodebookStride + val];
        }

        LspToCurve(s.Exponents[ch], out float max, s.BlockLen, lspCoefs);
        s.MaxExponent[ch] = max;
    }

    /// <summary>
    /// <c>decode_exp_vlc</c>: exponents as a VLC-coded delta chain over <c>pow_tab</c>.
    /// </summary>
    /// <remarks>
    /// The C writes through a <c>uint32_t *</c> aliased over both the exponent array and
    /// <c>pow_tab</c>, which copies the float's bits; writing the float is the same thing. Its run
    /// writer is a Duff's device that writes exactly <c>n</c> values, so this is a plain loop.
    /// </remarks>
    private bool DecodeExpVlc(int ch)
    {
        const int powTabOffset = 60;    // pow_tab + 60, so index 0 is 10^(-60/16)
        ReadOnlySpan<float> powTab = WmaTables.PowTab;

        int bandRow = s.FrameLenBits - s.BlockLenBits;
        int bandIndex = 0;
        float[] exponents = s.Exponents[ch];
        int q = 0;
        int qEnd = s.BlockLen;
        float maxScale = 0;
        int lastExp;

        if (s.Version == 1)
        {
            lastExp = (int)gb.GetBits(5) + 10;
            if ((uint)(lastExp + powTabOffset) >= powTab.Length)
                return false;
            float v1 = powTab[lastExp + powTabOffset];
            maxScale = v1;

            int n1 = s.ExponentBands[bandRow, bandIndex++];
            for (int i = 0; i < RunLength(n1) && q < qEnd; i++)
                exponents[q++] = v1;
        }
        else
        {
            lastExp = 36;
        }

        while (q < qEnd)
        {
            // No check for an invalid code here, deliberately: the C has none either, and the
            // range test below is what catches it. get_vlc2 returns -1 for a code that is not in
            // the table, which takes last_exp to 36 - 61 = -25, and -25 + 60 is still inside
            // pow_tab, so the C accepts it and carries on. Returning an error instead would
            // decode a damaged stream differently from the reference.
            int code = gb.GetVlc2(s.ExpVlc, WmaLimits.ExpVlcBits, WmaLimits.ExpMax);

            // The C's note: this offset is the same as MPEG-4 AAC.
            lastExp += code - 60;
            if ((uint)(lastExp + powTabOffset) >= powTab.Length)
                return false;   // exponent out of range

            float v = powTab[lastExp + powTabOffset];
            if (v > maxScale)
                maxScale = v;

            // The band index is bounded here where the C relies on the bands summing to the
            // block length; a stream whose bands do not would read past the array.
            if (bandIndex >= 25)
                return false;

            int n = s.ExponentBands[bandRow, bandIndex++];
            for (int i = 0; i < RunLength(n) && q < qEnd; i++)
                exponents[q++] = v;
        }

        s.MaxExponent[ch] = maxScale;
        return true;
    }

    /// <summary>
    /// How many exponents the C's run writer writes for a band of length <paramref name="n"/>.
    /// </summary>
    /// <remarks>
    /// The C writes the run with a Duff's device:
    /// <code>
    /// switch (n &amp; 3) do {
    /// case 0: *q++ = iv;
    /// case 3: *q++ = iv;
    /// case 2: *q++ = iv;
    /// case 1: *q++ = iv;
    /// } while ((n -= 4) &gt; 0);
    /// </code>
    /// which writes exactly <c>n</c> values for every positive <c>n</c> - and <b>four</b> for
    /// <c>n == 0</c>, because the entry point for <c>n &amp; 3 == 0</c> is the top of the body and
    /// the body always runs once. A plain <c>for (i = 0; i &lt; n; i++)</c> writes none, and then
    /// the exponent loop never advances and spins through the band table instead. A zero-length
    /// band is reachable: WMA v1 derives its bands by mapping critical frequencies onto the
    /// block, and two of them can land on the same position in a short block.
    /// </remarks>
    private static int RunLength(int n) => n > 0 ? n : 4;

    /// <summary>
    /// <c>wma_window</c>: window the IMDCT output and add it into the frame.
    /// </summary>
    /// <remarks>
    /// The C's comment: "We ensure that when the windows overlap their squared sum is always 1
    /// (MDCT reconstruction rule)." The two halves are asymmetric because a block may be longer
    /// than its neighbour, in which case only the overlapping middle is windowed and the rest is
    /// copied or zeroed.
    /// </remarks>
    private void Window(float[] frameOut, int outOffset)
    {
        float[] inBuffer = s.Output;
        int inOffset = 0;
        int blockLen, bsize, n;

        // Left part.
        if (s.BlockLenBits <= s.PrevBlockLenBits)
        {
            blockLen = s.BlockLen;
            bsize = s.FrameLenBits - s.BlockLenBits;
            float[] window = s.Windows[bsize]!;

            // vector_fmul_add(out, in, window, out, len): out[i] = in[i] * window[i] + out[i]
            for (int i = 0; i < blockLen; i++)
                frameOut[outOffset + i] = inBuffer[inOffset + i] * window[i] + frameOut[outOffset + i];
        }
        else
        {
            blockLen = 1 << s.PrevBlockLenBits;
            n = (s.BlockLen - blockLen) / 2;
            bsize = s.FrameLenBits - s.PrevBlockLenBits;
            float[] window = s.Windows[bsize]!;

            for (int i = 0; i < blockLen; i++)
                frameOut[outOffset + n + i] =
                    inBuffer[inOffset + n + i] * window[i] + frameOut[outOffset + n + i];

            Array.Copy(inBuffer, inOffset + n + blockLen, frameOut, outOffset + n + blockLen, n);
        }

        outOffset += s.BlockLen;
        inOffset += s.BlockLen;

        // Right part.
        if (s.BlockLenBits <= s.NextBlockLenBits)
        {
            blockLen = s.BlockLen;
            bsize = s.FrameLenBits - s.BlockLenBits;
            float[] window = s.Windows[bsize]!;

            // vector_fmul_reverse(out, in, window, len): out[i] = in[i] * window[len - 1 - i].
            // The window runs backwards and the source forwards, not the other way round.
            for (int i = 0; i < blockLen; i++)
                frameOut[outOffset + i] = inBuffer[inOffset + i] * window[blockLen - 1 - i];
        }
        else
        {
            blockLen = 1 << s.NextBlockLenBits;
            n = (s.BlockLen - blockLen) / 2;
            bsize = s.FrameLenBits - s.NextBlockLenBits;
            float[] window = s.Windows[bsize]!;

            Array.Copy(inBuffer, inOffset, frameOut, outOffset, n);

            for (int i = 0; i < blockLen; i++)
                frameOut[outOffset + n + i] = inBuffer[inOffset + n + i] * window[blockLen - 1 - i];

            Array.Clear(frameOut, outOffset + n + blockLen, n);
        }
    }

    /// <summary>
    /// <c>wma_decode_block</c>.
    /// </summary>
    /// <returns>0 if OK, 1 if this was the last block of the frame, -1 on a broken stream.</returns>
    private int DecodeBlock()
    {
        int channels = s.Channels;
        Span<int> nbCoefs = stackalloc int[WmaLimits.MaxChannels];
        int n, v, a;

        // Compute the current block length.
        if (s.UseVariableBlockLen)
        {
            n = Log2(s.NbBlockSizes - 1) + 1;

            if (s.ResetBlockLengths)
            {
                s.ResetBlockLengths = false;
                v = (int)gb.GetBits(n);
                if (v >= s.NbBlockSizes)
                {
                    LastError = "prev_block_len_bits " + (s.FrameLenBits - v) + " out of range";
                    return -1;
                }
                s.PrevBlockLenBits = s.FrameLenBits - v;

                v = (int)gb.GetBits(n);
                if (v >= s.NbBlockSizes)
                {
                    LastError = "block_len_bits " + (s.FrameLenBits - v) + " out of range";
                    return -1;
                }
                s.BlockLenBits = s.FrameLenBits - v;
            }
            else
            {
                s.PrevBlockLenBits = s.BlockLenBits;
                s.BlockLenBits = s.NextBlockLenBits;
            }

            v = (int)gb.GetBits(n);
            if (v >= s.NbBlockSizes)
            {
                LastError = "next_block_len_bits " + (s.FrameLenBits - v) + " out of range";
                return -1;
            }
            s.NextBlockLenBits = s.FrameLenBits - v;
        }
        else
        {
            s.NextBlockLenBits = s.FrameLenBits;
            s.PrevBlockLenBits = s.FrameLenBits;
            s.BlockLenBits = s.FrameLenBits;
        }

        if (s.FrameLenBits - s.BlockLenBits >= s.NbBlockSizes)
        {
            LastError = "block_len_bits not initialised to a valid value";
            return -1;
        }

        // Is the block length coherent with the frame length?
        s.BlockLen = 1 << s.BlockLenBits;
        if (s.BlockPos + s.BlockLen > s.FrameLen)
        {
            LastError = "a block of " + s.BlockLen + " at " + s.BlockPos + " overflows the "
                      + s.FrameLen + "-sample frame";
            return -1;
        }

        if (channels == 2)
            s.MsStereo = gb.GetBits1() != 0;

        v = 0;
        for (int ch = 0; ch < channels; ch++)
        {
            a = (int)gb.GetBits1();
            s.ChannelCoded[ch] = a != 0;
            v |= a;
        }

        int bsize = s.FrameLenBits - s.BlockLenBits;
        int totalGain = 1;
        int coefNbBits = 0;

        // If no channel is coded there is nothing to read; the C's comment here is
        // "XXX: fix potential framing problems".
        if (v != 0)
        {
            // Total gain, and from it the number of bits an escaped coefficient uses.
            while (true)
            {
                if (gb.GetBitsLeft() < 7)
                {
                    LastError = "total_gain overread";
                    return -1;
                }
                a = (int)gb.GetBits(7);
                totalGain += a;
                if (a != 127)
                    break;
            }

            coefNbBits = Wma.TotalGainToBits(totalGain);

            n = s.CoefsEnd[bsize] - s.CoefsStart;
            for (int ch = 0; ch < channels; ch++)
                nbCoefs[ch] = n;

            // Complex coding: which high bands are noise rather than coefficients.
            if (s.UseNoiseCoding)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    if (!s.ChannelCoded[ch])
                        continue;
                    int highCount = s.ExponentHighSizes[bsize];
                    for (int i = 0; i < highCount; i++)
                    {
                        uint coded = gb.GetBits1();
                        s.HighBandCoded[ch, i] = (int)coded;
                        // If noise coded, the coefficients are not transmitted.
                        if (coded != 0)
                            nbCoefs[ch] -= s.ExponentHighBands[bsize, i];
                    }
                }

                for (int ch = 0; ch < channels; ch++)
                {
                    if (!s.ChannelCoded[ch])
                        continue;
                    int highCount = s.ExponentHighSizes[bsize];
                    int val = unchecked((int)0x80000000);
                    for (int i = 0; i < highCount; i++)
                    {
                        if (s.HighBandCoded[ch, i] == 0)
                            continue;
                        if (val == unchecked((int)0x80000000))
                        {
                            val = (int)gb.GetBits(7) - 19;
                        }
                        else
                        {
                            int code = gb.GetVlc2(s.HGainVlc, WmaLimits.HGainVlcBits,
                                                  WmaLimits.HGainMax);
                            // ff_vlc_init_from_lengths was given an offset of -18, so the symbol
                            // the C reads is already shifted down by 18.
                            val += code - 18;
                        }
                        s.HighBandValues[ch, i] = val;
                    }
                }
            }

            // Exponents can be reused in short blocks.
            if (s.BlockLenBits == s.FrameLenBits || gb.GetBits1() != 0)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    if (!s.ChannelCoded[ch])
                        continue;
                    if (s.UseExpVlc)
                    {
                        if (!DecodeExpVlc(ch))
                        {
                            LastError ??= "channel " + ch + " exponents did not decode";
                            return -1;
                        }
                    }
                    else
                    {
                        DecodeExpLsp(ch);
                    }
                    s.ExponentsBsize[ch] = bsize;
                    s.ExponentsInitialized[ch] = true;
                }
            }

            for (int ch = 0; ch < channels; ch++)
            {
                if (s.ChannelCoded[ch] && !s.ExponentsInitialized[ch])
                {
                    LastError = "channel " + ch + " is coded but has no exponents yet";
                    return -1;
                }
            }

            // The spectral coefficients: run-length encoding and nothing else.
            for (int ch = 0; ch < channels; ch++)
            {
                if (s.ChannelCoded[ch])
                {
                    // Special VLC tables are used for mid/side stereo, because there is
                    // potentially less energy there.
                    int tindex = ch == 1 && s.MsStereo ? 1 : 0;
                    Array.Clear(s.Coefs1[ch], 0, s.BlockLen);

                    if (!Wma.RunLevelDecode(ref gb, s.CoefVlc[tindex], s.LevelTable[tindex],
                                            s.RunTable[tindex], 0, s.Coefs1[ch], 0, nbCoefs[ch],
                                            s.BlockLen, s.FrameLenBits, coefNbBits))
                    {
                        LastError = "channel " + ch + " coefficients overran the block";
                        return -1;
                    }
                }
                if (s.Version == 1 && channels >= 2)
                    gb.AlignGetBits();
            }

            // Normalise.
            int n4 = s.BlockLen / 2;
            float mdctNorm = (float)(1.0 / n4);
            if (s.Version == 1)
                mdctNorm = (float)(mdctNorm * Math.Sqrt(n4));

            // And finally the MDCT coefficients.
            for (int ch = 0; ch < channels; ch++)
            {
                if (!s.ChannelCoded[ch])
                    continue;

                float[] coefs1 = s.Coefs1[ch];
                int coefs1Index = 0;
                float[] exponentArray = s.Exponents[ch];
                int esize = s.ExponentsBsize[ch];
                float mult = (float)(Math.Pow(10.0, totalGain * 0.05) / s.MaxExponent[ch]);
                mult *= mdctNorm;
                float[] coefs = s.Coefs[ch];
                int coefsIndex = 0;

                if (s.UseNoiseCoding)
                {
                    float mult1 = mult;

                    // Very low frequencies: noise.
                    for (int i = 0; i < s.CoefsStart; i++)
                    {
                        coefs[coefsIndex++] = s.NoiseTable[s.NoiseIndex] *
                                              exponentArray[(i << bsize) >> esize] * mult1;
                        s.NoiseIndex = (s.NoiseIndex + 1) & (WmaLimits.NoiseTabSize - 1);
                    }

                    int n1 = s.ExponentHighSizes[bsize];

                    // The power of each coded high band.
                    Span<float> expPower = stackalloc float[WmaLimits.HighBandMaxSize];
                    int expOffset = (s.HighBandStart[bsize] << bsize) >> esize;
                    int lastHighBand = 0;   // the C's comment: avoid warning

                    for (int j = 0; j < n1; j++)
                    {
                        n = s.ExponentHighBands[s.FrameLenBits - s.BlockLenBits, j];
                        if (s.HighBandCoded[ch, j] != 0)
                        {
                            float e2 = 0;
                            for (int i = 0; i < n; i++)
                            {
                                float value = exponentArray[expOffset + ((i << bsize) >> esize)];
                                e2 += value * value;
                            }
                            expPower[j] = e2 / n;
                            lastHighBand = j;
                        }
                        expOffset += (n << bsize) >> esize;
                    }

                    // Main frequencies and high frequencies.
                    expOffset = (s.CoefsStart << bsize) >> esize;
                    for (int j = -1; j < n1; j++)
                    {
                        n = j < 0
                            ? s.HighBandStart[bsize] - s.CoefsStart
                            : s.ExponentHighBands[s.FrameLenBits - s.BlockLenBits, j];

                        if (j >= 0 && s.HighBandCoded[ch, j] != 0)
                        {
                            // Noise at the power this band asked for. Each assignment below
                            // rounds to float, as the C's do.
                            mult1 = (float)Math.Sqrt(expPower[j] / expPower[lastHighBand]);
                            // The C's comment here: XXX: use a table.
                            mult1 = (float)(mult1 * Math.Pow(10.0, s.HighBandValues[ch, j] * 0.05));
                            mult1 /= s.MaxExponent[ch] * s.NoiseMult;
                            mult1 *= mdctNorm;

                            for (int i = 0; i < n; i++)
                            {
                                float noise = s.NoiseTable[s.NoiseIndex];
                                s.NoiseIndex = (s.NoiseIndex + 1) & (WmaLimits.NoiseTabSize - 1);
                                coefs[coefsIndex++] =
                                    noise * exponentArray[expOffset + ((i << bsize) >> esize)] * mult1;
                            }
                        }
                        else
                        {
                            // Coded values, plus a little noise.
                            for (int i = 0; i < n; i++)
                            {
                                float noise = s.NoiseTable[s.NoiseIndex];
                                s.NoiseIndex = (s.NoiseIndex + 1) & (WmaLimits.NoiseTabSize - 1);
                                coefs[coefsIndex++] = (coefs1[coefs1Index++] + noise) *
                                                      exponentArray[expOffset + ((i << bsize) >> esize)] *
                                                      mult;
                            }
                        }
                        expOffset += (n << bsize) >> esize;
                    }

                    // Very high frequencies: noise. The exponent index here is *negative*
                    // relative to where the band walk left off, which is why the offset is
                    // tracked rather than the pointer.
                    n = s.BlockLen - s.CoefsEnd[bsize];
                    mult1 = mult * exponentArray[expOffset + ((-(1 << bsize)) >> esize)];
                    for (int i = 0; i < n; i++)
                    {
                        coefs[coefsIndex++] = s.NoiseTable[s.NoiseIndex] * mult1;
                        s.NoiseIndex = (s.NoiseIndex + 1) & (WmaLimits.NoiseTabSize - 1);
                    }
                }
                else
                {
                    // The C's comment: XXX: optimize more.
                    for (int i = 0; i < s.CoefsStart; i++)
                        coefs[coefsIndex++] = 0.0f;

                    n = nbCoefs[ch];
                    for (int i = 0; i < n; i++)
                        coefs[coefsIndex++] = coefs1[i] * exponentArray[(i << bsize) >> esize] * mult;

                    n = s.BlockLen - s.CoefsEnd[bsize];
                    for (int i = 0; i < n; i++)
                        coefs[coefsIndex++] = 0.0f;
                }
            }

            if (s.MsStereo && s.ChannelCoded[1])
            {
                // The nominal mid/side case, done before the MDCT. The C's comment: "no need to
                // optimize this case because it should almost never happen".
                if (!s.ChannelCoded[0])
                {
                    Array.Clear(s.Coefs[0], 0, s.BlockLen);
                    s.ChannelCoded[0] = true;
                }

                // butterflies_float(v1, v2, len): v2 becomes the old v1 - v2, v1 becomes v1 + v2.
                float[] v1 = s.Coefs[0];
                float[] v2 = s.Coefs[1];
                for (int i = 0; i < s.BlockLen; i++)
                {
                    float t = v1[i] - v2[i];
                    v1[i] += v2[i];
                    v2[i] = t;
                }
            }
        }

        // next: in the C - reached either normally or by the "no channel coded" jump.
        var mdct = s.Mdct[bsize]!;

        for (int ch = 0; ch < channels; ch++)
        {
            int n4 = s.BlockLen / 2;
            if (s.ChannelCoded[ch])
                mdct.Transform(s.Output, s.Coefs[ch]);
            else if (!(s.MsStereo && ch == 1))
                Array.Clear(s.Output);

            // Multiply by the window and add into the frame.
            int index = (s.FrameLen / 2) + s.BlockPos - n4;
            Window(s.FrameOut[ch], index);
        }

        s.BlockNum++;
        s.BlockPos += s.BlockLen;
        return s.BlockPos >= s.FrameLen ? 1 : 0;
    }

    /// <summary><c>wma_decode_frame</c>: one frame of <c>frame_len</c> samples.</summary>
    private bool DecodeFrame(int samplesOffset)
    {
        s.BlockNum = 0;
        s.BlockPos = 0;

        while (true)
        {
            int ret = DecodeBlock();
            if (ret < 0)
                return false;
            if (ret != 0)
                break;
        }

        for (int ch = 0; ch < s.Channels; ch++)
        {
            Array.Copy(s.FrameOut[ch], 0, Samples[ch], samplesOffset, s.FrameLen);

            // Prepare for the next block. The C moves the tail down and leaves what was there
            // behind it: the window's right half assigns rather than adds, so it overwrites that
            // region before it is read. Clearing it would be tidier and would not match.
            Array.Copy(s.FrameOut[ch], s.FrameLen, s.FrameOut[ch], 0, s.FrameLen);
        }

        return true;
    }

    /// <summary>
    /// <c>wma_decode_superframe</c>: one packet, which may carry several frames.
    /// </summary>
    /// <remarks>
    /// With the bit reservoir a frame can straddle two packets, so part of a packet is kept in
    /// <c>last_superframe</c> and prepended to the next one. That is the only stateful part of
    /// the format and the reason a WMA packet cannot be decoded on its own.
    /// </remarks>
    public bool DecodeSuperframe(ReadOnlySpan<byte> packet, out int samplesDecoded)
    {
        samplesDecoded = 0;
        SampleCount = 0;
        LastError = null;

        if (packet.Length == 0)
        {
            // End of stream: hand back what the last frame's overlap left behind.
            if (s.EofDone)
                return true;

            for (int ch = 0; ch < s.Channels; ch++)
                Array.Copy(s.FrameOut[ch], 0, Samples[ch], 0, s.FrameLen);

            s.LastSuperframeLen = 0;
            s.EofDone = true;
            samplesDecoded = s.FrameLen;
            SampleCount = s.FrameLen;
            return true;
        }

        if (packet.Length < s.BlockAlign)
        {
            LastError = "packet of " + packet.Length + " bytes is shorter than block_align "
                      + s.BlockAlign;
            return false;
        }

        // The packet is copied into a buffer this decoder owns, because the bit reader holds an
        // array rather than a span and the C's reader reads past the end of the data by up to
        // AV_INPUT_BUFFER_PADDING_SIZE. The buffer is kept and reused: allocating it per packet
        // cost 728 bytes a packet on every stream, which is a per-frame allocation on a playback
        // path and the one thing this project treats as a defect by itself.
        int bufSize = s.BlockAlign;
        if (packetBuffer.Length < bufSize + PacketPadding)
            packetBuffer = new byte[bufSize + PacketPadding];
        byte[] buffer = packetBuffer;
        packet[..bufSize].CopyTo(buffer);
        Array.Clear(buffer, bufSize, buffer.Length - bufSize);

        gb = default;
        gb.InitGetBits8(buffer, bufSize);

        int nbFrames;
        if (s.UseBitReservoir)
        {
            // The superframe header.
            gb.SkipBits(4);     // super frame index
            nbFrames = (int)gb.GetBits(4) - (s.LastSuperframeLen <= 0 ? 1 : 0);
            if (nbFrames <= 0)
            {
                bool isError = nbFrames < 0 || gb.GetBitsLeft() <= 8;
                if (isError)
                {
                    LastError = "nb_frames is " + nbFrames + " with " + gb.GetBitsLeft()
                              + " bits left";
                    return false;
                }

                if (s.LastSuperframeLen + bufSize - 1 > WmaLimits.MaxCodedSuperframeSize)
                {
                    s.LastSuperframeLen = 0;
                    return false;
                }

                int at = s.LastSuperframeLen;
                for (int len = bufSize - 1; len > 0; len--)
                    s.LastSuperframe[at++] = (byte)gb.GetBits(8);
                Array.Clear(s.LastSuperframe, at, Math.Min(64, s.LastSuperframe.Length - at));

                s.LastSuperframeLen += 8 * bufSize - 8;
                return true;        // no frame this packet
            }
        }
        else
        {
            nbFrames = 1;
        }

        int wanted = nbFrames * s.FrameLen;
        if (Samples.Length > 0 && Samples[0].Length < wanted)
        {
            for (int ch = 0; ch < s.Channels; ch++)
                Samples[ch] = new float[wanted];
        }

        int samplesOffset = 0;

        if (s.UseBitReservoir)
        {
            int bitOffset = (int)gb.GetBits(s.ByteOffsetBits + 3);
            if (bitOffset > gb.GetBitsLeft())
            {
                LastError = "last frame bit offset " + bitOffset + " exceeds the "
                          + gb.GetBitsLeft() + " bits left in a " + bufSize + "-byte packet";
                s.LastSuperframeLen = 0;
                return false;
            }

            if (s.LastSuperframeLen > 0)
            {
                // Add bit_offset bits to the last frame.
                if (s.LastSuperframeLen + ((bitOffset + 7) >> 3) > WmaLimits.MaxCodedSuperframeSize)
                {
                    s.LastSuperframeLen = 0;
                    return false;
                }

                int at = s.LastSuperframeLen;
                int len = bitOffset;
                while (len > 7)
                {
                    s.LastSuperframe[at++] = (byte)gb.GetBits(8);
                    len -= 8;
                }
                if (len > 0)
                    s.LastSuperframe[at++] = (byte)(gb.GetBits(len) << (8 - len));
                Array.Clear(s.LastSuperframe, at, Math.Min(64, s.LastSuperframe.Length - at));

                // The frame that straddles the two packets.
                var saved = new GetBitContext();
                saved.InitGetBits8(s.LastSuperframe, (s.LastSuperframeLen * 8 + bitOffset + 7) / 8);
                saved.SizeInBits = s.LastSuperframeLen * 8 + bitOffset;
                if (s.LastBitoffset > 0)
                    saved.SkipBits(s.LastBitoffset);

                GetBitContext outer = gb;
                gb = saved;
                bool ok = DecodeFrame(samplesOffset);
                gb = outer;
                if (!ok)
                {
                    s.LastSuperframeLen = 0;
                    return false;
                }

                samplesOffset += s.FrameLen;
                nbFrames--;
            }

            // Each frame from bit_offset on.
            int pos = bitOffset + 4 + 4 + s.ByteOffsetBits + 3;
            if (pos >= WmaLimits.MaxCodedSuperframeSize * 8 || pos > bufSize * 8)
            {
                LastError = "frame start " + pos + " bits is past the end of a " + bufSize
                          + "-byte packet";
                return false;
            }

            gb = default;
            gb.InitGetBits8(buffer, pos >> 3, bufSize - (pos >> 3));
            int skew = pos & 7;
            if (skew > 0)
                gb.SkipBits(skew);

            s.ResetBlockLengths = true;
            for (int i = 0; i < nbFrames; i++)
            {
                if (!DecodeFrame(samplesOffset))
                {
                    LastError ??= "frame " + i + " of " + nbFrames + " did not decode";
                    s.LastSuperframeLen = 0;
                    return false;
                }
                samplesOffset += s.FrameLen;
            }

            // Keep the end of the packet for the next one.
            pos = gb.GetBitsCount() + ((bitOffset + 4 + 4 + s.ByteOffsetBits + 3) & ~7);
            s.LastBitoffset = pos & 7;
            pos >>= 3;
            int tail = bufSize - pos;
            if (tail > WmaLimits.MaxCodedSuperframeSize || tail < 0)
            {
                s.LastSuperframeLen = 0;
                return false;
            }
            s.LastSuperframeLen = tail;
            Array.Copy(buffer, pos, s.LastSuperframe, 0, tail);
        }
        else
        {
            if (!DecodeFrame(samplesOffset))
            {
                LastError ??= "the frame did not decode";
                s.LastSuperframeLen = 0;
                return false;
            }
            samplesOffset += s.FrameLen;
        }

        samplesDecoded = samplesOffset;
        SampleCount = samplesOffset;
        return true;
    }

    /// <summary><c>flush</c>.</summary>
    public void Flush()
    {
        s.LastBitoffset = 0;
        s.LastSuperframeLen = 0;
        s.EofDone = false;
        for (int ch = 0; ch < s.Channels; ch++)
            Array.Clear(s.FrameOut[ch]);
    }

    /// <summary><c>av_log2</c>.</summary>
    private static int Log2(int value) =>
        value <= 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)value);
}

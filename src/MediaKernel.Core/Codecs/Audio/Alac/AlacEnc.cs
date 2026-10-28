// ALAC (Apple Lossless) encoder - a translation of ffmpeg-master/libavcodec/alacenc.c.
//
// WHY THIS IS A TRANSLATION AND NOT A DESIGN. ALAC is lossless, so "it sounds right" says
// nothing: a file that decodes back to the input samples can still differ byte for byte from
// what ffmpeg writes, and the only way to know which of two encoders is wrong is to be the same
// one. FFmpeg's native encoder is what `ffmpeg -c:a alac` runs, so the gate has an exact oracle
// and byte equality is the whole bar.
//
// Everything this file leans on was translated and gated earlier, which is why it is short:
//   PutBitContext              from the flacenc port, gated byte-exact
//   Lpc.CalcCoefs / Init / End from the flacenc port, Levinson with ORDER_METHOD_EST
//   AlacData.ChannelLayoutOffsets and ChannelElements, from alac_data.c
//
// TWO PLACES WHERE A C# HABIT WOULD CHANGE THE OUTPUT, both recorded in the port checklist:
//
//  1. AlacLinearPredictor takes a BY-VALUE COPY of the channel's LPC context and adapts the
//     coefficients in that copy as it walks the frame. The copy is thrown away at the end of the
//     frame, and the coefficients written to the bitstream are the UNADAPTED ones - WriteElement
//     writes them before calling the predictor at all. The decoder runs the same adaptation from
//     the same starting point, which is what makes this lossless. Making AlacLpcContext a class
//     would quietly change every frame.
//
//  2. The entropy coder's history is an unsigned int whose update is allowed to wrap, and
//     EstimateStereoMode accumulates absolute second-order residuals that are themselves allowed
//     to overflow int32. Both are written `unchecked` on purpose.

using System;
using System.Runtime.CompilerServices;
using MediaKernel.Core.Codecs.Audio.Flac;

namespace MediaKernel.Core.Codecs.Audio.Alac;

/// <summary>What the encoder is asked for, in place of the C's <c>AVOption</c> table.</summary>
public sealed class AlacEncoderOptions
{
    /// <summary><c>compression_level</c>, 0 to 2. 0 is verbatim - no prediction at all.</summary>
    public int CompressionLevel { get; set; } = 2;

    /// <summary><c>min_prediction_order</c>. <c>DEFAULT_MIN_PRED_ORDER</c>.</summary>
    public int MinPredictionOrder { get; set; } = AlacEnc.DefaultMinPredOrder;

    /// <summary><c>max_prediction_order</c>. <c>DEFAULT_MAX_PRED_ORDER</c>.</summary>
    public int MaxPredictionOrder { get; set; } = AlacEnc.DefaultMaxPredOrder;

    /// <summary>
    /// <c>bits_per_raw_sample</c>. 16 for S16 input, 24 for S32 input - the C allows no other
    /// value and warns before overriding one.
    /// </summary>
    public int BitsPerRawSample { get; set; } = 16;
}

/// <summary><c>alacenc.c</c>.</summary>
public sealed class AlacEnc
{
    /// <summary><c>DEFAULT_FRAME_SIZE</c>.</summary>
    public const int DefaultFrameSize = 4096;

    /// <summary><c>ALAC_EXTRADATA_SIZE</c>.</summary>
    public const int AlacExtradataSize = 36;

    /// <summary><c>ALAC_ESCAPE_CODE</c>.</summary>
    private const int AlacEscapeCode = 0x1FF;

    /// <summary><c>ALAC_MAX_LPC_ORDER</c>. Not <c>MAX_LPC_ORDER</c>, which is 32.</summary>
    private const int AlacMaxLpcOrder = 30;

    /// <summary><c>DEFAULT_MAX_PRED_ORDER</c>.</summary>
    public const int DefaultMaxPredOrder = 6;

    /// <summary><c>DEFAULT_MIN_PRED_ORDER</c>.</summary>
    public const int DefaultMinPredOrder = 4;

    /// <summary><c>ALAC_MAX_LPC_PRECISION</c>.</summary>
    private const int AlacMaxLpcPrecision = 9;

    /// <summary><c>ALAC_MIN_LPC_SHIFT</c>.</summary>
    private const int AlacMinLpcShift = 0;

    /// <summary><c>ALAC_MAX_LPC_SHIFT</c>.</summary>
    private const int AlacMaxLpcShift = 9;

    private const int AlacChmodeLeftRight = 0;
    private const int AlacChmodeLeftSide = 1;
    private const int AlacChmodeRightSide = 2;

    /// <summary><c>RiceContext</c>.</summary>
    private struct RiceContext
    {
        public int HistoryMult;
        public int InitialHistory;
        public int KModifier;
        public int RiceModifier;
    }

    /// <summary>
    /// <c>AlacLPCContext</c>. A STRUCT, because the C copies it by value and relies on that -
    /// see the note at the top of this file.
    /// </summary>
    private struct AlacLpcContext
    {
        public int LpcOrder;
        public LpcCoeffs LpcCoeff;
        public int LpcQuant;

        /// <summary><c>int lpc_coeff[ALAC_MAX_LPC_ORDER+1]</c>, inline so the copy is a copy.</summary>
        [InlineArray(AlacMaxLpcOrder + 1)]
        public struct LpcCoeffs
        {
            private int element0;
        }
    }

    // ── AlacEncodeContext, in the C's field order ───────────────────────────
    private int frameSize;
    private int verbatim;
    private int compressionLevel;
    private readonly int minPredictionOrder;
    private readonly int maxPredictionOrder;
    private int maxCodedFrameSize;
    private int writeSampleSize;
    private int extraBits;
    private readonly int[][] sampleBuf = [new int[DefaultFrameSize], new int[DefaultFrameSize]];
    private readonly int[][] predictorBuf = [new int[DefaultFrameSize], new int[DefaultFrameSize]];
    private int interlacingShift;
    private int interlacingLeftweight;
    private readonly PutBitContext pbctx;
    private RiceContext rc;
    private readonly AlacLpcContext[] lpc = new AlacLpcContext[2];
    private readonly Lpc lpcCtx = new();

    // Not in the C's context: avctx's fields, which this tree passes in rather than reaching for.
    private readonly int channels;
    private readonly int sampleRate;
    private readonly int bitsPerRawSample;
    private readonly bool sampleFmtIsS32;

    // calc_predictor_params' scratch. The C declares these on the stack per call; they are 4 KB
    // and this is a per-frame path, so they are fields here. Same contents, same lifetime.
    private readonly int[] coefs = new int[Lpc.MaxLpcOrder * Lpc.MaxLpcOrder];
    private readonly int[] shift = new int[Lpc.MaxLpcOrder];

    private readonly byte[] packet;
    private bool closed;

    /// <summary><c>alac_encode_init</c>.</summary>
    /// <param name="channelCount">Channels, 1 to 8.</param>
    /// <param name="sampleRateHz">Sample rate, written into the magic cookie.</param>
    /// <param name="is32BitInput">
    /// True for <c>AV_SAMPLE_FMT_S32P</c> input, which the C forces to 24 bits per raw sample.
    /// </param>
    /// <param name="options">Settings, in place of the C's AVOptions.</param>
    public AlacEnc(int channelCount, int sampleRateHz, bool is32BitInput,
                   AlacEncoderOptions? options = null)
    {
        options ??= new AlacEncoderOptions();

        if (channelCount is < 1 or > AlacData.MaxChannels)
            throw new ArgumentOutOfRangeException(nameof(channelCount),
                $"ALAC carries 1 to {AlacData.MaxChannels} channels; asked for {channelCount}.");

        channels = channelCount;
        sampleRate = sampleRateHz;
        sampleFmtIsS32 = is32BitInput;

        frameSize = DefaultFrameSize;

        // The C overrides bits_per_raw_sample from the sample format and warns if it disagreed.
        // There is nothing to warn to here, so the format decides and the option is ignored.
        if (is32BitInput)
        {
            bitsPerRawSample = 24;
        }
        else
        {
            bitsPerRawSample = 16;
            extraBits = 0;
        }

        compressionLevel = Math.Clamp(options.CompressionLevel, 0, 2);

        // Initialize default Rice parameters
        rc.HistoryMult = 40;
        rc.InitialHistory = 10;
        rc.KModifier = 14;
        rc.RiceModifier = 4;

        minPredictionOrder = options.MinPredictionOrder;
        maxPredictionOrder = options.MaxPredictionOrder;

        if (maxPredictionOrder < minPredictionOrder)
            throw new ArgumentException(
                $"invalid prediction orders: min={minPredictionOrder} max={maxPredictionOrder}",
                nameof(options));

        if (minPredictionOrder < Lpc.MinLpcOrder || maxPredictionOrder > AlacMaxLpcOrder)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"prediction orders are {Lpc.MinLpcOrder} to {AlacMaxLpcOrder}; asked for "
              + $"min={minPredictionOrder} max={maxPredictionOrder}.");

        maxCodedFrameSize = GetMaxFrameSize(frameSize, channels, bitsPerRawSample);

        // The C calls ff_alloc_packet per frame and init_put_bits over it. Both are done once
        // here instead: a full frame is the largest this encoder can ever produce - a short last
        // frame's bound is smaller on every term - so one buffer covers every call and the
        // per-frame path allocates nothing.
        packet = new byte[4 * maxCodedFrameSize];
        pbctx = new PutBitContext(packet, packet.Length);

        Extradata = BuildExtradata();

        lpcCtx.Init(frameSize, maxPredictionOrder, LpcType.Levinson);
    }

    /// <summary>
    /// The 36-byte ALAC magic cookie a container stores as the track's codec configuration.
    /// <c>avctx-&gt;extradata</c>.
    /// </summary>
    public byte[] Extradata { get; }

    /// <summary>Samples per frame. <c>avctx-&gt;frame_size</c>.</summary>
    public int FrameSize => DefaultFrameSize;

    /// <summary><c>alac_encode_close</c>.</summary>
    public void Close()
    {
        if (closed) return;
        lpcCtx.End();
        closed = true;
    }

    /// <summary>
    /// <c>alac_encode_frame</c> - one packet from one frame of planar samples.
    /// </summary>
    /// <param name="samples">
    /// One span per channel, in INPUT channel order, each holding <paramref name="sampleCount"/>
    /// samples. 16-bit input is sign-extended into int; 32-bit input is the full int.
    /// </param>
    /// <param name="sampleCount">
    /// Samples in this frame. May be fewer than <see cref="FrameSize"/> for the last frame.
    /// </param>
    /// <returns>The packet, as a span over the encoder's own buffer, valid until the next call.</returns>
    public ReadOnlySpan<byte> EncodeFrame(ReadOnlySpan<int[]> samples, int sampleCount)
    {
        if (closed)
            throw new InvalidOperationException("The encoder is closed.");

        if (samples.Length < channels)
            throw new ArgumentException(
                $"{channels} channels were configured and {samples.Length} were handed over.",
                nameof(samples));

        if (sampleCount is < 1 || sampleCount > DefaultFrameSize)
            throw new ArgumentOutOfRangeException(nameof(sampleCount),
                $"a frame carries 1 to {DefaultFrameSize} samples; asked for {sampleCount}.");

        frameSize = sampleCount;

        int maxFrameSize = sampleCount < DefaultFrameSize
            ? GetMaxFrameSize(frameSize, channels, bitsPerRawSample)
            : maxCodedFrameSize;

        /* use verbatim mode for compression_level 0 */
        if (compressionLevel != 0)
        {
            verbatim = 0;
            extraBits = bitsPerRawSample - 16;
        }
        else
        {
            verbatim = 1;
            extraBits = 0;
        }

        int outBytes = WriteFrame(samples);

        if (outBytes > maxFrameSize)
        {
            /* frame too large. use verbatim mode */
            verbatim = 1;
            extraBits = 0;
            outBytes = WriteFrame(samples);
        }

        return packet.AsSpan(0, outBytes);
    }

    /// <summary><c>write_frame</c>.</summary>
    private int WriteFrame(ReadOnlySpan<int[]> samples)
    {
        pbctx.Reset(packet, packet.Length);

        int ch = 0, element = 0, sce = 0, cpe = 0;

        while (ch < channels)
        {
            if (AlacData.Element(channels, element) == AlacRawDataBlockType.Cpe)
            {
                WriteElement(AlacRawDataBlockType.Cpe, cpe,
                             samples[AlacData.Offset(channels, ch)],
                             samples[AlacData.Offset(channels, ch + 1)]);
                cpe++;
                ch += 2;
            }
            else
            {
                WriteElement(AlacRawDataBlockType.Sce, sce,
                             samples[AlacData.Offset(channels, ch)], null);
                sce++;
                ch++;
            }
            element++;
        }

        pbctx.PutBits(3, (uint)AlacRawDataBlockType.End);
        pbctx.Flush();

        return pbctx.BytesOutput;
    }

    /// <summary><c>write_element</c>.</summary>
    private void WriteElement(AlacRawDataBlockType element, int instance,
                              int[] samples0, int[]? samples1)
    {
        int i, j;
        const int predictionType = 0;

        int elementChannels = element == AlacRawDataBlockType.Cpe ? 2 : 1;

        if (verbatim != 0)
        {
            WriteElementHeader(element, instance);

            /* samples are channel-interleaved in verbatim mode */
            //
            // The C reads the caller's buffer directly here, at the input width, and shifts a
            // 32-bit input down by (32 - bits_per_raw_sample). This tree hands over ints either
            // way, so the shift is applied only for the 32-bit case, exactly as the C does.
            int shift32 = 32 - bitsPerRawSample;

            for (i = 0; i < frameSize; i++)
            {
                for (j = 0; j < elementChannels; j++)
                {
                    int[] plane = j == 0 ? samples0 : samples1!;
                    int value = sampleFmtIsS32 ? plane[i] >> shift32 : plane[i];
                    pbctx.PutSBits(bitsPerRawSample, value);
                }
            }

            return;
        }

        writeSampleSize = bitsPerRawSample - extraBits + elementChannels - 1;

        InitSampleBuffers(elementChannels, samples0, samples1);
        WriteElementHeader(element, instance);

        // extract extra bits if needed
        if (extraBits != 0)
        {
            int mask = (1 << extraBits) - 1;
            for (j = 0; j < elementChannels; j++)
            {
                int[] extra = predictorBuf[j];
                int[] smp = sampleBuf[j];
                for (i = 0; i < frameSize; i++)
                {
                    extra[i] = smp[i] & mask;
                    smp[i] >>= extraBits;
                }
            }
        }

        if (elementChannels == 2)
            AlacStereoDecorrelation();
        else
            interlacingShift = interlacingLeftweight = 0;

        pbctx.PutBits(8, (uint)interlacingShift);
        pbctx.PutBits(8, (uint)interlacingLeftweight);

        for (i = 0; i < elementChannels; i++)
        {
            CalcPredictorParams(i);

            pbctx.PutBits(4, predictionType);
            pbctx.PutBits(4, (uint)lpc[i].LpcQuant);

            pbctx.PutBits(3, (uint)rc.RiceModifier);
            pbctx.PutBits(5, (uint)lpc[i].LpcOrder);

            // predictor coeff. table
            for (j = 0; j < lpc[i].LpcOrder; j++)
                pbctx.PutSBits(16, lpc[i].LpcCoeff[j]);
        }

        // write extra bits if needed
        if (extraBits != 0)
        {
            for (i = 0; i < frameSize; i++)
                for (j = 0; j < elementChannels; j++)
                    pbctx.PutBits(extraBits, (uint)predictorBuf[j][i]);
        }

        // apply lpc and entropy coding to audio samples
        for (i = 0; i < elementChannels; i++)
        {
            AlacLinearPredictor(i);

            // The C's second-pass first-order filter sits behind prediction_type == 15, which
            // nothing sets: "TODO: determine when this will actually help. for now it's not
            // used." It is not translated, and predictionType is a const above so the compiler
            // can see that too.

            AlacEntropyCoder(i);
        }
    }

    /// <summary><c>init_sample_buffers</c>, with <c>COPY_SAMPLES</c> folded in.</summary>
    /// <remarks>
    /// The C's macro exists to write the loop once per input width. This tree hands over ints for
    /// both widths, so the shift is all that differs and there is one loop.
    /// </remarks>
    private void InitSampleBuffers(int elementChannels, int[] samples0, int[]? samples1)
    {
        // av_get_bytes_per_sample(sample_fmt) * 8 - bits_per_raw_sample
        int shiftAmount = (sampleFmtIsS32 ? 32 : 16) - bitsPerRawSample;

        for (int ch = 0; ch < elementChannels; ch++)
        {
            int[] bptr = sampleBuf[ch];
            int[] sptr = ch == 0 ? samples0 : samples1!;

            for (int i = 0; i < frameSize; i++)
                bptr[i] = sptr[i] >> shiftAmount;
        }
    }

    /// <summary><c>write_element_header</c>.</summary>
    private void WriteElementHeader(AlacRawDataBlockType element, int instance)
    {
        int encodeFs = 0;

        if (frameSize < DefaultFrameSize)
            encodeFs = 1;

        pbctx.PutBits(3, (uint)element);             // element type
        pbctx.PutBits(4, (uint)instance);            // element instance
        pbctx.PutBits(12, 0);                        // unused header bits
        pbctx.PutBits(1, (uint)encodeFs);            // Sample count is in the header
        pbctx.PutBits(2, (uint)(extraBits >> 3));    // Extra bytes (for 24-bit)
        pbctx.PutBits(1, (uint)verbatim);            // Audio block is verbatim

        if (encodeFs != 0)
            pbctx.PutBits32((uint)frameSize);        // No. of samples in the frame
    }

    /// <summary><c>calc_predictor_params</c>.</summary>
    private void CalcPredictorParams(int ch)
    {
        if (compressionLevel == 1)
        {
            lpc[ch].LpcOrder = 6;
            lpc[ch].LpcQuant = 6;
            lpc[ch].LpcCoeff[0] = 160;
            lpc[ch].LpcCoeff[1] = -190;
            lpc[ch].LpcCoeff[2] = 170;
            lpc[ch].LpcCoeff[3] = -130;
            lpc[ch].LpcCoeff[4] = 80;
            lpc[ch].LpcCoeff[5] = -25;
            return;
        }

        int optOrder = lpcCtx.CalcCoefs(sampleBuf[ch], frameSize,
                                        minPredictionOrder, maxPredictionOrder,
                                        AlacMaxLpcPrecision, coefs, shift,
                                        LpcType.Levinson, 0,
                                        Lpc.OrderMethodEst, AlacMinLpcShift,
                                        AlacMaxLpcShift, 1);

        lpc[ch].LpcOrder = optOrder;
        lpc[ch].LpcQuant = shift[optOrder - 1];

        // memcpy(s->lpc[ch].lpc_coeff, coefs[opt_order-1], opt_order*sizeof(int))
        // coefs is the C's [MAX_LPC_ORDER][MAX_LPC_ORDER], flattened here as the Lpc port has it.
        ReadOnlySpan<int> row = coefs.AsSpan((optOrder - 1) * Lpc.MaxLpcOrder, optOrder);
        for (int i = 0; i < optOrder; i++)
            lpc[ch].LpcCoeff[i] = row[i];
    }

    /// <summary><c>estimate_stereo_mode</c>.</summary>
    /// <remarks>
    /// The second-order residuals are int32 and are allowed to overflow; the sums are uint64 and
    /// a negative residual's absolute value is taken as the C takes it, which for
    /// <see cref="int.MinValue"/> is still negative and sign-extends into the sum. Faithful on
    /// purpose: this picks the stereo mode, so a difference here changes the bitstream.
    /// </remarks>
    private static int EstimateStereoMode(ReadOnlySpan<int> leftCh, ReadOnlySpan<int> rightCh, int n)
    {
        Span<ulong> sum = stackalloc ulong[4];
        Span<ulong> score = stackalloc ulong[4];

        /* calculate sum of 2nd order residual for each channel */
        sum[0] = sum[1] = sum[2] = sum[3] = 0;

        unchecked
        {
            for (int i = 2; i < n; i++)
            {
                int lt = leftCh[i] - 2 * leftCh[i - 1] + leftCh[i - 2];
                int rt = rightCh[i] - 2 * rightCh[i - 1] + rightCh[i - 2];
                sum[2] += (ulong)(long)FfAbs((lt + rt) >> 1);
                sum[3] += (ulong)(long)FfAbs(lt - rt);
                sum[0] += (ulong)(long)FfAbs(lt);
                sum[1] += (ulong)(long)FfAbs(rt);
            }

            /* calculate score for each mode */
            score[0] = sum[0] + sum[1];
            score[1] = sum[0] + sum[3];
            score[2] = sum[1] + sum[3];
            score[3] = sum[2] + sum[3];
        }

        /* return mode with lowest score */
        int best = 0;
        for (int i = 1; i < 4; i++)
        {
            if (score[i] < score[best])
                best = i;
        }

        return best;
    }

    /// <summary><c>alac_stereo_decorrelation</c>.</summary>
    private void AlacStereoDecorrelation()
    {
        int[] left = sampleBuf[0], right = sampleBuf[1];
        int i, n = frameSize;
        int tmp;

        int mode = EstimateStereoMode(left, right, n);

        unchecked
        {
            switch (mode)
            {
                case AlacChmodeLeftRight:
                    interlacingLeftweight = 0;
                    interlacingShift = 0;
                    break;

                case AlacChmodeLeftSide:
                    for (i = 0; i < n; i++)
                        right[i] = left[i] - right[i];
                    interlacingLeftweight = 1;
                    interlacingShift = 0;
                    break;

                case AlacChmodeRightSide:
                    for (i = 0; i < n; i++)
                    {
                        tmp = right[i];
                        right[i] = left[i] - right[i];
                        left[i] = tmp + (right[i] >> 31);
                    }
                    interlacingLeftweight = 1;
                    interlacingShift = 31;
                    break;

                default:
                    for (i = 0; i < n; i++)
                    {
                        tmp = left[i];
                        left[i] = (tmp + right[i]) >> 1;
                        right[i] = tmp - right[i];
                    }
                    interlacingLeftweight = 1;
                    interlacingShift = 1;
                    break;
            }
        }
    }

    /// <summary><c>alac_linear_predictor</c>.</summary>
    /// <remarks>
    /// <c>lpc</c> is a BY-VALUE copy of the channel's context and the coefficient adaptation
    /// below writes to the copy. That is deliberate in the C and load-bearing: the coefficients
    /// already written to the bitstream are the unadapted ones, and the decoder reaches the same
    /// adapted state by running the same loop from the same start. A reference here instead of a
    /// copy changes every frame after the first.
    /// <para>
    /// The C advances a <c>samples</c> POINTER by one per output sample while indexing off it,
    /// so the window slides. <c>sOff</c> is that pointer.
    /// </para>
    /// </remarks>
    private void AlacLinearPredictor(int ch)
    {
        int i;
        AlacLpcContext lpc = this.lpc[ch];   // by value, on purpose
        int[] residual = predictorBuf[ch];

        if (lpc.LpcOrder == 31)
        {
            residual[0] = sampleBuf[ch][0];

            unchecked
            {
                for (i = 1; i < frameSize; i++)
                    residual[i] = sampleBuf[ch][i] - sampleBuf[ch][i - 1];
            }

            return;
        }

        // generalised linear predictor

        if (lpc.LpcOrder <= 0)
            return;

        int[] samples = sampleBuf[ch];
        int sOff = 0;

        unchecked
        {
            // generate warm-up samples
            residual[0] = samples[0];
            for (i = 1; i <= lpc.LpcOrder; i++)
                residual[i] = SignExtend(samples[i] - samples[i - 1], writeSampleSize);

            // perform lpc on remaining samples
            for (i = lpc.LpcOrder + 1; i < frameSize; i++)
            {
                int sum = 1 << (lpc.LpcQuant - 1);

                for (int j = 0; j < lpc.LpcOrder; j++)
                    sum += (samples[sOff + lpc.LpcOrder - j] - samples[sOff]) * lpc.LpcCoeff[j];

                sum >>= lpc.LpcQuant;
                sum += samples[sOff];
                residual[i] = SignExtend(samples[sOff + lpc.LpcOrder + 1] - sum, writeSampleSize);

                int resVal = residual[i];

                if (resVal != 0)
                {
                    int index = lpc.LpcOrder - 1;
                    bool neg = resVal < 0;

                    while (index >= 0 && (neg ? resVal < 0 : resVal > 0))
                    {
                        int val = samples[sOff] - samples[sOff + lpc.LpcOrder - index];
                        int sign = val != 0 ? FfSign(val) : 0;

                        if (neg)
                            sign *= -1;

                        lpc.LpcCoeff[index] -= sign;
                        val *= sign;
                        resVal -= (val >> lpc.LpcQuant) * (lpc.LpcOrder - index);
                        index--;
                    }
                }

                sOff++;
            }
        }
    }

    /// <summary><c>alac_entropy_coder</c>.</summary>
    /// <remarks>
    /// <c>history</c> is an unsigned int and its update is unsigned arithmetic that wraps; the C
    /// mixes a signed product into it and lets the conversion happen. Written out here rather
    /// than tidied, because the Rice parameter comes out of it and the decoder recomputes the
    /// same sequence.
    /// </remarks>
    private void AlacEntropyCoder(int ch)
    {
        uint history = (uint)rc.InitialHistory;
        int signModifier = 0;
        int[] samples = predictorBuf[ch];
        int sOff = 0;

        unchecked
        {
            for (int i = 0; i < frameSize;)
            {
                int k = MpegVideo.MathOps.AvLog2((history >> 9) + 3);

                int x = -2 * samples[sOff] - 1;
                x ^= x >> 31;

                sOff++;
                i++;

                EncodeScalar(x - signModifier, k, writeSampleSize);

                history += (uint)(x * rc.HistoryMult) - ((history * (uint)rc.HistoryMult) >> 9);

                signModifier = 0;
                if (x > 0xFFFF)
                    history = 0xFFFF;

                if (history < 128 && i < frameSize)
                {
                    uint blockSize = 0;

                    k = 7 - MpegVideo.MathOps.AvLog2(history) + (int)((history + 16) >> 6);

                    while (samples[sOff] == 0 && i < frameSize)
                    {
                        sOff++;
                        i++;
                        blockSize++;
                    }

                    EncodeScalar((int)blockSize, k, 16);
                    signModifier = blockSize <= 0xFFFF ? 1 : 0;
                    history = 0;
                }
            }
        }
    }

    /// <summary><c>encode_scalar</c>.</summary>
    private void EncodeScalar(int x, int k, int writeSize)
    {
        k = Math.Min(k, rc.KModifier);

        // k is at least 1 at both call sites - av_log2((history >> 9) + 3) >= av_log2(3) = 1, and
        // the zero-run branch only runs with history < 128, where 7 - av_log2(history) >= 1 - so
        // the C's divisor is never zero. A zero would be a division by zero in the C too.
        int divisor = (1 << k) - 1;
        int q = x / divisor;
        int r = x % divisor;

        if (q > 8)
        {
            // write escape code and sample value directly
            pbctx.PutBits(9, AlacEscapeCode);
            pbctx.PutBits(writeSize, (uint)x);
        }
        else
        {
            if (q != 0)
                pbctx.PutBits(q, (uint)((1 << q) - 1));
            pbctx.PutBits(1, 0);

            if (k != 1)
            {
                if (r > 0)
                    pbctx.PutBits(k, (uint)(r + 1));
                else
                    pbctx.PutBits(k - 1, 0);
            }
        }
    }

    /// <summary><c>get_max_frame_size</c>.</summary>
    private static int GetMaxFrameSize(int frameSize, int ch, int bps)
    {
        int headerBits = 23 + 32 * (frameSize < DefaultFrameSize ? 1 : 0);
        return FfAlign(headerBits + bps * ch * frameSize + 3, 8) / 8;
    }

    /// <summary>
    /// The extradata block <c>alac_encode_init</c> fills in - the ALAC magic cookie.
    /// </summary>
    private byte[] BuildExtradata()
    {
        var e = new byte[AlacExtradataSize];

        WriteBe32(e, 0, AlacExtradataSize);
        WriteBe32(e, 4, 0x61_6C_61_63u);                  // MKBETAG('a','l','a','c')
        WriteBe32(e, 12, (uint)FrameSize);
        e[17] = (byte)bitsPerRawSample;
        e[21] = (byte)channels;
        WriteBe32(e, 24, (uint)maxCodedFrameSize);
        WriteBe32(e, 28, (uint)(sampleRate * channels * bitsPerRawSample));   // average bitrate
        WriteBe32(e, 32, (uint)sampleRate);

        // Set relevant extradata fields
        if (compressionLevel > 0)
        {
            e[18] = (byte)rc.HistoryMult;
            e[19] = (byte)rc.InitialHistory;
            e[20] = (byte)rc.KModifier;
        }

        return e;
    }

    private static void WriteBe32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    /// <summary><c>FFALIGN</c>.</summary>
    private static int FfAlign(int x, int a) => (x + a - 1) & ~(a - 1);

    /// <summary><c>FFABS</c>. Returns int, as the C's macro does, including for int.MinValue.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FfAbs(int x) => x >= 0 ? x : unchecked(-x);

    /// <summary><c>FFSIGN</c>. Note that the C's macro answers -1 for zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FfSign(int x) => x > 0 ? 1 : -1;

    /// <summary><c>sign_extend</c> of <c>mathops.h</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SignExtend(int val, int bits)
    {
        int shift = 32 - bits;
        return (val * (1 << shift)) >> shift;
    }
}

// Port of ffmpeg-master/libavcodec/dcadsp.c - the per-sample kernels of the DTS core.
//
// FFmpeg reaches these through a function pointer table so the x86 assembly can replace any one
// of them. This port has no assembly, so they are plain static methods; the table itself is the
// divergence, recorded in the port checklist. The C's names are kept, minus the _c suffix
// that only exists to distinguish the scalar version from the SIMD one.
//
// Subband samples live in one flat int[] with a base offset per (channel, band), which is
// FFmpeg's own layout - its int32_t **subband_samples is a table of pointers into one
// allocation. Keeping it means the four samples of ADPCM history sit immediately before each
// band's data in both languages, which is what lets inverse_adpcm read ptr[j - 4] without a
// special case for the start of a frame.

using System;

namespace MediaKernel.Core.Codecs.Audio.Dts;

/// <summary><c>dcadsp.c</c>.</summary>
internal static class DcaDsp
{
    /// <summary>
    /// <c>decode_hf_c</c> - reconstruct the high frequency subbands from their VQ addresses.
    /// </summary>
    /// <remarks>
    /// The rounding is <c>coeff[j] * scale + (1 &lt;&lt; 3) &gt;&gt; 4</c>, and C's precedence
    /// makes that <c>(coeff*scale + 8) &gt;&gt; 4</c> - the add binds tighter than the shift.
    /// Reading it as <c>coeff*scale + (8 &gt;&gt; 4)</c>, which is what the spacing suggests,
    /// would add nothing and lose the rounding.
    /// </remarks>
    public static void DecodeHf(int[] samples, ReadOnlySpan<int> bandOffset,
                               ReadOnlySpan<int> vqIndex, ReadOnlySpan<sbyte> hfVq,
                               ReadOnlySpan<int> scaleFactors,
                               int sbStart, int sbEnd, int ofs, int len)
    {
        for (int i = sbStart; i < sbEnd; i++)
        {
            int coeff = vqIndex[i] * 32;
            int scale = scaleFactors[i * 2];
            int dst = bandOffset[i] + ofs;
            for (int j = 0; j < len; j++)
                samples[dst + j] = DcaMath.Clip23((hfVq[coeff + j] * scale + (1 << 3)) >> 4);
        }
    }

    /// <summary>
    /// <c>decode_joint_c</c> - a channel whose high subbands are scaled copies of another's.
    /// </summary>
    public static void DecodeJoint(int[] samples, ReadOnlySpan<int> dstOffset,
                                  ReadOnlySpan<int> srcOffset,
                                  ReadOnlySpan<int> scaleFactors,
                                  int sbStart, int sbEnd, int ofs, int len)
    {
        for (int i = sbStart; i < sbEnd; i++)
        {
            int scale = scaleFactors[i];
            int dst = dstOffset[i] + ofs;
            int src = srcOffset[i] + ofs;
            for (int j = 0; j < len; j++)
                samples[dst + j] = DcaMath.Clip23(DcaMath.Mul17(samples[src + j], scale));
        }
    }

    /// <summary>
    /// <c>ff_dcaadpcm_predict</c> (dcaadpcm.h) - the four-tap ADPCM predictor.
    /// </summary>
    /// <remarks>
    /// The coefficients are applied in reverse: <c>input[3 - i] * coeff[i]</c>, so the newest of
    /// the four history samples meets the first coefficient. Accumulated in 64 bits because four
    /// products of a 24-bit sample and a 16-bit coefficient do not fit in 32.
    /// </remarks>
    public static int AdpcmPredict(int predVqIndex, ReadOnlySpan<int> input)
    {
        ReadOnlySpan<short> coeff = DcaData.AdpcmVb;
        int at = predVqIndex * DcaData.AdpcmVbStride;
        long pred = 0;
        for (int i = 0; i < DcaCore.AdpcmCoeffs; i++)
            pred += (long)input[DcaCore.AdpcmCoeffs - 1 - i] * coeff[at + i];

        return DcaMath.Clip23(DcaMath.Norm13(pred));
    }

    /// <summary>
    /// <c>ff_dca_core_dequantize</c> (dca_core.h) - scale eight sub-band samples in place.
    /// </summary>
    /// <remarks>
    /// The shift is chosen per call rather than fixed: the product of step size and scale is
    /// capped at 22 bits of resolution, and anything above that is shifted out of the multiplier
    /// and out of the normalisation together, so the two shifts cancel. Dropping the cap would
    /// overflow the 64-bit product for the lossless step sizes.
    /// </remarks>
    public static void Dequantize(int[] output, int outputAt, ReadOnlySpan<int> input,
                                 int stepSize, int scale, bool residual, int len)
    {
        long stepScale = (long)stepSize * scale;
        int shift = 0;

        // Limit scale factor resolution to 22 bits.
        if (stepScale > (1 << 23))
        {
            shift = Log2(stepScale >> 23) + 1;
            stepScale >>= shift;
        }

        if (residual)
        {
            for (int n = 0; n < len; n++)
                output[outputAt + n] += DcaMath.Clip23(DcaMath.Norm(input[n] * stepScale, 22 - shift));
        }
        else
        {
            for (int n = 0; n < len; n++)
                output[outputAt + n] = DcaMath.Clip23(DcaMath.Norm(input[n] * stepScale, 22 - shift));
        }
    }

    /// <summary><c>av_log2</c> - the index of the highest set bit, or 0 for zero.</summary>
    private static int Log2(long v)
    {
        int n = 0;
        while ((v >>= 1) != 0)
            n++;
        return n;
    }

    /// <summary>
    /// <c>inverse_adpcm</c> (dca_core.c) - add the predicted value back into each sample.
    /// </summary>
    /// <remarks>
    /// Sequential by necessity: sample <c>j</c> is predicted from the four before it, which
    /// include ones this loop has already written. It cannot be vectorised and FFmpeg does not
    /// try.
    /// </remarks>
    public static void InverseAdpcm(int[] samples, ReadOnlySpan<int> bandOffset,
                                   ReadOnlySpan<short> vqIndex, ReadOnlySpan<sbyte> predictionMode,
                                   int sbStart, int sbEnd, int ofs, int len)
    {
        Span<int> history = stackalloc int[DcaCore.AdpcmCoeffs];

        for (int i = sbStart; i < sbEnd; i++)
        {
            if (predictionMode[i] == 0)
                continue;

            int predId = vqIndex[i];
            int ptr = bandOffset[i] + ofs;
            for (int j = 0; j < len; j++)
            {
                for (int k = 0; k < DcaCore.AdpcmCoeffs; k++)
                    history[k] = samples[ptr + j - DcaCore.AdpcmCoeffs + k];

                int x = AdpcmPredict(predId, history);
                samples[ptr + j] = DcaMath.Clip23(samples[ptr + j] + x);
            }
        }
    }

    /// <summary>
    /// <c>lfe_fir_float_c</c> - interpolate the decimated LFE channel back up to full rate.
    /// </summary>
    /// <remarks>
    /// One decimated sample becomes 64 or 128 interpolated ones. The coefficient table is read
    /// forwards for the first half of each output block and backwards from index 255 for the
    /// second, which is how a symmetric 256-tap filter is stored once and used twice.
    /// <paramref name="lfeAt"/> indexes the <i>current</i> sample and the filter reaches
    /// backwards from it, so the caller must leave <c>DCA_LFE_HISTORY</c> samples in front.
    /// </remarks>
    public static void LfeFirFloat(Span<float> pcmSamples, ReadOnlySpan<int> lfeSamples, int lfeAt,
                                  ReadOnlySpan<float> filterCoeff, int npcmblocks, int decSelect)
    {
        int factor = 64 << decSelect;
        int ncoeffs = 8 >> decSelect;
        int nlfesamples = npcmblocks >> (decSelect + 1);
        int outAt = 0;

        for (int i = 0; i < nlfesamples; i++)
        {
            for (int j = 0; j < factor / 2; j++)
            {
                float a = 0;
                float b = 0;

                for (int k = 0; k < ncoeffs; k++)
                {
                    a += filterCoeff[j * ncoeffs + k] * lfeSamples[lfeAt - k];
                    b += filterCoeff[255 - j * ncoeffs - k] * lfeSamples[lfeAt - k];
                }

                pcmSamples[outAt + j] = a;
                pcmSamples[outAt + factor / 2 + j] = b;
            }

            lfeAt++;
            outAt += factor;
        }
    }

    /// <summary>
    /// <c>sub_qmf32_float_c</c> - the 32-band synthesis filter bank over a whole frame.
    /// </summary>
    /// <remarks>
    /// The sign flip on load is the part to read twice: <c>if ((i - 1) &amp; 2)</c> negates
    /// subbands 3, 4, 7, 8, 11, 12, ... - a period of four starting at three, not every other
    /// band and not the odd ones. It is the alternating sign the QMF bank folds into its input
    /// rather than into the window.
    /// </remarks>
    public static void SubQmf32Float(MediaKernel.Core.Dsp.Tx.TxMdct imdct,
                                     Span<float> pcmSamples,
                                     int[] subbandSamples, ReadOnlySpan<int> bandOffset,
                                     float[] hist1, ref int offset, float[] hist2,
                                     ReadOnlySpan<float> filterCoeff, int npcmblocks,
                                     float scale)
    {
        Span<float> input = stackalloc float[32];
        int outAt = 0;

        for (int j = 0; j < npcmblocks; j++)
        {
            for (int i = 0; i < 32; i++)
            {
                int v = subbandSamples[bandOffset[i] + j];
                input[i] = ((i - 1) & 2) != 0 ? -v : v;
            }

            SynthFilter.SynthFilterFloat(imdct, hist1, ref offset, hist2, filterCoeff,
                                         pcmSamples.Slice(outAt, 32), input, scale);
            outAt += 32;
        }
    }

    /// <summary>
    /// <c>sub_qmf64_float_c</c> - the 64-band synthesis, for X96.
    /// </summary>
    /// <remarks>
    /// The low 32 bands are <em>residual</em> coded: the X96 extension's own samples are added
    /// to the core's for those bands, and only bands 32-63 are X96's alone. When the X96 data
    /// for a channel is absent, <paramref name="hasHi"/> is false and the top half stays zero,
    /// which is how a channel the extension did not cover still gets filtered at the doubled
    /// rate rather than falling out of the frame.
    /// <para>
    /// The sign flip on <c>(i - 1) &amp; 2</c> negates bands 3, 4, 7, 8, 11, 12 and so on. It is
    /// part of the filter bank, not a quirk of the data.
    /// </para>
    /// </remarks>
    public static void SubQmf64Float(MediaKernel.Core.Dsp.Tx.TxMdct imdct,
                                     Span<float> pcmSamples,
                                     int[] subbandSamplesLo, ReadOnlySpan<int> bandOffsetLo,
                                     int[] subbandSamplesHi, ReadOnlySpan<int> bandOffsetHi,
                                     bool hasHi,
                                     float[] hist1, ref int offset, float[] hist2,
                                     ReadOnlySpan<float> filterCoeff, int npcmblocks,
                                     float scale)
    {
        Span<float> input = stackalloc float[64];
        int outAt = 0;

        for (int j = 0; j < npcmblocks; j++)
        {
            if (hasHi)
            {
                for (int i = 0; i < 32; i++)
                {
                    int v = subbandSamplesLo[bandOffsetLo[i] + j]
                          + subbandSamplesHi[bandOffsetHi[i] + j];
                    input[i] = ((i - 1) & 2) != 0 ? -v : v;
                }
                for (int i = 32; i < 64; i++)
                {
                    int v = subbandSamplesHi[bandOffsetHi[i] + j];
                    input[i] = ((i - 1) & 2) != 0 ? -v : v;
                }
            }
            else
            {
                for (int i = 0; i < 32; i++)
                {
                    int v = subbandSamplesLo[bandOffsetLo[i] + j];
                    input[i] = ((i - 1) & 2) != 0 ? -v : v;
                }
                input[32..].Clear();
            }

            SynthFilter.SynthFilterFloat64(imdct, hist1, ref offset, hist2, filterCoeff,
                                           pcmSamples.Slice(outAt, 64), input, scale);
            outAt += 64;
        }
    }

    /// <summary>
    /// <c>lfe_x96_float_c</c> - interpolate the already-interpolated LFE to the doubled rate.
    /// </summary>
    /// <remarks>
    /// Two output samples per input, at 1:3 and 3:1 against the previous sample. This is a
    /// gentle low pass rather than a resampler: it attenuates the 47.6-48.0 kHz image the
    /// first interpolation leaves behind. <paramref name="history"/> carries the last input
    /// across the frame boundary, so it is state, not a scratch value.
    /// </remarks>
    public static void LfeX96Float(Span<float> dst, ReadOnlySpan<float> src,
                                   ref float history, int len)
    {
        float prev = history;

        int at = 0;
        for (int i = 0; i < len; i++)
        {
            float a = 0.25f * src[i] + 0.75f * prev;
            float b = 0.75f * src[i] + 0.25f * prev;
            prev = src[i];
            dst[at++] = a;
            dst[at++] = b;
        }

        history = prev;
    }

    // ---- The fixed point half ------------------------------------------------------------
    //
    // These are not the float kernels with the types changed. Three things differ, and all
    // three are deliberate in the C:
    //
    //  * SubQmf32Fixed and SubQmf64Fixed do NOT apply the (i - 1) & 2 sign flip their float
    //    counterparts apply to the subband samples on load, and SynthFilterFixed pairs its
    //    accumulators with the opposite synth_buf indices. Both fall out of the same cause:
    //    the fixed path's IMDCT is dcadct.c's own imdct_half_32/64, which has that sign and
    //    that permutation folded into its twiddle tables, where the float path's generic
    //    av_tx half-MDCT does not.
    //  * There is no output scale. The float path multiplies by 1/2^17 (or 1/2^16 at 64 bands)
    //    inside the synthesis window; the fixed path keeps 24-bit integer samples throughout
    //    and the caller shifts them up by 8 into s32 at the very end.
    //  * Every stage saturates to 24 bits signed. That is part of the result, not a guard: the
    //    reference decoder clips at exactly these points, so a port that clipped elsewhere
    //    would agree on quiet programme material and differ on loud.

    /// <summary>
    /// <c>lfe_fir_fixed_c</c> - interpolate the decimated LFE channel back up to full rate.
    /// </summary>
    /// <remarks>
    /// The integer version supports <b>only</b> the 64-tap decimation. Where
    /// <see cref="LfeFirFloat"/> takes a <c>decSelect</c> and handles LFF=1 as well, there is no
    /// <c>ff_dca_lfe_fir_128_fixed</c> table in FFmpeg at all, and
    /// <c>ff_dca_core_filter_fixed</c> rejects the frame outright rather than filtering it
    /// wrongly. The factor, the coefficient count and the backwards index base of 255 are
    /// therefore constants here instead of parameters.
    /// </remarks>
    public static void LfeFirFixed(Span<int> pcmSamples, ReadOnlySpan<int> lfeSamples, int lfeAt,
                                   ReadOnlySpan<int> filterCoeff, int npcmblocks)
    {
        int nlfesamples = npcmblocks >> 1;
        int outAt = 0;

        for (int i = 0; i < nlfesamples; i++)
        {
            // One decimated sample generates 64 interpolated ones.
            for (int j = 0; j < 32; j++)
            {
                long a = 0;
                long b = 0;

                for (int k = 0; k < 8; k++)
                {
                    a += (long)filterCoeff[j * 8 + k] * lfeSamples[lfeAt - k];
                    b += (long)filterCoeff[255 - j * 8 - k] * lfeSamples[lfeAt - k];
                }

                pcmSamples[outAt + j] = DcaMath.Clip23(DcaMath.Norm23(a));
                pcmSamples[outAt + 32 + j] = DcaMath.Clip23(DcaMath.Norm23(b));
            }

            lfeAt++;
            outAt += 64;
        }
    }

    /// <summary>
    /// <c>lfe_x96_fixed_c</c> - interpolate the already-interpolated LFE to the doubled rate.
    /// </summary>
    /// <remarks>
    /// The float version's 0.25 and 0.75 appear here as 2097471 and 6291137 over 2^23. They are
    /// not 0.25 and 0.75: a quarter of 2^23 is 2097152 and three quarters is 6291456, so the
    /// integer coefficients are 319 high and 319 low respectively. Rounding them to the obvious
    /// values would pass every ear and fail a bit-exact comparison on every X96 frame.
    /// </remarks>
    public static void LfeX96Fixed(Span<int> dst, ReadOnlySpan<int> src, ref int history, int len)
    {
        int prev = history;

        int at = 0;
        for (int i = 0; i < len; i++)
        {
            long a = 2097471L * src[i] + 6291137L * prev;
            long b = 6291137L * src[i] + 2097471L * prev;
            prev = src[i];
            dst[at++] = DcaMath.Clip23(DcaMath.Norm23(a));
            dst[at++] = DcaMath.Clip23(DcaMath.Norm23(b));
        }

        history = prev;
    }

    /// <summary>
    /// <c>sub_qmf32_fixed_c</c> - the integer 32-band synthesis filter bank over a whole frame.
    /// </summary>
    public static void SubQmf32Fixed(Span<int> pcmSamples,
                                     int[] subbandSamples, ReadOnlySpan<int> bandOffset,
                                     int[] hist1, ref int offset, int[] hist2,
                                     ReadOnlySpan<int> filterCoeff, int npcmblocks)
    {
        Span<int> input = stackalloc int[32];
        int outAt = 0;

        for (int j = 0; j < npcmblocks; j++)
        {
            // Load in one sample from each subband. No sign flip - see the note above.
            for (int i = 0; i < 32; i++)
                input[i] = subbandSamples[bandOffset[i] + j];

            SynthFilter.SynthFilterFixed(hist1, ref offset, hist2, filterCoeff,
                                         pcmSamples.Slice(outAt, 32), input);
            outAt += 32;
        }
    }

    /// <summary>
    /// <c>sub_qmf64_fixed_c</c> - the integer 64-band synthesis, for X96.
    /// </summary>
    /// <remarks>
    /// As in the float case the low 32 bands are <em>residual</em> coded, so X96's samples are
    /// added to the core's for those bands and bands 32-63 are X96's alone. When
    /// <paramref name="hasHi"/> is false the top half stays zero and the channel is still
    /// filtered at the doubled rate.
    /// </remarks>
    public static void SubQmf64Fixed(Span<int> pcmSamples,
                                     int[] subbandSamplesLo, ReadOnlySpan<int> bandOffsetLo,
                                     int[] subbandSamplesHi, ReadOnlySpan<int> bandOffsetHi,
                                     bool hasHi,
                                     int[] hist1, ref int offset, int[] hist2,
                                     ReadOnlySpan<int> filterCoeff, int npcmblocks)
    {
        Span<int> input = stackalloc int[64];
        int outAt = 0;

        for (int j = 0; j < npcmblocks; j++)
        {
            if (hasHi)
            {
                for (int i = 0; i < 32; i++)
                    input[i] = subbandSamplesLo[bandOffsetLo[i] + j]
                             + subbandSamplesHi[bandOffsetHi[i] + j];
                for (int i = 32; i < 64; i++)
                    input[i] = subbandSamplesHi[bandOffsetHi[i] + j];
            }
            else
            {
                for (int i = 0; i < 32; i++)
                    input[i] = subbandSamplesLo[bandOffsetLo[i] + j];
                input[32..].Clear();
            }

            SynthFilter.SynthFilterFixed64(hist1, ref offset, hist2, filterCoeff,
                                           pcmSamples.Slice(outAt, 64), input);
            outAt += 64;
        }
    }

    /// <summary>
    /// <c>decor_c</c> - add a decorrelated channel back into its pair.
    /// </summary>
    /// <remarks>
    /// Only <c>dca_xll.c</c> calls this; the lossy core has no use for it. It is here because it
    /// belongs to <c>dcadsp.c</c>, and it is left unexercised until XLL is translated.
    /// <para>
    /// The arithmetic deliberately wraps. The C casts through <c>SUINT</c> so that the overflow
    /// is defined rather than undefined behaviour, which in C# is simply what <c>int</c>
    /// arithmetic already does, so the casts have no equivalent and are not needed. The shift,
    /// however, must stay <i>arithmetic</i> on a signed value: the rounding term is added before
    /// it, so a logical shift of a negative product would be wrong by about 2^29.
    /// </para>
    /// </remarks>
    public static void Decor(Span<int> dst, ReadOnlySpan<int> src, int coeff, int len)
    {
        for (int i = 0; i < len; i++)
            dst[i] += (src[i] * coeff + (1 << 2)) >> 3;
    }

    /// <summary>
    /// <c>dmix_sub_xch_c</c> - take the embedded XCH downmix back out of Ls and Rs.
    /// </summary>
    /// <remarks>
    /// The coefficient is 5931520, which is <c>1/sqrt(2)</c> in Q23 - the float path's
    /// <c>0.70710678</c>. Both surround channels are reduced by the <em>same</em> value, so it
    /// is computed once per sample rather than twice.
    /// </remarks>
    public static void DmixSubXch(Span<int> dst1, Span<int> dst2, ReadOnlySpan<int> src, int len)
    {
        for (int i = 0; i < len; i++)
        {
            int cs = DcaMath.Mul23(src[i], 5931520);
            dst1[i] -= cs;
            dst2[i] -= cs;
        }
    }

    /// <summary>
    /// <c>filter0</c> - <c>dst[i] -= mul22(src[i], coeff)</c> over a run.
    /// </summary>
    /// <remarks>
    /// Q22, where <see cref="Filter1"/> is Q23. The two are otherwise identical and the
    /// precision is the only thing telling them apart, which is why they are separate functions
    /// in the C rather than one with a shift argument.
    /// </remarks>
    private static void Filter0(int[] dst, int dstAt, int[] src, int srcAt, int coeff, int len)
    {
        for (int i = 0; i < len; i++)
            dst[dstAt + i] -= DcaMath.Mul22(src[srcAt + i], coeff);
    }

    /// <summary><c>filter1</c> - as <see cref="Filter0"/> at Q23.</summary>
    private static void Filter1(int[] dst, int dstAt, int[] src, int srcAt, int coeff, int len)
    {
        for (int i = 0; i < len; i++)
            dst[dstAt + i] -= DcaMath.Mul23(src[srcAt + i], coeff);
    }

    /// <summary>
    /// <c>assemble_freq_bands_c</c> - interleave XLL's two frequency bands back to one rate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An all-pass lattice run in place, then an interleave. Both halves are unusual enough to
    /// be worth stating:
    /// </para>
    /// <para>
    /// The lattice <b>writes back into its own inputs</b>, alternating which band is destination
    /// and which is source, four times with <see cref="Filter0"/> and then 24 more with
    /// <see cref="Filter1"/>. Neither band holds its original samples afterwards, so a caller
    /// cannot reuse them - and the order of the calls is the filter. There is no temporary.
    /// </para>
    /// <para>
    /// The <c>src0--</c> in the second loop walks band 0's window <b>backwards by eight</b>, so
    /// by the last iteration it is reading eight samples in front of where it started. That is
    /// what the decimator-history headroom in front of band 0 is for; it is not slack and it is
    /// not optional. The final interleave then takes <c>*src1++</c> and <c>*++src0</c> - note
    /// the PRE-increment on src0 against the post-increment on src1, which is what re-aligns the
    /// window the loop above walked back.
    /// </para>
    /// </remarks>
    public static void AssembleFreqBands(int[] dst, int dstAt,
                                         int[] band0, int src0At,
                                         int[] band1, int src1At,
                                         ReadOnlySpan<int> coeff, int len)
    {
        Filter0(band0, src0At, band1, src1At, coeff[0], len);
        Filter0(band1, src1At, band0, src0At, coeff[1], len);
        Filter0(band0, src0At, band1, src1At, coeff[2], len);
        Filter0(band1, src1At, band0, src0At, coeff[3], len);

        for (int i = 0; i < 8; i++, src0At--)
        {
            Filter1(band0, src0At, band1, src1At, coeff[i + 4], len);
            Filter1(band1, src1At, band0, src0At, coeff[i + 12], len);
            Filter1(band0, src0At, band1, src1At, coeff[i + 4], len);
        }

        for (int i = 0; i < len; i++)
        {
            dst[dstAt++] = band1[src1At++];
            dst[dstAt++] = band0[++src0At];
        }
    }

    /// <summary><c>dmix_sub_c</c> - subtract a scaled channel, Q15 coefficient.</summary>
    public static void DmixSub(Span<int> dst, ReadOnlySpan<int> src, int coeff, int len)
    {
        for (int i = 0; i < len; i++)
            dst[i] -= DcaMath.Mul15(src[i], coeff);
    }

    /// <summary><c>dmix_add_c</c> - add a scaled channel, Q15 coefficient.</summary>
    public static void DmixAdd(Span<int> dst, ReadOnlySpan<int> src, int coeff, int len)
    {
        for (int i = 0; i < len; i++)
            dst[i] += DcaMath.Mul15(src[i], coeff);
    }

    /// <summary><c>dmix_scale_c</c> - scale a channel in place, Q15 coefficient.</summary>
    public static void DmixScale(Span<int> dst, int scale, int len)
    {
        for (int i = 0; i < len; i++)
            dst[i] = DcaMath.Mul15(dst[i], scale);
    }

    /// <summary>
    /// <c>dmix_scale_inv_c</c> - undo an embedded pre-scale, <b>Q16</b> coefficient.
    /// </summary>
    /// <remarks>
    /// Note the 16 against <see cref="DmixScale"/>'s 15. The forward and inverse scales are
    /// stored at different precisions, so sharing one helper between them - which the names
    /// invite - halves or doubles the gain of every core channel in an XXCH frame.
    /// </remarks>
    public static void DmixScaleInv(Span<int> dst, int scaleInv, int len)
    {
        for (int i = 0; i < len; i++)
            dst[i] = DcaMath.Mul16(dst[i], scaleInv);
    }
}

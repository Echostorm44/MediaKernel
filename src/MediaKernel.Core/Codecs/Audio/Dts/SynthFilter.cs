// Port of ffmpeg-master/libavcodec/synth_filter.c - the 32-band QMF synthesis window.
//
// This is the second half of the DTS filter bank: the IMDCT turns 32 subband samples into 32
// time-domain ones, and this overlaps them against a 512-tap window held in a 512-entry circular
// history. The circular buffer is what makes it worth translating carefully rather than
// rewriting: synth_buf_offset walks DOWN by 32 and wraps at 512, and the inner loop is split in
// two at the wrap point so neither half has to test for it.

using System;
using MediaKernel.Core.Dsp.Tx;

namespace MediaKernel.Core.Codecs.Audio.Dts;

/// <summary><c>synth_filter.c</c>.</summary>
internal static class SynthFilter
{
    /// <summary>
    /// <c>synth_filter_float</c> - one block of 32 subband samples to 32 PCM samples.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="synthBuf"/> is 512 floats used circularly and
    /// <paramref name="synthBufOffset"/> is where this block's IMDCT output goes. The two inner
    /// loops are one loop in effect: the first runs while <c>i + j</c> stays inside the buffer
    /// and the second continues with the index wrapped back by 512. Merging them into one loop
    /// with a modulo would be slower and would hide that the split point moves every block.
    /// </para>
    /// <para>
    /// The four accumulators are not four independent sums: <c>a</c> and <c>b</c> become this
    /// block's output while <c>c</c> and <c>d</c> are saved in <paramref name="synthBuf2"/> and
    /// become the <i>next</i> block's starting values for <c>a</c> and <c>b</c>. That is the
    /// overlap-add, and it is why <paramref name="synthBuf2"/> is read before it is written.
    /// </para>
    /// <para>
    /// Note the sign: the <c>a</c> term takes <c>-synthBuf[15 - i + j]</c>, with the index
    /// running backwards and the value negated. Both parts matter.
    /// </para>
    /// </remarks>
    public static void SynthFilterFloat(TxMdct imdct,
                                        float[] synthBuf, ref int synthBufOffset,
                                        float[] synthBuf2, ReadOnlySpan<float> window,
                                        Span<float> output, ReadOnlySpan<float> input,
                                        float scale)
    {
        int at = synthBufOffset;

        imdct.Transform(synthBuf.AsSpan(at, 32), input);

        for (int i = 0; i < 16; i++)
        {
            float a = synthBuf2[i];
            float b = synthBuf2[i + 16];
            float c = 0;
            float d = 0;

            int j = 0;
            for (; j < 512 - synthBufOffset; j += 64)
            {
                a += window[i + j] * -synthBuf[at + 15 - i + j];
                b += window[i + j + 16] * synthBuf[at + i + j];
                c += window[i + j + 32] * synthBuf[at + 16 + i + j];
                d += window[i + j + 48] * synthBuf[at + 31 - i + j];
            }
            for (; j < 512; j += 64)
            {
                a += window[i + j] * -synthBuf[at + 15 - i + j - 512];
                b += window[i + j + 16] * synthBuf[at + i + j - 512];
                c += window[i + j + 32] * synthBuf[at + 16 + i + j - 512];
                d += window[i + j + 48] * synthBuf[at + 31 - i + j - 512];
            }

            output[i] = a * scale;
            output[i + 16] = b * scale;
            synthBuf2[i] = c;
            synthBuf2[i + 16] = d;
        }

        synthBufOffset = (synthBufOffset - 32) & 511;
    }

    /// <summary>
    /// <c>synth_filter_float_64</c> - the X96 variant, 64 bands into 64 samples.
    /// </summary>
    /// <remarks>
    /// The same shape at twice the size: a 1024-tap window, a 1024-entry history and an offset
    /// that walks down by 64. Needed only when an X96 extension substream has been decoded.
    /// </remarks>
    public static void SynthFilterFloat64(TxMdct imdct,
                                          float[] synthBuf, ref int synthBufOffset,
                                          float[] synthBuf2, ReadOnlySpan<float> window,
                                          Span<float> output, ReadOnlySpan<float> input,
                                          float scale)
    {
        int at = synthBufOffset;

        imdct.Transform(synthBuf.AsSpan(at, 64), input);

        for (int i = 0; i < 32; i++)
        {
            float a = synthBuf2[i];
            float b = synthBuf2[i + 32];
            float c = 0;
            float d = 0;

            int j = 0;
            for (; j < 1024 - synthBufOffset; j += 128)
            {
                a += window[i + j] * -synthBuf[at + 31 - i + j];
                b += window[i + j + 32] * synthBuf[at + i + j];
                c += window[i + j + 64] * synthBuf[at + 32 + i + j];
                d += window[i + j + 96] * synthBuf[at + 63 - i + j];
            }
            for (; j < 1024; j += 128)
            {
                a += window[i + j] * -synthBuf[at + 31 - i + j - 1024];
                b += window[i + j + 32] * synthBuf[at + i + j - 1024];
                c += window[i + j + 64] * synthBuf[at + 32 + i + j - 1024];
                d += window[i + j + 96] * synthBuf[at + 63 - i + j - 1024];
            }

            output[i] = a * scale;
            output[i + 32] = b * scale;
            synthBuf2[i] = c;
            synthBuf2[i + 32] = d;
        }

        synthBufOffset = (synthBufOffset - 64) & 1023;
    }

    /// <summary>
    /// <c>synth_filter_fixed</c> - the integer 32-band synthesis window, for bit-exact output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not the float function with the types changed, and the difference is not a
    /// typo in FFmpeg.</b> Compare the two accumulators against
    /// <see cref="SynthFilterFloat"/>: there, <c>a</c> takes
    /// <c>-synthBuf[15 - i + j]</c> and <c>b</c> takes <c>synthBuf[i + j]</c>; here <c>a</c>
    /// takes <c>synthBuf[i + j]</c> and <c>b</c> takes <c>synthBuf[15 - i + j]</c>, with no
    /// negation anywhere. The two IMDCTs do not agree on the order or the sign of their output:
    /// the float path calls a general half-MDCT from <c>av_tx</c>, while this one calls
    /// <see cref="DcaDct.ImdctHalf32"/>, which has that permutation and that sign folded into
    /// its own twiddle tables. "Harmonising" the two loops, in either direction, breaks whichever
    /// one was changed. The same reasoning is why <see cref="DcaDsp.SubQmf32Fixed"/> has no
    /// <c>(i - 1) &amp; 2</c> sign flip on its input where the float one does.
    /// </para>
    /// <para>
    /// <paramref name="synthBuf2"/> enters the sum as <c>synthBuf2[i] * (1L &lt;&lt; 21)</c>, in
    /// 64 bits, because it is stored already normalised by 21 bits and has to be brought back up
    /// to the accumulator's scale before <see cref="DcaMath.Norm21"/> takes it down again. As an
    /// <c>int</c> shift it would overflow for any history sample above 2^10.
    /// </para>
    /// </remarks>
    public static void SynthFilterFixed(int[] synthBuf, ref int synthBufOffset,
                                        int[] synthBuf2, ReadOnlySpan<int> window,
                                        Span<int> output, ReadOnlySpan<int> input)
    {
        int at = synthBufOffset;

        DcaDct.ImdctHalf32(synthBuf.AsSpan(at, 32), input);

        for (int i = 0; i < 16; i++)
        {
            long a = synthBuf2[i] * (1L << 21);
            long b = synthBuf2[i + 16] * (1L << 21);
            long c = 0;
            long d = 0;

            int j = 0;
            for (; j < 512 - synthBufOffset; j += 64)
            {
                a += (long)window[i + j] * synthBuf[at + i + j];
                b += (long)window[i + j + 16] * synthBuf[at + 15 - i + j];
                c += (long)window[i + j + 32] * synthBuf[at + 16 + i + j];
                d += (long)window[i + j + 48] * synthBuf[at + 31 - i + j];
            }
            for (; j < 512; j += 64)
            {
                a += (long)window[i + j] * synthBuf[at + i + j - 512];
                b += (long)window[i + j + 16] * synthBuf[at + 15 - i + j - 512];
                c += (long)window[i + j + 32] * synthBuf[at + 16 + i + j - 512];
                d += (long)window[i + j + 48] * synthBuf[at + 31 - i + j - 512];
            }

            output[i] = DcaMath.Clip23(DcaMath.Norm21(a));
            output[i + 16] = DcaMath.Clip23(DcaMath.Norm21(b));
            synthBuf2[i] = DcaMath.Norm21(c);
            synthBuf2[i + 16] = DcaMath.Norm21(d);
        }

        synthBufOffset = (synthBufOffset - 32) & 511;
    }

    /// <summary>
    /// <c>synth_filter_fixed_64</c> - the integer 64-band synthesis window, for X96.
    /// </summary>
    /// <remarks>
    /// The same shape at twice the length, and the normalisation drops from 21 bits to 20: twice
    /// as many products land in each accumulator, so one more bit of headroom is left for them.
    /// The output is still clipped to 23 bits, because that is the sample width, not an artefact
    /// of the accumulator.
    /// </remarks>
    public static void SynthFilterFixed64(int[] synthBuf, ref int synthBufOffset,
                                          int[] synthBuf2, ReadOnlySpan<int> window,
                                          Span<int> output, ReadOnlySpan<int> input)
    {
        int at = synthBufOffset;

        DcaDct.ImdctHalf64(synthBuf.AsSpan(at, 64), input);

        for (int i = 0; i < 32; i++)
        {
            long a = synthBuf2[i] * (1L << 20);
            long b = synthBuf2[i + 32] * (1L << 20);
            long c = 0;
            long d = 0;

            int j = 0;
            for (; j < 1024 - synthBufOffset; j += 128)
            {
                a += (long)window[i + j] * synthBuf[at + i + j];
                b += (long)window[i + j + 32] * synthBuf[at + 31 - i + j];
                c += (long)window[i + j + 64] * synthBuf[at + 32 + i + j];
                d += (long)window[i + j + 96] * synthBuf[at + 63 - i + j];
            }
            for (; j < 1024; j += 128)
            {
                a += (long)window[i + j] * synthBuf[at + i + j - 1024];
                b += (long)window[i + j + 32] * synthBuf[at + 31 - i + j - 1024];
                c += (long)window[i + j + 64] * synthBuf[at + 32 + i + j - 1024];
                d += (long)window[i + j + 96] * synthBuf[at + 63 - i + j - 1024];
            }

            output[i] = DcaMath.Clip23(DcaMath.Norm20(a));
            output[i + 32] = DcaMath.Clip23(DcaMath.Norm20(b));
            synthBuf2[i] = DcaMath.Norm20(c);
            synthBuf2[i + 32] = DcaMath.Norm20(d);
        }

        synthBufOffset = (synthBufOffset - 64) & 1023;
    }
}

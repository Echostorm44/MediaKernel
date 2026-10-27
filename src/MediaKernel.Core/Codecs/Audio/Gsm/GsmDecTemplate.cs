// Translated from ffmpeg-master/libavcodec/gsmdec_template.c.
//
// WHY THE C CALLS THIS A TEMPLATE. gsmdec_template.c is a .c file that is #included twice:
// once by gsmdec.c, which reads the bitstream most-significant-bit first, and once by
// msgsmdec.c, which does
//
//     #define BITSTREAM_READER_LE
//     #include "gsmdec_template.c"
//
// so the identical decoder is compiled a second time against get_bits.h's little-endian
// reader. Plain GSM and Microsoft's GSM are the same codec packed in opposite bit orders, and
// that #include is the entire difference between them.
//
// DIVERGENCE, as the porting rules require it be recorded: there is one copy here, and the bit
// order is a field on GsmBitReader rather than two instantiations. The project's allowance is
// for generics over static abstract members in place of #define templates, which would also
// work; a bool is what this one deserves, because a block is 65 bytes and reads 76 fields, so
// the branch is not measurable and two copies of a 150-line decoder would be.
//
// TWO DETAILS LOOK LOAD-BEARING AND ARE NOT, which a perturbation sweep established and this
// comment records so nobody has to establish it twice:
//
//   gsm_mult's SUINT    The C multiplies through an unsigned int so the product WRAPS rather
//                       than overflowing a signed one, then casts back and shifts
//                       arithmetically. That is there to keep the sanitizers quiet on
//                       malformed input: the real operands are a gain of at most 32767 and a
//                       sample, whose product is under 2^30, so the round trip never changes
//                       an answer. It is translated faithfully anyway.
//   long_term_synth's   dst[i] = gsm_mult(...) stores into int16_t, but the gain is at most
//   store               32767 and the shift is 15, so the result cannot be larger in
//                       magnitude than the sample it came from. There is nothing to truncate.
//
// gsm_mult's ROUNDING TERM is a different matter and is load-bearing: dropping the (1 << 14)
// changes the output of every stream immediately.

using System;

namespace MediaKernel.Core.Codecs.Audio.Gsm;

/// <summary>
/// <c>gsmdec_template.c</c>: one 160-sample block, from the bitstream to the samples.
/// </summary>
public static class GsmDecTemplate
{
    /// <summary><c>apcm_dequant_add</c>.</summary>
    private static void ApcmDequantAdd(
        ref GsmBitReader gb, Span<short> refBuf, int dst, ReadOnlySpan<byte> frameBits)
    {
        int maxidx = gb.GetBits(6);
        ReadOnlySpan<short> tab = GsmDecData.GsmDequantTab.Slice(maxidx * 8, 8);
        for (int i = 0; i < 13; i++)
        {
            int val = gb.GetBits(frameBits[i]);
            refBuf[dst + 3 * i] +=
                tab[GsmDecData.GsmRequantTab[frameBits[i] * 8 + val]];
        }
    }

    /// <summary>
    /// <c>gsm_mult</c>. The multiply is unsigned so it WRAPS rather than overflowing, and the
    /// shift that follows is arithmetic on the reinterpreted signed value.
    /// </summary>
    private static int GsmMult(int a, int b) =>
        unchecked((int)((uint)a * (uint)b + (1u << 14))) >> 15;

    /// <summary><c>long_term_synth</c>: the pitch predictor, reaching back by the lag.</summary>
    private static void LongTermSynth(Span<short> refBuf, int dst, int lag, int gainIdx)
    {
        int src = dst - lag;
        ushort gain = GsmDecData.GsmLongTermGainTab[gainIdx];
        for (int i = 0; i < 40; i++)
            refBuf[dst + i] = (short)GsmMult(gain, refBuf[src + i]);
    }

    /// <summary><c>decode_log_area</c>.</summary>
    private static int DecodeLogArea(int coded, int factor, int offset)
    {
        coded <<= 10;
        coded -= offset;
        return GsmMult(coded, factor) * 2;
    }

    /// <summary><c>get_rrp</c>: a log-area ratio into a reflection coefficient.</summary>
    private static int GetRrp(int filtered)
    {
        int abs = Math.Abs(filtered);
        if (abs < 11059) abs <<= 1;
        else if (abs < 20070) abs += 11059;
        else abs = (abs >> 2) + 26112;
        return filtered < 0 ? -abs : abs;
    }

    /// <summary><c>filter_value</c>: one sample through the eight-stage lattice.</summary>
    private static int FilterValue(int input, ReadOnlySpan<int> rrp, Span<int> v)
    {
        for (int i = 7; i >= 0; i--)
        {
            input -= GsmMult(rrp[i], v[i]);
            v[i + 1] = v[i] + GsmMult(rrp[i], input);
        }
        v[0] = input;
        return input;
    }

    /// <summary>
    /// <c>short_term_synth</c>. The reflection coefficients are interpolated between the
    /// previous block's log-area ratios and this block's over the first 27 samples, in three
    /// steps, and the remaining 133 use this block's alone.
    /// </summary>
    private static void ShortTermSynth(
        GsmContext ctx, Span<short> dst, ReadOnlySpan<short> src)
    {
        Span<int> rrp = stackalloc int[8];
        int[] lar = ctx.Lar[ctx.LarIdx];
        int[] larPrev = ctx.Lar[ctx.LarIdx ^ 1];

        for (int i = 0; i < 8; i++)
            rrp[i] = GetRrp((larPrev[i] >> 2) + (larPrev[i] >> 1) + (lar[i] >> 2));
        for (int i = 0; i < 13; i++)
            dst[i] = (short)FilterValue(src[i], rrp, ctx.V);

        for (int i = 0; i < 8; i++)
            rrp[i] = GetRrp((larPrev[i] >> 1) + (lar[i] >> 1));
        for (int i = 13; i < 27; i++)
            dst[i] = (short)FilterValue(src[i], rrp, ctx.V);

        for (int i = 0; i < 8; i++)
            rrp[i] = GetRrp((larPrev[i] >> 2) + (lar[i] >> 1) + (lar[i] >> 2));
        for (int i = 27; i < 40; i++)
            dst[i] = (short)FilterValue(src[i], rrp, ctx.V);

        for (int i = 0; i < 8; i++)
            rrp[i] = GetRrp(lar[i]);
        for (int i = 40; i < 160; i++)
            dst[i] = (short)FilterValue(src[i], rrp, ctx.V);

        ctx.LarIdx ^= 1;
    }

    /// <summary><c>postprocess</c>: de-emphasis, a doubling and three bits thrown away.</summary>
    private static int Postprocess(Span<short> data, int msr)
    {
        for (int i = 0; i < 160; i++)
        {
            msr = AvClipInt16(data[i] + GsmMult(msr, 28180));
            data[i] = (short)(AvClipInt16(msr * 2) & ~7);
        }
        return msr;
    }

    /// <summary><c>av_clip_int16</c>.</summary>
    private static int AvClipInt16(int value) =>
        value < short.MinValue ? short.MinValue : (value > short.MaxValue ? short.MaxValue : value);

    /// <summary>
    /// <c>gsm_decode_block</c>: eight log-area ratios, four subframes of excitation, then the
    /// two synthesis filters and the de-emphasis.
    /// </summary>
    /// <param name="ctx">The running state.</param>
    /// <param name="samples">160 samples of output.</param>
    /// <param name="gb">The bitstream, positioned at the start of a block.</param>
    /// <param name="mode">Which bit allocation the block uses; see <see cref="GsmMode"/>.</param>
    public static void GsmDecodeBlock(
        GsmContext ctx, Span<short> samples, ref GsmBitReader gb, int mode)
    {
        short[] refBuf = ctx.RefBuf;
        int refDst = 120;
        int[] lar = ctx.Lar[ctx.LarIdx];

        lar[0] = DecodeLogArea(gb.GetBits(6), 13107, 1 << 15);
        lar[1] = DecodeLogArea(gb.GetBits(6), 13107, 1 << 15);
        lar[2] = DecodeLogArea(gb.GetBits(5), 13107, (1 << 14) + 2048 * 2);
        lar[3] = DecodeLogArea(gb.GetBits(5), 13107, (1 << 14) - 2560 * 2);
        lar[4] = DecodeLogArea(gb.GetBits(4), 19223, (1 << 13) + 94 * 2);
        lar[5] = DecodeLogArea(gb.GetBits(4), 17476, (1 << 13) - 1792 * 2);
        lar[6] = DecodeLogArea(gb.GetBits(3), 31454, (1 << 12) - 341 * 2);
        lar[7] = DecodeLogArea(gb.GetBits(3), 29708, (1 << 12) - 1144 * 2);

        for (int i = 0; i < 4; i++)
        {
            int lag = gb.GetBits(7);
            int gainIdx = gb.GetBits(2);
            int offset = gb.GetBits(2);
            lag = Math.Clamp(lag, 40, 120);
            LongTermSynth(refBuf, refDst, lag, gainIdx);
            ApcmDequantAdd(ref gb, refBuf, refDst + offset, GsmDecData.GsmApcmBits(mode, i));
            refDst += 40;
        }

        // The tail of this block becomes the history the next one's long term predictor
        // reaches into. 120 is the largest lag.
        Array.Copy(refBuf, 160, refBuf, 0, 120);
        ShortTermSynth(ctx, samples, refBuf.AsSpan(120, 160));

        // for optimal speed this could be merged with short_term_synth,
        // not done yet because it is a bit ugly
        ctx.Msr = Postprocess(samples, ctx.Msr);
    }
}

/// <summary>
/// <c>get_bits.h</c> reduced to the one call the GSM decoder makes, in both bit orders:
/// <c>get_bits</c> with and without <c>BITSTREAM_READER_LE</c>.
/// </summary>
/// <remarks>
/// A field is at most 7 bits, so this reads bit by bit rather than keeping the C's 32-bit
/// cache. Reading past the end returns zero bits, which is what <c>init_get_bits</c>'s size
/// check gives the C; a block is a fixed size the caller has already checked, so a well
/// formed stream never reaches it.
/// </remarks>
public ref struct GsmBitReader
{
    private readonly ReadOnlySpan<byte> buffer;
    private readonly bool littleEndian;
    private int bitsRead;

    /// <summary>Creates a reader over one block.</summary>
    /// <param name="buffer">The block.</param>
    /// <param name="packedLittleEndian">
    /// True for the Microsoft variant, which is <c>BITSTREAM_READER_LE</c>; false for plain
    /// GSM.
    /// </param>
    public GsmBitReader(ReadOnlySpan<byte> buffer, bool packedLittleEndian)
    {
        this.buffer = buffer;
        littleEndian = packedLittleEndian;
    }

    /// <summary><c>get_bits_count</c>.</summary>
    public int BitsRead => bitsRead;

    /// <summary><c>get_bits(n)</c>, in whichever bit order this reader was made for.</summary>
    public int GetBits(int n)
    {
        int value = 0;
        if (littleEndian)
        {
            for (int i = 0; i < n; i++)
            {
                int index = bitsRead + i;
                int bit = (index >> 3) < buffer.Length
                    ? (buffer[index >> 3] >> (index & 7)) & 1
                    : 0;
                value |= bit << i;
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                int index = bitsRead + i;
                int bit = (index >> 3) < buffer.Length
                    ? (buffer[index >> 3] >> (7 - (index & 7))) & 1
                    : 0;
                value = (value << 1) | bit;
            }
        }
        bitsRead += n;
        return value;
    }
}

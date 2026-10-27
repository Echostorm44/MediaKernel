// The USAC spectral arithmetic coder: a port of ffmpeg-master/libavcodec/aac/aacdec_ac.c.
//
// USAC codes its spectrum with a context-adaptive arithmetic coder rather than Huffman books.
// The context for each coefficient pair is built from the pair before it and from the SAME pair
// of the previous frame, so the state is carried across frames and a decoder that resets it at
// the wrong moment produces a frame that is plausible at the start and noise by the end.
//
// Two things in here look like candidates for tidying and are not:
//
//  * ff_aac_ac_decode's binary searches are hand-unrolled PER CDF LENGTH, with guards a
//    textbook search does not have - the 17-entry case tests whether the first probability is
//    met at all before descending, and the 27-entry case skips two of its steps at specific
//    positions. Those exist because 17 and 27 are not powers of two, so a uniform descent would
//    step past the end of the table. Written as the C writes them.
//
//  * ff_aac_ac_get_pk binary-searches ff_aac_ac_hash_m with i_min starting at -1 and returns
//    ff_aac_ac_lookup_m[i_max] when the loop ends without an exact hit. The two tables are one
//    structure; the search is not a plain lower_bound and does not simplify to one.

using System;

namespace MediaKernel.Core.Codecs.AAC;

/// <summary>
/// <c>AACArithState</c>: the arithmetic coder's context, carried from one frame to the next.
/// </summary>
public sealed class AacArithState
{
    /// <summary><c>last[512 + 1]</c>: the previous frame's per-pair magnitudes, 2048/4 of them.</summary>
    /// <remarks>
    /// The spare entry at the end is not padding and must not be trimmed: 
    /// <see cref="AacArithCoder.GetContext"/> reads <c>last[i + 1]</c>, and the last pair of a
    /// 1024-coefficient spectrum has <c>i</c> of 511. FFmpeg writes the size as
    /// <c>512 /* 2048 / 4 */ + 1</c> for exactly that reason. The scratch copy inside
    /// <see cref="AacArithCoder.MapProcess"/> is 512 and deliberately one shorter.
    /// </remarks>
    public readonly byte[] Last = new byte[512 + 1];

    /// <summary><c>last_len</c>: the transform length <see cref="Last"/> was filled at.</summary>
    public int LastLen;

    /// <summary><c>cur[4]</c>: the magnitudes of the four most recent pairs in this frame.</summary>
    public readonly byte[] Cur = new byte[4];

    /// <summary><c>state_pre</c>: the context accumulator, which carries between pairs.</summary>
    /// <remarks>
    /// Sixteen bits, as the C declares it, and the width is exactly enough rather than a
    /// truncation: <c>cur[0]</c> is clamped to <c>0xF</c> and <see cref="Last"/> never holds
    /// anything larger, so the accumulator is at most
    /// <c>((0xFF + (0xF &lt;&lt; 8)) &lt;&lt; 4) + 0xF</c>, which is 65535. Nothing is lost here
    /// and nothing would be gained by widening it; it is this width because the C is, and the
    /// bound is worth knowing so nobody has to wonder whether a wrap is being relied on.
    /// </remarks>
    public ushort StatePre;

    /// <summary>Clears everything, for a decoder reset rather than a frame boundary.</summary>
    public void Reset()
    {
        Array.Clear(Last);
        Array.Clear(Cur);
        LastLen = 0;
        StatePre = 0;
    }
}

/// <summary>
/// <c>AACArith</c>: the arithmetic decoder's interval, live for one spectrum.
/// </summary>
public struct AacArith
{
    /// <summary><c>low</c>, <c>high</c>: the current interval, 16 bits wide.</summary>
    /// <remarks>
    /// Sixteen bits as the C declares them. The decoder works in int locals and stores back
    /// here each symbol; the renormalisation loop runs until the interval fits, so the stores
    /// are in range and the narrowing never discards anything. Declared at the C's width so
    /// the struct is the C's struct, not because a wrap is wanted.
    /// </remarks>
    public ushort Low, High;

    /// <summary><c>val</c>: the window of bitstream being matched against the interval.</summary>
    public ushort Val;
}

/// <summary>
/// The USAC spectral arithmetic coder.
/// </summary>
public static class AacArithCoder
{
    /// <summary><c>FF_AAC_AC_ESCAPE</c>: the MSB symbol that means "another level follows".</summary>
    public const int Escape = 16;

    /// <summary>
    /// <c>ff_aac_ac_map_process</c>: fold the previous frame's magnitudes to this frame's
    /// transform length and start a new context.
    /// </summary>
    /// <remarks>
    /// The rescale is a nearest-neighbour decimation or expansion by <c>last_len / N</c>, done
    /// through a copy because it reads and writes the same array. It only runs when the length
    /// actually changed - a frame at the same length as its predecessor keeps the magnitudes
    /// untouched, which is what makes the context useful.
    /// </remarks>
    public static uint MapProcess(AacArithState state, bool reset, int n)
    {
        if (reset)
        {
            Array.Clear(state.Last);
            state.LastLen = n;
        }
        else if (state.LastLen != n)
        {
            // Through a copy: the loop reads state.Last[k] for k derived from i, and k can be
            // ahead of i when the previous frame was longer.
            // 512, not Last.Length: the C copies sizeof(last) bytes into a 512-byte local and
            // never reads the spare entry here.
            Span<byte> last = stackalloc byte[512];
            state.Last.AsSpan(0, 512).CopyTo(last);

            float ratio = state.LastLen / (float)n;
            int i = 0;
            for (; i < n / 2; i++)
            {
                int k = (int)(i * ratio);
                state.Last[i] = last[k];
            }

            for (; i < state.Last.Length; i++)
                state.Last[i] = 0;

            state.LastLen = n;
        }

        state.Cur[3] = 0;
        state.Cur[2] = 0;
        state.Cur[1] = 0;
        state.Cur[0] = 1;

        state.StatePre = (ushort)(state.Last[0] << 12);
        return (uint)(state.Last[0] << 12);
    }

    /// <summary>
    /// <c>ff_aac_ac_get_context</c>: the context for pair <paramref name="i"/>.
    /// </summary>
    /// <remarks>
    /// Built from three places at once: the running accumulator shifted down by 8, the NEXT
    /// pair's magnitude in the previous frame (<c>last[i + 1]</c>, not <c>last[i]</c>), and the
    /// previous pair's magnitude in this one. The <c>+ 0x10000</c> on a quiet run selects a
    /// different band of probability tables; it is deliberately not folded into the accumulator,
    /// because the accumulator carries to the next pair and this does not.
    ///
    /// <para>The returned context is not the stored one: the <c>+ 0x10000</c> pushes the result
    /// past sixteen bits, which is precisely why it cannot be folded into
    /// <see cref="AacArithState.StatePre"/> - that field holds sixteen, and the band selection
    /// must not survive into the next pair.</para>
    /// </remarks>
    public static uint GetContext(AacArithState state, uint c, int i, int n)
    {
        c = (uint)(state.StatePre >> 8);
        c += (uint)(state.Last[i + 1] << 8);
        c <<= 4;
        c += state.Cur[1];

        state.StatePre = (ushort)c;

        if (i > 3 && state.Cur[3] + state.Cur[2] + state.Cur[1] < 5)
            return c + 0x10000;

        return c;
    }

    /// <summary>
    /// <c>ff_aac_ac_get_pk</c>: which cumulative frequency table a context selects.
    /// </summary>
    public static uint GetPk(uint c)
    {
        ReadOnlySpan<uint> hash = AacArithTables.HashM;
        ReadOnlySpan<byte> lookup = AacArithTables.LookupM;

        int iMin = -1;
        int iMax = lookup.Length - 1;
        while (iMax - iMin > 1)
        {
            int i = iMin + (iMax - iMin) / 2;
            uint j = hash[i];
            if (c < j >> 8)
                iMax = i;
            else if (c > j >> 8)
                iMin = i;
            else
                return j & 0xFF;
        }

        return lookup[iMax];
    }

    /// <summary>
    /// <c>ff_aac_ac_update_context</c>: record a decoded pair's magnitude.
    /// </summary>
    public static void UpdateContext(AacArithState state, int idx, int a, int b)
    {
        state.Cur[0] = (byte)Math.Min(a + b + 1, 0xF);
        state.Cur[3] = state.Cur[2];
        state.Cur[2] = state.Cur[1];
        state.Cur[1] = state.Cur[0];

        state.Last[idx] = state.Cur[0];
    }

    /// <summary><c>ff_aac_ac_init</c>: prime the interval and read the first 16 bits.</summary>
    public static void Init(out AacArith ac, ref AacBitReader reader)
    {
        ac.Low = 0;
        ac.High = ushort.MaxValue;
        ac.Val = (ushort)reader.ReadBits(16);
    }

    /// <summary>
    /// <c>ff_aac_ac_decode</c>: one symbol from the given cumulative frequency table.
    /// </summary>
    /// <remarks>
    /// <paramref name="cdf"/> is indexed from -1 in the C, as <c>p = cdf - 1</c>, so every
    /// subscript here is one higher than the C's and <c>p</c> is carried as an offset rather
    /// than a pointer. The unrolled searches and their guards are the C's, for the reason given
    /// at the top of this file.
    /// </remarks>
    public static int Decode(ref AacArith ac, ref AacBitReader reader, ReadOnlySpan<ushort> cdf)
    {
        int val = ac.Val;
        int low = ac.Low;
        int high = ac.High;

        int rng = high - low + 1;
        int c = ((val - low + 1) << 14) - 1;

        // p is the C's pointer as an offset into cdf, biased by -1.
        int p = -1;

        switch (cdf.Length)
        {
            case 2:
                if (cdf[p + 1] * rng > c)
                    p += 1;
                break;

            case 4:
                if (cdf[p + 2] * rng > c)
                    p += 2;
                if (cdf[p + 1] * rng > c)
                    p += 1;
                break;

            case 17:
                // Does the first probability get met at all? If not there is nothing to descend
                // into, and descending anyway would read past the table.
                if (cdf[p + 1] * rng <= c)
                    break;
                p += 1;
                for (int i = 8; i >= 1; i >>= 1)
                    if (cdf[p + i] * rng > c)
                        p += i;
                break;

            case 27:
                if (cdf[p + 16] * rng > c)
                    p += 16;
                if (cdf[p + 8] * rng > c)
                    p += 8;
                if (p != -1 + 24)
                    if (cdf[p + 4] * rng > c)
                        p += 4;
                if (cdf[p + 2] * rng > c)
                    p += 2;
                if (p != -1 + 24 + 2)
                    if (cdf[p + 1] * rng > c)
                        p += 1;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(cdf), cdf.Length,
                    "The USAC arithmetic coder has cumulative frequency tables of 2, 4, 17 and "
                  + "27 entries only; any other length means the caller passed the wrong table.");
        }

        int sym = p + 1;
        if (sym != 0)
            high = low + (rng * cdf[sym - 1] >> 14) - 1;
        low += rng * cdf[sym] >> 14;

        while (true)
        {
            if (high < 32768)
            {
                // Nothing: the interval is already in the low half.
            }
            else if (low >= 32768)
            {
                val -= 32768;
                low -= 32768;
                high -= 32768;
            }
            else if (low >= 16384 && high < 49152)
            {
                val -= 16384;
                low -= 16384;
                high -= 16384;
            }
            else
            {
                break;
            }

            low += low;
            high += high + 1;
            val = (val << 1) | (reader.ReadBit() ? 1 : 0);
        }

        ac.Low = (ushort)low;
        ac.High = (ushort)high;
        ac.Val = (ushort)val;

        return sym;
    }

    /// <summary>
    /// <c>ff_aac_ac_finish</c>: fill the magnitudes the spectrum stopped short of.
    /// </summary>
    /// <remarks>
    /// Ones up to N/2 and zeros beyond, not zeros throughout. The ones are what the next frame's
    /// context reads for the coefficients this frame did not transmit, and the boundary between
    /// the two regions is what <see cref="MapProcess"/> rescales.
    /// </remarks>
    public static void Finish(AacArithState state, int offset, int n)
    {
        int i = offset;
        for (; i < n / 2; i++)
            state.Last[i] = 1;

        for (; i < state.Last.Length; i++)
            state.Last[i] = 0;
    }
}

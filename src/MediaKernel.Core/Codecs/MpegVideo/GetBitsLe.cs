// The little-endian instantiation of get_bits.h.
//
// FFmpeg has ONE bit reader and compiles it twice. A codec whose file begins
// `#define BITSTREAM_READER_LE` before including get_bits.h gets an otherwise identical reader
// in which bits are consumed from the LOW end of each byte instead of the high end:
//
//     BE:  cache = AV_RB32(buffer + (index >> 3)) << (index & 7);   show(n) = cache >> (32 - n)
//     LE:  cache = AV_RL32(buffer + (index >> 3)) >> (index & 7);   show(n) = cache & ((1<<n)-1)
//
// DTS Express (dca_lbr.c) is the codec in this tree that needs it. C# has no preprocessor
// instantiation of a struct, so the two readers are two types - a recorded divergence, and the
// alternative was a mode flag with a branch in every accessor of the hot big-endian paths.
//
// What the LE order actually means, stated plainly because it is the part that goes wrong: bit
// k of the stream is `(buffer[k >> 3] >> (k & 7)) & 1`, and a read of n bits returns the first
// bit read as the LEAST significant of the result. Bytes are still read in order; it is only
// the direction within each byte that differs. That also makes get_bits_long's LE form
// - `get_bits(16) | (get_bits(n - 16) << 16)` - fall out rather than needing its own case.
//
// A VLC table read through this reader must have been built with VLC_INIT_LE, which lays the
// table out bit-reversed so that ShowBits indexes it directly; see Vlc.InitFromLengths. The two
// halves are not independently correct. A BE table read with this reader, or an LE table read
// with the BE one, decodes the short codes of a well-formed stream often enough to look
// plausible.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>
/// <c>GetBitContext</c> as compiled with <c>BITSTREAM_READER_LE</c> - a least-significant-bit-
/// first bit reader over a byte buffer.
/// </summary>
/// <remarks>
/// The field layout, the meaning of <see cref="Index"/> and the read-past-the-end behaviour are
/// <see cref="GetBitContext"/>'s and are documented there. Only the bit order differs.
/// </remarks>
public struct GetBitContextLe
{
    /// <summary>The buffer being read.</summary>
    public byte[]? Buffer;

    /// <summary>Bit position from the start of <see cref="Buffer"/>.</summary>
    public int Index;

    /// <summary><c>size_in_bits</c>.</summary>
    public int SizeInBits;

    /// <summary>The bit this reader was started at; see <see cref="GetBitContext.StartIndex"/>.</summary>
    public int StartIndex;

    /// <summary><c>init_get_bits8</c>.</summary>
    public void InitGetBits8(byte[] buffer, int byteSize)
        => InitGetBits8(buffer, 0, byteSize);

    /// <summary><c>init_get_bits8</c> over a window of a larger buffer.</summary>
    public void InitGetBits8(byte[] buffer, int byteOffset, int byteSize)
    {
        Buffer = buffer;
        StartIndex = byteOffset * 8;
        Index = StartIndex;
        SizeInBits = StartIndex + byteSize * 8;
    }

    /// <summary><c>get_bits_count</c>.</summary>
    public readonly int GetBitsCount() => Index - StartIndex;

    /// <summary><c>get_bits_left</c>.</summary>
    public readonly int GetBitsLeft() => SizeInBits - Index;

    /// <summary>
    /// The <paramref name="n"/> bits at <paramref name="bitIndex"/>, first bit read as the
    /// least significant.
    /// </summary>
    /// <remarks>
    /// Bit by bit rather than through a 32-bit load, which is what
    /// <see cref="GetBitContext"/> does too and for the same reason: a load would read past the
    /// end of the array on the last few bytes, where FFmpeg relies on its input buffers having
    /// <c>AV_INPUT_BUFFER_PADDING_SIZE</c> of slack that a C# array does not have. Reads past
    /// the end yield zero, as they do in the C.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly uint PeekAt(int bitIndex, int n)
    {
        if (n <= 0) return 0;

        uint value = 0;
        byte[]? buf = Buffer;
        int limit = SizeInBits;
        for (int i = 0; i < n; i++)
        {
            int at = bitIndex + i;
            uint bit = 0;
            if (buf is not null && at >= 0 && at < limit)
                bit = (uint)(buf[at >> 3] >> (at & 7)) & 1u;
            value |= bit << i;
        }
        return value;
    }

    /// <summary><c>get_bits</c> / <c>get_bits_long</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetBits(int n)
    {
        uint value = PeekAt(Index, n);
        Index += n;
        return value;
    }

    /// <summary><c>get_bits1</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetBits1()
    {
        uint value = PeekAt(Index, 1);
        Index++;
        return value;
    }

    /// <summary><c>show_bits</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint ShowBits(int n) => PeekAt(Index, n);

    /// <summary><c>skip_bits</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBits(int n) => Index += n;

    /// <summary><c>skip_bits1</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBits1() => Index++;

    /// <summary><c>get_sbits</c> / <c>get_sbits_long</c>.</summary>
    /// <remarks>
    /// Sign extension from bit <c>n - 1</c>, not from the top of the word: an LE read already
    /// has its most significant bit in position <c>n - 1</c>, so there is no shift up and back
    /// the way the big-endian reader needs.
    /// </remarks>
    public int GetSBits(int n)
    {
        if (n <= 0) return 0;
        uint v = GetBits(n);
        return n >= 32 ? (int)v : (int)(v << (32 - n)) >> (32 - n);
    }

    /// <summary><c>align_get_bits</c>.</summary>
    public int AlignGetBits()
    {
        int n = -GetBitsCount() & 7;
        if (n != 0)
            SkipBits(n);
        return n;
    }

    /// <summary>
    /// <c>get_unary</c> (unary.h) - count bits until <paramref name="stop"/>, at most
    /// <paramref name="len"/>.
    /// </summary>
    public int GetUnary(int stop, int len)
    {
        int i = 0;
        while (i < len && GetBits1() != stop)
            i++;
        return i;
    }

    /// <summary>
    /// <c>get_vlc2</c> over a table built with <c>VLC_INIT_LE</c>.
    /// </summary>
    /// <remarks>
    /// Identical in shape to <see cref="GetBitContext.GetVlc2"/>, including the ordering trap
    /// documented there - the next level's index is formed from the OLD symbol, so the symbol is
    /// read after the index is computed. What makes this version correct is not the code but the
    /// table: it must have been built bit-reversed, by
    /// <c>Vlc.InitFromLengths(..., littleEndian: true)</c>. There is no way to detect at run
    /// time that it was not.
    /// </remarks>
    public int GetVlc2(ReadOnlySpan<VLCElem> table, int bits, int maxDepth)
    {
        int index = (int)ShowBits(bits);
        int code = table[index].Sym;
        int n = table[index].Len;

        if (maxDepth > 1 && n < 0)
        {
            SkipBits(bits);
            int nbBits = -n;

            index = (int)ShowBits(nbBits) + code;
            code = table[index].Sym;
            n = table[index].Len;

            if (maxDepth > 2 && n < 0)
            {
                SkipBits(nbBits);
                nbBits = -n;

                index = (int)ShowBits(nbBits) + code;
                code = table[index].Sym;
                n = table[index].Len;
            }
        }

        SkipBits(n);
        return code;
    }
}

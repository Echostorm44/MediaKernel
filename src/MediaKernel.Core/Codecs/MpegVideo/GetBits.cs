// Bit reader - a translation of the parts of ffmpeg-master/libavcodec/get_bits.h that the
// MPEG-video decoders use.
//
// Not the whole header: the MPEG-2 paths call thirteen of its accessors, counted across
// mpeg12dec.c, mpeg12.c, mpegvideo*.c and mpeg12dec.h, and those are what is here. The rest of
// get_bits.h serves codecs that are not in scope.
//
// Why not reuse Codecs/H264/BitstreamReader: it is a `ref struct` over a ReadOnlySpan, so it
// cannot be a field of Mpeg12SliceContext, which is exactly where FFmpeg puts its GetBitContext.
// It also speaks H.264's exp-Golomb rather than MPEG's VLC and marker bits. FFmpeg's own
// GetBitContext is a buffer pointer plus a bit index, which translates to an array plus an index
// with no such problem, so this follows the C.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>
/// <c>GetBitContext</c> - a most-significant-bit-first bit reader over a byte buffer.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Index"/> counts bits from the start of <see cref="Buffer"/>, which is what makes
/// <c>get_bits_count</c> and <c>get_bits_left</c> trivial and is why FFmpeg stores the position
/// that way rather than as a byte pointer and a shift.
/// </para>
/// <para>
/// Reads past the end return zero rather than throwing. That is FFmpeg's behaviour and it is not
/// laziness: a truncated or corrupt stream is normal input for a decoder, and the slice loop
/// detects it by checking <c>get_bits_left</c>, not by catching an exception from the reader.
/// </para>
/// </remarks>
public struct GetBitContext
{
    /// <summary>The buffer being read.</summary>
    public byte[]? Buffer;

    /// <summary>Bit position from the start of <see cref="Buffer"/>.</summary>
    public int Index;

    /// <summary><c>size_in_bits</c>.</summary>
    public int SizeInBits;

    /// <summary>
    /// The bit this reader was started at, which is zero unless it was given a window of a
    /// larger buffer.
    /// </summary>
    /// <remarks>
    /// It exists only so that <see cref="GetBitsCount"/> can mean what the C's
    /// <c>get_bits_count</c> means. In the C the buffer pointer IS the start, so the count is
    /// naturally relative to it; here the start may be partway into an array, and a count that
    /// included the offset would silently corrupt every caller that turns it into a byte length.
    /// </remarks>
    public int StartIndex;

    /// <summary>
    /// <c>init_get_bits8</c> - the byte-sized initialiser, which is the one these decoders use.
    /// </summary>
    public void InitGetBits8(byte[] buffer, int byteSize)
    {
        Buffer = buffer;
        Index = 0;
        StartIndex = 0;
        SizeInBits = byteSize * 8;
    }

    /// <summary>
    /// <c>init_get_bits8</c> over a window of <paramref name="buffer"/>, which is what the C's
    /// pointer-and-length argument really is.
    /// </summary>
    /// <remarks>
    /// This exists so that a parser handed part of a larger buffer does not have to copy it out.
    /// <see cref="Index"/> already counts bits from the start of the array, so a window costs
    /// nothing to express: the start becomes a bit index and the end becomes
    /// <see cref="SizeInBits"/>. Every <c>get_bits_left</c> and <c>get_bits_count</c> then
    /// reads the same as it would over a copy.
    /// </remarks>
    public void InitGetBits8(byte[] buffer, int byteOffset, int byteSize)
    {
        Buffer = buffer;
        Index = byteOffset * 8;
        StartIndex = Index;
        SizeInBits = (byteOffset + byteSize) * 8;
    }

    /// <summary><c>get_bits_count</c> - bits read since the reader was started.</summary>
    public readonly int GetBitsCount() => Index - StartIndex;

    /// <summary><c>get_bits_left</c> - what the slice loops test to notice a truncated stream.</summary>
    public readonly int GetBitsLeft() => SizeInBits - Index;

    /// <summary>
    /// Reads <paramref name="n"/> bits at <paramref name="bitIndex"/> without touching
    /// <see cref="Index"/>. Bits past the end read as zero.
    /// </summary>
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
                bit = (uint)(buf[at >> 3] >> (7 - (at & 7))) & 1u;
            value = (value << 1) | bit;
        }
        return value;
    }

    /// <summary><c>get_bits</c> - reads <paramref name="n"/> bits, unsigned.</summary>
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

    /// <summary><c>show_bits</c> - peeks <paramref name="n"/> bits without consuming them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint ShowBits(int n) => PeekAt(Index, n);

    /// <summary><c>skip_bits</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBits(int n) => Index += n;

    /// <summary><c>skip_bits1</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBits1() => Index++;

    /// <summary>
    /// <c>get_sbits</c> - reads <paramref name="n"/> bits and sign-extends from bit
    /// <paramref name="n"/>, so the top bit read is the sign.
    /// </summary>
    public int GetSBits(int n)
    {
        if (n <= 0) return 0;
        uint value = GetBits(n);
        // Sign-extend: if the top of the n-bit field is set, fill above it with ones.
        int shift = 32 - n;
        return (int)(value << shift) >> shift;
    }

    /// <summary>
    /// <c>get_xbits</c> - MPEG's DC differential coding, where a leading zero means the value is
    /// negative and is biased rather than two's complement.
    /// </summary>
    /// <remarks>
    /// FFmpeg computes this branchlessly over a 32-bit cache; written out, the result for an
    /// n-bit field <c>v</c> is <c>v</c> when the leading bit is set and <c>v - 2^n + 1</c> when it
    /// is not. The second form is the part worth stating, because it is not sign extension: a
    /// field of all zeros decodes to <c>1 - 2^n</c>, not to zero.
    /// </remarks>
    public int GetXBits(int n)
    {
        if (n <= 0) return 0;
        uint v = GetBits(n);
        bool positive = (v & (1u << (n - 1))) != 0;
        return positive ? (int)v : (int)v - (1 << n) + 1;
    }

    /// <summary>
    /// <c>align_get_bits</c> - advances to the next byte boundary. FFmpeg returns the aligned
    /// pointer; callers here want the byte offset, so that is what comes back.
    /// </summary>
    public int AlignGetBits()
    {
        int n = -Index & 7;
        if (n != 0) SkipBits(n);
        return Index >> 3;
    }

    /// <summary>
    /// <c>get_vlc2</c> - decodes one variable-length code through a table built by
    /// <see cref="Vlc.InitTableSparse"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first level is <paramref name="bits"/> wide. An entry whose length is negative means
    /// the code is longer than that: <c>-len</c> further bits are read and added to the entry's
    /// symbol, which is the base index of a subtable. <paramref name="maxDepth"/> caps how often
    /// that may happen, and FFmpeg passes a constant so the chain unrolls - 1 for tables whose
    /// codes always fit, 2 or 3 for the coefficient tables.
    /// </para>
    /// <para>
    /// The index for the next level has to be formed from the OLD symbol, so the symbol is read
    /// after the index is computed and not before. Getting that order wrong still decodes every
    /// short code correctly and only corrupts the long ones, which is the kind of bug that
    /// survives a casual test and ruins high-detail blocks.
    /// </para>
    /// <para>
    /// An invalid code has length 0 and symbol -1; nothing is consumed and -1 comes back, which
    /// is what the callers test for.
    /// </para>
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

    /// <summary>
    /// <c>GET_RL_VLC</c> - decodes one run/level pair from a run-length VLC table.
    /// </summary>
    /// <remarks>
    /// The same two-level walk as <see cref="GetVlc2"/>, but the entry is read through the
    /// run-length view of <see cref="VLCElem"/>: the level and run come out of the table rather
    /// than a symbol that would need another lookup. A level of 127 is the end-of-block
    /// sentinel and a run of 65 marks escape or an illegal code, both of which the coefficient
    /// loops test for.
    /// </remarks>
    public void GetRlVlc(ReadOnlySpan<VLCElem> table, int bits, int maxDepth, out int level, out int run)
    {
        int index = (int)ShowBits(bits);
        level = table[index].Level;
        int n = table[index].Len8;

        if (maxDepth > 1 && n < 0)
        {
            SkipBits(bits);
            int nbBits = -n;

            index = (int)ShowBits(nbBits) + level;
            level = table[index].Level;
            n = table[index].Len8;

            if (maxDepth > 2 && n < 0)
            {
                SkipBits(nbBits);
                nbBits = -n;

                index = (int)ShowBits(nbBits) + level;
                level = table[index].Level;
                n = table[index].Len8;
            }
        }

        run = table[index].Run;
        SkipBits(n);
    }

    /// <summary>
    /// <c>check_marker</c> - reads a bit that the syntax requires to be 1. A zero means the
    /// stream is not where the parser thinks it is.
    /// </summary>
    /// <returns><see langword="true"/> when the marker bit was set.</returns>
    public bool CheckMarker() => GetBits1() != 0;

    /// <summary>
    /// <c>get_unary</c> (unary.h) - count bits until one differs from <paramref name="stop"/>,
    /// up to <paramref name="len"/>.
    /// </summary>
    public int GetUnary(int stop, int len)
    {
        int i;
        for (i = 0; i < len && GetBits1() != stop; i++) { }
        return i;
    }

    /// <summary><c>decode012</c> - a truncated unary code for the values 0, 1, 2.</summary>
    public int Decode012() => GetBits1() == 0 ? 0 : (int)GetBits1() + 1;

    /// <summary><c>decode210</c> - a truncated unary code for the values 2, 1, 0.</summary>
    public int Decode210() => GetBits1() != 0 ? 0 : 2 - (int)GetBits1();
}

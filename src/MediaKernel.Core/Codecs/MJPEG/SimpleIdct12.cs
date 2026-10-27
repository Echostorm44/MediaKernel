// Ported from FFmpeg's libavcodec/simple_idct_template.c at BIT_DEPTH 12, which is the
// instantiation ff_simple_idct_put_int16_12bit comes from.
//
// A second copy of an algorithm already in IdctDsp, and deliberately so: the C builds this
// file twice with different constants and this is the other build. The alternative - making
// IdctDsp generic over the bit depth - would put a type parameter on the inner loop of every
// 8-bit decoder in the project, all of which are conformant and none of which needs it. The
// porting rules allow generics over static abstract members in place of a #define template;
// they do not require it where the C itself just instantiates twice.
//
// What differs from the 8-bit instantiation, and it is only this:
//
//   the W constants are twice the 8-bit ones - 45451 against 22725, and so on
//   ROW_SHIFT is 16, COL_SHIFT 17, DC_SHIFT -1, where 8-bit has 11, 20, 3
//   DC_SHIFT being NEGATIVE makes the all-DC shortcut a rounding RIGHT shift, not a left one
//   a pixel is a 16-bit sample clipped to 12 bits, not a byte clipped to 8
//
// The first attempt used the BIT_DEPTH 10 constants, which live in the same #elif arm as 12
// and differ in every one of those lines. It decoded a recognisable picture wrong by small
// amounts, which is the kind of error only a reference catches.
//
// The structure, the butterfly and the sparse-column tests are the same, so this file reads
// as the 8-bit one does.

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Mjpeg;

/// <summary>
/// <c>simple_idct_template.c</c> at <c>BIT_DEPTH 12</c> - the inverse transform a 12-bit JPEG
/// is decoded with.
/// </summary>
public static class SimpleIdct12
{
    private const int W1 = 45451;
    private const int W2 = 42813;
    private const int W3 = 38531;
    private const int W4 = 32767;
    private const int W5 = 25746;
    private const int W6 = 17734;
    private const int W7 = 9041;

    private const int RowShift = 16;
    private const int ColShift = 17;

    /// <summary>
    /// <c>DC_SHIFT</c>, which is NEGATIVE one at this depth - the all-DC shortcut shifts
    /// RIGHT by one with rounding, where the 8- and 10-bit instantiations shift left.
    /// </summary>
    private const int DcShift = -1;

    /// <summary>The largest value a 12-bit sample can take.</summary>
    private const int MaxPixel = (1 << 12) - 1;

    /// <summary>
    /// <c>ff_simple_idct_put_int16_12bit</c> - transform and write the result as 12-bit
    /// samples in 16-bit little-endian lanes.
    /// </summary>
    /// <param name="dest">The plane.</param>
    /// <param name="offset">Where the block starts, in BYTES.</param>
    /// <param name="lineSizeBytes">The plane's stride, in BYTES.</param>
    /// <param name="block">The coefficients, destroyed in place.</param>
    public static void Put(Span<byte> dest, int offset, int lineSizeBytes, Span<short> block)
    {
        for (int i = 0; i < 8; i++)
            IdctRowCondDc(block.Slice(i * 8, 8));

        for (int col = 0; col < 8; col++)
        {
            IdctCols(block, col, out uint a0, out uint a1, out uint a2, out uint a3,
                                 out uint b0, out uint b1, out uint b2, out uint b3);

            int at = offset + 2 * col;
            Write(dest, at, (int)(a0 + b0) >> ColShift); at += lineSizeBytes;
            Write(dest, at, (int)(a1 + b1) >> ColShift); at += lineSizeBytes;
            Write(dest, at, (int)(a2 + b2) >> ColShift); at += lineSizeBytes;
            Write(dest, at, (int)(a3 + b3) >> ColShift); at += lineSizeBytes;
            Write(dest, at, (int)(a3 - b3) >> ColShift); at += lineSizeBytes;
            Write(dest, at, (int)(a2 - b2) >> ColShift); at += lineSizeBytes;
            Write(dest, at, (int)(a1 - b1) >> ColShift); at += lineSizeBytes;
            Write(dest, at, (int)(a0 - b0) >> ColShift);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write(Span<byte> dest, int at, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(dest[at..], ClipPixel(value));

    /// <summary><c>av_clip_pixel</c> at 12 bits, which is <c>av_clip_uintp2(x, 12)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ClipPixel(int v) =>
        v < 0 ? (ushort)0 : v > MaxPixel ? (ushort)MaxPixel : (ushort)v;

    /// <summary>
    /// <c>idctRowCondDC</c> - one row, with the all-DC shortcut the C has.
    /// </summary>
    /// <remarks>
    /// The DC shortcut writes a 16-bit truncation of <c>dc &lt;&lt; DC_SHIFT</c> to all eight
    /// entries, as the 8-bit instantiation does; the truncation is part of the result because
    /// the C writes through a 16-bit lane.
    /// </remarks>
    private static void IdctRowCondDc(Span<short> row)
    {
        if (row[1] == 0 && row[2] == 0 && row[3] == 0 &&
            row[4] == 0 && row[5] == 0 && row[6] == 0 && row[7] == 0)
        {
            // DC_SHIFT is -1 here, so the C takes its second branch: a rounding RIGHT
            // shift. Its & 0xffff is the 16-bit lane, which the cast is.
            short temp = (short)((row[0] + (1 << (-DcShift - 1))) >> -DcShift);
            row[0] = temp; row[1] = temp; row[2] = temp; row[3] = temp;
            row[4] = temp; row[5] = temp; row[6] = temp; row[7] = temp;
            return;
        }

        uint a0 = (uint)(W4 * row[0]) + (1u << (RowShift - 1));
        uint a1 = a0, a2 = a0, a3 = a0;

        a0 += (uint)(W2 * row[2]);
        a1 += (uint)(W6 * row[2]);
        a2 -= (uint)(W6 * row[2]);
        a3 -= (uint)(W2 * row[2]);

        uint b0 = (uint)(W1 * row[1]);
        b0 += (uint)(W3 * row[3]);
        uint b1 = (uint)(W3 * row[1]);
        b1 += (uint)(-W7 * row[3]);
        uint b2 = (uint)(W5 * row[1]);
        b2 += (uint)(-W1 * row[3]);
        uint b3 = (uint)(W7 * row[1]);
        b3 += (uint)(-W5 * row[3]);

        if (row[4] != 0 || row[5] != 0 || row[6] != 0 || row[7] != 0)
        {
            a0 += (uint)(W4 * row[4]) + (uint)(W6 * row[6]);
            a1 += (uint)(-W4 * row[4]) - (uint)(W2 * row[6]);
            a2 += (uint)(-W4 * row[4]) + (uint)(W2 * row[6]);
            a3 += (uint)(W4 * row[4]) - (uint)(W6 * row[6]);

            b0 += (uint)(W5 * row[5]);
            b0 += (uint)(W7 * row[7]);
            b1 += (uint)(-W1 * row[5]);
            b1 += (uint)(-W5 * row[7]);
            b2 += (uint)(W7 * row[5]);
            b2 += (uint)(W3 * row[7]);
            b3 += (uint)(W3 * row[5]);
            b3 += (uint)(-W1 * row[7]);
        }

        row[0] = (short)((int)(a0 + b0) >> RowShift);
        row[7] = (short)((int)(a0 - b0) >> RowShift);
        row[1] = (short)((int)(a1 + b1) >> RowShift);
        row[6] = (short)((int)(a1 - b1) >> RowShift);
        row[2] = (short)((int)(a2 + b2) >> RowShift);
        row[5] = (short)((int)(a2 - b2) >> RowShift);
        row[3] = (short)((int)(a3 + b3) >> RowShift);
        row[4] = (short)((int)(a3 - b3) >> RowShift);
    }

    /// <summary><c>IDCT_COLS</c> - the column butterfly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IdctCols(
        ReadOnlySpan<short> block, int col,
        out uint a0, out uint a1, out uint a2, out uint a3,
        out uint b0, out uint b1, out uint b2, out uint b3)
    {
        a0 = (uint)(W4 * (block[col + 8 * 0] + ((1 << (ColShift - 1)) / W4)));
        a1 = a0; a2 = a0; a3 = a0;

        a0 += (uint)(W2 * block[col + 8 * 2]);
        a1 += (uint)(W6 * block[col + 8 * 2]);
        a2 += (uint)(-W6 * block[col + 8 * 2]);
        a3 += (uint)(-W2 * block[col + 8 * 2]);

        b0 = (uint)(W1 * block[col + 8 * 1]);
        b1 = (uint)(W3 * block[col + 8 * 1]);
        b2 = (uint)(W5 * block[col + 8 * 1]);
        b3 = (uint)(W7 * block[col + 8 * 1]);

        b0 += (uint)(W3 * block[col + 8 * 3]);
        b1 += (uint)(-W7 * block[col + 8 * 3]);
        b2 += (uint)(-W1 * block[col + 8 * 3]);
        b3 += (uint)(-W5 * block[col + 8 * 3]);

        if (block[col + 8 * 4] != 0)
        {
            a0 += (uint)(W4 * block[col + 8 * 4]);
            a1 += (uint)(-W4 * block[col + 8 * 4]);
            a2 += (uint)(-W4 * block[col + 8 * 4]);
            a3 += (uint)(W4 * block[col + 8 * 4]);
        }

        if (block[col + 8 * 5] != 0)
        {
            b0 += (uint)(W5 * block[col + 8 * 5]);
            b1 += (uint)(-W1 * block[col + 8 * 5]);
            b2 += (uint)(W7 * block[col + 8 * 5]);
            b3 += (uint)(W3 * block[col + 8 * 5]);
        }

        if (block[col + 8 * 6] != 0)
        {
            a0 += (uint)(W6 * block[col + 8 * 6]);
            a1 += (uint)(-W2 * block[col + 8 * 6]);
            a2 += (uint)(W2 * block[col + 8 * 6]);
            a3 += (uint)(-W6 * block[col + 8 * 6]);
        }

        if (block[col + 8 * 7] != 0)
        {
            b0 += (uint)(W7 * block[col + 8 * 7]);
            b1 += (uint)(-W5 * block[col + 8 * 7]);
            b2 += (uint)(W3 * block[col + 8 * 7]);
            b3 += (uint)(-W1 * block[col + 8 * 7]);
        }
    }
}

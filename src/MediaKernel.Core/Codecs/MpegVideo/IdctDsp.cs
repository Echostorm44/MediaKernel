// Inverse DCT and the clamped pixel writers - a translation of
// ffmpeg-master/libavcodec/simple_idct_template.c at BIT_DEPTH 8, plus the parts of idctdsp.c the
// MPEG-video decoders use.
//
// ff_idctdsp_init picks ff_simple_idct_int16_8bit for eight-bit video at the default idct_algo,
// with FF_IDCT_PERM_NONE, so that is what is here. The other algorithms it can select (jref,
// xvid, faan) and the 9, 10 and 12-bit instantiations are out of scope; so is the lowres family,
// which only serves scaled-down decoding.
//
// Per the 2026-10-16 decision the function-pointer table is gone and these are static calls.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>
/// <c>enum idct_permutation_type</c> (<c>idctdsp.h:27</c>) - the coefficient order an IDCT
/// expects, which the scan tables are permuted into.
/// </summary>
public enum IdctPermutationType
{
    /// <summary><c>FF_IDCT_PERM_NONE</c> - plain raster order. What the simple IDCT uses.</summary>
    None = 0,
    Libmpeg2 = 1,
    Simple = 2,
    Transpose = 3,
    Partrans = 4,
    SseAuto = 5,
}

/// <summary>The inverse transform and the block-to-picture writers (<c>idctdsp.c</c>).</summary>
public static class IdctDsp
{
    // cos(i * pi / 16) * sqrt(2) * (1 << 14) + 0.5, as FFmpeg's comments say.
    private const int W1 = 22725;
    private const int W2 = 21407;
    private const int W3 = 19266;
    private const int W4 = 16383;
    private const int W5 = 12873;
    private const int W6 = 8867;
    private const int W7 = 4520;

    private const int RowShift = 11;
    private const int ColShift = 20;
    private const int DcShift = 3;

    /// <summary>
    /// <c>idctRowCondDC</c> - one row of the inverse transform, with a shortcut for a row whose
    /// only non-zero coefficient is its DC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The intermediate sums are unsigned here because they are <c>SUINT</c> in the C, and that
    /// is not incidental: the transform is allowed to overflow and relies on wrapping, which is
    /// defined for unsigned and undefined for signed in C. Each result is cast back to a signed
    /// int before the shift, so the shift is arithmetic on the wrapped value. Translating these
    /// as <c>int</c> would be right almost always and wrong on extreme coefficients.
    /// </para>
    /// <para>
    /// The DC shortcut writes a 16-bit truncation of <c>dc &lt;&lt; 3</c> to all eight entries.
    /// The truncation is part of the result, not an optimisation detail - the C writes through a
    /// 16-bit lane and so does this.
    /// </para>
    /// </remarks>
    private static void IdctRowCondDc(Span<short> row)
    {
        // The row is all-DC when every other coefficient is zero.
        if (row[1] == 0 && row[2] == 0 && row[3] == 0 &&
            row[4] == 0 && row[5] == 0 && row[6] == 0 && row[7] == 0)
        {
            short temp = (short)(row[0] * (1 << DcShift));
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

        // FFmpeg tests the upper half as one 64-bit read; the four coefficients are what it means.
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

    /// <summary>
    /// <c>IDCT_COLS</c> - the column butterfly shared by the three column passes, which differ
    /// only in what they do with the results.
    /// </summary>
    /// <remarks>
    /// The <c>if (col[8*n])</c> tests are FFmpeg's and are kept: a zero coefficient contributes
    /// nothing, and skipping it is worth more than the branch because most blocks are sparse
    /// after the row pass.
    /// </remarks>
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClipPixel(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

    /// <summary><c>ff_simple_idct_int16_8bit</c> - transform in place.</summary>
    public static void SimpleIdct(Span<short> block)
    {
        for (int i = 0; i < 8; i++)
            IdctRowCondDc(block.Slice(i * 8, 8));

        for (int col = 0; col < 8; col++)
        {
            IdctCols(block, col, out uint a0, out uint a1, out uint a2, out uint a3,
                                 out uint b0, out uint b1, out uint b2, out uint b3);

            block[col + 8 * 0] = (short)((int)(a0 + b0) >> ColShift);
            block[col + 8 * 1] = (short)((int)(a1 + b1) >> ColShift);
            block[col + 8 * 2] = (short)((int)(a2 + b2) >> ColShift);
            block[col + 8 * 3] = (short)((int)(a3 + b3) >> ColShift);
            block[col + 8 * 4] = (short)((int)(a3 - b3) >> ColShift);
            block[col + 8 * 5] = (short)((int)(a2 - b2) >> ColShift);
            block[col + 8 * 6] = (short)((int)(a1 - b1) >> ColShift);
            block[col + 8 * 7] = (short)((int)(a0 - b0) >> ColShift);
        }
    }

    /// <summary>
    /// <c>ff_simple_idct_put_int16_8bit</c> - transform and write the result as pixels, replacing
    /// whatever was there. Used for intra blocks, which have nothing to add to.
    /// </summary>
    public static void SimpleIdctPut(Span<byte> dest, int offset, int lineSize, Span<short> block)
    {
        for (int i = 0; i < 8; i++)
            IdctRowCondDc(block.Slice(i * 8, 8));

        for (int col = 0; col < 8; col++)
        {
            IdctCols(block, col, out uint a0, out uint a1, out uint a2, out uint a3,
                                 out uint b0, out uint b1, out uint b2, out uint b3);

            int at = offset + col;
            dest[at] = ClipPixel((int)(a0 + b0) >> ColShift); at += lineSize;
            dest[at] = ClipPixel((int)(a1 + b1) >> ColShift); at += lineSize;
            dest[at] = ClipPixel((int)(a2 + b2) >> ColShift); at += lineSize;
            dest[at] = ClipPixel((int)(a3 + b3) >> ColShift); at += lineSize;
            dest[at] = ClipPixel((int)(a3 - b3) >> ColShift); at += lineSize;
            dest[at] = ClipPixel((int)(a2 - b2) >> ColShift); at += lineSize;
            dest[at] = ClipPixel((int)(a1 - b1) >> ColShift); at += lineSize;
            dest[at] = ClipPixel((int)(a0 - b0) >> ColShift);
        }
    }

    /// <summary>
    /// <c>ff_simple_idct_add_int16_8bit</c> - transform and add the result to what is already
    /// there. Used for inter blocks, where the prediction is already in the picture.
    /// </summary>
    public static void SimpleIdctAdd(Span<byte> dest, int offset, int lineSize, Span<short> block)
    {
        for (int i = 0; i < 8; i++)
            IdctRowCondDc(block.Slice(i * 8, 8));

        for (int col = 0; col < 8; col++)
        {
            IdctCols(block, col, out uint a0, out uint a1, out uint a2, out uint a3,
                                 out uint b0, out uint b1, out uint b2, out uint b3);

            int at = offset + col;
            dest[at] = ClipPixel(dest[at] + ((int)(a0 + b0) >> ColShift)); at += lineSize;
            dest[at] = ClipPixel(dest[at] + ((int)(a1 + b1) >> ColShift)); at += lineSize;
            dest[at] = ClipPixel(dest[at] + ((int)(a2 + b2) >> ColShift)); at += lineSize;
            dest[at] = ClipPixel(dest[at] + ((int)(a3 + b3) >> ColShift)); at += lineSize;
            dest[at] = ClipPixel(dest[at] + ((int)(a3 - b3) >> ColShift)); at += lineSize;
            dest[at] = ClipPixel(dest[at] + ((int)(a2 - b2) >> ColShift)); at += lineSize;
            dest[at] = ClipPixel(dest[at] + ((int)(a1 - b1) >> ColShift)); at += lineSize;
            dest[at] = ClipPixel(dest[at] + ((int)(a0 - b0) >> ColShift));
        }
    }

    /// <summary>
    /// <c>c-&gt;idct_put</c> - the transform the context selected, writing pixels over whatever
    /// was there.
    /// </summary>
    /// <remarks>
    /// This read replaces the C's function pointer; see <see cref="MpegIdct"/> for why the
    /// table was dropped and why an unknown value throws rather than falling back to the
    /// simple transform. A wrong IDCT does not fail, it produces a near-miss, which is the
    /// most expensive kind of wrong there is.
    /// </remarks>
    public static void IdctPut(MpegIdct algo, Span<byte> dest, int offset, int lineSize,
                               Span<short> block)
    {
        switch (algo)
        {
            case MpegIdct.Simple:
                SimpleIdctPut(dest, offset, lineSize, block);
                break;
            case MpegIdct.Xvid:
                XvidIdct.XvidIdctPut(dest, offset, lineSize, block);
                break;
            default:
                throw new NotSupportedException($"idct_put: no transform for {algo}.");
        }
    }

    /// <summary><c>c-&gt;idct_add</c>; see <see cref="IdctPut"/>.</summary>
    public static void IdctAdd(MpegIdct algo, Span<byte> dest, int offset, int lineSize,
                               Span<short> block)
    {
        switch (algo)
        {
            case MpegIdct.Simple:
                SimpleIdctAdd(dest, offset, lineSize, block);
                break;
            case MpegIdct.Xvid:
                XvidIdct.XvidIdctAdd(dest, offset, lineSize, block);
                break;
            default:
                throw new NotSupportedException($"idct_add: no transform for {algo}.");
        }
    }

    /// <summary>
    /// <c>ff_zigzag_direct</c> (<c>mathtables.c:137</c>) - the default coefficient scan.
    /// </summary>
    /// <remarks>
    /// Maps a position in scan order to its raster position in the block. MPEG-2 selects
    /// between this and the alternate vertical scan with the <c>alternate_scan</c> flag in the
    /// picture coding extension.
    /// </remarks>
    /// <summary>
    /// <see cref="ZigzagDirect"/> as an array, shared by every caller.
    /// </summary>
    /// <remarks>
    /// <c>ScanTable.scantable</c> is a <c>const uint8_t *</c> in the C - one pointer to one
    /// static table, shared and never copied - and the scan is installed on every picture
    /// header, not once. Calling ToArray() there made a fresh 64-byte copy per picture, which
    /// the MPEG-4 allocation check measured at 88 bytes a call on all five streams.
    /// </remarks>
    public static readonly byte[] ZigzagDirectArray = ZigzagDirect.ToArray();

    public static ReadOnlySpan<byte> ZigzagDirect => new byte[]
    {
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    };

    /// <summary>
    /// <c>ff_permute_scantable</c> - composes a scan order with an IDCT's coefficient
    /// permutation.
    /// </summary>
    /// <remarks>
    /// The simple IDCT uses FF_IDCT_PERM_NONE, so the permutation is the identity and the result
    /// equals the source scan. It is still composed rather than copied, because the composition
    /// is what the C does and another IDCT choice would need it.
    /// </remarks>
    public static void PermuteScantable(Span<byte> dst, ReadOnlySpan<byte> src, ReadOnlySpan<byte> permutation)
    {
        for (int i = 0; i < 64; i++)
            dst[i] = permutation[src[i]];
    }

    /// <summary>The identity permutation, which is what FF_IDCT_PERM_NONE means.</summary>
    public static ReadOnlySpan<byte> IdentityPermutation => new byte[]
    {
         0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
        32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47,
        48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63,
    };

    /// <summary>
    /// <c>ff_put_pixels_clamped_c</c> - write an already-transformed block as pixels.
    /// </summary>
    public static void PutPixelsClamped(ReadOnlySpan<short> block, Span<byte> dest, int offset, int lineSize)
    {
        for (int y = 0; y < 8; y++)
        {
            int row = offset + y * lineSize;
            for (int x = 0; x < 8; x++)
                dest[row + x] = ClipPixel(block[y * 8 + x]);
        }
    }

    /// <summary>
    /// <c>ff_add_pixels_clamped_c</c> - add an already-transformed block to the picture.
    /// </summary>
    public static void AddPixelsClamped(ReadOnlySpan<short> block, Span<byte> dest, int offset, int lineSize)
    {
        for (int y = 0; y < 8; y++)
        {
            int row = offset + y * lineSize;
            for (int x = 0; x < 8; x++)
                dest[row + x] = ClipPixel(dest[row + x] + block[y * 8 + x]);
        }
    }

    /// <summary>
    /// <c>put_signed_pixels_clamped_c</c> - write a block whose samples are signed, by biasing
    /// to the middle of the range.
    /// </summary>
    public static void PutSignedPixelsClamped(ReadOnlySpan<short> block, Span<byte> dest, int offset, int lineSize)
    {
        for (int y = 0; y < 8; y++)
        {
            int row = offset + y * lineSize;
            for (int x = 0; x < 8; x++)
            {
                int v = block[y * 8 + x];
                dest[row + x] = v < -128 ? (byte)0 : v > 127 ? (byte)255 : (byte)(v + 128);
            }
        }
    }
}

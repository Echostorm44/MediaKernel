using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Mpeg4Video;

/// <summary>
/// FFmpeg's <c>qpeldsp.c</c>: MPEG-4 Part 2's quarter-pel motion compensation.
/// </summary>
/// <remarks>
/// <para>
/// The C is one macro, <c>QPEL_MC(r, OPNAME, RND, OP)</c>, instantiated three times - as
/// <c>put_</c>, <c>put_no_rnd_</c> and <c>avg_</c> - and each instantiation emits 36 functions,
/// so the file is 108 functions written once. That is a template, and translation rule 5 allows a
/// C# generic over <c>static abstract</c> members in its place; <see cref="IQpelOp"/> is the
/// macro's three parameters, and the JIT specialises each instantiation the way the
/// preprocessor did. Writing out 108 methods would be a transcription of the expansion rather
/// than of the source.
/// </para>
/// <para>
/// Two details of the macro are easy to lose and are called out where they happen: the
/// intermediate lowpasses inside the diagonal positions always use the <em>put</em> op, never
/// the caller's - that is the <c>put ## RND ##</c> in the C, where <c>RND</c> is <c>_</c> for
/// both <c>put_</c> and <c>avg_</c> - and the <c>r</c> parameter of the macro is never used in
/// its body, because the rounding is carried by the name it builds.
/// </para>
/// </remarks>
public static class QpelDsp
{
    /// <summary>
    /// The macro's <c>OPNAME</c>/<c>RND</c>/<c>OP</c> triple. <c>NoRnd</c> selects
    /// <c>op_put_no_rnd</c> and the unrounded averages; <c>Average</c> makes the final write
    /// average with what is already in the destination.
    /// </summary>
    public interface IQpelOp
    {
        /// <summary>True for the <c>put_no_rnd_</c> instantiation.</summary>
        static abstract bool NoRnd { get; }
        /// <summary>True for the <c>avg_</c> instantiation.</summary>
        static abstract bool Average { get; }
    }

    /// <summary><c>QPEL_MC(0, put_, _, op_put)</c></summary>
    public struct Put : IQpelOp
    {
        /// <inheritdoc/>
        public static bool NoRnd => false;
        /// <inheritdoc/>
        public static bool Average => false;
    }

    /// <summary><c>QPEL_MC(1, put_no_rnd_, _no_rnd_, op_put_no_rnd)</c></summary>
    public struct PutNoRnd : IQpelOp
    {
        /// <inheritdoc/>
        public static bool NoRnd => true;
        /// <inheritdoc/>
        public static bool Average => false;
    }

    /// <summary><c>QPEL_MC(0, avg_, _, op_avg)</c></summary>
    public struct Avg : IQpelOp
    {
        /// <inheritdoc/>
        public static bool NoRnd => false;
        /// <inheritdoc/>
        public static bool Average => true;
    }

    // op_put:        a = cm[(b + 16) >> 5]
    // op_put_no_rnd: a = cm[(b + 15) >> 5]
    // op_avg:        a = (a + cm[(b + 16) >> 5] + 1) >> 1
    //
    // cm is ff_crop_tab + MAX_NEG_CROP, which clamps to 0..255 over a range far wider than
    // anything these sums reach, so a clamp says the same thing without the table.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte LowpassOp<TOp>(byte dst, int v) where TOp : IQpelOp
    {
        byte filtered = Clip((v + (TOp.NoRnd ? 15 : 16)) >> 5);
        return TOp.Average ? (byte)((dst + filtered + 1) >> 1) : filtered;
    }

    /// <summary>The <c>put ## RND ##</c> lowpass used for every intermediate: never averaging.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte InnerOp<TOp>(int v) where TOp : IQpelOp =>
        Clip((v + (TOp.NoRnd ? 15 : 16)) >> 5);

    /// <summary>rnd_avg / no_rnd_avg, which the C does four bytes at a time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Avg2<TOp>(byte a, byte b) where TOp : IQpelOp =>
        (byte)(TOp.NoRnd ? (a + b) >> 1 : (a + b + 1) >> 1);

    /// <summary>
    /// The edge reflection the C's hand-written taps encode: <c>-1 - i</c> on the left and
    /// <c>2w + 1 - i</c> on the right, where w is the last readable column or row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mirror(int i, int w) => i < 0 ? -1 - i : i > w ? 2 * w + 1 - i : i;

    /// <summary>copy_block8 / copy_block9 / copy_block16 / copy_block17.</summary>
    private static void CopyBlock(Span<byte> dst, int dstOff, ReadOnlySpan<byte> src, int srcOff,
                                  int dstStride, int srcStride, int width, int h)
    {
        for (int i = 0; i < h; i++)
            src.Slice(srcOff + i * srcStride, width).CopyTo(dst.Slice(dstOff + i * dstStride, width));
    }

    // ---------------------------------------------------------------- lowpass filters

    /// <summary><c>OPNAME ## mpeg4_qpel8_h_lowpass</c> and its 16-wide twin.</summary>
    /// <remarks>
    /// The C writes every tap out by hand, and what those hand-written indices encode at the
    /// edges is a <em>mirror</em>, not a clamp. Reading the first row of the 8-wide filter
    /// back as a general formula: position x wants
    /// <c>(s[x]+s[x+1])*20 - (s[x-1]+s[x+2])*6 + (s[x-2]+s[x+3])*3 - (s[x-3]+s[x+4])</c>, and
    /// at x = 0 the C writes <c>(src[1]+src[3])*3</c> where that asks for <c>s[-2]</c> - so
    /// s[-2] is s[1], not s[0]. The left reflection is <c>-1 - i</c> and the right one is
    /// <c>2w + 1 - i</c> about the last readable column w. A clamp agrees with the mirror only
    /// on the nearest neighbour and differs on the two beyond it.
    /// </remarks>
    private static void HLowpass<TOp>(Span<byte> dst, int dstOff, ReadOnlySpan<byte> src, int srcOff,
                                      int dstStride, int srcStride, int width, int h, bool inner)
        where TOp : IQpelOp
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int v = (S(src, srcOff, x) + S(src, srcOff, x + 1)) * 20
                      - (S(src, srcOff, x - 1) + S(src, srcOff, x + 2)) * 6
                      + (S(src, srcOff, x - 2) + S(src, srcOff, x + 3)) * 3
                      - (S(src, srcOff, x - 3) + S(src, srcOff, x + 4));
                int o = dstOff + x;
                dst[o] = inner ? InnerOp<TOp>(v) : LowpassOp<TOp>(dst[o], v);
            }
            dstOff += dstStride;
            srcOff += srcStride;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int S(ReadOnlySpan<byte> s, int baseOff, int x) => s[baseOff + Mirror(x, width)];
    }

    /// <summary><c>OPNAME ## mpeg4_qpel8_v_lowpass</c> and its 16-wide twin.</summary>
    private static void VLowpass<TOp>(Span<byte> dst, int dstOff, ReadOnlySpan<byte> src, int srcOff,
                                      int dstStride, int srcStride, int width, bool inner)
        where TOp : IQpelOp
    {
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < width; y++)
            {
                int v = (S(src, srcOff, y) + S(src, srcOff, y + 1)) * 20
                      - (S(src, srcOff, y - 1) + S(src, srcOff, y + 2)) * 6
                      + (S(src, srcOff, y - 2) + S(src, srcOff, y + 3)) * 3
                      - (S(src, srcOff, y - 3) + S(src, srcOff, y + 4));
                int o = dstOff + y * dstStride;
                dst[o] = inner ? InnerOp<TOp>(v) : LowpassOp<TOp>(dst[o], v);
            }
            dstOff++;
            srcOff++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int S(ReadOnlySpan<byte> s, int baseOff, int y) =>
            s[baseOff + Mirror(y, width) * srcStride];
    }

    /// <summary><c>OPNAME ## pixels8_l2_8</c> and its 16-wide twin.</summary>
    private static void PixelsL2<TOp>(Span<byte> dst, int dstOff,
                                      ReadOnlySpan<byte> src1, int src1Off,
                                      ReadOnlySpan<byte> src2, int src2Off,
                                      int dstStride, int src1Stride, int src2Stride,
                                      int width, int h, bool inner)
        where TOp : IQpelOp
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte a = src1[src1Off + y * src1Stride + x];
                byte b = src2[src2Off + y * src2Stride + x];
                byte v = Avg2<TOp>(a, b);
                int o = dstOff + y * dstStride + x;
                dst[o] = !inner && TOp.Average ? (byte)((dst[o] + v + 1) >> 1) : v;
            }
        }
    }

    // ---------------------------------------------------------------- the sixteen positions

    /// <summary>
    /// One quarter-pel position, for either block size. <paramref name="dx"/> and
    /// <paramref name="dy"/> are the macro's mcXY digits.
    /// </summary>
    /// <remarks>
    /// The C writes these as 32 separate functions because each needs a different set of
    /// scratch buffers; they are one method here with the buffers sized for the larger block,
    /// which keeps the sixteen cases side by side where the C has them pages apart.
    /// </remarks>
    public static void Qpel<TOp>(Span<byte> dst, int dstOff, ReadOnlySpan<byte> src, int srcOff,
                                 int stride, int size, int dx, int dy)
        where TOp : IQpelOp
    {
        if (size != 8 && size != 16)
            throw new ArgumentOutOfRangeException(nameof(size), size, "qpel blocks are 8 or 16 wide");
        if ((uint)dx > 3 || (uint)dy > 3)
            throw new ArgumentOutOfRangeException(nameof(dx), $"mc{dx}{dy} is not a qpel position");

        int w = size;                       // 8 or 16
        int fullStride = size == 8 ? 16 : 24;
        int hStride = size;                 // halfH is `size` wide

        // full[16 * 9] / full[24 * 17]; halfH[72] / halfH[272]; halfHV[64] / halfHV[256].
        // Sized for the larger block in both cases rather than branching on the size, because
        // the 8-wide buffers are a subset and one shape is easier to read than two.
        Span<byte> full = stackalloc byte[24 * 17];
        Span<byte> halfH = stackalloc byte[272];
        Span<byte> halfHV = stackalloc byte[256];

        switch (dx, dy)
        {
            case (0, 0):
                // Not part of QPEL_MC: the macro has no mc00, and ff_qpeldsp_init fills that
                // slot with ff_put_pixels8x8_c / ff_avg_pixels8x8_c.
                for (int y = 0; y < w; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int o = dstOff + y * stride + x;
                        byte v = src[srcOff + y * stride + x];
                        dst[o] = TOp.Average ? (byte)((dst[o] + v + 1) >> 1) : v;
                    }
                return;

            case (1, 0):
                HLowpass<TOp>(halfH, 0, src, srcOff, w, stride, w, w, inner: true);
                PixelsL2<TOp>(dst, dstOff, src, srcOff, halfH, 0, stride, stride, w, w, w, false);
                return;

            case (2, 0):
                HLowpass<TOp>(dst, dstOff, src, srcOff, stride, stride, w, w, inner: false);
                return;

            case (3, 0):
                HLowpass<TOp>(halfH, 0, src, srcOff, w, stride, w, w, inner: true);
                PixelsL2<TOp>(dst, dstOff, src, srcOff + 1, halfH, 0, stride, stride, w, w, w, false);
                return;

            case (0, 1):
                CopyBlock(full, 0, src, srcOff, fullStride, stride, w, w + 1);
                VLowpass<TOp>(halfHV, 0, full, 0, w, fullStride, w, inner: true);
                PixelsL2<TOp>(dst, dstOff, full, 0, halfHV, 0, stride, fullStride, w, w, w, false);
                return;

            case (0, 2):
                CopyBlock(full, 0, src, srcOff, fullStride, stride, w, w + 1);
                VLowpass<TOp>(dst, dstOff, full, 0, stride, fullStride, w, inner: false);
                return;

            case (0, 3):
                CopyBlock(full, 0, src, srcOff, fullStride, stride, w, w + 1);
                VLowpass<TOp>(halfHV, 0, full, 0, w, fullStride, w, inner: true);
                PixelsL2<TOp>(dst, dstOff, full, fullStride, halfHV, 0,
                              stride, fullStride, w, w, w, false);
                return;

            case (2, 2):
                HLowpass<TOp>(halfH, 0, src, srcOff, hStride, stride, w, w + 1, inner: true);
                VLowpass<TOp>(dst, dstOff, halfH, 0, stride, hStride, w, inner: false);
                return;

            case (2, 1):
                HLowpass<TOp>(halfH, 0, src, srcOff, hStride, stride, w, w + 1, inner: true);
                VLowpass<TOp>(halfHV, 0, halfH, 0, hStride, hStride, w, inner: true);
                PixelsL2<TOp>(dst, dstOff, halfH, 0, halfHV, 0, stride, hStride, hStride, w, w, false);
                return;

            case (2, 3):
                HLowpass<TOp>(halfH, 0, src, srcOff, hStride, stride, w, w + 1, inner: true);
                VLowpass<TOp>(halfHV, 0, halfH, 0, hStride, hStride, w, inner: true);
                PixelsL2<TOp>(dst, dstOff, halfH, hStride, halfHV, 0,
                              stride, hStride, hStride, w, w, false);
                return;

            // The remaining six are the diagonals, which all begin by copying a block one
            // column wider than the output - that extra column is what the `full + 1` reads
            // below are for - and then fold the horizontal half-pel into halfH in place.
            case (1, 1):
            case (3, 1):
            case (1, 3):
            case (3, 3):
            case (1, 2):
            case (3, 2):
            {
                int xOff = dx == 3 ? 1 : 0;
                CopyBlock(full, 0, src, srcOff, fullStride, stride, w + 1, w + 1);
                HLowpass<TOp>(halfH, 0, full, 0, hStride, fullStride, w, w + 1, inner: true);
                PixelsL2<TOp>(halfH, 0, halfH, 0, full, xOff,
                              hStride, hStride, fullStride, w, w + 1, inner: true);

                if (dy == 2)
                {
                    VLowpass<TOp>(dst, dstOff, halfH, 0, stride, hStride, w, inner: false);
                    return;
                }

                VLowpass<TOp>(halfHV, 0, halfH, 0, hStride, hStride, w, inner: true);
                int yOff = dy == 3 ? hStride : 0;
                PixelsL2<TOp>(dst, dstOff, halfH, yOff, halfHV, 0,
                              stride, hStride, hStride, w, w, false);
                return;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(dx), $"mc{dx}{dy} is unreachable");
        }
    }
}

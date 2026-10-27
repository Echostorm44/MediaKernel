// VP8 intra prediction — 4x4, 8x8 (chroma), 16x16 modes
// Ported from FFmpeg libavcodec/h264pred_template.c, h264pred.c
// VP8 uses H.264-style prediction with TM (True Motion) instead of plane prediction.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.VP8;

/// <summary>
/// VP8 intra prediction modes. All prediction functions operate in-place on a byte buffer.
/// The block at (src, offset) is filled based on already-reconstructed neighbors
/// above (offset - stride) and to the left (offset - 1).
/// </summary>
public static class Vp8IntraPred
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip8(int v) => (byte)Math.Clamp(v, 0, 255);

    // ==================== 4x4 prediction modes ====================
    // All take: src buffer, offset to top-left of 4x4 block, stride
    // topright: offset to the 4 pixels right of the top row (for DDL, VL modes)

    /// <summary>
    /// 4x4 vertical prediction (VP8-specific): averaged filter using top-left and top-right.
    /// Port of pred4x4_vertical_vp8_c.
    /// </summary>
    public static void Pred4x4Vertical(Span<byte> src, int off, int stride, ReadOnlySpan<byte> topright)
    {
        int lt = src[off - 1 - stride];
        int t0 = src[off - stride], t1 = src[off - stride + 1];
        int t2 = src[off - stride + 2], t3 = src[off - stride + 3];
        int t4 = topright[0];

        byte v0 = (byte)((lt + 2 * t0 + t1 + 2) >> 2);
        byte v1 = (byte)((t0 + 2 * t1 + t2 + 2) >> 2);
        byte v2 = (byte)((t1 + 2 * t2 + t3 + 2) >> 2);
        byte v3 = (byte)((t2 + 2 * t3 + t4 + 2) >> 2);

        for (int y = 0; y < 4; y++)
        {
            src[off + y * stride + 0] = v0;
            src[off + y * stride + 1] = v1;
            src[off + y * stride + 2] = v2;
            src[off + y * stride + 3] = v3;
        }
    }

    /// <summary>
    /// 4x4 horizontal prediction (VP8-specific): averaged filter using top-left and left neighbors.
    /// Port of pred4x4_horizontal_vp8_c.
    /// </summary>
    public static void Pred4x4Horizontal(Span<byte> src, int off, int stride)
    {
        int lt = src[off - 1 - stride];
        int l0 = src[off - 1], l1 = src[off + stride - 1];
        int l2 = src[off + 2 * stride - 1], l3 = src[off + 3 * stride - 1];

        byte v0 = (byte)((lt + 2 * l0 + l1 + 2) >> 2);
        byte v1 = (byte)((l0 + 2 * l1 + l2 + 2) >> 2);
        byte v2 = (byte)((l1 + 2 * l2 + l3 + 2) >> 2);
        byte v3 = (byte)((l2 + 2 * l3 + l3 + 2) >> 2);

        for (int x = 0; x < 4; x++) src[off + 0 * stride + x] = v0;
        for (int x = 0; x < 4; x++) src[off + 1 * stride + x] = v1;
        for (int x = 0; x < 4; x++) src[off + 2 * stride + x] = v2;
        for (int x = 0; x < 4; x++) src[off + 3 * stride + x] = v3;
    }

    /// <summary>4x4 DC: average of 4 top + 4 left neighbors.</summary>
    public static void Pred4x4Dc(Span<byte> src, int off, int stride)
    {
        int dc = src[off - stride] + src[off - stride + 1] + src[off - stride + 2] + src[off - stride + 3]
               + src[off - 1] + src[off + stride - 1] + src[off + 2 * stride - 1] + src[off + 3 * stride - 1];
        byte val = (byte)((dc + 4) >> 3);
        Fill4x4(src, off, stride, val);
    }

    /// <summary>4x4 left DC: only left neighbors.</summary>
    public static void Pred4x4LeftDc(Span<byte> src, int off, int stride)
    {
        int dc = src[off - 1] + src[off + stride - 1] + src[off + 2 * stride - 1] + src[off + 3 * stride - 1];
        Fill4x4(src, off, stride, (byte)((dc + 2) >> 2));
    }

    /// <summary>4x4 top DC: only top neighbors.</summary>
    public static void Pred4x4TopDc(Span<byte> src, int off, int stride)
    {
        int dc = src[off - stride] + src[off - stride + 1] + src[off - stride + 2] + src[off - stride + 3];
        Fill4x4(src, off, stride, (byte)((dc + 2) >> 2));
    }

    /// <summary>4x4 128 DC: fill with 128.</summary>
    public static void Pred4x4Dc128(Span<byte> src, int off, int stride)
        => Fill4x4(src, off, stride, 128);

    /// <summary>4x4 127 DC: fill with 127 (VP8 specific).</summary>
    public static void Pred4x4Dc127(Span<byte> src, int off, int stride)
        => Fill4x4(src, off, stride, 127);

    /// <summary>4x4 129 DC: fill with 129 (VP8 specific).</summary>
    public static void Pred4x4Dc129(Span<byte> src, int off, int stride)
        => Fill4x4(src, off, stride, 129);

    /// <summary>4x4 TM (True Motion): top[x] + left[y] - topleft.</summary>
    public static void Pred4x4Tm(Span<byte> src, int off, int stride)
    {
        int tl = src[off - stride - 1];
        int t0 = src[off - stride], t1 = src[off - stride + 1];
        int t2 = src[off - stride + 2], t3 = src[off - stride + 3];
        for (int y = 0; y < 4; y++)
        {
            int left = src[off + y * stride - 1];
            int base_ = left - tl;
            src[off + y * stride + 0] = Clip8(base_ + t0);
            src[off + y * stride + 1] = Clip8(base_ + t1);
            src[off + y * stride + 2] = Clip8(base_ + t2);
            src[off + y * stride + 3] = Clip8(base_ + t3);
        }
    }

    /// <summary>4x4 diagonal down-left.</summary>
    public static void Pred4x4DownLeft(Span<byte> src, int off, int stride, ReadOnlySpan<byte> topright)
    {
        int t0 = src[off - stride], t1 = src[off - stride + 1];
        int t2 = src[off - stride + 2], t3 = src[off - stride + 3];
        int t4 = topright[0], t5 = topright[1], t6 = topright[2], t7 = topright[3];

        src[off + 0 + 0 * stride] = (byte)((t0 + 2 * t1 + t2 + 2) >> 2);
        src[off + 1 + 0 * stride] =
        src[off + 0 + 1 * stride] = (byte)((t1 + 2 * t2 + t3 + 2) >> 2);
        src[off + 2 + 0 * stride] =
        src[off + 1 + 1 * stride] =
        src[off + 0 + 2 * stride] = (byte)((t2 + 2 * t3 + t4 + 2) >> 2);
        src[off + 3 + 0 * stride] =
        src[off + 2 + 1 * stride] =
        src[off + 1 + 2 * stride] =
        src[off + 0 + 3 * stride] = (byte)((t3 + 2 * t4 + t5 + 2) >> 2);
        src[off + 3 + 1 * stride] =
        src[off + 2 + 2 * stride] =
        src[off + 1 + 3 * stride] = (byte)((t4 + 2 * t5 + t6 + 2) >> 2);
        src[off + 3 + 2 * stride] =
        src[off + 2 + 3 * stride] = (byte)((t5 + 2 * t6 + t7 + 2) >> 2);
        src[off + 3 + 3 * stride] = (byte)((t6 + 3 * t7 + 2) >> 2);
    }

    /// <summary>4x4 diagonal down-right.</summary>
    public static void Pred4x4DownRight(Span<byte> src, int off, int stride)
    {
        int lt = src[off - stride - 1];
        int t0 = src[off - stride], t1 = src[off - stride + 1];
        int t2 = src[off - stride + 2], t3 = src[off - stride + 3];
        int l0 = src[off - 1], l1 = src[off + stride - 1];
        int l2 = src[off + 2 * stride - 1], l3 = src[off + 3 * stride - 1];

        src[off + 0 + 3 * stride] = (byte)((l3 + 2 * l2 + l1 + 2) >> 2);
        src[off + 0 + 2 * stride] =
        src[off + 1 + 3 * stride] = (byte)((l2 + 2 * l1 + l0 + 2) >> 2);
        src[off + 0 + 1 * stride] =
        src[off + 1 + 2 * stride] =
        src[off + 2 + 3 * stride] = (byte)((l1 + 2 * l0 + lt + 2) >> 2);
        src[off + 0 + 0 * stride] =
        src[off + 1 + 1 * stride] =
        src[off + 2 + 2 * stride] =
        src[off + 3 + 3 * stride] = (byte)((l0 + 2 * lt + t0 + 2) >> 2);
        src[off + 1 + 0 * stride] =
        src[off + 2 + 1 * stride] =
        src[off + 3 + 2 * stride] = (byte)((lt + 2 * t0 + t1 + 2) >> 2);
        src[off + 2 + 0 * stride] =
        src[off + 3 + 1 * stride] = (byte)((t0 + 2 * t1 + t2 + 2) >> 2);
        src[off + 3 + 0 * stride] = (byte)((t1 + 2 * t2 + t3 + 2) >> 2);
    }

    /// <summary>4x4 vertical-right.</summary>
    public static void Pred4x4VerticalRight(Span<byte> src, int off, int stride)
    {
        int lt = src[off - stride - 1];
        int t0 = src[off - stride], t1 = src[off - stride + 1];
        int t2 = src[off - stride + 2], t3 = src[off - stride + 3];
        int l0 = src[off - 1], l1 = src[off + stride - 1], l2 = src[off + 2 * stride - 1];

        src[off + 0 + 0 * stride] =
        src[off + 1 + 2 * stride] = (byte)((lt + t0 + 1) >> 1);
        src[off + 1 + 0 * stride] =
        src[off + 2 + 2 * stride] = (byte)((t0 + t1 + 1) >> 1);
        src[off + 2 + 0 * stride] =
        src[off + 3 + 2 * stride] = (byte)((t1 + t2 + 1) >> 1);
        src[off + 3 + 0 * stride] = (byte)((t2 + t3 + 1) >> 1);
        src[off + 0 + 1 * stride] =
        src[off + 1 + 3 * stride] = (byte)((l0 + 2 * lt + t0 + 2) >> 2);
        src[off + 1 + 1 * stride] =
        src[off + 2 + 3 * stride] = (byte)((lt + 2 * t0 + t1 + 2) >> 2);
        src[off + 2 + 1 * stride] =
        src[off + 3 + 3 * stride] = (byte)((t0 + 2 * t1 + t2 + 2) >> 2);
        src[off + 3 + 1 * stride] = (byte)((t1 + 2 * t2 + t3 + 2) >> 2);
        src[off + 0 + 2 * stride] = (byte)((lt + 2 * l0 + l1 + 2) >> 2);
        src[off + 0 + 3 * stride] = (byte)((l0 + 2 * l1 + l2 + 2) >> 2);
    }

    /// <summary>4x4 vertical-left.</summary>
    /// <summary>
    /// 4x4 vertical-left prediction (VP8-specific).
    /// Port of pred4x4_vertical_left_vp8_c. Reads t7 from topright[3].
    /// </summary>
    public static void Pred4x4VerticalLeft(Span<byte> src, int off, int stride, ReadOnlySpan<byte> topright)
    {
        int t0 = src[off - stride], t1 = src[off - stride + 1];
        int t2 = src[off - stride + 2], t3 = src[off - stride + 3];
        int t4 = topright[0], t5 = topright[1], t6 = topright[2], t7 = topright[3];

        src[off + 0 + 0 * stride] = (byte)((t0 + t1 + 1) >> 1);
        src[off + 1 + 0 * stride] =
        src[off + 0 + 2 * stride] = (byte)((t1 + t2 + 1) >> 1);
        src[off + 2 + 0 * stride] =
        src[off + 1 + 2 * stride] = (byte)((t2 + t3 + 1) >> 1);
        src[off + 3 + 0 * stride] =
        src[off + 2 + 2 * stride] = (byte)((t3 + t4 + 1) >> 1);
        src[off + 0 + 1 * stride] = (byte)((t0 + 2 * t1 + t2 + 2) >> 2);
        src[off + 1 + 1 * stride] =
        src[off + 0 + 3 * stride] = (byte)((t1 + 2 * t2 + t3 + 2) >> 2);
        src[off + 2 + 1 * stride] =
        src[off + 1 + 3 * stride] = (byte)((t2 + 2 * t3 + t4 + 2) >> 2);
        src[off + 3 + 1 * stride] =
        src[off + 2 + 3 * stride] = (byte)((t3 + 2 * t4 + t5 + 2) >> 2);
        src[off + 3 + 2 * stride] = (byte)((t4 + 2 * t5 + t6 + 2) >> 2);
        src[off + 3 + 3 * stride] = (byte)((t5 + 2 * t6 + t7 + 2) >> 2);
    }

    /// <summary>4x4 horizontal-up.</summary>
    public static void Pred4x4HorizontalUp(Span<byte> src, int off, int stride)
    {
        int l0 = src[off - 1], l1 = src[off + stride - 1];
        int l2 = src[off + 2 * stride - 1], l3 = src[off + 3 * stride - 1];

        src[off + 0 + 0 * stride] = (byte)((l0 + l1 + 1) >> 1);
        src[off + 1 + 0 * stride] = (byte)((l0 + 2 * l1 + l2 + 2) >> 2);
        src[off + 2 + 0 * stride] =
        src[off + 0 + 1 * stride] = (byte)((l1 + l2 + 1) >> 1);
        src[off + 3 + 0 * stride] =
        src[off + 1 + 1 * stride] = (byte)((l1 + 2 * l2 + l3 + 2) >> 2);
        src[off + 2 + 1 * stride] =
        src[off + 0 + 2 * stride] = (byte)((l2 + l3 + 1) >> 1);
        src[off + 3 + 1 * stride] =
        src[off + 1 + 2 * stride] = (byte)((l2 + 2 * l3 + l3 + 2) >> 2);
        src[off + 3 + 2 * stride] =
        src[off + 1 + 3 * stride] =
        src[off + 0 + 3 * stride] =
        src[off + 2 + 2 * stride] =
        src[off + 2 + 3 * stride] =
        src[off + 3 + 3 * stride] = (byte)l3;
    }

    /// <summary>4x4 horizontal-down.</summary>
    public static void Pred4x4HorizontalDown(Span<byte> src, int off, int stride)
    {
        int lt = src[off - stride - 1];
        int t0 = src[off - stride], t1 = src[off - stride + 1], t2 = src[off - stride + 2];
        int l0 = src[off - 1], l1 = src[off + stride - 1];
        int l2 = src[off + 2 * stride - 1], l3 = src[off + 3 * stride - 1];

        src[off + 0 + 0 * stride] =
        src[off + 2 + 1 * stride] = (byte)((lt + l0 + 1) >> 1);
        src[off + 1 + 0 * stride] =
        src[off + 3 + 1 * stride] = (byte)((l0 + 2 * lt + t0 + 2) >> 2);
        src[off + 2 + 0 * stride] = (byte)((lt + 2 * t0 + t1 + 2) >> 2);
        src[off + 3 + 0 * stride] = (byte)((t0 + 2 * t1 + t2 + 2) >> 2);
        src[off + 0 + 1 * stride] =
        src[off + 2 + 2 * stride] = (byte)((l0 + l1 + 1) >> 1);
        src[off + 1 + 1 * stride] =
        src[off + 3 + 2 * stride] = (byte)((lt + 2 * l0 + l1 + 2) >> 2);
        src[off + 0 + 2 * stride] =
        src[off + 2 + 3 * stride] = (byte)((l1 + l2 + 1) >> 1);
        src[off + 1 + 2 * stride] =
        src[off + 3 + 3 * stride] = (byte)((l0 + 2 * l1 + l2 + 2) >> 2);
        src[off + 0 + 3 * stride] = (byte)((l2 + l3 + 1) >> 1);
        src[off + 1 + 3 * stride] = (byte)((l1 + 2 * l2 + l3 + 2) >> 2);
    }

    /// <summary>4x4 vertical straight copy (H.264-style): used for VERT_VP8_PRED (mode 10).</summary>
    public static void Pred4x4VerticalStraight(Span<byte> src, int off, int stride)
    {
        byte t0 = src[off - stride], t1 = src[off - stride + 1];
        byte t2 = src[off - stride + 2], t3 = src[off - stride + 3];
        for (int y = 0; y < 4; y++)
        {
            src[off + y * stride + 0] = t0;
            src[off + y * stride + 1] = t1;
            src[off + y * stride + 2] = t2;
            src[off + y * stride + 3] = t3;
        }
    }

    /// <summary>4x4 horizontal straight copy (H.264-style): used for HOR_VP8_PRED (mode 14).</summary>
    public static void Pred4x4HorizontalStraight(Span<byte> src, int off, int stride)
    {
        for (int y = 0; y < 4; y++)
        {
            byte left = src[off + y * stride - 1];
            src[off + y * stride + 0] = left;
            src[off + y * stride + 1] = left;
            src[off + y * stride + 2] = left;
            src[off + y * stride + 3] = left;
        }
    }

    // ==================== 4x4 mode dispatch ====================

    /// <summary>
    /// Dispatch a 4x4 intra prediction mode. Mode values are the H.264/VP8 pred4x4 indices
    /// after edge emulation adjustment.
    /// </summary>
    public static void Pred4x4(Span<byte> src, int off, int stride, ReadOnlySpan<byte> topright, int mode)
    {
        switch (mode)
        {
            case 0: Pred4x4Vertical(src, off, stride, topright); break;  // VERT_PRED (VP8 averaged)
            case 1: Pred4x4Horizontal(src, off, stride); break;          // HOR_PRED (VP8 averaged)
            case 2: Pred4x4Dc(src, off, stride); break;                  // DC_PRED
            case 3: Pred4x4DownLeft(src, off, stride, topright); break;  // DIAG_DOWN_LEFT
            case 4: Pred4x4DownRight(src, off, stride); break;           // DIAG_DOWN_RIGHT
            case 5: Pred4x4VerticalRight(src, off, stride); break;       // VERT_RIGHT
            case 6: Pred4x4HorizontalDown(src, off, stride); break;      // HOR_DOWN
            case 7: Pred4x4VerticalLeft(src, off, stride, topright); break; // VERT_LEFT (VP8 version)
            case 8: Pred4x4HorizontalUp(src, off, stride); break;        // HOR_UP
            case 9: Pred4x4Tm(src, off, stride); break;                  // TM_VP8_PRED
            case 10: Pred4x4VerticalStraight(src, off, stride); break;   // VERT_VP8_PRED (straight copy)
            case 11: Pred4x4Dc128(src, off, stride); break;              // DC_128_PRED
            case 12: Pred4x4Dc127(src, off, stride); break;              // DC_127_PRED
            case 13: Pred4x4Dc129(src, off, stride); break;              // DC_129_PRED
            case 14: Pred4x4HorizontalStraight(src, off, stride); break; // HOR_VP8_PRED (straight copy)
            default: Pred4x4Dc128(src, off, stride); break;
        }
    }

    // ==================== 16x16 prediction modes ====================

    /// <summary>16x16 vertical: copy top row down 16 times.</summary>
    public static void Pred16x16Vertical(Span<byte> src, int off, int stride)
    {
        for (int y = 0; y < 16; y++)
            src.Slice(off - stride, 16).CopyTo(src.Slice(off + y * stride, 16));
    }

    /// <summary>16x16 horizontal: copy left column across.</summary>
    public static void Pred16x16Horizontal(Span<byte> src, int off, int stride)
    {
        for (int y = 0; y < 16; y++)
        {
            byte left = src[off + y * stride - 1];
            src.Slice(off + y * stride, 16).Fill(left);
        }
    }

    /// <summary>16x16 DC: average of 16 top + 16 left.</summary>
    public static void Pred16x16Dc(Span<byte> src, int off, int stride)
    {
        int dc = 0;
        for (int i = 0; i < 16; i++) dc += src[off - 1 + i * stride];
        for (int i = 0; i < 16; i++) dc += src[off + i - stride];
        byte val = (byte)((dc + 16) >> 5);
        Fill16x16(src, off, stride, val);
    }

    /// <summary>16x16 left DC: only left neighbors.</summary>
    public static void Pred16x16LeftDc(Span<byte> src, int off, int stride)
    {
        int dc = 0;
        for (int i = 0; i < 16; i++) dc += src[off - 1 + i * stride];
        Fill16x16(src, off, stride, (byte)((dc + 8) >> 4));
    }

    /// <summary>16x16 top DC: only top neighbors.</summary>
    public static void Pred16x16TopDc(Span<byte> src, int off, int stride)
    {
        int dc = 0;
        for (int i = 0; i < 16; i++) dc += src[off + i - stride];
        Fill16x16(src, off, stride, (byte)((dc + 8) >> 4));
    }

    /// <summary>16x16 DC 128.</summary>
    public static void Pred16x16Dc128(Span<byte> src, int off, int stride)
        => Fill16x16(src, off, stride, 128);

    /// <summary>16x16 DC 127 (VP8: !mb_y && mb_x).</summary>
    public static void Pred16x16Dc127(Span<byte> src, int off, int stride)
        => Fill16x16(src, off, stride, 127);

    /// <summary>16x16 DC 129 (VP8: mb_y && !mb_x).</summary>
    public static void Pred16x16Dc129(Span<byte> src, int off, int stride)
        => Fill16x16(src, off, stride, 129);

    /// <summary>16x16 TM (True Motion): top[x] + left[y] - topleft.</summary>
    public static void Pred16x16Tm(Span<byte> src, int off, int stride)
    {
        int tl = src[off - stride - 1];
        for (int y = 0; y < 16; y++)
        {
            int left = src[off + y * stride - 1];
            int base_ = left - tl;
            for (int x = 0; x < 16; x++)
                src[off + y * stride + x] = Clip8(base_ + src[off + x - stride]);
        }
    }

    /// <summary>Dispatch 16x16 prediction by mode (after edge adjustment).</summary>
    public static void Pred16x16(Span<byte> src, int off, int stride, int mode)
    {
        switch (mode)
        {
            case 0: Pred16x16Dc(src, off, stride); break;          // DC_PRED8x8
            case 1: Pred16x16Horizontal(src, off, stride); break;  // HOR_PRED8x8
            case 2: Pred16x16Vertical(src, off, stride); break;    // VERT_PRED8x8
            case 3: Pred16x16Tm(src, off, stride); break;          // PLANE_PRED8x8 → TM for VP8
            case 4: Pred16x16LeftDc(src, off, stride); break;      // LEFT_DC_PRED8x8
            case 5: Pred16x16TopDc(src, off, stride); break;       // TOP_DC_PRED8x8
            case 6: Pred16x16Dc128(src, off, stride); break;       // DC_128_PRED8x8
            case 7: Pred16x16Dc127(src, off, stride); break;       // DC_127_PRED8x8
            case 8: Pred16x16Dc129(src, off, stride); break;       // DC_129_PRED8x8
            default: Pred16x16Dc128(src, off, stride); break;
        }
    }

    // ==================== 8x8 (chroma) prediction modes ====================
    // These are H.264's pred8x8 modes, used for VP8 chroma prediction (U, V planes).
    // VP8 chroma blocks are 8x8 (2x2 macroblocks of 4x4).
    // DC mode splits into 4 quadrants with separate DC values.

    /// <summary>8x8 DC: standard H.264-style 4-quadrant DC prediction.</summary>
    /// <summary>
    /// 8x8 DC prediction — VP8/RV40 variant.
    /// Uses a single DC value for the entire 8x8 block (average of all 16 border pixels).
    /// Port of pred8x8_dc_rv40_c from h264pred.c.
    /// </summary>
    public static void Pred8x8Dc(Span<byte> src, int off, int stride)
    {
        int dc0 = 0;
        for (int i = 0; i < 4; i++)
        {
            dc0 += src[off - 1 + i * stride] + src[off + i - stride];
            dc0 += src[off + 4 + i - stride];
            dc0 += src[off - 1 + (i + 4) * stride];
        }
        byte v = (byte)((dc0 + 8) >> 4);
        Fill8x8(src, off, stride, v);
    }

    /// <summary>8x8 vertical.</summary>
    public static void Pred8x8Vertical(Span<byte> src, int off, int stride)
    {
        for (int y = 0; y < 8; y++)
            src.Slice(off - stride, 8).CopyTo(src.Slice(off + y * stride, 8));
    }

    /// <summary>8x8 horizontal.</summary>
    public static void Pred8x8Horizontal(Span<byte> src, int off, int stride)
    {
        for (int y = 0; y < 8; y++)
        {
            byte left = src[off + y * stride - 1];
            src.Slice(off + y * stride, 8).Fill(left);
        }
    }

    /// <summary>8x8 TM (True Motion) for chroma.</summary>
    public static void Pred8x8Tm(Span<byte> src, int off, int stride)
    {
        int tl = src[off - stride - 1];
        for (int y = 0; y < 8; y++)
        {
            int left = src[off + y * stride - 1];
            int base_ = left - tl;
            for (int x = 0; x < 8; x++)
                src[off + y * stride + x] = Clip8(base_ + src[off + x - stride]);
        }
    }

    /// <summary>
    /// 8x8 left DC — VP8/RV40 variant.
    /// Single DC value from all 8 left pixels.
    /// Port of pred8x8_left_dc_rv40_c from h264pred.c.
    /// </summary>
    public static void Pred8x8LeftDc(Span<byte> src, int off, int stride)
    {
        int dc0 = 0;
        for (int i = 0; i < 8; i++)
            dc0 += src[off - 1 + i * stride];
        byte v = (byte)((dc0 + 4) >> 3);
        Fill8x8(src, off, stride, v);
    }

    /// <summary>
    /// 8x8 top DC — VP8/RV40 variant.
    /// Single DC value from all 8 top pixels.
    /// Port of pred8x8_top_dc_rv40_c from h264pred.c.
    /// </summary>
    public static void Pred8x8TopDc(Span<byte> src, int off, int stride)
    {
        int dc0 = 0;
        for (int i = 0; i < 8; i++)
            dc0 += src[off + i - stride];
        byte v = (byte)((dc0 + 4) >> 3);
        Fill8x8(src, off, stride, v);
    }

    /// <summary>8x8 DC 128.</summary>
    public static void Pred8x8Dc128(Span<byte> src, int off, int stride)
        => Fill8x8(src, off, stride, 128);

    /// <summary>8x8 DC 127.</summary>
    public static void Pred8x8Dc127(Span<byte> src, int off, int stride)
        => Fill8x8(src, off, stride, 127);

    /// <summary>8x8 DC 129.</summary>
    public static void Pred8x8Dc129(Span<byte> src, int off, int stride)
        => Fill8x8(src, off, stride, 129);

    /// <summary>Dispatch 8x8 prediction by mode (after edge adjustment).</summary>
    public static void Pred8x8(Span<byte> src, int off, int stride, int mode)
    {
        switch (mode)
        {
            case 0: Pred8x8Dc(src, off, stride); break;          // DC_PRED8x8
            case 1: Pred8x8Horizontal(src, off, stride); break;  // HOR_PRED8x8
            case 2: Pred8x8Vertical(src, off, stride); break;    // VERT_PRED8x8
            case 3: Pred8x8Tm(src, off, stride); break;          // PLANE_PRED8x8 → TM for VP8
            case 4: Pred8x8LeftDc(src, off, stride); break;      // LEFT_DC_PRED8x8
            case 5: Pred8x8TopDc(src, off, stride); break;       // TOP_DC_PRED8x8
            case 6: Pred8x8Dc128(src, off, stride); break;       // DC_128_PRED8x8
            case 7: Pred8x8Dc127(src, off, stride); break;       // DC_127_PRED8x8
            case 8: Pred8x8Dc129(src, off, stride); break;       // DC_129_PRED8x8
            default: Pred8x8Dc128(src, off, stride); break;
        }
    }

    // ==================== Edge emulation mode checks ====================
    // These map VP8 decoded modes to actual prediction modes, handling
    // edge cases where neighbors don't exist (first row, first column).
    // Port of check_intra_pred8x8_mode_emuedge, check_intra_pred4x4_mode_emuedge.

    /// <summary>Check and adjust 16x16/8x8 mode for edge emulation (VP8).</summary>
    public static int CheckIntraPred8x8Mode(int mode, int mbX, int mbY)
    {
        return mode switch
        {
            0 => CheckDcPred8x8(mbX, mbY),           // DC_PRED8x8
            2 => mbY == 0 ? 7 : mode,                // VERT → DC_127 if !mb_y
            1 => mbX == 0 ? 8 : mode,                // HOR → DC_129 if !mb_x
            3 => CheckTmPred8x8(mbX, mbY),           // PLANE/TM
            _ => mode,
        };
    }

    private static int CheckDcPred8x8(int mbX, int mbY)
    {
        if (mbX == 0) return mbY != 0 ? 5 : 6;   // TOP_DC or DC_128
        return mbY != 0 ? 0 : 4;                   // DC or LEFT_DC
    }

    private static int CheckTmPred8x8(int mbX, int mbY)
    {
        if (mbX == 0) return mbY != 0 ? 2 : 8;    // VERT or DC_129
        return mbY != 0 ? 3 : 1;                   // TM or HOR
    }

    /// <summary>
    /// Check and adjust 4x4 mode for edge emulation (VP8).
    /// Returns (adjustedMode, needsCopyBuf).
    /// </summary>
    public static (int mode, bool copyBuf) CheckIntraPred4x4Mode(int mode, int mbX, int mbY)
    {
        switch (mode)
        {
            case 0: // VERT_PRED
                if (mbX == 0 && mbY != 0) return (mode, true);
                goto case 3;
            case 3: // DIAG_DOWN_LEFT
            case 7: // VERT_LEFT
                return mbY == 0 ? (12, false) : (mode, false); // DC_127
            case 1: // HOR_PRED
                if (mbY == 0) return (mode, true);
                goto case 8;
            case 8: // HOR_UP
                return mbX == 0 ? (13, false) : (mode, false); // DC_129
            case 9: // TM_VP8_PRED
                if (mbX == 0) return mbY != 0 ? (10, false) : (13, false); // VERT_VP8 or DC_129
                return mbY != 0 ? (mode, false) : (14, false); // TM or HOR_VP8
            case 2: // DC_PRED (4x4 DC doesn't use H.264-style exceptions)
            case 4: // DIAG_DOWN_RIGHT
            case 5: // VERT_RIGHT
            case 6: // HOR_DOWN
                return (mbY == 0 || mbX == 0) ? (mode, true) : (mode, false);
            default:
                return (mode, false);
        }
    }

    // ==================== Helpers ====================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fill4x4(Span<byte> src, int off, int stride, byte val)
    {
        for (int y = 0; y < 4; y++)
        {
            src[off + y * stride + 0] = val;
            src[off + y * stride + 1] = val;
            src[off + y * stride + 2] = val;
            src[off + y * stride + 3] = val;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fill8x8(Span<byte> src, int off, int stride, byte val)
    {
        for (int y = 0; y < 8; y++)
            src.Slice(off + y * stride, 8).Fill(val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fill16x16(Span<byte> src, int off, int stride, byte val)
    {
        for (int y = 0; y < 16; y++)
            src.Slice(off + y * stride, 16).Fill(val);
    }
}

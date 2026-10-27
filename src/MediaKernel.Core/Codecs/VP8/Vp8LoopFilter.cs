// VP8 loop filter — ported from FFmpeg libavcodec/vp8dsp.c
// Normal and simple deblocking filters for luma and chroma.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.VP8;

/// <summary>
/// VP8 in-loop deblocking filter.
/// Operates on pixel boundaries between 4x4 blocks (inner) and macroblocks (MB edge).
/// </summary>
public static class Vp8LoopFilter
{
    /// <summary>HEV threshold lookup table: [keyframe][filter_level].</summary>
    public static readonly byte[,] HevThreshLut = new byte[2, 64]
    {
        { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1,
          2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
          3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
          3, 3, 3, 3 },
        { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1,
          1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
          2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
          2, 2, 2, 2 }
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClipInt8(int n) => Math.Clamp(n, -128, 127);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClipPixel(int n) => (byte)Math.Clamp(n, 0, 255);

    /// <summary>VP8 simple limit test: 2*|p0-q0| + (|p1-q1| >> 1) &lt;= flim.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SimpleLimit(Span<byte> p, int off, int stride, int flim)
    {
        int p0 = p[off - stride], p1 = p[off - 2 * stride];
        int q0 = p[off], q1 = p[off + stride];
        return 2 * Math.Abs(p0 - q0) + (Math.Abs(p1 - q1) >> 1) <= flim;
    }

    /// <summary>VP8 normal limit: simple_limit AND all inner differences &lt;= I.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NormalLimit(Span<byte> p, int off, int stride, int e, int i)
    {
        int p3 = p[off - 4 * stride], p2 = p[off - 3 * stride];
        int p1 = p[off - 2 * stride], p0 = p[off - stride];
        int q0 = p[off], q1 = p[off + stride];
        int q2 = p[off + 2 * stride], q3 = p[off + 3 * stride];
        return SimpleLimit(p, off, stride, e) &&
               Math.Abs(p3 - p2) <= i && Math.Abs(p2 - p1) <= i &&
               Math.Abs(p1 - p0) <= i && Math.Abs(q3 - q2) <= i &&
               Math.Abs(q2 - q1) <= i && Math.Abs(q1 - q0) <= i;
    }

    /// <summary>High edge variance test.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Hev(Span<byte> p, int off, int stride, int thresh)
    {
        int p1 = p[off - 2 * stride], p0 = p[off - stride];
        int q0 = p[off], q1 = p[off + stride];
        return Math.Abs(p1 - p0) > thresh || Math.Abs(q1 - q0) > thresh;
    }

    /// <summary>
    /// Common filter for both MB-edge (is4tap=true) and inner (is4tap=false) boundaries.
    /// VP8 variant (different from VP7 in f2 calculation).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterCommon(Span<byte> p, int off, int stride, bool is4tap)
    {
        int p0 = p[off - stride], p1 = p[off - 2 * stride];
        int q0 = p[off], q1 = p[off + stride];

        int a = 3 * (q0 - p0);
        if (is4tap)
            a += ClipInt8(p1 - q1);
        a = ClipInt8(a);

        int f1 = Math.Min(a + 4, 127) >> 3;
        int f2 = Math.Min(a + 3, 127) >> 3;

        p[off - stride] = ClipPixel(p0 + f2);
        p[off] = ClipPixel(q0 - f1);

        if (!is4tap)
        {
            a = (f1 + 1) >> 1;
            p[off - 2 * stride] = ClipPixel(p1 + a);
            p[off + stride] = ClipPixel(q1 - a);
        }
    }

    /// <summary>
    /// Wide filter for MB edges. Uses 27/18/9 weights for p2..q2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterMbedge(Span<byte> p, int off, int stride)
    {
        int p2 = p[off - 3 * stride], p1 = p[off - 2 * stride], p0 = p[off - stride];
        int q0 = p[off], q1 = p[off + stride], q2 = p[off + 2 * stride];

        int w = ClipInt8(p1 - q1);
        w = ClipInt8(w + 3 * (q0 - p0));

        int a0 = (27 * w + 63) >> 7;
        int a1 = (18 * w + 63) >> 7;
        int a2 = (9 * w + 63) >> 7;

        p[off - 3 * stride] = ClipPixel(p2 + a2);
        p[off - 2 * stride] = ClipPixel(p1 + a1);
        p[off - stride] = ClipPixel(p0 + a0);
        p[off] = ClipPixel(q0 - a0);
        p[off + stride] = ClipPixel(q1 - a1);
        p[off + 2 * stride] = ClipPixel(q2 - a2);
    }

    // ==================== Vertical filters (horizontal edge) ====================
    // These filter along horizontal MB/block boundaries.
    // stride is linesize; iteration is over columns (stridea=1).

    /// <summary>Vertical 16-pixel MB-edge filter for luma.</summary>
    public static void VLoopFilter16Y(Span<byte> dst, int off, int stride, int flimE, int flimI, int hevThresh)
    {
        for (int i = 0; i < 16; i++)
        {
            int p = off + i;
            if (NormalLimit(dst, p, stride, flimE, flimI))
            {
                if (Hev(dst, p, stride, hevThresh))
                    FilterCommon(dst, p, stride, true);
                else
                    FilterMbedge(dst, p, stride);
            }
        }
    }

    /// <summary>Vertical 16-pixel inner filter for luma.</summary>
    public static void VLoopFilter16YInner(Span<byte> dst, int off, int stride, int flimE, int flimI, int hevThresh)
    {
        for (int i = 0; i < 16; i++)
        {
            int p = off + i;
            if (NormalLimit(dst, p, stride, flimE, flimI))
            {
                bool hv = Hev(dst, p, stride, hevThresh);
                FilterCommon(dst, p, stride, hv);
            }
        }
    }

    /// <summary>Vertical 8-pixel MB-edge filter for chroma (U or V).</summary>
    public static void VLoopFilter8Uv(Span<byte> dstU, int offU, Span<byte> dstV, int offV,
                                       int stride, int flimE, int flimI, int hevThresh)
    {
        VLoopFilter8(dstU, offU, stride, flimE, flimI, hevThresh);
        VLoopFilter8(dstV, offV, stride, flimE, flimI, hevThresh);
    }

    /// <summary>Vertical 8-pixel inner filter for chroma (U or V).</summary>
    public static void VLoopFilter8UvInner(Span<byte> dstU, int offU, Span<byte> dstV, int offV,
                                            int stride, int flimE, int flimI, int hevThresh)
    {
        VLoopFilter8Inner(dstU, offU, stride, flimE, flimI, hevThresh);
        VLoopFilter8Inner(dstV, offV, stride, flimE, flimI, hevThresh);
    }

    private static void VLoopFilter8(Span<byte> dst, int off, int stride, int flimE, int flimI, int hevThresh)
    {
        for (int i = 0; i < 8; i++)
        {
            int p = off + i;
            if (NormalLimit(dst, p, stride, flimE, flimI))
            {
                if (Hev(dst, p, stride, hevThresh))
                    FilterCommon(dst, p, stride, true);
                else
                    FilterMbedge(dst, p, stride);
            }
        }
    }

    private static void VLoopFilter8Inner(Span<byte> dst, int off, int stride, int flimE, int flimI, int hevThresh)
    {
        for (int i = 0; i < 8; i++)
        {
            int p = off + i;
            if (NormalLimit(dst, p, stride, flimE, flimI))
            {
                bool hv = Hev(dst, p, stride, hevThresh);
                FilterCommon(dst, p, stride, hv);
            }
        }
    }

    // ==================== Horizontal filters (vertical edge) ====================
    // These filter along vertical MB/block boundaries.
    // stride is 1 (pixel step); iteration is over rows (stridea=linesize).

    /// <summary>Horizontal 16-pixel MB-edge filter for luma.</summary>
    public static void HLoopFilter16Y(Span<byte> dst, int off, int stride, int flimE, int flimI, int hevThresh)
    {
        for (int i = 0; i < 16; i++)
        {
            int p = off + i * stride;
            if (NormalLimit(dst, p, 1, flimE, flimI))
            {
                if (Hev(dst, p, 1, hevThresh))
                    FilterCommon(dst, p, 1, true);
                else
                    FilterMbedge(dst, p, 1);
            }
        }
    }

    /// <summary>Horizontal 16-pixel inner filter for luma.</summary>
    public static void HLoopFilter16YInner(Span<byte> dst, int off, int stride, int flimE, int flimI, int hevThresh)
    {
        for (int i = 0; i < 16; i++)
        {
            int p = off + i * stride;
            if (NormalLimit(dst, p, 1, flimE, flimI))
            {
                bool hv = Hev(dst, p, 1, hevThresh);
                FilterCommon(dst, p, 1, hv);
            }
        }
    }

    /// <summary>Horizontal 8-pixel MB-edge filter for chroma.</summary>
    public static void HLoopFilter8Uv(Span<byte> dstU, int offU, Span<byte> dstV, int offV,
                                       int stride, int flimE, int flimI, int hevThresh)
    {
        HLoopFilter8(dstU, offU, stride, flimE, flimI, hevThresh);
        HLoopFilter8(dstV, offV, stride, flimE, flimI, hevThresh);
    }

    /// <summary>Horizontal 8-pixel inner filter for chroma.</summary>
    public static void HLoopFilter8UvInner(Span<byte> dstU, int offU, Span<byte> dstV, int offV,
                                            int stride, int flimE, int flimI, int hevThresh)
    {
        HLoopFilter8Inner(dstU, offU, stride, flimE, flimI, hevThresh);
        HLoopFilter8Inner(dstV, offV, stride, flimE, flimI, hevThresh);
    }

    private static void HLoopFilter8(Span<byte> dst, int off, int stride, int flimE, int flimI, int hevThresh)
    {
        for (int i = 0; i < 8; i++)
        {
            int p = off + i * stride;
            if (NormalLimit(dst, p, 1, flimE, flimI))
            {
                if (Hev(dst, p, 1, hevThresh))
                    FilterCommon(dst, p, 1, true);
                else
                    FilterMbedge(dst, p, 1);
            }
        }
    }

    private static void HLoopFilter8Inner(Span<byte> dst, int off, int stride, int flimE, int flimI, int hevThresh)
    {
        for (int i = 0; i < 8; i++)
        {
            int p = off + i * stride;
            if (NormalLimit(dst, p, 1, flimE, flimI))
            {
                bool hv = Hev(dst, p, 1, hevThresh);
                FilterCommon(dst, p, 1, hv);
            }
        }
    }

    // ==================== Simple loop filter (luma only) ====================

    /// <summary>Simple vertical loop filter for 16 luma pixels.</summary>
    public static void VLoopFilterSimple(Span<byte> dst, int off, int stride, int flim)
    {
        for (int i = 0; i < 16; i++)
            if (SimpleLimit(dst, off + i, stride, flim))
                FilterCommon(dst, off + i, stride, true);
    }

    /// <summary>Simple horizontal loop filter for 16 luma pixels.</summary>
    public static void HLoopFilterSimple(Span<byte> dst, int off, int stride, int flim)
    {
        for (int i = 0; i < 16; i++)
            if (SimpleLimit(dst, off + i * stride, 1, flim))
                FilterCommon(dst, off + i * stride, 1, true);
    }

    // ==================== High-level filter dispatch (per MB) ====================

    /// <summary>
    /// Calculate per-MB filter level and interior limit.
    /// Port of filter_level_for_mb from vp8.c.
    /// </summary>
    public static void FilterLevelForMb(Vp8DecoderContext ctx, in Vp8Macroblock mb,
                                         out int filterLevel, out int innerLimit, out bool innerFilter)
    {
        int level;
        if (ctx.Segmentation.Enabled)
        {
            level = ctx.Segmentation.FilterLevel[mb.Segment];
            if (!ctx.Segmentation.AbsoluteVals)
                level += ctx.Filter.Level;
        }
        else
        {
            level = ctx.Filter.Level;
        }

        if (ctx.LfDelta.Enabled)
        {
            level += ctx.LfDelta.Ref[mb.RefFrame];
            level += ctx.LfDelta.Mode[mb.Mode];
        }

        level = Math.Clamp(level, 0, 63);

        int interior = level;
        if (ctx.Filter.Sharpness > 0)
        {
            interior >>= (ctx.Filter.Sharpness + 3) >> 2;
            interior = Math.Min(interior, 9 - ctx.Filter.Sharpness);
        }
        interior = Math.Max(interior, 1);

        filterLevel = level;
        innerLimit = interior;
        innerFilter = mb.Skip == 0 || mb.Mode == (int)Vp8Pred16x16Mode.I4x4 ||
                      mb.Mode == (int)Vp8InterMvMode.Split;
    }

    /// <summary>
    /// Apply normal (full) loop filter to a macroblock.
    /// Port of filter_mb from vp8.c (VP8 path only).
    /// </summary>
    public static void FilterMb(Span<byte> dstY, int offY, int linesize,
                                 Span<byte> dstU, int offU, Span<byte> dstV, int offV, int uvlinesize,
                                 int filterLevel, int innerLimit, bool innerFilter,
                                 bool keyframe, int mbX, int mbY)
    {
        if (filterLevel == 0) return;

        int bedgeLim = filterLevel * 2 + innerLimit;
        int mbedgeLim = bedgeLim + 4;
        int hevThresh = HevThreshLut[keyframe ? 1 : 0, filterLevel];

        // Horizontal MB-edge filter (left edge)
        if (mbX > 0)
        {
            HLoopFilter16Y(dstY, offY, linesize, mbedgeLim, innerLimit, hevThresh);
            HLoopFilter8Uv(dstU, offU, dstV, offV, uvlinesize, mbedgeLim, innerLimit, hevThresh);
        }

        // Horizontal inner filters (at 4, 8, 12 pixels)
        if (innerFilter)
        {
            HLoopFilter16YInner(dstY, offY + 4, linesize, bedgeLim, innerLimit, hevThresh);
            HLoopFilter16YInner(dstY, offY + 8, linesize, bedgeLim, innerLimit, hevThresh);
            HLoopFilter16YInner(dstY, offY + 12, linesize, bedgeLim, innerLimit, hevThresh);
            HLoopFilter8UvInner(dstU, offU + 4, dstV, offV + 4, uvlinesize, bedgeLim, innerLimit, hevThresh);
        }

        // Vertical MB-edge filter (top edge)
        if (mbY > 0)
        {
            VLoopFilter16Y(dstY, offY, linesize, mbedgeLim, innerLimit, hevThresh);
            VLoopFilter8Uv(dstU, offU, dstV, offV, uvlinesize, mbedgeLim, innerLimit, hevThresh);
        }

        // Vertical inner filters (at 4, 8, 12 rows)
        if (innerFilter)
        {
            VLoopFilter16YInner(dstY, offY + 4 * linesize, linesize, bedgeLim, innerLimit, hevThresh);
            VLoopFilter16YInner(dstY, offY + 8 * linesize, linesize, bedgeLim, innerLimit, hevThresh);
            VLoopFilter16YInner(dstY, offY + 12 * linesize, linesize, bedgeLim, innerLimit, hevThresh);
            VLoopFilter8UvInner(dstU, offU + 4 * uvlinesize, dstV, offV + 4 * uvlinesize,
                                uvlinesize, bedgeLim, innerLimit, hevThresh);
        }
    }

    /// <summary>
    /// Apply simple loop filter (luma only) to a macroblock.
    /// Port of filter_mb_simple from vp8.c.
    /// </summary>
    public static void FilterMbSimple(Span<byte> dst, int off, int linesize,
                                       int filterLevel, int innerLimit, bool innerFilter,
                                       int mbX, int mbY)
    {
        if (filterLevel == 0) return;

        int bedgeLim = 2 * filterLevel + innerLimit;
        int mbedgeLim = bedgeLim + 4;

        if (mbX > 0)
            HLoopFilterSimple(dst, off, linesize, mbedgeLim);
        if (innerFilter)
        {
            HLoopFilterSimple(dst, off + 4, linesize, bedgeLim);
            HLoopFilterSimple(dst, off + 8, linesize, bedgeLim);
            HLoopFilterSimple(dst, off + 12, linesize, bedgeLim);
        }

        if (mbY > 0)
            VLoopFilterSimple(dst, off, linesize, mbedgeLim);
        if (innerFilter)
        {
            VLoopFilterSimple(dst, off + 4 * linesize, linesize, bedgeLim);
            VLoopFilterSimple(dst, off + 8 * linesize, linesize, bedgeLim);
            VLoopFilterSimple(dst, off + 12 * linesize, linesize, bedgeLim);
        }
    }
}

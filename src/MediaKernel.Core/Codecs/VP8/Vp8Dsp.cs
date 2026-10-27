// VP8 DSP functions — transforms, motion compensation, loop filter
// Ported from FFmpeg libavcodec/vp8dsp.c

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.VP8;

/// <summary>
/// VP8 DSP operations — IDCT, WHT, MC filters, loop filter.
/// All methods are static for zero-overhead calling.
/// </summary>
public static class Vp8Dsp
{
    // MUL_20091(a) = ((a * 20091) >> 16) + a
    // MUL_35468(a) = (a * 35468) >> 16
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul20091(int a) => ((a * 20091) >> 16) + a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul35468(int a) => (a * 35468) >> 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip8(int v) => (byte)Math.Clamp(v, 0, 255);

    // ==================== Walsh-Hadamard Transform ====================

    /// <summary>
    /// Inverse Walsh-Hadamard transform for luma DC coefficients.
    /// Distributes the 16 DC values across the 16 luma 4x4 sub-blocks.
    /// Port of vp8_luma_dc_wht_c — assigns (not adds) to block DC positions.
    /// </summary>
    public static void LumaDcWht(short[][][] block, short[] dc)
    {
        // Pass 1: column transform (in-place on dc[])
        for (int i = 0; i < 4; i++)
        {
            int t0 = dc[0 * 4 + i] + dc[3 * 4 + i];
            int t1 = dc[1 * 4 + i] + dc[2 * 4 + i];
            int t2 = dc[1 * 4 + i] - dc[2 * 4 + i];
            int t3 = dc[0 * 4 + i] - dc[3 * 4 + i];

            dc[0 * 4 + i] = (short)(t0 + t1);
            dc[1 * 4 + i] = (short)(t3 + t2);
            dc[2 * 4 + i] = (short)(t0 - t1);
            dc[3 * 4 + i] = (short)(t3 - t2);
        }

        // Pass 2: row transform + distribute to sub-blocks, zero dc
        for (int i = 0; i < 4; i++)
        {
            int t0 = dc[i * 4 + 0] + dc[i * 4 + 3] + 3; // rounding
            int t1 = dc[i * 4 + 1] + dc[i * 4 + 2];
            int t2 = dc[i * 4 + 1] - dc[i * 4 + 2];
            int t3 = dc[i * 4 + 0] - dc[i * 4 + 3] + 3; // rounding

            // Zero dc row
            dc[i * 4 + 0] = 0;
            dc[i * 4 + 1] = 0;
            dc[i * 4 + 2] = 0;
            dc[i * 4 + 3] = 0;

            block[i][0][0] = (short)((t0 + t1) >> 3);
            block[i][1][0] = (short)((t3 + t2) >> 3);
            block[i][2][0] = (short)((t0 - t1) >> 3);
            block[i][3][0] = (short)((t3 - t2) >> 3);
        }
    }

    /// <summary>
    /// Simplified WHT for DC-only case (only dc[0] is non-zero).
    /// Port of vp8_luma_dc_wht_dc_c.
    /// </summary>
    public static void LumaDcWhtDc(short[][][] block, short[] dc)
    {
        short val = (short)((dc[0] + 3) >> 3);
        dc[0] = 0;

        for (int i = 0; i < 4; i++)
        {
            block[i][0][0] = val;
            block[i][1][0] = val;
            block[i][2][0] = val;
            block[i][3][0] = val;
        }
    }

    // ==================== 4x4 IDCT ====================

    /// <summary>
    /// VP8 4x4 inverse DCT + add to destination.
    /// Port of vp8_idct_add_c. Zeroes the block after transform.
    /// </summary>
    public static void IdctAdd(Span<byte> dst, int stride, short[] block)
    {
        Span<short> tmp = stackalloc short[16];

        // Pass 1: column transform (read columns from block, write rows to tmp)
        for (int i = 0; i < 4; i++)
        {
            int t0 = block[0 * 4 + i] + block[2 * 4 + i];
            int t1 = block[0 * 4 + i] - block[2 * 4 + i];
            int t2 = Mul35468(block[1 * 4 + i]) - Mul20091(block[3 * 4 + i]);
            int t3 = Mul20091(block[1 * 4 + i]) + Mul35468(block[3 * 4 + i]);

            // Zero block column
            block[0 * 4 + i] = 0;
            block[1 * 4 + i] = 0;
            block[2 * 4 + i] = 0;
            block[3 * 4 + i] = 0;

            tmp[i * 4 + 0] = (short)(t0 + t3);
            tmp[i * 4 + 1] = (short)(t1 + t2);
            tmp[i * 4 + 2] = (short)(t1 - t2);
            tmp[i * 4 + 3] = (short)(t0 - t3);
        }

        // Pass 2: row transform + add to dst
        for (int i = 0; i < 4; i++)
        {
            int t0 = tmp[0 * 4 + i] + tmp[2 * 4 + i];
            int t1 = tmp[0 * 4 + i] - tmp[2 * 4 + i];
            int t2 = Mul35468(tmp[1 * 4 + i]) - Mul20091(tmp[3 * 4 + i]);
            int t3 = Mul20091(tmp[1 * 4 + i]) + Mul35468(tmp[3 * 4 + i]);

            dst[0] = Clip8(dst[0] + ((t0 + t3 + 4) >> 3));
            dst[1] = Clip8(dst[1] + ((t1 + t2 + 4) >> 3));
            dst[2] = Clip8(dst[2] + ((t1 - t2 + 4) >> 3));
            dst[3] = Clip8(dst[3] + ((t0 - t3 + 4) >> 3));
            dst = dst[stride..];
        }
    }

    /// <summary>
    /// VP8 4x4 DC-only IDCT + add (when only DC coefficient is non-zero).
    /// Port of vp8_idct_dc_add_c. Zeroes block[0] after transform.
    /// </summary>
    public static void IdctDcAdd(Span<byte> dst, int stride, short[] block)
    {
        int dc = (block[0] + 4) >> 3;
        block[0] = 0;

        for (int i = 0; i < 4; i++)
        {
            dst[0] = Clip8(dst[0] + dc);
            dst[1] = Clip8(dst[1] + dc);
            dst[2] = Clip8(dst[2] + dc);
            dst[3] = Clip8(dst[3] + dc);
            dst = dst[stride..];
        }
    }

    /// <summary>
    /// Apply IDCT DC-only for 4 consecutive luma blocks in a row.
    /// Port of vp8_idct_dc_add4y_c.
    /// </summary>
    public static void IdctDcAdd4Y(Span<byte> dst, short[][] blocks, int stride)
    {
        IdctDcAdd(dst, stride, blocks[0]);
        IdctDcAdd(dst[4..], stride, blocks[1]);
        IdctDcAdd(dst[8..], stride, blocks[2]);
        IdctDcAdd(dst[12..], stride, blocks[3]);
    }

    /// <summary>
    /// Apply IDCT DC-only for 4 chroma blocks (2x2 arrangement).
    /// Port of vp8_idct_dc_add4uv_c.
    /// </summary>
    public static void IdctDcAdd4Uv(Span<byte> dst, short[][] blocks, int stride)
    {
        IdctDcAdd(dst, stride, blocks[0]);
        IdctDcAdd(dst[4..], stride, blocks[1]);
        IdctDcAdd(dst.Slice(stride * 4), stride, blocks[2]);
        IdctDcAdd(dst.Slice(stride * 4 + 4), stride, blocks[3]);
    }

    // ==================== Motion Compensation ====================
    // 6-tap, 4-tap, and bilinear subpel interpolation filters.
    // Ported from FFmpeg vp8dsp.c VP8_EPEL_H/V/HV and VP8_BILINEAR macros.

    /// <summary>VP8 6-tap subpel filter coefficients [7 fractional positions][6 taps].</summary>
    public static readonly byte[,] SubpelFilters = new byte[7, 6]
    {
        { 0,  6, 123,  12,  1, 0 },
        { 2, 11, 108,  36,  8, 1 },
        { 0,  9,  93,  50,  6, 0 },
        { 3, 16,  77,  77, 16, 3 },
        { 0,  6,  50,  93,  9, 0 },
        { 1,  8,  36, 108, 11, 2 },
        { 0,  1,  12, 123,  6, 0 },
    };

    /// <summary>
    /// Subpel index table [3][8]:
    /// [0] = left extra pixels (also selects 4-tap vs 6-tap)
    /// [1] = total extra pixels needed
    /// [2] = right extra pixels
    /// </summary>
    public static readonly byte[,] SubpelIdx = new byte[3, 8]
    {
        { 0, 1, 2, 1, 2, 1, 2, 1 },
        { 0, 3, 5, 3, 5, 3, 5, 3 },
        { 0, 2, 3, 2, 3, 2, 3, 2 },
    };

    // FILTER_6TAP: cm[(F2*src[0] - F1*src[-1] + F0*src[-2] + F3*src[+1] - F4*src[+2] + F5*src[+3] + 64) >> 7]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Filter6Tap(ReadOnlySpan<byte> src, int x, int srcStride, ReadOnlySpan<byte> f)
    {
        int val = f[2] * src[x] - f[1] * src[x - srcStride] +
                  f[0] * src[x - 2 * srcStride] + f[3] * src[x + srcStride] -
                  f[4] * src[x + 2 * srcStride] + f[5] * src[x + 3 * srcStride] + 64;
        return Clip8(val >> 7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Filter4Tap(ReadOnlySpan<byte> src, int x, int srcStride, ReadOnlySpan<byte> f)
    {
        int val = f[2] * src[x] - f[1] * src[x - srcStride] +
                  f[3] * src[x + srcStride] - f[4] * src[x + 2 * srcStride] + 64;
        return Clip8(val >> 7);
    }

    /// <summary>Copy WxH pixels (full-pel MC, no interpolation).</summary>
    public static void PutPixels(Span<byte> dst, int dstOff, int dstStride,
                                  ReadOnlySpan<byte> src, int srcOff, int srcStride, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            src.Slice(srcOff, w).CopyTo(dst.Slice(dstOff, w));
            dstOff += dstStride;
            srcOff += srcStride;
        }
    }

    /// <summary>
    /// Horizontal-only subpel MC with 6-tap filter.
    /// </summary>
    public static void EpelH6(Span<byte> dst, int dstOff, int dstStride,
                               ReadOnlySpan<byte> src, int srcOff, int srcStride,
                               int w, int h, int mx)
    {
        ReadOnlySpan<byte> filter = GetFilter(mx);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                dst[dstOff + x] = Filter6Tap(src, srcOff + x, 1, filter);
            dstOff += dstStride;
            srcOff += srcStride;
        }
    }

    /// <summary>Horizontal-only subpel MC with 4-tap filter.</summary>
    public static void EpelH4(Span<byte> dst, int dstOff, int dstStride,
                               ReadOnlySpan<byte> src, int srcOff, int srcStride,
                               int w, int h, int mx)
    {
        ReadOnlySpan<byte> filter = GetFilter(mx);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                dst[dstOff + x] = Filter4Tap(src, srcOff + x, 1, filter);
            dstOff += dstStride;
            srcOff += srcStride;
        }
    }

    /// <summary>Vertical-only subpel MC with 6-tap filter.</summary>
    public static void EpelV6(Span<byte> dst, int dstOff, int dstStride,
                               ReadOnlySpan<byte> src, int srcOff, int srcStride,
                               int w, int h, int my)
    {
        ReadOnlySpan<byte> filter = GetFilter(my);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                dst[dstOff + x] = Filter6Tap(src, srcOff + x, srcStride, filter);
            dstOff += dstStride;
            srcOff += srcStride;
        }
    }

    /// <summary>Vertical-only subpel MC with 4-tap filter.</summary>
    public static void EpelV4(Span<byte> dst, int dstOff, int dstStride,
                               ReadOnlySpan<byte> src, int srcOff, int srcStride,
                               int w, int h, int my)
    {
        ReadOnlySpan<byte> filter = GetFilter(my);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                dst[dstOff + x] = Filter4Tap(src, srcOff + x, srcStride, filter);
            dstOff += dstStride;
            srcOff += srcStride;
        }
    }

    /// <summary>
    /// Combined H+V subpel MC. First pass applies horizontal filter to temp buffer,
    /// then vertical filter reads from temp.
    /// </summary>
    public static void EpelHV(Span<byte> dst, int dstOff, int dstStride,
                               ReadOnlySpan<byte> src, int srcOff, int srcStride,
                               int w, int h, int mx, int my, int hTaps, int vTaps)
    {
        // Temp buffer: (h + vTaps - 1) rows of w pixels
        int tmpH = h + vTaps - 1;
        Span<byte> tmp = stackalloc byte[tmpH * w];

        // Horizontal filter pass into temp
        ReadOnlySpan<byte> hFilter = GetFilter(mx);
        int vOffset = (vTaps == 6) ? 2 : 1; // extra rows above for vertical pass
        int hSrcOff = srcOff - vOffset * srcStride;
        for (int y = 0; y < tmpH; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (hTaps == 6)
                    tmp[y * w + x] = Filter6Tap(src, hSrcOff + x, 1, hFilter);
                else
                    tmp[y * w + x] = Filter4Tap(src, hSrcOff + x, 1, hFilter);
            }
            hSrcOff += srcStride;
        }

        // Vertical filter pass from temp to dst
        ReadOnlySpan<byte> vFilter = GetFilter(my);
        int tmpOff = vOffset * w;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (vTaps == 6)
                    dst[dstOff + x] = Filter6Tap(tmp, tmpOff + x, w, vFilter);
                else
                    dst[dstOff + x] = Filter4Tap(tmp, tmpOff + x, w, vFilter);
            }
            dstOff += dstStride;
            tmpOff += w;
        }
    }

    /// <summary>Bilinear H-only interpolation.</summary>
    public static void BilinearH(Span<byte> dst, int dstOff, int dstStride,
                                  ReadOnlySpan<byte> src, int srcOff, int srcStride,
                                  int w, int h, int mx)
    {
        int a = 8 - mx, b = mx;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                dst[dstOff + x] = (byte)((a * src[srcOff + x] + b * src[srcOff + x + 1] + 4) >> 3);
            dstOff += dstStride;
            srcOff += srcStride;
        }
    }

    /// <summary>Bilinear V-only interpolation.</summary>
    public static void BilinearV(Span<byte> dst, int dstOff, int dstStride,
                                  ReadOnlySpan<byte> src, int srcOff, int srcStride,
                                  int w, int h, int my)
    {
        int c = 8 - my, d = my;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                dst[dstOff + x] = (byte)((c * src[srcOff + x] + d * src[srcOff + x + srcStride] + 4) >> 3);
            dstOff += dstStride;
            srcOff += srcStride;
        }
    }

    /// <summary>Bilinear HV combined interpolation.</summary>
    public static void BilinearHV(Span<byte> dst, int dstOff, int dstStride,
                                   ReadOnlySpan<byte> src, int srcOff, int srcStride,
                                   int w, int h, int mx, int my)
    {
        int a = 8 - mx, b = mx;
        int c = 8 - my, d = my;
        Span<byte> tmp = stackalloc byte[(h + 1) * w];

        // Horizontal pass
        for (int y = 0; y < h + 1; y++)
        {
            for (int x = 0; x < w; x++)
                tmp[y * w + x] = (byte)((a * src[srcOff + x] + b * src[srcOff + x + 1] + 4) >> 3);
            srcOff += srcStride;
        }

        // Vertical pass
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                dst[dstOff + x] = (byte)((c * tmp[y * w + x] + d * tmp[(y + 1) * w + x] + 4) >> 3);
            dstOff += dstStride;
        }
    }

    /// <summary>
    /// Unified MC dispatch. Selects full-pel copy, H-only, V-only, or HV based on mx/my.
    /// mxIdx/myIdx come from SubpelIdx[0][mx/my].
    /// For bilinear mode, mxIdx/myIdx are non-zero when mx/my are non-zero.
    /// </summary>
    public static void McBlock(Span<byte> dst, int dstOff, int dstStride,
                                ReadOnlySpan<byte> src, int srcOff, int srcStride,
                                int w, int h, int mx, int my, int mxIdx, int myIdx, bool bilinear)
    {
        if (bilinear)
        {
            if (mxIdx == 0 && myIdx == 0) PutPixels(dst, dstOff, dstStride, src, srcOff, srcStride, w, h);
            else if (myIdx == 0) BilinearH(dst, dstOff, dstStride, src, srcOff, srcStride, w, h, mx);
            else if (mxIdx == 0) BilinearV(dst, dstOff, dstStride, src, srcOff, srcStride, w, h, my);
            else BilinearHV(dst, dstOff, dstStride, src, srcOff, srcStride, w, h, mx, my);
        }
        else
        {
            if (mxIdx == 0 && myIdx == 0)
            {
                PutPixels(dst, dstOff, dstStride, src, srcOff, srcStride, w, h);
            }
            else if (myIdx == 0)
            {
                if (mxIdx == 1) EpelH4(dst, dstOff, dstStride, src, srcOff, srcStride, w, h, mx);
                else EpelH6(dst, dstOff, dstStride, src, srcOff, srcStride, w, h, mx);
            }
            else if (mxIdx == 0)
            {
                if (myIdx == 1) EpelV4(dst, dstOff, dstStride, src, srcOff, srcStride, w, h, my);
                else EpelV6(dst, dstOff, dstStride, src, srcOff, srcStride, w, h, my);
            }
            else
            {
                int hTaps = (mxIdx == 1) ? 4 : 6;
                int vTaps = (myIdx == 1) ? 4 : 6;
                EpelHV(dst, dstOff, dstStride, src, srcOff, srcStride, w, h, mx, my, hTaps, vTaps);
            }
        }
    }

    /// <summary>
    /// Edge emulation: copies a block from the source with clamped coordinates
    /// for out-of-bounds regions. Port of emulated_edge_mc.
    /// </summary>
    public static void EmulatedEdgeMc(Span<byte> buf, int bufStride,
                                       ReadOnlySpan<byte> src, int srcOff, int srcStride,
                                       int blockW, int blockH,
                                       int srcX, int srcY, int width, int height)
    {
        // srcOff points to position (srcX, srcY) in the reference frame buffer.
        // Compute offset to frame origin (0,0) so we can index with absolute coords.
        int originOff = srcOff - srcY * srcStride - srcX;

        for (int y = 0; y < blockH; y++)
        {
            int sy = Math.Clamp(srcY + y, 0, height - 1);
            int dstRowOff = y * bufStride;

            for (int x = 0; x < blockW; x++)
            {
                int sx = Math.Clamp(srcX + x, 0, width - 1);
                buf[dstRowOff + x] = src[originOff + sy * srcStride + sx];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> GetFilter(int frac)
    {
        // Get a row from SubpelFilters. frac is 1..7 (never 0 for fractional)
        // We use a flat span since 2D arrays don't support slicing.
        return SubpelFiltersFlat.AsSpan((frac - 1) * 6, 6);
    }

    // Flat representation of SubpelFilters for use with ReadOnlySpan slicing
    private static readonly byte[] SubpelFiltersFlat =
    {
        0,  6, 123,  12,  1, 0,
        2, 11, 108,  36,  8, 1,
        0,  9,  93,  50,  6, 0,
        3, 16,  77,  77, 16, 3,
        0,  6,  50,  93,  9, 0,
        1,  8,  36, 108, 11, 2,
        0,  1,  12, 123,  6, 0,
    };
}

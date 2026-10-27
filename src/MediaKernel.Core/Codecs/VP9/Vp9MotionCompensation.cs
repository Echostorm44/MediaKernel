// VP9 motion compensation — 8-tap subpixel interpolation filters
// Ported from FFmpeg libavcodec/vp9dsp.c + vp9dsp_template.c

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vp9;

/// <summary>
/// VP9 motion compensation using 8-tap subpixel filters.
/// Three filter types: Regular, Sharp, Smooth + Bilinear.
/// Port of FFmpeg's ff_vp9_subpel_filters and do_8tap_1d_c/do_8tap_2d_c.
/// </summary>
public static class Vp9MotionCompensation
{
    /// <summary>
    /// 8-tap subpixel filter coefficients: [4 types][16 phases][8 taps].
    /// Types: 0=Smooth, 1=Regular, 2=Sharp, 3=Bilinear (padded to 8-tap).
    /// Phase 0 = integer position (128 at center tap).
    /// </summary>
    public static readonly short[][][] SubpelFilters = new short[][][]
    {
        // Index 0 = FILTER_8TAP_SMOOTH (matches Vp9FilterMode.EightTapSmooth = 0)
        new short[][]
        {
            new short[] {  0,  0,   0, 128,   0,   0,  0,  0 },
            new short[] { -3, -1,  32,  64,  38,   1, -3,  0 },
            new short[] { -2, -2,  29,  63,  41,   2, -3,  0 },
            new short[] { -2, -2,  26,  63,  43,   4, -4,  0 },
            new short[] { -2, -3,  24,  62,  46,   5, -4,  0 },
            new short[] { -2, -3,  21,  60,  49,   7, -4,  0 },
            new short[] { -1, -4,  18,  59,  51,   9, -4,  0 },
            new short[] { -1, -4,  16,  57,  53,  12, -4, -1 },
            new short[] { -1, -4,  14,  55,  55,  14, -4, -1 },
            new short[] { -1, -4,  12,  53,  57,  16, -4, -1 },
            new short[] {  0, -4,   9,  51,  59,  18, -4, -1 },
            new short[] {  0, -4,   7,  49,  60,  21, -3, -2 },
            new short[] {  0, -4,   5,  46,  62,  24, -3, -2 },
            new short[] {  0, -4,   4,  43,  63,  26, -2, -2 },
            new short[] {  0, -3,   2,  41,  63,  29, -2, -2 },
            new short[] {  0, -3,   1,  38,  64,  32, -1, -3 },
        },
        // Index 1 = FILTER_8TAP_REGULAR (matches Vp9FilterMode.EightTapRegular = 1)
        new short[][]
        {
            new short[] {  0,  0,   0, 128,   0,   0,  0,  0 },
            new short[] {  0,  1,  -5, 126,   8,  -3,  1,  0 },
            new short[] { -1,  3, -10, 122,  18,  -6,  2,  0 },
            new short[] { -1,  4, -13, 118,  27,  -9,  3, -1 },
            new short[] { -1,  4, -16, 112,  37, -11,  4, -1 },
            new short[] { -1,  5, -18, 105,  48, -14,  4, -1 },
            new short[] { -1,  5, -19,  97,  58, -16,  5, -1 },
            new short[] { -1,  6, -19,  88,  68, -18,  5, -1 },
            new short[] { -1,  6, -19,  78,  78, -19,  6, -1 },
            new short[] { -1,  5, -18,  68,  88, -19,  6, -1 },
            new short[] { -1,  5, -16,  58,  97, -19,  5, -1 },
            new short[] { -1,  4, -14,  48, 105, -18,  5, -1 },
            new short[] { -1,  4, -11,  37, 112, -16,  4, -1 },
            new short[] { -1,  3,  -9,  27, 118, -13,  4, -1 },
            new short[] {  0,  2,  -6,  18, 122, -10,  3, -1 },
            new short[] {  0,  1,  -3,   8, 126,  -5,  1,  0 },
        },
        // Index 2 = FILTER_8TAP_SHARP (matches Vp9FilterMode.EightTapSharp = 2)
        new short[][]
        {
            new short[] {  0,  0,   0, 128,   0,   0,  0,  0 },
            new short[] { -1,  3,  -7, 127,   8,  -3,  1,  0 },
            new short[] { -2,  5, -13, 125,  17,  -6,  3, -1 },
            new short[] { -3,  7, -17, 121,  27, -10,  5, -2 },
            new short[] { -4,  9, -20, 115,  37, -13,  6, -2 },
            new short[] { -4, 10, -23, 108,  48, -16,  8, -3 },
            new short[] { -4, 10, -24, 100,  59, -19,  9, -3 },
            new short[] { -4, 11, -24,  90,  70, -21, 10, -4 },
            new short[] { -4, 11, -23,  80,  80, -23, 11, -4 },
            new short[] { -4, 10, -21,  70,  90, -24, 11, -4 },
            new short[] { -3,  9, -19,  59, 100, -24, 10, -4 },
            new short[] { -3,  8, -16,  48, 108, -23, 10, -4 },
            new short[] { -2,  6, -13,  37, 115, -20,  9, -4 },
            new short[] { -2,  5, -10,  27, 121, -17,  7, -3 },
            new short[] { -1,  3,  -6,  17, 125, -13,  5, -2 },
            new short[] {  0,  1,  -3,   8, 127,  -7,  3, -1 },
        },
        // Index 3 = FILTER_BILINEAR (matches Vp9FilterMode.Bilinear = 3)
        // 2-tap bilinear coefficients padded to 8-tap format (zeros on unused taps)
        new short[][]
        {
            new short[] { 0, 0, 0, 128,   0, 0, 0, 0 },
            new short[] { 0, 0, 0, 120,   8, 0, 0, 0 },
            new short[] { 0, 0, 0, 112,  16, 0, 0, 0 },
            new short[] { 0, 0, 0, 104,  24, 0, 0, 0 },
            new short[] { 0, 0, 0,  96,  32, 0, 0, 0 },
            new short[] { 0, 0, 0,  88,  40, 0, 0, 0 },
            new short[] { 0, 0, 0,  80,  48, 0, 0, 0 },
            new short[] { 0, 0, 0,  72,  56, 0, 0, 0 },
            new short[] { 0, 0, 0,  64,  64, 0, 0, 0 },
            new short[] { 0, 0, 0,  56,  72, 0, 0, 0 },
            new short[] { 0, 0, 0,  48,  80, 0, 0, 0 },
            new short[] { 0, 0, 0,  40,  88, 0, 0, 0 },
            new short[] { 0, 0, 0,  32,  96, 0, 0, 0 },
            new short[] { 0, 0, 0,  24, 104, 0, 0, 0 },
            new short[] { 0, 0, 0,  16, 112, 0, 0, 0 },
            new short[] { 0, 0, 0,   8, 120, 0, 0, 0 },
        },
    };

    /// <summary>
    /// Bilinear filter coefficients: [16 phases][2 taps].
    /// </summary>
    public static readonly short[][] BilinearFilters = new short[][]
    {
        new short[] { 128,   0 },
        new short[] { 120,   8 },
        new short[] { 112,  16 },
        new short[] { 104,  24 },
        new short[] {  96,  32 },
        new short[] {  88,  40 },
        new short[] {  80,  48 },
        new short[] {  72,  56 },
        new short[] {  64,  64 },
        new short[] {  56,  72 },
        new short[] {  48,  80 },
        new short[] {  40,  88 },
        new short[] {  32,  96 },
        new short[] {  24, 104 },
        new short[] {  16, 112 },
        new short[] {   8, 120 },
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClipPixel(int val) => (byte)Math.Clamp(val, 0, 255);

    /// <summary>
    /// Apply 8-tap filter at a single sample position.
    /// Uses byte[] + index to support negative offsets (like C pointer arithmetic).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Filter8Tap(byte[] src, int idx, short[] f, int stride)
    {
        return f[0] * src[idx - 3 * stride] +
               f[1] * src[idx - 2 * stride] +
               f[2] * src[idx - 1 * stride] +
               f[3] * src[idx + 0 * stride] +
               f[4] * src[idx + 1 * stride] +
               f[5] * src[idx + 2 * stride] +
               f[6] * src[idx + 3 * stride] +
               f[7] * src[idx + 4 * stride];
    }

    /// <summary>
    /// The same filter over a span, for callers whose source is a stack buffer.
    /// </summary>
    /// <remarks>
    /// This overload exists so the two-dimensional filter's vertical pass can read its intermediate
    /// buffer where it lies. Without it the buffer had to be copied to the heap with ToArray to
    /// match the array overload - four and a half kilobytes per motion-compensated block, which at
    /// 1080p is tens of megabytes a frame.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Filter8Tap(ReadOnlySpan<byte> src, int idx, short[] f, int stride)
    {
        return f[0] * src[idx - 3 * stride] +
               f[1] * src[idx - 2 * stride] +
               f[2] * src[idx - 1 * stride] +
               f[3] * src[idx + 0 * stride] +
               f[4] * src[idx + 1 * stride] +
               f[5] * src[idx + 2 * stride] +
               f[6] * src[idx + 3 * stride] +
               f[7] * src[idx + 4 * stride];
    }

    /// <summary>
    /// 1D 8-tap horizontal or vertical filter (put or average).
    /// ds = 1 for horizontal, srcStride for vertical.
    /// src/srcOff: source array + offset to block center (supports backward reads for filter taps).
    /// </summary>
    public static void Filter8Tap1d(
        Span<byte> dst, int dstStride,
        byte[] src, int srcOff, int srcStride,
        int w, int h, int ds,
        short[] filter, bool avg)
    {
        int dstOffset = 0;
        for (int y = 0; y < h; y++)
        {
            int rowOff = srcOff + y * srcStride;
            for (int x = 0; x < w; x++)
            {
                int rawSum = Filter8Tap(src, rowOff + x, filter, ds);
                int val = ClipPixel((rawSum + 64) >> 7);
                if (DiagMcTrace && y == 0 && x == 7)
                {
                    int idx = rowOff + x;
                    Console.WriteLine($"  MC1d y={y} x={x} idx={idx} ds={ds} src[{idx-3*ds}..{idx+4*ds}]=" +
                        $"{src[idx-3*ds]:X2} {src[idx-2*ds]:X2} {src[idx-ds]:X2} {src[idx]:X2} " +
                        $"{src[idx+ds]:X2} {src[idx+2*ds]:X2} {src[idx+3*ds]:X2} {src[idx+4*ds]:X2}" +
                        $" f=[{filter[0]},{filter[1]},{filter[2]},{filter[3]},{filter[4]},{filter[5]},{filter[6]},{filter[7]}]" +
                        $" rawSum={rawSum} val={val}");
                }
                if (avg)
                    dst[dstOffset + x] = (byte)((dst[dstOffset + x] + val + 1) >> 1);
                else
                    dst[dstOffset + x] = (byte)val;
            }
            dstOffset += dstStride;
        }
    }

    public static bool DiagMcTrace;

    /// <summary>
    /// 2D 8-tap filter: horizontal pass to temp buffer, then vertical pass to dst.
    /// src/srcOff: source array + offset to block center.
    /// </summary>
    public static void Filter8Tap2d(
        Span<byte> dst, int dstStride,
        byte[] src, int srcOff, int srcStride,
        int w, int h,
        short[] filterX, short[] filterY, bool avg)
    {
        int tmpH = h + 7;
        Span<byte> tmp = stackalloc byte[64 * 71];

        // Horizontal pass: starting 3 rows above the block center
        for (int y = 0; y < tmpH; y++)
        {
            int rowOff = srcOff + (y - 3) * srcStride;
            int tmpRow = y * 64;
            for (int x = 0; x < w; x++)
                tmp[tmpRow + x] = ClipPixel((Filter8Tap(src, rowOff + x, filterX, 1) + 64) >> 7);
        }

        // Vertical pass, reading the intermediate buffer in place.
        int tmpStart = 3 * 64;
        int dstOffset = 0;
        for (int y = 0; y < h; y++)
        {
            int tmpRowOff = tmpStart + y * 64;
            for (int x = 0; x < w; x++)
            {
                int val = ClipPixel((Filter8Tap(tmp, tmpRowOff + x, filterY, 64) + 64) >> 7);
                if (avg)
                    dst[dstOffset + x] = (byte)((dst[dstOffset + x] + val + 1) >> 1);
                else
                    dst[dstOffset + x] = (byte)val;
            }
            dstOffset += dstStride;
        }
    }

    /// <summary>
    /// High-level MC function: applies subpixel filter based on MV fractional parts.
    /// src/srcOff: source array + offset to block center (filter reads 3 pixels/rows backward).
    /// mx/my: horizontal/vertical subpixel offset (0-15, where 0 = integer position).
    /// filterType: 0=regular, 1=sharp, 2=smooth.
    /// </summary>
    public static void McPut(
        Span<byte> dst, int dstStride,
        byte[] src, int srcOff, int srcStride,
        int w, int h, int mx, int my, int filterType)
    {
        if (mx == 0 && my == 0)
        {
            for (int y = 0; y < h; y++)
                src.AsSpan(srcOff + y * srcStride, w).CopyTo(dst.Slice(y * dstStride, w));
        }
        else if (my == 0)
        {
            Filter8Tap1d(dst, dstStride, src, srcOff, srcStride, w, h, 1,
                SubpelFilters[filterType][mx], false);
        }
        else if (mx == 0)
        {
            Filter8Tap1d(dst, dstStride, src, srcOff, srcStride, w, h, srcStride,
                SubpelFilters[filterType][my], false);
        }
        else
        {
            Filter8Tap2d(dst, dstStride, src, srcOff, srcStride, w, h,
                SubpelFilters[filterType][mx], SubpelFilters[filterType][my], false);
        }
    }

    /// <summary>
    /// MC average: same as McPut but averages with existing destination pixels.
    /// </summary>
    public static void McAvg(
        Span<byte> dst, int dstStride,
        byte[] src, int srcOff, int srcStride,
        int w, int h, int mx, int my, int filterType)
    {
        if (mx == 0 && my == 0)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    dst[y * dstStride + x] = (byte)((dst[y * dstStride + x] + src[srcOff + y * srcStride + x] + 1) >> 1);
        }
        else if (my == 0)
        {
            Filter8Tap1d(dst, dstStride, src, srcOff, srcStride, w, h, 1,
                SubpelFilters[filterType][mx], true);
        }
        else if (mx == 0)
        {
            Filter8Tap1d(dst, dstStride, src, srcOff, srcStride, w, h, srcStride,
                SubpelFilters[filterType][my], true);
        }
        else
        {
            Filter8Tap2d(dst, dstStride, src, srcOff, srcStride, w, h,
                SubpelFilters[filterType][mx], SubpelFilters[filterType][my], true);
        }
    }

    /// <summary>
    /// Emulate edge for motion compensation when the reference block extends beyond frame boundaries.
    /// Port of ff_emulated_edge_mc from FFmpeg videodsp_template.c.
    /// Copies valid pixels and edge-extends (clamps) pixels outside the frame.
    /// </summary>
    /// <param name="buf">Destination buffer</param>
    /// <param name="bufLinesize">Stride of destination buffer</param>
    /// <param name="src">Source frame buffer</param>
    /// <param name="srcLinesize">Stride of source frame</param>
    /// <param name="blockW">Width of block to copy</param>
    /// <param name="blockH">Height of block to copy</param>
    /// <param name="srcX">X position in source frame (may be negative)</param>
    /// <param name="srcY">Y position in source frame (may be negative)</param>
    /// <param name="w">Frame width</param>
    /// <param name="h">Frame height</param>
    public static void EmulateEdgeMc(
        Span<byte> buf, int bufLinesize,
        ReadOnlySpan<byte> src, int srcLinesize,
        int blockW, int blockH,
        int srcX, int srcY, int w, int h)
    {
        if (w == 0 || h == 0) return;

        // Clamp extreme cases
        if (srcY >= h)
        {
            srcY = h - 1;
        }
        else if (srcY <= -blockH)
        {
            srcY = 1 - blockH;
        }

        if (srcX >= w)
        {
            srcX = w - 1;
        }
        else if (srcX <= -blockW)
        {
            srcX = 1 - blockW;
        }

        int startY = Math.Max(0, -srcY);
        int startX = Math.Max(0, -srcX);
        int endY = Math.Min(blockH, h - srcY);
        int endX = Math.Min(blockW, w - srcX);
        int copyW = endX - startX;

        // Offset src to the first valid pixel we'll copy
        int srcOff = (srcY + startY) * srcLinesize + (srcX + startX);

        // Top rows: copy the first valid row
        int bufOff = startX;
        for (int y = 0; y < startY; y++)
        {
            src.Slice(srcOff, copyW).CopyTo(buf.Slice(bufOff, copyW));
            bufOff += bufLinesize;
        }

        // Middle rows: copy valid source data
        int curSrcOff = srcOff;
        for (int y = startY; y < endY; y++)
        {
            src.Slice(curSrcOff, copyW).CopyTo(buf.Slice(bufOff, copyW));
            curSrcOff += srcLinesize;
            bufOff += bufLinesize;
        }

        // Bottom rows: repeat last valid row
        curSrcOff -= srcLinesize; // back to last valid row
        for (int y = endY; y < blockH; y++)
        {
            src.Slice(curSrcOff, copyW).CopyTo(buf.Slice(bufOff, copyW));
            bufOff += bufLinesize;
        }

        // Left and right edge extension
        bufOff = 0;
        for (int y = 0; y < blockH; y++)
        {
            byte leftPixel = buf[bufOff + startX];
            for (int x = 0; x < startX; x++)
                buf[bufOff + x] = leftPixel;

            byte rightPixel = buf[bufOff + endX - 1];
            for (int x = endX; x < blockW; x++)
                buf[bufOff + x] = rightPixel;

            bufOff += bufLinesize;
        }
    }
}

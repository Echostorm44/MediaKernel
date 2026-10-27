using System;

namespace MediaKernel.Core.Codecs.Mpeg4Video;

/// <summary>
/// FFmpeg's <c>mpeg4videodsp.c</c>: the global motion compensation MPEG-4 Part 2 uses for
/// sprites and GMC macroblocks.
/// </summary>
public static class Mpeg4VideoDsp
{
    /// <summary>
    /// gmc1_c: the one-warping-point case, which is a plain bilinear interpolation at
    /// sixteenth-pel precision over an 8-wide block.
    /// </summary>
    public static void Gmc1(Span<byte> dst, int dstOff, ReadOnlySpan<byte> src, int srcOff,
                            int stride, int h, int x16, int y16, int rounder)
    {
        int a = (16 - x16) * (16 - y16);
        int b = x16 * (16 - y16);
        int c = (16 - x16) * y16;
        int d = x16 * y16;

        for (int i = 0; i < h; i++)
        {
            for (int x = 0; x < 8; x++)
            {
                dst[dstOff + x] = (byte)((a * src[srcOff + x]
                                        + b * src[srcOff + x + 1]
                                        + c * src[srcOff + stride + x]
                                        + d * src[srcOff + stride + x + 1]
                                        + rounder) >> 8);
            }
            dstOff += stride;
            srcOff += stride;
        }
    }

    /// <summary>
    /// ff_gmc_c: the general affine warp, bilinear at <c>1 &lt;&lt; shift</c> precision.
    /// </summary>
    /// <remarks>
    /// The three edge branches are not an optimisation of one formula. When the sample falls
    /// outside the picture the C drops the interpolation in that axis entirely and multiplies
    /// by the whole step <c>s</c> instead of by the two fractions, so an edge pixel is not what
    /// clamping the coordinates and interpolating anyway would give. Both <c>width</c> and
    /// <c>height</c> are decremented on entry, which is why the comparisons are strict.
    /// </remarks>
    public static void Gmc(Span<byte> dst, int dstOff, ReadOnlySpan<byte> src, int srcOff,
                           int stride, int h, int ox, int oy,
                           int dxx, int dxy, int dyx, int dyy, int shift, int r,
                           int width, int height)
    {
        int s = 1 << shift;

        width--;
        height--;

        for (int y = 0; y < h; y++)
        {
            int vx = ox;
            int vy = oy;
            for (int x = 0; x < 8; x++)
            {
                int srcX = vx >> 16;
                int srcY = vy >> 16;
                int fracX = srcX & (s - 1);
                int fracY = srcY & (s - 1);

                srcX >>= shift;
                srcY >>= shift;

                int index;
                if ((uint)srcX < (uint)width)
                {
                    if ((uint)srcY < (uint)height)
                    {
                        index = srcX + srcY * stride;
                        dst[dstOff + y * stride + x] = (byte)(
                            ((src[srcOff + index] * (s - fracX)
                            + src[srcOff + index + 1] * fracX) * (s - fracY)
                           + (src[srcOff + index + stride] * (s - fracX)
                            + src[srcOff + index + stride + 1] * fracX) * fracY
                           + r) >> (shift * 2));
                    }
                    else
                    {
                        index = srcX + Math.Clamp(srcY, 0, height) * stride;
                        dst[dstOff + y * stride + x] = (byte)(
                            ((src[srcOff + index] * (s - fracX)
                            + src[srcOff + index + 1] * fracX) * s
                           + r) >> (shift * 2));
                    }
                }
                else
                {
                    if ((uint)srcY < (uint)height)
                    {
                        index = Math.Clamp(srcX, 0, width) + srcY * stride;
                        dst[dstOff + y * stride + x] = (byte)(
                            ((src[srcOff + index] * (s - fracY)
                            + src[srcOff + index + stride] * fracY) * s
                           + r) >> (shift * 2));
                    }
                    else
                    {
                        index = Math.Clamp(srcX, 0, width)
                              + Math.Clamp(srcY, 0, height) * stride;
                        dst[dstOff + y * stride + x] = src[srcOff + index];
                    }
                }

                vx += dxx;
                vy += dyx;
            }
            ox += dxy;
            oy += dyy;
        }
    }
}

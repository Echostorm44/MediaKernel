// Chroma motion compensation - a translation of ffmpeg-master/libavcodec/h264chroma_template.c
// at 8 bits.
//
// Named for H.264 because that is where FFmpeg puts it, but it is shared: MPEG-1/2 chroma motion
// compensation is the same bilinear interpolation on an eighth-pel grid, and mpegvideo_motion.c
// calls straight into this table. MediaKernel's H.264 decoder has its own copy of this
// arithmetic inlined inside ReconstructInterChromaMc, so there was nothing to reuse.
//
// Per the 2026-10-16 decision the H264ChromaContext function-pointer table is gone. FFmpeg's
// SET_CHROMA macro, which fills that table per bit depth, has no counterpart.

using System;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>Bilinear chroma motion compensation (<c>h264chroma_template.c</c>).</summary>
/// <remarks>
/// The weights are <c>(8-x)(8-y)</c>, <c>x(8-y)</c>, <c>(8-x)y</c> and <c>xy</c> over an
/// eighth-pel grid, so the four of them sum to 64 and the result is rounded with
/// <c>(v + 32) &gt;&gt; 6</c>. The two reduced paths are FFmpeg's and are worth keeping: when
/// only one of x and y is non-zero the interpolation collapses to two taps along one axis, and
/// when both are zero it is a plain copy.
/// </remarks>
public static class H264ChromaDsp
{
    /// <summary>Rounds a weighted sum of four eighth-pel taps, as <c>op_put</c>.</summary>
    private static byte Put(int v) => (byte)((v + 32) >> 6);

    /// <summary>Averages into the destination, as <c>op_avg</c>.</summary>
    private static byte Avg(byte dst, int v) => (byte)((dst + ((v + 32) >> 6) + 1) >> 1);

    /// <summary>
    /// <c>put_h264_chroma_mc8_8_c</c> and friends, for a block <paramref name="width"/> wide.
    /// </summary>
    /// <param name="average">
    /// False for <c>put</c>, true for <c>avg</c>. One method rather than two because the only
    /// difference is the final write, and the weights above it are identical.
    /// </param>
    private static void ChromaMc(
        Span<byte> dst, int dstOffset,
        ReadOnlySpan<byte> src, int srcOffset,
        int stride, int h, int x, int y, int width, bool average)
    {
        int a = (8 - x) * (8 - y);
        int b = x * (8 - y);
        int c = (8 - x) * y;
        int d = x * y;

        if (d != 0)
        {
            for (int i = 0; i < h; i++)
            {
                for (int j = 0; j < width; j++)
                {
                    int v = a * src[srcOffset + j]
                          + b * src[srcOffset + j + 1]
                          + c * src[srcOffset + stride + j]
                          + d * src[srcOffset + stride + j + 1];
                    dst[dstOffset + j] = average ? Avg(dst[dstOffset + j], v) : Put(v);
                }
                dstOffset += stride;
                srcOffset += stride;
            }
        }
        else if (b + c != 0)
        {
            // Only one axis is fractional, so two taps along it.
            int e = b + c;
            int step = c != 0 ? stride : 1;

            for (int i = 0; i < h; i++)
            {
                for (int j = 0; j < width; j++)
                {
                    int v = a * src[srcOffset + j] + e * src[srcOffset + step + j];
                    dst[dstOffset + j] = average ? Avg(dst[dstOffset + j], v) : Put(v);
                }
                dstOffset += stride;
                srcOffset += stride;
            }
        }
        else
        {
            // Both are integral: a is 64 and this is a copy, through the same rounding.
            for (int i = 0; i < h; i++)
            {
                for (int j = 0; j < width; j++)
                {
                    int v = a * src[srcOffset + j];
                    dst[dstOffset + j] = average ? Avg(dst[dstOffset + j], v) : Put(v);
                }
                dstOffset += stride;
                srcOffset += stride;
            }
        }
    }

    /// <summary><c>put_h264_chroma_mc8_8_c</c>.</summary>
    public static void PutChromaMc8(Span<byte> dst, int dstOffset, ReadOnlySpan<byte> src, int srcOffset,
                                    int stride, int h, int x, int y)
        => ChromaMc(dst, dstOffset, src, srcOffset, stride, h, x, y, 8, average: false);

    /// <summary><c>avg_h264_chroma_mc8_8_c</c>.</summary>
    public static void AvgChromaMc8(Span<byte> dst, int dstOffset, ReadOnlySpan<byte> src, int srcOffset,
                                    int stride, int h, int x, int y)
        => ChromaMc(dst, dstOffset, src, srcOffset, stride, h, x, y, 8, average: true);

    /// <summary><c>put_h264_chroma_mc4_8_c</c>.</summary>
    public static void PutChromaMc4(Span<byte> dst, int dstOffset, ReadOnlySpan<byte> src, int srcOffset,
                                    int stride, int h, int x, int y)
        => ChromaMc(dst, dstOffset, src, srcOffset, stride, h, x, y, 4, average: false);

    /// <summary><c>avg_h264_chroma_mc4_8_c</c>.</summary>
    public static void AvgChromaMc4(Span<byte> dst, int dstOffset, ReadOnlySpan<byte> src, int srcOffset,
                                    int stride, int h, int x, int y)
        => ChromaMc(dst, dstOffset, src, srcOffset, stride, h, x, y, 4, average: true);

    /// <summary><c>put_h264_chroma_mc2_8_c</c>.</summary>
    public static void PutChromaMc2(Span<byte> dst, int dstOffset, ReadOnlySpan<byte> src, int srcOffset,
                                    int stride, int h, int x, int y)
        => ChromaMc(dst, dstOffset, src, srcOffset, stride, h, x, y, 2, average: false);

    /// <summary><c>avg_h264_chroma_mc2_8_c</c>.</summary>
    public static void AvgChromaMc2(Span<byte> dst, int dstOffset, ReadOnlySpan<byte> src, int srcOffset,
                                    int stride, int h, int x, int y)
        => ChromaMc(dst, dstOffset, src, srcOffset, stride, h, x, y, 2, average: true);
}

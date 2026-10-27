// VC-1's pixel kernels - a translation of ffmpeg-master/libavcodec/vc1dsp.c.
//
// The C installs these in a VC1DSPContext of function pointers. This port calls them as static
// methods, the decision MpegEncContext records for its seven DSP tables and VC1Context for its
// two; ff_vc1dsp_init is therefore a divergence rather than a method.
//
// FFmpeg's pointer parameters are a span plus an offset here, because every one of these walks
// backwards from where it is handed - the overlap filters read two rows above, the loop filter
// four, the interpolation one - and a span that started at the pointer would have nothing
// there.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vc1Video;

/// <summary>
/// FFmpeg's <c>vc1dsp.c</c>: VC-1's overlap transform, in-loop deblocking filter, the four
/// inverse transforms and the luma and chroma interpolation.
/// </summary>
public static class Vc1Dsp
{
    /// <summary><c>av_clip_uint8</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClipUint8(int a) => (byte)(a < 0 ? 0 : a > 255 ? 255 : a);

    // --- the overlap transform, 8.6.2 --------------------------------------------------------

    /// <summary><c>vc1_v_overlap_c</c> - the overlap transform across a horizontal edge.</summary>
    public static void Vc1VOverlap(Span<byte> src, int off, int stride)
    {
        int rnd = 1;
        for (int i = 0; i < 8; i++)
        {
            int a = src[off - 2 * stride];
            int b = src[off - stride];
            int c = src[off];
            int d = src[off + stride];
            int d1 = (a - d + 3 + rnd) >> 3;
            int d2 = (a - d + b - c + 4 - rnd) >> 3;

            src[off - 2 * stride] = (byte)(a - d1);
            src[off - stride] = ClipUint8(b - d2);
            src[off] = ClipUint8(c + d2);
            src[off + stride] = (byte)(d + d1);
            off++;
            rnd = rnd != 0 ? 0 : 1;
        }
    }

    /// <summary><c>vc1_h_overlap_c</c> - the overlap transform across a vertical edge.</summary>
    public static void Vc1HOverlap(Span<byte> src, int off, int stride)
    {
        int rnd = 1;
        for (int i = 0; i < 8; i++)
        {
            int a = src[off - 2];
            int b = src[off - 1];
            int c = src[off];
            int d = src[off + 1];
            int d1 = (a - d + 3 + rnd) >> 3;
            int d2 = (a - d + b - c + 4 - rnd) >> 3;

            src[off - 2] = (byte)(a - d1);
            src[off - 1] = ClipUint8(b - d2);
            src[off] = ClipUint8(c + d2);
            src[off + 1] = (byte)(d + d1);
            off += stride;
            rnd = rnd != 0 ? 0 : 1;
        }
    }

    /// <summary>
    /// <c>vc1_v_s_overlap_c</c> - the overlap transform across a horizontal edge, on the
    /// coefficients of two vertically adjacent blocks rather than on pixels.
    /// </summary>
    public static void Vc1VSOverlap(Span<short> top, int topOff, Span<short> bottom, int botOff)
    {
        int rnd1 = 4, rnd2 = 3;
        for (int i = 0; i < 8; i++)
        {
            int a = top[topOff + 48];
            int b = top[topOff + 56];
            int c = bottom[botOff + 0];
            int d = bottom[botOff + 8];
            int d1 = a - d;
            int d2 = a - d + b - c;

            top[topOff + 48] = (short)(((a * 8) - d1 + rnd1) >> 3);
            top[topOff + 56] = (short)(((b * 8) - d2 + rnd2) >> 3);
            bottom[botOff + 0] = (short)(((c * 8) + d2 + rnd1) >> 3);
            bottom[botOff + 8] = (short)(((d * 8) + d1 + rnd2) >> 3);

            botOff++;
            topOff++;
            rnd2 = 7 - rnd2;
            rnd1 = 7 - rnd1;
        }
    }

    /// <summary>
    /// <c>vc1_h_s_overlap_c</c> - the overlap transform across a vertical edge, on
    /// coefficients. <paramref name="flags"/> bit 1 picks the starting rounder and bit 0 says
    /// whether the two rounders alternate down the edge.
    /// </summary>
    public static void Vc1HSOverlap(
        Span<short> left, int leftOff, Span<short> right, int rightOff,
        int leftStride, int rightStride, int flags)
    {
        int rnd1 = (flags & 2) != 0 ? 3 : 4;
        int rnd2 = 7 - rnd1;
        for (int i = 0; i < 8; i++)
        {
            int a = left[leftOff + 6];
            int b = left[leftOff + 7];
            int c = right[rightOff + 0];
            int d = right[rightOff + 1];
            int d1 = a - d;
            int d2 = a - d + b - c;

            left[leftOff + 6] = (short)(((a * 8) - d1 + rnd1) >> 3);
            left[leftOff + 7] = (short)(((b * 8) - d2 + rnd2) >> 3);
            right[rightOff + 0] = (short)(((c * 8) + d2 + rnd1) >> 3);
            right[rightOff + 1] = (short)(((d * 8) + d1 + rnd2) >> 3);

            rightOff += rightStride;
            leftOff += leftStride;
            if ((flags & 1) != 0)
            {
                rnd2 = 7 - rnd2;
                rnd1 = 7 - rnd1;
            }
        }
    }

    // --- the in-loop deblocking filter, 8.6 --------------------------------------------------

    /// <summary>
    /// <c>vc1_filter_line</c> - the deblocking filter for one line.
    /// </summary>
    /// <returns>
    /// Whether the other three pairs of this group of four should be filtered.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Vc1FilterLine(Span<byte> src, int off, int stride, int pq)
    {
        int a0 = (2 * (src[off - 2 * stride] - src[off + 1 * stride]) -
                  5 * (src[off - 1 * stride] - src[off + 0 * stride]) + 4) >> 3;
        int a0Sign = a0 >> 31;          // Store sign.

        a0 = (a0 ^ a0Sign) - a0Sign;    // a0 = FFABS(a0);
        if (a0 < pq)
        {
            int a1 = Math.Abs((2 * (src[off - 4 * stride] - src[off - 1 * stride]) -
                               5 * (src[off - 3 * stride] - src[off - 2 * stride]) + 4) >> 3);
            int a2 = Math.Abs((2 * (src[off + 0 * stride] - src[off + 3 * stride]) -
                               5 * (src[off + 1 * stride] - src[off + 2 * stride]) + 4) >> 3);
            if (a1 < a0 || a2 < a0)
            {
                int clip = src[off - 1 * stride] - src[off + 0 * stride];
                int clipSign = clip >> 31;

                clip = ((clip ^ clipSign) - clipSign) >> 1;
                if (clip != 0)
                {
                    int a3 = Math.Min(a1, a2);
                    int d = 5 * (a3 - a0);
                    int dSign = d >> 31;

                    d = ((d ^ dSign) - dSign) >> 3;
                    dSign ^= a0Sign;

                    if ((dSign ^ clipSign) != 0)
                    {
                        d = 0;
                    }
                    else
                    {
                        d = Math.Min(d, clip);
                        d = (d ^ dSign) - dSign;  // Restore sign.
                        src[off - 1 * stride] = ClipUint8(src[off - 1 * stride] - d);
                        src[off + 0 * stride] = ClipUint8(src[off + 0 * stride] + d);
                    }
                    return 1;
                }
            }
        }
        return 0;
    }

    /// <summary>
    /// <c>vc1_loop_filter</c> - the deblocking filter over an edge of <paramref name="len"/>
    /// pixels, in groups of four. The third line of each group decides for the other three.
    /// </summary>
    private static void Vc1LoopFilter(
        Span<byte> src, int off, int step, int stride, int len, int pq)
    {
        for (int i = 0; i < len; i += 4)
        {
            int filt3 = Vc1FilterLine(src, off + 2 * step, stride, pq);
            if (filt3 != 0)
            {
                Vc1FilterLine(src, off + 0 * step, stride, pq);
                Vc1FilterLine(src, off + 1 * step, stride, pq);
                Vc1FilterLine(src, off + 3 * step, stride, pq);
            }
            off += step * 4;
        }
    }

    /// <summary><c>vc1_v_loop_filter4_c</c>.</summary>
    public static void Vc1VLoopFilter4(Span<byte> src, int off, int stride, int pq) =>
        Vc1LoopFilter(src, off, 1, stride, 4, pq);

    /// <summary><c>vc1_h_loop_filter4_c</c>.</summary>
    public static void Vc1HLoopFilter4(Span<byte> src, int off, int stride, int pq) =>
        Vc1LoopFilter(src, off, stride, 1, 4, pq);

    /// <summary><c>vc1_v_loop_filter8_c</c>.</summary>
    public static void Vc1VLoopFilter8(Span<byte> src, int off, int stride, int pq) =>
        Vc1LoopFilter(src, off, 1, stride, 8, pq);

    /// <summary><c>vc1_h_loop_filter8_c</c>.</summary>
    public static void Vc1HLoopFilter8(Span<byte> src, int off, int stride, int pq) =>
        Vc1LoopFilter(src, off, stride, 1, 8, pq);

    /// <summary><c>vc1_v_loop_filter16_c</c>.</summary>
    public static void Vc1VLoopFilter16(Span<byte> src, int off, int stride, int pq) =>
        Vc1LoopFilter(src, off, 1, stride, 16, pq);

    /// <summary><c>vc1_h_loop_filter16_c</c>.</summary>
    public static void Vc1HLoopFilter16(Span<byte> src, int off, int stride, int pq) =>
        Vc1LoopFilter(src, off, stride, 1, 16, pq);

    // --- the four inverse transforms, 8.7 ----------------------------------------------------

    /// <summary><c>vc1_inv_trans_8x8_dc_c</c> - an 8x8 block with only a DC coefficient.</summary>
    public static void Vc1InvTrans8x8Dc(Span<byte> dest, int off, int stride, Span<short> block)
    {
        int dc = block[0];

        dc = (3 * dc + 1) >> 1;
        dc = (3 * dc + 16) >> 5;

        for (int i = 0; i < 8; i++)
        {
            for (int x = 0; x < 8; x++)
                dest[off + x] = ClipUint8(dest[off + x] + dc);
            off += stride;
        }
    }

    /// <summary>
    /// <c>vc1_inv_trans_8x8_c</c> - the 8x8 inverse transform, in place on the coefficients.
    /// </summary>
    public static void Vc1InvTrans8x8(Span<short> block)
    {
        Span<short> temp = stackalloc short[64];

        for (int i = 0; i < 8; i++)
        {
            int s = i, d = i * 8;
            int t1 = 12 * (block[s + 0] + block[s + 32]) + 4;
            int t2 = 12 * (block[s + 0] - block[s + 32]) + 4;
            int t3 = 16 * block[s + 16] + 6 * block[s + 48];
            int t4 = 6 * block[s + 16] - 16 * block[s + 48];

            int t5 = t1 + t3;
            int t6 = t2 + t4;
            int t7 = t2 - t4;
            int t8 = t1 - t3;

            t1 = 16 * block[s + 8] + 15 * block[s + 24] + 9 * block[s + 40] + 4 * block[s + 56];
            t2 = 15 * block[s + 8] - 4 * block[s + 24] - 16 * block[s + 40] - 9 * block[s + 56];
            t3 = 9 * block[s + 8] - 16 * block[s + 24] + 4 * block[s + 40] + 15 * block[s + 56];
            t4 = 4 * block[s + 8] - 9 * block[s + 24] + 15 * block[s + 40] - 16 * block[s + 56];

            temp[d + 0] = (short)((t5 + t1) >> 3);
            temp[d + 1] = (short)((t6 + t2) >> 3);
            temp[d + 2] = (short)((t7 + t3) >> 3);
            temp[d + 3] = (short)((t8 + t4) >> 3);
            temp[d + 4] = (short)((t8 - t4) >> 3);
            temp[d + 5] = (short)((t7 - t3) >> 3);
            temp[d + 6] = (short)((t6 - t2) >> 3);
            temp[d + 7] = (short)((t5 - t1) >> 3);
        }

        for (int i = 0; i < 8; i++)
        {
            int s = i, d = i;
            int t1 = 12 * (temp[s + 0] + temp[s + 32]) + 64;
            int t2 = 12 * (temp[s + 0] - temp[s + 32]) + 64;
            int t3 = 16 * temp[s + 16] + 6 * temp[s + 48];
            int t4 = 6 * temp[s + 16] - 16 * temp[s + 48];

            int t5 = t1 + t3;
            int t6 = t2 + t4;
            int t7 = t2 - t4;
            int t8 = t1 - t3;

            t1 = 16 * temp[s + 8] + 15 * temp[s + 24] + 9 * temp[s + 40] + 4 * temp[s + 56];
            t2 = 15 * temp[s + 8] - 4 * temp[s + 24] - 16 * temp[s + 40] - 9 * temp[s + 56];
            t3 = 9 * temp[s + 8] - 16 * temp[s + 24] + 4 * temp[s + 40] + 15 * temp[s + 56];
            t4 = 4 * temp[s + 8] - 9 * temp[s + 24] + 15 * temp[s + 40] - 16 * temp[s + 56];

            block[d + 0] = (short)((t5 + t1) >> 7);
            block[d + 8] = (short)((t6 + t2) >> 7);
            block[d + 16] = (short)((t7 + t3) >> 7);
            block[d + 24] = (short)((t8 + t4) >> 7);
            block[d + 32] = (short)((t8 - t4 + 1) >> 7);
            block[d + 40] = (short)((t7 - t3 + 1) >> 7);
            block[d + 48] = (short)((t6 - t2 + 1) >> 7);
            block[d + 56] = (short)((t5 - t1 + 1) >> 7);
        }
    }

    /// <summary><c>vc1_inv_trans_8x4_dc_c</c>.</summary>
    public static void Vc1InvTrans8x4Dc(Span<byte> dest, int off, int stride, Span<short> block)
    {
        int dc = block[0];

        dc = (3 * dc + 1) >> 1;
        dc = (17 * dc + 64) >> 7;

        for (int i = 0; i < 4; i++)
        {
            for (int x = 0; x < 8; x++)
                dest[off + x] = ClipUint8(dest[off + x] + dc);
            off += stride;
        }
    }

    /// <summary>
    /// <c>vc1_inv_trans_8x4_c</c>. The horizontal pass is in place on the coefficients; the
    /// vertical pass adds straight into the destination.
    /// </summary>
    public static void Vc1InvTrans8x4(Span<byte> dest, int off, int stride, Span<short> block)
    {
        for (int i = 0; i < 4; i++)
        {
            int s = i * 8;
            int t1 = 12 * (block[s + 0] + block[s + 4]) + 4;
            int t2 = 12 * (block[s + 0] - block[s + 4]) + 4;
            int t3 = 16 * block[s + 2] + 6 * block[s + 6];
            int t4 = 6 * block[s + 2] - 16 * block[s + 6];

            int t5 = t1 + t3;
            int t6 = t2 + t4;
            int t7 = t2 - t4;
            int t8 = t1 - t3;

            t1 = 16 * block[s + 1] + 15 * block[s + 3] + 9 * block[s + 5] + 4 * block[s + 7];
            t2 = 15 * block[s + 1] - 4 * block[s + 3] - 16 * block[s + 5] - 9 * block[s + 7];
            t3 = 9 * block[s + 1] - 16 * block[s + 3] + 4 * block[s + 5] + 15 * block[s + 7];
            t4 = 4 * block[s + 1] - 9 * block[s + 3] + 15 * block[s + 5] - 16 * block[s + 7];

            block[s + 0] = (short)((t5 + t1) >> 3);
            block[s + 1] = (short)((t6 + t2) >> 3);
            block[s + 2] = (short)((t7 + t3) >> 3);
            block[s + 3] = (short)((t8 + t4) >> 3);
            block[s + 4] = (short)((t8 - t4) >> 3);
            block[s + 5] = (short)((t7 - t3) >> 3);
            block[s + 6] = (short)((t6 - t2) >> 3);
            block[s + 7] = (short)((t5 - t1) >> 3);
        }

        for (int i = 0; i < 8; i++)
        {
            int s = i;
            int t1 = 17 * (block[s + 0] + block[s + 16]) + 64;
            int t2 = 17 * (block[s + 0] - block[s + 16]) + 64;
            int t3 = 22 * block[s + 8] + 10 * block[s + 24];
            int t4 = 22 * block[s + 24] - 10 * block[s + 8];

            int p = off + i;
            dest[p + 0 * stride] = ClipUint8(dest[p + 0 * stride] + ((t1 + t3) >> 7));
            dest[p + 1 * stride] = ClipUint8(dest[p + 1 * stride] + ((t2 - t4) >> 7));
            dest[p + 2 * stride] = ClipUint8(dest[p + 2 * stride] + ((t2 + t4) >> 7));
            dest[p + 3 * stride] = ClipUint8(dest[p + 3 * stride] + ((t1 - t3) >> 7));
        }
    }

    /// <summary><c>vc1_inv_trans_4x8_dc_c</c>.</summary>
    public static void Vc1InvTrans4x8Dc(Span<byte> dest, int off, int stride, Span<short> block)
    {
        int dc = block[0];

        dc = (17 * dc + 4) >> 3;
        dc = (12 * dc + 64) >> 7;

        for (int i = 0; i < 8; i++)
        {
            for (int x = 0; x < 4; x++)
                dest[off + x] = ClipUint8(dest[off + x] + dc);
            off += stride;
        }
    }

    /// <summary><c>vc1_inv_trans_4x8_c</c>.</summary>
    public static void Vc1InvTrans4x8(Span<byte> dest, int off, int stride, Span<short> block)
    {
        for (int i = 0; i < 8; i++)
        {
            int s = i * 8;
            int t1 = 17 * (block[s + 0] + block[s + 2]) + 4;
            int t2 = 17 * (block[s + 0] - block[s + 2]) + 4;
            int t3 = 22 * block[s + 1] + 10 * block[s + 3];
            int t4 = 22 * block[s + 3] - 10 * block[s + 1];

            block[s + 0] = (short)((t1 + t3) >> 3);
            block[s + 1] = (short)((t2 - t4) >> 3);
            block[s + 2] = (short)((t2 + t4) >> 3);
            block[s + 3] = (short)((t1 - t3) >> 3);
        }

        for (int i = 0; i < 4; i++)
        {
            int s = i;
            int t1 = 12 * (block[s + 0] + block[s + 32]) + 64;
            int t2 = 12 * (block[s + 0] - block[s + 32]) + 64;
            int t3 = 16 * block[s + 16] + 6 * block[s + 48];
            int t4 = 6 * block[s + 16] - 16 * block[s + 48];

            int t5 = t1 + t3;
            int t6 = t2 + t4;
            int t7 = t2 - t4;
            int t8 = t1 - t3;

            t1 = 16 * block[s + 8] + 15 * block[s + 24] + 9 * block[s + 40] + 4 * block[s + 56];
            t2 = 15 * block[s + 8] - 4 * block[s + 24] - 16 * block[s + 40] - 9 * block[s + 56];
            t3 = 9 * block[s + 8] - 16 * block[s + 24] + 4 * block[s + 40] + 15 * block[s + 56];
            t4 = 4 * block[s + 8] - 9 * block[s + 24] + 15 * block[s + 40] - 16 * block[s + 56];

            int p = off + i;
            dest[p + 0 * stride] = ClipUint8(dest[p + 0 * stride] + ((t5 + t1) >> 7));
            dest[p + 1 * stride] = ClipUint8(dest[p + 1 * stride] + ((t6 + t2) >> 7));
            dest[p + 2 * stride] = ClipUint8(dest[p + 2 * stride] + ((t7 + t3) >> 7));
            dest[p + 3 * stride] = ClipUint8(dest[p + 3 * stride] + ((t8 + t4) >> 7));
            dest[p + 4 * stride] = ClipUint8(dest[p + 4 * stride] + ((t8 - t4 + 1) >> 7));
            dest[p + 5 * stride] = ClipUint8(dest[p + 5 * stride] + ((t7 - t3 + 1) >> 7));
            dest[p + 6 * stride] = ClipUint8(dest[p + 6 * stride] + ((t6 - t2 + 1) >> 7));
            dest[p + 7 * stride] = ClipUint8(dest[p + 7 * stride] + ((t5 - t1 + 1) >> 7));
        }
    }

    /// <summary><c>vc1_inv_trans_4x4_dc_c</c>.</summary>
    public static void Vc1InvTrans4x4Dc(Span<byte> dest, int off, int stride, Span<short> block)
    {
        int dc = block[0];

        dc = (17 * dc + 4) >> 3;
        dc = (17 * dc + 64) >> 7;

        for (int i = 0; i < 4; i++)
        {
            for (int x = 0; x < 4; x++)
                dest[off + x] = ClipUint8(dest[off + x] + dc);
            off += stride;
        }
    }

    /// <summary><c>vc1_inv_trans_4x4_c</c>.</summary>
    public static void Vc1InvTrans4x4(Span<byte> dest, int off, int stride, Span<short> block)
    {
        for (int i = 0; i < 4; i++)
        {
            int s = i * 8;
            int t1 = 17 * (block[s + 0] + block[s + 2]) + 4;
            int t2 = 17 * (block[s + 0] - block[s + 2]) + 4;
            int t3 = 22 * block[s + 1] + 10 * block[s + 3];
            int t4 = 22 * block[s + 3] - 10 * block[s + 1];

            block[s + 0] = (short)((t1 + t3) >> 3);
            block[s + 1] = (short)((t2 - t4) >> 3);
            block[s + 2] = (short)((t2 + t4) >> 3);
            block[s + 3] = (short)((t1 - t3) >> 3);
        }

        for (int i = 0; i < 4; i++)
        {
            int s = i;
            int t1 = 17 * (block[s + 0] + block[s + 16]) + 64;
            int t2 = 17 * (block[s + 0] - block[s + 16]) + 64;
            int t3 = 22 * block[s + 8] + 10 * block[s + 24];
            int t4 = 22 * block[s + 24] - 10 * block[s + 8];

            int p = off + i;
            dest[p + 0 * stride] = ClipUint8(dest[p + 0 * stride] + ((t1 + t3) >> 7));
            dest[p + 1 * stride] = ClipUint8(dest[p + 1 * stride] + ((t2 - t4) >> 7));
            dest[p + 2 * stride] = ClipUint8(dest[p + 2 * stride] + ((t2 + t4) >> 7));
            dest[p + 3 * stride] = ClipUint8(dest[p + 3 * stride] + ((t1 - t3) >> 7));
        }
    }

    // --- luma interpolation ------------------------------------------------------------------

    /// <summary>
    /// The <c>OP</c>/<c>OP4</c> pair of <c>VC1_MSPEL_MC</c>, which the C instantiates as
    /// <c>put_</c> and <c>avg_</c>.
    /// </summary>
    /// <remarks>
    /// translation rule 5: a C# generic over <c>static abstract</c> members in place of the macro,
    /// as <see cref="Mpeg4Video.QpelDsp.IQpelOp"/> already does for MPEG-4's 108 functions.
    /// Writing the expansion out would be a transcription of the preprocessor's output rather
    /// than of the source.
    /// </remarks>
    public interface IVc1MspelOp
    {
        /// <summary><c>op_put</c> or <c>op_avg</c>, over one pixel.</summary>
        static abstract byte Op(byte dst, int value);

        /// <summary><c>op4_put</c> or <c>op4_avg</c>, over four packed pixels.</summary>
        static abstract uint Op4(uint dst, uint src);
    }

    /// <summary><c>VC1_MSPEL_MC(op_put, op4_put, put_)</c>.</summary>
    public struct Put : IVc1MspelOp
    {
        /// <inheritdoc/>
        public static byte Op(byte dst, int value) => ClipUint8(value);

        /// <inheritdoc/>
        public static uint Op4(uint dst, uint src) => src;
    }

    /// <summary><c>VC1_MSPEL_MC(op_avg, op4_avg, avg_)</c>.</summary>
    public struct Avg : IVc1MspelOp
    {
        /// <inheritdoc/>
        public static byte Op(byte dst, int value) => (byte)((dst + ClipUint8(value) + 1) >> 1);

        /// <inheritdoc/>
        // rnd_avg32: a rounded average of four packed bytes without carrying between them.
        public static uint Op4(uint dst, uint src) =>
            (dst | src) - (((dst ^ src) & 0xFEFEFEFEu) >> 1);
    }

    /// <summary>
    /// <c>vc1_mspel_ver_filter_16bits</c> - the vertical half of the bicubic filter, over
    /// bytes, without the rounding and shift.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Vc1MspelVerFilter16Bits(ReadOnlySpan<byte> src, int off, int stride,
                                               int mode) => mode switch
    {
        0 => 0,  // no shift - should not occur
        1 => -4 * src[off - stride] + 53 * src[off] +
             18 * src[off + stride] - 3 * src[off + stride * 2],
        2 => -1 * src[off - stride] + 9 * src[off] +
             9 * src[off + stride] - 1 * src[off + stride * 2],
        3 => -3 * src[off - stride] + 18 * src[off] +
             53 * src[off + stride] - 4 * src[off + stride * 2],
        _ => 0,  // should not occur
    };

    /// <summary>
    /// <c>vc1_mspel_hor_filter_16bits</c> - the same filter over the 16-bit intermediate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Vc1MspelHorFilter16Bits(ReadOnlySpan<short> src, int off, int stride,
                                               int mode) => mode switch
    {
        0 => 0,
        1 => -4 * src[off - stride] + 53 * src[off] +
             18 * src[off + stride] - 3 * src[off + stride * 2],
        2 => -1 * src[off - stride] + 9 * src[off] +
             9 * src[off + stride] - 1 * src[off + stride * 2],
        3 => -3 * src[off - stride] + 18 * src[off] +
             53 * src[off + stride] - 4 * src[off + stride * 2],
        _ => 0,
    };

    /// <summary>
    /// <c>vc1_mspel_filter</c> - one pass of the bicubic filter, rounded and shifted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Vc1MspelFilter(ReadOnlySpan<byte> src, int off, int stride, int mode,
                                      int r) => mode switch
    {
        0 => src[off],  // no shift
        1 => (-4 * src[off - stride] + 53 * src[off] +
              18 * src[off + stride] - 3 * src[off + stride * 2] + 32 - r) >> 6,
        2 => (-1 * src[off - stride] + 9 * src[off] +
              9 * src[off + stride] - 1 * src[off + stride * 2] + 8 - r) >> 4,
        3 => (-3 * src[off - stride] + 18 * src[off] +
              53 * src[off + stride] - 4 * src[off + stride * 2] + 32 - r) >> 6,
        _ => 0,  // should not occur
    };

    /// <summary>The <c>shift_value</c> table of <c>VC1_MSPEL_MC</c>.</summary>
    private static ReadOnlySpan<int> ShiftValue => [0, 5, 1, 5];

    /// <summary>
    /// <c>vc1_mspel_mc</c> - bicubic motion compensation of an 8x8 block.
    /// </summary>
    public static void Vc1MspelMc<TOp>(Span<byte> dst, int dstOff, ReadOnlySpan<byte> src,
                                       int srcOff, int stride, int hmode, int vmode, int rnd)
        where TOp : IVc1MspelOp
        => MspelMc<TOp>(dst, dstOff, src, srcOff, stride, hmode, vmode, rnd, 8);

    /// <summary>
    /// <c>vc1_mspel_mc_16</c> - the same over a 16x16 block.
    /// </summary>
    public static void Vc1MspelMc16<TOp>(Span<byte> dst, int dstOff, ReadOnlySpan<byte> src,
                                         int srcOff, int stride, int hmode, int vmode, int rnd)
        where TOp : IVc1MspelOp
        => MspelMc<TOp>(dst, dstOff, src, srcOff, stride, hmode, vmode, rnd, 16);

    /// <summary>
    /// The body the C writes twice inside one macro, once for 8x8 and once for 16x16, with
    /// the intermediate's width three wider than the block.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE, recorded: <c>VC1_MSPEL_MC</c> emits <c>vc1_mspel_mc</c> and
    /// <c>vc1_mspel_mc_16</c> as two copies that differ only in 8 versus 16 and 11 versus 19.
    /// One method with <paramref name="size"/> is the same code; two copies would be a place
    /// for the 11 and the 19 to drift apart.
    /// </remarks>
    private static void MspelMc<TOp>(Span<byte> dst, int dstOff, ReadOnlySpan<byte> src,
                                     int srcOff, int stride, int hmode, int vmode, int rnd,
                                     int size)
        where TOp : IVc1MspelOp
    {
        int tmpWidth = size + 3;

        if (vmode != 0)  // Horizontal filter to apply.
        {
            int r;

            if (hmode != 0)  // Vertical filter to apply, output to tmp.
            {
                int shift = (ShiftValue[hmode] + ShiftValue[vmode]) >> 1;
                Span<short> tmp = stackalloc short[19 * 16];
                int tptr = 0;

                r = (1 << (shift - 1)) + rnd - 1;

                srcOff -= 1;
                for (int j = 0; j < size; j++)
                {
                    for (int i = 0; i < tmpWidth; i++)
                        tmp[tptr + i] = (short)
                            ((Vc1MspelVerFilter16Bits(src, srcOff + i, stride, vmode) + r)
                             >> shift);
                    srcOff += stride;
                    tptr += tmpWidth;
                }

                r = 64 - rnd;
                tptr = 1;
                for (int j = 0; j < size; j++)
                {
                    for (int i = 0; i < size; i++)
                        dst[dstOff + i] = TOp.Op(
                            dst[dstOff + i],
                            (Vc1MspelHorFilter16Bits(tmp, tptr + i, 1, hmode) + r) >> 7);
                    dstOff += stride;
                    tptr += tmpWidth;
                }

                return;
            }
            else  // No horizontal filter, output `size` lines to dst.
            {
                r = 1 - rnd;

                for (int j = 0; j < size; j++)
                {
                    for (int i = 0; i < size; i++)
                        dst[dstOff + i] = TOp.Op(
                            dst[dstOff + i], Vc1MspelFilter(src, srcOff + i, stride, vmode, r));
                    srcOff += stride;
                    dstOff += stride;
                }
                return;
            }
        }

        // Horizontal mode with no vertical mode.
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
                dst[dstOff + i] = TOp.Op(
                    dst[dstOff + i], Vc1MspelFilter(src, srcOff + i, 1, hmode, rnd));
            dstOff += stride;
            srcOff += stride;
        }
    }

    /// <summary><c>put_pixels8x8_c</c> / <c>avg_pixels8x8_c</c>.</summary>
    public static void Pixels8x8<TOp>(Span<byte> block, int blockOff, ReadOnlySpan<byte> pixels,
                                      int pixelsOff, int lineSize, int rnd)
        where TOp : IVc1MspelOp
        => Pixels<TOp>(block, blockOff, pixels, pixelsOff, lineSize, 8);

    /// <summary><c>put_pixels16x16_c</c> / <c>avg_pixels16x16_c</c>.</summary>
    public static void Pixels16x16<TOp>(Span<byte> block, int blockOff,
                                        ReadOnlySpan<byte> pixels, int pixelsOff, int lineSize,
                                        int rnd)
        where TOp : IVc1MspelOp
        => Pixels<TOp>(block, blockOff, pixels, pixelsOff, lineSize, 16);

    /// <summary>
    /// The body of <c>pixels8x8_c</c> and <c>pixels16x16_c</c>, which copy or average whole
    /// rows four bytes at a time. <c>rnd</c> is a parameter of both and is read by neither.
    /// </summary>
    private static void Pixels<TOp>(Span<byte> block, int blockOff, ReadOnlySpan<byte> pixels,
                                    int pixelsOff, int lineSize, int size)
        where TOp : IVc1MspelOp
    {
        for (int i = 0; i < size; i++)
        {
            for (int x = 0; x < size; x += 4)
            {
                uint dst = (uint)(block[blockOff + x] | (block[blockOff + x + 1] << 8) |
                                  (block[blockOff + x + 2] << 16) |
                                  (block[blockOff + x + 3] << 24));
                uint s = (uint)(pixels[pixelsOff + x] | (pixels[pixelsOff + x + 1] << 8) |
                                (pixels[pixelsOff + x + 2] << 16) |
                                (pixels[pixelsOff + x + 3] << 24));
                uint v = TOp.Op4(dst, s);
                block[blockOff + x] = (byte)v;
                block[blockOff + x + 1] = (byte)(v >> 8);
                block[blockOff + x + 2] = (byte)(v >> 16);
                block[blockOff + x + 3] = (byte)(v >> 24);
            }
            pixelsOff += lineSize;
            blockOff += lineSize;
        }
    }

    // --- chroma interpolation ----------------------------------------------------------------

    /// <summary>
    /// The bilinear chroma filter the four <c>no_rnd_vc1_chroma_mc</c> functions share, as
    /// <c>chroma_mc(a)</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ChromaMc(ReadOnlySpan<byte> src, int off, int stride,
                                int a, int b, int c, int d) =>
        (a * src[off] + b * src[off + 1] +
         c * src[off + stride] + d * src[off + stride + 1] + 32 - 4) >> 6;

    /// <summary><c>put_no_rnd_vc1_chroma_mc8_c</c>.</summary>
    public static void PutNoRndVc1ChromaMc8(Span<byte> dst, int dstOff, ReadOnlySpan<byte> src,
                                            int srcOff, int stride, int h, int x, int y)
        => ChromaMcBody(dst, dstOff, src, srcOff, stride, h, x, y, 8, average: false);

    /// <summary><c>put_no_rnd_vc1_chroma_mc4_c</c>.</summary>
    public static void PutNoRndVc1ChromaMc4(Span<byte> dst, int dstOff, ReadOnlySpan<byte> src,
                                            int srcOff, int stride, int h, int x, int y)
        => ChromaMcBody(dst, dstOff, src, srcOff, stride, h, x, y, 4, average: false);

    /// <summary><c>avg_no_rnd_vc1_chroma_mc8_c</c>.</summary>
    public static void AvgNoRndVc1ChromaMc8(Span<byte> dst, int dstOff, ReadOnlySpan<byte> src,
                                            int srcOff, int stride, int h, int x, int y)
        => ChromaMcBody(dst, dstOff, src, srcOff, stride, h, x, y, 8, average: true);

    /// <summary><c>avg_no_rnd_vc1_chroma_mc4_c</c>.</summary>
    public static void AvgNoRndVc1ChromaMc4(Span<byte> dst, int dstOff, ReadOnlySpan<byte> src,
                                            int srcOff, int stride, int h, int x, int y)
        => ChromaMcBody(dst, dstOff, src, srcOff, stride, h, x, y, 4, average: true);

    /// <summary>
    /// The body the four chroma functions share, which differ only in their width and in
    /// whether the result is averaged with the destination.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE, recorded: four copies in the C, written once here. They are not a macro
    /// there - they are four hand-written functions whose only differences are 8 versus 4 and
    /// the <c>avg2</c> around the store, which is exactly the kind of repetition a
    /// transcription loses a term in.
    /// </remarks>
    private static void ChromaMcBody(Span<byte> dst, int dstOff, ReadOnlySpan<byte> src,
                                     int srcOff, int stride, int h, int x, int y, int width,
                                     bool average)
    {
        int a = (8 - x) * (8 - y);
        int b = x * (8 - y);
        int c = (8 - x) * y;
        int d = x * y;

        if ((uint)x >= 8 || (uint)y >= 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x), $"chroma motion vector fraction ({x}, {y}) is outside 0..7 "
                         + "(av_assert2 in put_no_rnd_vc1_chroma_mc8_c)");
        }

        for (int i = 0; i < h; i++)
        {
            for (int j = 0; j < width; j++)
            {
                int value = ChromaMc(src, srcOff + j, stride, a, b, c, d);
                dst[dstOff + j] = average
                    ? (byte)((dst[dstOff + j] + value + 1) >> 1)  // avg2
                    : (byte)value;
            }
            dstOff += stride;
            srcOff += stride;
        }
    }
}

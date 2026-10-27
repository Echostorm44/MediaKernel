// VP9 intra prediction modes (8-bit)
// Ported from FFmpeg libavcodec/vp9dsp_template.c
// All prediction functions take: dst, stride, left[size] (bottom-to-top), top[size+1] (top[-1]=topLeft, top[0..size-1])
// NOTE: In VP9, 'left' is stored bottom-to-top: left[0]=bottom, left[size-1]=top
//       'top' array has top[-1]=topLeft, top[0..size-1]=above pixels

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vp9;

/// <summary>
/// VP9 intra prediction for 8-bit samples.
/// Provides all 13 prediction modes at 4×4, 8×8, 16×16, and 32×32 block sizes.
/// Port of FFmpeg's vp9dsp_template.c intra prediction functions.
/// </summary>
public static class Vp9IntraPrediction
{
    // VP9 intra mode indices (matching FFmpeg vp9.h IntraPredMode enum)
    public const int VertPred = 0;
    public const int HorPred = 1;
    public const int DcPred = 2;
    public const int DiagDownLeftPred = 3;
    public const int DiagDownRightPred = 4;
    public const int VertRightPred = 5;
    public const int HorDownPred = 6;
    public const int VertLeftPred = 7;
    public const int HorUpPred = 8;
    public const int TmVp8Pred = 9;
    public const int LeftDcPred = 10;
    public const int TopDcPred = 11;
    public const int Dc128Pred = 12;
    public const int Dc127Pred = 13;
    public const int Dc129Pred = 14;

    /// <summary>
    /// Delegate for intra prediction function.
    /// dst: destination block, stride: destination row stride,
    /// left: left column pixels (bottom-to-top), top: above pixels (top[-1]=topLeft at index 0, top[0..] at index 1..)
    /// </summary>
    public delegate void IntraPredFunc(Span<byte> dst, int stride,
        ReadOnlySpan<byte> left, ReadOnlySpan<byte> top);

    // ================================================================
    // Vertical (copy top row)
    // ================================================================

    public static void Vert(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        for (int y = 0; y < sz; y++)
        {
            top.Slice(1, sz).CopyTo(dst.Slice(y * stride, sz));
        }
    }

    // ================================================================
    // Horizontal (copy left column)
    // ================================================================

    public static void Hor(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        // left is stored bottom-to-top, so left[sz-1] is the topmost left pixel (row 0)
        for (int y = 0; y < sz; y++)
        {
            byte val = left[sz - 1 - y];
            dst.Slice(y * stride, sz).Fill(val);
        }
    }

    // ================================================================
    // DC prediction (average of top + left)
    // ================================================================

    public static void Dc(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        int sum = 0;
        for (int i = 0; i < sz; i++) sum += left[i];
        for (int i = 0; i < sz; i++) sum += top[1 + i];
        byte dc = (byte)((sum + sz) / (sz * 2));
        for (int y = 0; y < sz; y++)
            dst.Slice(y * stride, sz).Fill(dc);
    }

    public static void DcLeft(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        int sum = 0;
        for (int i = 0; i < sz; i++) sum += left[i];
        byte dc = (byte)((sum + (sz >> 1)) / sz);
        for (int y = 0; y < sz; y++)
            dst.Slice(y * stride, sz).Fill(dc);
    }

    public static void DcTop(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        int sum = 0;
        for (int i = 0; i < sz; i++) sum += top[1 + i];
        byte dc = (byte)((sum + (sz >> 1)) / sz);
        for (int y = 0; y < sz; y++)
            dst.Slice(y * stride, sz).Fill(dc);
    }

    public static void Dc128(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        for (int y = 0; y < sz; y++)
            dst.Slice(y * stride, sz).Fill(128);
    }

    public static void Dc127(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        for (int y = 0; y < sz; y++)
            dst.Slice(y * stride, sz).Fill(127);
    }

    public static void Dc129(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        for (int y = 0; y < sz; y++)
            dst.Slice(y * stride, sz).Fill(129);
    }

    // ================================================================
    // TM (True Motion): dst[x,y] = clip(top[x] + left[y] - topLeft)
    // ================================================================

    public static void Tm(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        int tl = top[0]; // top[-1] = topLeft
        for (int y = 0; y < sz; y++)
        {
            int l = left[sz - 1 - y]; // left is bottom-to-top
            for (int x = 0; x < sz; x++)
                dst[y * stride + x] = (byte)Math.Clamp(top[1 + x] + l - tl, 0, 255);
        }
    }

    // ================================================================
    // Diagonal Down-Left (D45)
    // ================================================================

    public static void DiagDownLeft(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        // top pixels: top[1+i] for i=0..2*sz-1
        if (sz == 4)
        {
            DiagDownLeft4x4(dst, stride, left, top);
            return;
        }

        Span<byte> v = stackalloc byte[sz - 1];
        for (int i = 0; i < sz - 2; i++)
            v[i] = (byte)((top[1 + i] + top[1 + i + 1] * 2 + top[1 + i + 2] + 2) >> 2);
        v[sz - 2] = (byte)((top[1 + sz - 2] + top[1 + sz - 1] * 3 + 2) >> 2);

        for (int j = 0; j < sz; j++)
        {
            int copyLen = sz - 1 - j;
            if (copyLen > 0)
                v.Slice(j, copyLen).CopyTo(dst.Slice(j * stride));
            dst.Slice(j * stride + Math.Max(0, copyLen), j + 1).Fill(top[1 + sz - 1]);
        }
    }

    private static void DiagDownLeft4x4(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top)
    {
        int a0 = top[1], a1 = top[2], a2 = top[3], a3 = top[4], a4 = top[5], a5 = top[6], a6 = top[7], a7 = top[8];

        dst[0 + 0 * stride] = (byte)((a0 + a1 * 2 + a2 + 2) >> 2);
        dst[1 + 0 * stride] = dst[0 + 1 * stride] = (byte)((a1 + a2 * 2 + a3 + 2) >> 2);
        dst[2 + 0 * stride] = dst[1 + 1 * stride] = dst[0 + 2 * stride] = (byte)((a2 + a3 * 2 + a4 + 2) >> 2);
        dst[3 + 0 * stride] = dst[2 + 1 * stride] = dst[1 + 2 * stride] = dst[0 + 3 * stride] = (byte)((a3 + a4 * 2 + a5 + 2) >> 2);
        dst[3 + 1 * stride] = dst[2 + 2 * stride] = dst[1 + 3 * stride] = (byte)((a4 + a5 * 2 + a6 + 2) >> 2);
        dst[3 + 2 * stride] = dst[2 + 3 * stride] = (byte)((a5 + a6 * 2 + a7 + 2) >> 2);
        dst[3 + 3 * stride] = (byte)a7;
    }

    // ================================================================
    // Diagonal Down-Right (D135)
    // ================================================================

    public static void DiagDownRight(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        if (sz == 4)
        {
            DiagDownRight4x4(dst, stride, left, top);
            return;
        }

        // left is bottom-to-top: left[0]=bottom, left[sz-1]=top adjacent
        // In FFmpeg: left[0]=top adjacent (row 0 left), left[sz-1]=bottom
        // But VP9 DSP convention: left is stored reversed. Need to reverse.
        Span<byte> v = stackalloc byte[sz + sz - 1];

        for (int i = 0; i < sz - 2; i++)
        {
            // left[i] in FFmpeg = left[sz-1-i] in our convention (flipped)
            // Actually FFmpeg stores left as: left[0] = row sz-1 (bottom), ... left[sz-1] = row 0 (top adjacent)
            // Wait — FFmpeg's left[0] is the top-adjacent left pixel (same row as dst row 0)
            // But the comment in vp9dsp_template.c isn't clear. Let me check how it's called.
            // In FFmpeg's diag_downright: left[i] goes from left[0] (top adjacent) to left[sz-1] (bottom)
            // Actually looking at the 4x4: l0=left[3], l1=left[2], l2=left[1], l3=left[0]
            // So left[0] = bottom-most, left[3] = top-most (adjacent to TL corner)
            // This matches our convention! left[0]=bottom, left[sz-1]=top
            v[i] = (byte)((left[i] + left[i + 1] * 2 + left[i + 2] + 2) >> 2);
            v[sz + 1 + i] = (byte)((top[1 + i] + top[1 + i + 1] * 2 + top[1 + i + 2] + 2) >> 2);
        }
        v[sz - 2] = (byte)((left[sz - 2] + left[sz - 1] * 2 + top[0] + 2) >> 2);
        v[sz - 1] = (byte)((left[sz - 1] + top[0] * 2 + top[1] + 2) >> 2);
        v[sz] = (byte)((top[0] + top[1] * 2 + top[2] + 2) >> 2);

        for (int j = 0; j < sz; j++)
            v.Slice(sz - 1 - j, sz).CopyTo(dst.Slice(j * stride));
    }

    private static void DiagDownRight4x4(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top)
    {
        int tl = top[0], a0 = top[1], a1 = top[2], a2 = top[3], a3 = top[4];
        int l0 = left[3], l1 = left[2], l2 = left[1], l3 = left[0];

        dst[0 + 3 * stride] = (byte)((l1 + l2 * 2 + l3 + 2) >> 2);
        dst[0 + 2 * stride] = dst[1 + 3 * stride] = (byte)((l0 + l1 * 2 + l2 + 2) >> 2);
        dst[0 + 1 * stride] = dst[1 + 2 * stride] = dst[2 + 3 * stride] = (byte)((tl + l0 * 2 + l1 + 2) >> 2);
        dst[0 + 0 * stride] = dst[1 + 1 * stride] = dst[2 + 2 * stride] = dst[3 + 3 * stride] = (byte)((l0 + tl * 2 + a0 + 2) >> 2);
        dst[1 + 0 * stride] = dst[2 + 1 * stride] = dst[3 + 2 * stride] = (byte)((tl + a0 * 2 + a1 + 2) >> 2);
        dst[2 + 0 * stride] = dst[3 + 1 * stride] = (byte)((a0 + a1 * 2 + a2 + 2) >> 2);
        dst[3 + 0 * stride] = (byte)((a1 + a2 * 2 + a3 + 2) >> 2);
    }

    // ================================================================
    // Vert-Right (D117)
    // ================================================================

    public static void VertRight(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        if (sz == 4)
        {
            VertRight4x4(dst, stride, left, top);
            return;
        }

        int halfSz = sz / 2;
        Span<byte> ve = stackalloc byte[sz + halfSz - 1];
        Span<byte> vo = stackalloc byte[sz + halfSz - 1];

        for (int i = 0; i < halfSz - 2; i++)
        {
            vo[i] = (byte)((left[i * 2 + 3] + left[i * 2 + 2] * 2 + left[i * 2 + 1] + 2) >> 2);
            ve[i] = (byte)((left[i * 2 + 4] + left[i * 2 + 3] * 2 + left[i * 2 + 2] + 2) >> 2);
        }
        // FFmpeg uses left[size-1..size-3] and top[-1]
        vo[halfSz - 2] = (byte)((left[sz - 1] + left[sz - 2] * 2 + left[sz - 3] + 2) >> 2);
        ve[halfSz - 2] = (byte)((top[0] + left[sz - 1] * 2 + left[sz - 2] + 2) >> 2);

        ve[halfSz - 1] = (byte)((top[0] + top[1] + 1) >> 1);
        vo[halfSz - 1] = (byte)((left[sz - 1] + top[0] * 2 + top[1] + 2) >> 2);
        for (int i = 0; i < sz - 1; i++)
        {
            ve[halfSz + i] = (byte)((top[1 + i] + top[1 + i + 1] + 1) >> 1);
            vo[halfSz + i] = (byte)((top[i] + top[1 + i] * 2 + top[1 + i + 1] + 2) >> 2);
        }

        for (int j = 0; j < halfSz; j++)
        {
            ve.Slice(halfSz - 1 - j, sz).CopyTo(dst.Slice(j * 2 * stride));
            vo.Slice(halfSz - 1 - j, sz).CopyTo(dst.Slice((j * 2 + 1) * stride));
        }
    }

    private static void VertRight4x4(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top)
    {
        int tl = top[0], a0 = top[1], a1 = top[2], a2 = top[3], a3 = top[4];
        int l0 = left[3], l1 = left[2], l2 = left[1];

        dst[0 + 3 * stride] = (byte)((l0 + l1 * 2 + l2 + 2) >> 2);
        dst[0 + 2 * stride] = (byte)((tl + l0 * 2 + l1 + 2) >> 2);
        dst[0 + 0 * stride] = dst[1 + 2 * stride] = (byte)((tl + a0 + 1) >> 1);
        dst[0 + 1 * stride] = dst[1 + 3 * stride] = (byte)((l0 + tl * 2 + a0 + 2) >> 2);
        dst[1 + 0 * stride] = dst[2 + 2 * stride] = (byte)((a0 + a1 + 1) >> 1);
        dst[1 + 1 * stride] = dst[2 + 3 * stride] = (byte)((tl + a0 * 2 + a1 + 2) >> 2);
        dst[2 + 0 * stride] = dst[3 + 2 * stride] = (byte)((a1 + a2 + 1) >> 1);
        dst[2 + 1 * stride] = dst[3 + 3 * stride] = (byte)((a0 + a1 * 2 + a2 + 2) >> 2);
        dst[3 + 0 * stride] = (byte)((a2 + a3 + 1) >> 1);
        dst[3 + 1 * stride] = (byte)((a1 + a2 * 2 + a3 + 2) >> 2);
    }

    // ================================================================
    // Hor-Down (D153)
    // ================================================================

    public static void HorDown(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        if (sz == 4)
        {
            HorDown4x4(dst, stride, left, top);
            return;
        }

        Span<byte> v = stackalloc byte[sz * 3 - 2];

        for (int i = 0; i < sz - 2; i++)
        {
            v[i * 2] = (byte)((left[i + 1] + left[i] + 1) >> 1);
            v[i * 2 + 1] = (byte)((left[i + 2] + left[i + 1] * 2 + left[i] + 2) >> 2);
            v[sz * 2 + i] = (byte)((top[i] + top[1 + i] * 2 + top[1 + i + 1] + 2) >> 2);
        }
        v[sz * 2 - 2] = (byte)((top[0] + left[sz - 1] + 1) >> 1);
        v[sz * 2 - 4] = (byte)((left[sz - 1] + left[sz - 2] + 1) >> 1);
        v[sz * 2 - 1] = (byte)((top[1] + top[0] * 2 + left[sz - 1] + 2) >> 2);
        v[sz * 2 - 3] = (byte)((top[0] + left[sz - 1] * 2 + left[sz - 2] + 2) >> 2);

        for (int j = 0; j < sz; j++)
            v.Slice(sz * 2 - 2 - j * 2, sz).CopyTo(dst.Slice(j * stride));
    }

    private static void HorDown4x4(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top)
    {
        int l0 = left[3], l1 = left[2], l2 = left[1], l3 = left[0];
        int tl = top[0], a0 = top[1], a1 = top[2], a2 = top[3];

        dst[2 + 0 * stride] = (byte)((tl + a0 * 2 + a1 + 2) >> 2);
        dst[3 + 0 * stride] = (byte)((a0 + a1 * 2 + a2 + 2) >> 2);
        dst[0 + 0 * stride] = dst[2 + 1 * stride] = (byte)((tl + l0 + 1) >> 1);
        dst[1 + 0 * stride] = dst[3 + 1 * stride] = (byte)((a0 + tl * 2 + l0 + 2) >> 2);
        dst[0 + 1 * stride] = dst[2 + 2 * stride] = (byte)((l0 + l1 + 1) >> 1);
        dst[1 + 1 * stride] = dst[3 + 2 * stride] = (byte)((tl + l0 * 2 + l1 + 2) >> 2);
        dst[0 + 2 * stride] = dst[2 + 3 * stride] = (byte)((l1 + l2 + 1) >> 1);
        dst[1 + 2 * stride] = dst[3 + 3 * stride] = (byte)((l0 + l1 * 2 + l2 + 2) >> 2);
        dst[0 + 3 * stride] = (byte)((l2 + l3 + 1) >> 1);
        dst[1 + 3 * stride] = (byte)((l1 + l2 * 2 + l3 + 2) >> 2);
    }

    // ================================================================
    // Vert-Left (D63)
    // ================================================================

    public static void VertLeft(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        if (sz == 4)
        {
            VertLeft4x4(dst, stride, left, top);
            return;
        }

        Span<byte> ve = stackalloc byte[sz - 1];
        Span<byte> vo = stackalloc byte[sz - 1];

        for (int i = 0; i < sz - 2; i++)
        {
            ve[i] = (byte)((top[1 + i] + top[1 + i + 1] + 1) >> 1);
            vo[i] = (byte)((top[1 + i] + top[1 + i + 1] * 2 + top[1 + i + 2] + 2) >> 2);
        }
        ve[sz - 2] = (byte)((top[1 + sz - 2] + top[1 + sz - 1] + 1) >> 1);
        vo[sz - 2] = (byte)((top[1 + sz - 2] + top[1 + sz - 1] * 3 + 2) >> 2);

        byte topRight = top[1 + sz - 1];
        for (int j = 0; j < sz / 2; j++)
        {
            int copyLen = sz - j - 1;
            ve.Slice(j, copyLen).CopyTo(dst.Slice(j * 2 * stride));
            dst.Slice(j * 2 * stride + copyLen, j + 1).Fill(topRight);
            vo.Slice(j, copyLen).CopyTo(dst.Slice((j * 2 + 1) * stride));
            dst.Slice((j * 2 + 1) * stride + copyLen, j + 1).Fill(topRight);
        }
    }

    private static void VertLeft4x4(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top)
    {
        int a0 = top[1], a1 = top[2], a2 = top[3], a3 = top[4], a4 = top[5], a5 = top[6], a6 = top[7];

        dst[0 + 0 * stride] = (byte)((a0 + a1 + 1) >> 1);
        dst[0 + 1 * stride] = (byte)((a0 + a1 * 2 + a2 + 2) >> 2);
        dst[1 + 0 * stride] = dst[0 + 2 * stride] = (byte)((a1 + a2 + 1) >> 1);
        dst[1 + 1 * stride] = dst[0 + 3 * stride] = (byte)((a1 + a2 * 2 + a3 + 2) >> 2);
        dst[2 + 0 * stride] = dst[1 + 2 * stride] = (byte)((a2 + a3 + 1) >> 1);
        dst[2 + 1 * stride] = dst[1 + 3 * stride] = (byte)((a2 + a3 * 2 + a4 + 2) >> 2);
        dst[3 + 0 * stride] = dst[2 + 2 * stride] = (byte)((a3 + a4 + 1) >> 1);
        dst[3 + 1 * stride] = dst[2 + 3 * stride] = (byte)((a3 + a4 * 2 + a5 + 2) >> 2);
        dst[3 + 2 * stride] = (byte)((a4 + a5 + 1) >> 1);
        dst[3 + 3 * stride] = (byte)((a4 + a5 * 2 + a6 + 2) >> 2);
    }

    // ================================================================
    // Hor-Up (D27)
    // ================================================================

    public static void HorUp(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top, int sz)
    {
        if (sz == 4)
        {
            HorUp4x4(dst, stride, left, top);
            return;
        }

        // For HorUp, invert_left=true in CheckIntraMode, so left[0]=top, left[sz-1]=bottom
        // This matches FFmpeg's left[i] directly — no index reversal needed

        Span<byte> v = stackalloc byte[sz * 2 - 2];
        for (int i = 0; i < sz - 2; i++)
        {
            v[i * 2] = (byte)((left[i] + left[i + 1] + 1) >> 1);
            v[i * 2 + 1] = (byte)((left[i] + left[i + 1] * 2 + left[i + 2] + 2) >> 2);
        }
        v[sz * 2 - 4] = (byte)((left[sz - 2] + left[sz - 1] + 1) >> 1);
        v[sz * 2 - 3] = (byte)((left[sz - 2] + left[sz - 1] * 3 + 2) >> 2);

        byte bottomLeft = left[sz - 1];
        int halfSz = sz / 2;
        for (int j = 0; j < halfSz; j++)
            v.Slice(j * 2, sz).CopyTo(dst.Slice(j * stride));
        for (int j = halfSz; j < sz; j++)
        {
            int copyLen = sz * 2 - 2 - j * 2;
            if (copyLen > 0)
                v.Slice(j * 2, copyLen).CopyTo(dst.Slice(j * stride));
            dst.Slice(j * stride + Math.Max(0, copyLen), 2 + j * 2 - sz).Fill(bottomLeft);
        }
    }

    private static void HorUp4x4(Span<byte> dst, int stride, ReadOnlySpan<byte> left, ReadOnlySpan<byte> top)
    {
        // For HorUp, invert_left=true in CheckIntraMode, so left[0]=top, left[3]=bottom
        // This matches FFmpeg's left[0..3] directly — no reversal needed
        int l0 = left[0], l1 = left[1], l2 = left[2], l3 = left[3];

        dst[0 + 0 * stride] = (byte)((l0 + l1 + 1) >> 1);
        dst[1 + 0 * stride] = (byte)((l0 + l1 * 2 + l2 + 2) >> 2);
        dst[0 + 1 * stride] = dst[2 + 0 * stride] = (byte)((l1 + l2 + 1) >> 1);
        dst[1 + 1 * stride] = dst[3 + 0 * stride] = (byte)((l1 + l2 * 2 + l3 + 2) >> 2);
        dst[0 + 2 * stride] = dst[2 + 1 * stride] = (byte)((l2 + l3 + 1) >> 1);
        dst[1 + 2 * stride] = dst[3 + 1 * stride] = (byte)((l2 + l3 * 3 + 2) >> 2);
        dst[0 + 3 * stride] = dst[1 + 3 * stride] = dst[2 + 2 * stride] = dst[2 + 3 * stride] =
            dst[3 + 2 * stride] = dst[3 + 3 * stride] = (byte)l3;
    }

    // ================================================================
    // Dispatch table: IntraPred[txSize][mode]
    // txSize: 0=4, 1=8, 2=16, 3=32
    // mode: VP9 intra prediction mode index
    // ================================================================

    /// <summary>
    /// Size-parameterized intra prediction dispatch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Predict(int mode, int sz, Span<byte> dst, int stride,
        ReadOnlySpan<byte> left, ReadOnlySpan<byte> top)
    {
        switch (mode)
        {
            case VertPred: Vert(dst, stride, left, top, sz); break;
            case HorPred: Hor(dst, stride, left, top, sz); break;
            case DcPred: Dc(dst, stride, left, top, sz); break;
            case DiagDownLeftPred: DiagDownLeft(dst, stride, left, top, sz); break;
            case DiagDownRightPred: DiagDownRight(dst, stride, left, top, sz); break;
            case VertRightPred: VertRight(dst, stride, left, top, sz); break;
            case HorDownPred: HorDown(dst, stride, left, top, sz); break;
            case VertLeftPred: VertLeft(dst, stride, left, top, sz); break;
            case HorUpPred: HorUp(dst, stride, left, top, sz); break;
            case TmVp8Pred: Tm(dst, stride, left, top, sz); break;
            case LeftDcPred: DcLeft(dst, stride, left, top, sz); break;
            case TopDcPred: DcTop(dst, stride, left, top, sz); break;
            case Dc128Pred: Dc128(dst, stride, left, top, sz); break;
            case Dc127Pred: Dc127(dst, stride, left, top, sz); break;
            case Dc129Pred: Dc129(dst, stride, left, top, sz); break;
        }
    }
}

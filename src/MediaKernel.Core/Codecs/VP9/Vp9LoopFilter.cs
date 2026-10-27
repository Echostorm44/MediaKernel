// VP9 loop filter — port of FFmpeg vp9lpf.c + loop_filter from vp9dsp_template.c
// Also includes mask_edges from vp9block.c and filter LUT computation.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vp9;

/// <summary>Per-superblock loop filter data — filter levels and edge masks.</summary>
public struct Vp9Filter
{
    /// <summary>Filter level per 8×8 block within a 64×64 superblock. [8*8] = [row][col].</summary>
    public readonly byte[] Level;

    /// <summary>
    /// Edge filter masks. Indexed as [plane 0=Y,1=UV][direction 0=col,1=row][row 0..7][type 0=16,1=8,2=4,3=inner4].
    /// Each byte is a bitmask of columns needing filtering.
    /// </summary>
    public readonly byte[] Mask;

    public Vp9Filter()
    {
        Level = new byte[64];
        Mask = new byte[2 * 2 * 8 * 4]; // [2][2][8][4]
    }

    public void Clear()
    {
        Array.Clear(Level);
        Array.Clear(Mask);
    }

    public void ClearMask() => Array.Clear(Mask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte MaskRef(int plane, int dir, int row, int type)
        => ref Mask[((plane * 2 + dir) * 8 + row) * 4 + type];
}

/// <summary>Loop filter LUT — limit and mblim values indexed by filter level.</summary>
public class Vp9FilterLut
{
    public readonly byte[] LimLut = new byte[64];
    public readonly byte[] MblimLut = new byte[64];

    public void Compute(int sharpness)
    {
        for (int i = 1; i <= 63; i++)
        {
            int limit = i;
            if (sharpness > 0)
            {
                limit >>= (sharpness + 3) >> 2;
                limit = Math.Min(limit, 9 - sharpness);
            }
            limit = Math.Max(limit, 1);

            LimLut[i] = (byte)limit;
            MblimLut[i] = (byte)(2 * (i + 2) + limit);
        }
    }
}

public partial class Vp9Decoder
{

    /// <summary>
    /// Compute edge filter masks for a block within a superblock.
    /// Port of FFmpeg mask_edges() from vp9block.c.
    /// </summary>
    private static void MaskEdges(byte[] mask, int maskPlaneOff,
                                   int ssH, int ssV,
                                   int rowAnd7, int colAnd7,
                                   int w, int h, int colEnd, int rowEnd,
                                   int tx, int skipInter)
    {
        // mask layout: [dir 0=col,1=row][row 0..7][type 0..3]
        // maskPlaneOff is the start of the plane's mask data (plane * 2*8*4)
        // Dir 0 (col) offset = maskPlaneOff + 0*8*4 = maskPlaneOff
        // Dir 1 (row) offset = maskPlaneOff + 1*8*4 = maskPlaneOff + 32
        ReadOnlySpan<uint> wideFilterColMask = stackalloc uint[] { 0x11, 0x01 };
        ReadOnlySpan<uint> wideFilterRowMask = stackalloc uint[] { 0x03, 0x07 };

        if (tx == 0 && (ssV | ssH) != 0) // TX_4X4 with subsampling
        {
            if (h == ssV)
            {
                if ((rowAnd7 & 1) != 0) return;
                if (rowEnd == 0) h += 1;
            }
            if (w == ssH)
            {
                if ((colAnd7 & 1) != 0) return;
                if (colEnd == 0) w += 1;
            }
        }

        int colOff = maskPlaneOff;      // dir=0 (col edges)
        int rowOff = maskPlaneOff + 32;  // dir=1 (row edges)

        if (tx == 0 && skipInter == 0) // TX_4X4, not skip
        {
            uint t = 1u << colAnd7;
            uint mCol = (t << w) - t;
            uint mRow8 = mCol & wideFilterColMask[ssH];
            uint mRow4 = mCol - mRow8;

            for (int y = rowAnd7; y < h + rowAnd7; y++)
            {
                int colMaskId = 2 - ((y & (int)wideFilterRowMask[ssV]) == 0 ? 1 : 0);

                mask[colOff + y * 4 + 1] |= (byte)mRow8;
                mask[colOff + y * 4 + 2] |= (byte)mRow4;

                if ((ssH & ssV) != 0 && (colEnd & 1) != 0 && (y & 1) != 0)
                    mask[rowOff + y * 4 + colMaskId] |= (byte)((t << (w - 1)) - t);
                else
                    mask[rowOff + y * 4 + colMaskId] |= (byte)mCol;

                if (ssH == 0)
                    mask[colOff + y * 4 + 3] |= (byte)mCol;
                if (ssV == 0)
                {
                    if (ssH != 0 && (colEnd & 1) != 0)
                        mask[rowOff + y * 4 + 3] |= (byte)((t << (w - 1)) - t);
                    else
                        mask[rowOff + y * 4 + 3] |= (byte)mCol;
                }
            }
        }
        else
        {
            uint t = 1u << colAnd7;
            uint mCol = (t << w) - t;

            if (skipInter == 0)
            {
                int maskId = tx == 1 ? 1 : 0; // TX_8X8 → 1, else 0
                int l2 = tx + ssH - 1;
                ReadOnlySpan<uint> masks = stackalloc uint[] { 0xff, 0x55, 0x11, 0x01 };
                uint mRow = mCol & masks[l2];

                if (ssH != 0 && tx > 1 && (w ^ (w - 1)) == 1)
                {
                    uint mRow16 = ((t << (w - 1)) - t) & masks[l2];
                    uint mRow8 = mRow - mRow16;
                    for (int y = rowAnd7; y < h + rowAnd7; y++)
                    {
                        mask[colOff + y * 4 + 0] |= (byte)mRow16;
                        mask[colOff + y * 4 + 1] |= (byte)mRow8;
                    }
                }
                else
                {
                    for (int y = rowAnd7; y < h + rowAnd7; y++)
                        mask[colOff + y * 4 + maskId] |= (byte)mRow;
                }

                l2 = tx + ssV - 1;
                int step1d = 1 << l2;
                if (ssV != 0 && tx > 1 && (h ^ (h - 1)) == 1)
                {
                    int y;
                    for (y = rowAnd7; y < h + rowAnd7 - 1; y += step1d)
                        mask[rowOff + y * 4 + 0] |= (byte)mCol;
                    if (y - rowAnd7 == h - 1)
                        mask[rowOff + y * 4 + 1] |= (byte)mCol;
                }
                else
                {
                    for (int y = rowAnd7; y < h + rowAnd7; y += step1d)
                        mask[rowOff + y * 4 + maskId] |= (byte)mCol;
                }
            }
            else if (tx != 0) // skip_inter && tx != TX_4X4
            {
                int maskId = (tx == 1 || h == ssV) ? 1 : 0;
                mask[rowOff + rowAnd7 * 4 + maskId] |= (byte)mCol;

                maskId = (tx == 1 || w == ssH) ? 1 : 0;
                for (int y = rowAnd7; y < h + rowAnd7; y++)
                    mask[colOff + y * 4 + maskId] |= (byte)t;
            }
            else // skip_inter && tx == TX_4X4
            {
                uint t8 = t & wideFilterColMask[ssH];
                uint t4 = t - t8;

                for (int y = rowAnd7; y < h + rowAnd7; y++)
                {
                    mask[colOff + y * 4 + 2] |= (byte)t4;
                    mask[colOff + y * 4 + 1] |= (byte)t8;
                }
                mask[rowOff + rowAnd7 * 4 + (2 - ((rowAnd7 & (int)wideFilterRowMask[ssV]) == 0 ? 1 : 0))] |= (byte)mCol;
            }
        }
    }

    /// <summary>
    /// Core VP9 loop filter for a single edge (8 pixels along the edge).
    /// Port of FFmpeg loop_filter() from vp9dsp_template.c — 8bpp.
    /// </summary>
    /// <param name="dst">Frame buffer.</param>
    /// <param name="dstOff">Offset to edge start.</param>
    /// <param name="E">MB-level limit (mblim).</param>
    /// <param name="I">Inner limit (lim).</param>
    /// <param name="H">HEV threshold (level >> 4).</param>
    /// <param name="strideA">Step along the edge (to next of 8 pixels).</param>
    /// <param name="strideB">Step across the edge (perpendicular to edge).</param>
    /// <param name="wd">Filter width: 4, 8, or 16.</param>
    private static void LoopFilter(byte[] dst, int dstOff, int E, int I, int H,
                                    int strideA, int strideB, int wd)
    {
        for (int i = 0; i < 8; i++, dstOff += strideA)
        {
            int p3 = dst[dstOff + strideB * -4], p2 = dst[dstOff + strideB * -3];
            int p1 = dst[dstOff + strideB * -2], p0 = dst[dstOff + strideB * -1];
            int q0 = dst[dstOff + strideB * 0], q1 = dst[dstOff + strideB * 1];
            int q2 = dst[dstOff + strideB * 2], q3 = dst[dstOff + strideB * 3];

            bool fm = Math.Abs(p3 - p2) <= I && Math.Abs(p2 - p1) <= I &&
                      Math.Abs(p1 - p0) <= I && Math.Abs(q1 - q0) <= I &&
                      Math.Abs(q2 - q1) <= I && Math.Abs(q3 - q2) <= I &&
                      Math.Abs(p0 - q0) * 2 + (Math.Abs(p1 - q1) >> 1) <= E;

            if (!fm) continue;

            int p7 = 0, p6 = 0, p5 = 0, p4 = 0;
            int q4 = 0, q5 = 0, q6 = 0, q7 = 0;
            bool flat8out = false, flat8in = false;

            if (wd >= 16)
            {
                p7 = dst[dstOff + strideB * -8];
                p6 = dst[dstOff + strideB * -7];
                p5 = dst[dstOff + strideB * -6];
                p4 = dst[dstOff + strideB * -5];
                q4 = dst[dstOff + strideB * 4];
                q5 = dst[dstOff + strideB * 5];
                q6 = dst[dstOff + strideB * 6];
                q7 = dst[dstOff + strideB * 7];

                flat8out = Math.Abs(p7 - p0) <= 1 && Math.Abs(p6 - p0) <= 1 &&
                           Math.Abs(p5 - p0) <= 1 && Math.Abs(p4 - p0) <= 1 &&
                           Math.Abs(q4 - q0) <= 1 && Math.Abs(q5 - q0) <= 1 &&
                           Math.Abs(q6 - q0) <= 1 && Math.Abs(q7 - q0) <= 1;
            }

            if (wd >= 8)
            {
                flat8in = Math.Abs(p3 - p0) <= 1 && Math.Abs(p2 - p0) <= 1 &&
                          Math.Abs(p1 - p0) <= 1 && Math.Abs(q1 - q0) <= 1 &&
                          Math.Abs(q2 - q0) <= 1 && Math.Abs(q3 - q0) <= 1;
            }

            if (wd >= 16 && flat8out && flat8in)
            {
                // Wide 15-tap filter
                dst[dstOff + strideB * -7] = (byte)((p7 + p7 + p7 + p7 + p7 + p7 + p7 + p6 * 2 + p5 + p4 + p3 + p2 + p1 + p0 + q0 + 8) >> 4);
                dst[dstOff + strideB * -6] = (byte)((p7 + p7 + p7 + p7 + p7 + p7 + p6 + p5 * 2 + p4 + p3 + p2 + p1 + p0 + q0 + q1 + 8) >> 4);
                dst[dstOff + strideB * -5] = (byte)((p7 + p7 + p7 + p7 + p7 + p6 + p5 + p4 * 2 + p3 + p2 + p1 + p0 + q0 + q1 + q2 + 8) >> 4);
                dst[dstOff + strideB * -4] = (byte)((p7 + p7 + p7 + p7 + p6 + p5 + p4 + p3 * 2 + p2 + p1 + p0 + q0 + q1 + q2 + q3 + 8) >> 4);
                dst[dstOff + strideB * -3] = (byte)((p7 + p7 + p7 + p6 + p5 + p4 + p3 + p2 * 2 + p1 + p0 + q0 + q1 + q2 + q3 + q4 + 8) >> 4);
                dst[dstOff + strideB * -2] = (byte)((p7 + p7 + p6 + p5 + p4 + p3 + p2 + p1 * 2 + p0 + q0 + q1 + q2 + q3 + q4 + q5 + 8) >> 4);
                dst[dstOff + strideB * -1] = (byte)((p7 + p6 + p5 + p4 + p3 + p2 + p1 + p0 * 2 + q0 + q1 + q2 + q3 + q4 + q5 + q6 + 8) >> 4);
                dst[dstOff + strideB * 0] = (byte)((p6 + p5 + p4 + p3 + p2 + p1 + p0 + q0 * 2 + q1 + q2 + q3 + q4 + q5 + q6 + q7 + 8) >> 4);
                dst[dstOff + strideB * 1] = (byte)((p5 + p4 + p3 + p2 + p1 + p0 + q0 + q1 * 2 + q2 + q3 + q4 + q5 + q6 + q7 + q7 + 8) >> 4);
                dst[dstOff + strideB * 2] = (byte)((p4 + p3 + p2 + p1 + p0 + q0 + q1 + q2 * 2 + q3 + q4 + q5 + q6 + q7 + q7 + q7 + 8) >> 4);
                dst[dstOff + strideB * 3] = (byte)((p3 + p2 + p1 + p0 + q0 + q1 + q2 + q3 * 2 + q4 + q5 + q6 + q7 + q7 + q7 + q7 + 8) >> 4);
                dst[dstOff + strideB * 4] = (byte)((p2 + p1 + p0 + q0 + q1 + q2 + q3 + q4 * 2 + q5 + q6 + q7 + q7 + q7 + q7 + q7 + 8) >> 4);
                dst[dstOff + strideB * 5] = (byte)((p1 + p0 + q0 + q1 + q2 + q3 + q4 + q5 * 2 + q6 + q7 + q7 + q7 + q7 + q7 + q7 + 8) >> 4);
                dst[dstOff + strideB * 6] = (byte)((p0 + q0 + q1 + q2 + q3 + q4 + q5 + q6 * 2 + q7 + q7 + q7 + q7 + q7 + q7 + q7 + 8) >> 4);
            }
            else if (wd >= 8 && flat8in)
            {
                // 7-tap filter
                dst[dstOff + strideB * -3] = (byte)((p3 + p3 + p3 + 2 * p2 + p1 + p0 + q0 + 4) >> 3);
                dst[dstOff + strideB * -2] = (byte)((p3 + p3 + p2 + 2 * p1 + p0 + q0 + q1 + 4) >> 3);
                dst[dstOff + strideB * -1] = (byte)((p3 + p2 + p1 + 2 * p0 + q0 + q1 + q2 + 4) >> 3);
                dst[dstOff + strideB * 0] = (byte)((p2 + p1 + p0 + 2 * q0 + q1 + q2 + q3 + 4) >> 3);
                dst[dstOff + strideB * 1] = (byte)((p1 + p0 + q0 + 2 * q1 + q2 + q3 + q3 + 4) >> 3);
                dst[dstOff + strideB * 2] = (byte)((p0 + q0 + q1 + 2 * q2 + q3 + q3 + q3 + 4) >> 3);
            }
            else
            {
                // Normal/HEV 4-tap filter
                bool hev = Math.Abs(p1 - p0) > H || Math.Abs(q1 - q0) > H;

                if (hev)
                {
                    int f = Math.Clamp(p1 - q1, -128, 127);
                    f = Math.Clamp(3 * (q0 - p0) + f, -128, 127);

                    int f1 = Math.Min(f + 4, 127) >> 3;
                    int f2 = Math.Min(f + 3, 127) >> 3;

                    dst[dstOff + strideB * -1] = (byte)Math.Clamp(p0 + f2, 0, 255);
                    dst[dstOff + strideB * 0] = (byte)Math.Clamp(q0 - f1, 0, 255);
                }
                else
                {
                    int f = Math.Clamp(3 * (q0 - p0), -128, 127);

                    int f1 = Math.Min(f + 4, 127) >> 3;
                    int f2 = Math.Min(f + 3, 127) >> 3;

                    dst[dstOff + strideB * -1] = (byte)Math.Clamp(p0 + f2, 0, 255);
                    dst[dstOff + strideB * 0] = (byte)Math.Clamp(q0 - f1, 0, 255);

                    f = (f1 + 1) >> 1;
                    dst[dstOff + strideB * -2] = (byte)Math.Clamp(p1 + f, 0, 255);
                    dst[dstOff + strideB * 1] = (byte)Math.Clamp(q1 - f, 0, 255);
                }
            }
        }
    }

    // Convenience wrappers for horizontal/vertical filter at different widths.
    // H = horizontal filter (column/vertical edges): stridea=stride (down rows), strideb=1 (across cols)
    // V = vertical filter (row/horizontal edges): stridea=1 (across cols), strideb=stride (down rows)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoopFilterH4(byte[] dst, int off, int stride, int E, int I, int H)
        => LoopFilter(dst, off, E, I, H, stride, 1, 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoopFilterV4(byte[] dst, int off, int stride, int E, int I, int H)
        => LoopFilter(dst, off, E, I, H, 1, stride, 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoopFilterH8(byte[] dst, int off, int stride, int E, int I, int H)
        => LoopFilter(dst, off, E, I, H, stride, 1, 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoopFilterV8(byte[] dst, int off, int stride, int E, int I, int H)
        => LoopFilter(dst, off, E, I, H, 1, stride, 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoopFilterH16(byte[] dst, int off, int stride, int E, int I, int H)
        => LoopFilter(dst, off, E, I, H, stride, 1, 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoopFilterV16(byte[] dst, int off, int stride, int E, int I, int H)
        => LoopFilter(dst, off, E, I, H, 1, stride, 16);

    /// <summary>
    /// Filter column (vertical) edges for one plane of a superblock row.
    /// Port of FFmpeg filter_plane_cols().
    /// </summary>
    private void FilterPlaneCols(int col, int ssH, int ssV,
                                  byte[] lvl, int lvlOff,
                                  byte[] mask, int maskOff,
                                  byte[] dst, int dstOff, int ls)
    {
        for (int y = 0; y < 8; y += 2 << ssV, dstOff += 16 * ls, lvlOff += 16 << ssV)
        {
            int ptr = dstOff;
            int l = lvlOff;
            int hmask1Off = maskOff + y * 4;
            int hmask2Off = maskOff + (y + 1 + ssV) * 4;
            uint hm1 = (uint)(mask[hmask1Off + 0] | mask[hmask1Off + 1] | mask[hmask1Off + 2]);
            uint hm13 = mask[hmask1Off + 3];
            uint hm2 = (uint)(mask[hmask2Off + 1] | mask[hmask2Off + 2]);
            uint hm23 = mask[hmask2Off + 3];
            uint hm = hm1 | hm2 | hm13 | hm23;

            for (uint x = 1; (hm & ~(x - 1)) != 0; x <<= 1, ptr += 8 >> ssH)
            {
                if (col != 0 || x > 1)
                {
                    if ((hm1 & x) != 0)
                    {
                        int L = lvl[l]; int H2 = L >> 4;
                        int E = ctx.MbLimLut[L]; int I2 = ctx.LimLut[L];

                        if ((mask[hmask1Off + 0] & x) != 0)
                        {
                            if ((mask[hmask2Off + 0] & x) != 0)
                            {
                                // H16_16: 16-wide filter on both 8-pixel halves
                                LoopFilter(dst, ptr, E, I2, H2, ls, 1, 16);
                                LoopFilter(dst, ptr + 8 * ls, E, I2, H2, ls, 1, 16);
                            }
                            else
                            {
                                LoopFilter(dst, ptr, E, I2, H2, ls, 1, 16); // H16_8
                            }
                        }
                        else if ((hm2 & x) != 0)
                        {
                            int L2 = lvl[l + (8 << ssV)];
                            int H3 = H2 | ((L2 >> 4) << 8);
                            int E2 = E | (ctx.MbLimLut[L2] << 8);
                            int I3 = I2 | (ctx.LimLut[L2] << 8);
                            // Mix2: process two 8-pixel edges
                            LoopFilterMix2H(dst, ptr, ls,
                                            (mask[hmask1Off + 1] & x) != 0 ? 1 : 0,
                                            (mask[hmask2Off + 1] & x) != 0 ? 1 : 0,
                                            E2, I3, H3);
                        }
                        else
                        {
                            int wd = (mask[hmask1Off + 1] & x) != 0 ? 8 : 4;
                            LoopFilter(dst, ptr, E, I2, H2, ls, 1, wd);
                        }
                    }
                    else if ((hm2 & x) != 0)
                    {
                        int L = lvl[l + (8 << ssV)]; int H2 = L >> 4;
                        int E = ctx.MbLimLut[L]; int I2 = ctx.LimLut[L];
                        int wd = (mask[hmask2Off + 1] & x) != 0 ? 8 : 4;
                        LoopFilter(dst, ptr + 8 * ls, E, I2, H2, ls, 1, wd);
                    }
                }

                if (ssH != 0)
                {
                    if ((x & 0xAA) != 0) l += 2;
                }
                else
                {
                    if ((hm13 & x) != 0)
                    {
                        int L = lvl[l]; int H2 = L >> 4;
                        int E = ctx.MbLimLut[L]; int I2 = ctx.LimLut[L];

                        if ((hm23 & x) != 0)
                        {
                            int L2 = lvl[l + (8 << ssV)];
                            int H3 = H2 | ((L2 >> 4) << 8);
                            int E2 = E | (ctx.MbLimLut[L2] << 8);
                            int I3 = I2 | (ctx.LimLut[L2] << 8);
                            LoopFilterMix2H(dst, ptr + 4, ls, 0, 0, E2, I3, H3);
                        }
                        else
                        {
                            LoopFilter(dst, ptr + 4, E, I2, H2, ls, 1, 4);
                        }
                    }
                    else if ((hm23 & x) != 0)
                    {
                        int L = lvl[l + (8 << ssV)]; int H2 = L >> 4;
                        int E = ctx.MbLimLut[L]; int I2 = ctx.LimLut[L];
                        LoopFilter(dst, ptr + 8 * ls + 4, E, I2, H2, ls, 1, 4);
                    }
                    l++;
                }
            }
        }
    }

    /// <summary>
    /// Filter row (horizontal) edges for one plane of a superblock row.
    /// Port of FFmpeg filter_plane_rows().
    /// </summary>
    private void FilterPlaneRows(int row, int ssH, int ssV,
                                  byte[] lvl, int lvlOff,
                                  byte[] mask, int maskOff,
                                  byte[] dst, int dstOff, int ls)
    {
        for (int y = 0; y < 8; y++, dstOff += 8 * ls >> ssV)
        {
            int ptr = dstOff;
            int l = lvlOff;
            int vmaskOff = maskOff + y * 4;
            uint vm = (uint)(mask[vmaskOff + 0] | mask[vmaskOff + 1] | mask[vmaskOff + 2]);
            uint vm3 = mask[vmaskOff + 3];

            for (uint x = 1; (vm & ~(x - 1)) != 0; x <<= (2 << ssH), ptr += 16, l += 2 << ssH)
            {
                if (row != 0 || y != 0)
                {
                    if ((vm & x) != 0)
                    {
                        int L = lvl[l]; int H2 = L >> 4;
                        int E = ctx.MbLimLut[L]; int I2 = ctx.LimLut[L];

                        if ((mask[vmaskOff + 0] & x) != 0)
                        {
                            if ((mask[vmaskOff + 0] & (x << (1 + ssH))) != 0)
                            {
                                // V16_16: 16-wide filter on both 8-pixel halves
                                LoopFilter(dst, ptr, E, I2, H2, 1, ls, 16);
                                LoopFilter(dst, ptr + 8, E, I2, H2, 1, ls, 16);
                            }
                            else
                            {
                                LoopFilter(dst, ptr, E, I2, H2, 1, ls, 16); // V16_8
                            }
                        }
                        else if ((vm & (x << (1 + ssH))) != 0)
                        {
                            int L2 = lvl[l + 1 + ssH];
                            int H3 = H2 | ((L2 >> 4) << 8);
                            int E2 = E | (ctx.MbLimLut[L2] << 8);
                            int I3 = I2 | (ctx.LimLut[L2] << 8);
                            LoopFilterMix2V(dst, ptr, ls,
                                            (mask[vmaskOff + 1] & x) != 0 ? 1 : 0,
                                            (mask[vmaskOff + 1] & (x << (1 + ssH))) != 0 ? 1 : 0,
                                            E2, I3, H3);
                        }
                        else
                        {
                            int wd = (mask[vmaskOff + 1] & x) != 0 ? 8 : 4;
                            LoopFilter(dst, ptr, E, I2, H2, 1, ls, wd);
                        }
                    }
                    else if ((vm & (x << (1 + ssH))) != 0)
                    {
                        int L = lvl[l + 1 + ssH]; int H2 = L >> 4;
                        int E = ctx.MbLimLut[L]; int I2 = ctx.LimLut[L];
                        int wd = (mask[vmaskOff + 1] & (x << (1 + ssH))) != 0 ? 8 : 4;
                        LoopFilter(dst, ptr + 8, E, I2, H2, 1, ls, wd);
                    }
                }

                if (ssV == 0)
                {
                    if ((vm3 & x) != 0)
                    {
                        int L = lvl[l]; int H2 = L >> 4;
                        int E = ctx.MbLimLut[L]; int I2 = ctx.LimLut[L];

                        if ((vm3 & (x << (1 + ssH))) != 0)
                        {
                            int L2 = lvl[l + 1 + ssH];
                            int H3 = H2 | ((L2 >> 4) << 8);
                            int E2 = E | (ctx.MbLimLut[L2] << 8);
                            int I3 = I2 | (ctx.LimLut[L2] << 8);
                            LoopFilterMix2V(dst, ptr + ls * 4, ls, 0, 0, E2, I3, H3);
                        }
                        else
                        {
                            LoopFilter(dst, ptr + ls * 4, E, I2, H2, 1, ls, 4);
                        }
                    }
                    else if ((vm3 & (x << (1 + ssH))) != 0)
                    {
                        int L = lvl[l + 1 + ssH]; int H2 = L >> 4;
                        int E = ctx.MbLimLut[L]; int I2 = ctx.LimLut[L];
                        LoopFilter(dst, ptr + ls * 4 + 8, E, I2, H2, 1, ls, 4);
                    }
                }
            }

            if (ssV != 0)
            {
                if ((y & 1) != 0) lvlOff += 16;
            }
            else
            {
                lvlOff += 8;
            }
        }
    }

    /// <summary>
    /// Mix2 horizontal filter (column edges): two stacked 8-pixel edges with potentially different filter widths.
    /// stridea=stride (down rows along edge), strideb=1 (across edge horizontally).
    /// </summary>
    private static void LoopFilterMix2H(byte[] dst, int off, int stride, int wd1, int wd2, int E, int I, int H)
    {
        LoopFilter(dst, off, E & 0xff, I & 0xff, H & 0xff, stride, 1, wd1 != 0 ? 8 : 4);
        LoopFilter(dst, off + 8 * stride, E >> 8, I >> 8, H >> 8, stride, 1, wd2 != 0 ? 8 : 4);
    }

    /// <summary>
    /// Mix2 vertical filter (row edges): two side-by-side 8-pixel edges with potentially different filter widths.
    /// stridea=1 (across edge horizontally along edge), strideb=stride (across edge vertically).
    /// </summary>
    private static void LoopFilterMix2V(byte[] dst, int off, int stride, int wd1, int wd2, int E, int I, int H)
    {
        LoopFilter(dst, off, E & 0xff, I & 0xff, H & 0xff, 1, stride, wd1 != 0 ? 8 : 4);
        LoopFilter(dst, off + 8, E >> 8, I >> 8, H >> 8, 1, stride, wd2 != 0 ? 8 : 4);
    }

    /// <summary>
    /// Apply loop filter to one superblock — all planes.
    /// Port of FFmpeg ff_vp9_loopfilter_sb().
    /// </summary>
    // Diagnostic flags — set to true to skip column/row filter passes independently
    public static bool DiagSkipColFilter = false;
    public static bool DiagSkipRowFilter = false;
    /// <summary>When >= 0, skip LF for SB rows at or above this value (in 8x8 units).</summary>
    public static int DiagSkipLfMinRow = -1;

    public void LoopfilterSb(ref Vp9Filter lflvl, int row, int col, int yOff, int uvOff)
    {
        // DIAG: Log first SB's filter state
        if (DiagSkipLoopFilter) return;
        if (DiagSkipLfMinRow >= 0 && row >= DiagSkipLfMinRow) return;
        if (DiagTraceBlocks && row == 0 && col == 0)
        {
            int maxLvl = 0;
            for (int i = 0; i < 64; i++)
                if (lflvl.Level[i] > maxLvl) maxLvl = lflvl.Level[i];
            int maskSum = 0;
            for (int i = 0; i < lflvl.Mask.Length; i++)
                maskSum += lflvl.Mask[i];
            Console.WriteLine($"[LF-SB] row={row} col={col} maxLevel={maxLvl} maskSum={maskSum}");
            Console.WriteLine($"[LF-SB] LimLut[{maxLvl}]={ctx.LimLut[Math.Min(maxLvl, 63)]} MbLimLut[{maxLvl}]={ctx.MbLimLut[Math.Min(maxLvl, 63)]}");
            Console.WriteLine($"[LF-SB] Header: FilterLevel={ctx.Header.FilterLevel} Sharpness={ctx.Header.Sharpness}");
            Console.WriteLine($"[LF-SB] LfDeltaEnabled={ctx.Header.LfDeltaEnabled} LfDeltaRef0={ctx.Header.LfDeltaRef0}");
        }

        var curFrame = ctx.Frames[Vp9DecoderContext.CurFrame];
        byte[] dstY = curFrame.LumaData!;
        int lsY = curFrame.LumaStride;
        int lsUv = curFrame.ChromaStride;

        int ssH = ctx.SubsamplingX;
        int ssV = ctx.SubsamplingY;

        // Y plane: col edges then row edges
        // mask[0] = Y plane: col=mask[0][0], row=mask[0][1]
        int yColMaskOff = 0;           // plane=0, dir=0
        int yRowMaskOff = 32;          // plane=0, dir=1
        if (!DiagSkipColFilter)
            FilterPlaneCols(col, 0, 0, lflvl.Level, 0, lflvl.Mask, yColMaskOff, dstY, yOff, lsY);
        if (!DiagSkipRowFilter)
            FilterPlaneRows(row, 0, 0, lflvl.Level, 0, lflvl.Mask, yRowMaskOff, dstY, yOff, lsY);

        // UV planes
        int uvMaskPlane = (ssH | ssV) != 0 ? 1 : 0;
        int uvColMaskOff = uvMaskPlane * 64;      // plane=uv, dir=0
        int uvRowMaskOff = uvMaskPlane * 64 + 32;  // plane=uv, dir=1
        for (int p = 0; p < 2; p++)
        {
            byte[] dstUv = p == 0 ? curFrame.ChromaUData! : curFrame.ChromaVData!;
            if (!DiagSkipColFilter)
                FilterPlaneCols(col, ssH, ssV, lflvl.Level, 0, lflvl.Mask, uvColMaskOff, dstUv, uvOff, lsUv);
            if (!DiagSkipRowFilter)
                FilterPlaneRows(row, ssH, ssV, lflvl.Level, 0, lflvl.Mask, uvRowMaskOff, dstUv, uvOff, lsUv);
        }
    }
}

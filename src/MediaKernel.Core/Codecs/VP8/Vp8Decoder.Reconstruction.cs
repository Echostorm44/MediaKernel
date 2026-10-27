// VP8 decoder reconstruction pipeline — intra predict, inter predict, IDCT, border management
// Ported from FFmpeg libavcodec/vp8.c: intra_predict, inter_predict, idct_mb,
// backup_mb_border, xchg_mb_border, decode_mb_row_no_filter, filter_mb_row

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.VP8;

public partial class Vp8Decoder
{
    private const int EdgeEmuLinesize = 32;

    /// <summary>Edge emulation buffer for MC near frame edges.</summary>
    private readonly byte[] edgeEmuBuffer = new byte[21 * EdgeEmuLinesize];

    /// <summary>Per-MB filter strengths for current row.</summary>
    private Vp8FilterStrength[]? filterStrength;

    /// <summary>MV bounds for current MB row.</summary>
    private Vp8MvBounds mvBounds;

    // ==================== Intra prediction ====================

    /// <summary>
    /// Perform intra prediction for a macroblock.
    /// Port of intra_predict() from vp8.c (VP8 path only).
    /// </summary>
    private void IntraPredict(Span<byte> dstY, int offY, int linesize,
                               Span<byte> dstU, int offU, Span<byte> dstV, int offV, int uvlinesize,
                               ref Vp8Macroblock mb, int mbX, int mbY)
    {
        // For the first row, we need xchg_mb_border to init the top edge to 127
        // Otherwise, skip if we aren't going to deblock
        if (mbY > 0 && (ctx.DeblockFilter || mbY == 0))
            XchgMbBorder(dstY, offY, linesize, dstU, offU, dstV, offV, uvlinesize,
                         mbX, mbY, true);

        if (mb.Mode < (int)Vp8Pred16x16Mode.I4x4)
        {
            int mode = Vp8IntraPred.CheckIntraPred8x8Mode(mb.Mode, mbX, mbY);
            Vp8IntraPred.Pred16x16(dstY, offY, linesize, mode);
        }
        else
        {
            IntraPredict4x4(dstY, offY, linesize, ref mb, mbX, mbY);
        }

        // Chroma prediction (8x8)
        int chromaMode = Vp8IntraPred.CheckIntraPred8x8Mode(mb.ChromaPredMode, mbX, mbY);
        Vp8IntraPred.Pred8x8(dstU, offU, uvlinesize, chromaMode);
        Vp8IntraPred.Pred8x8(dstV, offV, uvlinesize, chromaMode);

        // Restore border pixels
        if (mbY > 0 && (ctx.DeblockFilter || mbY == 0))
            XchgMbBorder(dstY, offY, linesize, dstU, offU, dstV, offV, uvlinesize,
                         mbX, mbY, false);
    }

    /// <summary>
    /// 4x4 intra prediction with interleaved IDCT.
    /// Port of the MODE_I4x4 branch of intra_predict().
    /// </summary>
    private unsafe void IntraPredict4x4(Span<byte> dst, int off, int stride,
                                         ref Vp8Macroblock mb, int mbX, int mbY)
    {
        const byte lo = 127, hi = 129;

        // Top-right pixels for right-edge blocks come from the row above, 16 pixels right
        int trRightOff = off - stride + 16;

        // If rightmost MB in frame, extend from last top pixel
        Span<byte> trExtended = stackalloc byte[4];
        bool useExtendedTr = (mbY > 0 && mbX == ctx.MbWidth - 1);
        if (useExtendedTr)
        {
            byte trByte = dst[off - stride + 15];
            trExtended[0] = trExtended[1] = trExtended[2] = trExtended[3] = trByte;
        }

        // tr_top for top-of-frame fallback
        Span<byte> trTop = stackalloc byte[4];
        trTop[0] = trTop[1] = trTop[2] = trTop[3] = lo;

        if (mb.Skip != 0)
            Array.Clear(nonZeroCountCache);

        int ptr = off;
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int blockOff = ptr + 4 * x;

                // Determine topright source — follows FFmpeg's logic exactly
                ReadOnlySpan<byte> topright;
                if ((y == 0 || x == 3) && mbY == 0)
                    topright = trTop;
                else if (x == 3)
                    topright = useExtendedTr ? (ReadOnlySpan<byte>)trExtended : dst.Slice(trRightOff, 4);
                else
                    topright = dst.Slice(blockOff + 4 - stride, 4);

                int mode = mb.Intra4x4PredMode[y * 4 + x];
                var (adjMode, needsCopy) = Vp8IntraPred.CheckIntraPred4x4Mode(mode, mbX + x, mbY + y);

                if (needsCopy)
                {
                    // Edge emulation: predict into temp buffer then copy back
                    Span<byte> copyBuf = stackalloc byte[5 * 8];

                    if (mbY + y == 0)
                    {
                        copyBuf[3] = lo;
                        copyBuf[4] = lo; copyBuf[5] = lo; copyBuf[6] = lo; copyBuf[7] = lo;
                    }
                    else
                    {
                        dst.Slice(blockOff - stride, 4).CopyTo(copyBuf.Slice(4));
                        copyBuf[3] = (mbX + x == 0) ? hi : dst[blockOff - stride - 1];
                    }

                    if (mbX + x == 0)
                    {
                        copyBuf[11] = copyBuf[19] = copyBuf[27] = copyBuf[35] = hi;
                    }
                    else
                    {
                        copyBuf[11] = dst[blockOff - 1];
                        copyBuf[19] = dst[blockOff + stride - 1];
                        copyBuf[27] = dst[blockOff + stride * 2 - 1];
                        copyBuf[35] = dst[blockOff + stride * 3 - 1];
                    }

                    Vp8IntraPred.Pred4x4(copyBuf, 12, 8, topright, adjMode);

                    // Copy back to frame
                    for (int row = 0; row < 4; row++)
                        copyBuf.Slice(12 + row * 8, 4).CopyTo(dst.Slice(blockOff + row * stride, 4));
                }
                else
                {
                    Vp8IntraPred.Pred4x4(dst, blockOff, stride, topright, adjMode);
                }

                // Apply per-block IDCT for I4x4 mode
                int nnz = nonZeroCountCache[y, x];
                if (nnz > 0)
                {
                    if (nnz == 1)
                        Vp8Dsp.IdctDcAdd(dst.Slice(blockOff), stride, blockCoeffs[y][x]);
                    else
                        Vp8Dsp.IdctAdd(dst.Slice(blockOff), stride, blockCoeffs[y][x]);
                }
            }

            ptr += 4 * stride;
        }
    }

    // ==================== Inter prediction ====================

    /// <summary>
    /// Apply motion compensation for an inter macroblock.
    /// Port of inter_predict() from vp8.c.
    /// </summary>
    private void InterPredict(Span<byte> dstY, int offY, int linesize,
                               Span<byte> dstU, int offU, Span<byte> dstV, int offV, int uvlinesize,
                               ref Vp8Macroblock mb, int mbX, int mbY)
    {
        int xOff = mbX << 4, yOff = mbY << 4;
        int width = 16 * ctx.MbWidth, height = 16 * ctx.MbHeight;
        var refFrame = ctx.FrameRef[mb.RefFrame];
        if (refFrame == null || !refFrame.IsAllocated) return;

        // FFmpeg: profiles 1-3 use bilinear MC, profile 0 uses 6-tap (epel)
        bool bilinear = ctx.Profile > 0;

        switch ((Vp8SplitMvMode)mb.Partitioning)
        {
            case Vp8SplitMvMode.None:
                McPart(dstY, offY, linesize, dstU, offU, dstV, offV, uvlinesize,
                       refFrame, xOff, yOff, 0, 0, 16, 16, width, height, mb.Mv, bilinear);
                break;

            case Vp8SplitMvMode.Full4x4:
                unsafe
                {
                    // Y: 16 4x4 blocks
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            var bmv = new Vp8Mv(mb.Bmv[(y * 4 + x) * 2], mb.Bmv[(y * 4 + x) * 2 + 1]);
                            McLuma(dstY, offY + y * 4 * linesize + x * 4, linesize,
                                   refFrame, bmv, 4 * x + xOff, 4 * y + yOff, 4, 4,
                                   width, height, bilinear);
                        }

                    // U/V: average 4 sub-block MVs per 8x8 chroma block
                    int cxOff = xOff >> 1, cyOff = yOff >> 1;
                    int cWidth = width >> 1, cHeight = height >> 1;
                    for (int y = 0; y < 2; y++)
                        for (int x = 0; x < 2; x++)
                        {
                            int mvX = mb.Bmv[(2 * y * 4 + 2 * x) * 2] +
                                      mb.Bmv[(2 * y * 4 + 2 * x + 1) * 2] +
                                      mb.Bmv[((2 * y + 1) * 4 + 2 * x) * 2] +
                                      mb.Bmv[((2 * y + 1) * 4 + 2 * x + 1) * 2];
                            int mvY = mb.Bmv[(2 * y * 4 + 2 * x) * 2 + 1] +
                                      mb.Bmv[(2 * y * 4 + 2 * x + 1) * 2 + 1] +
                                      mb.Bmv[((2 * y + 1) * 4 + 2 * x) * 2 + 1] +
                                      mb.Bmv[((2 * y + 1) * 4 + 2 * x + 1) * 2 + 1];
                            // FF_SIGNBIT: (sum + 2 + sign) >> 2 where sign = -1 for negative, 0 for non-negative
                            mvX = (mvX + 2 + (mvX >> 31)) >> 2;
                            mvY = (mvY + 2 + (mvY >> 31)) >> 2;
                            if (ctx.Profile == 3) { mvX &= ~7; mvY &= ~7; }

                            var uvmv = new Vp8Mv((short)mvX, (short)mvY);
                            McChroma(dstU, offU + y * 4 * uvlinesize + x * 4,
                                     dstV, offV + y * 4 * uvlinesize + x * 4, uvlinesize,
                                     refFrame, uvmv, 4 * x + cxOff, 4 * y + cyOff, 4, 4,
                                     cWidth, cHeight, bilinear);
                        }
                }
                break;

            case Vp8SplitMvMode.TopBottom:
                unsafe
                {
                    var bmv0 = new Vp8Mv(mb.Bmv[0], mb.Bmv[1]);
                    var bmv1 = new Vp8Mv(mb.Bmv[2], mb.Bmv[3]);
                    McPart(dstY, offY, linesize, dstU, offU, dstV, offV, uvlinesize,
                           refFrame, xOff, yOff, 0, 0, 16, 8, width, height, bmv0, bilinear);
                    McPart(dstY, offY, linesize, dstU, offU, dstV, offV, uvlinesize,
                           refFrame, xOff, yOff, 0, 8, 16, 8, width, height, bmv1, bilinear);
                }
                break;

            case Vp8SplitMvMode.LeftRight:
                unsafe
                {
                    var bmv0 = new Vp8Mv(mb.Bmv[0], mb.Bmv[1]);
                    var bmv1 = new Vp8Mv(mb.Bmv[2], mb.Bmv[3]);
                    McPart(dstY, offY, linesize, dstU, offU, dstV, offV, uvlinesize,
                           refFrame, xOff, yOff, 0, 0, 8, 16, width, height, bmv0, bilinear);
                    McPart(dstY, offY, linesize, dstU, offU, dstV, offV, uvlinesize,
                           refFrame, xOff, yOff, 8, 0, 8, 16, width, height, bmv1, bilinear);
                }
                break;

            case Vp8SplitMvMode.Quarters:
                unsafe
                {
                    for (int i = 0; i < 4; i++)
                    {
                        var bmv = new Vp8Mv(mb.Bmv[i * 2], mb.Bmv[i * 2 + 1]);
                        int bx = (i & 1) * 8, by = (i >> 1) * 8;
                        McPart(dstY, offY, linesize, dstU, offU, dstV, offV, uvlinesize,
                               refFrame, xOff, yOff, bx, by, 8, 8, width, height, bmv, bilinear);
                    }
                }
                break;
        }
    }

    /// <summary>MC for a sub-partition: luma + chroma.</summary>
    private void McPart(Span<byte> dstY, int offY, int linesize,
                         Span<byte> dstU, int offU, Span<byte> dstV, int offV, int uvlinesize,
                         Vp8Frame refFrame, int xOff, int yOff,
                         int bxOff, int byOff, int blockW, int blockH,
                         int width, int height, Vp8Mv mv, bool bilinear)
    {
        McLuma(dstY, offY + byOff * linesize + bxOff, linesize,
               refFrame, mv, xOff + bxOff, yOff + byOff, blockW, blockH,
               width, height, bilinear);

        var uvmv = mv;
        if (ctx.Profile == 3) { uvmv.X &= unchecked((short)~7); uvmv.Y &= unchecked((short)~7); }

        int cxOff = (xOff + bxOff) >> 1;
        int cyOff = (yOff + byOff) >> 1;
        int cbxOff = bxOff >> 1;
        int cbyOff = byOff >> 1;
        int cw = blockW >> 1, ch = blockH >> 1;

        McChroma(dstU, offU + cbyOff * uvlinesize + cbxOff,
                 dstV, offV + cbyOff * uvlinesize + cbxOff, uvlinesize,
                 refFrame, uvmv, cxOff, cyOff, cw, ch,
                 width >> 1, height >> 1, bilinear);
    }

    /// <summary>
    /// Luma motion compensation with subpel interpolation and edge emulation.
    /// Port of vp8_mc_luma() from vp8.c.
    /// </summary>
    private void McLuma(Span<byte> dst, int dstOff, int dstStride,
                         Vp8Frame refFrame, Vp8Mv mv,
                         int xOff, int yOff, int blockW, int blockH,
                         int width, int height, bool bilinear)
    {
        ReadOnlySpan<byte> src = refFrame.DataY;
        int srcBase = refFrame.YOffset;
        int srcStride = refFrame.Linesize;

        if (mv.AsUint() != 0)
        {
            int mx = (mv.X * 2) & 7, mxIdx = Vp8Dsp.SubpelIdx[0, mx];
            int my = (mv.Y * 2) & 7, myIdx = Vp8Dsp.SubpelIdx[0, my];

            xOff += mv.X >> 2;
            yOff += mv.Y >> 2;

            int srcOff = srcBase + yOff * srcStride + xOff;

            if (xOff < mxIdx || xOff >= width - blockW - Vp8Dsp.SubpelIdx[2, mx] ||
                yOff < myIdx || yOff >= height - blockH - Vp8Dsp.SubpelIdx[2, my])
            {
                Vp8Dsp.EmulatedEdgeMc(edgeEmuBuffer, EdgeEmuLinesize,
                                       src, srcOff - myIdx * srcStride - mxIdx, srcStride,
                                       blockW + Vp8Dsp.SubpelIdx[1, mx],
                                       blockH + Vp8Dsp.SubpelIdx[1, my],
                                       xOff - mxIdx, yOff - myIdx, width, height);

                Vp8Dsp.McBlock(dst, dstOff, dstStride,
                                edgeEmuBuffer, mxIdx + EdgeEmuLinesize * myIdx, EdgeEmuLinesize,
                                blockW, blockH, mx, my, mxIdx, myIdx, bilinear);
            }
            else
            {
                Vp8Dsp.McBlock(dst, dstOff, dstStride,
                                src, srcOff, srcStride,
                                blockW, blockH, mx, my, mxIdx, myIdx, bilinear);
            }
        }
        else
        {
            Vp8Dsp.PutPixels(dst, dstOff, dstStride,
                              src, srcBase + yOff * srcStride + xOff, srcStride, blockW, blockH);
        }
    }

    /// <summary>
    /// Chroma motion compensation.
    /// Port of vp8_mc_chroma() from vp8.c.
    /// </summary>
    private void McChroma(Span<byte> dstU, int offU,
                           Span<byte> dstV, int offV, int uvStride,
                           Vp8Frame refFrame, Vp8Mv mv,
                           int xOff, int yOff, int blockW, int blockH,
                           int width, int height, bool bilinear)
    {
        ReadOnlySpan<byte> srcU = refFrame.DataCb;
        ReadOnlySpan<byte> srcV = refFrame.DataCr;
        int srcBase = refFrame.UvOffset;
        int srcStride = refFrame.UvLinesize;

        if (mv.AsUint() != 0)
        {
            int mx = mv.X & 7, mxIdx = Vp8Dsp.SubpelIdx[0, mx];
            int my = mv.Y & 7, myIdx = Vp8Dsp.SubpelIdx[0, my];

            xOff += mv.X >> 3;
            yOff += mv.Y >> 3;

            int srcOff = srcBase + yOff * srcStride + xOff;

            if (xOff < mxIdx || xOff >= width - blockW - Vp8Dsp.SubpelIdx[2, mx] ||
                yOff < myIdx || yOff >= height - blockH - Vp8Dsp.SubpelIdx[2, my])
            {
                Vp8Dsp.EmulatedEdgeMc(edgeEmuBuffer, EdgeEmuLinesize,
                                       srcU, srcOff - myIdx * srcStride - mxIdx, srcStride,
                                       blockW + Vp8Dsp.SubpelIdx[1, mx],
                                       blockH + Vp8Dsp.SubpelIdx[1, my],
                                       xOff - mxIdx, yOff - myIdx, width, height);
                Vp8Dsp.McBlock(dstU, offU, uvStride,
                                edgeEmuBuffer, mxIdx + EdgeEmuLinesize * myIdx, EdgeEmuLinesize,
                                blockW, blockH, mx, my, mxIdx, myIdx, bilinear);

                Vp8Dsp.EmulatedEdgeMc(edgeEmuBuffer, EdgeEmuLinesize,
                                       srcV, srcOff - myIdx * srcStride - mxIdx, srcStride,
                                       blockW + Vp8Dsp.SubpelIdx[1, mx],
                                       blockH + Vp8Dsp.SubpelIdx[1, my],
                                       xOff - mxIdx, yOff - myIdx, width, height);
                Vp8Dsp.McBlock(dstV, offV, uvStride,
                                edgeEmuBuffer, mxIdx + EdgeEmuLinesize * myIdx, EdgeEmuLinesize,
                                blockW, blockH, mx, my, mxIdx, myIdx, bilinear);
            }
            else
            {
                Vp8Dsp.McBlock(dstU, offU, uvStride, srcU, srcOff, srcStride,
                                blockW, blockH, mx, my, mxIdx, myIdx, bilinear);
                Vp8Dsp.McBlock(dstV, offV, uvStride, srcV, srcOff, srcStride,
                                blockW, blockH, mx, my, mxIdx, myIdx, bilinear);
            }
        }
        else
        {
            int srcOff = srcBase + yOff * srcStride + xOff;
            Vp8Dsp.PutPixels(dstU, offU, uvStride, srcU, srcOff, srcStride, blockW, blockH);
            Vp8Dsp.PutPixels(dstV, offV, uvStride, srcV, srcOff, srcStride, blockW, blockH);
        }
    }

    // ==================== IDCT ====================

    /// <summary>
    /// Apply IDCT to a non-I4x4 macroblock's residuals.
    /// Port of idct_mb() from vp8.c.
    /// For I4x4 mode, IDCT is applied inline during IntraPredict4x4.
    /// </summary>
    private void IdctMb(Span<byte> dstY, int offY, int linesize,
                         Span<byte> dstU, int offU, Span<byte> dstV, int offV, int uvlinesize,
                         ref Vp8Macroblock mb)
    {
        // Luma
        if (mb.Mode != (int)Vp8Pred16x16Mode.I4x4)
        {
            int yPtr = offY;
            for (int y = 0; y < 4; y++)
            {
                uint nnz4 = (uint)nonZeroCountCache[y, 0] |
                            ((uint)nonZeroCountCache[y, 1] << 8) |
                            ((uint)nonZeroCountCache[y, 2] << 16) |
                            ((uint)nonZeroCountCache[y, 3] << 24);
                if (nnz4 != 0)
                {
                    if ((nnz4 & ~0x01010101u) != 0)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            byte nnz = (byte)nnz4;
                            if (nnz == 1)
                                Vp8Dsp.IdctDcAdd(dstY.Slice(yPtr + 4 * x), linesize, blockCoeffs[y][x]);
                            else if (nnz > 1)
                                Vp8Dsp.IdctAdd(dstY.Slice(yPtr + 4 * x), linesize, blockCoeffs[y][x]);
                            nnz4 >>= 8;
                            if (nnz4 == 0) break;
                        }
                    }
                    else
                    {
                        Vp8Dsp.IdctDcAdd4Y(dstY.Slice(yPtr), blockCoeffs[y], linesize);
                    }
                }
                yPtr += 4 * linesize;
            }
        }

        // Chroma (U and V)
        for (int ch = 0; ch < 2; ch++)
        {
            int plane = 4 + ch;
            uint nnz4 = (uint)nonZeroCountCache[plane, 0] |
                        ((uint)nonZeroCountCache[plane, 1] << 8) |
                        ((uint)nonZeroCountCache[plane, 2] << 16) |
                        ((uint)nonZeroCountCache[plane, 3] << 24);
            if (nnz4 != 0)
            {
                Span<byte> chDst = (ch == 0) ? dstU : dstV;
                int chOff = (ch == 0) ? offU : offV;
                if ((nnz4 & ~0x01010101u) != 0)
                {
                    int chPtr = chOff;
                    for (int y = 0; y < 2; y++)
                    {
                        for (int x = 0; x < 2; x++)
                        {
                            byte nnz = (byte)nnz4;
                            if (nnz == 1)
                                Vp8Dsp.IdctDcAdd(chDst.Slice(chPtr + 4 * x), uvlinesize, blockCoeffs[plane][(y << 1) + x]);
                            else if (nnz > 1)
                                Vp8Dsp.IdctAdd(chDst.Slice(chPtr + 4 * x), uvlinesize, blockCoeffs[plane][(y << 1) + x]);
                            nnz4 >>= 8;
                            if (nnz4 == 0) goto chromaDone;
                        }
                        chPtr += 4 * uvlinesize;
                    }
                }
                else
                {
                    Vp8Dsp.IdctDcAdd4Uv(chDst.Slice(chOff), blockCoeffs[plane], uvlinesize);
                }
            }
        chromaDone:;
        }
    }

    // ==================== Border management ====================

    /// <summary>
    /// Save the bottom row of a MB for use as the top-row reference by the next MB row.
    /// Port of backup_mb_border from vp8.c.
    /// </summary>
    private static void BackupMbBorder(Span<byte> topBorder,
                                        ReadOnlySpan<byte> srcY, int yOff, int linesize,
                                        ReadOnlySpan<byte> srcCb, int cbOff,
                                        ReadOnlySpan<byte> srcCr, int crOff, int uvlinesize,
                                        bool simple)
    {
        srcY.Slice(yOff + 15 * linesize, 16).CopyTo(topBorder);

        if (!simple)
        {
            srcCb.Slice(cbOff + 7 * uvlinesize, 8).CopyTo(topBorder.Slice(16));
            srcCr.Slice(crOff + 7 * uvlinesize, 8).CopyTo(topBorder.Slice(24));
        }
    }

    /// <summary>
    /// Exchange/copy MB border pixels for intra prediction neighbor access.
    /// Port of xchg_mb_border from vp8.c.
    /// </summary>
    private void XchgMbBorder(Span<byte> srcY, int yOff, int linesize,
                               Span<byte> srcCb, int cbOff,
                               Span<byte> srcCr, int crOff, int uvlinesize,
                               int mbX, int mbY, bool xchg)
    {
        if (ctx.TopBorder == null) return;

        Span<byte> topBorder = ctx.TopBorder.AsSpan(mbX * 32, 32);
        Span<byte> topBorderM1 = mbX > 0 ? ctx.TopBorder.AsSpan((mbX - 1) * 32, 32) : Span<byte>.Empty;

        // Adjust to row above the current MB
        int yAbove = yOff - linesize;
        int cbAbove = cbOff - uvlinesize;
        int crAbove = crOff - uvlinesize;

        // Exchange luma — follows FFmpeg: XCHG(top_border_m1 + 8, src_y - 8, xchg)
        if (topBorderM1.Length > 0)
            Xchg8(topBorderM1.Slice(8), srcY, yAbove - 8, xchg);
        Xchg8(topBorder, srcY, yAbove, xchg);
        Xchg8(topBorder.Slice(8), srcY, yAbove + 8, true); // always swap right half

        if (mbX < ctx.MbWidth - 1)
        {
            Span<byte> topBorderP1 = ctx.TopBorder.AsSpan((mbX + 1) * 32, 32);
            Xchg8(topBorderP1, srcY, yAbove + 16, true);
        }

        // Exchange chroma (only for normal loop filter or first row init)
        if (!ctx.Filter.Simple || mbY == 0)
        {
            if (topBorderM1.Length > 0)
            {
                Xchg8(topBorderM1.Slice(16), srcCb, cbAbove - 8, xchg);
                Xchg8(topBorderM1.Slice(24), srcCr, crAbove - 8, xchg);
            }
            Xchg8(topBorder.Slice(16), srcCb, cbAbove, true);
            Xchg8(topBorder.Slice(24), srcCr, crAbove, true);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Xchg8(Span<byte> border, Span<byte> src, int off, bool swap)
    {
        if (swap)
        {
            for (int i = 0; i < 8; i++)
            {
                byte tmp = border[i];
                border[i] = src[off + i];
                src[off + i] = tmp;
            }
        }
        else
        {
            // Copy border → frame (restore filtered values after prediction)
            for (int i = 0; i < 8; i++)
                src[off + i] = border[i];
        }
    }

    // ==================== Main decode loop ====================

    /// <summary>
    /// Decode one MB row: mode decode, coefficient decode, reconstruction.
    /// Port of decode_mb_row_no_filter (single-threaded path).
    /// </summary>
    private unsafe int DecodeMbRowNoFilter(Vp8Frame curFrame, Vp8Frame? prevFrame, int mbY)
    {
        int linesize = curFrame.Linesize;
        int uvlinesize = curFrame.UvLinesize;
        int yBase = curFrame.YOffset + 16 * mbY * linesize;
        int cbBase = curFrame.UvOffset + 8 * mbY * uvlinesize;
        int crBase = curFrame.UvOffset + 8 * mbY * uvlinesize;
        Span<byte> dataY = curFrame.DataY;
        Span<byte> dataCb = curFrame.DataCb;
        Span<byte> dataCr = curFrame.DataCr;

        ref var coeffCoder = ref coeffCoders[mbY & (ctx.NumCoeffPartitions - 1)];

        // MB row base (with +1 offset for left sentinel)
        int mbBase = (mbY + 1) * (ctx.MbWidth + 1) + 1;
        int mbXY = mbY * ctx.MbWidth;

        // Reset left context
        leftNnz.AsSpan().Clear();
        Array.Fill(intra4x4PredModeLeft, (byte)2); // DC_PRED

        // MV bounds
        mvBounds.MinX = -64;
        mvBounds.MaxX = ((ctx.MbWidth - 1) << 6) + 64;

        Span<byte> topNnz = ctx.TopNnz;

        // Segment map pointers — pin for the entire row
        byte[] curSegMap = curFrame.SegMap!;
        byte[]? prevSegMapArray = prevFrame?.SegMap;

        for (int mbX = 0; mbX < ctx.MbWidth; mbX++)
        {
            ref var mb = ref ctx.Macroblocks![mbBase + mbX];
            int offY = yBase + mbX * 16;
            int offU = cbBase + mbX * 8;
            int offV = crBase + mbX * 8;

            // Segmentation: inherit from previous frame or decode from bitstream
            byte segment = 0;
            int segIdx = mbXY + mbX;
            byte prevSeg = (prevSegMapArray != null && segIdx < prevSegMapArray.Length)
                ? prevSegMapArray[segIdx]
                : (byte)0;

            // Mode decode — segment is set by DecodeMbMode
            DecodeMbMode(ref mb, mbX, mbY, ref segment, prevSeg);

            // Store segment into current frame's segment map
            curSegMap[segIdx] = segment;

            // Coefficient decode
            if (mb.Skip == 0)
            {
                DecodeMbCoeffs(ref mb, ref coeffCoder, topNnz.Slice(mbX * 9, 9), mbX, mbY);
            }

            // Prediction (intra or inter)
            if (mb.Mode <= (int)Vp8Pred16x16Mode.I4x4)
            {
                IntraPredict(dataY, offY, linesize, dataCb, offU, dataCr, offV, uvlinesize,
                             ref mb, mbX, mbY);
            }
            else
            {
                InterPredict(dataY, offY, linesize, dataCb, offU, dataCr, offV, uvlinesize,
                             ref mb, mbX, mbY);
            }

            // IDCT
            if (mb.Skip == 0)
            {
                IdctMb(dataY, offY, linesize, dataCb, offU, dataCr, offV, uvlinesize, ref mb);
            }

            if (mb.Skip != 0)
            {
                // Clear NNZ for skip blocks
                leftNnz.AsSpan(0, 8).Clear();
                topNnz.Slice(mbX * 9, 8).Clear();

                if (mb.Mode != (int)Vp8Pred16x16Mode.I4x4 &&
                    mb.Mode != (int)Vp8InterMvMode.Split)
                {
                    leftNnz[8] = 0;
                    topNnz[mbX * 9 + 8] = 0;
                }
            }

            // Calculate filter strength
            if (ctx.DeblockFilter)
            {
                Vp8LoopFilter.FilterLevelForMb(ctx, in mb, out int fl, out int il, out bool inf);
                filterStrength![mbX] = new Vp8FilterStrength
                {
                    FilterLevel = (byte)fl,
                    InnerLimit = (byte)il,
                    InnerFilter = inf ? (byte)1 : (byte)0
                };
            }

            mvBounds.MinX -= 64;
            mvBounds.MaxX -= 64;
        }

        return 0;
    }

    /// <summary>
    /// Apply loop filter to one MB row.
    /// Port of filter_mb_row (single-threaded path).
    /// </summary>
    private void FilterMbRow(Vp8Frame curFrame, int mbY)
    {
        int linesize = curFrame.Linesize;
        int uvlinesize = curFrame.UvLinesize;
        int yBase = curFrame.YOffset + 16 * mbY * linesize;
        int cbBase = curFrame.UvOffset + 8 * mbY * uvlinesize;
        int crBase = curFrame.UvOffset + 8 * mbY * uvlinesize;
        Span<byte> dataY = curFrame.DataY;
        Span<byte> dataCb = curFrame.DataCb;
        Span<byte> dataCr = curFrame.DataCr;

        for (int mbX = 0; mbX < ctx.MbWidth; mbX++)
        {
            ref var f = ref filterStrength![mbX];
            int offY = yBase + mbX * 16;
            int offU = cbBase + mbX * 8;
            int offV = crBase + mbX * 8;

            // Backup border for next row's intra prediction
            Span<byte> topBorder = ctx.TopBorder.AsSpan(mbX * 32, 32);
            BackupMbBorder(topBorder,
                           dataY, offY, linesize,
                           dataCb, offU, dataCr, offV, uvlinesize,
                           ctx.Filter.Simple);

            if (ctx.Filter.Simple)
                Vp8LoopFilter.FilterMbSimple(dataY, offY, linesize,
                                              f.FilterLevel, f.InnerLimit, f.InnerFilter != 0,
                                              mbX, mbY);
            else
                Vp8LoopFilter.FilterMb(dataY, offY, linesize,
                                        dataCb, offU, dataCr, offV, uvlinesize,
                                        f.FilterLevel, f.InnerLimit, f.InnerFilter != 0,
                                        ctx.Keyframe, mbX, mbY);
        }
    }
}

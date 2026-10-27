// VP9 decoder — reconstruction (intra prediction + transforms, inter prediction + MC)
// Ported from FFmpeg libavcodec/vp9recon.c and vp9_mc_template.c

using System.Buffers;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vp9;

public sealed partial class Vp9Decoder
{
    // Mode conversion table: [orig_mode][have_left][have_top] → adjusted mode
    // Used when neighboring pixels aren't available for prediction
    // Indexed by FFmpeg vp9.h enum order: VERT=0, HOR=1, DC=2, DDL=3, DDR=4, VR=5, HD=6, VL=7, HU=8, TM=9
    private static ReadOnlySpan<byte> IntraModeConv => new byte[]
    {
        // VERT_PRED (idx 0)
        (byte)Vp9IntraMode.Dc127, (byte)Vp9IntraMode.Vertical, (byte)Vp9IntraMode.Dc127, (byte)Vp9IntraMode.Vertical,
        // HOR_PRED (idx 1)
        (byte)Vp9IntraMode.Dc129, (byte)Vp9IntraMode.Dc129, (byte)Vp9IntraMode.Horizontal, (byte)Vp9IntraMode.Horizontal,
        // DC_PRED (idx 2)
        (byte)Vp9IntraMode.Dc128, (byte)Vp9IntraMode.TopDc, (byte)Vp9IntraMode.LeftDc, (byte)Vp9IntraMode.Dc,
        // DIAG_DOWN_LEFT (idx 3)
        (byte)Vp9IntraMode.Dc127, (byte)Vp9IntraMode.DiagonalDownLeft, (byte)Vp9IntraMode.Dc127, (byte)Vp9IntraMode.DiagonalDownLeft,
        // DIAG_DOWN_RIGHT (idx 4)
        (byte)Vp9IntraMode.DiagonalDownRight, (byte)Vp9IntraMode.DiagonalDownRight, (byte)Vp9IntraMode.DiagonalDownRight, (byte)Vp9IntraMode.DiagonalDownRight,
        // VERT_RIGHT (idx 5)
        (byte)Vp9IntraMode.VerticalRight, (byte)Vp9IntraMode.VerticalRight, (byte)Vp9IntraMode.VerticalRight, (byte)Vp9IntraMode.VerticalRight,
        // HOR_DOWN (idx 6)
        (byte)Vp9IntraMode.HorizontalDown, (byte)Vp9IntraMode.HorizontalDown, (byte)Vp9IntraMode.HorizontalDown, (byte)Vp9IntraMode.HorizontalDown,
        // VERT_LEFT (idx 7)
        (byte)Vp9IntraMode.Dc127, (byte)Vp9IntraMode.VerticalLeft, (byte)Vp9IntraMode.Dc127, (byte)Vp9IntraMode.VerticalLeft,
        // HOR_UP (idx 8)
        (byte)Vp9IntraMode.Dc129, (byte)Vp9IntraMode.Dc129, (byte)Vp9IntraMode.HorizontalUp, (byte)Vp9IntraMode.HorizontalUp,
        // TM_PRED (idx 9)
        (byte)Vp9IntraMode.Dc129, (byte)Vp9IntraMode.Vertical, (byte)Vp9IntraMode.Horizontal, (byte)Vp9IntraMode.TrueMotion,
    };

    // Edge requirements per intra mode: bit0=needs_left, bit1=needs_top, bit2=needs_topleft,
    // bit3=needs_topright, bit4=invert_left
    // Indexed by FFmpeg vp9.h enum order: VERT=0, HOR=1, DC=2, DDL=3, DDR=4, VR=5, HD=6, VL=7, HU=8, TM=9
    private static ReadOnlySpan<byte> IntraModeEdges => new byte[]
    {
        0x02, // Vert: needs_top
        0x01, // Hor: needs_left
        0x03, // Dc: needs_top + needs_left
        0x0A, // DiagDownLeft: needs_top + needs_topright
        0x07, // DiagDownRight: needs_left + needs_top + needs_topleft
        0x07, // VertRight: needs_left + needs_top + needs_topleft
        0x07, // HorDown: needs_left + needs_top + needs_topleft
        0x0A, // VertLeft: needs_top + needs_topright
        0x11, // HorUp: needs_left + invert_left
        0x07, // Tm: needs_left + needs_top + needs_topleft
        0x01, // LeftDc: needs_left
        0x02, // TopDc: needs_top
        0x00, // Dc128: nothing
        0x00, // Dc127: nothing
        0x00, // Dc129: nothing
    };

    /// <summary>
    /// Prepare edge pixels and convert intra mode if neighbors aren't available.
    /// Port of FFmpeg check_intra_mode() — 8bpp only.
    /// </summary>
    /// <returns>Adjusted intra prediction mode.</returns>
    private int CheckIntraMode(
        int mode, Span<byte> aboveBuf, Span<byte> leftBuf,
        byte[] dstEdge, int strideEdge, byte[] dstInner, int strideInner,
        int dstEdgeOff, int dstInnerOff,
        int col, int x, int w4,
        int row, int y, int tx,
        int plane, int ssH, int ssV)
    {
        bool haveTop = row > 0 || y > 0;
        bool haveLeft = col > ctx.TileColStart || x > 0;
        bool haveRight = x < w4 - 1;

        // Convert mode based on available neighbors
        if (mode < 10)
            mode = IntraModeConv[mode * 4 + (haveLeft ? 2 : 0) + (haveTop ? 1 : 0)];

        int edges = IntraModeEdges[mode];
        bool needsTop = (edges & 0x02) != 0;
        bool needsLeft = (edges & 0x01) != 0;
        bool needsTopLeft = (edges & 0x04) != 0;
        bool needsTopRight = (edges & 0x08) != 0;
        bool invertLeft = (edges & 0x10) != 0;

        int txSz = 4 << tx; // prediction block size in pixels

        // Prepare top row (aboveBuf[1..txSz] = top pixels, aboveBuf[0] = top-left)
        if (needsTop)
        {
            int nPxNeed = txSz;
            int nPxHave = ((ctx.Cols - col) << (ssH == 0 ? 1 : 0)) * 4 - x * 4;
            int nPxNeedTr = 0;
            if (tx == 0 && needsTopRight && haveRight) // TX_4X4
                nPxNeedTr = 4;

            if (haveTop)
            {
                // Get top pixel source
                byte[] topSrc;
                int topOff;
                int topStride;

                if ((row & 7) == 0 && y == 0)
                {
                    // Use intra pred data (pre-loopfilter)
                    topSrc = plane == 0 ? ctx.IntraPredDataY! : (plane == 1 ? ctx.IntraPredDataU! : ctx.IntraPredDataV!);
                    topOff = (col * (8 >> ssH) + x * 4);
                    topStride = 0; // unused for row copy
                }
                else if (y == 0)
                {
                    topSrc = dstEdge;
                    topOff = dstEdgeOff - strideEdge;
                    topStride = strideEdge;
                }
                else
                {
                    topSrc = dstInner;
                    topOff = dstInnerOff - strideInner;
                    topStride = strideInner;
                }

                // FFmpeg checks pointer identity (top == topleft) to verify the
                // top-left pixel lives at top[-1]. They differ only when y > 0
                // and x == 0: top comes from the inner buffer while topleft
                // comes from the edge buffer.
                bool topEqualsTopleft = !(y > 0 && x == 0);

                // Can we use the top row directly without copying?
                bool canUseDirect = haveTop &&
                    (!needsTopLeft || (haveLeft && topEqualsTopleft)) &&
                    (tx != 0 || !needsTopRight || haveRight) &&
                    nPxNeed + nPxNeedTr <= nPxHave;

                if (!canUseDirect)
                {
                    // Copy available top pixels
                    int toCopy = Math.Min(nPxNeed, nPxHave);
                    if (toCopy > 0)
                        topSrc.AsSpan(topOff, toCopy).CopyTo(aboveBuf.Slice(1));

                    // Extend right edge
                    if (toCopy < nPxNeed)
                    {
                        byte edgeVal = toCopy > 0 ? aboveBuf[toCopy] : (byte)127;
                        aboveBuf.Slice(1 + toCopy, nPxNeed - toCopy).Fill(edgeVal);
                    }

                    // Top-left pixel
                    if (needsTopLeft)
                    {
                        if (haveLeft && haveTop)
                        {
                            // Get top-left from source
                            byte[] tlSrc;
                            int tlOff;
                            if ((row & 7) == 0 && y == 0)
                            {
                                tlSrc = plane == 0 ? ctx.IntraPredDataY! : (plane == 1 ? ctx.IntraPredDataU! : ctx.IntraPredDataV!);
                                tlOff = (col * (8 >> ssH) + x * 4) - 1;
                            }
                            else if (y == 0 || x == 0)
                            {
                                tlSrc = dstEdge;
                                tlOff = dstEdgeOff - strideEdge - 1;
                            }
                            else
                            {
                                tlSrc = dstInner;
                                tlOff = dstInnerOff - strideInner - 1;
                            }
                            aboveBuf[0] = tlOff >= 0 && tlOff < tlSrc.Length ? tlSrc[tlOff] : (byte)128;
                        }
                        else
                        {
                            aboveBuf[0] = (byte)(128 + (haveTop ? 1 : -1));
                        }
                    }

                    // Top-right extension for TX_4X4
                    if (tx == 0 && needsTopRight)
                    {
                        if (haveTop && haveRight && nPxNeed + nPxNeedTr <= nPxHave)
                        {
                            topSrc.AsSpan(topOff + 4, 4).CopyTo(aboveBuf.Slice(5));
                        }
                        else
                        {
                            byte ext = aboveBuf[4]; // last top pixel
                            aboveBuf.Slice(5, 4).Fill(ext);
                        }
                    }
                }
                else
                {
                    // Direct copy: top row
                    topSrc.AsSpan(topOff, nPxNeed + nPxNeedTr).CopyTo(aboveBuf.Slice(1));
                    // Top-left
                    if (needsTopLeft)
                    {
                        aboveBuf[0] = topOff > 0 ? topSrc[topOff - 1] : (byte)128;
                    }
                }
            }
            else
            {
                // No top available
                aboveBuf.Slice(1, nPxNeed + nPxNeedTr).Fill(127);
                if (needsTopLeft)
                    aboveBuf[0] = (byte)(128 + (haveTop ? 1 : -1));
            }
        }

        // Prepare left column
        if (needsLeft)
        {
            int nPxNeed = txSz;
            int nPxHave = ((ctx.Rows - row) << (ssV == 0 ? 1 : 0)) * 4 - y * 4;

            if (haveLeft)
            {
                byte[] src = x == 0 ? dstEdge : dstInner;
                int stride = x == 0 ? strideEdge : strideInner;
                int srcOff = x == 0 ? dstEdgeOff - 1 : dstInnerOff - 1;

                int toCopy = Math.Min(nPxNeed, nPxHave);

                if (invertLeft)
                {
                    // Bottom-to-top (normal order for left array)
                    for (int i = 0; i < toCopy; i++)
                        leftBuf[i] = src[srcOff + i * stride];
                }
                else
                {
                    // Top-to-bottom → store in reversed order (bottom=index 0)
                    for (int i = 0; i < toCopy; i++)
                        leftBuf[nPxNeed - 1 - i] = src[srcOff + i * stride];
                }

                // Extend if needed
                if (toCopy < nPxNeed)
                {
                    if (invertLeft)
                    {
                        byte ext = leftBuf[toCopy - 1];
                        leftBuf.Slice(toCopy, nPxNeed - toCopy).Fill(ext);
                    }
                    else
                    {
                        byte ext = leftBuf[nPxNeed - toCopy];
                        leftBuf.Slice(0, nPxNeed - toCopy).Fill(ext);
                    }
                }
            }
            else
            {
                leftBuf.Slice(0, nPxNeed).Fill(129);
            }
        }

        return mode;
    }

    /// <summary>
    /// Perform intra reconstruction for the current block (8bpp).
    /// Port of FFmpeg intra_recon() with bytesperpixel=1.
    /// </summary>
    private void IntraRecon(int yOff, int uvOff)
    {
        ref var b = ref ctx.CurrentBlock;
        ref readonly var h = ref ctx.Header;
        int row = ctx.CurrentRow;
        int col = ctx.CurrentCol;
        int bTx = (int)b.Tx;
        int step1d = 1 << bTx;
        int step = 1 << (bTx * 2);
        // Use bwh_tab[1] * 2 to match FFmpeg (sub-8x8 blocks clamp to 8x8 area)
        int w4 = GetBwh(1, (int)b.BlockSize, 0) * 2;
        int h4 = GetBwh(1, (int)b.BlockSize, 1) * 2;
        int endX = Math.Min(2 * (ctx.Cols - col), w4);
        int endY = Math.Min(2 * (ctx.Rows - row), h4);
        int tx = 4 * h.Lossless + bTx;

        ctx.DiagBlockCount++;

        var curFrame = ctx.Frames[Vp9DecoderContext.CurFrame];
        byte[] frameY = curFrame.LumaData!;
        int frameStrideY = curFrame.LumaStride;

        // Quick diagnostic: print block setup for first few blocks
        if (ctx.DiagCoefLog && ctx.DiagBlockCount <= 8)
        {
            Console.WriteLine($"IntraRecon blk#{ctx.DiagBlockCount}: row={row} col={col} bs={b.BlockSize} tx={b.Tx} uvtx={b.UvTx} skip={b.Skip} w4={w4} h4={h4} endX={endX} endY={endY}");
            Console.WriteLine($"  mode[0]={b.GetMode(0)} uvMode={b.UvMode}");
        }

        // Temp buffers for edge pixels
        Span<byte> aboveBuf = stackalloc byte[96]; // 32 for left pad + 64 for top + right
        Span<byte> leftBuf = stackalloc byte[64];
        int aboveOff = 32; // above[0] = top-left, above[1..] = top pixels

        int n = 0;
        for (int y = 0; y < endY; y += step1d)
        {
            for (int x = 0; x < endX; x += step1d, n += step)
            {
                int modeIdx = (int)b.BlockSize > (int)Vp9BlockSize.Bs8x8 && bTx == 0 ? y * 2 + x : 0;
                int mode = b.GetMode(modeIdx);
                int txType = Vp9Data.IntraTxfmType[mode];

                int pixY = (row * 8 + y * 4);
                int pixX = (col * 8 + x * 4);
                int dstOff = pixY * frameStrideY + pixX;

                // Get eob
                int eob = 0;
                if (b.Skip == 0 && ctx.EobBase != null)
                {
                    int eobIdx = n;
                    if (bTx >= 2) // TX_16X16 or TX_32X32: 16-bit eob (FFmpeg: step >= 4)
                        eob = ctx.EobBase.Length > eobIdx + 1
                            ? (ctx.EobBase[eobIdx] | (ctx.EobBase[eobIdx + 1] << 8))
                            : 0;
                    else
                        eob = eobIdx < ctx.EobBase.Length ? ctx.EobBase[eobIdx] : 0;
                }

                // Diagnostic: force eob=0 for bottom-edge blocks
                if (DiagForceZeroEobMinRow >= 0 && row >= DiagForceZeroEobMinRow)
                    eob = 0;

                // Prepare edge pixels and adjust mode
                mode = CheckIntraMode(mode, aboveBuf.Slice(aboveOff), leftBuf,
                                       frameY, frameStrideY, frameY, frameStrideY,
                                       dstOff, dstOff,
                                       col, x, w4, row, y, bTx, 0, 0, 0);

                // Trace reconstruction for diagnostic row
                if (DiagTraceReconRow >= 0 && row >= DiagTraceReconRow && row < DiagTraceReconRow + 8)
                {
                    int origMode2 = b.GetMode(modeIdx);
                    Console.WriteLine($"[RECON] row={row} col={col} x={x} y={y} bs={b.BlockSize} tx={b.Tx} mode={origMode2}→{mode} eob={eob} w4={w4} h4={h4} endX={endX} endY={endY} skip={b.Skip}");
                    Console.Write("  Above[0..15]: ");
                    for (int ai = 0; ai < Math.Min(16, 4 << bTx); ai++) Console.Write($"{aboveBuf[aboveOff + ai]} ");
                    Console.WriteLine();
                    Console.Write("  Left[0..15]: ");
                    for (int li = 0; li < Math.Min(16, 4 << bTx); li++) Console.Write($"{leftBuf[li]} ");
                    Console.WriteLine();
                    Console.Write("  TopLeft: "); Console.WriteLine(aboveBuf[aboveOff - 1]);
                    // Dump coefficients
                    if (eob > 0 && ctx.BlockBase != null)
                    {
                        int blkOff = n * 16;
                        int coeffCount = Math.Min(eob + 1, (4 << bTx) * (4 << bTx));
                        Console.Write("  Coefs: ");
                        for (int ci = 0; ci < Math.Min(coeffCount, 16); ci++)
                            Console.Write($"{ctx.BlockBase[blkOff + ci]} ");
                        Console.WriteLine();
                    }
                }

                // Call intra predictor
                int sz = 4 << bTx;
                Vp9IntraPrediction.Predict(mode, sz,
                                           frameY.AsSpan(dstOff), frameStrideY,
                                           leftBuf, aboveBuf.Slice(aboveOff));

                // Diagnostic: SB(8,8) first sub-block detail
                if (ctx.DiagCoefLog && row == 8 && col == 8 && x == 0 && y == 0)
                {
                    Console.WriteLine($"  SB88_RECON: origMode={b.GetMode(modeIdx)} adjMode={mode} tx={bTx} eob={eob} n={n}");
                    Console.Write("    Above(tl+8): ");
                    for (int ai = 0; ai < 9; ai++) Console.Write($"{aboveBuf[aboveOff + ai]} ");
                    Console.WriteLine();
                    Console.Write("    Left(8): ");
                    for (int li = 0; li < 8; li++) Console.Write($"{leftBuf[li]} ");
                    Console.WriteLine();
                    Console.Write("    Pred row0: ");
                    for (int pi = 0; pi < sz; pi++) Console.Write($"{frameY[dstOff + pi]} ");
                    Console.WriteLine();
                    Console.Write("    Pred row1: ");
                    for (int pi = 0; pi < sz; pi++) Console.Write($"{frameY[dstOff + frameStrideY + pi]} ");
                    Console.WriteLine();
                    if (eob > 0 && ctx.BlockBase != null)
                    {
                        Console.Write("    Coef[0..15]: ");
                        for (int ci = 0; ci < Math.Min(16, ctx.BlockBase.Length - n * 16); ci++)
                            Console.Write($"{ctx.BlockBase[n * 16 + ci]} ");
                        Console.WriteLine();
                    }
                }

                // Diagnostic: dump full block data for first two blocks
                if (ctx.DiagCoefLog && row == 0 && (col * 2 + x) < 4 && y == 0)
                {
                    Console.WriteLine($"  DIAG r={row},c={col} x={x},y={y} origMode={b.GetMode(modeIdx)} adjMode={mode} txType={txType} tx={bTx} eob={eob}");
                    Console.Write("    Above: ");
                    for (int ai = 0; ai < Math.Min(sz, 8); ai++) Console.Write($"{aboveBuf[aboveOff + ai]:X2} ");
                    Console.WriteLine();
                    Console.Write("    Left(raw): ");
                    for (int li = 0; li < Math.Min(sz, 8); li++) Console.Write($"{leftBuf[li]:X2} ");
                    Console.WriteLine();
                    Console.Write("    Pred row0: ");
                    for (int pi = 0; pi < sz; pi++) Console.Write($"{frameY[dstOff + pi]:X2} ");
                    Console.WriteLine();
                    Console.Write("    Pred rows: ");
                    for (int pr = 0; pr < sz; pr++) Console.Write($"r{pr}={frameY[dstOff + pr * frameStrideY]:X2} ");
                    Console.WriteLine();
                    if (eob > 0 && ctx.BlockBase != null)
                    {
                        int nCoeffs2 = sz * sz;
                        Console.WriteLine($"    Full coef block ({sz}x{sz}, eob={eob}):");
                        for (int cr = 0; cr < sz; cr++)
                        {
                            Console.Write($"      [{cr}]: ");
                            for (int cc = 0; cc < sz; cc++)
                                Console.Write($"{ctx.BlockBase[n * 16 + cr * sz + cc],6} ");
                            Console.WriteLine();
                        }
                    }
                }

                // Add residual (inverse transform)
                if (eob > 0 && ctx.BlockBase != null)
                {
                    int blockOff = n * 16;
                    var blockSpan = ctx.BlockBase.AsSpan(blockOff);
                    Vp9Transform.ItxfmAdd[tx][txType](frameY.AsSpan(dstOff), frameStrideY, blockSpan, eob);
                }

                // Trace output for diagnostic row
                if (DiagTraceReconRow >= 0 && row >= DiagTraceReconRow && row < DiagTraceReconRow + 8)
                {
                    int sz2 = 4 << bTx;
                    int showRows = Math.Min(sz2, 4); // show first 4 output rows
                    for (int dr = 0; dr < showRows; dr++)
                    {
                        Console.Write($"  Out r{dr}: ");
                        int showCols = Math.Min(sz2, 8);
                        for (int pi = 0; pi < showCols; pi++) Console.Write($"{frameY[dstOff + dr * frameStrideY + pi]} ");
                        Console.WriteLine();
                    }
                }

                // Diagnostic: SB(8,8) first sub-block final pixels
                if (ctx.DiagCoefLog && row == 8 && col == 8 && x == 0 && y == 0)
                {
                    Console.Write("    Final row0: ");
                    for (int pi = 0; pi < sz; pi++) Console.Write($"{frameY[dstOff + pi]} ");
                    Console.WriteLine();
                    Console.Write("    Final row1: ");
                    for (int pi = 0; pi < sz; pi++) Console.Write($"{frameY[dstOff + frameStrideY + pi]} ");
                    Console.WriteLine();
                }

                // Diagnostic: final pixel after IDCT
                if (ctx.DiagCoefLog && row == 0 && (col * 2 + x) < 4 && y == 0)
                {
                    for (int dr = 0; dr < sz; dr++)
                    {
                        Console.Write($"    Final r{dr}: ");
                        for (int pi = 0; pi < sz; pi++) Console.Write($"{frameY[dstOff + dr * frameStrideY + pi]:X2} ");
                        Console.WriteLine();
                    }
                }
            }
        }

        // UV intra prediction
        int uvTx = (int)b.UvTx;
        int uvStep1d = 1 << uvTx;
        int uvStep = 1 << (uvTx * 2);
        int uvTxLookup = h.Lossless * 4 + uvTx;

        int uvW4 = w4 >> ctx.SubsamplingX;
        int uvH4 = h4 >> ctx.SubsamplingY;
        int uvEndX = endX >> ctx.SubsamplingX;
        int uvEndY = endY >> ctx.SubsamplingY;

        for (int p = 0; p < 2; p++)
        {
            byte[] framePlane = p == 0 ? curFrame.ChromaUData! : curFrame.ChromaVData!;
            int frameStride = p == 0 ? curFrame.ChromaStride : curFrame.ChromaStride;
            short[]? uvBlock = p == 0 ? ctx.UvBlockBase0 : ctx.UvBlockBase1;
            byte[]? uvEob = p == 0 ? ctx.UvEobBase0 : ctx.UvEobBase1;

            n = 0;
            for (int y = 0; y < uvEndY; y += uvStep1d)
            {
                for (int x = 0; x < uvEndX; x += uvStep1d, n += uvStep)
                {
                    int mode = b.UvMode;

                    int pixY = ((row * 8 >> ctx.SubsamplingY) + y * 4);
                    int pixX = ((col * 8 >> ctx.SubsamplingX) + x * 4);
                    int dstOff = pixY * frameStride + pixX;

                    int eob = 0;
                    if (b.Skip == 0 && uvEob != null)
                    {
                        if (uvTx >= 2) // TX_16X16+: 16-bit eob
                            eob = n + 1 < uvEob.Length ? (uvEob[n] | (uvEob[n + 1] << 8)) : 0;
                        else
                            eob = n < uvEob.Length ? uvEob[n] : 0;
                    }

                    mode = CheckIntraMode(mode, aboveBuf.Slice(aboveOff), leftBuf,
                                           framePlane, frameStride, framePlane, frameStride,
                                           dstOff, dstOff,
                                           col, x, uvW4, row, y, uvTx,
                                           p + 1, ctx.SubsamplingX, ctx.SubsamplingY);

                    int sz = 4 << uvTx;
                    Vp9IntraPrediction.Predict(mode, sz,
                                               framePlane.AsSpan(dstOff), frameStride,
                                               leftBuf, aboveBuf.Slice(aboveOff));

                    // Diagnostic: dump V-plane block data
                    if (ctx.DiagCoefLog && p == 1)
                    {
                        Console.WriteLine($"  V-BLK r={row},c={col} x={x},y={y} uvMode={b.UvMode} adjMode={mode} uvTx={uvTx} eob={eob} n={n}");
                        Console.Write("    Above(tl+sz): ");
                        for (int ai = 0; ai <= sz; ai++) Console.Write($"{aboveBuf[aboveOff + ai]} ");
                        Console.WriteLine();
                        Console.Write("    Left(sz): ");
                        for (int li = 0; li < sz; li++) Console.Write($"{leftBuf[li]} ");
                        Console.WriteLine();
                        Console.WriteLine("    Pred (before IDCT):");
                        for (int pr = 0; pr < sz && pr < 5; pr++)
                        {
                            Console.Write($"      [{pr}]: ");
                            for (int pc = 0; pc < sz; pc++) Console.Write($"{framePlane[dstOff + pr * frameStride + pc],4} ");
                            Console.WriteLine();
                        }
                        if (eob > 0 && uvBlock != null)
                        {
                            int blockOff2 = n * 16;
                            Console.WriteLine($"    Coefs (eob={eob}):");
                            for (int cr = 0; cr < sz; cr++)
                            {
                                Console.Write($"      [{cr}]: ");
                                for (int cc = 0; cc < sz; cc++)
                                    Console.Write($"{uvBlock[blockOff2 + cr * sz + cc],6} ");
                                Console.WriteLine();
                            }
                        }
                    }

                    if (eob > 0 && uvBlock != null)
                    {
                        int blockOff = n * 16;
                        Vp9Transform.ItxfmAdd[uvTxLookup][0](framePlane.AsSpan(dstOff), frameStride,
                                                              uvBlock.AsSpan(blockOff), eob);
                    }

                    // Diagnostic: dump V-plane final pixels
                    if (ctx.DiagCoefLog && p == 1)
                    {
                        Console.WriteLine("    Final (after IDCT):");
                        for (int pr = 0; pr < sz && pr < 5; pr++)
                        {
                            Console.Write($"      [{pr}]: ");
                            for (int pc = 0; pc < sz; pc++) Console.Write($"{framePlane[dstOff + pr * frameStride + pc],4} ");
                            Console.WriteLine();
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Perform inter reconstruction for the current block (8bpp, unscaled).
    /// Port of FFmpeg inter_pred_8bpp() — simplified for Profile 0 (4:2:0).
    /// </summary>
    private void InterRecon()
    {
        ref var b = ref ctx.CurrentBlock;
        ref readonly var h = ref ctx.Header;
        int row = ctx.CurrentRow;
        int col = ctx.CurrentCol;

        var curFrame = ctx.Frames[Vp9DecoderContext.CurFrame];
        byte[] dstY = curFrame.LumaData!;
        byte[] dstU = curFrame.ChromaUData!;
        byte[] dstV = curFrame.ChromaVData!;
        int strideY = curFrame.LumaStride;
        int strideUv = curFrame.ChromaStride;

        // Check if scaled MC is needed (not yet implemented)
        int refIdx0 = b.Ref0;
        if (ctx.MvScale[refIdx0 * 2] != 0 || (b.Comp != 0 && ctx.MvScale[b.Ref1 * 2] != 0))
        {
            if (DiagTraceInterBlock)
                Console.WriteLine($"  WARNING: Scaled MC needed! ref0={refIdx0} scale=({ctx.MvScale[refIdx0 * 2]},{ctx.MvScale[refIdx0 * 2 + 1]})");
            // TODO: implement scaled MC path
        }

        // Get reference frame data
        var ref0Frame = ctx.Refs[h.GetRefIdx(refIdx0)];
        byte[] ref0Y = ref0Frame.LumaData!;
        byte[] ref0U = ref0Frame.ChromaUData!;
        byte[] ref0V = ref0Frame.ChromaVData!;
        int ref0StrideY = ref0Frame.LumaStride;
        int ref0StrideUv = ref0Frame.ChromaStride;
        int w1 = ref0Frame.Width;
        int h1 = ref0Frame.Height;

        int bw4 = BlockDimension4x4[(int)b.BlockSize * 2];
        int bh4 = BlockDimension4x4[(int)b.BlockSize * 2 + 1];
        int bw = bw4 * 4; // pixel width
        int bh = bh4 * 4; // pixel height

        int yBase = row * 8;
        int xBase = col * 8;
        int yOff = yBase * strideY + xBase;
        int uvBase_y = yBase >> ctx.SubsamplingY;
        int uvBase_x = xBase >> ctx.SubsamplingX;
        int uvOff = uvBase_y * strideUv + uvBase_x;

        int filter = (int)b.Filter;

        // Trace blocks near the first error region (R32 C160 in frame 1)
        bool traceThisBlock = DiagTraceInterBlock;
        if (traceThisBlock)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"InterRecon: R{row}(y{yBase}) C{col}(x{xBase}) bs={b.BlockSize} {bw}x{bh} ");
            sb.Append($"skip={b.Skip} comp={b.Comp} ref0={b.Ref0} ref1={b.Ref1} filter={b.Filter}");
            if ((int)b.BlockSize > (int)Vp9BlockSize.Bs8x8)
            {
                // Sub-8x8: show all sub-block MVs
                int nMvs = b.BlockSize == Vp9BlockSize.Bs8x4 ? 2 : (b.BlockSize == Vp9BlockSize.Bs4x8 ? 2 : 4);
                int[] mvIdx = b.BlockSize == Vp9BlockSize.Bs8x4 ? new[]{0,2} : (b.BlockSize == Vp9BlockSize.Bs4x8 ? new[]{0,1} : new[]{0,1,2,3});
                for (int mi = 0; mi < nMvs; mi++)
                {
                    int idx = mvIdx[mi];
                    var m0 = b.GetMv(idx, 0);
                    var m1 = b.Comp != 0 ? b.GetMv(idx, 1) : default;
                    sb.Append($" mv[{idx}]=({m0.X},{m0.Y})/({m1.X},{m1.Y})");
                }
            }
            else
            {
                var mv = b.GetMv(0, 0);
                var mv1 = b.Comp != 0 ? b.GetMv(0, 1) : default;
                sb.Append($" mv0=({mv.X},{mv.Y}) mv1=({mv1.X},{mv1.Y})");
            }
            Console.WriteLine(sb);

            // Dump ref0 plane when this is the first compound block
            if (b.Comp != 0 && yBase == 0 && xBase == 0)
            {
                Console.WriteLine($"  Ref0(altref) stride={ref0StrideY} w={w1} h={h1}:");
                for (int r = 0; r < Math.Min(8, h1); r++)
                {
                    var rsb = new System.Text.StringBuilder($"    R{r}: ");
                    for (int c = 0; c < Math.Min(16, w1); c++)
                        rsb.Append($"{ref0Y[r * ref0StrideY + c]:D3} ");
                    Console.WriteLine(rsb);
                }
            }
        }

        if (EnableDiagCoefLog && xBase == 0 && yBase == 0)
        {
            int refSlot = h.GetRefIdx(refIdx0);
            var sb = new System.Text.StringBuilder($"RefTrace: ref0={b.Ref0} slot={refSlot} stride={ref0StrideY} first16=");
            for (int c = 0; c < Math.Min(16, w1); c++)
                sb.Append($"{ref0Y[c]:X2} ");
            Console.WriteLine(sb);
        }

        if ((int)b.BlockSize <= (int)Vp9BlockSize.Bs8x8)
        {
            // >=8x8: single MV for luma, single/averaged for chroma
            var mv0 = b.GetMv(0, 0);

            // Luma MC
            McPutLuma(dstY, yOff, strideY, ref0Y, ref0StrideY, yBase, xBase, mv0, bw, bh, w1, h1, filter);

            // Chroma MC
            int uvW = bw >> ctx.SubsamplingX;
            int uvH = bh >> ctx.SubsamplingY;
            int chromaW = (w1 + ctx.SubsamplingX) >> ctx.SubsamplingX;
            int chromaH = (h1 + ctx.SubsamplingY) >> ctx.SubsamplingY;
            Vp9Mv chromaMv = ChromaMv(mv0, ctx.SubsamplingX, ctx.SubsamplingY);

            McPutChroma(dstU, uvOff, strideUv, ref0U, ref0StrideUv,
                        uvBase_y, uvBase_x, chromaMv, uvW, uvH, chromaW, chromaH, filter);
            McPutChroma(dstV, uvOff, strideUv, ref0V, ref0StrideUv,
                        uvBase_y, uvBase_x, chromaMv, uvW, uvH, chromaW, chromaH, filter);

            if (b.Comp != 0)
            {
                int refIdx1 = b.Ref1;
                var ref1Frame = ctx.Refs[h.GetRefIdx(refIdx1)];
                var mv1 = b.GetMv(0, 1);

                McAvgLuma(dstY, yOff, strideY, ref1Frame.LumaData!, ref1Frame.LumaStride,
                          yBase, xBase, mv1, bw, bh, ref1Frame.Width, ref1Frame.Height, filter);

                chromaMv = ChromaMv(mv1, ctx.SubsamplingX, ctx.SubsamplingY);
                int chromaW2 = (ref1Frame.Width + ctx.SubsamplingX) >> ctx.SubsamplingX;
                int chromaH2 = (ref1Frame.Height + ctx.SubsamplingY) >> ctx.SubsamplingY;

                McAvgChroma(dstU, uvOff, strideUv, ref1Frame.ChromaUData!, ref1Frame.ChromaStride,
                            uvBase_y, uvBase_x, chromaMv, uvW, uvH, chromaW2, chromaH2, filter);
                McAvgChroma(dstV, uvOff, strideUv, ref1Frame.ChromaVData!, ref1Frame.ChromaStride,
                            uvBase_y, uvBase_x, chromaMv, uvW, uvH, chromaW2, chromaH2, filter);
            }
        }
        else
        {
            // Sub-8x8 blocks (BS_8x4, BS_4x8, BS_4x4)
            InterReconSub8x8(ref b, in h, dstY, dstU, dstV, strideY, strideUv,
                             ref0Y, ref0U, ref0V, ref0StrideY, ref0StrideUv,
                             row, col, yBase, xBase, yOff, uvOff,
                             w1, h1, filter);
        }

        // Inter residual: add transform coefficients
        InterResidual();
    }

    /// <summary>
    /// Handle sub-8x8 inter prediction (BS_8x4, BS_4x8, BS_4x4).
    /// </summary>
    /// <remarks>
    /// The luma half is one call per sub-block. The chroma half is
    /// <see cref="InterPredSub8x8Chroma"/>, which has a case per subsampling: this used to
    /// assume 4:2:0 everywhere, so 4:2:2 and 4:4:4 decoded luma bit-exactly and chroma wrong
    /// from the first inter frame onwards.
    /// </remarks>
    private void InterReconSub8x8(
        ref Vp9Block b, ref readonly Vp9BitstreamHeader h,
        byte[] dstY, byte[] dstU, byte[] dstV,
        int strideY, int strideUv,
        byte[] ref0Y, byte[] ref0U, byte[] ref0V,
        int ref0StrideY, int ref0StrideUv,
        int row, int col, int yBase, int xBase,
        int yOff, int uvOff,
        int w1, int h1, int filter)
    {
        var bs = b.BlockSize;

        bool traceComp = DiagTraceInterBlock && b.Comp != 0;

        if (bs == Vp9BlockSize.Bs8x4)
        {
            var mv0 = b.GetMv(0, 0);
            var mv2 = b.GetMv(2, 0);
            McPutLuma(dstY, yOff, strideY, ref0Y, ref0StrideY, yBase, xBase, mv0, 8, 4, w1, h1, filter);
            McPutLuma(dstY, yOff + 4 * strideY, strideY, ref0Y, ref0StrideY, yBase + 4, xBase, mv2, 8, 4, w1, h1, filter);

            if (traceComp)
            {
                var sb = new System.Text.StringBuilder("  AfterPut R0: ");
                for (int c = 0; c < 8; c++) sb.Append($"{dstY[yOff + c]:D3} ");
                Console.WriteLine(sb);
            }

            InterPredSub8x8Chroma(ref b, 0, avg: false, dstU, dstV, uvOff, strideUv,
                                  ref0U, ref0V, ref0StrideUv, row, col, w1, h1, filter);
        }
        else if (bs == Vp9BlockSize.Bs4x8)
        {
            var mv0 = b.GetMv(0, 0);
            var mv1 = b.GetMv(1, 0);
            McPutLuma(dstY, yOff, strideY, ref0Y, ref0StrideY, yBase, xBase, mv0, 4, 8, w1, h1, filter);
            McPutLuma(dstY, yOff + 4, strideY, ref0Y, ref0StrideY, yBase, xBase + 4, mv1, 4, 8, w1, h1, filter);

            InterPredSub8x8Chroma(ref b, 0, avg: false, dstU, dstV, uvOff, strideUv,
                                  ref0U, ref0V, ref0StrideUv, row, col, w1, h1, filter);
        }
        else
        {
            // BS_4x4 (each sub-block has its own MV)
            for (int i = 0; i < 4; i++)
            {
                int subX = (i & 1) * 4;
                int subY = (i >> 1) * 4;
                var mv = b.GetMv(i, 0);
                McPutLuma(dstY, yOff + subY * strideY + subX, strideY,
                          ref0Y, ref0StrideY,
                          yBase + subY, xBase + subX, mv, 4, 4, w1, h1, filter);
            }

            InterPredSub8x8Chroma(ref b, 0, avg: false, dstU, dstV, uvOff, strideUv,
                                  ref0U, ref0V, ref0StrideUv, row, col, w1, h1, filter);
        }

        // Compound prediction for sub-8x8: average second reference into output
        if (b.Comp != 0)
        {
            int refIdx1 = b.Ref1;
            var ref1Frame = ctx.Refs[h.GetRefIdx(refIdx1)];
            byte[] ref1Y = ref1Frame.LumaData!;
            byte[] ref1U = ref1Frame.ChromaUData!;
            byte[] ref1V = ref1Frame.ChromaVData!;
            int ref1StrideY = ref1Frame.LumaStride;
            int ref1StrideUv = ref1Frame.ChromaStride;
            int w2 = ref1Frame.Width, h2 = ref1Frame.Height;

            if (bs == Vp9BlockSize.Bs8x4)
            {
                McAvgLuma(dstY, yOff, strideY, ref1Y, ref1StrideY,
                          yBase, xBase, b.GetMv(0, 1), 8, 4, w2, h2, filter);
                McAvgLuma(dstY, yOff + 4 * strideY, strideY, ref1Y, ref1StrideY,
                          yBase + 4, xBase, b.GetMv(2, 1), 8, 4, w2, h2, filter);

                if (traceComp)
                {
                    var sb = new System.Text.StringBuilder("  AfterAvg R0: ");
                    for (int c = 0; c < 8; c++) sb.Append($"{dstY[yOff + c]:D3} ");
                    Console.WriteLine(sb);
                }

                InterPredSub8x8Chroma(ref b, 1, avg: true, dstU, dstV, uvOff, strideUv,
                                      ref1U, ref1V, ref1StrideUv, row, col, w2, h2, filter);
            }
            else if (bs == Vp9BlockSize.Bs4x8)
            {
                McAvgLuma(dstY, yOff, strideY, ref1Y, ref1StrideY,
                          yBase, xBase, b.GetMv(0, 1), 4, 8, w2, h2, filter);
                McAvgLuma(dstY, yOff + 4, strideY, ref1Y, ref1StrideY,
                          yBase, xBase + 4, b.GetMv(1, 1), 4, 8, w2, h2, filter);

                InterPredSub8x8Chroma(ref b, 1, avg: true, dstU, dstV, uvOff, strideUv,
                                      ref1U, ref1V, ref1StrideUv, row, col, w2, h2, filter);
            }
            else
            {
                // BS_4x4: each sub-block gets its own MV from ref1
                for (int i = 0; i < 4; i++)
                {
                    int subX = (i & 1) * 4;
                    int subY = (i >> 1) * 4;
                    McAvgLuma(dstY, yOff + subY * strideY + subX, strideY,
                              ref1Y, ref1StrideY,
                              yBase + subY, xBase + subX, b.GetMv(i, 1), 4, 4, w2, h2, filter);
                }

                InterPredSub8x8Chroma(ref b, 1, avg: true, dstU, dstV, uvOff, strideUv,
                                      ref1U, ref1V, ref1StrideUv, row, col, w2, h2, filter);
            }
        }
    }

    /// <summary>Add inter-frame residual (inverse transforms for inter blocks).</summary>
    private void InterResidual()
    {
        ref var b = ref ctx.CurrentBlock;
        ref readonly var h = ref ctx.Header;

        if (b.Skip != 0 || DiagSkipInterResidual) return;

        var curFrame = ctx.Frames[Vp9DecoderContext.CurFrame];
        int bTx = (int)b.Tx;
        int step1d = 1 << bTx;
        int step = 1 << (bTx * 2);
        int w4 = GetBwh(1, (int)b.BlockSize, 0) * 2;
        int h4 = GetBwh(1, (int)b.BlockSize, 1) * 2;
        int row_ = ctx.CurrentRow, col_ = ctx.CurrentCol;
        int endX = Math.Min(2 * (ctx.Cols - col_), w4);
        int endY = Math.Min(2 * (ctx.Rows - row_), h4);
        int tx = 4 * h.Lossless + bTx;

        byte[] frameY = curFrame.LumaData!;
        int strideY = curFrame.LumaStride;

        // Y residual
        int n = 0;
        for (int y = 0; y < endY; y += step1d)
        {
            for (int x = 0; x < endX; x += step1d, n += step)
            {
                int eob = 0;
                if (ctx.EobBase != null)
                {
                    if (bTx >= 2)
                        eob = n + 1 < ctx.EobBase.Length ? (ctx.EobBase[n] | (ctx.EobBase[n + 1] << 8)) : 0;
                    else
                        eob = n < ctx.EobBase.Length ? ctx.EobBase[n] : 0;
                }

                if (eob > 0 && ctx.BlockBase != null)
                {
                    int pixY = row_ * 8 + y * 4;
                    int pixX = col_ * 8 + x * 4;
                    int dstOff = pixY * strideY + pixX;
                    int txType = 0; // DCT_DCT for inter

                    Vp9Transform.ItxfmAdd[tx][txType](frameY.AsSpan(dstOff), strideY,
                                                       ctx.BlockBase.AsSpan(n * 16), eob);
                }
            }
        }

        // UV residual
        int uvTx = (int)b.UvTx;
        int uvStep1d = 1 << uvTx;
        int uvStep = 1 << (uvTx * 2);
        int uvEndX = endX >> ctx.SubsamplingX;
        int uvEndY = endY >> ctx.SubsamplingY;
        int uvTxLookup = h.Lossless * 4 + uvTx;

        for (int p = 0; p < 2; p++)
        {
            byte[] framePlane = p == 0 ? curFrame.ChromaUData! : curFrame.ChromaVData!;
            int stride = p == 0 ? curFrame.ChromaStride : curFrame.ChromaStride;
            short[]? uvBlock = p == 0 ? ctx.UvBlockBase0 : ctx.UvBlockBase1;
            byte[]? uvEob = p == 0 ? ctx.UvEobBase0 : ctx.UvEobBase1;

            n = 0;
            for (int y = 0; y < uvEndY; y += uvStep1d)
            {
                for (int x = 0; x < uvEndX; x += uvStep1d, n += uvStep)
                {
                    int eob = 0;
                    if (uvEob != null)
                    {
                        if (uvTx >= 2)
                            eob = n + 1 < uvEob.Length ? (uvEob[n] | (uvEob[n + 1] << 8)) : 0;
                        else
                            eob = n < uvEob.Length ? uvEob[n] : 0;
                    }

                    if (eob > 0 && uvBlock != null)
                    {
                        int pixY = (row_ * 8 >> ctx.SubsamplingY) + y * 4;
                        int pixX = (col_ * 8 >> ctx.SubsamplingX) + x * 4;
                        int dstOff = pixY * stride + pixX;

                        Vp9Transform.ItxfmAdd[uvTxLookup][0](framePlane.AsSpan(dstOff), stride,
                                                               uvBlock.AsSpan(n * 16), eob);
                    }
                }
            }
        }
    }

    // MC helper methods for luma/chroma put/avg

    // Edge emu buffer: max block size (128+7) × (128+7) for VP9, stride = 160 matching FFmpeg
    [ThreadStatic] private static byte[]? t_edgeEmuBuf;
    private const int EdgeEmuStride = 160;

    private static byte[] GetEdgeEmuBuf()
    {
        return t_edgeEmuBuf ??= new byte[EdgeEmuStride * 135]; // 135 = max 128 + 7
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NeedsEdgeEmu(int sx, int sy, int mx, int my, int bw, int bh, int w, int h)
    {
        // FFmpeg uses !!my * 5 for vertical (one extra row beyond the 8-tap filter's 4 taps)
        // to match libvpx behavior. We must match for bit-exact output.
        bool hasMx = mx != 0;
        bool hasMy = my != 0;
        return sx < (hasMx ? 3 : 0) || sy < (hasMy ? 3 : 0) ||
               sx + bw + (hasMx ? 4 : 0) > w || sy + bh + (hasMy ? 5 : 0) > h;
    }

    private static void McPutLuma(byte[] dst, int dstOff, int dstStride,
                                   byte[] src, int srcStride,
                                   int y, int x, Vp9Mv mv,
                                   int bw, int bh, int w, int h, int filter)
    {
        int mx = mv.X & 7;
        int my = mv.Y & 7;
        int sx = x + (mv.X >> 3);
        int sy = y + (mv.Y >> 3);

        byte[] mcSrc = src;
        int mcOff, mcStride;

        if (NeedsEdgeEmu(sx, sy, mx, my, bw, bh, w, h))
        {
            int borderX = mx != 0 ? 3 : 0, borderY = my != 0 ? 3 : 0;
            mcSrc = GetEdgeEmuBuf();
            Vp9MotionCompensation.EmulateEdgeMc(
                mcSrc.AsSpan(), EdgeEmuStride, src.AsSpan(), srcStride,
                bw + (mx != 0 ? 7 : 0), bh + (my != 0 ? 7 : 0),
                sx - borderX, sy - borderY, w, h);
            mcOff = borderY * EdgeEmuStride + borderX;
            mcStride = EdgeEmuStride;

            if (Vp9Decoder.EnableDiagCoefLog && x == 0 && y == 0)
            {
                int emuW = bw + (mx != 0 ? 7 : 0);
                int emuH = bh + (my != 0 ? 7 : 0);
                Console.WriteLine($"McPutLuma emu: sx={sx} sy={sy} mx={mx} my={my} bw={bw} bh={bh} borderX={borderX} borderY={borderY} emuW={emuW} emuH={emuH}");
                for (int r = 0; r < emuH; r++)
                {
                    var sb = new System.Text.StringBuilder($"  emu R{r}: ");
                    for (int c = 0; c < Math.Min(emuW, 16); c++)
                        sb.Append($"{mcSrc[r * EdgeEmuStride + c]:X2} ");
                    Console.WriteLine(sb);
                }
                Console.WriteLine($"  mcOff={mcOff} mcStride={mcStride} mx<<1={mx << 1} my<<1={my << 1}");
            }
        }
        else
        {
            mcOff = sy * srcStride + sx;
            mcStride = srcStride;
        }

        if (mx == 0 && my == 0)
        {
            for (int r = 0; r < bh; r++)
                mcSrc.AsSpan(mcOff + r * mcStride, bw).CopyTo(dst.AsSpan(dstOff + r * dstStride, bw));
        }
        else
        {
            Vp9MotionCompensation.McPut(
                dst.AsSpan(dstOff), dstStride,
                mcSrc, mcOff, mcStride,
                bw, bh, mx << 1, my << 1, filter);
        }

        if (Vp9Decoder.EnableDiagCoefLog && x == 0 && y == 0)
        {
            var sb = new System.Text.StringBuilder($"McPutLuma out: ");
            for (int c = 0; c < Math.Min(bw, 8); c++)
                sb.Append($"{dst[dstOff + c]:D3} ");
            Console.WriteLine(sb);
        }
    }

    private static void McAvgLuma(byte[] dst, int dstOff, int dstStride,
                                   byte[] src, int srcStride,
                                   int y, int x, Vp9Mv mv,
                                   int bw, int bh, int w, int h, int filter)
    {
        int mx = mv.X & 7;
        int my = mv.Y & 7;
        int sx = x + (mv.X >> 3);
        int sy = y + (mv.Y >> 3);

        byte[] mcSrc = src;
        int mcOff, mcStride;

        if (NeedsEdgeEmu(sx, sy, mx, my, bw, bh, w, h))
        {
            int borderX = mx != 0 ? 3 : 0, borderY = my != 0 ? 3 : 0;
            mcSrc = GetEdgeEmuBuf();
            Vp9MotionCompensation.EmulateEdgeMc(
                mcSrc.AsSpan(), EdgeEmuStride, src.AsSpan(), srcStride,
                bw + (mx != 0 ? 7 : 0), bh + (my != 0 ? 7 : 0),
                sx - borderX, sy - borderY, w, h);
            mcOff = borderY * EdgeEmuStride + borderX;
            mcStride = EdgeEmuStride;
        }
        else
        {
            mcOff = sy * srcStride + sx;
            mcStride = srcStride;
        }

        Vp9MotionCompensation.McAvg(
            dst.AsSpan(dstOff), dstStride,
            mcSrc, mcOff, mcStride,
            bw, bh, mx << 1, my << 1, filter);
    }

    /// <summary>
    /// <c>vp9_mc_template.c</c>'s chroma half of <c>inter_pred</c> for the three sub-8x8 block
    /// sizes, for ONE reference. <paramref name="avg"/> is the C's <c>mc_chroma_dir</c> last
    /// argument: 0 writes the prediction, 1 averages into what the other reference wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every one of these block sizes has a case per subsampling, and they do not differ only
    /// in a scale factor - they differ in HOW MANY chroma blocks there are and which luma
    /// motion vectors each one averages. A 4:2:0 8x4 block is one 4x4 chroma block with the
    /// mean of the two luma vectors; a 4:4:4 8x4 block is two 8x4 chroma blocks with a vector
    /// each. This decoder had only the 4:2:0 case, with the subsampling written in as the
    /// literal 1, so profile 1 and profile 3 streams reconstructed luma bit-exactly and chroma
    /// wrong from the first inter frame on - which is what
    /// <c>Vp9ProfileConformanceTests</c> measures.
    /// </para>
    /// <para>
    /// Two of the arms reproduce libvpx bugs that the C carries deliberately, and both are
    /// about the wrong block index for a bottom chroma block
    /// (<c>code.google.com/p/webm/issues/detail?id=993</c>). They are marked where they occur:
    /// a decoder that "fixed" them would disagree with every VP9 encoder in existence.
    /// </para>
    /// </remarks>
    private void InterPredSub8x8Chroma(
        ref Vp9Block b, int refList, bool avg,
        byte[] dstU, byte[] dstV, int uvOff, int strideUv,
        byte[] srcU, byte[] srcV, int srcStrideUv,
        int row, int col, int refW, int refH, int filter)
    {
        int ssH = ctx.SubsamplingX;
        int ssV = ctx.SubsamplingY;

        // w1 = (w1 + s->ss_h) >> s->ss_h, h1 the same with ss_v. The C writes it out per
        // arm, halving only on the subsampled axis; this is the same arithmetic for all
        // four, because the shift by zero is the identity.
        int w = (refW + ssH) >> ssH;
        int h = (refH + ssV) >> ssV;

        var bs = b.BlockSize;

        if (bs == Vp9BlockSize.Bs8x4)
        {
            Vp9Mv mv0 = b.GetMv(0, refList);
            Vp9Mv mv2 = b.GetMv(2, refList);

            if (ssV != 0)
            {
                Put(row << 2, col << (3 - ssH), 0, RoundedDivMvx2(mv0, mv2), 8 >> ssH, 4);
            }
            else
            {
                Put(row << 3, col << (3 - ssH), 0, mv0, 8 >> ssH, 4);
                // BUG for 4:2:2 bs=8x4, libvpx uses the wrong block index to get the motion
                // vector for the bottom 4x4 block.
                Vp9Mv bottom = ssH == 0 ? mv2 : RoundedDivMvx2(mv0, mv2);
                Put((row << 3) + 4, col << (3 - ssH), 4 * strideUv, bottom, 8 >> ssH, 4);
            }
        }
        else if (bs == Vp9BlockSize.Bs4x8)
        {
            Vp9Mv mv0 = b.GetMv(0, refList);
            Vp9Mv mv1 = b.GetMv(1, refList);

            if (ssH != 0)
            {
                Put(row << (3 - ssV), col << 2, 0, RoundedDivMvx2(mv0, mv1), 4, 8 >> ssV);
            }
            else
            {
                Put(row << (3 - ssV), col << 3, 0, mv0, 4, 8 >> ssV);
                Put(row << (3 - ssV), (col << 3) + 4, 4, mv1, 4, 8 >> ssV);
            }
        }
        else
        {
            Vp9Mv mv0 = b.GetMv(0, refList);
            Vp9Mv mv1 = b.GetMv(1, refList);
            Vp9Mv mv2 = b.GetMv(2, refList);
            Vp9Mv mv3 = b.GetMv(3, refList);

            if (ssV != 0)
            {
                if (ssH != 0)
                {
                    Put(row << 2, col << 2, 0, RoundedDivMvx4(mv0, mv1, mv2, mv3), 4, 4);
                }
                else
                {
                    // UNREACHED by anything this decoder accepts: ss_v with no ss_h is
                    // 4:4:0, and 4:4:0 is refused for want of a pixel format. Changing
                    // either pairing here leaves vp9-profiles green, which was checked
                    // rather than assumed. It is a translation of the C and nothing more.
                    Put(row << 2, col << 3, 0, RoundedDivMvx2(mv0, mv2), 4, 4);
                    Put(row << 2, (col << 3) + 4, 4, RoundedDivMvx2(mv1, mv3), 4, 4);
                }
            }
            else
            {
                if (ssH != 0)
                {
                    Put(row << 3, col << 2, 0, RoundedDivMvx2(mv0, mv1), 4, 4);
                    // BUG libvpx uses the wrong block index for the 4:2:2 bs=4x4 bottom
                    // block: mv[1] and mv[2], where mv[2] and mv[3] is what the geometry
                    // says.
                    Put((row << 3) + 4, col << 2, 4 * strideUv,
                        RoundedDivMvx2(mv1, mv2), 4, 4);
                }
                else
                {
                    Put(row << 3, col << 3, 0, mv0, 4, 4);
                    Put(row << 3, (col << 3) + 4, 4, mv1, 4, 4);
                    Put((row << 3) + 4, col << 3, 4 * strideUv, mv2, 4, 4);
                    Put((row << 3) + 4, (col << 3) + 4, 4 * strideUv + 4, mv3, 4, 4);
                }
            }
        }

        void Put(int y, int x, int dstDelta, Vp9Mv mv, int bw, int bh)
        {
            Vp9Mv uvMv = ChromaMv(mv, ssH, ssV);
            if (avg)
            {
                McAvgChroma(dstU, uvOff + dstDelta, strideUv, srcU, srcStrideUv,
                            y, x, uvMv, bw, bh, w, h, filter);
                McAvgChroma(dstV, uvOff + dstDelta, strideUv, srcV, srcStrideUv,
                            y, x, uvMv, bw, bh, w, h, filter);
            }
            else
            {
                McPutChroma(dstU, uvOff + dstDelta, strideUv, srcU, srcStrideUv,
                            y, x, uvMv, bw, bh, w, h, filter);
                McPutChroma(dstV, uvOff + dstDelta, strideUv, srcV, srcStrideUv,
                            y, x, uvMv, bw, bh, w, h, filter);
            }
        }
    }

    private static void McPutChroma(byte[] dst, int dstOff, int dstStride,
                                     byte[] src, int srcStride,
                                     int y, int x, Vp9Mv mv,
                                     int bw, int bh, int w, int h, int filter)
    {
        int mx = mv.X & 15;
        int my = mv.Y & 15;
        int sx = x + (mv.X >> 4);
        int sy = y + (mv.Y >> 4);

        byte[] mcSrc = src;
        int mcOff, mcStride;

        if (NeedsEdgeEmu(sx, sy, mx, my, bw, bh, w, h))
        {
            int borderX = mx != 0 ? 3 : 0, borderY = my != 0 ? 3 : 0;
            mcSrc = GetEdgeEmuBuf();
            Vp9MotionCompensation.EmulateEdgeMc(
                mcSrc.AsSpan(), EdgeEmuStride, src.AsSpan(), srcStride,
                bw + (mx != 0 ? 7 : 0), bh + (my != 0 ? 7 : 0),
                sx - borderX, sy - borderY, w, h);
            mcOff = borderY * EdgeEmuStride + borderX;
            mcStride = EdgeEmuStride;
        }
        else
        {
            mcOff = sy * srcStride + sx;
            mcStride = srcStride;
        }

        if (mx == 0 && my == 0)
        {
            for (int r = 0; r < bh; r++)
                mcSrc.AsSpan(mcOff + r * mcStride, bw).CopyTo(dst.AsSpan(dstOff + r * dstStride, bw));
        }
        else
        {
            Vp9MotionCompensation.McPut(
                dst.AsSpan(dstOff), dstStride,
                mcSrc, mcOff, mcStride,
                bw, bh, mx, my, filter);
        }
    }

    private static void McAvgChroma(byte[] dst, int dstOff, int dstStride,
                                     byte[] src, int srcStride,
                                     int y, int x, Vp9Mv mv,
                                     int bw, int bh, int w, int h, int filter)
    {
        int mx = mv.X & 15;
        int my = mv.Y & 15;
        int sx = x + (mv.X >> 4);
        int sy = y + (mv.Y >> 4);

        byte[] mcSrc = src;
        int mcOff, mcStride;

        if (NeedsEdgeEmu(sx, sy, mx, my, bw, bh, w, h))
        {
            int borderX = mx != 0 ? 3 : 0, borderY = my != 0 ? 3 : 0;
            mcSrc = GetEdgeEmuBuf();
            Vp9MotionCompensation.EmulateEdgeMc(
                mcSrc.AsSpan(), EdgeEmuStride, src.AsSpan(), srcStride,
                bw + (mx != 0 ? 7 : 0), bh + (my != 0 ? 7 : 0),
                sx - borderX, sy - borderY, w, h);
            mcOff = borderY * EdgeEmuStride + borderX;
            mcStride = EdgeEmuStride;
        }
        else
        {
            mcOff = sy * srcStride + sx;
            mcStride = srcStride;
        }

        Vp9MotionCompensation.McAvg(
            dst.AsSpan(dstOff), dstStride,
            mcSrc, mcOff, mcStride,
            bw, bh, mx, my, filter);
    }

    /// <summary>Convert luma MV to chroma MV for the given subsampling.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vp9Mv ChromaMv(Vp9Mv mv, int ssH, int ssV)
    {
        return new Vp9Mv
        {
            X = (short)(mv.X * (ssH == 0 ? 2 : 1)),
            Y = (short)(mv.Y * (ssV == 0 ? 2 : 1)),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vp9Mv RoundedDivMvx2(Vp9Mv a, Vp9Mv b)
    {
        return new Vp9Mv
        {
            X = (short)((a.X + b.X + ((a.X + b.X) >> 15) + 1) >> 1),
            Y = (short)((a.Y + b.Y + ((a.Y + b.Y) >> 15) + 1) >> 1),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vp9Mv RoundedDivMvx4(Vp9Mv a, Vp9Mv b, Vp9Mv c, Vp9Mv d)
    {
        int sx = a.X + b.X + c.X + d.X;
        int sy = a.Y + b.Y + c.Y + d.Y;
        return new Vp9Mv
        {
            X = (short)((sx + (sx >> 15) + 2) >> 2),
            Y = (short)((sy + (sy >> 15) + 2) >> 2),
        };
    }
}


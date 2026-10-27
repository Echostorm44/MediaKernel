// H.264/AVC motion vector prediction, reference list construction, and direct mode
// Partial class: MV prediction (median), MV cache management, reference picture lists
// Reference: FFmpeg h264_mvpred.h (pred_motion, fetch_diagonal_mv, pred_16x8_motion, pred_8x16_motion)

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Codecs.H264;

public sealed partial class H264Decoder
{
    private const int PartNotAvailable = -2;
    
    /// <summary>
    /// Computes median of three short values (mid_pred in FFmpeg).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short MedianShort(short a, short b, short c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return b;
    }
    
    /// <summary>
    /// Fetches the diagonal (top-right C) motion vector for MV prediction.
    /// If C is unavailable, falls back to D (top-left). Returns the ref_idx at C/D position.
    /// For MBAFF cross-type boundaries, fetches the diagonal from the frame-level deblocking
    /// MV arrays with field↔frame scaling (FFmpeg h264_mvpred.h fetch_diagonal_mv lines 177-206).
    /// </summary>
    private int FetchDiagonalMv<TShape>(int[] refCache, short[] mvCacheX, short[] mvCacheY,
        int index8, int partWidth, int list, out short mvCX, out short mvCY)
        where TShape : struct, IPictureShape
    {
        int cPos = index8 - 8 + partWidth;
        int topRightRef = (cPos >= 0 && cPos < 40) ? refCache[cPos] : PartNotAvailable;
        
        bool diagTrace = DiagTraceMbAddr >= 0 && diagTraceMbActive;
        if (diagTrace) Console.WriteLine($"[DIAG-FD] idx8={index8} pw={partWidth} cPos={cPos} topRightRef={topRightRef} isMbaff={TShape.Mbaff}");
        
        // MBAFF cross-type diagonal path: when the standard C position is PART_NOT_AVAILABLE
        // at inner-boundary positions (scan8[4] or scan8[12]) in the bottom half of the cache,
        // and the left neighbor exists, FFmpeg fetches the diagonal from frame-level motion_val
        // with cross-type MV/ref scaling. This handles frame↔field neighbor boundaries.
        // Conditions: MBAFF frame, C unavailable, row >= 2 of cache, column = right of 8x8 block,
        // left border (scan8[0]-1) has a valid reference.
        if (TShape.Mbaff && topRightRef == PartNotAvailable
            && index8 >= Scan8[0] + 8 && (index8 & 7) == 4
            && refCache[Scan8[0] - 1] != PartNotAvailable
            && mbaffLeftXYTop >= 0)
        {
            bool currentIsField = mbaffCurrentFieldFlag;
            bool leftIsField = mbFieldDecodingFlag![mbaffLeftXYTop];
            
            if (diagTrace) Console.WriteLine($"[DIAG-FD] MBAFF-diag: curField={currentIsField} leftField={leftIsField} leftXYTop={mbaffLeftXYTop} refAtLeft={refCache[Scan8[0] - 1]}");
            
            // Zero the temp diagonal position (scan8[0] - 2 = position 10)
            mvCacheX[Scan8[0] - 2] = 0;
            mvCacheY[Scan8[0] - 2] = 0;
            
            if (!currentIsField && leftIsField)
            {
                // Frame current, field left: diagonal from left pair's bottom MB
                int targetMb = mbaffLeftXYTop + currentDecodeMbWidth;
                int y4 = (currentDecodeMbY & 1) * 2 + (index8 >> 5);
                if (diagTrace) Console.WriteLine($"[DIAG-FD] frame-cur/field-left: targetMb={targetMb} y4={y4}");
                int result = FetchMbaffDiagFromDeblockData(
                    mvCacheX, mvCacheY, list, targetMb, y4, true);
                if (diagTrace) Console.WriteLine($"[DIAG-FD] result={result} mv=({mvCacheX[Scan8[0] - 2]},{mvCacheY[Scan8[0] - 2]})");
                if (result != int.MinValue)
                {
                    mvCX = mvCacheX[Scan8[0] - 2];
                    mvCY = mvCacheY[Scan8[0] - 2];
                    return result;
                }
            }
            else if (currentIsField && !leftIsField)
            {
                // Field current, frame left: diagonal from left pair's top or bottom MB
                int targetMb = (index8 >= 36) ? mbaffLeftXYBot : mbaffLeftXYTop;
                int y4 = (index8 >> 2) & 3;
                int result = FetchMbaffDiagFromDeblockData(
                    mvCacheX, mvCacheY, list, targetMb, y4, false);
                if (result != int.MinValue)
                {
                    mvCX = mvCacheX[Scan8[0] - 2];
                    mvCY = mvCacheY[Scan8[0] - 2];
                    return result;
                }
            }
            // Same-type: no cross-type scaling, fall through to standard D path
        }
        
        if (topRightRef != PartNotAvailable)
        {
            mvCX = mvCacheX[cPos];
            mvCY = mvCacheY[cPos];
            return topRightRef;
        }
        
        // Fallback to D (top-left diagonal)
        int dPos = index8 - 8 - 1;
        if (diagTrace) Console.WriteLine($"[DIAG-FD] D-fallback: dPos={dPos} ref={((dPos >= 0 && dPos < 40) ? refCache[dPos] : -99)} mv=({((dPos >= 0 && dPos < 40) ? mvCacheX[dPos] : 0)},{((dPos >= 0 && dPos < 40) ? mvCacheY[dPos] : 0)})");
        if (dPos >= 0 && dPos < 40)
        {
            mvCX = mvCacheX[dPos];
            mvCY = mvCacheY[dPos];
            return refCache[dPos];
        }
        
        mvCX = 0;
        mvCY = 0;
        return PartNotAvailable;
    }
    
    /// <summary>
    /// Fetches MBAFF diagonal MV from frame-level deblocking MV arrays.
    /// Reads position (col=3, row=y4) of the target MB, applies cross-type scaling.
    /// Stores result at cache position scan8[0]-2 (position 10).
    /// Returns scaled ref index, or int.MinValue if target doesn't use this list.
    /// </summary>
    private int FetchMbaffDiagFromDeblockData(
        short[] mvCacheX, short[] mvCacheY,
        int list, int targetMb, int y4, bool frameCurrentFieldLeft)
    {
        // Check target MB has valid data for this list
        int sub8x8 = (y4 >> 1) * 2 + 1; // right column (x4=3) → sub8x8 index
        sbyte refAtBlock;
        if (list == 0)
            refAtBlock = mbRefIdxL0![targetMb * 4 + sub8x8];
        else
            refAtBlock = mbRefIdxL1![targetMb * 4 + sub8x8];
        
        bool diagFetch = DiagTraceMbAddr >= 0 && diagTraceMbActive;
        if (diagFetch)
        {
            int b = targetMb * 4;
            bool isIntra = mbIsIntra != null && targetMb < mbIsIntra.Length && mbIsIntra[targetMb];
            Console.WriteLine($"[DIAG-FETCH] targetMb={targetMb} y4={y4} sub8x8={sub8x8} refAtBlock={refAtBlock} allRefs=[{mbRefIdxL0![b]},{mbRefIdxL0[b+1]},{mbRefIdxL0[b+2]},{mbRefIdxL0[b+3]}] isIntra={isIntra}");
        }
        
        if (refAtBlock < 0)
            return -1; // LIST_NOT_USED
        
        // Read MV at (col=3, row=y4) from frame-level deblock arrays
        int mvIdx = targetMb * 16 + y4 * 4 + 3;
        short mvX, mvY;
        if (list == 0)
        {
            mvX = deblockMvL0![2 * mvIdx];
            mvY = deblockMvL0[2 * mvIdx + 1];
        }
        else
        {
            mvX = deblockMvL1![2 * mvIdx];
            mvY = deblockMvL1[2 * mvIdx + 1];
        }
        
        // Store at temp position (scan8[0] - 2 = position 10) with cross-type scaling
        mvCacheX[Scan8[0] - 2] = mvX;
        if (frameCurrentFieldLeft)
        {
            // Frame current, field left: double MV.Y (field→frame), halve ref
            mvCacheY[Scan8[0] - 2] = (short)(mvY * 2);
            return refAtBlock >> 1;
        }
        else
        {
            // Field current, frame left: halve MV.Y (frame→field), double ref
            mvCacheY[Scan8[0] - 2] = (short)(mvY / 2);
            return refAtBlock * 2;
        }
    }
    
    /// <summary>
    /// Full MV prediction per H.264 spec 8.4.1.3, matching FFmpeg's pred_motion exactly.
    /// Uses ref_idx matching to select between median prediction, single-neighbor, or fallback.
    /// </summary>
    private void PredMotion<TShape>(int n, int partWidth, int list, int refIdx,
        out short mvpX, out short mvpY)
        where TShape : struct, IPictureShape
    {
        int index8 = Scan8[n];
        int[] refCache = list == 0 ? refCacheL0 : refCacheL1;
        short[] cacheX = list == 0 ? mvCacheL0X : mvCacheL1X;
        short[] cacheY = list == 0 ? mvCacheL0Y : mvCacheL1Y;
        
        int topRef = refCache[index8 - 8];
        int leftRef = refCache[index8 - 1];
        short mvAX = cacheX[index8 - 1];
        short mvAY = cacheY[index8 - 1];
        short mvBX = cacheX[index8 - 8];
        short mvBY = cacheY[index8 - 8];

        int diagonalRef = FetchDiagonalMv<TShape>(refCache, cacheX, cacheY,
            index8, partWidth, list, out short mvCX, out short mvCY);
        
        bool diagTrace = DiagTraceMbAddr >= 0 && diagTraceMbActive;
        if (diagTrace)
            Console.WriteLine($"[DIAG-PM] n={n} idx8={index8} pw={partWidth} ref={refIdx} A=({mvAX},{mvAY},r={leftRef}) B=({mvBX},{mvBY},r={topRef}) C=({mvCX},{mvCY},r={diagonalRef})");
        
        int matchCount = (diagonalRef == refIdx ? 1 : 0)
                       + (topRef == refIdx ? 1 : 0)
                       + (leftRef == refIdx ? 1 : 0);
        
        if (matchCount > 1)
        {
            // Most common: median of all three
            mvpX = MedianShort(mvAX, mvBX, mvCX);
            mvpY = MedianShort(mvAY, mvBY, mvCY);
        }
        else if (matchCount == 1)
        {
            // Exactly one neighbor matches ref — use that neighbor directly
            if (leftRef == refIdx) { mvpX = mvAX; mvpY = mvAY; }
            else if (topRef == refIdx) { mvpX = mvBX; mvpY = mvBY; }
            else { mvpX = mvCX; mvpY = mvCY; }
        }
        else
        {
            // No matches. If only A is available (B and C both unavailable), use A.
            if (topRef == PartNotAvailable && diagonalRef == PartNotAvailable
                && leftRef != PartNotAvailable)
            {
                mvpX = mvAX;
                mvpY = mvAY;
            }
            else
            {
                mvpX = MedianShort(mvAX, mvBX, mvCX);
                mvpY = MedianShort(mvAY, mvBY, mvCY);
            }
        }
    }
    
    /// <summary>
    /// Special 16x8 MV prediction per FFmpeg's pred_16x8_motion.
    /// Top partition: use B directly if B's ref matches; else fall through to generic pred_motion.
    /// Bottom partition: use A directly if A's ref matches; else fall through to generic pred_motion.
    /// </summary>
    private void Pred16x8Motion<TShape>(int n, int list, int refIdx, out short mvpX, out short mvpY)
        where TShape : struct, IPictureShape
    {
        int[] refCache = list == 0 ? refCacheL0 : refCacheL1;
        short[] cacheX = list == 0 ? mvCacheL0X : mvCacheL1X;
        short[] cacheY = list == 0 ? mvCacheL0Y : mvCacheL1Y;
        
        if (n == 0)
        {
            int topRef = refCache[Scan8[0] - 8];
            if (topRef == refIdx)
            {
                mvpX = cacheX[Scan8[0] - 8];
                mvpY = cacheY[Scan8[0] - 8];
                return;
            }
        }
        else
        {
            int leftRef = refCache[Scan8[8] - 1];
            if (leftRef == refIdx)
            {
                mvpX = cacheX[Scan8[8] - 1];
                mvpY = cacheY[Scan8[8] - 1];
                return;
            }
        }
        
        // Rare fallback: full pred_motion with part_width=4
        PredMotion<TShape>(n, 4, list, refIdx, out mvpX, out mvpY);
    }
    
    /// <summary>
    /// Special 8x16 MV prediction per FFmpeg's pred_8x16_motion.
    /// Left partition: use A directly if A's ref matches; else fall through to generic pred_motion.
    /// Right partition: use C directly if C's ref matches; else fall through to generic pred_motion.
    /// </summary>
    private void Pred8x16Motion<TShape>(int n, int list, int refIdx, out short mvpX, out short mvpY)
        where TShape : struct, IPictureShape
    {
        int[] refCache = list == 0 ? refCacheL0 : refCacheL1;
        short[] cacheX = list == 0 ? mvCacheL0X : mvCacheL1X;
        short[] cacheY = list == 0 ? mvCacheL0Y : mvCacheL1Y;
        
        if (n == 0)
        {
            int leftRef = refCache[Scan8[0] - 1];
            if (leftRef == refIdx)
            {
                mvpX = cacheX[Scan8[0] - 1];
                mvpY = cacheY[Scan8[0] - 1];
                return;
            }
        }
        else
        {
            // Right partition: use C (diagonal) if it matches
            int diagonalRef = FetchDiagonalMv<TShape>(refCache, cacheX, cacheY,
                Scan8[4], 2, list, out short mvCX, out short mvCY);
            if (diagonalRef == refIdx)
            {
                mvpX = mvCX;
                mvpY = mvCY;
                return;
            }
        }
        
        // Rare fallback: full pred_motion with part_width=2
        PredMotion<TShape>(n, 2, list, refIdx, out mvpX, out mvpY);
    }
    
    /// <summary>
    /// Fills a rectangle in the MV cache with the given motion vector.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillMvCache(short[] cacheX, short[] cacheY, int startPos, int width, int height, short mvX, short mvY)
    {
        // Almost every caller fills a whole macroblock or one 8x8 partition, so a row is four
        // or two consecutive shorts - eight or four bytes, one store. The scalar loop wrote them
        // one at a time with a bounds check each.
        if (width == 4)
        {
            ulong wideX = BroadcastShort(mvX), wideY = BroadcastShort(mvY);
            for (int row = 0; row < height; row++)
            {
                int at = startPos + row * 8;
                WriteRow(cacheX, at, wideX);
                WriteRow(cacheY, at, wideY);
            }

            return;
        }

        if (width == 2)
        {
            uint pairX = (uint)(ushort)mvX * 0x00010001u, pairY = (uint)(ushort)mvY * 0x00010001u;
            for (int row = 0; row < height; row++)
            {
                int at = startPos + row * 8;
                WritePair(cacheX, at, pairX);
                WritePair(cacheY, at, pairY);
            }

            return;
        }

        for (int row = 0; row < height; row++)
        {
            int rowStart = startPos + row * 8;
            for (int col = 0; col < width; col++)
            {
                cacheX[rowStart + col] = mvX;
                cacheY[rowStart + col] = mvY;
            }
        }
    }

    /// <summary>
    /// Prepares the MV cache for a macroblock by loading neighbor MVs from boundary storage.
    /// Loads top row, left column, top-right corner (C), and top-left corner (D fallback).
    /// Must be called alongside PrepareMvdCache at the start of each MB's inter prediction decode.
    /// </summary>
    private void PrepareMvCache<TShape>(int mbAddr, int mbX, int mbY, int mbWidth)
        where TShape : struct, IPictureShape
    {
        // Store current MB position for MBAFF diagonal fetch (FetchDiagonalMv)
        currentDecodeMbY = mbY;
        currentDecodeMbWidth = mbWidth;
        
        // Initialize all border positions to 0 (unavailable neighbors have MV=0)
        mvCacheL0X[3] = mvCacheL0X[4] = mvCacheL0X[5] = mvCacheL0X[6] = mvCacheL0X[7] = mvCacheL0X[8] = 0;
        mvCacheL0Y[3] = mvCacheL0Y[4] = mvCacheL0Y[5] = mvCacheL0Y[6] = mvCacheL0Y[7] = mvCacheL0Y[8] = 0;
        mvCacheL1X[3] = mvCacheL1X[4] = mvCacheL1X[5] = mvCacheL1X[6] = mvCacheL1X[7] = mvCacheL1X[8] = 0;
        mvCacheL1Y[3] = mvCacheL1Y[4] = mvCacheL1Y[5] = mvCacheL1Y[6] = mvCacheL1Y[7] = mvCacheL1Y[8] = 0;
        mvCacheL0X[11] = mvCacheL0X[19] = mvCacheL0X[27] = mvCacheL0X[35] = 0;
        mvCacheL0Y[11] = mvCacheL0Y[19] = mvCacheL0Y[27] = mvCacheL0Y[35] = 0;
        mvCacheL1X[11] = mvCacheL1X[19] = mvCacheL1X[27] = mvCacheL1X[35] = 0;
        mvCacheL1Y[11] = mvCacheL1Y[19] = mvCacheL1Y[27] = mvCacheL1Y[35] = 0;

        if (TShape.Mbaff)
        {
            PrepareMvCacheMbaff(mbAddr, mbX, mbY, mbWidth);
            return;
        }

        // --- Non-MBAFF path (unchanged) ---
        if (mbY > 0)
        {
            int topAddr = mbAddr - mbWidth;
            if (mbSliceIndex![topAddr] == currentSliceIdx)
            {
                int topBase = topAddr * 4;
                WriteRow(mvCacheL0X, 4, ReadRow(mbMvL0XBottom!, topBase));
                WriteRow(mvCacheL0Y, 4, ReadRow(mbMvL0YBottom!, topBase));
                WriteRow(mvCacheL1X, 4, ReadRow(mbMvL1XBottom!, topBase));
                WriteRow(mvCacheL1Y, 4, ReadRow(mbMvL1YBottom!, topBase));
            }
            
            if (mbX < mbWidth - 1)
            {
                int topRightAddr = topAddr + 1;
                if (mbSliceIndex[topRightAddr] == currentSliceIdx)
                {
                    int trBase = topRightAddr * 4;
                    mvCacheL0X[8] = mbMvL0XBottom![trBase]; mvCacheL0Y[8] = mbMvL0YBottom![trBase];
                    mvCacheL1X[8] = mbMvL1XBottom![trBase]; mvCacheL1Y[8] = mbMvL1YBottom![trBase];
                }
            }
            
            if (mbX > 0)
            {
                int topLeftAddr = topAddr - 1;
                if (mbSliceIndex[topLeftAddr] == currentSliceIdx)
                {
                    int tlBase = topLeftAddr * 4;
                    mvCacheL0X[3] = mbMvL0XBottom![tlBase + 3]; mvCacheL0Y[3] = mbMvL0YBottom![tlBase + 3];
                    mvCacheL1X[3] = mbMvL1XBottom![tlBase + 3]; mvCacheL1Y[3] = mbMvL1YBottom![tlBase + 3];
                }
            }
        }
        
        if (mbX > 0)
        {
            int leftAddr = mbAddr - 1;
            if (mbSliceIndex![leftAddr] == currentSliceIdx)
            {
                int leftBase = leftAddr * 4;
                ScatterColumn(mvCacheL0X, ReadRow(mbMvL0XRight!, leftBase));
                ScatterColumn(mvCacheL0Y, ReadRow(mbMvL0YRight!, leftBase));
                ScatterColumn(mvCacheL1X, ReadRow(mbMvL1XRight!, leftBase));
                ScatterColumn(mvCacheL1Y, ReadRow(mbMvL1YRight!, leftBase));
            }
        }
    }

    /// <summary>
    /// MBAFF-aware MV cache preparation. Uses pre-computed neighbor addresses from
    /// ComputeMbaffNeighbors() and applies left_block remapping + MV Y scaling.
    /// </summary>
    private void PrepareMvCacheMbaff(int mbAddr, int mbX, int mbY, int mbWidth)
    {
        bool currentIsField = mbFieldDecodingFlag![mbAddr];
        int totalMbs = mbFieldDecodingFlag.Length;

        // Top border
        if (mbaffTopXY >= 0 && mbaffTopXY < totalMbs
            && mbSliceIndex![mbaffTopXY] == currentSliceIdx)
        {
            int topBase = mbaffTopXY * 4;
            mvCacheL0X[4] = mbMvL0XBottom![topBase];     mvCacheL0X[5] = mbMvL0XBottom[topBase + 1];
            mvCacheL0X[6] = mbMvL0XBottom[topBase + 2];  mvCacheL0X[7] = mbMvL0XBottom[topBase + 3];
            mvCacheL0Y[4] = mbMvL0YBottom![topBase];     mvCacheL0Y[5] = mbMvL0YBottom[topBase + 1];
            mvCacheL0Y[6] = mbMvL0YBottom[topBase + 2];  mvCacheL0Y[7] = mbMvL0YBottom[topBase + 3];
            mvCacheL1X[4] = mbMvL1XBottom![topBase];     mvCacheL1X[5] = mbMvL1XBottom[topBase + 1];
            mvCacheL1X[6] = mbMvL1XBottom[topBase + 2];  mvCacheL1X[7] = mbMvL1XBottom[topBase + 3];
            mvCacheL1Y[4] = mbMvL1YBottom![topBase];     mvCacheL1Y[5] = mbMvL1YBottom[topBase + 1];
            mvCacheL1Y[6] = mbMvL1YBottom[topBase + 2];  mvCacheL1Y[7] = mbMvL1YBottom[topBase + 3];

            // MV Y scaling for field/frame mismatch
            bool topIsField = mbFieldDecodingFlag[mbaffTopXY];
            if (currentIsField && !topIsField)
            {
                // Current=field, neighbor=frame: halve MV.y
                for (int i = 4; i <= 7; i++)
                {
                    mvCacheL0Y[i] /= 2;
                    mvCacheL1Y[i] /= 2;
                }
            }
            else if (!currentIsField && topIsField)
            {
                // Current=frame, neighbor=field: double MV.y
                for (int i = 4; i <= 7; i++)
                {
                    mvCacheL0Y[i] *= 2;
                    mvCacheL1Y[i] *= 2;
                }
            }
        }

        // Top-right corner
        if (mbX < mbWidth - 1 && mbaffTopRightXY >= 0 && mbaffTopRightXY < totalMbs
            && mbSliceIndex![mbaffTopRightXY] == currentSliceIdx)
        {
            int trBase = mbaffTopRightXY * 4;
            mvCacheL0X[8] = mbMvL0XBottom![trBase]; mvCacheL0Y[8] = mbMvL0YBottom![trBase];
            mvCacheL1X[8] = mbMvL1XBottom![trBase]; mvCacheL1Y[8] = mbMvL1YBottom![trBase];

            bool trIsField = mbFieldDecodingFlag[mbaffTopRightXY];
            if (currentIsField && !trIsField)
            {
                mvCacheL0Y[8] /= 2; mvCacheL1Y[8] /= 2;
            }
            else if (!currentIsField && trIsField)
            {
                mvCacheL0Y[8] *= 2; mvCacheL1Y[8] *= 2;
            }
        }

        // Top-left corner
        if (mbX > 0 && mbaffTopLeftXY >= 0 && mbaffTopLeftXY < totalMbs
            && mbSliceIndex![mbaffTopLeftXY] == currentSliceIdx)
        {
            int tlBase = mbaffTopLeftXY * 4;
            // topleft_partition: -1 = bottom-right (row 3, col 3), 0 = top-right (row 1, col 3)
            if (mbaffTopleftPartition == 0)
            {
                mvCacheL0X[3] = mbMvL0XRight![tlBase + 1]; mvCacheL0Y[3] = mbMvL0YRight![tlBase + 1];
                mvCacheL1X[3] = mbMvL1XRight![tlBase + 1]; mvCacheL1Y[3] = mbMvL1YRight![tlBase + 1];
            }
            else
            {
                mvCacheL0X[3] = mbMvL0XBottom![tlBase + 3]; mvCacheL0Y[3] = mbMvL0YBottom![tlBase + 3];
                mvCacheL1X[3] = mbMvL1XBottom![tlBase + 3]; mvCacheL1Y[3] = mbMvL1YBottom![tlBase + 3];
            }

            bool tlIsField = mbFieldDecodingFlag[mbaffTopLeftXY];
            if (currentIsField && !tlIsField)
            {
                mvCacheL0Y[3] /= 2; mvCacheL1Y[3] /= 2;
            }
            else if (!currentIsField && tlIsField)
            {
                mvCacheL0Y[3] *= 2; mvCacheL1Y[3] *= 2;
            }
        }

        // Left border with MBAFF left_block remapping
        int[] lb = LeftBlockOptions[mbaffLeftBlockIdx];

        // LTOP: fills cache positions 11, 19
        if (mbX > 0 && mbaffLeftXYTop >= 0 && mbaffLeftXYTop < totalMbs
            && mbSliceIndex![mbaffLeftXYTop] == currentSliceIdx)
        {
            int ltBase = mbaffLeftXYTop * 4;
            mvCacheL0X[11] = mbMvL0XRight![ltBase + lb[0]]; mvCacheL0Y[11] = mbMvL0YRight![ltBase + lb[0]];
            mvCacheL0X[19] = mbMvL0XRight[ltBase + lb[1]];  mvCacheL0Y[19] = mbMvL0YRight[ltBase + lb[1]];
            mvCacheL1X[11] = mbMvL1XRight![ltBase + lb[0]]; mvCacheL1Y[11] = mbMvL1YRight![ltBase + lb[0]];
            mvCacheL1X[19] = mbMvL1XRight[ltBase + lb[1]];  mvCacheL1Y[19] = mbMvL1YRight[ltBase + lb[1]];

            bool ltIsField = mbFieldDecodingFlag[mbaffLeftXYTop];
            if (currentIsField && !ltIsField)
            {
                mvCacheL0Y[11] /= 2; mvCacheL0Y[19] /= 2;
                mvCacheL1Y[11] /= 2; mvCacheL1Y[19] /= 2;
            }
            else if (!currentIsField && ltIsField)
            {
                mvCacheL0Y[11] *= 2; mvCacheL0Y[19] *= 2;
                mvCacheL1Y[11] *= 2; mvCacheL1Y[19] *= 2;
            }
        }

        // LBOT: fills cache positions 27, 35
        if (mbX > 0 && mbaffLeftXYBot >= 0 && mbaffLeftXYBot < totalMbs
            && mbSliceIndex![mbaffLeftXYBot] == currentSliceIdx)
        {
            int lbBase = mbaffLeftXYBot * 4;
            mvCacheL0X[27] = mbMvL0XRight![lbBase + lb[2]]; mvCacheL0Y[27] = mbMvL0YRight![lbBase + lb[2]];
            mvCacheL0X[35] = mbMvL0XRight[lbBase + lb[3]];  mvCacheL0Y[35] = mbMvL0YRight[lbBase + lb[3]];
            mvCacheL1X[27] = mbMvL1XRight![lbBase + lb[2]]; mvCacheL1Y[27] = mbMvL1YRight![lbBase + lb[2]];
            mvCacheL1X[35] = mbMvL1XRight[lbBase + lb[3]];  mvCacheL1Y[35] = mbMvL1YRight[lbBase + lb[3]];

            bool lbIsField = mbFieldDecodingFlag[mbaffLeftXYBot];
            if (currentIsField && !lbIsField)
            {
                mvCacheL0Y[27] /= 2; mvCacheL0Y[35] /= 2;
                mvCacheL1Y[27] /= 2; mvCacheL1Y[35] /= 2;
            }
            else if (!currentIsField && lbIsField)
            {
                mvCacheL0Y[27] *= 2; mvCacheL0Y[35] *= 2;
                mvCacheL1Y[27] *= 2; mvCacheL1Y[35] *= 2;
            }
        }
        
        // Diagnostic: dump loaded MV cache border values for a target MB
        if (DiagnosticOutput != null && DiagnosticDumpAllMbsForPoc == currentPoc && mbAddr == DiagDumpDetailMbAddr)
        {
            DiagnosticOutput.WriteLine($"  MVCACHE-MBAFF mb={mbAddr} ({mbX},{mbY}) curField={currentIsField} leftBlockIdx={mbaffLeftBlockIdx} lb=[{LeftBlockOptions[mbaffLeftBlockIdx][0]},{LeftBlockOptions[mbaffLeftBlockIdx][1]},{LeftBlockOptions[mbaffLeftBlockIdx][2]},{LeftBlockOptions[mbaffLeftBlockIdx][3]}]");
            DiagnosticOutput.WriteLine($"    nbr-addr: topXY={mbaffTopXY} leftTopXY={mbaffLeftXYTop} leftBotXY={mbaffLeftXYBot} tlXY={mbaffTopLeftXY} trXY={mbaffTopRightXY} tlPart={mbaffTopleftPartition}");
            DiagnosticOutput.WriteLine($"    topleft(3): mv=({mvCacheL0X[3]},{mvCacheL0Y[3]}) ref={refCacheL0[3]}");
            DiagnosticOutput.WriteLine($"    top(4-7):   mv=({mvCacheL0X[4]},{mvCacheL0Y[4]}) ({mvCacheL0X[5]},{mvCacheL0Y[5]}) ({mvCacheL0X[6]},{mvCacheL0Y[6]}) ({mvCacheL0X[7]},{mvCacheL0Y[7]}) ref={refCacheL0[4]},{refCacheL0[5]},{refCacheL0[6]},{refCacheL0[7]}");
            DiagnosticOutput.WriteLine($"    topright(8): mv=({mvCacheL0X[8]},{mvCacheL0Y[8]}) ref={refCacheL0[8]}");
            DiagnosticOutput.WriteLine($"    left(11,19,27,35): mv=({mvCacheL0X[11]},{mvCacheL0Y[11]}) ({mvCacheL0X[19]},{mvCacheL0Y[19]}) ({mvCacheL0X[27]},{mvCacheL0Y[27]}) ({mvCacheL0X[35]},{mvCacheL0Y[35]}) ref={refCacheL0[11]},{refCacheL0[19]},{refCacheL0[27]},{refCacheL0[35]}");
            // Also dump raw stored values before scaling
            if (mbaffLeftXYTop >= 0 && mbaffLeftXYTop < totalMbs)
            {
                int ltBase2 = mbaffLeftXYTop * 4;
                int[] lbo = LeftBlockOptions[mbaffLeftBlockIdx];
                DiagnosticOutput.WriteLine($"    RAW-LEFT-TOP(addr={mbaffLeftXYTop}): stored_right[lb0={lbo[0]}]=({mbMvL0XRight![ltBase2 + lbo[0]]},{mbMvL0YRight![ltBase2 + lbo[0]]}) stored_right[lb1={lbo[1]}]=({mbMvL0XRight[ltBase2 + lbo[1]]},{mbMvL0YRight[ltBase2 + lbo[1]]}) fieldFlag={mbFieldDecodingFlag[mbaffLeftXYTop]}");
            }
            if (mbaffLeftXYBot >= 0 && mbaffLeftXYBot < totalMbs)
            {
                int lbBase2 = mbaffLeftXYBot * 4;
                int[] lbo2 = LeftBlockOptions[mbaffLeftBlockIdx];
                DiagnosticOutput.WriteLine($"    RAW-LEFT-BOT(addr={mbaffLeftXYBot}): stored_right[lb2={lbo2[2]}]=({mbMvL0XRight![lbBase2 + lbo2[2]]},{mbMvL0YRight![lbBase2 + lbo2[2]]}) stored_right[lb3={lbo2[3]}]=({mbMvL0XRight[lbBase2 + lbo2[3]]},{mbMvL0YRight[lbBase2 + lbo2[3]]}) fieldFlag={mbFieldDecodingFlag[mbaffLeftXYBot]}");
            }
        }
    }
    
    /// <summary>
    /// Stores MV boundary values from the current MB's MV cache for use by future neighbor MBs.
    /// Must be called after inter prediction decode, alongside StoreMvdBoundaries.
    /// </summary>
    /// <summary>The right column of the cache - rows 15, 23, 31, 39 - packed into one word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong GatherColumn(short[] cache)
        => (ulong)(ushort)cache[15]
         | ((ulong)(ushort)cache[23] << 16)
         | ((ulong)(ushort)cache[31] << 32)
         | ((ulong)(ushort)cache[39] << 48);

    /// <summary>Unpacks one word into the cache's left column - rows 11, 19, 27, 35.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScatterColumn(short[] cache, ulong packed)
    {
        cache[11] = (short)packed;
        cache[19] = (short)(packed >> 16);
        cache[27] = (short)(packed >> 32);
        cache[35] = (short)(packed >> 48);
    }

    private void StoreMvBoundaries(int mbAddr)
    {
        int b = mbAddr * 4;

        // The macroblock's bottom row is four consecutive cache entries going to four
        // consecutive frame entries, so it moves as one word. Its right column is four cache
        // rows apart, so only the store widens there.
        WriteRow(mbMvL0XBottom!, b, ReadRow(mvCacheL0X, 36));
        WriteRow(mbMvL0YBottom!, b, ReadRow(mvCacheL0Y, 36));
        WriteRow(mbMvL1XBottom!, b, ReadRow(mvCacheL1X, 36));
        WriteRow(mbMvL1YBottom!, b, ReadRow(mvCacheL1Y, 36));

        WriteRow(mbMvL0XRight!, b, GatherColumn(mvCacheL0X));
        WriteRow(mbMvL0YRight!, b, GatherColumn(mvCacheL0Y));
        WriteRow(mbMvL1XRight!, b, GatherColumn(mvCacheL1X));
        WriteRow(mbMvL1YRight!, b, GatherColumn(mvCacheL1Y));
    }
    
    /// <summary>
    /// Clears MV boundaries for intra/skipped MBs (MV=0 for all positions).
    /// </summary>
    private void ClearMvBoundaries(int mbAddr)
    {
        int b = mbAddr * 4;
        mbMvL0XRight![b] = mbMvL0XRight[b + 1] = mbMvL0XRight[b + 2] = mbMvL0XRight[b + 3] = 0;
        mbMvL0YRight![b] = mbMvL0YRight[b + 1] = mbMvL0YRight[b + 2] = mbMvL0YRight[b + 3] = 0;
        mbMvL0XBottom![b] = mbMvL0XBottom[b + 1] = mbMvL0XBottom[b + 2] = mbMvL0XBottom[b + 3] = 0;
        mbMvL0YBottom![b] = mbMvL0YBottom[b + 1] = mbMvL0YBottom[b + 2] = mbMvL0YBottom[b + 3] = 0;
        mbMvL1XRight![b] = mbMvL1XRight[b + 1] = mbMvL1XRight[b + 2] = mbMvL1XRight[b + 3] = 0;
        mbMvL1YRight![b] = mbMvL1YRight[b + 1] = mbMvL1YRight[b + 2] = mbMvL1YRight[b + 3] = 0;
        mbMvL1XBottom![b] = mbMvL1XBottom[b + 1] = mbMvL1XBottom[b + 2] = mbMvL1XBottom[b + 3] = 0;
        mbMvL1YBottom![b] = mbMvL1YBottom[b + 1] = mbMvL1YBottom[b + 2] = mbMvL1YBottom[b + 3] = 0;
    }
    
    /// <summary>
    /// Copies per-4x4-block MVs and ref indices from the working cache to frame-level arrays
    /// for use by the deblocking filter's boundary strength calculation and co-located data.
    /// MVs come from the MV cache (per-4x4), refs come from mbRefIdxL0/L1 (per-8x8, expanded).
    /// Using mbRefIdxL0/L1 instead of refCacheL0/L1 ensures correctness even when the ref cache
    /// has been clamped (B_8x8 Direct sub-MBs) or otherwise modified for CABAC context purposes.
    /// </summary>
    /// <summary>Four consecutive motion vector components as one 64-bit word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadRow(short[] source, int index)
        => System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(
            ref System.Runtime.CompilerServices.Unsafe.As<short, byte>(
                ref source[index]));

    /// <summary>Writes four consecutive motion vector components as one 64-bit word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteRow(short[] destination, int index, ulong value)
        => System.Runtime.CompilerServices.Unsafe.WriteUnaligned(
            ref System.Runtime.CompilerServices.Unsafe.As<short, byte>(
                ref destination[index]), value);

    /// <summary>Writes two consecutive motion vector components as one 32-bit word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePair(short[] destination, int index, uint value)
        => System.Runtime.CompilerServices.Unsafe.WriteUnaligned(
            ref System.Runtime.CompilerServices.Unsafe.As<short, byte>(
                ref destination[index]), value);

    /// <summary>One short repeated into all four lanes of a 64-bit word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong BroadcastShort(short value)
        => 0x0001000100010001UL * (ushort)value;

    /// <summary>
    /// Writes a macroblock's sixteen per-4x4 reference indices from its four per-partition
    /// values, in the pattern p0 p0 p1 p1 / p0 p0 p1 p1 / p2 p2 p3 p3 / p2 p2 p3 p3.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteRefPattern(sbyte[] destination, int at, sbyte p0, sbyte p1, sbyte p2, sbyte p3)
    {
        uint top = (uint)(byte)p0 | ((uint)(byte)p0 << 8) | ((uint)(byte)p1 << 16) | ((uint)(byte)p1 << 24);
        uint bottom = (uint)(byte)p2 | ((uint)(byte)p2 << 8) | ((uint)(byte)p3 << 16) | ((uint)(byte)p3 << 24);

        ref byte to = ref Unsafe.As<sbyte, byte>(
            ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(destination), at));
        Unsafe.WriteUnaligned(ref to, top | ((ulong)top << 32));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, 8), bottom | ((ulong)bottom << 32));
    }

    /// <summary>The four per-partition reference identities, written as one vector.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteRefPicPartitions(int[] destination, int at, ReadOnlySpan<int> byPart)
        => Vector128.Create(byPart[0], byPart[1], byPart[2], byPart[3])
            .StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(destination), (nuint)at);

    /// <summary>
    /// Interleaves a row of four x components with its four y components and stores the eight
    /// resulting shorts in one go.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteInterleavedRow(short[] destination, int block, ulong xs, ulong ys)
    {
        Vector128<short> interleaved = Sse2.UnpackLow(
            Vector128.CreateScalar(xs).AsInt16(), Vector128.CreateScalar(ys).AsInt16());
        interleaved.StoreUnsafe(
            ref MemoryMarshal.GetArrayDataReference(destination), (nuint)(2 * block));
    }

    private void StoreBlockDataForDeblocking<TShape>(int mbAddr)
        where TShape : struct, IPictureShape
    {
        int frameBase = mbAddr * 16;
        int refBase = mbAddr * 4;

        // Whether the macroblock carries one motion throughout is settled here, where every
        // value is already in a register on its way to memory. Deblocking needs the answer for
        // each macroblock and for both its neighbours; deriving it there means re-reading all
        // ninety-six entries, which is why the first attempt at using it lost 3.2%.
        bool uniform = true;
        short firstL0X = mvCacheL0X[12], firstL0Y = mvCacheL0Y[12];
        short firstL1X = mvCacheL1X[12], firstL1Y = mvCacheL1Y[12];
        sbyte firstRefL0 = mbRefIdxL0![refBase], firstRefL1 = mbRefIdxL1![refBase];

        // Reference identity depends on the reference index, which is held per 8x8 partition,
        // and on flags that belong to the whole macroblock - so there are four distinct answers
        // per list, not sixteen. It was being derived once per 4x4 block, which is thirty-two
        // calls a macroblock to compute eight values, each of them a branch on the picture
        // structure and a lookup in the reference list.
        bool mbIsFieldInMbaff = TShape.Mbaff && mbFieldDecodingFlag != null && mbFieldDecodingFlag[mbAddr];

        Scratch.Of4<int> refPicByPartL0Storage = default;
        Span<int> refPicByPartL0 = refPicByPartL0Storage;
        Scratch.Of4<int> refPicByPartL1Storage = default;
        Span<int> refPicByPartL1 = refPicByPartL1Storage;
        for (int part = 0; part < 4; part++)
        {
            refPicByPartL0[part] = EncodeDeblockRefIdentity(
                mbRefIdxL0[refBase + part], refPicListL0Poc, refPicListL0IsTopField, mbIsFieldInMbaff);
            refPicByPartL1[part] = EncodeDeblockRefIdentity(
                mbRefIdxL1![refBase + part], refPicListL1Poc, refPicListL1IsTopField, mbIsFieldInMbaff);
        }
        // A row of four motion vectors is four consecutive shorts in the cache and four
        // consecutive shorts in the frame array, so each one moves as a single eight-byte read
        // and write instead of four of each. Sixteen such copies replace sixty-four scalar
        // stores per macroblock.
        //
        // The uniformity test comes along for free. Four identical shorts packed into a 64-bit
        // word are that short multiplied by 0x0001000100010001, so comparing the word against
        // that constant asks whether all four blocks of the row agree with the first - four
        // comparisons per array rather than sixteen.
        ulong wantL0X = BroadcastShort(firstL0X), wantL0Y = BroadcastShort(firstL0Y);
        ulong wantL1X = BroadcastShort(firstL1X), wantL1Y = BroadcastShort(firstL1Y);

        for (int y4 = 0; y4 < 4; y4++)
        {
            int cacheRow = 12 + y4 * 8;
            int frameRow = frameBase + y4 * 4;
            int sub8x8Y = y4 >> 1;

            ulong rowL0X = ReadRow(mvCacheL0X, cacheRow);
            ulong rowL0Y = ReadRow(mvCacheL0Y, cacheRow);
            ulong rowL1X = ReadRow(mvCacheL1X, cacheRow);
            ulong rowL1Y = ReadRow(mvCacheL1Y, cacheRow);

            uniform &= rowL0X == wantL0X && rowL0Y == wantL0Y
                    && rowL1X == wantL1X && rowL1Y == wantL1Y;

            // A row is four vectors: x0 y0 x1 y1 x2 y2 x3 y3. The x and y words are already in
            // registers, so one unpack interleaves them and one sixteen-byte store lands them.
            WriteInterleavedRow(deblockMvL0!, frameRow, rowL0X, rowL0Y);
            WriteInterleavedRow(deblockMvL1!, frameRow, rowL1X, rowL1Y);

        }

        // The reference arrays hold one value per 8x8 partition, so a macroblock's sixteen
        // entries are four values in a fixed 2x2-of-2x2 pattern:
        //
        //     p0 p0 p1 p1 | p0 p0 p1 p1 | p2 p2 p3 p3 | p2 p2 p3 p3
        //
        // and all sixteen are contiguous in the frame arrays. Writing them a block at a time
        // replaces sixty-four scalar stores with ten, and the uniformity test stops being
        // thirty-two comparisons: four values agreeing is all it can mean.
        sbyte r0 = mbRefIdxL0![refBase], r1 = mbRefIdxL0[refBase + 1];
        sbyte r2 = mbRefIdxL0[refBase + 2], r3 = mbRefIdxL0[refBase + 3];
        sbyte s0 = mbRefIdxL1![refBase], s1 = mbRefIdxL1[refBase + 1];
        sbyte s2 = mbRefIdxL1[refBase + 2], s3 = mbRefIdxL1[refBase + 3];

        uniform &= r0 == firstRefL0 && r1 == firstRefL0 && r2 == firstRefL0 && r3 == firstRefL0
                && s0 == firstRefL1 && s1 == firstRefL1 && s2 == firstRefL1 && s3 == firstRefL1;

        WriteRefPattern(deblockRefL0!, frameBase, r0, r1, r2, r3);
        WriteRefPattern(deblockRefL1!, frameBase, s0, s1, s2, s3);
        // Four values, written where they are read from - one vector each instead of four.
        WriteRefPicPartitions(deblockRefPicL0!, refBase, refPicByPartL0);
        WriteRefPicPartitions(deblockRefPicL1!, refBase, refPicByPartL1);

        deblockMotionUniform![mbAddr] = uniform;
    }
    
    /// <summary>
    /// Encodes a reference identity for deblocking bS comparison.
    /// For field pictures and MBAFF field MBs, encodes field parity (top=1, bottom=2)
    /// so that top and bottom fields of the same frame are treated as different references.
    /// For MBAFF field MBs, converts field-level refIdx (0..2N-1) to frame-level (refIdx>>1)
    /// before looking up the POC, since the ref list arrays are frame-indexed.
    /// Matches FFmpeg's ref2frm = 4*pic_id + (reference &amp; 3) encoding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int EncodeDeblockRefIdentity(sbyte refIdx, int[] pocArray, bool[] isTopFieldArray, bool mbIsFieldInMbaff = false)
    {
        if (refIdx < 0) return -1;
        
        if (mbIsFieldInMbaff)
        {
            // MBAFF field MB: refIdx is field-level (even=top, odd=bottom of frame refIdx>>1).
            // Convert to frame-level for POC lookup, encode field parity.
            int frameRefIdx = refIdx >> 1;
            if (frameRefIdx >= pocArray.Length) return -1;
            int poc = pocArray[frameRefIdx];
            bool isTop = (refIdx & 1) == 0;
            return (poc << 2) | (isTop ? 1 : 2);
        }
        
        if (currentFieldPicture)
        {
            int poc = pocArray[refIdx];
            return (poc << 2) | (isTopFieldArray[refIdx] ? 1 : 2);
        }
        
        return pocArray[refIdx];
    }
    
    /// <summary>
    /// Clears per-4x4-block deblocking data for intra MBs (MV=0, ref=-1).
    /// </summary>
    private void ClearBlockDataForDeblocking(int mbAddr)
    {
        int frameBase = mbAddr * 16;
        for (int i = 0; i < 16; i++)
        {
            deblockMvL0![2 * (frameBase + i)] = 0;
            deblockMvL0[2 * (frameBase + i) + 1] = 0;
            deblockMvL1![2 * (frameBase + i)] = 0;
            deblockMvL1[2 * (frameBase + i) + 1] = 0;
            deblockRefL0![frameBase + i] = -1;
            deblockRefL1![frameBase + i] = -1;

        }

        int partBase = mbAddr * 4;
        for (int part = 0; part < 4; part++)
        {
            deblockRefPicL0![partBase + part] = -1;
            deblockRefPicL1![partBase + part] = -1;
        }

        deblockMotionUniform![mbAddr] = true;
    }
    
    /// <summary>
    /// Stores the ref_idx for an 8x8 sub-block of the current macroblock.
    /// sub8x8: 0-3 for the four 8x8 blocks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StoreRefIdx(int mbAddr, int sub8x8, int list, sbyte refIdx)
    {
        int idx = mbAddr * 4 + sub8x8;
        if (list == 0)
            mbRefIdxL0![idx] = refIdx;
        else
            mbRefIdxL1![idx] = refIdx;
    }
    
    /// <summary>
    /// Stores uniform ref_idx for all 4 sub-blocks of a 16x16 partition.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StoreRefIdx16x16(int mbAddr, int list, sbyte refIdx)
    {
        int baseIdx = mbAddr * 4;
        if (list == 0)
        {
            mbRefIdxL0![baseIdx] = mbRefIdxL0[baseIdx + 1] = mbRefIdxL0[baseIdx + 2] = mbRefIdxL0[baseIdx + 3] = refIdx;
        }
        else
        {
            mbRefIdxL1![baseIdx] = mbRefIdxL1[baseIdx + 1] = mbRefIdxL1[baseIdx + 2] = mbRefIdxL1[baseIdx + 3] = refIdx;
        }
    }
    
    /// <summary>
    /// Builds reference picture lists L0 and L1 from the DPB for the current slice.
    /// P-slices: L0 = short-term refs sorted by frame_num descending.
    /// B-slices: L0 = refs with POC &lt; current sorted by POC desc, then POC &gt; current sorted asc.
    ///           L1 = refs with POC &gt; current sorted by POC asc, then POC &lt; current sorted desc.
    /// </summary>
    // The co-located picture for direct prediction is refPicListL1[0], which is settled when
    // the slice's reference lists are built and does not change again until the next slice.
    // Finding it used to mean walking the decoded picture buffer - three separate searches, on
    // every macroblock. FFmpeg settles the same thing once per slice in
    // ff_h264_direct_ref_list_init and afterwards only reads it.
    //
    // Only the MBAFF field flag is genuinely per macroblock, so the buffer index is what is
    // kept; the flag is read from it where it is needed.
    private bool directColocValid;
    private bool directFrameToFieldPair;
    private bool directFieldToFrame;
    private int directMbaffColocIndex = -1;
    private int directMbaffColParity;
    private sbyte[]? directFtfColRef;
    private short[]? directFtfColMvX, directFtfColMvY;
    private sbyte[]? directFtfColRefL1;
    private short[]? directFtfColMvL1X, directFtfColMvL1Y;
    private bool[]? directFtfMbFieldFlags;

    // The colZero test reads the co-located picture's reference indices and motion vectors.
    // Which picture that is, and which of its six arrays to read, is settled for the whole
    // slice - but it used to be worked out inside the per-block test, so a direct macroblock
    // walked the decoded picture buffer four times (sixteen without 8x8 inference) and ran the
    // same field-versus-frame cascade each time. FFmpeg resolves l1ref0, l1ref1, l1mv0 and
    // l1mv1 once per macroblock and the loop after that is plain indexing
    // (h264_direct.c:342-350); these are the same four pointers, resolved once per slice.
    private bool colZeroResolved;
    private sbyte[]? colZeroRefL0;
    private short[]? colZeroMvX, colZeroMvY;
    private sbyte[]? colZeroRefL1;
    private short[]? colZeroMvL1X, colZeroMvL1Y;
    private ColZeroAddressing colZeroAddressing;

    /// <summary>How a macroblock address maps onto the co-located picture's block arrays.</summary>
    private enum ColZeroAddressing
    {
        /// <summary>Same geometry both sides - the address is used as it stands.</summary>
        SameGeometry,
        /// <summary>A frame referencing a field pair: two frame rows share one field row.</summary>
        FrameToFieldPair,
        /// <summary>A field referencing a frame: one field row maps to one of two frame rows.</summary>
        FieldToFrame,
    }

    /// <summary>Forgets the co-located picture, so the next direct prediction finds it again.</summary>
    private void InvalidateDirectColocated() => directColocValid = false;

    /// <summary>Finds the co-located picture once, on the first direct prediction of a slice.</summary>
    private void EnsureDirectColocated<TShape>()
        where TShape : struct, IPictureShape
    {
        if (directColocValid)
            return;

        directColocValid = true;
        directFrameToFieldPair = false;
        directFieldToFrame = false;
        directMbaffColocIndex = -1;
        directMbaffColParity = 0;
        directFtfColRef = null;
        directFtfColMvX = directFtfColMvY = null;
        directFtfColRefL1 = null;
        directFtfColMvL1X = directFtfColMvL1Y = null;
        directFtfMbFieldFlags = null;
        colZeroResolved = false;
        colZeroRefL0 = colZeroRefL1 = null;
        colZeroMvX = colZeroMvY = colZeroMvL1X = colZeroMvL1Y = null;
        colZeroAddressing = ColZeroAddressing.SameGeometry;

        // Spec 8.4.1.2.2: colZero does not apply when L1[0] is long-term, and there is nothing
        // to resolve without an L1. Both are slice-wide, so they are decided here rather than
        // re-tested per block.
        if (refPicListL1Count <= 0 || refPicListL1IsLongTerm[0])
            return;

        ResolveColZeroPicture();

        int l1Poc = refPicListL1Poc[0];

        for (int di = 0; di < dpbCount; di++)
        {
            // A frame referencing a field pair.
            if (!currentFieldPicture && dpb[di].Poc == l1Poc
                && dpb[di].FieldPicture && dpb[di].TopFieldColocRefIdx != null)
            {
                directFrameToFieldPair = true;
            }

            // A field referencing a frame.
            if (currentFieldPicture && !directFieldToFrame
                && (dpb[di].TopFieldPoc == l1Poc || dpb[di].BottomFieldPoc == l1Poc)
                && !dpb[di].FieldPicture && dpb[di].ColocatedRefIdx != null)
            {
                directFieldToFrame = true;
                directFtfColRef = dpb[di].ColocatedRefIdx;
                directFtfColMvX = dpb[di].ColocatedMvX;
                directFtfColMvY = dpb[di].ColocatedMvY;
                directFtfColRefL1 = dpb[di].ColocatedRefIdxL1;
                directFtfColMvL1X = dpb[di].ColocatedMvL1X;
                directFtfColMvL1Y = dpb[di].ColocatedMvL1Y;
                directFtfMbFieldFlags = dpb[di].MbFieldFlags;
            }

            // MBAFF: the buffer entry is fixed, the per-macroblock field flag is not.
            if (TShape.Mbaff && directMbaffColocIndex < 0
                && dpb[di].Poc == l1Poc && dpb[di].MbFieldFlags != null)
            {
                directMbaffColocIndex = di;
                long topDist = Math.Abs((long)dpb[di].TopFieldPoc - currentPoc);
                long botDist = Math.Abs((long)dpb[di].BottomFieldPoc - currentPoc);
                directMbaffColParity = topDist >= botDist ? 1 : 0;
            }
        }
    }

    /// <summary>
    /// Picks the co-located picture's colZero arrays for this slice, and how its block
    /// addresses line up with ours.
    /// </summary>
    /// <remarks>
    /// This is the body that used to run inside every per-block colZero test: the search for
    /// the buffer entry whose picture order count matches L1[0], then the choice between a
    /// frame's arrays and one of a field pair's. Neither depends on the macroblock.
    /// </remarks>
    private void ResolveColZeroPicture()
    {
        int l1Poc = refPicListL1Poc[0];

        for (int i = 0; i < dpbCount; i++)
        {
            bool pocMatch;
            bool useTopFieldColoc = false;
            if (currentFieldPicture)
            {
                pocMatch = dpb[i].TopFieldPoc == l1Poc || dpb[i].BottomFieldPoc == l1Poc;
                // Use L1[0]'s actual field parity, not POC matching (both fields may share same POC)
                useTopFieldColoc = refPicListL1IsTopField[0];
            }
            else
            {
                pocMatch = dpb[i].Poc == l1Poc;
                // For frame->field-pair: L1[0] POC may be a field POC rather than min(top,bot)
                if (!pocMatch && dpb[i].FieldPicture)
                    pocMatch = dpb[i].TopFieldPoc == l1Poc || dpb[i].BottomFieldPoc == l1Poc;
            }

            if (!pocMatch || dpb[i].ColocatedRefIdx == null)
                continue;

            if (currentFieldPicture && dpb[i].FieldPicture && dpb[i].TopFieldColocRefIdx != null)
            {
                if (useTopFieldColoc)
                    TakeTopField(i);
                else
                    TakeBottomField(i);
            }
            else if (!currentFieldPicture && dpb[i].FieldPicture && dpb[i].TopFieldColocRefIdx != null)
            {
                // Frame->field-pair: choose field closest to current POC
                bool colUseBotField = Math.Abs(dpb[i].TopFieldPoc - currentPoc) >=
                                      Math.Abs(dpb[i].BottomFieldPoc - currentPoc);
                if (!colUseBotField)
                    TakeTopField(i);
                else
                    TakeBottomField(i);
                colZeroAddressing = ColZeroAddressing.FrameToFieldPair;
            }
            else
            {
                // A frame's own arrays - including the field->frame case, which reads them
                // with a remapped address rather than different arrays.
                colZeroRefL0 = dpb[i].ColocatedRefIdx;
                colZeroMvX = dpb[i].ColocatedMvX;
                colZeroMvY = dpb[i].ColocatedMvY;
                colZeroRefL1 = dpb[i].ColocatedRefIdxL1;
                colZeroMvL1X = dpb[i].ColocatedMvL1X;
                colZeroMvL1Y = dpb[i].ColocatedMvL1Y;
                if (currentFieldPicture && !dpb[i].FieldPicture)
                    colZeroAddressing = ColZeroAddressing.FieldToFrame;
            }

            colZeroResolved = colZeroRefL0 != null;
            return;
        }

        void TakeTopField(int i)
        {
            colZeroRefL0 = dpb[i].TopFieldColocRefIdx;
            colZeroMvX = dpb[i].TopFieldColocMvX;
            colZeroMvY = dpb[i].TopFieldColocMvY;
            colZeroRefL1 = dpb[i].TopFieldColocRefIdxL1;
            colZeroMvL1X = dpb[i].TopFieldColocMvL1X;
            colZeroMvL1Y = dpb[i].TopFieldColocMvL1Y;
        }

        void TakeBottomField(int i)
        {
            colZeroRefL0 = dpb[i].BotFieldColocRefIdx;
            colZeroMvX = dpb[i].BotFieldColocMvX;
            colZeroMvY = dpb[i].BotFieldColocMvY;
            colZeroRefL1 = dpb[i].BotFieldColocRefIdxL1;
            colZeroMvL1X = dpb[i].BotFieldColocMvL1X;
            colZeroMvL1Y = dpb[i].BotFieldColocMvL1Y;
        }
    }

    private void BuildReferencePictureLists(SliceHeader sliceHeader)
    {
        InvalidateDirectColocated();
        if (DiagnosticOutput != null && DiagnosticDumpAllMbsForPoc == currentPoc)
            DiagnosticOutput.WriteLine($"BUILD-REFLIST: poc={currentPoc} fieldPic={currentFieldPicture} sliceType={sliceHeader.Type} dpbCount={dpbCount}");
        
        refPicListL0Count = 0;
        refPicListL1Count = 0;
        
        // Clear stride/offset arrays to prevent stale field values from persisting
        // into subsequent frame slices. Field slices set stride=2*width; without clearing,
        // frame slices inherit these doubled strides via SetFrameRefDefaults (which only
        // sets entries where stride==0).
        Array.Clear(refPicListL0RefStride, 0, refPicListL0RefStride.Length);
        Array.Clear(refPicListL0RefOffset, 0, refPicListL0RefOffset.Length);
        Array.Clear(refPicListL1RefStride, 0, refPicListL1RefStride.Length);
        Array.Clear(refPicListL1RefOffset, 0, refPicListL1RefOffset.Length);
        
        if (dpbCount == 0)
            return;
        
        int codedWidth = context.CodedWidth;
        
        // Field pictures use a fundamentally different reference list construction:
        // each DPB entry is split into individual field entries (top + bottom),
        // interleaved with same-parity entries first. Matches FFmpeg build_def_list().
        if (currentFieldPicture)
        {
            BuildFieldReferencePictureLists(sliceHeader, codedWidth);
            return;
        }
        
        // Frame picture reference list construction (existing logic)
        if (sliceHeader.IsPSlice)
        {
            // P-slice L0: short-term by PicNum descending, then long-term by LongTermPicNum ascending
            // (H.264 spec 8.2.4.2.1)
            int maxFrameNum = context.ActiveSps!.MaxFrameNumber;
            int curFrameNum = sliceHeader.FrameNumber;
            
            // Short-term refs sorted by PicNum descending
            Span<int> stIndices = stackalloc int[dpbCount];
            Span<int> stPicNums = stackalloc int[dpbCount];
            int stCount = 0;
            
            Span<int> ltIndices = stackalloc int[dpbCount];
            int ltCount = 0;
            
            for (int i = 0; i < dpbCount; i++)
            {
                if (dpb[i].IsLongTerm)
                {
                    ltIndices[ltCount++] = i;
                }
                else
                {
                    stIndices[stCount] = i;
                    int fn = dpb[i].FrameNum;
                    stPicNums[stCount] = fn > curFrameNum ? fn - maxFrameNum : fn;
                    stCount++;
                }
            }
            
            // Sort short-term by PicNum descending
            for (int i = 0; i < stCount - 1; i++)
                for (int j = i + 1; j < stCount; j++)
                    if (stPicNums[i] < stPicNums[j])
                    {
                        (stIndices[i], stIndices[j]) = (stIndices[j], stIndices[i]);
                        (stPicNums[i], stPicNums[j]) = (stPicNums[j], stPicNums[i]);
                    }
            
            // Sort long-term by LongTermPicNum ascending
            for (int i = 0; i < ltCount - 1; i++)
                for (int j = i + 1; j < ltCount; j++)
                    if (dpb[ltIndices[i]].LongTermFrameIdx > dpb[ltIndices[j]].LongTermFrameIdx)
                        (ltIndices[i], ltIndices[j]) = (ltIndices[j], ltIndices[i]);
            
            // Build L0: short-term first, then long-term
            for (int i = 0; i < stCount && refPicListL0Count < MaxRefFrames; i++)
            {
                int idx = stIndices[i];
                refPicListL0[refPicListL0Count] = dpb[idx].Buffer;
                refPicListL0Poc[refPicListL0Count] = dpb[idx].Poc;
                refPicListL0RefPoc[refPicListL0Count] = dpb[idx].RefPoc;
                refPicListL0DpbPoc[refPicListL0Count] = dpb[idx].Poc;
                refPicListL0FrameNum[refPicListL0Count] = dpb[idx].FrameNum;
                refPicListL0IsLongTerm[refPicListL0Count] = false;
                refPicListL0TopFieldPoc[refPicListL0Count] = dpb[idx].TopFieldPoc;
                refPicListL0BotFieldPoc[refPicListL0Count] = dpb[idx].BottomFieldPoc;
                refPicListL0Count++;
            }
            for (int i = 0; i < ltCount && refPicListL0Count < MaxRefFrames; i++)
            {
                int idx = ltIndices[i];
                refPicListL0[refPicListL0Count] = dpb[idx].Buffer;
                refPicListL0Poc[refPicListL0Count] = dpb[idx].Poc;
                refPicListL0RefPoc[refPicListL0Count] = dpb[idx].RefPoc;
                refPicListL0DpbPoc[refPicListL0Count] = dpb[idx].Poc;
                refPicListL0FrameNum[refPicListL0Count] = dpb[idx].FrameNum;
                refPicListL0IsLongTerm[refPicListL0Count] = true;
                refPicListL0TopFieldPoc[refPicListL0Count] = dpb[idx].TopFieldPoc;
                refPicListL0BotFieldPoc[refPicListL0Count] = dpb[idx].BottomFieldPoc;
                refPicListL0Count++;
            }
        }
        else if (sliceHeader.IsBSlice)
        {
            // B-slice: separate short-term and long-term refs
            // Short-term split by POC relative to current, long-term appended at end
            
            Span<int> beforeIndices = stackalloc int[dpbCount];
            Span<int> afterIndices = stackalloc int[dpbCount];
            Span<int> ltIndices = stackalloc int[dpbCount];
            int beforeCount = 0, afterCount = 0, ltCount = 0;
            
            for (int i = 0; i < dpbCount; i++)
            {
                if (dpb[i].IsLongTerm)
                {
                    ltIndices[ltCount++] = i;
                }
                else if (dpb[i].Poc <= currentPoc)
                    beforeIndices[beforeCount++] = i;
                else
                    afterIndices[afterCount++] = i;
            }
            
            // Sort before by POC descending
            for (int i = 0; i < beforeCount - 1; i++)
                for (int j = i + 1; j < beforeCount; j++)
                    if (dpb[beforeIndices[i]].Poc < dpb[beforeIndices[j]].Poc)
                        (beforeIndices[i], beforeIndices[j]) = (beforeIndices[j], beforeIndices[i]);
            
            // Sort after by POC ascending
            for (int i = 0; i < afterCount - 1; i++)
                for (int j = i + 1; j < afterCount; j++)
                    if (dpb[afterIndices[i]].Poc > dpb[afterIndices[j]].Poc)
                        (afterIndices[i], afterIndices[j]) = (afterIndices[j], afterIndices[i]);
            
            // Sort long-term by LongTermPicNum ascending
            for (int i = 0; i < ltCount - 1; i++)
                for (int j = i + 1; j < ltCount; j++)
                    if (dpb[ltIndices[i]].LongTermFrameIdx > dpb[ltIndices[j]].LongTermFrameIdx)
                        (ltIndices[i], ltIndices[j]) = (ltIndices[j], ltIndices[i]);
            
            // L0: before (desc) then after (asc) then long-term (asc)
            for (int i = 0; i < beforeCount && refPicListL0Count < MaxRefFrames; i++)
            {
                int idx = beforeIndices[i];
                refPicListL0[refPicListL0Count] = dpb[idx].Buffer;
                refPicListL0Poc[refPicListL0Count] = dpb[idx].Poc;
                refPicListL0RefPoc[refPicListL0Count] = dpb[idx].RefPoc;
                refPicListL0DpbPoc[refPicListL0Count] = dpb[idx].Poc;
                refPicListL0FrameNum[refPicListL0Count] = dpb[idx].FrameNum;
                refPicListL0IsLongTerm[refPicListL0Count] = false;
                refPicListL0TopFieldPoc[refPicListL0Count] = dpb[idx].TopFieldPoc;
                refPicListL0BotFieldPoc[refPicListL0Count] = dpb[idx].BottomFieldPoc;
                refPicListL0Count++;
            }
            for (int i = 0; i < afterCount && refPicListL0Count < MaxRefFrames; i++)
            {
                int idx = afterIndices[i];
                refPicListL0[refPicListL0Count] = dpb[idx].Buffer;
                refPicListL0Poc[refPicListL0Count] = dpb[idx].Poc;
                refPicListL0RefPoc[refPicListL0Count] = dpb[idx].RefPoc;
                refPicListL0DpbPoc[refPicListL0Count] = dpb[idx].Poc;
                refPicListL0FrameNum[refPicListL0Count] = dpb[idx].FrameNum;
                refPicListL0IsLongTerm[refPicListL0Count] = false;
                refPicListL0TopFieldPoc[refPicListL0Count] = dpb[idx].TopFieldPoc;
                refPicListL0BotFieldPoc[refPicListL0Count] = dpb[idx].BottomFieldPoc;
                refPicListL0Count++;
            }
            for (int i = 0; i < ltCount && refPicListL0Count < MaxRefFrames; i++)
            {
                int idx = ltIndices[i];
                refPicListL0[refPicListL0Count] = dpb[idx].Buffer;
                refPicListL0Poc[refPicListL0Count] = dpb[idx].Poc;
                refPicListL0RefPoc[refPicListL0Count] = dpb[idx].RefPoc;
                refPicListL0DpbPoc[refPicListL0Count] = dpb[idx].Poc;
                refPicListL0FrameNum[refPicListL0Count] = dpb[idx].FrameNum;
                refPicListL0IsLongTerm[refPicListL0Count] = true;
                refPicListL0TopFieldPoc[refPicListL0Count] = dpb[idx].TopFieldPoc;
                refPicListL0BotFieldPoc[refPicListL0Count] = dpb[idx].BottomFieldPoc;
                refPicListL0Count++;
            }
            
            // L1: after (asc) then before (desc) then long-term (asc)
            for (int i = 0; i < afterCount && refPicListL1Count < MaxRefFrames; i++)
            {
                int idx = afterIndices[i];
                refPicListL1[refPicListL1Count] = dpb[idx].Buffer;
                refPicListL1Poc[refPicListL1Count] = dpb[idx].Poc;
                refPicListL1RefPoc[refPicListL1Count] = dpb[idx].RefPoc;
                refPicListL1DpbPoc[refPicListL1Count] = dpb[idx].Poc;
                refPicListL1FrameNum[refPicListL1Count] = dpb[idx].FrameNum;
                refPicListL1IsLongTerm[refPicListL1Count] = false;
                refPicListL1TopFieldPoc[refPicListL1Count] = dpb[idx].TopFieldPoc;
                refPicListL1BotFieldPoc[refPicListL1Count] = dpb[idx].BottomFieldPoc;
                refPicListL1Count++;
            }
            for (int i = 0; i < beforeCount && refPicListL1Count < MaxRefFrames; i++)
            {
                int idx = beforeIndices[i];
                refPicListL1[refPicListL1Count] = dpb[idx].Buffer;
                refPicListL1Poc[refPicListL1Count] = dpb[idx].Poc;
                refPicListL1RefPoc[refPicListL1Count] = dpb[idx].RefPoc;
                refPicListL1DpbPoc[refPicListL1Count] = dpb[idx].Poc;
                refPicListL1FrameNum[refPicListL1Count] = dpb[idx].FrameNum;
                refPicListL1IsLongTerm[refPicListL1Count] = false;
                refPicListL1TopFieldPoc[refPicListL1Count] = dpb[idx].TopFieldPoc;
                refPicListL1BotFieldPoc[refPicListL1Count] = dpb[idx].BottomFieldPoc;
                refPicListL1Count++;
            }
            for (int i = 0; i < ltCount && refPicListL1Count < MaxRefFrames; i++)
            {
                int idx = ltIndices[i];
                refPicListL1[refPicListL1Count] = dpb[idx].Buffer;
                refPicListL1Poc[refPicListL1Count] = dpb[idx].Poc;
                refPicListL1RefPoc[refPicListL1Count] = dpb[idx].RefPoc;
                refPicListL1DpbPoc[refPicListL1Count] = dpb[idx].Poc;
                refPicListL1FrameNum[refPicListL1Count] = dpb[idx].FrameNum;
                refPicListL1IsLongTerm[refPicListL1Count] = true;
                refPicListL1TopFieldPoc[refPicListL1Count] = dpb[idx].TopFieldPoc;
                refPicListL1BotFieldPoc[refPicListL1Count] = dpb[idx].BottomFieldPoc;
                refPicListL1Count++;
            }
            
            // If L1 == L0 and L1 has at least 2 entries, swap first two of L1 (spec 8.2.4.2.4)
            if (refPicListL1Count >= 2 && refPicListL0Count >= 1 &&
                refPicListL1Poc[0] == refPicListL0Poc[0])
            {
                (refPicListL1[0], refPicListL1[1]) = (refPicListL1[1], refPicListL1[0]);
                (refPicListL1Poc[0], refPicListL1Poc[1]) = (refPicListL1Poc[1], refPicListL1Poc[0]);
                (refPicListL1RefPoc[0], refPicListL1RefPoc[1]) = (refPicListL1RefPoc[1], refPicListL1RefPoc[0]);
                (refPicListL1DpbPoc[0], refPicListL1DpbPoc[1]) = (refPicListL1DpbPoc[1], refPicListL1DpbPoc[0]);
                (refPicListL1FrameNum[0], refPicListL1FrameNum[1]) = (refPicListL1FrameNum[1], refPicListL1FrameNum[0]);
                (refPicListL1IsLongTerm[0], refPicListL1IsLongTerm[1]) = (refPicListL1IsLongTerm[1], refPicListL1IsLongTerm[0]);
                (refPicListL1TopFieldPoc[0], refPicListL1TopFieldPoc[1]) = (refPicListL1TopFieldPoc[1], refPicListL1TopFieldPoc[0]);
                (refPicListL1BotFieldPoc[0], refPicListL1BotFieldPoc[1]) = (refPicListL1BotFieldPoc[1], refPicListL1BotFieldPoc[0]);
            }
        }
        
        // B-frame reference list trace
        if (sliceHeader.IsBSlice && DiagnosticOutput != null)
        {
            DiagnosticOutput.WriteLine($"B-REFLIST: poc={currentPoc} dpbCount={dpbCount}");
            for (int i = 0; i < dpbCount; i++)
            {
                DiagnosticOutput.Write($"  DPB[{i}]: poc={dpb[i].Poc} fn={dpb[i].FrameNum} isLT={dpb[i].IsLongTerm} ltIdx={dpb[i].LongTermFrameIdx} fp={dpb[i].FieldPicture} ref={dpb[i].Reference} tpoc={dpb[i].TopFieldPoc} bpoc={dpb[i].BottomFieldPoc}");
                if (DiagnosticDumpAllMbsForPoc == currentPoc && dpb[i].Buffer != null)
                {
                    int w = context.CodedWidth;
                    int h = context.CodedHeight;
                    int lumaSize = w * h;
                    var buf = dpb[i].Buffer!;
                    int checkLen = Math.Min(lumaSize, buf.Length);
                    uint a = 1, b = 0;
                    for (int j = 0; j < checkLen; j++) { a = (a + buf[j]) % 65521; b = (b + a) % 65521; }
                    DiagnosticOutput.Write($" lumaAdler=0x{(b << 16) | a:x8}");
                    if (dpb[i].FieldPicture && h >= 2)
                    {
                        uint ta = 1, tb2 = 0, ba2 = 1, bb2 = 0;
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w && y * w + x < checkLen; x++)
                            {
                                byte px = buf[y * w + x];
                                if ((y & 1) == 0) { ta = (ta + px) % 65521; tb2 = (tb2 + ta) % 65521; }
                                else { ba2 = (ba2 + px) % 65521; bb2 = (bb2 + ba2) % 65521; }
                            }
                        DiagnosticOutput.Write($" topAdler=0x{(tb2 << 16) | ta:x8} botAdler=0x{(bb2 << 16) | ba2:x8}");
                        // Per-field-row checksums for ALL rows of bottom field
                        DiagnosticOutput.Write(" botRows=[");
                        int fieldRowCount = h / 2;
                        for (int fr = 0; fr < fieldRowCount; fr++)
                        {
                            int rowOff = (fr * 2 + 1) * w; // odd rows = bottom field
                            uint ra = 1, rb = 0;
                            for (int x = 0; x < w && rowOff + x < checkLen; x++) { ra = (ra + buf[rowOff + x]) % 65521; rb = (rb + ra) % 65521; }
                            if (fr > 0) DiagnosticOutput.Write(",");
                            DiagnosticOutput.Write($"0x{(rb << 16) | ra:x8}");
                        }
                        DiagnosticOutput.Write("]");
                    }
                }
                DiagnosticOutput.WriteLine();
            }
            for (int i = 0; i < refPicListL0Count; i++)
                DiagnosticOutput.WriteLine($"  L0[{i}]: poc={refPicListL0Poc[i]} fn={refPicListL0FrameNum[i]}");
            for (int i = 0; i < refPicListL1Count; i++)
                DiagnosticOutput.WriteLine($"  L1[{i}]: poc={refPicListL1Poc[i]} fn={refPicListL1FrameNum[i]}");
        }
        
        // Apply RPLM (Reference Picture List Modification) per spec 8.2.4.3
        int maxPicNum = context.ActiveSps!.MaxFrameNumber;
        int currPicNum = sliceHeader.FrameNumber;
        
        if (DiagnosticOutput != null && sliceHeader.IsBSlice)
        {
            DiagnosticOutput.WriteLine($"  RPLM-CHECK: L0mod={sliceHeader.RefPicListModificationL0} L0count={sliceHeader.RplmL0Count} L1mod={sliceHeader.RefPicListModificationL1} L1count={sliceHeader.RplmL1Count}");
            if (sliceHeader.RefPicListModificationL0)
                for (int rc = 0; rc < sliceHeader.RplmL0Count; rc++)
                    DiagnosticOutput.WriteLine($"    L0-RPLM[{rc}]: idc={sliceHeader.RplmL0Idc[rc]} val={sliceHeader.RplmL0Value[rc]}");
            if (sliceHeader.RefPicListModificationL1)
                for (int rc = 0; rc < sliceHeader.RplmL1Count; rc++)
                    DiagnosticOutput.WriteLine($"    L1-RPLM[{rc}]: idc={sliceHeader.RplmL1Idc[rc]} val={sliceHeader.RplmL1Value[rc]}");
        }
        
        CaptureDefaultRefs(sliceHeader.IsBSlice ? 2 : 1);

        int numRefIdxL0Active = sliceHeader.NumRefIdxL0ActiveMinus1 + 1;
        if (sliceHeader.RefPicListModificationL0 && sliceHeader.RplmL0Count > 0)
        {
            ApplyRefPicListModification(
                refPicListL0, refPicListL0Poc, refPicListL0RefPoc, refPicListL0DpbPoc, refPicListL0FrameNum, refPicListL0IsLongTerm, refPicListL0Count,
                sliceHeader.RplmL0Idc, sliceHeader.RplmL0Value, sliceHeader.RplmL0Count,
                currPicNum, maxPicNum, numRefIdxL0Active);
            // RPLM populates exactly numRefIdxActive entries (may duplicate pictures)
            refPicListL0Count = numRefIdxL0Active;
        }
        else if (refPicListL0Count > numRefIdxL0Active)
        {
            // No RPLM: clamp to active count
            refPicListL0Count = numRefIdxL0Active;
        }
        
        if (sliceHeader.IsBSlice)
        {
            int numRefIdxL1Active = sliceHeader.NumRefIdxL1ActiveMinus1 + 1;
            if (sliceHeader.RefPicListModificationL1 && sliceHeader.RplmL1Count > 0)
            {
                ApplyRefPicListModification(
                    refPicListL1, refPicListL1Poc, refPicListL1RefPoc, refPicListL1DpbPoc, refPicListL1FrameNum, refPicListL1IsLongTerm, refPicListL1Count,
                    sliceHeader.RplmL1Idc, sliceHeader.RplmL1Value, sliceHeader.RplmL1Count,
                    currPicNum, maxPicNum, numRefIdxL1Active);
                // RPLM populates exactly numRefIdxActive entries (may duplicate pictures)
                refPicListL1Count = numRefIdxL1Active;
            }
            else if (refPicListL1Count > numRefIdxL1Active)
            {
                refPicListL1Count = numRefIdxL1Active;
            }
        }
        
        // Set default frame stride/offset for all frame reference entries
        SetFrameRefDefaults(codedWidth);
        ReplaceGrayReferences(sliceHeader);
        RecordPictureGray(sliceHeader);
    }
    
    /// <summary>
    /// Applies reference picture list modification commands per H.264 spec 8.2.4.3.1.
    /// Reorders entries in the reference list based on encoder-specified PicNum commands.
    /// </summary>
    private void ApplyRefPicListModification(
        byte[]?[] list, int[] pocs, int[] refPocs, int[] dpbPocs, int[] frameNums, bool[] isLongTermFlags, int listCount,
        uint[] rplmIdc, uint[] rplmValue, int rplmCount,
        int currPicNum, int maxPicNum, int numRefIdxActive)
    {
        // H.264 spec 8.2.4.3.1: shift-right, insert, compact (remove duplicate).
        // Needs one extra slot beyond numRefIdxActive for the temporary shift.
        for (int i = listCount; i <= numRefIdxActive; i++)
        {
            list[i] = null;
            pocs[i] = -1;
            refPocs[i] = -1;
            dpbPocs[i] = -1;
            frameNums[i] = -1;
            isLongTermFlags[i] = false;
        }
        
        int picNumPred = currPicNum;
        int refIdxLX = 0;
        
        for (int c = 0; c < rplmCount; c++)
        {
            uint idc = rplmIdc[c];
            uint absDiffPicNumMinus1 = rplmValue[c];
            
            if (idc == 0 || idc == 1)
            {
                int picNumNoWrap;
                if (idc == 0)
                {
                    picNumNoWrap = picNumPred - ((int)absDiffPicNumMinus1 + 1);
                    if (picNumNoWrap < 0)
                        picNumNoWrap += maxPicNum;
                }
                else
                {
                    picNumNoWrap = picNumPred + ((int)absDiffPicNumMinus1 + 1);
                    if (picNumNoWrap >= maxPicNum)
                        picNumNoWrap -= maxPicNum;
                }
                picNumPred = picNumNoWrap;
                int picNumLX = picNumNoWrap;
                
                // Find the matching short-term DPB entry
                byte[]? refBuf = null;
                int refPoc = 0, refRefPoc = 0, refFn = 0;
                for (int d = 0; d < dpbCount; d++)
                {
                    if (!dpb[d].IsLongTerm && dpb[d].FrameNum == picNumLX)
                    {
                        refBuf = dpb[d].Buffer;
                        refPoc = dpb[d].Poc;
                        refRefPoc = dpb[d].RefPoc;
                        refFn = dpb[d].FrameNum;
                        break;
                    }
                }
                
                // Step 1: Shift entries right from numRefIdxActive down to refIdxLX+1
                for (int i = numRefIdxActive; i > refIdxLX; i--)
                {
                    list[i] = list[i - 1];
                    pocs[i] = pocs[i - 1];
                    refPocs[i] = refPocs[i - 1];
                    dpbPocs[i] = dpbPocs[i - 1];
                    frameNums[i] = frameNums[i - 1];
                    isLongTermFlags[i] = isLongTermFlags[i - 1];
                }
                
                // Step 2: Insert at refIdxLX
                list[refIdxLX] = refBuf;
                pocs[refIdxLX] = refPoc;
                refPocs[refIdxLX] = refRefPoc;
                dpbPocs[refIdxLX] = refPoc; // For frames, DpbPoc == Poc
                frameNums[refIdxLX] = refFn;
                isLongTermFlags[refIdxLX] = false;
                
                // Step 3: Compact — scan from refIdxLX+1, keep non-duplicates
                int nIdx = refIdxLX + 1;
                for (int cIdx = refIdxLX + 1; cIdx <= numRefIdxActive; cIdx++)
                {
                    if (frameNums[cIdx] != picNumLX)
                    {
                        if (nIdx != cIdx)
                        {
                            list[nIdx] = list[cIdx];
                            pocs[nIdx] = pocs[cIdx];
                            refPocs[nIdx] = refPocs[cIdx];
                            dpbPocs[nIdx] = dpbPocs[cIdx];
                            frameNums[nIdx] = frameNums[cIdx];
                            isLongTermFlags[nIdx] = isLongTermFlags[cIdx];
                        }
                        nIdx++;
                    }
                }
                
                refIdxLX++;
            }
            else if (idc == 2)
            {
                // Long-term reference reordering (H.264 spec 8.2.4.3.2)
                int longTermPicNum = (int)absDiffPicNumMinus1; // value is long_term_pic_num for idc==2
                
                // Find the matching long-term DPB entry
                byte[]? refBuf = null;
                int refPoc = 0, refRefPoc = 0, refFn = 0;
                for (int d = 0; d < dpbCount; d++)
                {
                    if (dpb[d].IsLongTerm && dpb[d].LongTermFrameIdx == longTermPicNum)
                    {
                        refBuf = dpb[d].Buffer;
                        refPoc = dpb[d].Poc;
                        refRefPoc = dpb[d].RefPoc;
                        refFn = dpb[d].FrameNum;
                        break;
                    }
                }
                
                // Same shift-insert-compact as short-term
                for (int i = numRefIdxActive; i > refIdxLX; i--)
                {
                    list[i] = list[i - 1];
                    pocs[i] = pocs[i - 1];
                    refPocs[i] = refPocs[i - 1];
                    dpbPocs[i] = dpbPocs[i - 1];
                    frameNums[i] = frameNums[i - 1];
                    isLongTermFlags[i] = isLongTermFlags[i - 1];
                }
                
                list[refIdxLX] = refBuf;
                pocs[refIdxLX] = refPoc;
                refPocs[refIdxLX] = refRefPoc;
                dpbPocs[refIdxLX] = refPoc; // For frames, DpbPoc == Poc
                frameNums[refIdxLX] = refFn;
                isLongTermFlags[refIdxLX] = true;
                
                // Compact: remove duplicates by matching long-term pic num via buffer identity
                int nIdx = refIdxLX + 1;
                for (int cIdx = refIdxLX + 1; cIdx <= numRefIdxActive; cIdx++)
                {
                    if (list[cIdx] != refBuf)
                    {
                        if (nIdx != cIdx)
                        {
                            list[nIdx] = list[cIdx];
                            pocs[nIdx] = pocs[cIdx];
                            refPocs[nIdx] = refPocs[cIdx];
                            dpbPocs[nIdx] = dpbPocs[cIdx];
                            frameNums[nIdx] = frameNums[cIdx];
                            isLongTermFlags[nIdx] = isLongTermFlags[cIdx];
                        }
                        nIdx++;
                    }
                }
                
                refIdxLX++;
            }
        }
    }
    
    /// <summary>
    /// Applies reference picture list modification for field pictures (H.264 spec 8.2.4.3.1).
    /// Field pictures use PicNum = 2*FrameNumWrap + parity, where parity=1 for same as current,
    /// parity=0 for opposite. MaxPicNum = 2*MaxFrameNum, CurrPicNum = 2*frame_num + 1.
    /// </summary>
    private void ApplyFieldRefPicListModification(int list,
        uint[] rplmIdc, uint[] rplmValue, int rplmCount,
        int currFrameNum, int maxFrameNum, int numRefIdxActive)
    {
        byte[]?[] listBuf = list == 0 ? refPicListL0 : refPicListL1;
        int[] pocs = list == 0 ? refPicListL0Poc : refPicListL1Poc;
        int[] refPocArr = list == 0 ? refPicListL0RefPoc : refPicListL1RefPoc;
        int[] dpbPocs = list == 0 ? refPicListL0DpbPoc : refPicListL1DpbPoc;
        int[] frameNums = list == 0 ? refPicListL0FrameNum : refPicListL1FrameNum;
        bool[] isLongTermFlags = list == 0 ? refPicListL0IsLongTerm : refPicListL1IsLongTerm;
        int[] strides = list == 0 ? refPicListL0RefStride : refPicListL1RefStride;
        int[] offsets = list == 0 ? refPicListL0RefOffset : refPicListL1RefOffset;
        bool[] isTopFields = list == 0 ? refPicListL0IsTopField : refPicListL1IsTopField;
        int listCount = list == 0 ? refPicListL0Count : refPicListL1Count;
        
        bool currentIsTop = currentPictureStructure == ReferenceFrame.PictTopField;
        int currPicNum = 2 * currFrameNum + 1; // Current field always has same-parity PicNum (+1)
        int maxPicNum = 2 * maxFrameNum;
        
        // Pad to numRefIdxActive+1 for shift space
        for (int i = listCount; i <= numRefIdxActive; i++)
        {
            listBuf[i] = null;
            pocs[i] = -1;
            refPocArr[i] = -1;
            dpbPocs[i] = -1;
            frameNums[i] = -1;
            isLongTermFlags[i] = false;
            strides[i] = 0;
            offsets[i] = 0;
            isTopFields[i] = false;
        }
        
        int picNumPred = currPicNum;
        int refIdxLX = 0;
        int codedWidth = context.CodedWidth;
        
        for (int c = 0; c < rplmCount; c++)
        {
            uint idc = rplmIdc[c];
            uint absDiffPicNumMinus1 = rplmValue[c];
            
            if (idc == 0 || idc == 1)
            {
                int picNumNoWrap;
                if (idc == 0)
                {
                    picNumNoWrap = picNumPred - ((int)absDiffPicNumMinus1 + 1);
                    if (picNumNoWrap < 0)
                        picNumNoWrap += maxPicNum;
                }
                else
                {
                    picNumNoWrap = picNumPred + ((int)absDiffPicNumMinus1 + 1);
                    if (picNumNoWrap >= maxPicNum)
                        picNumNoWrap -= maxPicNum;
                }
                picNumPred = picNumNoWrap;
                int picNumLX = picNumNoWrap;
                
                // For field pictures: picNumLX encodes both FrameNum and parity
                // picNumLX = 2*FrameNumWrap + parityBit
                // parityBit=1 → same parity as current, parityBit=0 → opposite parity
                int targetFrameNum = picNumLX >> 1;
                bool sameParity = (picNumLX & 1) == 1;
                bool targetIsTop = sameParity ? currentIsTop : !currentIsTop;
                
                // Find matching field entry in current ref list (must match both FrameNum and parity)
                byte[]? refBuf = null;
                int refPoc = 0, refRefPocVal = 0, refFn = 0, refStride = 0, refOffset = 0, refDpbPoc = 0;
                
                // Search ref list for the matching entry
                int foundIdx = -1;
                for (int d = 0; d <= numRefIdxActive && d < MaxRefFrames; d++)
                {
                    if (!isLongTermFlags[d] && frameNums[d] == targetFrameNum && isTopFields[d] == targetIsTop)
                    {
                        foundIdx = d;
                        refBuf = listBuf[d];
                        refPoc = pocs[d];
                        refRefPocVal = refPocArr[d];
                        refDpbPoc = dpbPocs[d];
                        refFn = frameNums[d];
                        refStride = strides[d];
                        refOffset = offsets[d];
                        break;
                    }
                }
                
                // If not found in list, search DPB directly
                if (foundIdx < 0)
                {
                    for (int d = 0; d < dpbCount; d++)
                    {
                        if (!dpb[d].IsLongTerm && dpb[d].FrameNum == targetFrameNum)
                        {
                            int parityMask = targetIsTop ? ReferenceFrame.PictTopField : ReferenceFrame.PictBottomField;
                            if ((dpb[d].Reference & parityMask) != 0)
                            {
                                refBuf = dpb[d].Buffer;
                                refPoc = targetIsTop ? dpb[d].TopFieldPoc : dpb[d].BottomFieldPoc;
                                refRefPocVal = refPoc; // For field refs, RefPoc == field POC
                                refDpbPoc = dpb[d].Poc;
                                refFn = dpb[d].FrameNum;
                                refStride = codedWidth * 2;
                                refOffset = targetIsTop ? 0 : codedWidth;
                                break;
                            }
                        }
                    }
                }
                
                // Shift right
                for (int i = numRefIdxActive; i > refIdxLX; i--)
                {
                    listBuf[i] = listBuf[i - 1];
                    pocs[i] = pocs[i - 1];
                    refPocArr[i] = refPocArr[i - 1];
                    dpbPocs[i] = dpbPocs[i - 1];
                    frameNums[i] = frameNums[i - 1];
                    isLongTermFlags[i] = isLongTermFlags[i - 1];
                    strides[i] = strides[i - 1];
                    offsets[i] = offsets[i - 1];
                    isTopFields[i] = isTopFields[i - 1];
                }
                
                // Insert
                listBuf[refIdxLX] = refBuf;
                pocs[refIdxLX] = refPoc;
                refPocArr[refIdxLX] = refRefPocVal;
                dpbPocs[refIdxLX] = refDpbPoc;
                frameNums[refIdxLX] = refFn;
                isLongTermFlags[refIdxLX] = false;
                strides[refIdxLX] = refStride;
                offsets[refIdxLX] = refOffset;
                isTopFields[refIdxLX] = targetIsTop;
                
                // Compact: remove duplicate (same FrameNum + same parity)
                int nIdx = refIdxLX + 1;
                for (int cIdx = refIdxLX + 1; cIdx <= numRefIdxActive; cIdx++)
                {
                    bool isDuplicate = !isLongTermFlags[cIdx] &&
                        frameNums[cIdx] == refFn && isTopFields[cIdx] == targetIsTop;
                    if (!isDuplicate)
                    {
                        if (nIdx != cIdx)
                        {
                            listBuf[nIdx] = listBuf[cIdx];
                            pocs[nIdx] = pocs[cIdx];
                            refPocArr[nIdx] = refPocArr[cIdx];
                            dpbPocs[nIdx] = dpbPocs[cIdx];
                            frameNums[nIdx] = frameNums[cIdx];
                            isLongTermFlags[nIdx] = isLongTermFlags[cIdx];
                            strides[nIdx] = strides[cIdx];
                            offsets[nIdx] = offsets[cIdx];
                            isTopFields[nIdx] = isTopFields[cIdx];
                        }
                        nIdx++;
                    }
                }
                
                refIdxLX++;
            }
            else if (idc == 2)
            {
                // Long-term reference reordering for field pictures
                int longTermPicNum = (int)absDiffPicNumMinus1;
                int targetLtIdx = longTermPicNum >> 1;
                bool ltSameParity = (longTermPicNum & 1) == 1;
                bool ltTargetIsTop = ltSameParity ? currentIsTop : !currentIsTop;
                
                byte[]? refBuf = null;
                int refPoc = 0, refRefPocVal = 0, refFn = 0, refStride = 0, refOffset = 0, refDpbPoc = 0;
                for (int d = 0; d < dpbCount; d++)
                {
                    if (dpb[d].IsLongTerm && dpb[d].LongTermFrameIdx == targetLtIdx)
                    {
                        int parityMask = ltTargetIsTop ? ReferenceFrame.PictTopField : ReferenceFrame.PictBottomField;
                        if ((dpb[d].Reference & parityMask) != 0)
                        {
                            refBuf = dpb[d].Buffer;
                            refPoc = ltTargetIsTop ? dpb[d].TopFieldPoc : dpb[d].BottomFieldPoc;
                            refRefPocVal = refPoc; // For field refs, RefPoc == field POC
                            refDpbPoc = dpb[d].Poc;
                            refFn = dpb[d].FrameNum;
                            refStride = codedWidth * 2;
                            refOffset = ltTargetIsTop ? 0 : codedWidth;
                            break;
                        }
                    }
                }
                
                // Shift, insert, compact
                for (int i = numRefIdxActive; i > refIdxLX; i--)
                {
                    listBuf[i] = listBuf[i - 1];
                    pocs[i] = pocs[i - 1];
                    refPocArr[i] = refPocArr[i - 1];
                    dpbPocs[i] = dpbPocs[i - 1];
                    frameNums[i] = frameNums[i - 1];
                    isLongTermFlags[i] = isLongTermFlags[i - 1];
                    strides[i] = strides[i - 1];
                    offsets[i] = offsets[i - 1];
                    isTopFields[i] = isTopFields[i - 1];
                }
                
                listBuf[refIdxLX] = refBuf;
                pocs[refIdxLX] = refPoc;
                refPocArr[refIdxLX] = refRefPocVal;
                dpbPocs[refIdxLX] = refDpbPoc;
                frameNums[refIdxLX] = refFn;
                isLongTermFlags[refIdxLX] = true;
                strides[refIdxLX] = refStride;
                offsets[refIdxLX] = refOffset;
                isTopFields[refIdxLX] = ltTargetIsTop;
                
                int nIdx = refIdxLX + 1;
                for (int cIdx = refIdxLX + 1; cIdx <= numRefIdxActive; cIdx++)
                {
                    if (listBuf[cIdx] != refBuf || isTopFields[cIdx] != ltTargetIsTop)
                    {
                        if (nIdx != cIdx)
                        {
                            listBuf[nIdx] = listBuf[cIdx];
                            pocs[nIdx] = pocs[cIdx];
                            refPocArr[nIdx] = refPocArr[cIdx];
                            dpbPocs[nIdx] = dpbPocs[cIdx];
                            frameNums[nIdx] = frameNums[cIdx];
                            isLongTermFlags[nIdx] = isLongTermFlags[cIdx];
                            strides[nIdx] = strides[cIdx];
                            offsets[nIdx] = offsets[cIdx];
                            isTopFields[nIdx] = isTopFields[cIdx];
                        }
                        nIdx++;
                    }
                }
                
                refIdxLX++;
            }
        }
        
        // Update list count
        if (list == 0)
            refPicListL0Count = numRefIdxActive;
        else
            refPicListL1Count = numRefIdxActive;
    }
    /// Per H.264 spec 8.4.2.3.2: weights are derived from POC distances.
    /// w1 = distScaleFactor for L1 reference, w0 = 64 - w1.
    /// Used with logWeightDenom=5, offsets=0, prediction = ((w0*L0 + w1*L1 + 32) >> 6).
    /// For MBAFF, also computes per-field weights using field-specific POCs.
    /// Matches FFmpeg's implicit_weight_table() called with field=-1, then field=0 and field=1.
    /// </summary>
    private void ComputeImplicitWeightTable()
    {
        // Frame-level weights (field=-1 in FFmpeg): uses frame POCs, valid for frame MBs
        for (int ref0 = 0; ref0 < refPicListL0Count; ref0++)
        {
            int poc0 = refPicListL0Poc[ref0];
            for (int ref1 = 0; ref1 < refPicListL1Count; ref1++)
            {
                int poc1 = refPicListL1Poc[ref1];
                
                if (refPicListL0IsLongTerm[ref0] || refPicListL1IsLongTerm[ref1])
                {
                    implicitWeightL1[ref0, ref1] = 32;
                    continue;
                }
                
                int td = Math.Clamp(poc1 - poc0, -128, 127);
                int currPocForScaling = currentFieldPicture 
                    ? (currentPictureStructure == ReferenceFrame.PictTopField ? currentTopFieldPoc : currentBottomFieldPoc)
                    : currentPoc;
                int tb = Math.Clamp(currPocForScaling - poc0, -128, 127);
                
                implicitWeightL1[ref0, ref1] = ComputeImplicitWeight(td, tb);
            }
        }
        
        // MBAFF field-level weights (field=0 and field=1 in FFmpeg): uses per-field POCs.
        // Field MBs use field ref indices where fieldRef>>1 = frame ref, fieldRef&1 = parity code.
        // Parity code 0 = same parity as current MB, 1 = opposite parity.
        // Actual ref field: (curParity ^ (fieldRef & 1)) → 0=top, 1=bottom.
        if (isMbaffFrame)
        {
            for (int curParity = 0; curParity < 2; curParity++)
            {
                int curFieldPoc = curParity == 0 ? currentTopFieldPoc : currentBottomFieldPoc;
                
                for (int fr0 = 0; fr0 < 2 * refPicListL0Count; fr0++)
                {
                    int frameRef0 = fr0 >> 1;
                    int refParity0 = curParity ^ (fr0 & 1);
                    int poc0 = refParity0 == 0
                        ? refPicListL0TopFieldPoc[frameRef0]
                        : refPicListL0BotFieldPoc[frameRef0];
                    
                    for (int fr1 = 0; fr1 < 2 * refPicListL1Count; fr1++)
                    {
                        int frameRef1 = fr1 >> 1;
                        int refParity1 = curParity ^ (fr1 & 1);
                        int poc1 = refParity1 == 0
                            ? refPicListL1TopFieldPoc[frameRef1]
                            : refPicListL1BotFieldPoc[frameRef1];
                        
                        if (refPicListL0IsLongTerm[frameRef0] || refPicListL1IsLongTerm[frameRef1])
                        {
                            implicitWeightL1Field[fr0, fr1, curParity] = 32;
                            continue;
                        }
                        
                        int td = Math.Clamp(poc1 - poc0, -128, 127);
                        int tb = Math.Clamp(curFieldPoc - poc0, -128, 127);
                        
                        implicitWeightL1Field[fr0, fr1, curParity] = ComputeImplicitWeight(td, tb);
                    }
                }
            }
        }
    }
    
    private static int ComputeImplicitWeight(int td, int tb)
    {
        if (td == 0)
            return 32;
        
        int tx = (16384 + (Math.Abs(td) >> 1)) / td;
        int distScaleFactor = Math.Clamp((tb * tx + 32) >> 6, -1024, 1023);
        int w1 = distScaleFactor >> 2;
        
        if (w1 < -64 || w1 > 128)
            w1 = 32;
        
        return w1;
    }
    
    /// <summary>
    /// Builds reference picture lists for field pictures (H.264 spec 8.2.4.2.2/8.2.4.2.4).
    /// Each DPB entry is split into individual field entries (top and bottom).
    /// Entries with the same parity as the current field appear first, then opposite parity.
    /// Matches FFmpeg's build_def_list() + split_field_copy() + pic_as_field() pipeline.
    /// </summary>
    private void BuildFieldReferencePictureLists(SliceHeader sliceHeader, int codedWidth)
    {
        int sel = currentPictureStructure; // PICT_TOP_FIELD=1 or PICT_BOTTOM_FIELD=2
        int oppositeSel = sel ^ 3;         // XOR with 3 flips: 1→2, 2→1
        
        if (sliceHeader.IsPSlice)
        {
            // P-slice field ref list: short-term by FrameNumWrap descending, then long-term ascending
            int maxFrameNum = context.ActiveSps!.MaxFrameNumber;
            int curFrameNum = sliceHeader.FrameNumber;
            
            Span<int> stIndices = stackalloc int[dpbCount];
            Span<int> stPicNums = stackalloc int[dpbCount];
            int stCount = 0;
            Span<int> ltIndices = stackalloc int[dpbCount];
            int ltCount = 0;
            
            for (int i = 0; i < dpbCount; i++)
            {
                if (dpb[i].IsLongTerm)
                    ltIndices[ltCount++] = i;
                else
                {
                    stIndices[stCount] = i;
                    int fn = dpb[i].FrameNum;
                    stPicNums[stCount] = fn > curFrameNum ? fn - maxFrameNum : fn;
                    stCount++;
                }
            }
            
            // Sort short-term by PicNum descending (same as frame P-slice)
            for (int i = 0; i < stCount - 1; i++)
                for (int j = i + 1; j < stCount; j++)
                    if (stPicNums[i] < stPicNums[j])
                    {
                        (stIndices[i], stIndices[j]) = (stIndices[j], stIndices[i]);
                        (stPicNums[i], stPicNums[j]) = (stPicNums[j], stPicNums[i]);
                    }
            
            // Sort long-term by LongTermFrameIdx ascending
            for (int i = 0; i < ltCount - 1; i++)
                for (int j = i + 1; j < ltCount; j++)
                    if (dpb[ltIndices[i]].LongTermFrameIdx > dpb[ltIndices[j]].LongTermFrameIdx)
                        (ltIndices[i], ltIndices[j]) = (ltIndices[j], ltIndices[i]);
            
            // Build field-interleaved L0 from short-term refs
            BuildFieldDefList(stIndices, stCount, sel, false, codedWidth, 0);
            // Append long-term refs
            BuildFieldDefList(ltIndices, ltCount, sel, true, codedWidth, 0);
        }
        else if (sliceHeader.IsBSlice)
        {
            // B-slice: split DPB by field POC relative to current, then interleave fields
            int fieldPoc = (sel == ReferenceFrame.PictTopField) ? currentTopFieldPoc : currentBottomFieldPoc;
            
            Span<int> beforeIndices = stackalloc int[dpbCount];
            Span<int> afterIndices = stackalloc int[dpbCount];
            Span<int> ltIndices = stackalloc int[dpbCount];
            int beforeCount = 0, afterCount = 0, ltCount = 0;
            
            for (int i = 0; i < dpbCount; i++)
            {
                if (dpb[i].IsLongTerm)
                    ltIndices[ltCount++] = i;
                else if (dpb[i].Poc <= fieldPoc)
                    beforeIndices[beforeCount++] = i;
                else
                    afterIndices[afterCount++] = i;
            }
            
            // Sort before by POC descending
            for (int i = 0; i < beforeCount - 1; i++)
                for (int j = i + 1; j < beforeCount; j++)
                    if (dpb[beforeIndices[i]].Poc < dpb[beforeIndices[j]].Poc)
                        (beforeIndices[i], beforeIndices[j]) = (beforeIndices[j], beforeIndices[i]);
            
            // Sort after by POC ascending
            for (int i = 0; i < afterCount - 1; i++)
                for (int j = i + 1; j < afterCount; j++)
                    if (dpb[afterIndices[i]].Poc > dpb[afterIndices[j]].Poc)
                        (afterIndices[i], afterIndices[j]) = (afterIndices[j], afterIndices[i]);
            
            // Sort long-term by LongTermFrameIdx ascending
            for (int i = 0; i < ltCount - 1; i++)
                for (int j = i + 1; j < ltCount; j++)
                    if (dpb[ltIndices[i]].LongTermFrameIdx > dpb[ltIndices[j]].LongTermFrameIdx)
                        (ltIndices[i], ltIndices[j]) = (ltIndices[j], ltIndices[i]);
            
            // L0: before (desc) then after (asc) then long-term
            BuildFieldDefList(beforeIndices, beforeCount, sel, false, codedWidth, 0);
            BuildFieldDefList(afterIndices, afterCount, sel, false, codedWidth, 0);
            BuildFieldDefList(ltIndices, ltCount, sel, true, codedWidth, 0);
            
            // L1: after (asc) then before (desc) then long-term
            BuildFieldDefList(afterIndices, afterCount, sel, false, codedWidth, 1);
            BuildFieldDefList(beforeIndices, beforeCount, sel, false, codedWidth, 1);
            BuildFieldDefList(ltIndices, ltCount, sel, true, codedWidth, 1);
            
            // If L1 == L0 and L1 has at least 2 entries, swap first two of L1 (spec 8.2.4.2.4)
            if (refPicListL1Count >= 2 && refPicListL0Count >= 1 &&
                refPicListL1Poc[0] == refPicListL0Poc[0])
            {
                SwapRefListEntries(1, 0, 1);
            }
        }
        
        // Apply RPLM (Reference Picture List Modification) for field pictures
        // H.264 spec 8.2.4.3.1: field pictures use PicNum = 2*FrameNumWrap + parity
        int maxFrameNumForRplm = context.ActiveSps!.MaxFrameNumber;
        int currFrameNumForRplm = currentSliceHeader!.FrameNumber;
        
        CaptureDefaultRefs(currentSliceHeader.IsBSlice ? 2 : 1);

        int numRefIdxL0Active = currentSliceHeader.NumRefIdxL0ActiveMinus1 + 1;
        if (currentSliceHeader.RefPicListModificationL0 && currentSliceHeader.RplmL0Count > 0)
        {
            ApplyFieldRefPicListModification(0,
                currentSliceHeader.RplmL0Idc, currentSliceHeader.RplmL0Value, currentSliceHeader.RplmL0Count,
                currFrameNumForRplm, maxFrameNumForRplm, numRefIdxL0Active);
        }
        else if (refPicListL0Count > numRefIdxL0Active)
        {
            refPicListL0Count = numRefIdxL0Active;
        }
        
        if (currentSliceHeader.IsBSlice)
        {
            int numRefIdxL1Active = currentSliceHeader.NumRefIdxL1ActiveMinus1 + 1;
            if (currentSliceHeader.RefPicListModificationL1 && currentSliceHeader.RplmL1Count > 0)
            {
                ApplyFieldRefPicListModification(1,
                    currentSliceHeader.RplmL1Idc, currentSliceHeader.RplmL1Value, currentSliceHeader.RplmL1Count,
                    currFrameNumForRplm, maxFrameNumForRplm, numRefIdxL1Active);
            }
            else if (refPicListL1Count > numRefIdxL1Active)
            {
                refPicListL1Count = numRefIdxL1Active;
            }
        }
        
        // Set frame stride/offset defaults for any remaining uninitialized entries
        SetFrameRefDefaults(codedWidth);
        ReplaceGrayReferences(sliceHeader);
        RecordPictureGray(sliceHeader);
        
        // Temporary diagnostic: dump field ref lists for specific POC
        if (DiagnosticOutput != null && sliceHeader.IsBSlice)
        {
            DiagnosticOutput.WriteLine($"FIELD-B-CHECK: poc={currentPoc} target={DiagnosticDumpAllMbsForPoc} match={DiagnosticDumpAllMbsForPoc == currentPoc}");
        }
        if (DiagnosticDumpAllMbsForPoc == currentPoc && DiagnosticOutput != null)
        {
            DiagnosticOutput.WriteLine($"FIELD-REFLIST: poc={currentPoc} struct={currentPictureStructure} sliceType={sliceHeader.Type} L0={refPicListL0Count} L1={refPicListL1Count}");
            for (int i = 0; i < refPicListL0Count; i++)
                DiagnosticOutput.WriteLine($"  L0[{i}]: poc={refPicListL0Poc[i]} fn={refPicListL0FrameNum[i]} top={refPicListL0IsTopField[i]} lt={refPicListL0IsLongTerm[i]} stride={refPicListL0RefStride[i]} offset={refPicListL0RefOffset[i]} dpbPoc={refPicListL0DpbPoc[i]}");
            for (int i = 0; i < refPicListL1Count; i++)
                DiagnosticOutput.WriteLine($"  L1[{i}]: poc={refPicListL1Poc[i]} fn={refPicListL1FrameNum[i]} top={refPicListL1IsTopField[i]} lt={refPicListL1IsLongTerm[i]} stride={refPicListL1RefStride[i]} offset={refPicListL1RefOffset[i]} dpbPoc={refPicListL1DpbPoc[i]}");
        }
    }
    
    /// <summary>
    /// Builds a field-interleaved reference list segment from sorted DPB indices.
    /// Matches FFmpeg's build_def_list(): walks through DPB entries with two parallel scans
    /// (same parity then opposite parity), adding field entries in interleaved order.
    /// </summary>
    private void BuildFieldDefList(ReadOnlySpan<int> sortedDpbIndices, int count, 
        int sel, bool isLongTerm, int codedWidth, int list)
    {
        int oppositeSel = sel ^ 3;
        int i0 = 0, i1 = 0; // parallel indices: i0 for same parity, i1 for opposite
        
        while (i0 < count || i1 < count)
        {
            // Advance i0 to next DPB entry with same parity available
            while (i0 < count && (dpb[sortedDpbIndices[i0]].Reference & sel) == 0)
                i0++;
            
            // Advance i1 to next DPB entry with opposite parity available
            while (i1 < count && (dpb[sortedDpbIndices[i1]].Reference & oppositeSel) == 0)
                i1++;
            
            // Add same-parity field entry first
            if (i0 < count)
            {
                int dpbIdx = sortedDpbIndices[i0++];
                bool isTopField = sel == ReferenceFrame.PictTopField;
                AddFieldRefEntry(dpbIdx, isTopField, isLongTerm, codedWidth, list);
            }
            
            // Then add opposite-parity field entry
            if (i1 < count)
            {
                int dpbIdx = sortedDpbIndices[i1++];
                bool isTopField = oppositeSel == ReferenceFrame.PictTopField;
                AddFieldRefEntry(dpbIdx, isTopField, isLongTerm, codedWidth, list);
            }
        }
    }
    
    /// <summary>
    /// Adds a single field reference entry to L0 or L1.
    /// Matches FFmpeg's split_field_copy() + pic_as_field():
    /// - Uses field-specific POC (TopFieldPoc or BottomFieldPoc)
    /// - Sets doubled stride for field MC
    /// - Sets appropriate luma offset (0 for top, codedWidth for bottom)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddFieldRefEntry(int dpbIdx, bool isTopField, bool isLongTerm, int codedWidth, int list)
    {
        ref var entry = ref dpb[dpbIdx];
        int fieldPoc = isTopField ? entry.TopFieldPoc : entry.BottomFieldPoc;
        int lumaStride = codedWidth * 2;
        int lumaOffset = isTopField ? 0 : codedWidth;
        
        if (list == 0)
        {
            if (refPicListL0Count >= MaxRefFrames) return;
            refPicListL0[refPicListL0Count] = entry.Buffer;
            refPicListL0Poc[refPicListL0Count] = fieldPoc;
            refPicListL0RefPoc[refPicListL0Count] = fieldPoc;
            refPicListL0FrameNum[refPicListL0Count] = entry.FrameNum;
            refPicListL0IsLongTerm[refPicListL0Count] = isLongTerm;
            refPicListL0RefStride[refPicListL0Count] = lumaStride;
            refPicListL0RefOffset[refPicListL0Count] = lumaOffset;
            refPicListL0IsTopField[refPicListL0Count] = isTopField;
            refPicListL0DpbPoc[refPicListL0Count] = entry.Poc;
            refPicListL0Count++;
        }
        else
        {
            if (refPicListL1Count >= MaxRefFrames) return;
            refPicListL1[refPicListL1Count] = entry.Buffer;
            refPicListL1Poc[refPicListL1Count] = fieldPoc;
            refPicListL1RefPoc[refPicListL1Count] = fieldPoc;
            refPicListL1FrameNum[refPicListL1Count] = entry.FrameNum;
            refPicListL1IsLongTerm[refPicListL1Count] = isLongTerm;
            refPicListL1RefStride[refPicListL1Count] = lumaStride;
            refPicListL1RefOffset[refPicListL1Count] = lumaOffset;
            refPicListL1IsTopField[refPicListL1Count] = isTopField;
            refPicListL1DpbPoc[refPicListL1Count] = entry.Poc;
            refPicListL1Count++;
        }
    }
    
    /// <summary>
    /// Swaps two entries in a reference list (for B-slice L1 identity fix, spec 8.2.4.2.4).
    /// </summary>
    /// <summary>
    /// <c>h->default_ref[list]</c> - one entry of a reference list, saved whole. The C copies
    /// an <c>H264Ref</c> by assignment; here a list entry is fifteen parallel arrays, so the
    /// members that <see cref="SwapRefListEntries"/> moves are the ones that make an entry.
    /// </summary>
    private struct RefListEntry
    {
        public byte[]? Buffer;
        public int Poc;
        public int RefPoc;
        public int FrameNum;
        public int DpbPoc;
        public int RefStride;
        public int RefOffset;
        public bool IsLongTerm;
        public bool IsTopField;
        public bool Valid;
    }

    private readonly RefListEntry[] defaultRef = new RefListEntry[2];

    /// <summary>
    /// <c>h->default_ref[i] = sl->ref_list[i][0]</c> at the end of
    /// <c>h264_initialise_ref_list</c>: the head of each list BEFORE any reordering, which is
    /// what a missing or grey entry is replaced with afterwards.
    /// </summary>
    private void CaptureDefaultRefs(int listCount)
    {
        for (int list = 0; list < listCount; list++)
        {
            int count = list == 0 ? refPicListL0Count : refPicListL1Count;
            defaultRef[list] = count > 0 ? ReadRefListEntry(list, 0) : default;
        }
    }

    private RefListEntry ReadRefListEntry(int list, int index) => list == 0
        ? new RefListEntry
        {
            Buffer = refPicListL0[index],
            Poc = refPicListL0Poc[index],
            RefPoc = refPicListL0RefPoc[index],
            FrameNum = refPicListL0FrameNum[index],
            DpbPoc = refPicListL0DpbPoc[index],
            RefStride = refPicListL0RefStride[index],
            RefOffset = refPicListL0RefOffset[index],
            IsLongTerm = refPicListL0IsLongTerm[index],
            IsTopField = refPicListL0IsTopField[index],
            Valid = refPicListL0[index] != null,
        }
        : new RefListEntry
        {
            Buffer = refPicListL1[index],
            Poc = refPicListL1Poc[index],
            RefPoc = refPicListL1RefPoc[index],
            FrameNum = refPicListL1FrameNum[index],
            DpbPoc = refPicListL1DpbPoc[index],
            RefStride = refPicListL1RefStride[index],
            RefOffset = refPicListL1RefOffset[index],
            IsLongTerm = refPicListL1IsLongTerm[index],
            IsTopField = refPicListL1IsTopField[index],
            Valid = refPicListL1[index] != null,
        };

    private void WriteRefListEntry(int list, int index, in RefListEntry e)
    {
        if (list == 0)
        {
            refPicListL0[index] = e.Buffer;
            refPicListL0Poc[index] = e.Poc;
            refPicListL0RefPoc[index] = e.RefPoc;
            refPicListL0FrameNum[index] = e.FrameNum;
            refPicListL0DpbPoc[index] = e.DpbPoc;
            refPicListL0RefStride[index] = e.RefStride;
            refPicListL0RefOffset[index] = e.RefOffset;
            refPicListL0IsLongTerm[index] = e.IsLongTerm;
            refPicListL0IsTopField[index] = e.IsTopField;
        }
        else
        {
            refPicListL1[index] = e.Buffer;
            refPicListL1Poc[index] = e.Poc;
            refPicListL1RefPoc[index] = e.RefPoc;
            refPicListL1FrameNum[index] = e.FrameNum;
            refPicListL1DpbPoc[index] = e.DpbPoc;
            refPicListL1RefStride[index] = e.RefStride;
            refPicListL1RefOffset[index] = e.RefOffset;
            refPicListL1IsLongTerm[index] = e.IsLongTerm;
            refPicListL1IsTopField[index] = e.IsTopField;
        }
    }

    /// <summary>
    /// Whether a reference list entry is one of the grey frame_num gap stand-ins.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE: the C asks the entry's <c>parent->gray</c>, because an H264Ref carries a
    /// pointer back to its picture. A list entry here carries the picture's buffer instead,
    /// and a buffer belongs to exactly one DPB entry, so the buffer identifies the picture.
    /// </remarks>
    private bool IsGrayReference(byte[]? buffer)
    {
        if (buffer == null)
            return false;
        for (int i = 0; i < dpbCount; i++)
        {
            if (ReferenceEquals(dpb[i].Buffer, buffer))
                return dpb[i].Gray;
        }
        return false;
    }

    /// <summary>
    /// <c>noref_gray</c>, which ffmpeg has on by default: once a real intra picture has been
    /// seen, a grey gap stand-in is a worse reference than any real picture, so a list entry
    /// pointing at one is replaced by a non-grey default ref ("replacement of gray gap
    /// frame"). Without it a stream joined mid-GOP predicts from flat grey where ffmpeg
    /// predicts from a picture, and no frame can match.
    /// </summary>
    private void ReplaceGrayReferences(SliceHeader sliceHeader)
    {
        if (!nonGray)
            return;

        int listCount = sliceHeader.IsBSlice ? 2 : 1;
        for (int list = 0; list < listCount; list++)
        {
            int count = list == 0 ? refPicListL0Count : refPicListL1Count;
            for (int index = 0; index < count; index++)
            {
                byte[]? buffer = list == 0 ? refPicListL0[index] : refPicListL1[index];
                if (!IsGrayReference(buffer))
                    continue;

                for (int j = 0; j < listCount; j++)
                {
                    int list2 = (list + j) & 1;
                    if (defaultRef[list2].Valid && !IsGrayReference(defaultRef[list2].Buffer))
                    {
                        WriteRefListEntry(list, index, in defaultRef[list2]);
                        DiagnosticOutput?.WriteLine(
                            $"GRAY-REPLACE: list={list} index={index} from list{list2} default");
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// <c>h->cur_pic_ptr->gray</c> and <c>h->non_gray</c>: an intra picture is never grey and
    /// declares that real pictures exist again; an inter picture is grey if any of its
    /// references is.
    /// </summary>
    private void RecordPictureGray(SliceHeader sliceHeader)
    {
        if (sliceHeader.Type == SliceType.ISlice || sliceHeader.Type == SliceType.SiSlice)
        {
            currentPictureGray = false;
            nonGray = true;
            return;
        }

        bool gray = false;
        for (int i = 0; i < refPicListL0Count; i++)
            gray |= IsGrayReference(refPicListL0[i]);
        if (sliceHeader.IsBSlice)
        {
            for (int i = 0; i < refPicListL1Count; i++)
                gray |= IsGrayReference(refPicListL1[i]);
        }
        currentPictureGray = gray;
    }

    private void SwapRefListEntries(int list, int idxA, int idxB)
    {
        if (list == 0)
        {
            (refPicListL0[idxA], refPicListL0[idxB]) = (refPicListL0[idxB], refPicListL0[idxA]);
            (refPicListL0Poc[idxA], refPicListL0Poc[idxB]) = (refPicListL0Poc[idxB], refPicListL0Poc[idxA]);
            (refPicListL0RefPoc[idxA], refPicListL0RefPoc[idxB]) = (refPicListL0RefPoc[idxB], refPicListL0RefPoc[idxA]);
            (refPicListL0FrameNum[idxA], refPicListL0FrameNum[idxB]) = (refPicListL0FrameNum[idxB], refPicListL0FrameNum[idxA]);
            (refPicListL0IsLongTerm[idxA], refPicListL0IsLongTerm[idxB]) = (refPicListL0IsLongTerm[idxB], refPicListL0IsLongTerm[idxA]);
            (refPicListL0RefStride[idxA], refPicListL0RefStride[idxB]) = (refPicListL0RefStride[idxB], refPicListL0RefStride[idxA]);
            (refPicListL0RefOffset[idxA], refPicListL0RefOffset[idxB]) = (refPicListL0RefOffset[idxB], refPicListL0RefOffset[idxA]);
            (refPicListL0IsTopField[idxA], refPicListL0IsTopField[idxB]) = (refPicListL0IsTopField[idxB], refPicListL0IsTopField[idxA]);
            (refPicListL0DpbPoc[idxA], refPicListL0DpbPoc[idxB]) = (refPicListL0DpbPoc[idxB], refPicListL0DpbPoc[idxA]);
        }
        else
        {
            (refPicListL1[idxA], refPicListL1[idxB]) = (refPicListL1[idxB], refPicListL1[idxA]);
            (refPicListL1Poc[idxA], refPicListL1Poc[idxB]) = (refPicListL1Poc[idxB], refPicListL1Poc[idxA]);
            (refPicListL1RefPoc[idxA], refPicListL1RefPoc[idxB]) = (refPicListL1RefPoc[idxB], refPicListL1RefPoc[idxA]);
            (refPicListL1FrameNum[idxA], refPicListL1FrameNum[idxB]) = (refPicListL1FrameNum[idxB], refPicListL1FrameNum[idxA]);
            (refPicListL1IsLongTerm[idxA], refPicListL1IsLongTerm[idxB]) = (refPicListL1IsLongTerm[idxB], refPicListL1IsLongTerm[idxA]);
            (refPicListL1RefStride[idxA], refPicListL1RefStride[idxB]) = (refPicListL1RefStride[idxB], refPicListL1RefStride[idxA]);
            (refPicListL1RefOffset[idxA], refPicListL1RefOffset[idxB]) = (refPicListL1RefOffset[idxB], refPicListL1RefOffset[idxA]);
            (refPicListL1IsTopField[idxA], refPicListL1IsTopField[idxB]) = (refPicListL1IsTopField[idxB], refPicListL1IsTopField[idxA]);
            (refPicListL1DpbPoc[idxA], refPicListL1DpbPoc[idxB]) = (refPicListL1DpbPoc[idxB], refPicListL1DpbPoc[idxA]);
        }
    }
    
    /// <summary>
    /// Sets frame reference defaults (stride=codedWidth, offset=0) for all current L0/L1 entries
    /// that don't already have field stride/offset set. Called after frame-based list building.
    /// </summary>
    private void SetFrameRefDefaults(int codedWidth)
    {
        for (int i = 0; i < refPicListL0Count; i++)
        {
            if (refPicListL0RefStride[i] == 0)
            {
                refPicListL0RefStride[i] = codedWidth;
                refPicListL0RefOffset[i] = 0;
            }
        }
        for (int i = 0; i < refPicListL1Count; i++)
        {
            if (refPicListL1RefStride[i] == 0)
            {
                refPicListL1RefStride[i] = codedWidth;
                refPicListL1RefOffset[i] = 0;
            }
        }
    }
    
    /// <summary>
    /// Gets the reference frame buffer for the given ref_idx and list.
    /// Returns null if the ref_idx is out of range (caller handles gracefully).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[]? GetReferenceBuffer(int refIdx, int list)
    {
        if (list == 0)
        {
            if (refIdx >= 0 && refIdx < refPicListL0Count)
                return refPicListL0[refIdx];
        }
        else
        {
            if (refIdx >= 0 && refIdx < refPicListL1Count)
                return refPicListL1[refIdx];
        }
        return null;
    }
    
    /// <summary>
    /// Gets the luma byte offset within the reference buffer for field MC.
    /// For frame references: 0. For bottom field references: codedWidth (one line offset).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetRefLumaOffset(int refIdx, int list)
    {
        if (list == 0)
            return (refIdx >= 0 && refIdx < refPicListL0RefOffset.Length) ? refPicListL0RefOffset[refIdx] : 0;
        else
            return (refIdx >= 0 && refIdx < refPicListL1RefOffset.Length) ? refPicListL1RefOffset[refIdx] : 0;
    }
    
    /// <summary>
    /// Derives the P_Skip motion vector using FFmpeg's pred_pskip_motion logic.
    /// Per H.264 spec 8.4.1.1: if A or B has (ref=0, MV=0), use MV=(0,0).
    /// Otherwise use median predictor with ref_idx=0.
    /// </summary>
    private void DerivePSkipMotionVector<TShape>(int mbAddr)
        where TShape : struct, IPictureShape
    {
        int pos = Scan8[0];
        int leftRef = refCacheL0[pos - 1];
        int topRef = refCacheL0[pos - 8];
        short mvAX = mvCacheL0X[pos - 1], mvAY = mvCacheL0Y[pos - 1];
        short mvBX = mvCacheL0X[pos - 8], mvBY = mvCacheL0Y[pos - 8];
        
        // Diagnostic trace for MBAFF skip MV debugging
        if (DiagnosticOutput != null && TShape.Mbaff)
        {
            int mbW = context.WidthInMacroblocks;
            int mX = mbAddr % mbW, mY = mbAddr / mbW;
            DiagnosticOutput.WriteLine($"PSKIP MB({mX},{mY}) leftRef={leftRef} leftMV=({mvAX},{mvAY}) topRef={topRef} topMV=({mvBX},{mvBY}) topXY={mbaffTopXY} leftTopXY={mbaffLeftXYTop}");
        }
        
        // P_Skip zero-MV shortcut: if left or top has (ref=0, MV=(0,0)), use (0,0)
        if (leftRef == PartNotAvailable ||
            (leftRef == 0 && mvAX == 0 && mvAY == 0))
        {
            FillMvCache(mvCacheL0X, mvCacheL0Y, pos, 4, 4, 0, 0);
        }
        else if (topRef == PartNotAvailable ||
                 (topRef == 0 && mvBX == 0 && mvBY == 0))
        {
            FillMvCache(mvCacheL0X, mvCacheL0Y, pos, 4, 4, 0, 0);
        }
        else
        {
            PredMotion<TShape>(0, 4, 0, 0, out short mvpX, out short mvpY);
            FillMvCache(mvCacheL0X, mvCacheL0Y, pos, 4, 4, mvpX, mvpY);
            if (DiagnosticOutput != null && TShape.Mbaff)
            {
                int mbW = context.WidthInMacroblocks;
                int mX = mbAddr % mbW, mY = mbAddr / mbW;
                DiagnosticOutput.WriteLine($"PSKIP-MEDIAN MB({mX},{mY}) mvp=({mvpX},{mvpY})");
            }
        }
        
        FillMvCache(mvCacheL1X, mvCacheL1Y, pos, 4, 4, 0, 0);
        StoreRefIdx16x16(mbAddr, 0, 0);
        StoreRefIdx16x16(mbAddr, 1, -1);
    }
    
    /// <summary>
    /// Derives B_Skip/B_Direct_16x16 motion vectors using spatial direct mode.
    /// Per H.264 spec 8.4.1.2.2: derive ref_idx from smallest positive neighbor ref,
    /// then use median predictor for L0 and L1 independently.
    /// </summary>
    // Temporary diagnostic target for spatial direct mode
    public static (int mbX, int mbY, int poc)? DiagDirectTarget = null;
    public static int DiagDirectTracePoc = -1;
    
    private void DeriveBDirectSpatialMotionVectors<TShape>(int mbAddr)
        where TShape : struct, IPictureShape
    {
        int pos = Scan8[0];
        int mbWidth = context.WidthInMacroblocks;
        int mbX = mbAddr % mbWidth;
        int mbY = mbAddr / mbWidth;
        
        if (DiagDirectTracePoc >= 0 && currentPoc == DiagDirectTracePoc && DiagnosticOutput != null)
            DiagnosticOutput.WriteLine($"DIRECT-CALL MB({mbX},{mbY}) poc={currentPoc} mbAddr={mbAddr} d8x8inf={context.ActiveSps?.Direct8x8Inference}");
        
        bool diagDirect = DiagDirectTarget.HasValue && DiagnosticOutput != null
            && DiagDirectTarget.Value.mbX == mbX && DiagDirectTarget.Value.mbY == mbY
            && (DiagDirectTarget.Value.poc < 0 || DiagDirectTarget.Value.poc == currentPoc);
        
        // Read neighbor ref_idx from refCache. For coded inter MBs, the cache has the
        // parsed ref. For Direct/Skip MBs, the cache has the actual derived ref (stored
        // unclamped, matching ffmpeg). Override below verifies from per-MB arrays.
        //
        // The diagonal neighbour's vector is kept, not discarded. It used to be fetched here
        // for the reference derivation and then fetched a second time inside PredMotion, which
        // is two walks of the MBAFF diagonal rule per list where FFmpeg does one: its ref loop
        // keeps `C` alongside `refc` and predicts from it in the same pass (h264_direct.c:224).
        int leftRefL0 = refCacheL0[pos - 1];
        int topRefL0 = refCacheL0[pos - 8];
        int diagRefL0 = FetchDiagonalMv<TShape>(refCacheL0, mvCacheL0X, mvCacheL0Y,
            pos, 4, 0, out short diagMvL0X, out short diagMvL0Y);
        int leftRefL1 = refCacheL1[pos - 1];
        int topRefL1 = refCacheL1[pos - 8];
        int diagRefL1 = FetchDiagonalMv<TShape>(refCacheL1, mvCacheL1X, mvCacheL1Y,
            pos, 4, 1, out short diagMvL1X, out short diagMvL1Y);
        
        int derivedRefL0 = DeriveDirectRefIdx(leftRefL0, topRefL0, diagRefL0);
        int derivedRefL1 = DeriveDirectRefIdx(leftRefL1, topRefL1, diagRefL1);
        
        // A list with no usable neighbour predicts the zero vector, and is only then given an
        // index. Predicting before the fixup below is what lets the predictor assume one of the
        // three neighbours carries the derived index, which is the assumption that removes its
        // fallback cases.
        short mvL0X = 0, mvL0Y = 0;
        if (derivedRefL0 >= 0)
            PredictDirectMotion(mvCacheL0X, mvCacheL0Y, pos, derivedRefL0,
                leftRefL0, topRefL0, diagRefL0, diagMvL0X, diagMvL0Y, out mvL0X, out mvL0Y);
        
        short mvL1X = 0, mvL1Y = 0;
        if (derivedRefL1 >= 0)
            PredictDirectMotion(mvCacheL1X, mvCacheL1Y, pos, derivedRefL1,
                leftRefL1, topRefL1, diagRefL1, diagMvL1X, diagMvL1Y, out mvL1X, out mvL1Y);
        
        // Per H.264 spec 8.4.1.2.2: if both lists have all-negative neighbor refs,
        // set both to 0 (both lists used with ref_idx=0), with both vectors left at zero.
        if (derivedRefL0 < 0 && derivedRefL1 < 0)
        {
            derivedRefL0 = 0;
            derivedRefL1 = 0;
        }
        
        if (diagDirect)
        {
            DiagnosticOutput!.WriteLine($"DIRECT-DIAG MB({mbX},{mbY}) poc={currentPoc} mbAddr={mbAddr}");
            DiagnosticOutput.WriteLine($"  Neighbor refs L0: left={leftRefL0} top={topRefL0} diag={diagRefL0}");
            DiagnosticOutput.WriteLine($"  Neighbor refs L1: left={leftRefL1} top={topRefL1} diag={diagRefL1}");
            DiagnosticOutput.WriteLine($"  Neighbor L0 MVs: left=({mvCacheL0X[pos-1]},{mvCacheL0Y[pos-1]}) top=({mvCacheL0X[pos-8]},{mvCacheL0Y[pos-8]})");
            DiagnosticOutput.WriteLine($"  Neighbor L1 MVs: left=({mvCacheL1X[pos-1]},{mvCacheL1Y[pos-1]}) top=({mvCacheL1X[pos-8]},{mvCacheL1Y[pos-8]})");
            DiagnosticOutput.WriteLine($"  Derived ref: L0={derivedRefL0} L1={derivedRefL1}");
            DiagnosticOutput.WriteLine($"  MV predictor: L0=({mvL0X},{mvL0Y}) L1=({mvL1X},{mvL1Y})");
        }
        
        // FFmpeg's fast path, h264_direct.c: `if (!(is_b8x8 | mv[0] | mv[1]))`.
        //
        // When both predicted vectors are zero, every 4x4 block of this macroblock ends at zero
        // whatever the co-located picture says: the colZero test only ever *replaces* a vector
        // with zero, and these are zero already. So the answer is known without reading the
        // co-located picture at all - no cross-type detection, no per-8x8 or per-4x4 loop, and
        // none of the sixteen co-located lookups. Four fills and out.
        //
        // FFmpeg's is_b8x8 term is constant false here: this function only handles the 16x16
        // direct case, and B_8x8 with direct sub-partitions goes to the 8x8 variant instead.
        if (mvL0X == 0 && mvL0Y == 0 && mvL1X == 0 && mvL1Y == 0 && !diagDirect)
        {
            FillMvCache(mvCacheL0X, mvCacheL0Y, pos, 4, 4, 0, 0);
            FillMvCache(mvCacheL1X, mvCacheL1Y, pos, 4, 4, 0, 0);
            FillRefCache(pos, 4, 4, derivedRefL0);
            FillRefCacheL1(pos, 4, 4, derivedRefL1);
            StoreRefIdx16x16(mbAddr, 0, (sbyte)derivedRefL0);
            StoreRefIdx16x16(mbAddr, 1, (sbyte)derivedRefL1);
            return;
        }
        // Per ffmpeg pred_spatial_direct_motion: check colZero per-8x8 or per-4x4 block.
        // When direct_8x8_inference_flag=1, check per-8x8 and fill all 4x4 blocks uniformly.
        // When direct_8x8_inference_flag=0, check per-4x4 — each 4x4 may independently zero its MV.
        bool needsColZeroCheck = derivedRefL0 == 0 || derivedRefL1 == 0;
        bool direct8x8Inference = context.ActiveSps?.Direct8x8Inference ?? true;
        
        // Detect frame→field-pair cross-type (ffmpeg h264_direct.c lines 286-291).
        // When a frame MB references a field-pair L1[0], the cross-type path uses
        // b8_stride=0 addressing with even/odd frame row mapping to field MB halves.
        EnsureDirectColocated<TShape>();
        bool frameToFieldPair = directFrameToFieldPair;
        
        // Detect field→frame cross-type (current=field, L1[0]=frame).
        // Field→frame colZero uses dual-MB addressing: y8=0 reads from even-row frame MB,
        // y8=1 from odd-row frame MB, with cross-type offsets {0,3,8,11}.
        bool fieldToFrame = directFieldToFrame;
        sbyte[]? ftfColRefArr = directFtfColRef;
        short[]? ftfColMvXArr = directFtfColMvX, ftfColMvYArr = directFtfColMvY;
        sbyte[]? ftfColRefL1Arr = directFtfColRefL1;
        short[]? ftfColMvL1XArr = directFtfColMvL1X, ftfColMvL1YArr = directFtfColMvL1Y;
        bool[]? ftfMbFieldFlags = directFtfMbFieldFlags;

        // MBAFF per-MB cross-type detection (FFmpeg h264_direct.c lines 286-340).
        // In MBAFF frames, both current and L1[0] are frame pictures, but individual MBs
        // may be field-coded. FFmpeg checks IS_INTERLACED(col_mb_type[mb_xy]) per-MB.
        // Only the field flag is per macroblock; which picture to read it from was
        // settled for the slice.
        bool mbaffColIsField = false;
        bool mbaffCurIsField = false;
        int mbaffColMbAddr = mbAddr;
        if (TShape.Mbaff && directMbaffColocIndex >= 0)
        {
            mbaffCurIsField = mbFieldDecodingFlag != null && mbFieldDecodingFlag[mbAddr];

            bool[]? colFlags = dpb[directMbaffColocIndex].MbFieldFlags;
            if (colFlags != null && mbAddr < colFlags.Length)
            {
                mbaffColIsField = colFlags[mbAddr];

                if (mbaffColIsField && !mbaffCurIsField)
                {
                    // col=field, current=frame: pick the co-located field macroblock.
                    mbaffColMbAddr = mbX + ((mbY & ~1) + directMbaffColParity) * mbWidth;
                }
            }
        }

        bool mbaffCrossType = TShape.Mbaff && (mbaffColIsField != mbaffCurIsField);
        
        for(int i8 = 0; i8 < 4; i8++)
        {
            int subPos = Scan8[4 * i8];
            
            if (frameToFieldPair && needsColZeroCheck)
            {
                // Cross-type colZero: always per-8x8 with cross-type sub-block offsets.
                // FFmpeg uses b8_stride=0: two frame MB rows map to one field MB row.
                // Even frame row reads top half of field MB, odd reads bottom half.
                // Offset formula matches ffmpeg xy4 = x8*3 + y8*b4_stride with odd-row shift.
                bool isOddFrameRow = (mbY & 1) != 0;
                int crossTypeOffset = (isOddFrameRow ? 8 : 0) + (i8 >> 1) * 4 + (i8 & 1) * 3;
                bool blockColZero = GetColocatedZeroFlagFor4x4<TShape>(mbAddr, crossTypeOffset);
                short finalL0X = mvL0X, finalL0Y = mvL0Y;
                short finalL1X = mvL1X, finalL1Y = mvL1Y;
                if (blockColZero)
                {
                    if (derivedRefL0 == 0) { finalL0X = 0; finalL0Y = 0; }
                    if (derivedRefL1 == 0) { finalL1X = 0; finalL1Y = 0; }
                }
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, finalL0X, finalL0Y);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, finalL1X, finalL1Y);
            }
            else if (mbaffCrossType && needsColZeroCheck && mbaffColIsField && !mbaffCurIsField)
            {
                // MBAFF col=field, current=frame: b8_stride=0 cross-type
                // FFmpeg h264_direct.c lines 286-291, 348-355, 357-399
                // Even current pair row → top half of co-located field MB
                // Odd current pair row → bottom half of co-located field MB
                bool isOddMbRow = (mbY & 1) != 0;
                int crossTypeOffset = (isOddMbRow ? 8 : 0) + (i8 >> 1) * 4 + (i8 & 1) * 3;
                bool blockColZero = GetColocatedZeroFlagFor4x4<TShape>(mbaffColMbAddr, crossTypeOffset);
                short finalL0X2 = mvL0X, finalL0Y2 = mvL0Y;
                short finalL1X2 = mvL1X, finalL1Y2 = mvL1Y;
                if (blockColZero)
                {
                    if (derivedRefL0 == 0) { finalL0X2 = 0; finalL0Y2 = 0; }
                    if (derivedRefL1 == 0) { finalL1X2 = 0; finalL1Y2 = 0; }
                }
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, finalL0X2, finalL0Y2);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, finalL1X2, finalL1Y2);
            }
            else if (mbaffCrossType && needsColZeroCheck && !mbaffColIsField && mbaffCurIsField)
            {
                // MBAFF col=frame, current=field: dual-MB addressing
                // FFmpeg h264_direct.c lines 298-310, 357-399
                // y8=0 reads from top MB of co-located pair, y8=1 from bottom MB
                int y8 = i8 >> 1;
                int x8 = i8 & 1;
                int colMbAddrTop = mbX + (mbY & ~1) * mbWidth;
                int colAddr = (y8 == 0) ? colMbAddrTop : colMbAddrTop + mbWidth;
                int crossTypeOffset = (y8 == 0) ? (x8 * 3) : (8 + x8 * 3);
                bool blockColZero = GetColocatedZeroFlagFor4x4<TShape>(colAddr, crossTypeOffset);
                short finalL0X3 = mvL0X, finalL0Y3 = mvL0Y;
                short finalL1X3 = mvL1X, finalL1Y3 = mvL1Y;
                if (blockColZero)
                {
                    if (derivedRefL0 == 0) { finalL0X3 = 0; finalL0Y3 = 0; }
                    if (derivedRefL1 == 0) { finalL1X3 = 0; finalL1Y3 = 0; }
                }
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, finalL0X3, finalL0Y3);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, finalL1X3, finalL1Y3);
            }
            else if (fieldToFrame && needsColZeroCheck && ftfColRefArr != null)
            {
                // Field→frame spatial direct colZero (FFmpeg h264_direct.c lines 286-395).
                // PicAFF field MBs have IS_INTERLACED=1 (MB_TYPE_INTERLACED always set).
                // FFmpeg's sl->mb_y is in FRAME coordinates: bottom field row k → 2k+1.
                // mb_y & ~1 gives the frame pair base (2k), then dual-MB addressing.
                int fieldMbY = mbAddr / mbWidth;
                int fieldMbX = mbAddr % mbWidth;
                int framePairBaseY = fieldMbY * 2;
                int framePairBaseAddr = framePairBaseY * mbWidth + fieldMbX;
                
                bool colIsFieldCoded = ftfMbFieldFlags != null
                    && framePairBaseAddr < ftfMbFieldFlags.Length
                    && ftfMbFieldFlags[framePairBaseAddr];
                
                int colMbAddr;
                int blkOffset;
                
                if (colIsFieldCoded)
                {
                    // Col MB is field-coded in MBAFF: FL→AFL single_col with b8_stride=0
                    // (FFmpeg lines 286-296: mb_y = (sl->mb_y & ~1) + col_parity)
                    // Both current and col are interlaced → same type, NOT cross-type.
                    int colParity = (currentPictureStructure == ReferenceFrame.PictBottomField) ? 1 : 0;
                    colMbAddr = fieldMbX + (framePairBaseY + colParity) * mbWidth;
                    
                    // b8_stride=0: both y8 rows read from same 8x8 blocks
                    int x8 = i8 & 1;
                    // FFmpeg lines 348-354: if (sl->mb_y & 1) shift to bottom half.
                    // sl->mb_y for bottom field = 2k+1 (always odd), top field = 2k (always even).
                    int parityShift = (currentPictureStructure == ReferenceFrame.PictBottomField) ? 8 : 0;
                    blkOffset = x8 * 2 + parityShift;
                }
                else
                {
                    // Col MB is frame-coded: FL→FR/AFR cross-type with dual-MB addressing.
                    // FFmpeg lines 298-308: mb_y = sl->mb_y & ~1 (pair base), reads TWO MBs.
                    // b8_stride = 2+4*mb_stride, cross-type at line 357 (IS_INTERLACED differs).
                    // y8=0 reads from frame row 2*fieldMbY, y8=1 from 2*fieldMbY+1.
                    int y8 = i8 >> 1;
                    colMbAddr = (framePairBaseY + y8) * mbWidth + fieldMbX;
                    ReadOnlySpan<int> crossOffsets = CrossOffsets;
                    blkOffset = crossOffsets[i8];
                }
                
                int blkIdx = colMbAddr * 16 + blkOffset;
                
                bool blockColZero = false;
                if (blkIdx >= 0 && blkIdx < ftfColRefArr.Length)
                {
                    sbyte colRefL0 = ftfColRefArr[blkIdx];
                    if (colRefL0 == 0)
                    {
                        blockColZero = Math.Abs(ftfColMvXArr![blkIdx]) <= 1
                            && Math.Abs(ftfColMvYArr![blkIdx]) <= 1;
                    }
                    else if (colRefL0 < 0 && ftfColRefL1Arr != null)
                    {
                        sbyte colRefL1 = ftfColRefL1Arr[blkIdx];
                        if (colRefL1 == 0)
                            blockColZero = Math.Abs(ftfColMvL1XArr![blkIdx]) <= 1
                                && Math.Abs(ftfColMvL1YArr![blkIdx]) <= 1;
                    }
                }
                
                if (diagDirect)
                {
                    sbyte colR0 = blkIdx >= 0 && blkIdx < ftfColRefArr.Length ? ftfColRefArr[blkIdx] : (sbyte)-99;
                    short colMx = blkIdx >= 0 && blkIdx < ftfColMvXArr!.Length ? ftfColMvXArr[blkIdx] : (short)0;
                    short colMy = blkIdx >= 0 && blkIdx < ftfColMvYArr!.Length ? ftfColMvYArr[blkIdx] : (short)0;
                    DiagnosticOutput!.WriteLine($"  FTF 8x8[{i8}] colIsField={colIsFieldCoded} colMb={colMbAddr} blkOff={blkOffset} blkIdx={blkIdx} colRefL0={colR0} colMvL0=({colMx},{colMy}) colZero={blockColZero}");
                }
                
                short finalL0X = mvL0X, finalL0Y = mvL0Y;
                short finalL1X = mvL1X, finalL1Y = mvL1Y;
                if (blockColZero)
                {
                    if (derivedRefL0 == 0) { finalL0X = 0; finalL0Y = 0; }
                    if (derivedRefL1 == 0) { finalL1X = 0; finalL1Y = 0; }
                }
                if (diagDirect)
                    DiagnosticOutput!.WriteLine($"    → final L0=({finalL0X},{finalL0Y}) L1=({finalL1X},{finalL1Y})");
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, finalL0X, finalL0Y);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, finalL1X, finalL1Y);
            }
            else if (needsColZeroCheck)
            {
                if (direct8x8Inference)
                {
                    // Per-8x8 colZero: single check, uniform MV for all 4 4x4 blocks
                    bool blockColZero = GetColocatedZeroFlagForBlock<TShape>(mbAddr, i8);
                    short finalL0X = mvL0X, finalL0Y = mvL0Y;
                    short finalL1X = mvL1X, finalL1Y = mvL1Y;
                    if (blockColZero)
                    {
                        if (derivedRefL0 == 0) { finalL0X = 0; finalL0Y = 0; }
                        if (derivedRefL1 == 0) { finalL1X = 0; finalL1Y = 0; }
                    }
                    if (diagDirect)
                        DiagnosticOutput!.WriteLine($"  8x8[{i8}] colZero={blockColZero} final L0=({finalL0X},{finalL0Y}) L1=({finalL1X},{finalL1Y})");
                    FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, finalL0X, finalL0Y);
                    FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, finalL1X, finalL1Y);
                }
                else
                {
                    // Per-4x4 colZero: fill with derived MV first, then selectively zero
                    FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, mvL0X, mvL0Y);
                    FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, mvL1X, mvL1Y);
                    
                    int x8 = i8 & 1;
                    int y8 = i8 >> 1;
                    for (int i4 = 0; i4 < 4; i4++)
                    {
                        int colBlockOffset = x8 * 2 + (i4 & 1) + (y8 * 2 + (i4 >> 1)) * 4;
                        bool col4x4Zero = GetColocatedZeroFlagFor4x4<TShape>(mbAddr, colBlockOffset);
                        if (col4x4Zero)
                        {
                            int scan8Pos = Scan8[4 * i8 + i4];
                            if (derivedRefL0 == 0) { mvCacheL0X[scan8Pos] = 0; mvCacheL0Y[scan8Pos] = 0; }
                            if (derivedRefL1 == 0) { mvCacheL1X[scan8Pos] = 0; mvCacheL1Y[scan8Pos] = 0; }
                        }
                    }
                    if (diagDirect)
                    {
                        DiagnosticOutput!.Write($"  8x8[{i8}] per-4x4:");
                        for (int i4 = 0; i4 < 4; i4++)
                        {
                            int s = Scan8[4 * i8 + i4];
                            DiagnosticOutput.Write($" [{i4}]=L0({mvCacheL0X[s]},{mvCacheL0Y[s]})L1({mvCacheL1X[s]},{mvCacheL1Y[s]})");
                        }
                        DiagnosticOutput.WriteLine();
                    }
                }
            }
            else
            {
                if (diagDirect)
                    DiagnosticOutput!.WriteLine($"  8x8[{i8}] noColZeroCheck final L0=({mvL0X},{mvL0Y}) L1=({mvL1X},{mvL1Y})");
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, mvL0X, mvL0Y);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, mvL1X, mvL1Y);
            }
        }
        
        // Fill ref cache for the whole 16x16 MB
        FillRefCache(pos, 4, 4, derivedRefL0);
        FillRefCacheL1(pos, 4, 4, derivedRefL1);

        // Store actual derived ref_idx including -1 for unused lists.
        // The reconstruction path uses these to decide uni vs bi-prediction.
        StoreRefIdx16x16(mbAddr, 0, (sbyte)derivedRefL0);
        StoreRefIdx16x16(mbAddr, 1, (sbyte)derivedRefL1);
        
    }
    
    /// <summary>
    /// Derives motion vectors for B_Direct_8x8 sub-MBs within a B_8x8 macroblock.
    /// Uses spatial direct mode (H.264 spec 8.4.1.2.2) applied per-sub-8x8 block.
    /// Only fills cache positions for sub-MBs with sub_mb_type=0 (B_Direct_8x8).
    /// Must be called after PrepareMvCache/PrepareRefCache and inner-boundary markers.
    /// </summary>
    private void DeriveBDirect8x8SpatialMotionVectors<TShape>(int mbAddr, ReadOnlySpan<int> subMbTypes)
        where TShape : struct, IPictureShape
    {
        int pos = Scan8[0];
        int mbWidth = context.WidthInMacroblocks;
        int mbX = mbAddr % mbWidth;
        int mbY = mbAddr / mbWidth;
        
        // Derive ref_idx from MB-level border neighbors (same logic as B_Direct_16x16),
        // and keep the diagonal neighbour's vector rather than fetching it again below.
        int leftRefL0 = refCacheL0[pos - 1];
        int topRefL0 = refCacheL0[pos - 8];
        int diagRefL0 = FetchDiagonalMv<TShape>(refCacheL0, mvCacheL0X, mvCacheL0Y,
            pos, 4, 0, out short diagMvL0X, out short diagMvL0Y);
        int leftRefL1 = refCacheL1[pos - 1];
        int topRefL1 = refCacheL1[pos - 8];
        int diagRefL1 = FetchDiagonalMv<TShape>(refCacheL1, mvCacheL1X, mvCacheL1Y,
            pos, 4, 1, out short diagMvL1X, out short diagMvL1Y);
        
        int derivedRefL0 = DeriveDirectRefIdx(leftRefL0, topRefL0, diagRefL0);
        int derivedRefL1 = DeriveDirectRefIdx(leftRefL1, topRefL1, diagRefL1);
        
        // As in the 16x16 case: a list with no usable neighbour predicts zero, and is given
        // an index only afterwards, so the predictor can assume one neighbour carries it.
        short mvL0X = 0, mvL0Y = 0;
        if (derivedRefL0 >= 0)
            PredictDirectMotion(mvCacheL0X, mvCacheL0Y, pos, derivedRefL0,
                leftRefL0, topRefL0, diagRefL0, diagMvL0X, diagMvL0Y, out mvL0X, out mvL0Y);
        
        short mvL1X = 0, mvL1Y = 0;
        if (derivedRefL1 >= 0)
            PredictDirectMotion(mvCacheL1X, mvCacheL1Y, pos, derivedRefL1,
                leftRefL1, topRefL1, diagRefL1, diagMvL1X, diagMvL1Y, out mvL1X, out mvL1Y);
        
        if (derivedRefL0 < 0 && derivedRefL1 < 0)
        {
            derivedRefL0 = 0;
            derivedRefL1 = 0;
        }
        
        // ColZeroFlag check: per-8x8 when d8x8inf=true, per-4x4 when d8x8inf=false
        bool needsColZeroCheck = derivedRefL0 == 0 || derivedRefL1 == 0;
        bool direct8x8Inference = context.ActiveSps?.Direct8x8Inference ?? true;
        
        // Detect frame→field-pair cross-type (same as 16x16 variant)
        EnsureDirectColocated<TShape>();
        bool frameToFieldPair = directFrameToFieldPair;
        
        // Detect field→frame cross-type (current=field, L1[0]=frame)
        bool fieldToFrame = directFieldToFrame;
        sbyte[]? ftfColRefArr = directFtfColRef;
        short[]? ftfColMvXArr = directFtfColMvX, ftfColMvYArr = directFtfColMvY;
        sbyte[]? ftfColRefL1Arr = directFtfColRefL1;
        short[]? ftfColMvL1XArr = directFtfColMvL1X, ftfColMvL1YArr = directFtfColMvL1Y;
        bool[]? ftfMbFieldFlags = directFtfMbFieldFlags;

        // MBAFF per-MB cross-type detection (same as 16x16 variant)
        // Only the field flag is per macroblock; which picture to read it from was
        // settled for the slice.
        bool mbaffColIsField = false;
        bool mbaffCurIsField = false;
        int mbaffColMbAddr = mbAddr;
        if (TShape.Mbaff && directMbaffColocIndex >= 0)
        {
            mbaffCurIsField = mbFieldDecodingFlag != null && mbFieldDecodingFlag[mbAddr];

            bool[]? colFlags = dpb[directMbaffColocIndex].MbFieldFlags;
            if (colFlags != null && mbAddr < colFlags.Length)
            {
                mbaffColIsField = colFlags[mbAddr];

                if (mbaffColIsField && !mbaffCurIsField)
                {
                    // col=field, current=frame: pick the co-located field macroblock.
                    mbaffColMbAddr = mbX + ((mbY & ~1) + directMbaffColParity) * mbWidth;
                }
            }
        }

        bool mbaffCrossType = TShape.Mbaff && (mbaffColIsField != mbaffCurIsField);
        
        // Fill cache only for B_Direct_8x8 sub-MBs
        for (int i = 0; i < 4; i++)
        {
            if (subMbTypes[i] != 0) continue;
            
            int subPos = Scan8[4 * i];
            
            FillRefCache(subPos, 2, 2, derivedRefL0);
            FillRefCacheL1(subPos, 2, 2, derivedRefL1);
            StoreRefIdx(mbAddr, i, 0, (sbyte)derivedRefL0);
            StoreRefIdx(mbAddr, i, 1, (sbyte)derivedRefL1);
            
            if (frameToFieldPair && needsColZeroCheck)
            {
                // Cross-type colZero: per-8x8 with cross-type sub-block offsets
                bool isOddFrameRow = (mbY & 1) != 0;
                int crossTypeOffset = (isOddFrameRow ? 8 : 0) + (i >> 1) * 4 + (i & 1) * 3;
                bool blockColZero = GetColocatedZeroFlagFor4x4<TShape>(mbAddr, crossTypeOffset);
                short finalL0X = mvL0X, finalL0Y = mvL0Y;
                short finalL1X = mvL1X, finalL1Y = mvL1Y;
                if (blockColZero)
                {
                    if (derivedRefL0 == 0) { finalL0X = 0; finalL0Y = 0; }
                    if (derivedRefL1 == 0) { finalL1X = 0; finalL1Y = 0; }
                }
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, finalL0X, finalL0Y);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, finalL1X, finalL1Y);
            }
            else if (mbaffCrossType && needsColZeroCheck && mbaffColIsField && !mbaffCurIsField)
            {
                // MBAFF col=field, current=frame: b8_stride=0 cross-type
                bool isOddMbRow = (mbY & 1) != 0;
                int crossTypeOffset = (isOddMbRow ? 8 : 0) + (i >> 1) * 4 + (i & 1) * 3;
                bool blockColZero = GetColocatedZeroFlagFor4x4<TShape>(mbaffColMbAddr, crossTypeOffset);
                short finalL0X2 = mvL0X, finalL0Y2 = mvL0Y;
                short finalL1X2 = mvL1X, finalL1Y2 = mvL1Y;
                if (blockColZero)
                {
                    if (derivedRefL0 == 0) { finalL0X2 = 0; finalL0Y2 = 0; }
                    if (derivedRefL1 == 0) { finalL1X2 = 0; finalL1Y2 = 0; }
                }
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, finalL0X2, finalL0Y2);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, finalL1X2, finalL1Y2);
            }
            else if (mbaffCrossType && needsColZeroCheck && !mbaffColIsField && mbaffCurIsField)
            {
                // MBAFF col=frame, current=field: dual-MB addressing
                int y8 = i >> 1;
                int x8 = i & 1;
                int colMbAddrTop = mbX + (mbY & ~1) * mbWidth;
                int colAddr = (y8 == 0) ? colMbAddrTop : colMbAddrTop + mbWidth;
                int crossTypeOffset2 = (y8 == 0) ? (x8 * 3) : (8 + x8 * 3);
                bool blockColZero = GetColocatedZeroFlagFor4x4<TShape>(colAddr, crossTypeOffset2);
                short finalL0X3 = mvL0X, finalL0Y3 = mvL0Y;
                short finalL1X3 = mvL1X, finalL1Y3 = mvL1Y;
                if (blockColZero)
                {
                    if (derivedRefL0 == 0) { finalL0X3 = 0; finalL0Y3 = 0; }
                    if (derivedRefL1 == 0) { finalL1X3 = 0; finalL1Y3 = 0; }
                }
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, finalL0X3, finalL0Y3);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, finalL1X3, finalL1Y3);
            }
            else if (fieldToFrame && needsColZeroCheck && ftfColRefArr != null)
            {
                // Field→frame colZero (FFmpeg h264_direct.c lines 286-395).
                // PicAFF field MBs have IS_INTERLACED=1. sl->mb_y in frame coords.
                int fieldMbY = mbAddr / mbWidth;
                int fieldMbX = mbAddr % mbWidth;
                int framePairBaseY = fieldMbY * 2;
                int framePairBaseAddr = framePairBaseY * mbWidth + fieldMbX;
                
                bool colIsFieldCoded = ftfMbFieldFlags != null
                    && framePairBaseAddr < ftfMbFieldFlags.Length
                    && ftfMbFieldFlags[framePairBaseAddr];
                
                int colMbAddr;
                int blkOffset;
                
                if (colIsFieldCoded)
                {
                    // FL→AFL single_col: b8_stride=0, parity-based MB selection
                    int colParity = (currentPictureStructure == ReferenceFrame.PictBottomField) ? 1 : 0;
                    colMbAddr = fieldMbX + (framePairBaseY + colParity) * mbWidth;
                    int x8 = i & 1;
                    int parityShift = (currentPictureStructure == ReferenceFrame.PictBottomField) ? 8 : 0;
                    blkOffset = x8 * 2 + parityShift;
                }
                else
                {
                    // FL→FR/AFR cross-type: dual-MB addressing
                    int y8 = i >> 1;
                    colMbAddr = (framePairBaseY + y8) * mbWidth + fieldMbX;
                    ReadOnlySpan<int> crossOffsets = CrossOffsets;
                    blkOffset = crossOffsets[i];
                }
                
                int blkIdx = colMbAddr * 16 + blkOffset;
                
                bool blockColZero = false;
                if (blkIdx >= 0 && blkIdx < ftfColRefArr.Length)
                {
                    sbyte colRefL0 = ftfColRefArr[blkIdx];
                    if (colRefL0 == 0)
                    {
                        blockColZero = Math.Abs(ftfColMvXArr![blkIdx]) <= 1
                            && Math.Abs(ftfColMvYArr![blkIdx]) <= 1;
                    }
                    else if (colRefL0 < 0 && ftfColRefL1Arr != null)
                    {
                        sbyte colRefL1 = ftfColRefL1Arr[blkIdx];
                        if (colRefL1 == 0)
                            blockColZero = Math.Abs(ftfColMvL1XArr![blkIdx]) <= 1
                                && Math.Abs(ftfColMvL1YArr![blkIdx]) <= 1;
                    }
                }
                
                short finalL0X = mvL0X, finalL0Y = mvL0Y;
                short finalL1X = mvL1X, finalL1Y = mvL1Y;
                if (blockColZero)
                {
                    if (derivedRefL0 == 0) { finalL0X = 0; finalL0Y = 0; }
                    if (derivedRefL1 == 0) { finalL1X = 0; finalL1Y = 0; }
                }
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, finalL0X, finalL0Y);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, finalL1X, finalL1Y);
            }
            else if (needsColZeroCheck && direct8x8Inference)
            {
                // Per-8x8 colZero: single check, uniform MV for all 4 4x4 blocks
                short finalL0X = mvL0X, finalL0Y = mvL0Y;
                short finalL1X = mvL1X, finalL1Y = mvL1Y;
                bool blockColZero = GetColocatedZeroFlagForBlock<TShape>(mbAddr, i);
                if (blockColZero)
                {
                    if (derivedRefL0 == 0) { finalL0X = 0; finalL0Y = 0; }
                    if (derivedRefL1 == 0) { finalL1X = 0; finalL1Y = 0; }
                }
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, finalL0X, finalL0Y);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, finalL1X, finalL1Y);
            }
            else if (needsColZeroCheck)
            {
                // Per-4x4 colZero: fill with derived MV first, then selectively zero
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, mvL0X, mvL0Y);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, mvL1X, mvL1Y);
                
                int x8 = i & 1;
                int y8 = i >> 1;
                for (int i4 = 0; i4 < 4; i4++)
                {
                    int colBlockOffset = x8 * 2 + (i4 & 1) + (y8 * 2 + (i4 >> 1)) * 4;
                    bool col4x4Zero = GetColocatedZeroFlagFor4x4<TShape>(mbAddr, colBlockOffset);
                    if (col4x4Zero)
                    {
                        int scan8Pos = Scan8[4 * i + i4];
                        if (derivedRefL0 == 0) { mvCacheL0X[scan8Pos] = 0; mvCacheL0Y[scan8Pos] = 0; }
                        if (derivedRefL1 == 0) { mvCacheL1X[scan8Pos] = 0; mvCacheL1Y[scan8Pos] = 0; }
                    }
                }
            }
            else
            {
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, mvL0X, mvL0Y);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, mvL1X, mvL1Y);
            }
        }
    }
    
    /// <summary>
    /// Derives B_Direct_8x8 motion vectors using temporal prediction (H.264 spec 8.4.1.2.3).
    /// Per-sub-MB variant: only fills cache for sub-MBs with sub_mb_type == 0 (B_Direct).
    /// Reads per-4x4-block co-located data from L1[0] reference for accurate temporal scaling.
    /// </summary>
    private void DeriveBDirect8x8TemporalMotionVectors<TShape>(int mbAddr, ReadOnlySpan<int> subMbTypes)
        where TShape : struct, IPictureShape
    {
        if (refPicListL1Count == 0)
        {
            for (int i = 0; i < 4; i++)
            {
                if (subMbTypes[i] != 0) continue;
                int subPos = Scan8[4 * i];
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, 0, 0);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, 0, 0);
                FillRefCache(subPos, 2, 2, 0);
                FillRefCacheL1(subPos, 2, 2, 0);
                StoreRefIdx(mbAddr, i, 0, 0);
                StoreRefIdx(mbAddr, i, 1, 0);
            }
            return;
        }
        
        // Find the co-located reference in DPB (L1[0])
        int l1Poc = refPicListL1Poc[0];
        int colDpbIdx = -1;
        for (int di = 0; di < dpbCount; di++)
        {
            if (dpb[di].ColocatedRefIdx == null) continue;
            if (currentFieldPicture)
            {
                if (dpb[di].TopFieldPoc == l1Poc || dpb[di].BottomFieldPoc == l1Poc)
                {
                    colDpbIdx = di;
                    break;
                }
            }
            else if (dpb[di].Poc == l1Poc)
            {
                colDpbIdx = di;
                break;
            }
        }
        
        // Resolve per-field collocated arrays
        short[]? colMvXArr = null, colMvYArr = null, colMvL1XArr = null, colMvL1YArr = null;
        sbyte[]? colRefArr = null, colRefL1Arr = null;
        int[]? colL0PocsArr = null, colL1PocsArr = null;
        int colL0CountVal = 0, colL1CountVal = 0;
        bool[]? colL0IsTopArr = null, colL1IsTopArr = null;
        int colParity8x8 = 0; // col_parity for frame→field-pair half-MB offset
        
        if (colDpbIdx >= 0)
        {
            ref var colFrame = ref dpb[colDpbIdx];
            if (currentFieldPicture && colFrame.FieldPicture && colFrame.TopFieldColocRefIdx != null)
            {
                // Field→field: use L1[0]'s actual field parity to select collocated arrays.
                // POC alone is ambiguous when both fields share the same POC value.
                bool useTopField = refPicListL1IsTopField[0];
                if (useTopField)
                {
                    colMvXArr = colFrame.TopFieldColocMvX; colMvYArr = colFrame.TopFieldColocMvY;
                    colRefArr = colFrame.TopFieldColocRefIdx;
                    colMvL1XArr = colFrame.TopFieldColocMvL1X; colMvL1YArr = colFrame.TopFieldColocMvL1Y;
                    colRefL1Arr = colFrame.TopFieldColocRefIdxL1;
                    colL0PocsArr = colFrame.TopFieldColocL0Pocs; colL0CountVal = colFrame.TopFieldColocL0Count;
                    colL1PocsArr = colFrame.TopFieldColocL1Pocs; colL1CountVal = colFrame.TopFieldColocL1Count;
                    colL0IsTopArr = colFrame.TopFieldColocL0IsTop; colL1IsTopArr = colFrame.TopFieldColocL1IsTop;
                }
                else
                {
                    colMvXArr = colFrame.BotFieldColocMvX; colMvYArr = colFrame.BotFieldColocMvY;
                    colRefArr = colFrame.BotFieldColocRefIdx;
                    colMvL1XArr = colFrame.BotFieldColocMvL1X; colMvL1YArr = colFrame.BotFieldColocMvL1Y;
                    colRefL1Arr = colFrame.BotFieldColocRefIdxL1;
                    colL0PocsArr = colFrame.BotFieldColocL0Pocs; colL0CountVal = colFrame.BotFieldColocL0Count;
                    colL1PocsArr = colFrame.BotFieldColocL1Pocs; colL1CountVal = colFrame.BotFieldColocL1Count;
                    colL0IsTopArr = colFrame.BotFieldColocL0IsTop; colL1IsTopArr = colFrame.BotFieldColocL1IsTop;
                }
            }
            else if (!currentFieldPicture && colFrame.FieldPicture && colFrame.TopFieldColocRefIdx != null)
            {
                // Frame→field-pair cross-type: FFmpeg sets MB_TYPE_INTERLACED on non-MBAFF
                // field MBs (via MB_FIELD), so the cross-type path IS entered.
                // col_parity selects ONE field (closest in POC) for ALL frame MBs.
                int curPoc = currentPoc;
                long topDist = Math.Abs((long)colFrame.TopFieldPoc - curPoc);
                long botDist = Math.Abs((long)colFrame.BottomFieldPoc - curPoc);
                bool useTopField = topDist < botDist; // FFmpeg: col_parity = (abs(top-cur) >= abs(bot-cur)), equal → bottom
                colParity8x8 = useTopField ? 0 : 1;
                
                if (useTopField)
                {
                    colMvXArr = colFrame.TopFieldColocMvX; colMvYArr = colFrame.TopFieldColocMvY;
                    colRefArr = colFrame.TopFieldColocRefIdx;
                    colMvL1XArr = colFrame.TopFieldColocMvL1X; colMvL1YArr = colFrame.TopFieldColocMvL1Y;
                    colRefL1Arr = colFrame.TopFieldColocRefIdxL1;
                    colL0PocsArr = colFrame.TopFieldColocL0Pocs; colL0CountVal = colFrame.TopFieldColocL0Count;
                    colL1PocsArr = colFrame.TopFieldColocL1Pocs; colL1CountVal = colFrame.TopFieldColocL1Count;
                    colL0IsTopArr = colFrame.TopFieldColocL0IsTop; colL1IsTopArr = colFrame.TopFieldColocL1IsTop;
                }
                else
                {
                    colMvXArr = colFrame.BotFieldColocMvX; colMvYArr = colFrame.BotFieldColocMvY;
                    colRefArr = colFrame.BotFieldColocRefIdx;
                    colMvL1XArr = colFrame.BotFieldColocMvL1X; colMvL1YArr = colFrame.BotFieldColocMvL1Y;
                    colRefL1Arr = colFrame.BotFieldColocRefIdxL1;
                    colL0PocsArr = colFrame.BotFieldColocL0Pocs; colL0CountVal = colFrame.BotFieldColocL0Count;
                    colL1PocsArr = colFrame.BotFieldColocL1Pocs; colL1CountVal = colFrame.BotFieldColocL1Count;
                    colL0IsTopArr = colFrame.BotFieldColocL0IsTop; colL1IsTopArr = colFrame.BotFieldColocL1IsTop;
                }
            }
            else if (currentFieldPicture && !colFrame.FieldPicture)
            {
                // Field→frame: collocated picture is a frame, use generic collocated data
                colMvXArr = colFrame.ColocatedMvX; colMvYArr = colFrame.ColocatedMvY;
                colRefArr = colFrame.ColocatedRefIdx;
                colMvL1XArr = colFrame.ColocatedMvL1X; colMvL1YArr = colFrame.ColocatedMvL1Y;
                colRefL1Arr = colFrame.ColocatedRefIdxL1;
                colL0PocsArr = colFrame.ColocatedL0Pocs; colL0CountVal = colFrame.ColocatedL0Count;
                colL1PocsArr = colFrame.ColocatedL1Pocs; colL1CountVal = colFrame.ColocatedL1Count;
            }
            else
            {
                colMvXArr = colFrame.ColocatedMvX; colMvYArr = colFrame.ColocatedMvY;
                colRefArr = colFrame.ColocatedRefIdx;
                colMvL1XArr = colFrame.ColocatedMvL1X; colMvL1YArr = colFrame.ColocatedMvL1Y;
                colRefL1Arr = colFrame.ColocatedRefIdxL1;
                colL0PocsArr = colFrame.ColocatedL0Pocs; colL0CountVal = colFrame.ColocatedL0Count;
                colL1PocsArr = colFrame.ColocatedL1Pocs; colL1CountVal = colFrame.ColocatedL1Count;
            }
        }
        
        // Track frame→field-pair for cross-type MV handling (Y-doubling + special block addressing).
        // Non-MBAFF field MBs have MB_TYPE_INTERLACED set via MB_FIELD, so cross-type IS needed.
        bool frameToFieldPair = colDpbIdx >= 0 && !currentFieldPicture && dpb[colDpbIdx].FieldPicture;
        // Track field→frame for MV Y halving and colBlock remapping
        bool fieldToFrame = colDpbIdx >= 0 && currentFieldPicture && !dpb[colDpbIdx].FieldPicture;
        
        // Early detection of MBAFF per-MB field status for cross-type handling.
        bool isMbaffFieldMb = TShape.Mbaff && mbFieldDecodingFlag != null && mbFieldDecodingFlag[mbAddr];
        bool earlyColMbIsField = false;
        if (colDpbIdx >= 0)
        {
            var colFieldFlags = dpb[colDpbIdx].MbFieldFlags;
            if (colFieldFlags != null && mbAddr < colFieldFlags.Length)
                earlyColMbIsField = colFieldFlags[mbAddr];
        }
        
        // MBAFF field-coded MB + field pair collocated: both are field-level, NOT cross-type.
        // Same logic as B_Direct_16x16 — select per-MB field, normal block offsets, no Y-doubling.
        bool mbaffFieldToFieldPair = frameToFieldPair && isMbaffFieldMb;
        if (mbaffFieldToFieldPair)
            frameToFieldPair = false;
        
        // MBAFF intra-frame cross-type
        bool mbaffFrameToField = !frameToFieldPair && !fieldToFrame && !mbaffFieldToFieldPair && !isMbaffFieldMb && earlyColMbIsField;
        bool mbaffFieldToFrame = !frameToFieldPair && !fieldToFrame && !mbaffFieldToFieldPair && isMbaffFieldMb && !earlyColMbIsField;
        
        // Compute collocated block base for 8x8 sub-partitions.
        // For frame→field-pair: two frame MB rows map to one field MB.
        // colBlockBase addresses the field MB; isOddFrameRow selects bottom half.
        int colBlockBase8x8 = mbAddr * 16;
        bool isOddFrameRow8x8 = false;
        int fieldToFrameEvenBase8x8 = -1, fieldToFrameOddBase8x8 = -1;
        bool fieldToFrameColIsField8x8 = false;
        int mbaffColParity = 0;
        int mbaffCurRowParity8x8 = 0; // current MB's position in MBAFF pair: 0=top, 1=bottom
        
        if (mbaffFieldToFieldPair)
        {
            // MBAFF field-coded MB → field pair collocated: NOT cross-type!
            // FFmpeg h264_direct.c:510-513: col_fieldoff=0 for MBAFF, b8_stride stays at default (2).
            // Standard block addressing. Field-level scaling via dist_scale_factor_field.
            int mbWidth = context.WidthInMacroblocks;
            int mbY = mbAddr / mbWidth;
            int mbX = mbAddr % mbWidth;
            int fieldMbAddr = (mbY >> 1) * mbWidth + mbX;
            colBlockBase8x8 = fieldMbAddr * 16;
            mbaffCurRowParity8x8 = mbY & 1;
            
            // FFmpeg: mb_xy naturally selects the correct collocated field per-MB
            // (even row → top field data, odd row → bottom field data).
            // Re-select collocated arrays based on current MB's field parity.
            ref var colFrameF2F = ref dpb[colDpbIdx];
            if (mbaffCurRowParity8x8 == 0)
            {
                colMvXArr = colFrameF2F.TopFieldColocMvX;
                colMvYArr = colFrameF2F.TopFieldColocMvY;
                colRefArr = colFrameF2F.TopFieldColocRefIdx;
                colMvL1XArr = colFrameF2F.TopFieldColocMvL1X;
                colMvL1YArr = colFrameF2F.TopFieldColocMvL1Y;
                colRefL1Arr = colFrameF2F.TopFieldColocRefIdxL1;
                colL0PocsArr = colFrameF2F.TopFieldColocL0Pocs;
                colL0CountVal = colFrameF2F.TopFieldColocL0Count;
                colL1PocsArr = colFrameF2F.TopFieldColocL1Pocs;
                colL1CountVal = colFrameF2F.TopFieldColocL1Count;
                colL0IsTopArr = colFrameF2F.TopFieldColocL0IsTop;
                colL1IsTopArr = colFrameF2F.TopFieldColocL1IsTop;
            }
            else
            {
                colMvXArr = colFrameF2F.BotFieldColocMvX;
                colMvYArr = colFrameF2F.BotFieldColocMvY;
                colRefArr = colFrameF2F.BotFieldColocRefIdx;
                colMvL1XArr = colFrameF2F.BotFieldColocMvL1X;
                colMvL1YArr = colFrameF2F.BotFieldColocMvL1Y;
                colRefL1Arr = colFrameF2F.BotFieldColocRefIdxL1;
                colL0PocsArr = colFrameF2F.BotFieldColocL0Pocs;
                colL0CountVal = colFrameF2F.BotFieldColocL0Count;
                colL1PocsArr = colFrameF2F.BotFieldColocL1Pocs;
                colL1CountVal = colFrameF2F.BotFieldColocL1Count;
                colL0IsTopArr = colFrameF2F.BotFieldColocL0IsTop;
                colL1IsTopArr = colFrameF2F.BotFieldColocL1IsTop;
            }
        }
        else if (frameToFieldPair)
        {
            int mbWidth = context.WidthInMacroblocks;
            int mbY = mbAddr / mbWidth;
            int mbX = mbAddr % mbWidth;
            // Both even and odd frame rows map to the same field MB row
            int fieldMbAddr = (mbY >> 1) * mbWidth + mbX;
            colBlockBase8x8 = fieldMbAddr * 16;
            // FFmpeg h264_direct.c:573: half-MB offset uses sl->mb_y & 1 (original row parity).
            isOddFrameRow8x8 = (mbY & 1) != 0;
        }
        else if (fieldToFrame)
        {
            // Same fix as 16x16 path: use spec-level address mapping (fieldMbY & ~1),
            // not spatial mapping (fieldMbY * 2). See H.264 spec §8.4.1.2.3.
            int mbWidth = context.WidthInMacroblocks;
            int fieldMbY = mbAddr / mbWidth;
            int mbX = mbAddr % mbWidth;
            int framePairBaseY = fieldMbY * 2;
            fieldToFrameEvenBase8x8 = (framePairBaseY * mbWidth + mbX) * 16;
            fieldToFrameOddBase8x8 = ((framePairBaseY + 1) * mbWidth + mbX) * 16;
            
            // Check if collocated MB at frame pair base is field-coded → single MB mode.
            if (colDpbIdx >= 0)
            {
                var colFieldFlags = dpb[colDpbIdx].MbFieldFlags;
                int colCheckMbAddr = framePairBaseY * mbWidth + mbX;
                if (colFieldFlags != null && colCheckMbAddr < colFieldFlags.Length && colFieldFlags[colCheckMbAddr])
                {
                    fieldToFrameColIsField8x8 = true;
                    // FFmpeg: goto single_col at sl->mb_y (frame-level row from field parity).
                    // Bottom field → odd frame row → bottom MB of pair.
                    // Top field → even frame row → top MB of pair.
                    int fieldParity = currentPictureStructure == ReferenceFrame.PictBottomField ? 1 : 0;
                    int selectedBase = fieldParity == 0 ? fieldToFrameEvenBase8x8 : fieldToFrameOddBase8x8;
                    fieldToFrameEvenBase8x8 = selectedBase;
                    fieldToFrameOddBase8x8 = selectedBase;
                }
            }
        }
        else if (mbaffFrameToField)
        {
            int mbWidth = context.WidthInMacroblocks;
            long topDist = Math.Abs((long)dpb[colDpbIdx].TopFieldPoc - currentPoc);
            long botDist = Math.Abs((long)dpb[colDpbIdx].BottomFieldPoc - currentPoc);
            mbaffColParity = (topDist >= botDist) ? 1 : 0;
            
            int mbY = mbAddr / mbWidth;
            int mbX = mbAddr % mbWidth;
            int selectedColRow = (mbY & ~1) | mbaffColParity;
            colBlockBase8x8 = (selectedColRow * mbWidth + mbX) * 16;
            // FFmpeg h264_direct.c:572-578: within-MB half offset uses sl->mb_y & 1
            mbaffCurRowParity8x8 = mbY & 1;
        }
        else if (mbaffFieldToFrame)
        {
            int mbWidth = context.WidthInMacroblocks;
            int mbY = mbAddr / mbWidth;
            int mbX = mbAddr % mbWidth;
            int evenMbY = mbY & ~1;
            fieldToFrameEvenBase8x8 = (evenMbY * mbWidth + mbX) * 16;
            fieldToFrameOddBase8x8 = ((evenMbY + 1) * mbWidth + mbX) * 16;
        }
        
        bool direct8x8Inference = context.ActiveSps?.Direct8x8Inference ?? true;
        ReadOnlySpan<int> colocOffsets = ColocOffsets;
        
        for (int i = 0; i < 4; i++)
        {
            if (subMbTypes[i] != 0) continue;
            int subPos = Scan8[4 * i];
            
            int colRefPoc = 0;
            int mappedL0Ref = 0;
            bool isIntra = true;
            sbyte repColRef = -1;
            
            // Compute per-i8 cross-type collocated indices (separate ref and MV).
            // For frame→field-pair: b8_stride=0 collapses ref y8; odd frame row reads bottom half.
            // For field→frame: use even/odd frame MB bases per y8.
            int refColBlkIdx8, mvColBlkIdx8;
            if (frameToFieldPair)
            {
                // FFmpeg cross-type: b8_stride=0, ref uses x8 only (no y8).
                // Ref: even row → offsets {0,3} (TL,TR); odd row → offsets {12,15} (BL,BR).
                // MV with d8x8: even row → {0,3,4,7}; odd row → {8,11,12,15}.
                int x8 = i & 1;
                int y8 = i >> 1;
                if (!isOddFrameRow8x8)
                {
                    // Even frame row → top half of field MB
                    refColBlkIdx8 = colBlockBase8x8 + (x8 == 0 ? 0 : 3);
                    mvColBlkIdx8 = colBlockBase8x8 + x8 * 3 + y8 * 4;
                }
                else
                {
                    // Odd frame row → bottom half of field MB
                    refColBlkIdx8 = colBlockBase8x8 + (x8 == 0 ? 12 : 15);
                    mvColBlkIdx8 = colBlockBase8x8 + 8 + x8 * 3 + y8 * 4;
                }
            }
            else if (fieldToFrame)
            {
                if (fieldToFrameColIsField8x8)
                {
                    // Collocated pair is field-coded → FFmpeg single_col with b8_stride=2.
                    // Both IS_INTERLACED match → standard one-to-one path, not cross-type.
                    // Selected base already points to the correct MB (top/bottom per field parity).
                    refColBlkIdx8 = fieldToFrameEvenBase8x8 + colocOffsets[i];
                    mvColBlkIdx8 = refColBlkIdx8;
                }
                else
                {
                    int y8 = i >> 1;
                    int perI8Offset = i switch { 0 => 0, 1 => 3, 2 => 8, _ => 11 };
                    int perI8Base = y8 == 0 ? fieldToFrameEvenBase8x8 : fieldToFrameOddBase8x8;
                    refColBlkIdx8 = perI8Base + perI8Offset;
                    mvColBlkIdx8 = refColBlkIdx8;
                }
            }
            else if (mbaffFrameToField)
            {
                // FFmpeg h264_direct.c:572-578: within-MB half offset uses sl->mb_y & 1
                int x8 = i & 1;
                int y8 = i >> 1;
                if (mbaffCurRowParity8x8 == 0)
                {
                    refColBlkIdx8 = colBlockBase8x8 + (x8 == 0 ? 0 : 3);
                    mvColBlkIdx8 = colBlockBase8x8 + x8 * 3 + y8 * 4;
                }
                else
                {
                    refColBlkIdx8 = colBlockBase8x8 + (x8 == 0 ? 12 : 15);
                    mvColBlkIdx8 = colBlockBase8x8 + 8 + x8 * 3 + y8 * 4;
                }
            }
            else if (mbaffFieldToFrame)
            {
                int y8 = i >> 1;
                int perI8Offset = i switch { 0 => 0, 1 => 3, 2 => 8, _ => 11 };
                int perI8Base = y8 == 0 ? fieldToFrameEvenBase8x8 : fieldToFrameOddBase8x8;
                refColBlkIdx8 = perI8Base + perI8Offset;
                mvColBlkIdx8 = refColBlkIdx8;
            }
            else if (mbaffFieldToFieldPair)
            {
                // MBAFF field MB → field picture: NOT cross-type (both IS_INTERLACED).
                // FFmpeg h264_direct.c:510-513 → goto single_col → standard b8_stride (no half-MB).
                // Field picture stores one field per MB, so standard per-8x8 block addressing.
                refColBlkIdx8 = colBlockBase8x8 + colocOffsets[i];
                mvColBlkIdx8 = refColBlkIdx8;
            }
            else
            {
                refColBlkIdx8 = colBlockBase8x8 + colocOffsets[i];
                mvColBlkIdx8 = refColBlkIdx8;
            }
            
            bool colMbIsField = false;
            // Determine collocated MB's field/frame coding status FIRST, independently of ref index.
            // This affects L1 ref halving, Y MV scaling, and reference mapping even when L0 ref is -1.
            if (colDpbIdx >= 0)
            {
                var colFieldFlagsCheck = dpb[colDpbIdx].MbFieldFlags;
                int colMbAddrCheck = refColBlkIdx8 / 16;
                if (colFieldFlagsCheck != null && colMbAddrCheck < colFieldFlagsCheck.Length && colFieldFlagsCheck[colMbAddrCheck])
                    colMbIsField = true;
            }
            if (colDpbIdx >= 0 && colRefArr != null)
            {
                if (refColBlkIdx8 < colRefArr.Length)
                {
                    repColRef = colRefArr[refColBlkIdx8];
                    
                    // Collocated MBAFF frames have field-coded MBs with field-level ref indices (0..2N-1),
                    // but colL0PocsArr has only N frame-level entries. Convert for POC lookup.
                    int pocLookupRef = repColRef;
                    if (colMbIsField && repColRef >= 0)
                        pocLookupRef = repColRef >> 1;
                    
                    if (repColRef >= 0)
                    {
                        isIntra = false;
                        if (colL0PocsArr != null && pocLookupRef < colL0CountVal)
                            colRefPoc = colL0PocsArr[pocLookupRef];
                        else if (pocLookupRef < refPicListL0Count)
                            colRefPoc = refPicListL0Poc[pocLookupRef];
                        else
                            colRefPoc = refPicListL0Poc[0];
                    }
                    else if (colRefL1Arr != null)
                    {
                        sbyte colRefL1Val = colRefL1Arr[refColBlkIdx8];
                        if (colRefL1Val >= 0)
                        {
                            isIntra = false;
                            // MBAFF: same conversion for L1 ref of field-coded collocated MBs
                            int l1PocLookup = colRefL1Val;
                            if (colMbIsField) l1PocLookup = colRefL1Val >> 1;
                            
                            if (colL1PocsArr != null && l1PocLookup < colL1CountVal)
                                colRefPoc = colL1PocsArr[l1PocLookup];
                            else if (colL0PocsArr != null && colL0CountVal > 0)
                                colRefPoc = colL0PocsArr[0];
                            else
                                colRefPoc = refPicListL1Poc[0];
                        }
                    }
                    
                    // Determine collocated reference's field parity for field-aware matching.
                    // When both fields share the same POC, parity disambiguates the mapping.
                    int colRefFieldId = -1; // -1 = unknown parity
                    if (currentFieldPicture && !isIntra)
                    {
                        int fieldLookup = repColRef >= 0 ? pocLookupRef : -1;
                        if (fieldLookup >= 0 && colL0IsTopArr != null && fieldLookup < colL0CountVal)
                            colRefFieldId = colL0IsTopArr[fieldLookup] ? 1 : 2; // 1=top, 2=bot
                        else if (repColRef < 0 && colRefL1Arr != null)
                        {
                            sbyte colRefL1Val2 = colRefL1Arr[refColBlkIdx8];
                            int l1FieldLookup = colMbIsField ? (colRefL1Val2 >> 1) : colRefL1Val2;
                            if (colRefL1Val2 >= 0 && colL1IsTopArr != null && l1FieldLookup < colL1CountVal)
                                colRefFieldId = colL1IsTopArr[l1FieldLookup] ? 1 : 2;
                        }
                    }
                    
                    if (!isIntra)
                    {
                        // Map colRefPoc to current L0 index, handling cross-type POC matching
                        if (frameToFieldPair || mbaffFieldToFieldPair)
                        {
                            // colRefPoc is field-level; find frame-level POC via DPB
                            int targetFramePoc = colRefPoc;
                            for (int k = 0; k < dpbCount; k++)
                            {
                                if (dpb[k].TopFieldPoc == colRefPoc || dpb[k].BottomFieldPoc == colRefPoc)
                                {
                                    targetFramePoc = dpb[k].Poc;
                                    break;
                                }
                            }
                            for (int li = 0; li < refPicListL0Count; li++)
                            {
                                if (refPicListL0Poc[li] == targetFramePoc)
                                {
                                    mappedL0Ref = li;
                                    break;
                                }
                            }
                        }
                        else if (currentFieldPicture && colDpbIdx >= 0 && !dpb[colDpbIdx].FieldPicture)
                        {
                            // Field→frame: match by DPB frame-level POC.
                            // When col MB is field-coded in MBAFF, determine which field
                            // the collocated reference points to.
                            // FFmpeg fill_colmap: map[2*old_ref + (rfield^field) + 16] = cur_ref
                            // The MBAFF ref_list always has top=even/bottom=odd offsets, but
                            // the map table XORs with the current field parity. So for bottom
                            // field current: odd colRef → top field, even colRef → bottom field.
                            // This means: wantTop = ((colRef & 1) ^ currentFieldParity) == 0
                            bool fieldParityKnown = false;
                            bool wantTopField = false;
                            if (colMbIsField && repColRef >= 0)
                            {
                                int curFieldParity = currentPictureStructure == ReferenceFrame.PictBottomField ? 1 : 0;
                                wantTopField = ((repColRef & 1) ^ curFieldParity) == 0;
                                fieldParityKnown = true;
                            }
                            
                            bool fi2fMatched = false;
                            // Try parity-aware match first
                            if (fieldParityKnown)
                            {
                                for (int li = 0; li < refPicListL0Count; li++)
                                {
                                    if (refPicListL0DpbPoc[li] == colRefPoc &&
                                        refPicListL0IsTopField[li] == wantTopField)
                                    {
                                        mappedL0Ref = li;
                                        fi2fMatched = true;
                                        break;
                                    }
                                }
                            }
                            
                            // Fallback: match by DPB POC only (frame-coded col, or parity match failed)
                            if (!fi2fMatched)
                            {
                                for (int li = 0; li < refPicListL0Count; li++)
                                {
                                    if (refPicListL0DpbPoc[li] == colRefPoc)
                                    {
                                        mappedL0Ref = li;
                                        break;
                                    }
                                }
                            }
                        }
                        else
                        {
                            bool matchFound = false;
                            // For field pictures with known collocated ref parity,
                            // match on both POC and field parity (FFmpeg uses 4*frame_num + parity).
                            // This disambiguates when both fields share the same POC.
                            if (colRefFieldId > 0)
                            {
                                bool wantTop = colRefFieldId == 1;
                                for (int li = 0; li < refPicListL0Count; li++)
                                {
                                    if (refPicListL0Poc[li] == colRefPoc && refPicListL0IsTopField[li] == wantTop)
                                    {
                                        mappedL0Ref = li;
                                        matchFound = true;
                                        break;
                                    }
                                }
                            }
                            if (!matchFound)
                            {
                                for (int li = 0; li < refPicListL0Count; li++)
                                {
                                    if (refPicListL0Poc[li] == colRefPoc)
                                    {
                                        mappedL0Ref = li;
                                        matchFound = true;
                                        break;
                                    }
                                }
                            }
                            if (!matchFound && DiagnosticOutput != null)
                            {
                                DiagnosticOutput.Write($"TD8x8-POCMISS: poc={currentPoc} mb={mbAddr} i8={i} colRef={repColRef} colRefPoc={colRefPoc} L0[{refPicListL0Count}]=");
                                for (int li = 0; li < refPicListL0Count; li++) DiagnosticOutput.Write($"{refPicListL0Poc[li]} ");
                                DiagnosticOutput.WriteLine();
                            }
                        }
                    }
                }
            }
            
            // Diagnostic: show temporal direct mapping details for target POC
            if (DiagnosticOutput != null && currentPoc == DiagnosticDumpAllMbsForPoc && i == 0)
            {
                string mapPath = (frameToFieldPair || mbaffFieldToFieldPair) ? "fi2fp" :
                    (currentFieldPicture && colDpbIdx >= 0 && !dpb[colDpbIdx].FieldPicture) ? "field2frame" : "else";
                DiagnosticOutput.WriteLine($"TD-MAP: poc={currentPoc} mb={mbAddr} i8={i} path={mapPath} " +
                    $"colRef={repColRef} colRefPoc={colRefPoc} mapped={mappedL0Ref} " +
                    $"isIntra={isIntra} colMbIsField={colMbIsField} colFldPic={(colDpbIdx >= 0 ? dpb[colDpbIdx].FieldPicture : false)} " +
                    $"fi2f={frameToFieldPair} fi2frame={fieldToFrame} mbaff={isMbaffFieldMb}");
                // Dump all L0 refs for diagnosis
                for (int li = 0; li < refPicListL0Count; li++)
                    DiagnosticOutput.WriteLine($"  L0[{li}]: poc={refPicListL0Poc[li]} isTop={refPicListL0IsTopField[li]}");
                // Dump collocated arrays info
                if (colL0PocsArr != null)
                {
                    DiagnosticOutput.Write($"  COL-L0-POCS[{colL0CountVal}]:");
                    for (int ci = 0; ci < colL0CountVal; ci++) DiagnosticOutput.Write($" {colL0PocsArr[ci]}");
                    DiagnosticOutput.WriteLine();
                }
                if (colL0IsTopArr != null)
                {
                    DiagnosticOutput.Write($"  COL-L0-ISTOP[{colL0CountVal}]:");
                    for (int ci = 0; ci < colL0CountVal; ci++) DiagnosticOutput.Write($" {colL0IsTopArr[ci]}");
                    DiagnosticOutput.WriteLine();
                }
            }
            
            if (isIntra)
            {
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, 0, 0);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, 0, 0);
                FillRefCache(subPos, 2, 2, 0);
                FillRefCacheL1(subPos, 2, 2, 0);
                StoreRefIdx(mbAddr, i, 0, 0);
                StoreRefIdx(mbAddr, i, 1, 0);
                continue;
            }
            
            // MBAFF field MBs: convert frame-level mappedL0Ref to field-level for MC.
            // Also use field POCs for distance scale factor (FFmpeg: dist_scale_factor_field).
            int storedL0Ref = mappedL0Ref;
            int storedL1Ref = 0;
            if (isMbaffFieldMb)
            {
                // For MBAFF field MBs, encode with field parity.
                // Default is same parity (refParityCode=0), but field pair collocated
                // may reference opposite parity field.
                int refParityCode = 0;
                int mbWidth_ref = context.WidthInMacroblocks;
                int mbY_ref = mbAddr / mbWidth_ref;
                int curParity = mbY_ref & 1;
                
                if (mbaffFieldToFieldPair)
                {
                    // Field pair col: use stored IsTop arrays to determine target field parity.
                    sbyte paritySourceRef = repColRef >= 0 ? repColRef
                        : (colRefL1Arr != null ? colRefL1Arr[refColBlkIdx8] : (sbyte)-1);
                    if (paritySourceRef >= 0)
                    {
                        bool[]? isTopArr = repColRef >= 0 ? colL0IsTopArr : colL1IsTopArr;
                        int countVal = repColRef >= 0 ? colL0CountVal : colL1CountVal;
                        if (isTopArr != null && paritySourceRef < countVal)
                        {
                            int targetField = isTopArr[paritySourceRef] ? 0 : 1;
                            refParityCode = curParity ^ targetField;
                        }
                    }
                }
                else if (!frameToFieldPair && earlyColMbIsField)
                {
                    // Same-type field-coded MBAFF col: use original ref index parity.
                    sbyte paritySourceRef = repColRef >= 0 ? repColRef
                        : (colRefL1Arr != null ? colRefL1Arr[refColBlkIdx8] : (sbyte)0);
                    if (paritySourceRef >= 0)
                    {
                        int colMbParity = (refColBlkIdx8 / 16) / mbWidth_ref & 1;
                        int targetField = colMbParity ^ (paritySourceRef & 1);
                        refParityCode = curParity ^ targetField;
                    }
                }
                
                storedL0Ref = mappedL0Ref * 2 + refParityCode;
                storedL1Ref = 0;
            }
            
            FillRefCache(subPos, 2, 2, storedL0Ref);
            FillRefCacheL1(subPos, 2, 2, storedL1Ref);
            StoreRefIdx(mbAddr, i, 0, (sbyte)storedL0Ref);
            StoreRefIdx(mbAddr, i, 1, (sbyte)storedL1Ref);
            
            bool mappedRefIsLongTerm = mappedL0Ref < refPicListL0Count 
                && refPicListL0IsLongTerm[mappedL0Ref];
            
            // Compute temporal distance scaling factors.
            // For MBAFF field MBs, use field-level POCs (FFmpeg: dist_scale_factor_field[field]).
            // currPoc and l1Poc use the current MB's field parity.
            // refPoc uses the ACTUAL field that the L0 ref points to (fieldParity ^ refParityCode).
            // This matches FFmpeg's dist_scale_factor_field[field][i^field] XOR convention:
            // the scale at mapped index j uses ref_list[0][(j^field)+16].poc.
            int currPocForScaling, l1PocForScaling, refPocForScaling;
            if (isMbaffFieldMb)
            {
                int mbWidth8x8 = context.WidthInMacroblocks;
                int mbY = mbAddr / mbWidth8x8;
                int fieldParity = mbY & 1; // 0=top field, 1=bottom field
                int refFieldParity = fieldParity ^ (storedL0Ref & 1); // actual field of L0 ref
                currPocForScaling = fieldParity == 0 ? currentTopFieldPoc : currentBottomFieldPoc;
                l1PocForScaling = colDpbIdx >= 0
                    ? (fieldParity == 0 ? dpb[colDpbIdx].TopFieldPoc : dpb[colDpbIdx].BottomFieldPoc)
                    : l1Poc;
                refPocForScaling = refFieldParity == 0 
                    ? refPicListL0TopFieldPoc[mappedL0Ref] 
                    : refPicListL0BotFieldPoc[mappedL0Ref];
            }
            else if (currentFieldPicture)
            {
                currPocForScaling = currentPictureStructure == ReferenceFrame.PictTopField 
                    ? currentTopFieldPoc : currentBottomFieldPoc;
                l1PocForScaling = l1Poc;
                refPocForScaling = refPicListL0Poc[mappedL0Ref];
            }
            else
            {
                currPocForScaling = currentPoc;
                l1PocForScaling = l1Poc;
                refPocForScaling = refPicListL0Poc[mappedL0Ref];
            }
            
            int td = Math.Clamp(l1PocForScaling - refPocForScaling, -128, 127);
            int tb = Math.Clamp(currPocForScaling - refPocForScaling, -128, 127);
            int distScaleFactor = 0;
            bool useScaling = td != 0 && !mappedRefIsLongTerm;
            if (useScaling)
            {
                int tx = (16384 + (Math.Abs(td) >> 1)) / td;
                distScaleFactor = Math.Clamp((tb * tx + 32) >> 6, -1024, 1023);
            }
            
            // Cross-type always uses d8x8 inference (FFmpeg asserts direct_8x8_inference_flag
            // must be true for cross-type, h264_direct.c line 596)
            if (direct8x8Inference || frameToFieldPair || fieldToFrame || mbaffFrameToField || mbaffFieldToFrame || mbaffFieldToFieldPair)
            {
                short colMvXVal, colMvYVal;
                if (repColRef >= 0)
                {
                    colMvXVal = colMvXArr![mvColBlkIdx8];
                    colMvYVal = colMvYArr![mvColBlkIdx8];
                }
                else
                {
                    colMvXVal = colMvL1XArr![mvColBlkIdx8];
                    colMvYVal = colMvL1YArr![mvColBlkIdx8];
                }
                
                // Frame→field-pair: field MVs are in field coordinates, double Y for frame
                if (frameToFieldPair) colMvYVal = (short)(colMvYVal * 2);
                // Field→frame: only halve Y when collocated MB is frame-coded (MVs in frame coords)
                if (fieldToFrame && !colMbIsField) colMvYVal = (short)(colMvYVal / 2);
                // MBAFF cross-type: scale MV Y when collocated MB's field type differs from current.
                // FFmpeg h264_direct.c:595,630: y_shift=2*!IS_INTERLACED(cur); my_col=(mvY*(1<<y_shift))/2
                // Skip for mbaffFieldToFieldPair: both sides are field-level (NOT cross-type).
                // FFmpeg h264_direct.c:594 confirms NOT cross-type for AFL→FL.
                if (!frameToFieldPair && !fieldToFrame && !mbaffFieldToFieldPair && isMbaffFieldMb != colMbIsField)
                {
                    if (isMbaffFieldMb)
                        colMvYVal = (short)(colMvYVal / 2); // field current ← frame collocated
                    else
                        colMvYVal = (short)(colMvYVal * 2); // frame current ← field collocated
                }
                
                DeriveTemporalScaledMv(colMvXVal, colMvYVal, useScaling, distScaleFactor,
                    out short mvL0X, out short mvL0Y, out short mvL1X, out short mvL1Y);
                
                // Diagnostic: detailed temporal direct trace per-8x8
                if (DiagnosticOutput != null && mbAddr == DiagDumpDetailMbAddr && 
                    (DiagnosticDumpAllMbsForPoc == currentPoc || currentPoc == DiagCabacTracePoc))
                {
                    DiagnosticOutput.WriteLine($"  TD8x8[{i}]: colRef={repColRef} colMbField={colMbIsField} " +
                        $"colRefPoc={colRefPoc} mapped={mappedL0Ref} stored=({storedL0Ref},{storedL1Ref}) " +
                        $"colMV=({colMvXVal},{colMvYVal}) refBlk={refColBlkIdx8} mvBlk={mvColBlkIdx8} " +
                        $"dist={distScaleFactor} td={td} tb={tb} useSc={useScaling} ltRef={mappedRefIsLongTerm} " +
                        $"curPoc={currPocForScaling} l1Poc={l1PocForScaling} refPoc={refPocForScaling} " +
                        $"fi2f={fieldToFrame} fldPic={currentFieldPicture} colFldPic={(colDpbIdx >= 0 ? dpb[colDpbIdx].FieldPicture : false)} " +
                        $"mvL0=({mvL0X},{mvL0Y}) mvL1=({mvL1X},{mvL1Y})");
                }
                
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, mvL0X, mvL0Y);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, mvL1X, mvL1Y);
            }
            else
            {
                int colBase = colBlockBase8x8;
                bool useColL0 = repColRef >= 0;
                int x8 = i & 1;
                int y8 = i >> 1;
                for (int i4 = 0; i4 < 4; i4++)
                {
                    int colBlockOffset = x8 * 2 + (i4 & 1) + (y8 * 2 + (i4 >> 1)) * 4;
                    int col4x4Idx = colBase + colBlockOffset;
                    int scan8Pos = Scan8[i * 4 + i4];
                    
                    short colMvXVal, colMvYVal;
                    if (useColL0)
                    {
                        colMvXVal = colMvXArr![col4x4Idx];
                        colMvYVal = colMvYArr![col4x4Idx];
                    }
                    else
                    {
                        colMvXVal = colMvL1XArr![col4x4Idx];
                        colMvYVal = colMvL1YArr![col4x4Idx];
                    }
                    
                    if (frameToFieldPair) colMvYVal = (short)(colMvYVal * 2);
                    if (fieldToFrame && !colMbIsField) colMvYVal = (short)(colMvYVal / 2);
                    if (!frameToFieldPair && !fieldToFrame && !mbaffFieldToFieldPair && isMbaffFieldMb != colMbIsField)
                    {
                        if (isMbaffFieldMb)
                            colMvYVal = (short)(colMvYVal / 2);
                        else
                            colMvYVal = (short)(colMvYVal * 2);
                    }
                    
                    DeriveTemporalScaledMv(colMvXVal, colMvYVal, useScaling, distScaleFactor,
                        out short mvL0X, out short mvL0Y, out short mvL1X, out short mvL1Y);
                    
                    mvCacheL0X[scan8Pos] = mvL0X; mvCacheL0Y[scan8Pos] = mvL0Y;
                    mvCacheL1X[scan8Pos] = mvL1X; mvCacheL1Y[scan8Pos] = mvL1Y;
                }
            }
        }
    }
    
    /// <summary>
    /// Derives B_Direct/B_Skip motion vectors using temporal prediction (H.264 spec 8.4.1.2.3).
    /// Processes per 8x8 block, reading the co-located sub-block's MV from L1[0] reference.
    /// When Direct8x8Inference is true, each 8x8 partition derives its own scaled MV.
    /// When co-located block is intra, both L0 and L1 MVs are zero for that partition.
    /// </summary>
    private void DeriveBDirectTemporalMotionVectors<TShape>(int mbAddr)
        where TShape : struct, IPictureShape
    {
        int pos = Scan8[0];
        
        // Entry trace for temporal direct
        if (DiagnosticOutput != null && mbAddr == 0)
            DiagnosticOutput.WriteLine($"TD16-ENTRY: poc={currentPoc} field={currentFieldPicture} l1Count={refPicListL1Count}");
        
        if (refPicListL1Count == 0)
        {
            FillMvCache(mvCacheL0X, mvCacheL0Y, pos, 4, 4, 0, 0);
            FillMvCache(mvCacheL1X, mvCacheL1Y, pos, 4, 4, 0, 0);
            StoreRefIdx16x16(mbAddr, 0, 0);
            StoreRefIdx16x16(mbAddr, 1, 0);
            return;
        }
        
        // Find the co-located reference in DPB (L1[0])
        // For field pictures, refPicListL1Poc contains field-specific POCs, so we must
        // match against TopFieldPoc/BottomFieldPoc rather than the combined frame Poc.
        int l1Poc = refPicListL1Poc[0];
        int colDpbIdx = -1;
        for (int i = 0; i < dpbCount; i++)
        {
            if (dpb[i].ColocatedRefIdx == null) continue;
            if (currentFieldPicture)
            {
                // Field picture: match against individual field POCs
                if (dpb[i].TopFieldPoc == l1Poc || dpb[i].BottomFieldPoc == l1Poc)
                {
                    colDpbIdx = i;
                    break;
                }
            }
            else if (dpb[i].Poc == l1Poc)
            {
                colDpbIdx = i;
                break;
            }
        }
        
        if (colDpbIdx < 0)
        {
            FillMvCache(mvCacheL0X, mvCacheL0Y, pos, 4, 4, 0, 0);
            FillMvCache(mvCacheL1X, mvCacheL1Y, pos, 4, 4, 0, 0);
            StoreRefIdx16x16(mbAddr, 0, 0);
            StoreRefIdx16x16(mbAddr, 1, 0);
            return;
        }
        
        ref ReferenceFrame colFrame = ref dpb[colDpbIdx];
        int colBlockBase = mbAddr * 16;
        
        // Diagnostic: check colFrame properties for Direct MBs in target POC
        if (DiagnosticOutput != null && currentPoc == DiagCabacTracePoc)
            DiagnosticOutput.WriteLine($"TD16-COL: mb={mbAddr} colPoc={colFrame.Poc} colField={colFrame.FieldPicture} topColocRef={colFrame.TopFieldColocRefIdx != null} isMbaff={TShape.Mbaff} mbField={TShape.Mbaff && mbFieldDecodingFlag != null && mbFieldDecodingFlag[mbAddr]} colMbFieldFlags={colFrame.MbFieldFlags != null}");
        
        // For field pictures, select the correct field's collocated data.
        // The second field's StoreReferenceFrame overwrites the generic Colocated* arrays,
        // so for field pairs we must use the per-field arrays to get the right data.
        short[]? colMvXArr, colMvYArr, colMvL1XArr, colMvL1YArr;
        sbyte[]? colRefArr, colRefL1Arr;
        int[]? colL0PocsArr, colL1PocsArr;
        int colL0CountVal, colL1CountVal;
        bool[]? colL0IsTopArr = null, colL1IsTopArr = null;
        int colParity = 0; // col_parity: 0=top field closer, 1=bottom field closer
        
        if (currentFieldPicture && colFrame.FieldPicture && colFrame.TopFieldColocRefIdx != null)
        {
            // Field→field: use L1[0]'s actual field parity to select collocated arrays.
            // POC alone is ambiguous when both fields share the same POC value.
            bool useTopField = refPicListL1IsTopField[0];
            if (useTopField)
            {
                colMvXArr = colFrame.TopFieldColocMvX;
                colMvYArr = colFrame.TopFieldColocMvY;
                colRefArr = colFrame.TopFieldColocRefIdx;
                colMvL1XArr = colFrame.TopFieldColocMvL1X;
                colMvL1YArr = colFrame.TopFieldColocMvL1Y;
                colRefL1Arr = colFrame.TopFieldColocRefIdxL1;
                colL0PocsArr = colFrame.TopFieldColocL0Pocs;
                colL0CountVal = colFrame.TopFieldColocL0Count;
                colL1PocsArr = colFrame.TopFieldColocL1Pocs;
                colL1CountVal = colFrame.TopFieldColocL1Count;
                colL0IsTopArr = colFrame.TopFieldColocL0IsTop;
                colL1IsTopArr = colFrame.TopFieldColocL1IsTop;
            }
            else
            {
                colMvXArr = colFrame.BotFieldColocMvX;
                colMvYArr = colFrame.BotFieldColocMvY;
                colRefArr = colFrame.BotFieldColocRefIdx;
                colMvL1XArr = colFrame.BotFieldColocMvL1X;
                colMvL1YArr = colFrame.BotFieldColocMvL1Y;
                colRefL1Arr = colFrame.BotFieldColocRefIdxL1;
                colL0PocsArr = colFrame.BotFieldColocL0Pocs;
                colL0CountVal = colFrame.BotFieldColocL0Count;
                colL1PocsArr = colFrame.BotFieldColocL1Pocs;
                colL1CountVal = colFrame.BotFieldColocL1Count;
                colL0IsTopArr = colFrame.BotFieldColocL0IsTop;
                colL1IsTopArr = colFrame.BotFieldColocL1IsTop;
            }
        }
        else if (!currentFieldPicture && colFrame.FieldPicture && colFrame.TopFieldColocRefIdx != null)
        {
            // Frame→field-pair cross-type: non-MBAFF field MBs have MB_TYPE_INTERLACED
            // via MB_FIELD, so cross-type IS needed. col_parity picks closest field by POC.
            int mbWidth = context.WidthInMacroblocks;
            int mbY = mbAddr / mbWidth;
            int mbX = mbAddr % mbWidth;
            int fieldMbAddr = (mbY >> 1) * mbWidth + mbX;
            colBlockBase = fieldMbAddr * 16;
            
            int curPoc = currentPoc;
            long topDist = Math.Abs((long)colFrame.TopFieldPoc - curPoc);
            long botDist = Math.Abs((long)colFrame.BottomFieldPoc - curPoc);
            bool useTopField = topDist < botDist; // FFmpeg: col_parity = (abs(top-cur) >= abs(bot-cur)), equal → bottom
            colParity = useTopField ? 0 : 1;
            
            if (useTopField)
            {
                colMvXArr = colFrame.TopFieldColocMvX;
                colMvYArr = colFrame.TopFieldColocMvY;
                colRefArr = colFrame.TopFieldColocRefIdx;
                colMvL1XArr = colFrame.TopFieldColocMvL1X;
                colMvL1YArr = colFrame.TopFieldColocMvL1Y;
                colRefL1Arr = colFrame.TopFieldColocRefIdxL1;
                colL0PocsArr = colFrame.TopFieldColocL0Pocs;
                colL0CountVal = colFrame.TopFieldColocL0Count;
                colL1PocsArr = colFrame.TopFieldColocL1Pocs;
                colL1CountVal = colFrame.TopFieldColocL1Count;
                colL0IsTopArr = colFrame.TopFieldColocL0IsTop;
                colL1IsTopArr = colFrame.TopFieldColocL1IsTop;
            }
            else
            {
                colMvXArr = colFrame.BotFieldColocMvX;
                colMvYArr = colFrame.BotFieldColocMvY;
                colRefArr = colFrame.BotFieldColocRefIdx;
                colMvL1XArr = colFrame.BotFieldColocMvL1X;
                colMvL1YArr = colFrame.BotFieldColocMvL1Y;
                colRefL1Arr = colFrame.BotFieldColocRefIdxL1;
                colL0PocsArr = colFrame.BotFieldColocL0Pocs;
                colL0CountVal = colFrame.BotFieldColocL0Count;
                colL1PocsArr = colFrame.BotFieldColocL1Pocs;
                colL1CountVal = colFrame.BotFieldColocL1Count;
                colL0IsTopArr = colFrame.BotFieldColocL0IsTop;
                colL1IsTopArr = colFrame.BotFieldColocL1IsTop;
            }
        }
        else if (currentFieldPicture && !colFrame.FieldPicture)
        {
            // Field→frame: collocated picture is a frame, use generic collocated data.
            // colBlockBase is not used directly; per-i8 bases are computed from
            // fieldToFrameEvenBase/fieldToFrameOddBase instead.
            colMvXArr = colFrame.ColocatedMvX;
            colMvYArr = colFrame.ColocatedMvY;
            colRefArr = colFrame.ColocatedRefIdx;
            colMvL1XArr = colFrame.ColocatedMvL1X;
            colMvL1YArr = colFrame.ColocatedMvL1Y;
            colRefL1Arr = colFrame.ColocatedRefIdxL1;
            colL0PocsArr = colFrame.ColocatedL0Pocs;
            colL0CountVal = colFrame.ColocatedL0Count;
            colL1PocsArr = colFrame.ColocatedL1Pocs;
            colL1CountVal = colFrame.ColocatedL1Count;
        }
        else
        {
            colMvXArr = colFrame.ColocatedMvX;
            colMvYArr = colFrame.ColocatedMvY;
            colRefArr = colFrame.ColocatedRefIdx;
            colMvL1XArr = colFrame.ColocatedMvL1X;
            colMvL1YArr = colFrame.ColocatedMvL1Y;
            colRefL1Arr = colFrame.ColocatedRefIdxL1;
            colL0PocsArr = colFrame.ColocatedL0Pocs;
            colL0CountVal = colFrame.ColocatedL0Count;
            colL1PocsArr = colFrame.ColocatedL1Pocs;
            colL1CountVal = colFrame.ColocatedL1Count;
        }
        
        // Track frame→field-pair for cross-type MV handling (Y-doubling + special block addressing).
        // Non-MBAFF field MBs have MB_TYPE_INTERLACED set via MB_FIELD, so cross-type IS needed.
        bool frameToFieldPair = !currentFieldPicture && colFrame.FieldPicture;
        // Track field→frame case for MV Y halving (frame MVs need to be converted to field coordinates)
        bool fieldToFrame = currentFieldPicture && !colFrame.FieldPicture;
        
        // Early detection of MBAFF per-MB field status for cross-type handling.
        // FFmpeg h264_direct.c:504-513: checks IS_INTERLACED on collocated MB, not picture.
        bool isMbaffFieldMb = TShape.Mbaff && mbFieldDecodingFlag != null && mbFieldDecodingFlag[mbAddr];
        bool earlyColMbIsField = false;
        if (colDpbIdx >= 0)
        {
            var colFieldFlags = dpb[colDpbIdx].MbFieldFlags;
            if (colFieldFlags != null && mbAddr < colFieldFlags.Length)
                earlyColMbIsField = colFieldFlags[mbAddr];
        }
        
        // MBAFF field-coded MB + field pair collocated: NOT cross-type (both IS_INTERLACED).
        // FFmpeg h264_direct.c:504-514: IS_INTERLACED(col)=true, IS_INTERLACED(cur)=true
        // → else branch at line 510, col_fieldoff=0 for non-MBAFF col, goto single_col.
        // Standard block addressing (b8_stride stays default), no half-MB pattern.
        bool mbaffFieldToFieldPair = frameToFieldPair && isMbaffFieldMb;
        if (mbaffFieldToFieldPair)
            frameToFieldPair = false; // remove from frame→field-pair path; handle separately
        
        // MBAFF intra-frame cross-type: current and collocated are both MBAFF frames but
        // individual MBs have different field-coding. FFmpeg h264_direct.c:504-527.
        bool mbaffFrameToField = !frameToFieldPair && !fieldToFrame && !mbaffFieldToFieldPair && !isMbaffFieldMb && earlyColMbIsField;
        bool mbaffFieldToFrame = !frameToFieldPair && !fieldToFrame && !mbaffFieldToFieldPair && isMbaffFieldMb && !earlyColMbIsField;
        
        // Cross-type temporal direct: precompute per-i8 addressing info.
        int crossTypeMbWidth = context.WidthInMacroblocks;
        bool isOddFrameRow = false;
        int fieldToFrameEvenBase = -1, fieldToFrameOddBase = -1;
        bool fieldToFrameColIsField = false;
        int mbaffColParity = 0;
        int mbaffCurRowParity = 0; // current MB's position in MBAFF pair: 0=top, 1=bottom
        
        if (mbaffFieldToFieldPair)
        {
            // MBAFF field-coded MB → field pair collocated: NOT cross-type!
            // FFmpeg h264_direct.c:510-513: col_fieldoff=0 for MBAFF, b8_stride stays at default (2).
            // Line 594: IS_INTERLACED(cur)=true == IS_INTERLACED(col)=true → NOT cross-type.
            // mb_xy stays unchanged, so current row parity determines which field is accessed.
            // Standard block addressing (no half-MB). Field-level scaling via dist_scale_factor_field.
            int mbY = mbAddr / crossTypeMbWidth;
            mbaffCurRowParity = mbY & 1;
            
            // FFmpeg: mb_xy naturally selects the correct collocated field per-MB
            // (even row → top field data, odd row → bottom field data).
            // Re-select collocated arrays based on current MB's field parity.
            ref var colFrameF2F = ref dpb[colDpbIdx];
            int mbX = mbAddr % crossTypeMbWidth;
            colBlockBase = ((mbY >> 1) * crossTypeMbWidth + mbX) * 16;
            if (mbaffCurRowParity == 0)
            {
                colRefArr = colFrameF2F.TopFieldColocRefIdx;
                colMvXArr = colFrameF2F.TopFieldColocMvX;
                colMvYArr = colFrameF2F.TopFieldColocMvY;
                colMvL1XArr = colFrameF2F.TopFieldColocMvL1X;
                colMvL1YArr = colFrameF2F.TopFieldColocMvL1Y;
                colRefL1Arr = colFrameF2F.TopFieldColocRefIdxL1;
                colL0PocsArr = colFrameF2F.TopFieldColocL0Pocs;
                colL0CountVal = colFrameF2F.TopFieldColocL0Count;
                colL1PocsArr = colFrameF2F.TopFieldColocL1Pocs;
                colL1CountVal = colFrameF2F.TopFieldColocL1Count;
                colL0IsTopArr = colFrameF2F.TopFieldColocL0IsTop;
                colL1IsTopArr = colFrameF2F.TopFieldColocL1IsTop;
            }
            else
            {
                colRefArr = colFrameF2F.BotFieldColocRefIdx;
                colMvXArr = colFrameF2F.BotFieldColocMvX;
                colMvYArr = colFrameF2F.BotFieldColocMvY;
                colMvL1XArr = colFrameF2F.BotFieldColocMvL1X;
                colMvL1YArr = colFrameF2F.BotFieldColocMvL1Y;
                colRefL1Arr = colFrameF2F.BotFieldColocRefIdxL1;
                colL0PocsArr = colFrameF2F.BotFieldColocL0Pocs;
                colL0CountVal = colFrameF2F.BotFieldColocL0Count;
                colL1PocsArr = colFrameF2F.BotFieldColocL1Pocs;
                colL1CountVal = colFrameF2F.BotFieldColocL1Count;
                colL0IsTopArr = colFrameF2F.BotFieldColocL0IsTop;
                colL1IsTopArr = colFrameF2F.BotFieldColocL1IsTop;
            }
        }
        else if (frameToFieldPair)
        {
            // FFmpeg h264_direct.c:506: mb_y = (sl->mb_y & ~1) + col_parity (overrides local mb_y).
            // But line 573: half-MB offset uses sl->mb_y & 1 (ORIGINAL row parity, not overridden).
            // Even frame rows → top half of collocated field MB; odd → bottom half.
            int ftfpMbY = mbAddr / crossTypeMbWidth;
            isOddFrameRow = (ftfpMbY & 1) != 0;
        }
        else if (fieldToFrame)
        {
            // Field→frame: FFmpeg h264_direct.c:517: mb_y = sl->mb_y & ~1 where sl->mb_y
            // is in FRAME coordinates (bottom field row k → 2k+1, top field row k → 2k).
            // sl->mb_y & ~1 = 2k for both fields. Our fieldMbY is in FIELD coordinates,
            // so the pair base is fieldMbY * 2 (NOT fieldMbY & ~1, which would be wrong
            // since that rounds the field row, not the frame row).
            int fieldMbY = mbAddr / crossTypeMbWidth;
            int mbX = mbAddr % crossTypeMbWidth;
            int framePairBaseY = fieldMbY * 2;
            fieldToFrameEvenBase = (framePairBaseY * crossTypeMbWidth + mbX) * 16;
            fieldToFrameOddBase = ((framePairBaseY + 1) * crossTypeMbWidth + mbX) * 16;
            
            // Check if the collocated MB at the frame pair base is field-coded.
            // FFmpeg h264_direct.c:504: IS_INTERLACED(parent->mb_type[mb_xy])
            // mb_xy = (sl->mb_y & ~1) * mb_stride + mb_x = framePairBaseY * stride + mbX
            if (colDpbIdx >= 0)
            {
                var colFieldFlags = dpb[colDpbIdx].MbFieldFlags;
                int colCheckMbAddr = framePairBaseY * crossTypeMbWidth + mbX;
                if (colFieldFlags != null && colCheckMbAddr < colFieldFlags.Length && colFieldFlags[colCheckMbAddr])
                {
                    // Collocated pair is field-coded → FFmpeg single_col path.
                    // FFmpeg: goto single_col at sl->mb_y, which is the frame-level row.
                    // Bottom field → odd frame row → bottom MB of pair.
                    // Top field → even frame row → top MB of pair.
                    fieldToFrameColIsField = true;
                    int fieldParity = currentPictureStructure == ReferenceFrame.PictBottomField ? 1 : 0;
                    int selectedBase = fieldParity == 0 ? fieldToFrameEvenBase : fieldToFrameOddBase;
                    fieldToFrameEvenBase = selectedBase;
                    fieldToFrameOddBase = selectedBase;
                }
            }
        }
        else if (mbaffFrameToField)
        {
            // MBAFF frame current ← field collocated: select one field MB of the collocated pair.
            // FFmpeg h264_direct.c:505-509: mb_y = (mb_y & ~1) + col_parity; b8_stride = 0.
            // col_parity selects the field closer to current POC.
            long topDist = Math.Abs((long)colFrame.TopFieldPoc - currentPoc);
            long botDist = Math.Abs((long)colFrame.BottomFieldPoc - currentPoc);
            mbaffColParity = (topDist >= botDist) ? 1 : 0; // FFmpeg: FFABS(top-cur) >= FFABS(bot-cur)
            
            int mbY = mbAddr / crossTypeMbWidth;
            int mbX = mbAddr % crossTypeMbWidth;
            int selectedColRow = (mbY & ~1) | mbaffColParity;
            colBlockBase = (selectedColRow * crossTypeMbWidth + mbX) * 16;
            // FFmpeg h264_direct.c:572-578: within-MB half offset uses sl->mb_y & 1 (current pair pos),
            // NOT col_parity. Top current MB → top half, bottom current MB → bottom half.
            mbaffCurRowParity = mbY & 1;
        }
        else if (mbaffFieldToFrame)
        {
            // MBAFF field current ← frame collocated: read from TWO collocated frame MBs.
            // FFmpeg h264_direct.c:516-522: mb_y = mb_y & ~1; b8_stride = 2+4*stride.
            int mbY = mbAddr / crossTypeMbWidth;
            int mbX = mbAddr % crossTypeMbWidth;
            int evenMbY = mbY & ~1;
            fieldToFrameEvenBase = (evenMbY * crossTypeMbWidth + mbX) * 16;
            fieldToFrameOddBase = ((evenMbY + 1) * crossTypeMbWidth + mbX) * 16;
        }
        
        // Diagnostic: trace cross-type decisions for specific POC
        if (DiagnosticOutput != null && (DiagnosticDumpAllMbsForPoc == currentPoc || currentPoc == DiagCabacTracePoc))
        {
            int dbgMbY = mbAddr / crossTypeMbWidth;
            int dbgMbX = mbAddr % crossTypeMbWidth;
            if (fieldToFrame && mbAddr < 5)
            {
                DiagnosticOutput.WriteLine($"TD-FI2F: mb={mbAddr} ({dbgMbX},{dbgMbY}) colDpb={colDpbIdx} colPoc={colFrame.Poc} " +
                    $"colFieldPic={colFrame.FieldPicture} colMbFieldFlags={colFrame.MbFieldFlags != null} " +
                    $"evenBase={fieldToFrameEvenBase} oddBase={fieldToFrameOddBase} colIsField={fieldToFrameColIsField} " +
                    $"colRefArr.len={colRefArr?.Length} colMvX.len={colMvXArr?.Length} colL0Pocs={colL0PocsArr?.Length}:{colL0CountVal}");
                if (mbAddr == 0)
                {
                    // Dump the first few collocated ref/MV entries for the bases
                    if (colRefArr != null && fieldToFrameEvenBase >= 0 && fieldToFrameEvenBase + 15 < colRefArr.Length)
                    {
                        DiagnosticOutput.Write($"  COL-EVEN(base={fieldToFrameEvenBase}): ref=[");
                        for (int d = 0; d < 16; d++) DiagnosticOutput.Write($"{colRefArr[fieldToFrameEvenBase + d]},");
                        DiagnosticOutput.Write($"] mv=[");
                        for (int d = 0; d < 16; d++) DiagnosticOutput.Write($"({colMvXArr![fieldToFrameEvenBase + d]},{colMvYArr![fieldToFrameEvenBase + d]}),");
                        DiagnosticOutput.WriteLine("]");
                    }
                    if (colRefArr != null && fieldToFrameOddBase >= 0 && fieldToFrameOddBase + 15 < colRefArr.Length)
                    {
                        DiagnosticOutput.Write($"  COL-ODD(base={fieldToFrameOddBase}): ref=[");
                        for (int d = 0; d < 16; d++) DiagnosticOutput.Write($"{colRefArr[fieldToFrameOddBase + d]},");
                        DiagnosticOutput.Write($"] mv=[");
                        for (int d = 0; d < 16; d++) DiagnosticOutput.Write($"({colMvXArr![fieldToFrameOddBase + d]},{colMvYArr![fieldToFrameOddBase + d]}),");
                        DiagnosticOutput.WriteLine("]");
                    }
                    if (colL0PocsArr != null)
                    {
                        DiagnosticOutput.Write($"  COL-L0POCS: [");
                        for (int d = 0; d < colL0CountVal; d++) DiagnosticOutput.Write($"{colL0PocsArr[d]},");
                        DiagnosticOutput.WriteLine("]");
                    }
                    // Dump current field B-frame's L0 ref list
                    DiagnosticOutput.Write($"  CUR-L0({refPicListL0Count}): ");
                    for (int d = 0; d < Math.Min(refPicListL0Count, 8); d++)
                        DiagnosticOutput.Write($"[{d}]poc={refPicListL0Poc[d]} dpbPoc={refPicListL0DpbPoc[d]} top={refPicListL0IsTopField[d]} ");
                    DiagnosticOutput.WriteLine();
                }
            }
            else
            {
                DiagnosticOutput.WriteLine($"TD-SETUP: mb=({dbgMbX},{dbgMbY}) curField={isMbaffFieldMb} colField={earlyColMbIsField} " +
                    $"f2f={mbaffFrameToField} fi2fr={mbaffFieldToFrame} fr2fp={frameToFieldPair} fi2f={fieldToFrame} fi2fp={mbaffFieldToFieldPair} " +
                    $"colParity={mbaffColParity} curPairPos={mbaffCurRowParity} colBase={colBlockBase}");
            }
        }
        
        // Process each 8x8 partition. When direct_8x8_inference_flag=1, use one representative
        // 4x4 per 8x8 and fill all 4 sub-blocks uniformly. When flag=0, each 4x4 block
        // independently looks up its colocated MV and derives its own scaled MV.
        bool direct8x8Inference = context.ActiveSps?.Direct8x8Inference ?? true;
        ReadOnlySpan<int> colocOffsets = ColocOffsets;
        
        for (int i8 = 0; i8 < 4; i8++)
        {
            int subPos = Scan8[i8 * 4];
            
            // Compute per-i8 collocated block indices for ref and MV lookup.
            // For cross-type cases these differ from the simple colBlockBase + colocOffsets[i8].
            int refColBlkIdx, mvColBlkIdx;
            
            if (frameToFieldPair)
            {
                // Frame→field-pair cross-type: b8_stride=0, d8x8 inference.
                // Ref: even row → {0,3}; odd row → {12,15}.
                // MV: even row → {0,3,4,7}; odd row → {8,11,12,15}.
                int x8 = i8 & 1;
                int y8 = i8 >> 1;
                if (!isOddFrameRow)
                {
                    refColBlkIdx = colBlockBase + (x8 == 0 ? 0 : 3);
                    mvColBlkIdx = colBlockBase + x8 * 3 + y8 * 4;
                }
                else
                {
                    refColBlkIdx = colBlockBase + (x8 == 0 ? 12 : 15);
                    mvColBlkIdx = colBlockBase + 8 + x8 * 3 + y8 * 4;
                }
            }
            else if (fieldToFrame)
            {
                if (fieldToFrameColIsField)
                {
                    // Collocated pair is field-coded → FFmpeg single_col with b8_stride=2.
                    // Both IS_INTERLACED match → standard one-to-one path, not cross-type.
                    // Selected base already points to the correct MB (top/bottom per field parity).
                    refColBlkIdx = fieldToFrameEvenBase + colocOffsets[i8];
                    mvColBlkIdx = refColBlkIdx;
                }
                else
                {
                    // Collocated pair is frame-coded → two MBs, different offsets per y8.
                    // FFmpeg: b8_stride=2+4*mb_stride, b4_stride*=6
                    int y8 = i8 >> 1;
                    int perI8Offset = i8 switch { 0 => 0, 1 => 3, 2 => 8, _ => 11 };
                    int perI8Base = y8 == 0 ? fieldToFrameEvenBase : fieldToFrameOddBase;
                    refColBlkIdx = perI8Base + perI8Offset;
                    mvColBlkIdx = refColBlkIdx;
                }
            }
            else if (mbaffFrameToField)
            {
                // MBAFF frame current ← field collocated: b8_stride=0, d8x8 inference.
                // colBlockBase points to the selected field MB (via col_parity).
                // FFmpeg h264_direct.c:572-578: within-MB half offset uses sl->mb_y & 1
                // (current MB's pair position), NOT col_parity.
                int x8 = i8 & 1;
                int y8 = i8 >> 1;
                if (mbaffCurRowParity == 0)
                {
                    refColBlkIdx = colBlockBase + (x8 == 0 ? 0 : 3);
                    mvColBlkIdx = colBlockBase + x8 * 3 + y8 * 4;
                }
                else
                {
                    refColBlkIdx = colBlockBase + (x8 == 0 ? 12 : 15);
                    mvColBlkIdx = colBlockBase + 8 + x8 * 3 + y8 * 4;
                }
            }
            else if (mbaffFieldToFrame)
            {
                // MBAFF field current ← frame collocated: read from TWO collocated frame MBs.
                // y8=0 from even-row MB, y8=1 from odd-row MB. Same offsets as fieldToFrame.
                int y8 = i8 >> 1;
                int perI8Offset = i8 switch { 0 => 0, 1 => 3, 2 => 8, _ => 11 };
                int perI8Base = y8 == 0 ? fieldToFrameEvenBase : fieldToFrameOddBase;
                refColBlkIdx = perI8Base + perI8Offset;
                mvColBlkIdx = refColBlkIdx;
            }
            else if (mbaffFieldToFieldPair)
            {
                // MBAFF field MB → field picture: NOT cross-type (both IS_INTERLACED).
                // FFmpeg h264_direct.c:510-513 → goto single_col → standard b8_stride (no half-MB).
                // Field picture stores one field per MB, so standard per-8x8 block addressing.
                refColBlkIdx = colBlockBase + colocOffsets[i8];
                mvColBlkIdx = refColBlkIdx;
            }
            else
            {
                refColBlkIdx = colBlockBase + colocOffsets[i8];
                mvColBlkIdx = refColBlkIdx;
            }
            
            int maxIdx = Math.Max(refColBlkIdx, mvColBlkIdx);
            if (colRefArr == null || maxIdx >= colRefArr.Length || maxIdx >= (colMvXArr?.Length ?? 0))
            {
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, 0, 0);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, 0, 0);
                FillRefCache(subPos, 2, 2, 0);
                FillRefCacheL1(subPos, 2, 2, 0);
                StoreRefIdx(mbAddr, i8, 0, 0);
                StoreRefIdx(mbAddr, i8, 1, 0);
                continue;
            }
            
            // Resolve ref POC from representative block (used for ref mapping and scaling)
            sbyte repColRef = colRefArr[refColBlkIdx];
            int colRefPoc;
            bool isIntra = false;
            
            // Collocated MBAFF frames have field-coded MBs with field-level ref indices (0..2N-1),
            // but colL0PocsArr has only N frame-level entries. Convert to frame-level for POC lookup.
            // This applies regardless of whether the CURRENT picture is MBAFF or field — what matters
            // is whether the COLLOCATED MB was field-coded.
            // FFmpeg handles this via ref_offset = (mbaff<<4) & (mb_type_col>>3) in h264_direct.c:592.
            // NOTE: colMbIsField must be determined independently of repColRef — even when L0 ref is
            // unavailable (repColRef=-1) and we fall back to L1, the MB's field/frame coding status
            // still affects L1 ref halving, MV Y-scaling, and reference mapping.
            bool colMbIsField = false;
            if (colDpbIdx >= 0)
            {
                var colFieldFlags = dpb[colDpbIdx].MbFieldFlags;
                int colMbAddr = refColBlkIdx / 16;
                if (colFieldFlags != null && colMbAddr < colFieldFlags.Length && colFieldFlags[colMbAddr])
                    colMbIsField = true;
            }
            int pocLookupRef = repColRef;
            if (colMbIsField && repColRef >= 0)
                pocLookupRef = repColRef >> 1; // field ref → frame ref for POC lookup
            
            if (repColRef >= 0)
            {
                if (colL0PocsArr != null && pocLookupRef < colL0CountVal)
                    colRefPoc = colL0PocsArr[pocLookupRef];
                else if (pocLookupRef < refPicListL0Count)
                    colRefPoc = refPicListL0Poc[pocLookupRef];
                else
                    colRefPoc = refPicListL0Poc[0];
            }
            else if (colRefL1Arr != null)
            {
                sbyte colRefL1Val = colRefL1Arr[refColBlkIdx];
                if (colRefL1Val < 0)
                {
                    isIntra = true;
                    colRefPoc = 0;
                }
                else
                {
                    // MBAFF: same conversion for L1 ref of field-coded collocated MBs
                    int l1PocLookup = colRefL1Val;
                    if (colMbIsField) l1PocLookup = colRefL1Val >> 1;
                    
                    if (colL1PocsArr != null && l1PocLookup < colL1CountVal)
                        colRefPoc = colL1PocsArr[l1PocLookup];
                    else if (colL0PocsArr != null && colL0CountVal > 0)
                        colRefPoc = colL0PocsArr[0];
                    else
                        colRefPoc = refPicListL1Poc[0];
                }
            }
            else
            {
                isIntra = true;
                colRefPoc = 0;
            }
            
            // Determine collocated reference's field parity for disambiguation
            int colRefFieldId = -1;
            if (currentFieldPicture && !isIntra)
            {
                int fieldLookup = repColRef >= 0 ? pocLookupRef : -1;
                if (fieldLookup >= 0 && colL0IsTopArr != null && fieldLookup < colL0CountVal)
                    colRefFieldId = colL0IsTopArr[fieldLookup] ? 1 : 2;
                else if (repColRef < 0 && colRefL1Arr != null)
                {
                    sbyte colRefL1Val2 = colRefL1Arr[refColBlkIdx];
                    int l1FieldLookup = colMbIsField ? (colRefL1Val2 >> 1) : colRefL1Val2;
                    if (colRefL1Val2 >= 0 && colL1IsTopArr != null && l1FieldLookup < colL1CountVal)
                        colRefFieldId = colL1IsTopArr[l1FieldLookup] ? 1 : 2;
                }
            }
            
            
            if (isIntra)
            {
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, 0, 0);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, 0, 0);
                FillRefCache(subPos, 2, 2, 0);
                FillRefCacheL1(subPos, 2, 2, 0);
                StoreRefIdx(mbAddr, i8, 0, 0);
                StoreRefIdx(mbAddr, i8, 1, 0);
                continue;
            }
            
            // Map co-located reference POC to current frame's L0 index.
            // Three cases: frame→field-pair (colRefPoc is field-level, map via DPB),
            // field→frame (colRefPoc is frame-level, match via DpbPoc),
            // same-type (direct POC match).
            int mappedL0Ref = 0;
            if (frameToFieldPair || mbaffFieldToFieldPair)
            {
                // colRefPoc is field-level; find frame-level POC via DPB then match L0
                int targetFramePoc = colRefPoc;
                for (int k = 0; k < dpbCount; k++)
                {
                    if (dpb[k].TopFieldPoc == colRefPoc || dpb[k].BottomFieldPoc == colRefPoc)
                    {
                        targetFramePoc = dpb[k].Poc;
                        break;
                    }
                }
                for (int li = 0; li < refPicListL0Count; li++)
                {
                    if (refPicListL0Poc[li] == targetFramePoc)
                    {
                        mappedL0Ref = li;
                        break;
                    }
                }
            }
            else if (currentFieldPicture && !colFrame.FieldPicture)
            {
                // Field→frame: match by DPB frame-level POC.
                // When col MB is field-coded in MBAFF, FFmpeg fill_colmap uses
                // map[2*old_ref + (rfield^field) + 16] where field is the current
                // picture's field parity, NOT the collocated MB's row parity.
                // MBAFF ref_list always has top=even/bottom=odd, but the map table
                // XORs with current field. So: wantTop = ((colRef & 1) ^ curField) == 0
                bool fieldParityKnown = false;
                bool wantTopField = false;
                if (colMbIsField && repColRef >= 0)
                {
                    int curFieldParity = currentPictureStructure == ReferenceFrame.PictBottomField ? 1 : 0;
                    wantTopField = ((repColRef & 1) ^ curFieldParity) == 0;
                    fieldParityKnown = true;
                }
                
                bool fi2fMatched = false;
                if (fieldParityKnown)
                {
                    for (int li = 0; li < refPicListL0Count; li++)
                    {
                        if (refPicListL0DpbPoc[li] == colRefPoc &&
                            refPicListL0IsTopField[li] == wantTopField)
                        {
                            mappedL0Ref = li;
                            fi2fMatched = true;
                            break;
                        }
                    }
                }
                
                if (!fi2fMatched)
                {
                    for (int li = 0; li < refPicListL0Count; li++)
                    {
                        if (refPicListL0DpbPoc[li] == colRefPoc)
                        {
                            mappedL0Ref = li;
                            break;
                        }
                    }
                }
            }
            else
            {
                bool matchFound = false;
                if (colRefFieldId > 0)
                {
                    bool wantTop = colRefFieldId == 1;
                    for (int li = 0; li < refPicListL0Count; li++)
                    {
                        if (refPicListL0Poc[li] == colRefPoc && refPicListL0IsTopField[li] == wantTop)
                        {
                            mappedL0Ref = li;
                            matchFound = true;
                            break;
                        }
                    }
                }
                if (!matchFound)
                {
                    for (int li = 0; li < refPicListL0Count; li++)
                    {
                        if (refPicListL0Poc[li] == colRefPoc)
                        {
                            mappedL0Ref = li;
                            matchFound = true;
                            break;
                        }
                    }
                }
                if (!matchFound && DiagnosticOutput != null)
                {
                    DiagnosticOutput.Write($"TD16x16-POCMISS: poc={currentPoc} mb={mbAddr} i8={i8} colRef={repColRef} colRefPoc={colRefPoc} L0[{refPicListL0Count}]=");
                    for (int li = 0; li < refPicListL0Count; li++) DiagnosticOutput.Write($"{refPicListL0Poc[li]} ");
                    DiagnosticOutput.WriteLine();
                }
            }
            
            // MBAFF field MBs: convert frame-level mappedL0Ref to field-level for MC.
            // MC does fieldRef>>1 for frame ref, fieldRef&1 encodes field parity:
            //   0 = same parity as current MB, 1 = opposite parity.
            // FFmpeg: map_col_to_list0_field[mb_y&1] produces the correct field-level index
            // by considering the co-located ref's actual field parity. We must replicate this
            // by computing parityCode from the co-located ref's parity relative to current MB.
            // Also use field POCs for distance scale factor (FFmpeg: dist_scale_factor_field).
            int storedL0Ref = mappedL0Ref;
            int storedL1Ref = 0;
            int refParityCode = 0; // 0=same parity as current MB, 1=opposite
            if (isMbaffFieldMb)
            {
                int curMbY = mbAddr / crossTypeMbWidth;
                int curParity = curMbY & 1;

                
                // Determine the actual field the co-located reference points to.
                // For field-coded co-located MBs, the raw ref index encodes parity:
                //   even = same as co-located MB's parity, odd = opposite.
                // FFmpeg h264_direct.c:592 uses ref_offset=(mbaff<<4)&(mb_type_col>>3) to
                // select field-specific mapping entries that encode this parity correctly.
                if (colMbIsField)
                {
                    int colMbY = (refColBlkIdx / 16) / crossTypeMbWidth;
                    int colMbParity = colMbY & 1;
                    sbyte paritySourceRef = repColRef >= 0 ? repColRef 
                        : (colRefL1Arr != null ? colRefL1Arr[refColBlkIdx] : (sbyte)0);
                    if (paritySourceRef >= 0)
                    {
                        int targetField = colMbParity ^ (paritySourceRef & 1); // actual field: 0=top, 1=bottom
                        refParityCode = curParity ^ targetField;
                    }
                }
                else if (frameToFieldPair || mbaffFieldToFieldPair)
                {
                    // Field pair L1[0]: ref indices from the field's ref list already encode
                    // field parity. Use the stored IsTop arrays to determine which field the
                    // collocated reference points to. FFmpeg handles this via fill_colmap which
                    // builds map_col_to_list0_field with proper parity-aware field-level indices.
                    sbyte paritySourceRef = repColRef >= 0 ? repColRef
                        : (colRefL1Arr != null ? colRefL1Arr[refColBlkIdx] : (sbyte)-1);
                    if (paritySourceRef >= 0)
                    {
                        bool[]? isTopArr = repColRef >= 0 ? colL0IsTopArr : colL1IsTopArr;
                        int countVal = repColRef >= 0 ? colL0CountVal : colL1CountVal;
                        if (isTopArr != null && paritySourceRef < countVal)
                        {
                            int targetField = isTopArr[paritySourceRef] ? 0 : 1;
                            refParityCode = curParity ^ targetField;
                        }
                    }
                }
                
                storedL0Ref = mappedL0Ref * 2 + refParityCode;
                storedL1Ref = 0;
            }
            
            // Fill ref cache per-8x8
            FillRefCache(subPos, 2, 2, storedL0Ref);
            FillRefCacheL1(subPos, 2, 2, storedL1Ref);
            StoreRefIdx(mbAddr, i8, 0, (sbyte)storedL0Ref);
            StoreRefIdx(mbAddr, i8, 1, (sbyte)storedL1Ref);
            
            // H.264 spec 8.4.1.2.3: no temporal scaling when mapped L0 ref is long-term
            bool mappedRefIsLongTerm = mappedL0Ref < refPicListL0Count 
                && refPicListL0IsLongTerm[mappedL0Ref];
            
            // Compute temporal distance scaling factors.
            // For MBAFF field MBs, use field-level POCs (FFmpeg: dist_scale_factor_field[field]).
            // currPoc and l1Poc use the current MB's field parity.
            // refPoc uses the ACTUAL field that the L0 ref points to (may differ from current
            // MB's parity when co-located ref had opposite parity).
            // FFmpeg: dist_scale_factor_field[field][i^field] = get_scale_factor(..., i+16)
            int currPocForScaling, l1PocForScaling, refPocForScaling;
            if (isMbaffFieldMb)
            {
                int mbY = mbAddr / crossTypeMbWidth;
                int fieldParity = mbY & 1; // 0=top field, 1=bottom field
                int refFieldParity = fieldParity ^ refParityCode; // actual field of L0 ref
                currPocForScaling = fieldParity == 0 ? currentTopFieldPoc : currentBottomFieldPoc;
                l1PocForScaling = fieldParity == 0 ? dpb[colDpbIdx].TopFieldPoc : dpb[colDpbIdx].BottomFieldPoc;
                refPocForScaling = refFieldParity == 0 
                    ? refPicListL0TopFieldPoc[mappedL0Ref] 
                    : refPicListL0BotFieldPoc[mappedL0Ref];
            }
            else if (currentFieldPicture)
            {
                currPocForScaling = currentPictureStructure == ReferenceFrame.PictTopField 
                    ? currentTopFieldPoc : currentBottomFieldPoc;
                l1PocForScaling = l1Poc;
                refPocForScaling = refPicListL0Poc[mappedL0Ref];
            }
            else
            {
                currPocForScaling = currentPoc;
                l1PocForScaling = l1Poc;
                refPocForScaling = refPicListL0Poc[mappedL0Ref];
            }
            
            int td = Math.Clamp(l1PocForScaling - refPocForScaling, -128, 127);
            int tb = Math.Clamp(currPocForScaling - refPocForScaling, -128, 127);
            int distScaleFactor = 0;
            bool useScaling = td != 0 && !mappedRefIsLongTerm;
            if (useScaling)
            {
                int tx = (16384 + (Math.Abs(td) >> 1)) / td;
                distScaleFactor = Math.Clamp((tb * tx + 32) >> 6, -1024, 1023);
            }
            
            // Cross-type always uses d8x8 inference (FFmpeg asserts direct_8x8_inference_flag
            // must be true for cross-type, h264_direct.c line 596)
            if (direct8x8Inference || frameToFieldPair || fieldToFrame || mbaffFrameToField || mbaffFieldToFrame || mbaffFieldToFieldPair)
            {
                // Per-8x8: one colocated MV lookup, uniform for all 4 4x4 blocks
                short colMvXVal, colMvYVal;
                if (repColRef >= 0)
                {
                    colMvXVal = colMvXArr![mvColBlkIdx];
                    colMvYVal = colMvYArr![mvColBlkIdx];
                }
                else
                {
                    colMvXVal = colMvL1XArr![mvColBlkIdx];
                    colMvYVal = colMvL1YArr![mvColBlkIdx];
                }
                
                // Frame→field-pair: field MVs are in field coordinates, double Y for frame
                if (frameToFieldPair) colMvYVal = (short)(colMvYVal * 2);
                // Field→frame: only halve Y when collocated MB is frame-coded (MVs in frame coords)
                if (fieldToFrame && !colMbIsField) colMvYVal = (short)(colMvYVal / 2);
                // MBAFF cross-type: scale MV Y when collocated MB's field type differs from current.
                // Skip for mbaffFieldToFieldPair: both sides are field-level (NOT cross-type).
                if (!frameToFieldPair && !fieldToFrame && !mbaffFieldToFieldPair && isMbaffFieldMb != colMbIsField)
                {
                    if (isMbaffFieldMb)
                        colMvYVal = (short)(colMvYVal / 2); // field current ← frame collocated
                    else
                        colMvYVal = (short)(colMvYVal * 2); // frame current ← field collocated
                }
                
                DeriveTemporalScaledMv(colMvXVal, colMvYVal, useScaling, distScaleFactor,
                    out short mvL0X, out short mvL0Y, out short mvL1X, out short mvL1Y);
                
                FillMvCache(mvCacheL0X, mvCacheL0Y, subPos, 2, 2, mvL0X, mvL0Y);
                FillMvCache(mvCacheL1X, mvCacheL1Y, subPos, 2, 2, mvL1X, mvL1Y);
            }
            else
            {
                // Per-4x4: each 4x4 independently reads its colocated MV, but from the
                // SAME list (L0 or L1) determined by the representative block's ref
                bool useColL0 = repColRef >= 0;
                int x8 = i8 & 1;
                int y8 = i8 >> 1;
                for (int i4 = 0; i4 < 4; i4++)
                {
                    int colBlockOffset = x8 * 2 + (i4 & 1) + (y8 * 2 + (i4 >> 1)) * 4;
                    int col4x4Idx = colBlockBase + colBlockOffset;
                    int scan8Pos = Scan8[i8 * 4 + i4];
                    
                    short colMvXVal, colMvYVal;
                    if (useColL0)
                    {
                        colMvXVal = colMvXArr![col4x4Idx];
                        colMvYVal = colMvYArr![col4x4Idx];
                    }
                    else
                    {
                        colMvXVal = colMvL1XArr![col4x4Idx];
                        colMvYVal = colMvL1YArr![col4x4Idx];
                    }
                    
                    // Frame→field-pair: double Y MV for field→frame coordinate conversion
                    if (frameToFieldPair) colMvYVal = (short)(colMvYVal * 2);
                    // Field→frame: only halve Y when collocated MB is frame-coded (MVs in frame coords)
                    if (fieldToFrame && !colMbIsField) colMvYVal = (short)(colMvYVal / 2);
                    // MBAFF cross-type MV Y scaling (skip for mbaffFieldToFieldPair: NOT cross-type)
                    if (!frameToFieldPair && !fieldToFrame && !mbaffFieldToFieldPair && isMbaffFieldMb != colMbIsField)
                    {
                        if (isMbaffFieldMb)
                            colMvYVal = (short)(colMvYVal / 2);
                        else
                            colMvYVal = (short)(colMvYVal * 2);
                    }
                    
                    DeriveTemporalScaledMv(colMvXVal, colMvYVal, useScaling, distScaleFactor,
                        out short mvL0X, out short mvL0Y, out short mvL1X, out short mvL1Y);
                    
                    mvCacheL0X[scan8Pos] = mvL0X; mvCacheL0Y[scan8Pos] = mvL0Y;
                    mvCacheL1X[scan8Pos] = mvL1X; mvCacheL1Y[scan8Pos] = mvL1Y;
                }
            }
        }
    }
    
    /// <summary>
    /// Computes temporal direct scaled MVs from a colocated MV using distance scaling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DeriveTemporalScaledMv(short colMvX, short colMvY, bool useScaling, int distScaleFactor,
        out short mvL0X, out short mvL0Y, out short mvL1X, out short mvL1Y)
    {
        if (!useScaling || (colMvX == 0 && colMvY == 0))
        {
            mvL0X = colMvX;
            mvL0Y = colMvY;
            mvL1X = 0;
            mvL1Y = 0;
        }
        else
        {
            mvL0X = (short)Math.Clamp((distScaleFactor * colMvX + 128) >> 8, -32768, 32767);
            mvL0Y = (short)Math.Clamp((distScaleFactor * colMvY + 128) >> 8, -32768, 32767);
            mvL1X = (short)(mvL0X - colMvX);
            mvL1Y = (short)(mvL0Y - colMvY);
        }
    }
    
    /// <summary>
    /// Checks colZero flag for a specific 4x4 block within a macroblock.
    /// blockOffset4x4 is the raster-scan 4x4 block index within the MB (0-15).
    /// </summary>
    /// <summary>
    /// Whether the co-located 4x4 block is "colZero": referenced by L1[0] with a motion vector
    /// of at most a quarter sample either way, so spatial direct zeroes this block's vector.
    /// </summary>
    /// <remarks>
    /// Everything about which picture and which arrays to read is settled for the slice by
    /// <see cref="ResolveColZeroPicture"/>; what is left here is one address and six loads.
    /// </remarks>
    private bool GetColocatedZeroFlagFor4x4<TShape>(int mbAddr, int blockOffset4x4)
        where TShape : struct, IPictureShape
    {
        EnsureDirectColocated<TShape>();
        if (!colZeroResolved)
            return false;

        int blkIdx = mbAddr;
        if (colZeroAddressing != ColZeroAddressing.SameGeometry)
        {
            int mbWidth = context.WidthInMacroblocks;
            int mbX = mbAddr % mbWidth;
            int mbY = mbAddr / mbWidth;
            blkIdx = colZeroAddressing == ColZeroAddressing.FrameToFieldPair
                // Two frame rows share one field row.
                ? (mbY / 2) * mbWidth + mbX
                // One field row is one of the two frame rows of its pair.
                : (mbY * 2 + (currentPictureStructure == ReferenceFrame.PictBottomField ? 1 : 0))
                      * mbWidth + mbX;
        }
        blkIdx = blkIdx * 16 + blockOffset4x4;

        sbyte[] colRefL0Arr = colZeroRefL0!;
        if ((uint)blkIdx >= (uint)colRefL0Arr.Length)
            return false;

        sbyte colRefL0 = colRefL0Arr[blkIdx];
        if (colRefL0 == 0)
            return Math.Abs(colZeroMvX![blkIdx]) <= 1 && Math.Abs(colZeroMvY![blkIdx]) <= 1;

        // The co-located block predicted from L1 alone: the test moves to that list.
        if (colRefL0 < 0 && colZeroRefL1 != null && colZeroRefL1[blkIdx] == 0)
            return Math.Abs(colZeroMvL1X![blkIdx]) <= 1 && Math.Abs(colZeroMvL1Y![blkIdx]) <= 1;

        return false;
    }

    private bool GetColocatedZeroFlagForBlock<TShape>(int mbAddr, int block8x8)
        where TShape : struct, IPictureShape
    {
        ReadOnlySpan<int> colocOffsets = ColocOffsets;
        return GetColocatedZeroFlagFor4x4<TShape>(mbAddr, colocOffsets[block8x8]);
    }
    
    /// <summary>
    /// Derives direct mode ref_idx as the minimum non-negative ref_idx from neighbors.
    /// Returns 0 if no valid neighbor ref exists.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// <summary>
    /// Returns the minimum non-negative ref_idx from three neighbors.
    /// Per H.264 spec 8.4.1.2.2: returns -1 when ALL neighbors have negative ref_idx,
    /// indicating this list is not used for the Direct MB.
    /// </summary>
    private static int DeriveDirectRefIdx(int refA, int refB, int refC)
    {
        // FFmpeg's FFMIN3 over *unsigned* values, h264_direct.c:234. An unavailable or intra
        // neighbour is negative, which as unsigned is larger than any real index, so the
        // minimum skips it without being asked to - and when all three are negative the result
        // is the largest of them, which read back as signed is negative again. Six branches
        // become two compares.
        uint min = Math.Min(Math.Min((uint)refA, (uint)refB), (uint)refC);
        return (int)min < 0 ? -1 : (int)min;
    }

    /// <summary>
    /// Predicts a direct macroblock's motion vector from the three neighbours whose references
    /// were just read.
    /// </summary>
    /// <remarks>
    /// This is <c>pred_motion</c> with the cases removed that a direct block cannot reach -
    /// FFmpeg says as much where it inlines the same thing (h264_direct.c:237). The derived
    /// index is the minimum of the three neighbours' own indices, so at least one of them
    /// carries it, and the "no neighbour matches" fallback that the general predictor needs
    /// cannot happen here. The diagonal neighbour arrives already fetched.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PredictDirectMotion(
        short[] cacheX, short[] cacheY, int pos, int refIdx,
        int leftRef, int topRef, int diagRef, short diagMvX, short diagMvY,
        out short mvpX, out short mvpY)
    {
        short mvAX = cacheX[pos - 1], mvAY = cacheY[pos - 1];
        short mvBX = cacheX[pos - 8], mvBY = cacheY[pos - 8];

        int matchCount = (leftRef == refIdx ? 1 : 0)
                       + (topRef == refIdx ? 1 : 0)
                       + (diagRef == refIdx ? 1 : 0);

        if (matchCount > 1)
        {
            // Most common.
            mvpX = MedianShort(mvAX, mvBX, diagMvX);
            mvpY = MedianShort(mvAY, mvBY, diagMvY);
        }
        else if (leftRef == refIdx) { mvpX = mvAX; mvpY = mvAY; }
        else if (topRef == refIdx) { mvpX = mvBX; mvpY = mvBY; }
        else { mvpX = diagMvX; mvpY = diagMvY; }
    }

    /// <summary>
    /// Processes a CAVLC-parsed inter macroblock by computing MVPs, filling MV/ref caches,
    /// and storing boundary data. This replicates the cache logic that CABAC does inline during parsing.
    /// Must be called after MacroblockDecoder.DecodeMacroblockCavlc and before ReconstructMacroblock.
    /// </summary>
    private unsafe void ProcessCavlcInterMacroblock<TShape>(ref Macroblock mb, int mbAddr, int mbX, int mbY, int mbWidth)
        where TShape : struct, IPictureShape
    {
        PrepareRefCache<TShape>(mbAddr, mbX, mbY, mbWidth);
        PrepareMvCache<TShape>(mbAddr, mbX, mbY, mbWidth);

        int mode = mb.InterPartitionMode;
        bool trace = (DiagTraceMbAddr >= 0 && mbAddr == DiagTraceMbAddr);
        diagTraceMbActive = trace;
        if (trace) Console.WriteLine($"[DIAG-MB] mbAddr={mbAddr} mbX={mbX} mbY={mbY} mode={mode} isField={mbFieldDecodingFlag?[mbAddr]}");

        switch (mode)
        {
            case 0: // P_L0_16x16
            {
                int refIdx = mb.InterRefIdxL0[0];
                FillRefCache(Scan8[0], 4, 4, refIdx);
                FillRefCacheL1(Scan8[0], 4, 4, -1);
                StoreRefIdx16x16(mbAddr, 0, (sbyte)refIdx);
                StoreRefIdx16x16(mbAddr, 1, -1);

                PredMotion<TShape>(0, 4, 0, refIdx, out short mvpX, out short mvpY);
                short mvX = (short)(mvpX + mb.InterMvdL0X[0]);
                short mvY = (short)(mvpY + mb.InterMvdL0Y[0]);
                FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[0], 4, 4, mvX, mvY);
                FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[0], 4, 4, 0, 0);
                break;
            }

            case 1: // P_L0_L0_16x8
            {
                int ref0 = mb.InterRefIdxL0[0];
                int ref1 = mb.InterRefIdxL0[1];
                FillRefCache(Scan8[0], 4, 2, ref0);
                FillRefCache(Scan8[8], 4, 2, ref1);
                FillRefCacheL1(Scan8[0], 4, 4, -1);
                StoreRefIdx(mbAddr, 0, 0, (sbyte)ref0);
                StoreRefIdx(mbAddr, 1, 0, (sbyte)ref0);
                StoreRefIdx(mbAddr, 2, 0, (sbyte)ref1);
                StoreRefIdx(mbAddr, 3, 0, (sbyte)ref1);
                StoreRefIdx16x16(mbAddr, 1, -1);

                Pred16x8Motion<TShape>(0, 0, ref0, out short mvpX0, out short mvpY0);
                short mvX0 = (short)(mvpX0 + mb.InterMvdL0X[0]);
                short mvY0 = (short)(mvpY0 + mb.InterMvdL0Y[0]);
                FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[0], 4, 2, mvX0, mvY0);

                Pred16x8Motion<TShape>(8, 0, ref1, out short mvpX1, out short mvpY1);
                short mvX1 = (short)(mvpX1 + mb.InterMvdL0X[1]);
                short mvY1 = (short)(mvpY1 + mb.InterMvdL0Y[1]);
                FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[8], 4, 2, mvX1, mvY1);
                FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[0], 4, 4, 0, 0);
                break;
            }

            case 2: // P_L0_L0_8x16
            {
                int ref0 = mb.InterRefIdxL0[0];
                int ref1 = mb.InterRefIdxL0[1];
                FillRefCache(Scan8[0], 2, 4, ref0);
                FillRefCache(Scan8[4], 2, 4, ref1);
                FillRefCacheL1(Scan8[0], 4, 4, -1);
                StoreRefIdx(mbAddr, 0, 0, (sbyte)ref0);
                StoreRefIdx(mbAddr, 2, 0, (sbyte)ref0);
                StoreRefIdx(mbAddr, 1, 0, (sbyte)ref1);
                StoreRefIdx(mbAddr, 3, 0, (sbyte)ref1);
                StoreRefIdx16x16(mbAddr, 1, -1);

                Pred8x16Motion<TShape>(0, 0, ref0, out short mvpX0, out short mvpY0);
                short mvX0 = (short)(mvpX0 + mb.InterMvdL0X[0]);
                short mvY0 = (short)(mvpY0 + mb.InterMvdL0Y[0]);
                FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[0], 2, 4, mvX0, mvY0);

                Pred8x16Motion<TShape>(4, 0, ref1, out short mvpX1, out short mvpY1);
                short mvX1 = (short)(mvpX1 + mb.InterMvdL0X[1]);
                short mvY1 = (short)(mvpY1 + mb.InterMvdL0Y[1]);
                FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[4], 2, 4, mvX1, mvY1);
                FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[0], 4, 4, 0, 0);
                break;
            }

            case 3: // P_8x8
            case 4: // P_8x8ref0
            {
                // Fill ref cache for each 8x8 sub-MB
                for (int i = 0; i < 4; i++)
                {
                    int refI = mb.InterRefIdxL0[i];
                    FillRefCache(Scan8[4 * i], 2, 2, refI);
                    StoreRefIdx(mbAddr, i, 0, (sbyte)refI);
                }
                FillRefCacheL1(Scan8[0], 4, 4, -1);
                StoreRefIdx16x16(mbAddr, 1, -1);

                // Mark inner-boundary positions as unavailable (matching CABAC path)
                refCacheL0[Scan8[4]] = PartNotAvailable;
                refCacheL0[Scan8[12]] = PartNotAvailable;
                mvCacheL0X[Scan8[4]] = mvCacheL0Y[Scan8[4]] = 0;
                mvCacheL0X[Scan8[12]] = mvCacheL0Y[Scan8[12]] = 0;

                int mvdIdx = 0;
                if (trace)
                {
                    Console.Write("[DIAG-MB] all mvdL0X:");
                    for (int k = 0; k < 16; k++) Console.Write($" {mb.InterMvdL0X[k]}");
                    Console.Write(" mvdL0Y:");
                    for (int k = 0; k < 16; k++) Console.Write($" {mb.InterMvdL0Y[k]}");
                    Console.WriteLine();
                }
                for (int i = 0; i < 4; i++)
                {
                    // Restore correct ref at sub-MB origin (was PART_NOT_AVAILABLE for i=1,3)
                    refCacheL0[Scan8[4 * i]] = refCacheL0[Scan8[4 * i] + 1];

                    int baseBlock = 4 * i;
                    int refI = mb.InterRefIdxL0[i];
                    int subType = mb.InterSubMbTypes[i];

                    switch (subType)
                    {
                        case 0: // P_L0_8x8
                        {
                            PredMotion<TShape>(baseBlock, 2, 0, refI, out short mvpX, out short mvpY);
                            short mvX = (short)(mvpX + mb.InterMvdL0X[mvdIdx]);
                            short mvY = (short)(mvpY + mb.InterMvdL0Y[mvdIdx]);
                            if (trace) Console.WriteLine($"[DIAG-MB] sub{i} 8x8: mvp=({mvpX},{mvpY}) mvd=({mb.InterMvdL0X[mvdIdx]},{mb.InterMvdL0Y[mvdIdx]}) mv=({mvX},{mvY}) mvdIdx={mvdIdx}");
                            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[baseBlock], 2, 2, mvX, mvY);
                            mvdIdx++;
                            break;
                        }
                        case 1: // P_L0_8x4
                        {
                            PredMotion<TShape>(baseBlock, 2, 0, refI, out short mvpX0, out short mvpY0);
                            short mvX0 = (short)(mvpX0 + mb.InterMvdL0X[mvdIdx]);
                            short mvY0 = (short)(mvpY0 + mb.InterMvdL0Y[mvdIdx]);
                            if (trace) Console.WriteLine($"[DIAG-MB] sub{i} 8x4-top: mvp=({mvpX0},{mvpY0}) mvd=({mb.InterMvdL0X[mvdIdx]},{mb.InterMvdL0Y[mvdIdx]}) mv=({mvX0},{mvY0}) mvdIdx={mvdIdx}");
                            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[baseBlock], 2, 1, mvX0, mvY0);
                            mvdIdx++;

                            PredMotion<TShape>(baseBlock + 2, 2, 0, refI, out short mvpX1, out short mvpY1);
                            short mvX1 = (short)(mvpX1 + mb.InterMvdL0X[mvdIdx]);
                            short mvY1 = (short)(mvpY1 + mb.InterMvdL0Y[mvdIdx]);
                            if (trace) Console.WriteLine($"[DIAG-MB] sub{i} 8x4-bot: mvp=({mvpX1},{mvpY1}) mvd=({mb.InterMvdL0X[mvdIdx]},{mb.InterMvdL0Y[mvdIdx]}) mv=({mvX1},{mvY1}) mvdIdx={mvdIdx}");
                            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[baseBlock + 2], 2, 1, mvX1, mvY1);
                            mvdIdx++;
                            break;
                        }
                        case 2: // P_L0_4x8
                        {
                            PredMotion<TShape>(baseBlock, 1, 0, refI, out short mvpX0, out short mvpY0);
                            short mvX0 = (short)(mvpX0 + mb.InterMvdL0X[mvdIdx]);
                            short mvY0 = (short)(mvpY0 + mb.InterMvdL0Y[mvdIdx]);
                            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[baseBlock], 1, 2, mvX0, mvY0);
                            mvdIdx++;

                            PredMotion<TShape>(baseBlock + 1, 1, 0, refI, out short mvpX1, out short mvpY1);
                            short mvX1 = (short)(mvpX1 + mb.InterMvdL0X[mvdIdx]);
                            short mvY1 = (short)(mvpY1 + mb.InterMvdL0Y[mvdIdx]);
                            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[baseBlock + 1], 1, 2, mvX1, mvY1);
                            mvdIdx++;
                            break;
                        }
                        default: // P_L0_4x4
                        {
                            for (int j = 0; j < 4; j++)
                            {
                                PredMotion<TShape>(baseBlock + j, 1, 0, refI, out short mvpX, out short mvpY);
                                short mvX = (short)(mvpX + mb.InterMvdL0X[mvdIdx]);
                                short mvY = (short)(mvpY + mb.InterMvdL0Y[mvdIdx]);
                                FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[baseBlock + j], 1, 1, mvX, mvY);
                                mvdIdx++;
                            }
                            break;
                        }
                    }
                }
                FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[0], 4, 4, 0, 0);
                break;
            }
        }

        if (trace)
        {
            Console.Write("[DIAG-MB] L0 MV cache:");
            for (int blk = 0; blk < 16; blk++)
            {
                int s8 = Scan8[blk];
                Console.Write($" [{blk}]=({mvCacheL0X[s8]},{mvCacheL0Y[s8]},r={refCacheL0[s8]})");
            }
            Console.WriteLine();
            if (mode >= 3) // P_8x8
            {
                Console.Write("[DIAG-MB] subTypes:");
                for (int i = 0; i < 4; i++) Console.Write($" [{i}]={mb.InterSubMbTypes[i]}");
                Console.Write(" refs:");
                for (int i = 0; i < 4; i++) Console.Write($" [{i}]={mb.InterRefIdxL0[i]}");
                Console.WriteLine();
            }
        }
        StoreMvBoundaries(mbAddr);
        StoreRefBoundaries(mbAddr);
        StoreBlockDataForDeblocking<TShape>(mbAddr);
    }

    /// <summary>
    /// Processes a CAVLC-parsed B-slice inter macroblock by computing MVPs, filling MV/ref caches,
    /// and storing boundary data. Mirrors what CABAC does inline via DecodeBSliceInterPredictionCabac.
    /// </summary>
    private unsafe void ProcessCavlcBSliceInterMacroblock<TShape>(ref Macroblock mb, int mbAddr, int mbX, int mbY, int mbWidth)
        where TShape : struct, IPictureShape
    {
        PrepareRefCache<TShape>(mbAddr, mbX, mbY, mbWidth);
        PrepareMvCache<TShape>(mbAddr, mbX, mbY, mbWidth);
        PrepareDirectCache<TShape>(mbAddr, mbX, mbY, mbWidth);

        int mbType = mb.BSliceMbType;

        if(mbType == 0)
        {
            // B_Direct_16x16: derive MVs from direct mode, no explicit syntax
            if (currentSliceHeader!.DirectSpatialMvPrediction)
                DeriveBDirectSpatialMotionVectors<TShape>(mbAddr);
            else
                DeriveBDirectTemporalMotionVectors<TShape>(mbAddr);
            FillDirectCache(Scan8[0], 4, 4, 1);
            StoreDirectBoundaries(mbAddr);
            StoreMvBoundaries(mbAddr);
            StoreRefBoundaries(mbAddr);
            StoreBlockDataForDeblocking<TShape>(mbAddr);
            return;
        }

        if (mbType >= 1 && mbType <= 3)
        {
            // B_L0_16x16 (1), B_L1_16x16 (2), B_Bi_16x16 (3)
            bool usesL0 = (mbType == 1 || mbType == 3);
            bool usesL1 = (mbType == 2 || mbType == 3);

            int refL0 = usesL0 ? mb.InterRefIdxL0[0] : -1;
            int refL1 = usesL1 ? mb.InterRefIdxL1[0] : -1;

            FillRefCache(Scan8[0], 4, 4, refL0);
            FillRefCacheL1(Scan8[0], 4, 4, refL1);
            StoreRefIdx16x16(mbAddr, 0, (sbyte)refL0);
            StoreRefIdx16x16(mbAddr, 1, (sbyte)refL1);

            if (usesL0)
            {
                PredMotion<TShape>(0, 4, 0, refL0, out short mvpX, out short mvpY);
                short finalMvX = (short)(mvpX + mb.InterMvdL0X[0]);
                short finalMvY = (short)(mvpY + mb.InterMvdL0Y[0]);
                FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[0], 4, 4, finalMvX, finalMvY);
            }
            else
            {
                FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[0], 4, 4, 0, 0);
            }

            if (usesL1)
            {
                PredMotion<TShape>(0, 4, 1, refL1, out short mvpX, out short mvpY);
                short finalMvX = (short)(mvpX + mb.InterMvdL1X[0]);
                short finalMvY = (short)(mvpY + mb.InterMvdL1Y[0]);
                FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[0], 4, 4, finalMvX, finalMvY);
            }
            else
            {
                FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[0], 4, 4, 0, 0);
            }
        }
        else if (mbType >= 4 && mbType <= 21)
        {
            // Two-partition modes: even=16x8, odd=8x16
            bool is16x8 = (mbType % 2 == 0);
            int n0 = 0;
            int n1 = is16x8 ? 8 : 4;
            int fw = is16x8 ? 4 : 2;
            int fh = is16x8 ? 2 : 4;

            // L0/L1 usage per partition from H.264 Table 7-14
            bool p0L0, p0L1, p1L0, p1L1;
            switch (mbType)
            {
                case 4: case 5: p0L0 = p1L0 = true; p0L1 = p1L1 = false; break;
                case 6: case 7: p0L0 = p1L0 = false; p0L1 = p1L1 = true; break;
                case 8: case 9: p0L0 = true; p0L1 = false; p1L0 = false; p1L1 = true; break;
                case 10: case 11: p0L0 = false; p0L1 = true; p1L0 = true; p1L1 = false; break;
                case 12: case 13: p0L0 = true; p0L1 = false; p1L0 = p1L1 = true; break;
                case 14: case 15: p0L0 = false; p0L1 = true; p1L0 = p1L1 = true; break;
                case 16: case 17: p0L0 = p0L1 = true; p1L0 = true; p1L1 = false; break;
                case 18: case 19: p0L0 = p0L1 = true; p1L0 = false; p1L1 = true; break;
                default: p0L0 = p0L1 = p1L0 = p1L1 = true; break; // 20, 21: Bi_Bi
            }

            int refP0L0 = p0L0 ? mb.InterRefIdxL0[0] : -1;
            int refP1L0 = p1L0 ? mb.InterRefIdxL0[1] : -1;
            int refP0L1 = p0L1 ? mb.InterRefIdxL1[0] : -1;
            int refP1L1 = p1L1 ? mb.InterRefIdxL1[1] : -1;

            FillRefCache(Scan8[n0], fw, fh, refP0L0);
            FillRefCache(Scan8[n1], fw, fh, refP1L0);
            FillRefCacheL1(Scan8[n0], fw, fh, refP0L1);
            FillRefCacheL1(Scan8[n1], fw, fh, refP1L1);

            if (is16x8)
            {
                StoreRefIdx(mbAddr, 0, 0, (sbyte)refP0L0); StoreRefIdx(mbAddr, 1, 0, (sbyte)refP0L0);
                StoreRefIdx(mbAddr, 2, 0, (sbyte)refP1L0); StoreRefIdx(mbAddr, 3, 0, (sbyte)refP1L0);
                StoreRefIdx(mbAddr, 0, 1, (sbyte)refP0L1); StoreRefIdx(mbAddr, 1, 1, (sbyte)refP0L1);
                StoreRefIdx(mbAddr, 2, 1, (sbyte)refP1L1); StoreRefIdx(mbAddr, 3, 1, (sbyte)refP1L1);
            }
            else
            {
                StoreRefIdx(mbAddr, 0, 0, (sbyte)refP0L0); StoreRefIdx(mbAddr, 2, 0, (sbyte)refP0L0);
                StoreRefIdx(mbAddr, 1, 0, (sbyte)refP1L0); StoreRefIdx(mbAddr, 3, 0, (sbyte)refP1L0);
                StoreRefIdx(mbAddr, 0, 1, (sbyte)refP0L1); StoreRefIdx(mbAddr, 2, 1, (sbyte)refP0L1);
                StoreRefIdx(mbAddr, 1, 1, (sbyte)refP1L1); StoreRefIdx(mbAddr, 3, 1, (sbyte)refP1L1);
            }

            // Compute MVPs and apply MVDs for both lists
            if (is16x8)
            {
                ProcessBSlice16x8Mvs<TShape>(ref mb, n0, n1, fw, fh, p0L0, p0L1, p1L0, p1L1,
                    refP0L0, refP1L0, refP0L1, refP1L1);
            }
            else
            {
                ProcessBSlice8x16Mvs<TShape>(ref mb, n0, n1, fw, fh, p0L0, p0L1, p1L0, p1L1,
                    refP0L0, refP1L0, refP0L1, refP1L1);
            }
        }
        else if (mbType == 22)
        {
            ProcessCavlcBSlice8x8<TShape>(ref mb, mbAddr, mbX, mbY, mbWidth);
            return; // StoreMv/Ref/Block already called inside
        }

        StoreMvBoundaries(mbAddr);
        StoreRefBoundaries(mbAddr);
        StoreDirectBoundaries(mbAddr);
        StoreBlockDataForDeblocking<TShape>(mbAddr);
    }
    private unsafe void ProcessBSlice16x8Mvs<TShape>(ref Macroblock mb, int n0, int n1, int fw, int fh,
        bool p0L0, bool p0L1, bool p1L0, bool p1L1,
        int refP0L0, int refP1L0, int refP0L1, int refP1L1)
        where TShape : struct, IPictureShape
    {
        if (p0L0)
        {
            Pred16x8Motion<TShape>(n0, 0, refP0L0, out short mvpX, out short mvpY);
            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[n0], fw, fh,
                (short)(mvpX + mb.InterMvdL0X[0]), (short)(mvpY + mb.InterMvdL0Y[0]));
        }
        else
            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[n0], fw, fh, 0, 0);

        if (p1L0)
        {
            Pred16x8Motion<TShape>(n1, 0, refP1L0, out short mvpX, out short mvpY);
            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[n1], fw, fh,
                (short)(mvpX + mb.InterMvdL0X[1]), (short)(mvpY + mb.InterMvdL0Y[1]));
        }
        else
            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[n1], fw, fh, 0, 0);

        if (p0L1)
        {
            Pred16x8Motion<TShape>(n0, 1, refP0L1, out short mvpX, out short mvpY);
            FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[n0], fw, fh,
                (short)(mvpX + mb.InterMvdL1X[0]), (short)(mvpY + mb.InterMvdL1Y[0]));
        }
        else
            FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[n0], fw, fh, 0, 0);

        if (p1L1)
        {
            Pred16x8Motion<TShape>(n1, 1, refP1L1, out short mvpX, out short mvpY);
            FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[n1], fw, fh,
                (short)(mvpX + mb.InterMvdL1X[1]), (short)(mvpY + mb.InterMvdL1Y[1]));
        }
        else
            FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[n1], fw, fh, 0, 0);
    }

    /// <summary>
    /// Process MVs for B-slice 8x16 two-partition modes.
    /// </summary>
    private unsafe void ProcessBSlice8x16Mvs<TShape>(ref Macroblock mb, int n0, int n1, int fw, int fh,
        bool p0L0, bool p0L1, bool p1L0, bool p1L1,
        int refP0L0, int refP1L0, int refP0L1, int refP1L1)
        where TShape : struct, IPictureShape
    {
        if (p0L0)
        {
            Pred8x16Motion<TShape>(n0, 0, refP0L0, out short mvpX, out short mvpY);
            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[n0], fw, fh,
                (short)(mvpX + mb.InterMvdL0X[0]), (short)(mvpY + mb.InterMvdL0Y[0]));
        }
        else
            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[n0], fw, fh, 0, 0);

        if (p1L0)
        {
            Pred8x16Motion<TShape>(n1, 0, refP1L0, out short mvpX, out short mvpY);
            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[n1], fw, fh,
                (short)(mvpX + mb.InterMvdL0X[1]), (short)(mvpY + mb.InterMvdL0Y[1]));
        }
        else
            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[n1], fw, fh, 0, 0);

        if (p0L1)
        {
            Pred8x16Motion<TShape>(n0, 1, refP0L1, out short mvpX, out short mvpY);
            FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[n0], fw, fh,
                (short)(mvpX + mb.InterMvdL1X[0]), (short)(mvpY + mb.InterMvdL1Y[0]));
        }
        else
            FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[n0], fw, fh, 0, 0);

        if (p1L1)
        {
            Pred8x16Motion<TShape>(n1, 1, refP1L1, out short mvpX, out short mvpY);
            FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[n1], fw, fh,
                (short)(mvpX + mb.InterMvdL1X[1]), (short)(mvpY + mb.InterMvdL1Y[1]));
        }
        else
            FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[n1], fw, fh, 0, 0);
    }

    /// <summary>
    /// Process B_8x8 sub-macroblock types for CAVLC. Each sub-MB can be Direct, L0, L1, or Bi
    /// with various partition shapes (8x8, 8x4, 4x8, 4x4).
    /// </summary>
    private unsafe void ProcessCavlcBSlice8x8<TShape>(ref Macroblock mb, int mbAddr, int mbX, int mbY, int mbWidth)
        where TShape : struct, IPictureShape
    {
        // Extract sub-MB types for DeriveBDirect8x8. On the stack: this runs once per B_8x8
        // macroblock, so as an array it was allocated for every such macroblock in every frame.
        Scratch.Of4<int> subMbTypesStorage = default;
        Span<int> subMbTypes = subMbTypesStorage;
        for (int i = 0; i < 4; i++)
            subMbTypes[i] = mb.BSliceSubMbTypes[i];

        // Fill ref caches per sub-MB
        for (int i = 0; i < 4; i++)
        {
            int st = subMbTypes[i];
            if (st == 0)
            {
                // B_Direct_8x8: ref 0 for both lists (will be overwritten by direct mode)
                FillRefCache(Scan8[4 * i], 2, 2, 0);
                FillRefCacheL1(Scan8[4 * i], 2, 2, 0);
                StoreRefIdx(mbAddr, i, 0, 0);
                StoreRefIdx(mbAddr, i, 1, 0);
            }
            else
            {
                bool usesL0 = CavlcBSubMbUsesL0(st);
                bool usesL1 = CavlcBSubMbUsesL1(st);
                int refL0 = usesL0 ? mb.InterRefIdxL0[i] : -1;
                int refL1 = usesL1 ? mb.InterRefIdxL1[i] : -1;
                FillRefCache(Scan8[4 * i], 2, 2, refL0);
                FillRefCacheL1(Scan8[4 * i], 2, 2, refL1);
                StoreRefIdx(mbAddr, i, 0, (sbyte)refL0);
                StoreRefIdx(mbAddr, i, 1, (sbyte)refL1);
            }
        }

        // Mark inner-boundary positions as unavailable (prevents stale C neighbor reads)
        refCacheL0[Scan8[4]] = PartNotAvailable;
        refCacheL0[Scan8[12]] = PartNotAvailable;
        refCacheL1[Scan8[4]] = PartNotAvailable;
        refCacheL1[Scan8[12]] = PartNotAvailable;
        mvCacheL0X[Scan8[4]] = mvCacheL0Y[Scan8[4]] = 0;
        mvCacheL0X[Scan8[12]] = mvCacheL0Y[Scan8[12]] = 0;
        mvCacheL1X[Scan8[4]] = mvCacheL1Y[Scan8[4]] = 0;
        mvCacheL1X[Scan8[12]] = mvCacheL1Y[Scan8[12]] = 0;

        // Derive direct MVs for B_Direct_8x8 sub-MBs before MVD processing
        bool hasDirectSubMb = subMbTypes[0] == 0 || subMbTypes[1] == 0 || subMbTypes[2] == 0 || subMbTypes[3] == 0;
        if (hasDirectSubMb)
        {
            if (currentSliceHeader!.DirectSpatialMvPrediction)
                DeriveBDirect8x8SpatialMotionVectors<TShape>(mbAddr, subMbTypes);
            else
                DeriveBDirect8x8TemporalMotionVectors<TShape>(mbAddr, subMbTypes);
            
            // Re-set PartNotAvailable after direct derivation overwrites these positions.
            // Same fix as CABAC path — prevents PredMotion from using stale C neighbors.
            refCacheL0[Scan8[4]] = PartNotAvailable;
            refCacheL0[Scan8[12]] = PartNotAvailable;
            refCacheL1[Scan8[4]] = PartNotAvailable;
            refCacheL1[Scan8[12]] = PartNotAvailable;
            
            for (int i = 0; i < 4; i++)
            {
                if (subMbTypes[i] == 0)
                    FillDirectCache(Scan8[4 * i], 2, 2, 1);
            }
        }

        // Read ref values from +1 positions (not +0, which may be PART_NOT_AVAILABLE for i=1,3)
        Scratch.Of4<int> refL0ValsStorage = default;
        Span<int> refL0Vals = refL0ValsStorage;
        Scratch.Of4<int> refL1ValsStorage = default;
        Span<int> refL1Vals = refL1ValsStorage;
        for (int i = 0; i < 4; i++)
        {
            refL0Vals[i] = refCacheL0[Scan8[4 * i] + 1];
            refL1Vals[i] = refCacheL1[Scan8[4 * i] + 1];
        }

        // Process L0 MVDs per sub-MB
        int mvdL0Idx = 0;
        for (int i = 0; i < 4; i++)
        {
            refCacheL0[Scan8[4 * i]] = refCacheL0[Scan8[4 * i] + 1];
            int st = subMbTypes[i];
            int baseBlock = 4 * i;

            if (st == 0)
                continue; // Direct: MVs already derived

            if (!CavlcBSubMbUsesL0(st))
            {
                FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[baseBlock], 2, 2, 0, 0);
                continue;
            }

            int refI = refL0Vals[i];
            fixed (short* mvdL0X = mb.InterMvdL0X, mvdL0Y = mb.InterMvdL0Y)
            {
                mvdL0Idx = ProcessBSubMbMvdsCavlc<TShape>(st, baseBlock, 0, refI, mvdL0Idx,
                    mvCacheL0X, mvCacheL0Y, mvdL0X, mvdL0Y);
            }
        }

        // Process L1 MVDs per sub-MB
        int mvdL1Idx = 0;
        for (int i = 0; i < 4; i++)
        {
            refCacheL1[Scan8[4 * i]] = refCacheL1[Scan8[4 * i] + 1];
            int st = subMbTypes[i];
            int baseBlock = 4 * i;

            if (st == 0)
                continue;

            if (!CavlcBSubMbUsesL1(st))
            {
                FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[baseBlock], 2, 2, 0, 0);
                continue;
            }

            int refI = refL1Vals[i];
            fixed (short* mvdL1X = mb.InterMvdL1X, mvdL1Y = mb.InterMvdL1Y)
            {
                mvdL1Idx = ProcessBSubMbMvdsCavlc<TShape>(st, baseBlock, 1, refI, mvdL1Idx,
                    mvCacheL1X, mvCacheL1Y, mvdL1X, mvdL1Y);
            }
        }

        StoreMvBoundaries(mbAddr);
        StoreRefBoundaries(mbAddr);
        StoreDirectBoundaries(mbAddr);
        StoreBlockDataForDeblocking<TShape>(mbAddr);
        
        // DIAG: dump sub_mb_types and MV cache for target MB
        if (DiagnosticOutput != null && DiagnosticTargetMb.HasValue 
            && DiagnosticTargetMb.Value.mbX == mbX && DiagnosticTargetMb.Value.mbY == mbY
            && (DiagnosticTargetMb.Value.frame < 0 || DiagnosticTargetMb.Value.frame == currentPoc))
        {
            DiagnosticOutput.WriteLine($"B8x8-DIAG MB({mbX},{mbY}) poc={currentPoc} subTypes=[{subMbTypes[0]},{subMbTypes[1]},{subMbTypes[2]},{subMbTypes[3]}] d8x8inf={context.ActiveSps?.Direct8x8Inference}");
            for (int blk = 0; blk < 16; blk++)
            {
                int s8 = Scan8[blk];
                DiagnosticOutput.WriteLine($"  blk{blk}: L0ref={refCacheL0[s8]} mv=({mvCacheL0X[s8]},{mvCacheL0Y[s8]}) L1ref={refCacheL1[s8]} mv=({mvCacheL1X[s8]},{mvCacheL1Y[s8]})");
            }
        }
    }

    /// <summary>
    /// Compute MVPs and apply MVDs for one list of a B_8x8 sub-MB's sub-partitions.
    /// Returns the updated mvdIdx (compact index into the MVD arrays).
    /// </summary>
    private unsafe int ProcessBSubMbMvdsCavlc<TShape>(int subMbType, int baseBlock,
        int list, int refIdx, int mvdIdx,
        short[] cacheX, short[] cacheY, short* mvdArrayX, short* mvdArrayY)
        where TShape : struct, IPictureShape
    {
        // Partition shapes: 1-3=8x8, 4/6/8=8x4, 5/7/9=4x8, 10-12=4x4
        if (subMbType <= 3)
        {
            // 8x8: single sub-partition
            PredMotion<TShape>(baseBlock, 2, list, refIdx, out short mvpX, out short mvpY);
            FillMvCache(cacheX, cacheY, Scan8[baseBlock], 2, 2,
                (short)(mvpX + mvdArrayX[mvdIdx]), (short)(mvpY + mvdArrayY[mvdIdx]));
            return mvdIdx + 1;
        }

        bool is8x4 = (subMbType == 4 || subMbType == 6 || subMbType == 8);
        if (subMbType <= 9)
        {
            if (is8x4)
            {
                // 8x4: two sub-partitions
                PredMotion<TShape>(baseBlock, 2, list, refIdx, out short mvpX0, out short mvpY0);
                FillMvCache(cacheX, cacheY, Scan8[baseBlock], 2, 1,
                    (short)(mvpX0 + mvdArrayX[mvdIdx]), (short)(mvpY0 + mvdArrayY[mvdIdx]));
                mvdIdx++;

                PredMotion<TShape>(baseBlock + 2, 2, list, refIdx, out short mvpX1, out short mvpY1);
                FillMvCache(cacheX, cacheY, Scan8[baseBlock + 2], 2, 1,
                    (short)(mvpX1 + mvdArrayX[mvdIdx]), (short)(mvpY1 + mvdArrayY[mvdIdx]));
                return mvdIdx + 1;
            }
            else
            {
                // 4x8: two sub-partitions
                PredMotion<TShape>(baseBlock, 1, list, refIdx, out short mvpX0, out short mvpY0);
                FillMvCache(cacheX, cacheY, Scan8[baseBlock], 1, 2,
                    (short)(mvpX0 + mvdArrayX[mvdIdx]), (short)(mvpY0 + mvdArrayY[mvdIdx]));
                mvdIdx++;

                PredMotion<TShape>(baseBlock + 1, 1, list, refIdx, out short mvpX1, out short mvpY1);
                FillMvCache(cacheX, cacheY, Scan8[baseBlock + 1], 1, 2,
                    (short)(mvpX1 + mvdArrayX[mvdIdx]), (short)(mvpY1 + mvdArrayY[mvdIdx]));
                return mvdIdx + 1;
            }
        }

        // 4x4: four sub-partitions
        for (int j = 0; j < 4; j++)
        {
            PredMotion<TShape>(baseBlock + j, 1, list, refIdx, out short mvpX, out short mvpY);
            FillMvCache(cacheX, cacheY, Scan8[baseBlock + j], 1, 1,
                (short)(mvpX + mvdArrayX[mvdIdx]), (short)(mvpY + mvdArrayY[mvdIdx]));
            mvdIdx++;
        }
        return mvdIdx;
    }

    /// <summary>Returns true if B-slice sub_mb_type uses L0 prediction (Table 7-18).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CavlcBSubMbUsesL0(int subMbType)
    {
        // L0: 1, Bi: 3, L0_8x4: 4, L0_4x8: 5, Bi_8x4: 8, Bi_4x8: 9, L0_4x4: 10, Bi_4x4: 12
        return subMbType != 2 && subMbType != 6 && subMbType != 7 && subMbType != 11 && subMbType != 0;
    }

    /// <summary>Returns true if B-slice sub_mb_type uses L1 prediction (Table 7-18).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CavlcBSubMbUsesL1(int subMbType)
    {
        // L1: 2, Bi: 3, L1_8x4: 6, L1_4x8: 7, Bi_8x4: 8, Bi_4x8: 9, L1_4x4: 11, Bi_4x4: 12
        return subMbType != 1 && subMbType != 4 && subMbType != 5 && subMbType != 10 && subMbType != 0;
    }
    /// <summary>Corner blocks of a macroblock, in the cross pattern the direct modes read.</summary>
    private static readonly int[] CrossOffsetsData = { 0, 3, 8, 11 };
    private static ReadOnlySpan<int> CrossOffsets => CrossOffsetsData;

    /// <summary>Corner blocks of a macroblock, as the co-located snapshot indexes them.</summary>
    private static readonly int[] ColocOffsetsData = { 0, 3, 12, 15 };
    private static ReadOnlySpan<int> ColocOffsets => ColocOffsetsData;

}

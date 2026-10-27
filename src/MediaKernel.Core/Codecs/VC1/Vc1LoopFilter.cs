// VC-1's overlap smoothing and in-loop deblocking - a translation of
// ffmpeg-master/libavcodec/vc1_loopfilter.c.
//
// All of vc1_loopfilter.c is translated: the overlap smoothing filters, which run on the
// coefficient blocks before the inverse transform and need no picture at all; the per-block
// deblocking decisions, which run on the reconstructed planes; and the four drivers that walk
// the macroblock grid and work out which block sits where.
//
// FFmpeg indexes v->block as block[idx][n] - a group of six 64-coefficient blocks per index -
// and this port carries it flattened with a stride of 6 * 64, so a group is an offset and a
// block within it is another 64.

using System;
using System.Runtime.CompilerServices;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Vc1Video;

/// <summary>
/// FFmpeg's <c>vc1_loopfilter.c</c>: the overlap smoothing that runs across block edges before
/// the inverse transform, and the in-loop deblocking that runs after reconstruction.
/// </summary>
public static class Vc1LoopFilter
{
    /// <summary>One group of six 64-coefficient blocks, by its index in <c>v-&gt;block</c>.</summary>
    private static int Group(int blkIdx) => blkIdx * 6 * 64;

    /// <summary>
    /// <c>vc1_h_overlap_filter</c> - overlap smoothing across a vertical edge, between the
    /// block to the left and the block to the right of it.
    /// </summary>
    /// <remarks>
    /// The strides and the flags are what carry FIELDTX: when one of the two macroblocks is
    /// field-coded and the other is not, the two halves of the field-coded one are 16
    /// coefficients apart where the frame-coded one's are 8, and the <c>+ 8</c> on blocks 2 and
    /// 3 picks the half that lines up.
    /// </remarks>
    public static void Vc1HOverlapFilter(Vc1Context v, int leftBlk, int rightBlk,
                                         int leftFieldtx, int rightFieldtx, int blockNum)
    {
        Span<short> block = v.Block;
        int left = Group(leftBlk);
        int right = Group(rightBlk);

        switch (blockNum)
        {
            case 0:
                Vc1Dsp.Vc1HSOverlap(
                    block, left + 2 * 64, block, right + 0 * 64,
                    (leftFieldtx ^ rightFieldtx) != 0 ? 16 - 8 * leftFieldtx : 8,
                    (leftFieldtx ^ rightFieldtx) != 0 ? 16 - 8 * rightFieldtx : 8,
                    (leftFieldtx != 0 || rightFieldtx != 0) ? 0 : 1);
                break;

            case 1:
                Vc1Dsp.Vc1HSOverlap(
                    block, right + 0 * 64, block, right + 2 * 64,
                    8, 8, rightFieldtx != 0 ? 0 : 1);
                break;

            case 2:
                Vc1Dsp.Vc1HSOverlap(
                    block,
                    leftFieldtx == 0 && rightFieldtx != 0 ? left + 2 * 64 + 8 : left + 3 * 64,
                    block,
                    leftFieldtx != 0 && rightFieldtx == 0 ? right + 0 * 64 + 8 : right + 1 * 64,
                    (leftFieldtx ^ rightFieldtx) != 0 ? 16 - 8 * leftFieldtx : 8,
                    (leftFieldtx ^ rightFieldtx) != 0 ? 16 - 8 * rightFieldtx : 8,
                    (leftFieldtx != 0 || rightFieldtx != 0) ? 2 : 1);
                break;

            case 3:
                Vc1Dsp.Vc1HSOverlap(
                    block, right + 1 * 64, block, right + 3 * 64,
                    8, 8, rightFieldtx != 0 ? 2 : 1);
                break;

            case 4:
            case 5:
                Vc1Dsp.Vc1HSOverlap(block, left + blockNum * 64, block, right + blockNum * 64,
                                    8, 8, 1);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// <c>vc1_v_overlap_filter</c> - overlap smoothing across a horizontal edge.
    /// </summary>
    public static void Vc1VOverlapFilter(Vc1Context v, int topBlk, int bottomBlk, int blockNum)
    {
        Span<short> block = v.Block;
        int top = Group(topBlk);
        int bottom = Group(bottomBlk);

        switch (blockNum)
        {
            case 0:
                Vc1Dsp.Vc1VSOverlap(block, top + 1 * 64, block, bottom + 0 * 64);
                break;
            case 1:
                Vc1Dsp.Vc1VSOverlap(block, top + 3 * 64, block, bottom + 2 * 64);
                break;
            case 2:
                Vc1Dsp.Vc1VSOverlap(block, bottom + 0 * 64, block, bottom + 1 * 64);
                break;
            case 3:
                Vc1Dsp.Vc1VSOverlap(block, bottom + 2 * 64, block, bottom + 3 * 64);
                break;
            case 4:
            case 5:
                Vc1Dsp.Vc1VSOverlap(block, top + blockNum * 64, block, bottom + blockNum * 64);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// <c>ff_vc1_i_overlap_filter</c> - overlap smoothing for an I or BI macroblock.
    /// </summary>
    /// <remarks>
    /// FFmpeg's comment on the ordering is worth keeping: within a macroblock the horizontal
    /// overlap always runs before the vertical, so this runs H on the left and internal
    /// vertical borders of the macroblock just decoded, and waits for the next call to do the
    /// right edge - which means the H overlap trails by one macroblock column and the V
    /// overlap by one row.
    /// </remarks>
    public static void Vc1IOverlapFilter(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        // CONFIG_GRAY is off in this port, so the block count is always six.
        const int blockCount = 6;
        int mbPos = s.MbX + s.MbY * s.MbStride;

        int topleftBlk = v.TopleftBlkIdx;
        int topBlk = v.TopBlkIdx;
        int leftBlk = v.LeftBlkIdx;
        int curBlk = v.CurBlkIdx;

        byte[] overFlags = v.OverFlagsPlane!;
        byte[] fieldtx = v.FieldtxPlane!;

        for (int i = 0; i < blockCount; i++)
        {
            if (s.MbX == 0 && (i & 5) != 1)
                continue;

            if (v.Pq >= 9 ||
                (v.Profile == (int)Vc1Profile.Advanced &&
                 (v.Condover == (byte)CoTypes.All ||
                  (overFlags[mbPos] != 0 &&
                   ((i & 5) == 1 || overFlags[mbPos - 1] != 0)))))
            {
                Vc1HOverlapFilter(
                    v,
                    s.MbX != 0 ? leftBlk : curBlk, curBlk,
                    v.Fcm == FrameCodingMode.IlaceFrame && s.MbX != 0 &&
                        fieldtx[mbPos - 1] != 0 ? 1 : 0,
                    v.Fcm == FrameCodingMode.IlaceFrame && fieldtx[mbPos] != 0 ? 1 : 0,
                    i);
            }
        }

        if (v.Fcm == FrameCodingMode.IlaceFrame)
            return;

        for (int i = 0; i < blockCount; i++)
        {
            if (s.FirstSliceLine != 0 && (i & 2) == 0)
                continue;

            if (s.MbX != 0 &&
                (v.Pq >= 9 ||
                 (v.Profile == (int)Vc1Profile.Advanced &&
                  (v.Condover == (byte)CoTypes.All ||
                   (overFlags[mbPos - 1] != 0 &&
                    ((i & 2) != 0 || overFlags[mbPos - 1 - s.MbStride] != 0))))))
            {
                Vc1VOverlapFilter(v, s.FirstSliceLine != 0 ? leftBlk : topleftBlk, leftBlk, i);
            }
            if (s.MbX == s.MbWidth - 1 &&
                (v.Pq >= 9 ||
                 (v.Profile == (int)Vc1Profile.Advanced &&
                  (v.Condover == (byte)CoTypes.All ||
                   (overFlags[mbPos] != 0 &&
                    ((i & 2) != 0 || overFlags[mbPos - s.MbStride] != 0))))))
            {
                Vc1VOverlapFilter(v, s.FirstSliceLine != 0 ? curBlk : topBlk, curBlk, i);
            }
        }
    }

    /// <summary>
    /// <c>ff_vc1_p_overlap_filter</c> - the same for a P macroblock, where whether an edge is
    /// smoothed is decided by both of its blocks being intra rather than by CONDOVER.
    /// </summary>
    public static void Vc1POverlapFilter(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        const int blockCount = 6;
        int mbPos = s.MbX + s.MbY * s.MbStride;

        int topleftBlk = v.TopleftBlkIdx;
        int topBlk = v.TopBlkIdx;
        int leftBlk = v.LeftBlkIdx;
        int curBlk = v.CurBlkIdx;

        byte[] mbType = v.MbTypeBase!;
        int mbTypeOff = v.MbTypeOffset;
        byte[] fieldtx = v.FieldtxPlane!;

        for (int i = 0; i < blockCount; i++)
        {
            if (s.MbX == 0 && (i & 5) != 1)
                continue;

            if (mbType[mbTypeOff + s.BlockIndex[i]] != 0 &&
                mbType[mbTypeOff + s.BlockIndex[i] - 1] != 0)
            {
                Vc1HOverlapFilter(
                    v,
                    s.MbX != 0 ? leftBlk : curBlk, curBlk,
                    v.Fcm == FrameCodingMode.IlaceFrame && s.MbX != 0 &&
                        fieldtx[mbPos - 1] != 0 ? 1 : 0,
                    v.Fcm == FrameCodingMode.IlaceFrame && fieldtx[mbPos] != 0 ? 1 : 0,
                    i);
            }
        }

        if (v.Fcm == FrameCodingMode.IlaceFrame)
            return;

        for (int i = 0; i < blockCount; i++)
        {
            if (s.FirstSliceLine != 0 && (i & 2) == 0)
                continue;

            int shift = i > 3 ? 1 : 0;
            if (s.MbX != 0 &&
                mbType[mbTypeOff + s.BlockIndex[i] - 2 + shift] != 0 &&
                mbType[mbTypeOff + s.BlockIndex[i] - s.BlockWrap[i] - 2 + shift] != 0)
            {
                Vc1VOverlapFilter(v, s.FirstSliceLine != 0 ? leftBlk : topleftBlk, leftBlk, i);
            }
            if (s.MbX == s.MbWidth - 1 &&
                mbType[mbTypeOff + s.BlockIndex[i]] != 0 &&
                mbType[mbTypeOff + s.BlockIndex[i] - s.BlockWrap[i]] != 0)
            {
                Vc1VOverlapFilter(v, s.FirstSliceLine != 0 ? curBlk : topBlk, curBlk, i);
            }
        }
    }
    // --- In-loop deblocking -------------------------------------------------------------------
    //
    // vc1_loopfilter.c's four #defines for the picture edges a macroblock can sit against. An
    // edge flag suppresses the filter on a border that has nothing on the other side of it.

    /// <summary><c>LEFT_EDGE</c>.</summary>
    public const uint LeftEdge = 1 << 0;

    /// <summary><c>RIGHT_EDGE</c>.</summary>
    public const uint RightEdge = 1 << 1;

    /// <summary><c>TOP_EDGE</c>.</summary>
    public const uint TopEdge = 1 << 2;

    /// <summary><c>BOTTOM_EDGE</c>.</summary>
    public const uint BottomEdge = 1 << 3;

    /// <summary>
    /// The offset of block <paramref name="blockNum"/> within a macroblock: the four luma blocks
    /// sit in a 2x2 arrangement, and the two chroma blocks are each a plane of their own, so
    /// their <c>dest</c> already points at them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BlockDest(int dest, int blockNum, int linesize) =>
        blockNum > 3 ? dest : dest + (blockNum & 2) * 4 * linesize + (blockNum & 1) * 8;

    /// <summary>
    /// <c>vc1_i_h_loop_filter</c> - deblocking across the vertical edges of one block of an
    /// I macroblock.
    /// </summary>
    /// <remarks>
    /// An I picture has no coded block pattern to consult: every edge is filtered, and the only
    /// decisions are which edges exist. Blocks 2 and 3 are skipped because their left edge is
    /// the same edge as blocks 0 and 1's, already filtered by the 16-tall kernel.
    /// </remarks>
    public static void Vc1IHLoopFilter(Vc1Context v, Span<byte> plane, int dest,
                                       uint flags, int blockNum)
    {
        ref MpegEncContext s = ref v.S;
        int pq = v.Pq;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;

        if ((blockNum & 2) != 0)
            return;

        if ((flags & LeftEdge) == 0 || (blockNum & 5) == 1)
        {
            int dst = BlockDest(dest, blockNum, linesize);

            if (v.Fcm == FrameCodingMode.IlaceFrame)
            {
                if (blockNum > 3)
                {
                    Vc1Dsp.Vc1HLoopFilter4(plane, dst, 2 * uvlinesize, pq);
                    Vc1Dsp.Vc1HLoopFilter4(plane, dst + uvlinesize, 2 * uvlinesize, pq);
                }
                else
                {
                    Vc1Dsp.Vc1HLoopFilter8(plane, dst, 2 * linesize, pq);
                    Vc1Dsp.Vc1HLoopFilter8(plane, dst + linesize, 2 * linesize, pq);
                }
            }
            else
            {
                if (blockNum > 3)
                    Vc1Dsp.Vc1HLoopFilter8(plane, dst, uvlinesize, pq);
                else
                    Vc1Dsp.Vc1HLoopFilter16(plane, dst, linesize, pq);
            }
        }
    }

    /// <summary>
    /// <c>vc1_i_v_loop_filter</c> - deblocking across the horizontal edges of one block of an
    /// I macroblock.
    /// </summary>
    /// <remarks>
    /// Blocks 1 and 5 share their top edge with block 0, so they are skipped. Under FIELDTX the
    /// two luma rows of a macroblock are separate fields and the lower pair has no horizontal
    /// edge of its own within the frame, which is the <c>block_num &lt; 2 || !fieldtx</c> guard.
    /// </remarks>
    public static void Vc1IVLoopFilter(Vc1Context v, Span<byte> plane, int dest,
                                       uint flags, byte fieldtx, int blockNum)
    {
        ref MpegEncContext s = ref v.S;
        int pq = v.Pq;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;

        if ((blockNum & 5) == 1)
            return;

        if ((flags & TopEdge) == 0 || (blockNum & 2) != 0)
        {
            int dst = BlockDest(dest, blockNum, linesize);

            if (v.Fcm == FrameCodingMode.IlaceFrame)
            {
                if (blockNum > 3)
                {
                    Vc1Dsp.Vc1VLoopFilter8(plane, dst, 2 * uvlinesize, pq);
                    Vc1Dsp.Vc1VLoopFilter8(plane, dst + uvlinesize, 2 * uvlinesize, pq);
                }
                else if (blockNum < 2 || fieldtx == 0)
                {
                    Vc1Dsp.Vc1VLoopFilter16(plane, dst, 2 * linesize, pq);
                    Vc1Dsp.Vc1VLoopFilter16(plane, dst + linesize, 2 * linesize, pq);
                }
            }
            else
            {
                if (blockNum > 3)
                    Vc1Dsp.Vc1VLoopFilter8(plane, dst, uvlinesize, pq);
                else
                    Vc1Dsp.Vc1VLoopFilter16(plane, dst, linesize, pq);
            }
        }
    }

    /// <summary>
    /// <c>vc1_p_h_loop_filter</c> - deblocking across the vertical edges of one block of a
    /// P macroblock.
    /// </summary>
    /// <remarks>
    /// A P picture filters an edge in full only where the two sides genuinely differ: either
    /// side intra, or the motion vectors disagree. Where they agree the edge is interior to one
    /// moving region and only the coded sub-block edges are filtered, which is what the coded
    /// block pattern selects. <paramref name="cbp"/>, <paramref name="isIntra"/> and
    /// <paramref name="ttblk"/> are interior pointers: index 0 is this macroblock, index 1 the
    /// one to the right, and <c>mb_stride</c> the one below.
    /// </remarks>
    public static void Vc1PHLoopFilter(Vc1Context v, Span<byte> plane, int dest,
                                       ReadOnlySpan<uint> cbp, int cbpOff,
                                       ReadOnlySpan<byte> isIntra, int isIntraOff,
                                       ReadOnlySpan<short> mv, int mvOff,
                                       ReadOnlySpan<byte> mvF, int mvFOff,
                                       ReadOnlySpan<int> ttblk, int ttblkOff,
                                       uint flags, int blockNum)
    {
        ref MpegEncContext s = ref v.S;
        int pq = v.Pq;
        int linesizeY = (int)s.Linesize;
        uint leftCbp = cbp[cbpOff] >> (blockNum * 4), rightCbp;
        int leftIsIntra, rightIsIntra;
        int linesize = blockNum > 3 ? (int)s.Uvlinesize : linesizeY;

        int dst = BlockDest(dest, blockNum, linesizeY);

        if ((flags & RightEdge) == 0 || (blockNum & 5) == 0)
        {
            leftIsIntra = isIntra[isIntraOff] & (1 << blockNum);

            if (blockNum > 3)
            {
                rightIsIntra = isIntra[isIntraOff + 1] & (1 << blockNum);
                rightCbp = cbp[cbpOff + 1] >> (blockNum * 4);
            }
            else if ((blockNum & 1) != 0)
            {
                rightIsIntra = isIntra[isIntraOff + 1] & (1 << (blockNum - 1));
                rightCbp = cbp[cbpOff + 1] >> ((blockNum - 1) * 4);
            }
            else
            {
                rightIsIntra = isIntra[isIntraOff] & (1 << (blockNum + 1));
                rightCbp = cbp[cbpOff] >> ((blockNum + 1) * 4);
            }

            if (leftIsIntra != 0 || rightIsIntra != 0 ||
                mv[mvOff] != mv[mvOff + 2] || mv[mvOff + 1] != mv[mvOff + 3] ||
                (v.Fcm == FrameCodingMode.IlaceField && mvF[mvFOff] != mvF[mvFOff + 1]))
            {
                Vc1Dsp.Vc1HLoopFilter8(plane, dst + 8, linesize, pq);
            }
            else
            {
                uint idx = (leftCbp | (rightCbp >> 1)) & 5;
                if ((idx & 1) != 0)
                    Vc1Dsp.Vc1HLoopFilter4(plane, dst + 4 * linesize + 8, linesize, pq);
                if ((idx & 4) != 0)
                    Vc1Dsp.Vc1HLoopFilter4(plane, dst + 8, linesize, pq);
            }
        }

        int tt = ttblk[ttblkOff] >> (blockNum * 4) & 0xf;
        if (tt == (int)TransformType.Tt4X4 || tt == (int)TransformType.Tt4X8)
        {
            if ((leftCbp & 3) != 0)
                Vc1Dsp.Vc1HLoopFilter4(plane, dst + 4 * linesize + 4, linesize, pq);
            if ((leftCbp & 12) != 0)
                Vc1Dsp.Vc1HLoopFilter4(plane, dst + 4, linesize, pq);
        }
    }

    /// <summary>
    /// <c>vc1_p_v_loop_filter</c> - deblocking across the horizontal edges of one block of a
    /// P macroblock.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="Vc1PHLoopFilter"/>, except that the neighbour below is a whole
    /// row away: <c>mb_stride</c> entries for the per-macroblock arrays, <c>b8_stride</c> for the
    /// motion vectors, which are per 8x8 block.
    /// </remarks>
    public static void Vc1PVLoopFilter(Vc1Context v, Span<byte> plane, int dest,
                                       ReadOnlySpan<uint> cbp, int cbpOff,
                                       ReadOnlySpan<byte> isIntra, int isIntraOff,
                                       ReadOnlySpan<short> mv, int mvOff,
                                       ReadOnlySpan<byte> mvF, int mvFOff,
                                       ReadOnlySpan<int> ttblk, int ttblkOff,
                                       uint flags, int blockNum)
    {
        ref MpegEncContext s = ref v.S;
        int pq = v.Pq;
        int linesizeY = (int)s.Linesize;
        uint topCbp = cbp[cbpOff] >> (blockNum * 4), bottomCbp;
        int topIsIntra, bottomIsIntra;
        int linesize = blockNum > 3 ? (int)s.Uvlinesize : linesizeY;

        int dst = BlockDest(dest, blockNum, linesizeY);

        if ((flags & BottomEdge) == 0 || blockNum < 2)
        {
            topIsIntra = isIntra[isIntraOff] & (1 << blockNum);

            if (blockNum > 3)
            {
                bottomIsIntra = isIntra[isIntraOff + s.MbStride] & (1 << blockNum);
                bottomCbp = cbp[cbpOff + s.MbStride] >> (blockNum * 4);
            }
            else if (blockNum < 2)
            {
                bottomIsIntra = isIntra[isIntraOff] & (1 << (blockNum + 2));
                bottomCbp = cbp[cbpOff] >> ((blockNum + 2) * 4);
            }
            else
            {
                bottomIsIntra = isIntra[isIntraOff + s.MbStride] & (1 << (blockNum - 2));
                bottomCbp = cbp[cbpOff + s.MbStride] >> ((blockNum - 2) * 4);
            }

            // The neighbour below is mb_stride away for chroma, whose vectors are per
            // macroblock, and b8_stride away for luma, whose vectors are per 8x8 block.
            int below = blockNum > 3 ? s.MbStride : s.B8Stride;

            if (topIsIntra != 0 || bottomIsIntra != 0 ||
                mv[mvOff] != mv[mvOff + below * 2] ||
                mv[mvOff + 1] != mv[mvOff + below * 2 + 1] ||
                (v.Fcm == FrameCodingMode.IlaceField && mvF[mvFOff] != mvF[mvFOff + below]))
            {
                Vc1Dsp.Vc1VLoopFilter8(plane, dst + 8 * linesize, linesize, pq);
            }
            else
            {
                uint idx = (topCbp | (bottomCbp >> 2)) & 3;
                if ((idx & 1) != 0)
                    Vc1Dsp.Vc1VLoopFilter4(plane, dst + 8 * linesize + 4, linesize, pq);
                if ((idx & 2) != 0)
                    Vc1Dsp.Vc1VLoopFilter4(plane, dst + 8 * linesize, linesize, pq);
            }
        }

        int tt = ttblk[ttblkOff] >> (blockNum * 4) & 0xf;
        if (tt == (int)TransformType.Tt4X4 || tt == (int)TransformType.Tt8X4)
        {
            if ((topCbp & 5) != 0)
                Vc1Dsp.Vc1VLoopFilter4(plane, dst + 4 * linesize + 4, linesize, pq);
            if ((topCbp & 10) != 0)
                Vc1Dsp.Vc1VLoopFilter4(plane, dst + 4 * linesize, linesize, pq);
        }
    }

    /// <summary>
    /// <c>vc1_p_h_intfr_loop_filter</c> - vertical edges of one block of a P macroblock in an
    /// interlaced frame picture.
    /// </summary>
    /// <remarks>
    /// Every kernel here runs at twice the line size, because the rows of one field are every
    /// other row of the frame. FIELDTX decides whether a luma block's two fields are stored
    /// apart - then each is filtered 8 tall in one call - or interleaved, when the same edge
    /// takes two 4-tall calls one row apart.
    /// </remarks>
    public static void Vc1PHIntfrLoopFilter(Vc1Context v, Span<byte> plane, int dest,
                                            ReadOnlySpan<int> ttblk, int ttblkOff,
                                            uint flags, byte fieldtx, int blockNum)
    {
        ref MpegEncContext s = ref v.S;
        int pq = v.Pq;
        int linesizeY = (int)s.Linesize;
        int linesize = blockNum > 3 ? (int)s.Uvlinesize : linesizeY;

        int dst = BlockDest(dest, blockNum, linesizeY);

        int tt = ttblk[ttblkOff] >> (blockNum * 4) & 0xf;
        bool split = tt == (int)TransformType.Tt4X4 || tt == (int)TransformType.Tt4X8;

        if (blockNum < 4)
        {
            if (fieldtx != 0)
            {
                if (blockNum < 2)
                {
                    if (split)
                        Vc1Dsp.Vc1HLoopFilter8(plane, dst + 4, 2 * linesize, pq);
                    if ((flags & RightEdge) == 0 || blockNum == 0)
                        Vc1Dsp.Vc1HLoopFilter8(plane, dst + 8, 2 * linesize, pq);
                }
                else
                {
                    if (split)
                        Vc1Dsp.Vc1HLoopFilter8(plane, dst - 7 * linesize + 4, 2 * linesize, pq);
                    if ((flags & RightEdge) == 0 || blockNum == 2)
                        Vc1Dsp.Vc1HLoopFilter8(plane, dst - 7 * linesize + 8, 2 * linesize, pq);
                }
            }
            else
            {
                if (split)
                {
                    Vc1Dsp.Vc1HLoopFilter4(plane, dst + 4, 2 * linesize, pq);
                    Vc1Dsp.Vc1HLoopFilter4(plane, dst + linesize + 4, 2 * linesize, pq);
                }
                if ((flags & RightEdge) == 0 || (blockNum & 5) == 0)
                {
                    Vc1Dsp.Vc1HLoopFilter4(plane, dst + 8, 2 * linesize, pq);
                    Vc1Dsp.Vc1HLoopFilter4(plane, dst + linesize + 8, 2 * linesize, pq);
                }
            }
        }
        else
        {
            if (split)
            {
                Vc1Dsp.Vc1HLoopFilter4(plane, dst + 4, 2 * linesize, pq);
                Vc1Dsp.Vc1HLoopFilter4(plane, dst + linesize + 4, 2 * linesize, pq);
            }
            if ((flags & RightEdge) == 0)
            {
                Vc1Dsp.Vc1HLoopFilter4(plane, dst + 8, 2 * linesize, pq);
                Vc1Dsp.Vc1HLoopFilter4(plane, dst + linesize + 8, 2 * linesize, pq);
            }
        }
    }

    /// <summary>
    /// <c>vc1_p_v_intfr_loop_filter</c> - horizontal edges of one block of a P macroblock in an
    /// interlaced frame picture.
    /// </summary>
    /// <remarks>
    /// Each edge is filtered once per field, the two calls one line apart at twice the line
    /// size. Under FIELDTX the lower luma pair's rows have been moved, so its edges are at
    /// <c>linesize</c> and <c>9 * linesize</c> rather than 8 and 16.
    /// </remarks>
    public static void Vc1PVIntfrLoopFilter(Vc1Context v, Span<byte> plane, int dest,
                                            ReadOnlySpan<int> ttblk, int ttblkOff,
                                            uint flags, byte fieldtx, int blockNum)
    {
        ref MpegEncContext s = ref v.S;
        int pq = v.Pq;
        int linesizeY = (int)s.Linesize;
        int linesize = blockNum > 3 ? (int)s.Uvlinesize : linesizeY;

        int dst = BlockDest(dest, blockNum, linesizeY);

        int tt = ttblk[ttblkOff] >> (blockNum * 4) & 0xf;
        bool split = tt == (int)TransformType.Tt4X4 || tt == (int)TransformType.Tt8X4;

        if (blockNum < 4)
        {
            if (fieldtx != 0)
            {
                if (blockNum < 2)
                {
                    if (split)
                        Vc1Dsp.Vc1VLoopFilter8(plane, dst + 8 * linesize, 2 * linesize, pq);
                    if ((flags & BottomEdge) == 0)
                        Vc1Dsp.Vc1VLoopFilter8(plane, dst + 16 * linesize, 2 * linesize, pq);
                }
                else
                {
                    if (split)
                        Vc1Dsp.Vc1VLoopFilter8(plane, dst + linesize, 2 * linesize, pq);
                    if ((flags & BottomEdge) == 0)
                        Vc1Dsp.Vc1VLoopFilter8(plane, dst + 9 * linesize, 2 * linesize, pq);
                }
            }
            else
            {
                if (blockNum < 2)
                {
                    if ((flags & TopEdge) == 0 && split)
                    {
                        Vc1Dsp.Vc1VLoopFilter8(plane, dst + 4 * linesize, 2 * linesize, pq);
                        Vc1Dsp.Vc1VLoopFilter8(plane, dst + 5 * linesize, 2 * linesize, pq);
                    }
                    Vc1Dsp.Vc1VLoopFilter8(plane, dst + 8 * linesize, 2 * linesize, pq);
                    Vc1Dsp.Vc1VLoopFilter8(plane, dst + 9 * linesize, 2 * linesize, pq);
                }
                else if ((flags & BottomEdge) == 0)
                {
                    if (split)
                    {
                        Vc1Dsp.Vc1VLoopFilter8(plane, dst + 4 * linesize, 2 * linesize, pq);
                        Vc1Dsp.Vc1VLoopFilter8(plane, dst + 5 * linesize, 2 * linesize, pq);
                    }
                    Vc1Dsp.Vc1VLoopFilter8(plane, dst + 8 * linesize, 2 * linesize, pq);
                    Vc1Dsp.Vc1VLoopFilter8(plane, dst + 9 * linesize, 2 * linesize, pq);
                }
            }
        }
        else
        {
            if ((flags & BottomEdge) == 0)
            {
                if ((flags & TopEdge) == 0 && split)
                {
                    Vc1Dsp.Vc1VLoopFilter8(plane, dst + 4 * linesize, 2 * linesize, pq);
                    Vc1Dsp.Vc1VLoopFilter8(plane, dst + 5 * linesize, 2 * linesize, pq);
                }
                Vc1Dsp.Vc1VLoopFilter8(plane, dst + 8 * linesize, 2 * linesize, pq);
                Vc1Dsp.Vc1VLoopFilter8(plane, dst + 9 * linesize, 2 * linesize, pq);
            }
        }
    }

    /// <summary>
    /// <c>vc1_b_h_intfi_loop_filter</c> - vertical edges of one block of a B macroblock in an
    /// interlaced field picture.
    /// </summary>
    /// <remarks>
    /// A field picture's two fields are coded as separate pictures, so the rows are contiguous
    /// again and the strides are plain. The block edge is always filtered; only the interior
    /// sub-block edges consult the coded block pattern, and they consult this macroblock's own
    /// rather than a neighbour's.
    /// </remarks>
    public static void Vc1BHIntfiLoopFilter(Vc1Context v, Span<byte> plane, int dest,
                                            ReadOnlySpan<uint> cbp, int cbpOff,
                                            ReadOnlySpan<int> ttblk, int ttblkOff,
                                            uint flags, int blockNum)
    {
        ref MpegEncContext s = ref v.S;
        int pq = v.Pq;
        int linesizeY = (int)s.Linesize;
        uint blockCbp = cbp[cbpOff] >> (blockNum * 4);
        int linesize = blockNum > 3 ? (int)s.Uvlinesize : linesizeY;

        int dst = BlockDest(dest, blockNum, linesizeY);

        if ((flags & RightEdge) == 0 || (blockNum & 5) == 0)
            Vc1Dsp.Vc1HLoopFilter8(plane, dst + 8, linesize, pq);

        int tt = ttblk[ttblkOff] >> (blockNum * 4) & 0xf;
        if (tt == (int)TransformType.Tt4X4 || tt == (int)TransformType.Tt4X8)
        {
            uint idx = (blockCbp | (blockCbp >> 1)) & 5;
            if ((idx & 1) != 0)
                Vc1Dsp.Vc1HLoopFilter4(plane, dst + 4 * linesize + 4, linesize, pq);
            if ((idx & 4) != 0)
                Vc1Dsp.Vc1HLoopFilter4(plane, dst + 4, linesize, pq);
        }
    }

    /// <summary>
    /// <c>vc1_b_v_intfi_loop_filter</c> - horizontal edges of one block of a B macroblock in an
    /// interlaced field picture.
    /// </summary>
    public static void Vc1BVIntfiLoopFilter(Vc1Context v, Span<byte> plane, int dest,
                                            ReadOnlySpan<uint> cbp, int cbpOff,
                                            ReadOnlySpan<int> ttblk, int ttblkOff,
                                            uint flags, int blockNum)
    {
        ref MpegEncContext s = ref v.S;
        int pq = v.Pq;
        int linesizeY = (int)s.Linesize;
        uint blockCbp = cbp[cbpOff] >> (blockNum * 4);
        int linesize = blockNum > 3 ? (int)s.Uvlinesize : linesizeY;

        int dst = BlockDest(dest, blockNum, linesizeY);

        if ((flags & BottomEdge) == 0 || blockNum < 2)
            Vc1Dsp.Vc1VLoopFilter8(plane, dst + 8 * linesize, linesize, pq);

        int tt = ttblk[ttblkOff] >> (blockNum * 4) & 0xf;
        if (tt == (int)TransformType.Tt4X4 || tt == (int)TransformType.Tt8X4)
        {
            uint idx = (blockCbp | (blockCbp >> 2)) & 3;
            if ((idx & 1) != 0)
                Vc1Dsp.Vc1VLoopFilter4(plane, dst + 4 * linesize + 4, linesize, pq);
            if ((idx & 2) != 0)
                Vc1Dsp.Vc1VLoopFilter4(plane, dst + 4 * linesize, linesize, pq);
        }
    }
    // --- The drivers --------------------------------------------------------------------------
    //
    // Within a macroblock the vertical loop filter always runs before the horizontal, and an
    // edge can only be filtered once both macroblocks either side of it are reconstructed. The
    // drivers get both by trailing the decoding loop: each one runs on a macroblock decoded one
    // or two rows and columns ago, which is why every dest below is s->dest[0] minus some
    // multiple of a macroblock. The comments on each say by how much.
    //
    // CONFIG_GRAY is off in this build, so block_count is always six.

    private const int BlockCount = 6;

    /// <summary>
    /// <c>ff_vc1_i_loop_filter</c> - deblocking for an I picture, which filters every edge.
    /// </summary>
    /// <remarks>
    /// Trails the overlap filter by one row and one column, and the decoding loop by two of
    /// each. The vertical pass runs on the top and internal horizontal borders of the last
    /// overlap filtered macroblock; the bottom edge waits for the next row.
    /// </remarks>
    public static void Vc1ILoopFilter(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        Span<byte> frame = s.CurPic.Frame!;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;
        int mbPos = s.MbX + s.MbY * s.MbStride;
        byte[] fieldtxPlane = v.FieldtxPlane!;
        int dest;
        uint flags;
        byte fieldtx;

        if (s.FirstSliceLine == 0)
        {
            dest = s.Dest[0] - 16 * linesize - 16;
            flags = s.MbY == s.StartMbY + 1 ? TopEdge : 0;
            if (s.MbX != 0)
            {
                fieldtx = fieldtxPlane[mbPos - s.MbStride - 1];
                for (int i = 0; i < BlockCount; i++)
                    Vc1IVLoopFilter(v, frame,
                        i > 3 ? s.Dest[i - 3] - 8 * uvlinesize - 8 : dest, flags, fieldtx, i);
            }
            if (s.MbX == v.EndMbX - 1)
            {
                dest += 16;
                fieldtx = fieldtxPlane[mbPos - s.MbStride];
                for (int i = 0; i < BlockCount; i++)
                    Vc1IVLoopFilter(v, frame,
                        i > 3 ? s.Dest[i - 3] - 8 * uvlinesize : dest, flags, fieldtx, i);
            }
        }
        if (s.MbY == s.EndMbY - 1)
        {
            dest = s.Dest[0] - 16;
            flags = s.FirstSliceLine != 0 ? TopEdge | BottomEdge : BottomEdge;
            if (s.MbX != 0)
            {
                fieldtx = fieldtxPlane[mbPos - 1];
                for (int i = 0; i < BlockCount; i++)
                    Vc1IVLoopFilter(v, frame,
                        i > 3 ? s.Dest[i - 3] - 8 : dest, flags, fieldtx, i);
            }
            if (s.MbX == v.EndMbX - 1)
            {
                dest += 16;
                fieldtx = fieldtxPlane[mbPos];
                for (int i = 0; i < BlockCount; i++)
                    Vc1IVLoopFilter(v, frame, i > 3 ? s.Dest[i - 3] : dest, flags, fieldtx, i);
            }
        }

        if (s.MbY >= s.StartMbY + 2)
        {
            dest = s.Dest[0] - 32 * linesize - 16;
            if (s.MbX != 0)
            {
                flags = s.MbX == 1 ? LeftEdge : 0;
                for (int i = 0; i < BlockCount; i++)
                    Vc1IHLoopFilter(v, frame,
                        i > 3 ? s.Dest[i - 3] - 16 * uvlinesize - 8 : dest, flags, i);
            }
            if (s.MbX == v.EndMbX - 1)
            {
                dest += 16;
                flags = s.MbX == 0 ? LeftEdge | RightEdge : RightEdge;
                for (int i = 0; i < BlockCount; i++)
                    Vc1IHLoopFilter(v, frame,
                        i > 3 ? s.Dest[i - 3] - 16 * uvlinesize : dest, flags, i);
            }
        }
        if (s.MbY == s.EndMbY - 1)
        {
            if (s.MbY >= s.StartMbY + 1)
            {
                dest = s.Dest[0] - 16 * linesize - 16;
                if (s.MbX != 0)
                {
                    flags = s.MbX == 1 ? LeftEdge : 0;
                    for (int i = 0; i < BlockCount; i++)
                        Vc1IHLoopFilter(v, frame,
                            i > 3 ? s.Dest[i - 3] - 8 * uvlinesize - 8 : dest, flags, i);
                }
                if (s.MbX == v.EndMbX - 1)
                {
                    flags = s.MbX == 0 ? LeftEdge | RightEdge : RightEdge;
                    dest += 16;
                    for (int i = 0; i < BlockCount; i++)
                        Vc1IHLoopFilter(v, frame,
                            i > 3 ? s.Dest[i - 3] - 8 * uvlinesize : dest, flags, i);
                }
            }
            dest = s.Dest[0] - 16;
            if (s.MbX != 0)
            {
                flags = s.MbX == 1 ? LeftEdge : 0;
                for (int i = 0; i < BlockCount; i++)
                    Vc1IHLoopFilter(v, frame, i > 3 ? s.Dest[i - 3] - 8 : dest, flags, i);
            }
            if (s.MbX == v.EndMbX - 1)
            {
                dest += 16;
                flags = s.MbX == 0 ? LeftEdge | RightEdge : RightEdge;
                for (int i = 0; i < BlockCount; i++)
                    Vc1IHLoopFilter(v, frame, i > 3 ? s.Dest[i - 3] : dest, flags, i);
            }
        }
    }

    /// <summary>
    /// <c>ff_vc1_p_loop_filter</c> - deblocking for a progressive P picture.
    /// </summary>
    /// <remarks>
    /// Trails the overlap filter by one row and one column, and the decoding loop by two of
    /// each. Every call needs the per-macroblock arrays at the macroblock being filtered, and
    /// the motion vectors at the 8x8 block for luma or the macroblock for chroma, which is why
    /// each arm names six offsets rather than one.
    /// </remarks>
    public static void Vc1PLoopFilter(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        byte[] frame = s.CurPic.Frame!;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;

        uint[] cbp = v.CbpBase!;
        byte[] isIntra = v.IsIntraBase!;
        short[] lumaMv = v.LumaMvBase!;
        int[] ttblk = v.TtblkBase!;
        short[] motionVal = s.CurPic.MotionValL0!;
        int motionValOff = s.CurPic.MotionValOffsetL0;
        byte[] mvF = v.MvFBase!;
        int mvFOff = v.MvFOffset[0];

        // mbOffset picks the macroblock within the per-macroblock arrays; blockOffset and
        // mbMvOffset pick the motion vectors for one block of it.
        void VerticalPass(int dest, int chromaDest, int mbOffset, int blockOffset,
                          int mbMvOffset, uint flags)
        {
            for (int i = 0; i < BlockCount; i++)
                Vc1PVLoopFilter(v, frame,
                    i > 3 ? v.S.Dest[i - 3] + chromaDest : dest,
                    cbp, v.CbpOffset + mbOffset,
                    isIntra, v.IsIntraOffset + mbOffset,
                    i > 3 ? lumaMv : motionVal,
                    i > 3 ? v.LumaMvOffset + mbOffset * 2
                          : motionValOff + (v.S.BlockIndex[i] + blockOffset + v.BlocksOff) * 2,
                    mvF,
                    i > 3 ? mvFOff + v.S.BlockIndex[i] + mbMvOffset + v.MbOff
                          : mvFOff + v.S.BlockIndex[i] + blockOffset + v.BlocksOff,
                    ttblk, v.TtblkOffset + mbOffset,
                    flags, i);
        }

        void HorizontalPass(int dest, int chromaDest, int mbOffset, int blockOffset,
                            int mbMvOffset, uint flags)
        {
            for (int i = 0; i < BlockCount; i++)
                Vc1PHLoopFilter(v, frame,
                    i > 3 ? v.S.Dest[i - 3] + chromaDest : dest,
                    cbp, v.CbpOffset + mbOffset,
                    isIntra, v.IsIntraOffset + mbOffset,
                    i > 3 ? lumaMv : motionVal,
                    i > 3 ? v.LumaMvOffset + mbOffset * 2
                          : motionValOff + (v.S.BlockIndex[i] + blockOffset + v.BlocksOff) * 2,
                    mvF,
                    i > 3 ? mvFOff + v.S.BlockIndex[i] + mbMvOffset + v.MbOff
                          : mvFOff + v.S.BlockIndex[i] + blockOffset + v.BlocksOff,
                    ttblk, v.TtblkOffset + mbOffset,
                    flags, i);
        }

        if (s.MbY >= s.StartMbY + 2)
        {
            if (s.MbX != 0)
                VerticalPass(s.Dest[0] - 32 * linesize - 16, -16 * uvlinesize - 8,
                             s.MbX - 2 * s.MbStride - 1, -4 * s.B8Stride - 2,
                             -2 * s.MbStride - 1,
                             s.MbY == s.StartMbY + 2 ? TopEdge : 0);
            if (s.MbX == s.MbWidth - 1)
                VerticalPass(s.Dest[0] - 32 * linesize, -16 * uvlinesize,
                             s.MbX - 2 * s.MbStride, -4 * s.B8Stride,
                             -2 * s.MbStride,
                             s.MbY == s.StartMbY + 2 ? TopEdge : 0);
        }
        if (s.MbY == s.EndMbY - 1)
        {
            if (s.MbX != 0)
            {
                if (s.MbY >= s.StartMbY + 1)
                    VerticalPass(s.Dest[0] - 16 * linesize - 16, -8 * uvlinesize - 8,
                                 s.MbX - s.MbStride - 1, -2 * s.B8Stride - 2,
                                 -s.MbStride - 1,
                                 s.MbY == s.StartMbY + 1 ? TopEdge : 0);
                VerticalPass(s.Dest[0] - 16, -8,
                             s.MbX - 1, -2, -1,
                             s.MbY == s.StartMbY ? TopEdge | BottomEdge : BottomEdge);
            }
            if (s.MbX == s.MbWidth - 1)
            {
                if (s.MbY >= s.StartMbY + 1)
                    VerticalPass(s.Dest[0] - 16 * linesize, -8 * uvlinesize,
                                 s.MbX - s.MbStride, -2 * s.B8Stride,
                                 -s.MbStride,
                                 s.MbY == s.StartMbY + 1 ? TopEdge : 0);
                VerticalPass(s.Dest[0], 0,
                             s.MbX, 0, 0,
                             s.MbY == s.StartMbY ? TopEdge | BottomEdge : BottomEdge);
            }
        }

        if (s.MbY >= s.StartMbY + 2)
        {
            if (s.MbX >= 2)
                HorizontalPass(s.Dest[0] - 32 * linesize - 32, -16 * uvlinesize - 16,
                               s.MbX - 2 * s.MbStride - 2, -4 * s.B8Stride - 4,
                               -2 * s.MbStride - 2,
                               s.MbX == 2 ? LeftEdge : 0);
            if (s.MbX == s.MbWidth - 1)
            {
                if (s.MbX >= 1)
                    HorizontalPass(s.Dest[0] - 32 * linesize - 16, -16 * uvlinesize - 8,
                                   s.MbX - 2 * s.MbStride - 1, -4 * s.B8Stride - 2,
                                   -2 * s.MbStride - 1,
                                   s.MbX == 1 ? LeftEdge : 0);
                HorizontalPass(s.Dest[0] - 32 * linesize, -16 * uvlinesize,
                               s.MbX - 2 * s.MbStride, -4 * s.B8Stride,
                               -2 * s.MbStride,
                               s.MbX != 0 ? RightEdge : LeftEdge | RightEdge);
            }
        }
        if (s.MbY == s.EndMbY - 1)
        {
            if (s.MbY >= s.StartMbY + 1)
            {
                if (s.MbX >= 2)
                    HorizontalPass(s.Dest[0] - 16 * linesize - 32, -8 * uvlinesize - 16,
                                   s.MbX - s.MbStride - 2, -2 * s.B8Stride - 4,
                                   -s.MbStride - 2,
                                   s.MbX == 2 ? LeftEdge : 0);
                if (s.MbX == s.MbWidth - 1)
                {
                    if (s.MbX >= 1)
                        HorizontalPass(s.Dest[0] - 16 * linesize - 16, -8 * uvlinesize - 8,
                                       s.MbX - s.MbStride - 1, -2 * s.B8Stride - 2,
                                       -s.MbStride - 1,
                                       s.MbX == 1 ? LeftEdge : 0);
                    HorizontalPass(s.Dest[0] - 16 * linesize, -8 * uvlinesize,
                                   s.MbX - s.MbStride, -2 * s.B8Stride,
                                   -s.MbStride,
                                   s.MbX != 0 ? RightEdge : LeftEdge | RightEdge);
                }
            }
            if (s.MbX >= 2)
                HorizontalPass(s.Dest[0] - 32, -16,
                               s.MbX - 2, -4, -2,
                               s.MbX == 2 ? LeftEdge : 0);
            if (s.MbX == s.MbWidth - 1)
            {
                if (s.MbX >= 1)
                    HorizontalPass(s.Dest[0] - 16, -8,
                                   s.MbX - 1, -2, -1,
                                   s.MbX == 1 ? LeftEdge : 0);
                HorizontalPass(s.Dest[0], 0,
                               s.MbX, 0, 0,
                               s.MbX != 0 ? RightEdge : LeftEdge | RightEdge);
            }
        }
    }

    /// <summary>
    /// <c>ff_vc1_p_intfr_loop_filter</c> - deblocking for a P picture in an interlaced frame.
    /// </summary>
    /// <remarks>
    /// Trails the overlap filter by two rows and one column, and the decoding loop by two of
    /// each. These filters consult only the transform type and FIELDTX, so an arm needs one
    /// macroblock offset rather than six.
    /// </remarks>
    public static void Vc1PIntfrLoopFilter(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        byte[] frame = s.CurPic.Frame!;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;
        int mbPos = s.MbX + s.MbY * s.MbStride;
        int[] ttblk = v.TtblkBase!;
        byte[] fieldtxPlane = v.FieldtxPlane!;

        void VerticalPass(int dest, int chromaDest, int mbOffset, uint flags, int fieldtxAt)
        {
            byte fieldtx = fieldtxPlane[fieldtxAt];
            for (int i = 0; i < BlockCount; i++)
                Vc1PVIntfrLoopFilter(v, frame, i > 3 ? v.S.Dest[i - 3] + chromaDest : dest,
                    ttblk, v.TtblkOffset + mbOffset, flags, fieldtx, i);
        }

        void HorizontalPass(int dest, int chromaDest, int mbOffset, uint flags, int fieldtxAt)
        {
            byte fieldtx = fieldtxPlane[fieldtxAt];
            for (int i = 0; i < BlockCount; i++)
                Vc1PHIntfrLoopFilter(v, frame, i > 3 ? v.S.Dest[i - 3] + chromaDest : dest,
                    ttblk, v.TtblkOffset + mbOffset, flags, fieldtx, i);
        }

        if (s.MbX != 0 && s.MbY >= s.StartMbY + 1)
            VerticalPass(s.Dest[0] - 16 * linesize - 16, -8 * uvlinesize - 8,
                         s.MbX - s.MbStride - 1,
                         s.MbY == s.StartMbY + 1 ? TopEdge : 0,
                         mbPos - s.MbStride - 1);
        if (s.MbX == s.MbWidth - 1 && s.MbY >= s.StartMbY + 1)
            VerticalPass(s.Dest[0] - 16 * linesize, -8 * uvlinesize,
                         s.MbX - s.MbStride,
                         s.MbY == s.StartMbY + 1 ? TopEdge : 0,
                         mbPos - s.MbStride);
        if (s.MbY == s.EndMbY - 1)
        {
            if (s.MbX != 0)
                VerticalPass(s.Dest[0] - 16, -8,
                             s.MbX - 1,
                             s.MbY == s.StartMbY ? TopEdge | BottomEdge : BottomEdge,
                             mbPos - 1);
            if (s.MbX == s.MbWidth - 1)
                VerticalPass(s.Dest[0], 0,
                             s.MbX,
                             s.MbY == s.StartMbY ? TopEdge | BottomEdge : BottomEdge,
                             mbPos);
        }

        if (s.MbY >= s.StartMbY + 2)
        {
            if (s.MbX >= 2)
                HorizontalPass(s.Dest[0] - 32 * linesize - 32, -16 * uvlinesize - 16,
                               s.MbX - 2 * s.MbStride - 2,
                               s.MbX == 2 ? LeftEdge : 0,
                               mbPos - 2 * s.MbStride - 2);
            if (s.MbX == s.MbWidth - 1)
            {
                if (s.MbX >= 1)
                    HorizontalPass(s.Dest[0] - 32 * linesize - 16, -16 * uvlinesize - 8,
                                   s.MbX - 2 * s.MbStride - 1,
                                   s.MbX == 1 ? LeftEdge : 0,
                                   mbPos - 2 * s.MbStride - 1);
                HorizontalPass(s.Dest[0] - 32 * linesize, -16 * uvlinesize,
                               s.MbX - 2 * s.MbStride,
                               s.MbX != 0 ? RightEdge : LeftEdge | RightEdge,
                               mbPos - 2 * s.MbStride);
            }
        }
        if (s.MbY == s.EndMbY - 1)
        {
            if (s.MbY >= s.StartMbY + 1)
            {
                if (s.MbX >= 2)
                    HorizontalPass(s.Dest[0] - 16 * linesize - 32, -8 * uvlinesize - 16,
                                   s.MbX - s.MbStride - 2,
                                   s.MbX == 2 ? LeftEdge : 0,
                                   mbPos - s.MbStride - 2);
                if (s.MbX == s.MbWidth - 1)
                {
                    if (s.MbX >= 1)
                        HorizontalPass(s.Dest[0] - 16 * linesize - 16, -8 * uvlinesize - 8,
                                       s.MbX - s.MbStride - 1,
                                       s.MbX == 1 ? LeftEdge : 0,
                                       mbPos - s.MbStride - 1);
                    HorizontalPass(s.Dest[0] - 16 * linesize, -8 * uvlinesize,
                                   s.MbX - s.MbStride,
                                   s.MbX != 0 ? RightEdge : LeftEdge | RightEdge,
                                   mbPos - s.MbStride);
                }
            }
            if (s.MbX >= 2)
                HorizontalPass(s.Dest[0] - 32, -16,
                               s.MbX - 2,
                               s.MbX == 2 ? LeftEdge : 0,
                               mbPos - 2);
            if (s.MbX == s.MbWidth - 1)
            {
                if (s.MbX >= 1)
                    HorizontalPass(s.Dest[0] - 16, -8,
                                   s.MbX - 1,
                                   s.MbX == 1 ? LeftEdge : 0,
                                   mbPos - 1);
                HorizontalPass(s.Dest[0], 0,
                               s.MbX,
                               s.MbX != 0 ? RightEdge : LeftEdge | RightEdge,
                               mbPos);
            }
        }
    }

    /// <summary>
    /// <c>ff_vc1_b_intfi_loop_filter</c> - deblocking for a B picture in an interlaced field.
    /// </summary>
    /// <remarks>
    /// A field picture's rows are contiguous, so this one trails the decoding loop by only one
    /// row and one column.
    /// </remarks>
    public static void Vc1BIntfiLoopFilter(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        Span<byte> frame = s.CurPic.Frame!;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;
        uint[] cbp = v.CbpBase!;
        int[] ttblk = v.TtblkBase!;
        int dest;
        uint flags;

        if (s.FirstSliceLine == 0)
        {
            dest = s.Dest[0] - 16 * linesize;
            int mbOffset = s.MbX - s.MbStride;
            flags = s.MbY == s.StartMbY + 1 ? TopEdge : 0;
            for (int i = 0; i < BlockCount; i++)
                Vc1BVIntfiLoopFilter(v, frame,
                    i > 3 ? s.Dest[i - 3] - 8 * uvlinesize : dest,
                    cbp, v.CbpOffset + mbOffset, ttblk, v.TtblkOffset + mbOffset, flags, i);
        }
        if (s.MbY == s.EndMbY - 1)
        {
            dest = s.Dest[0];
            int mbOffset = s.MbX;
            flags = s.FirstSliceLine != 0 ? TopEdge | BottomEdge : BottomEdge;
            for (int i = 0; i < BlockCount; i++)
                Vc1BVIntfiLoopFilter(v, frame, i > 3 ? s.Dest[i - 3] : dest,
                    cbp, v.CbpOffset + mbOffset, ttblk, v.TtblkOffset + mbOffset, flags, i);
        }

        if (s.FirstSliceLine == 0)
        {
            dest = s.Dest[0] - 16 * linesize - 16;
            int mbOffset = s.MbX - s.MbStride - 1;
            if (s.MbX != 0)
            {
                flags = s.MbX == 1 ? LeftEdge : 0;
                for (int i = 0; i < BlockCount; i++)
                    Vc1BHIntfiLoopFilter(v, frame,
                        i > 3 ? s.Dest[i - 3] - 8 * uvlinesize - 8 : dest,
                        cbp, v.CbpOffset + mbOffset, ttblk, v.TtblkOffset + mbOffset, flags, i);
            }
            if (s.MbX == s.MbWidth - 1)
            {
                dest += 16;
                mbOffset++;
                flags = s.MbX == 0 ? LeftEdge | RightEdge : RightEdge;
                for (int i = 0; i < BlockCount; i++)
                    Vc1BHIntfiLoopFilter(v, frame,
                        i > 3 ? s.Dest[i - 3] - 8 * uvlinesize : dest,
                        cbp, v.CbpOffset + mbOffset, ttblk, v.TtblkOffset + mbOffset, flags, i);
            }
        }
        if (s.MbY == s.EndMbY - 1)
        {
            dest = s.Dest[0] - 16;
            int mbOffset = s.MbX - 1;
            if (s.MbX != 0)
            {
                flags = s.MbX == 1 ? LeftEdge : 0;
                for (int i = 0; i < BlockCount; i++)
                    Vc1BHIntfiLoopFilter(v, frame, i > 3 ? s.Dest[i - 3] - 8 : dest,
                        cbp, v.CbpOffset + mbOffset, ttblk, v.TtblkOffset + mbOffset, flags, i);
            }
            if (s.MbX == s.MbWidth - 1)
            {
                dest += 16;
                mbOffset++;
                flags = s.MbX == 0 ? LeftEdge | RightEdge : RightEdge;
                for (int i = 0; i < BlockCount; i++)
                    Vc1BHIntfiLoopFilter(v, frame, i > 3 ? s.Dest[i - 3] : dest,
                        cbp, v.CbpOffset + mbOffset, ttblk, v.TtblkOffset + mbOffset, flags, i);
            }
        }
    }
}

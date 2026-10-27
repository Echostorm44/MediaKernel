// VP9 block decoding — superblock partitioning, mode parsing, coefficient decode
// Ported from FFmpeg libavcodec/vp9block.c + vp9.c (decode_sb)
// This is a partial class extension of Vp9Decoder.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vp9;

public partial class Vp9Decoder
{
    // --- Lookup tables (from decode_mode) ---

    private static readonly byte[] LeftCtx = { 0x0, 0x8, 0x0, 0x8, 0xc, 0x8, 0xc, 0xe, 0xc, 0xe, 0xf, 0xe, 0xf };
    private static readonly byte[] AboveCtx = { 0x0, 0x0, 0x8, 0x8, 0x8, 0xc, 0xc, 0xc, 0xe, 0xe, 0xe, 0xf, 0xf };
    private static readonly byte[] MaxTxForBlock = { 3, 3, 3, 3, 2, 2, 2, 1, 1, 1, 0, 0, 0 };
    private static readonly byte[] SizeGroupLut = { 3, 3, 3, 3, 2, 2, 2, 1, 1, 1 };

    // inter_modectx_lut[14][14]
    private static readonly byte[,] InterModeCtxLut =
    {
        { 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5 },
        { 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5 },
        { 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5 },
        { 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5 },
        { 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5 },
        { 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5 },
        { 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5 },
        { 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5 },
        { 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5 },
        { 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5 },
        { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 2, 2, 1, 3 },
        { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 2, 2, 1, 3 },
        { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 1, 1, 0, 3 },
        { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 3, 3, 3, 4 },
    };

    // Band counts per tx size for coefficient decoding
    private static readonly short[][] BandCounts = new short[][]
    {
        new short[] { 1, 2, 3, 4,  3,  3  },  // TX_4X4:   sum=16
        new short[] { 1, 2, 3, 4, 11, 43  },  // TX_8X8:   sum=64
        new short[] { 1, 2, 3, 4, 11, 235 },  // TX_16X16: sum=256
        new short[] { 1, 2, 3, 4, 11, 1003 }, // TX_32X32: sum=1024
    };

    // Block-size-to-sub-block-offset table for inter modes
    private static readonly byte[] BlockSizeOffsets = { 3, 0, 0, 1, 0, 0, 0, 0, 0, 0 };

    // ==================== Scan order helpers ====================

    /// <summary>
    /// Get scan order for a given tx size and tx type.
    /// ff_vp9_scans[tx][txType] → flat array of scan indices.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<short> GetScanOrder(int tx, int txType)
    {
        return (tx, txType) switch
        {
            (0, 0) => Vp9Data.DefaultScan4X4,
            (0, 1) => Vp9Data.ColScan4X4,     // txType 1 → col scan
            (0, 2) => Vp9Data.RowScan4X4,     // txType 2 → row scan
            (0, 3) => Vp9Data.DefaultScan4X4,  // ADST_ADST → default
            (1, 0) => Vp9Data.DefaultScan8X8,
            (1, 1) => Vp9Data.ColScan8X8,     // txType 1 → col scan
            (1, 2) => Vp9Data.RowScan8X8,     // txType 2 → row scan
            (1, 3) => Vp9Data.DefaultScan8X8,
            (2, 0) => Vp9Data.DefaultScan16X16,
            (2, 1) => Vp9Data.ColScan16X16,   // txType 1 → col scan
            (2, 2) => Vp9Data.RowScan16X16,   // txType 2 → row scan
            (2, 3) => Vp9Data.DefaultScan16X16,
            (3, _) => Vp9Data.DefaultScan32X32, // 32x32 always default
            (4, _) => Vp9Data.DefaultScan4X4,   // lossless always 4x4 default
            _ => Vp9Data.DefaultScan4X4,
        };
    }

    /// <summary>
    /// Get neighbor table for scan order (same indexing as GetScanOrder).
    /// Each entry is 2 shorts (neighbor0, neighbor1) used for nnz context.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<short> GetScanNeighbors(int tx, int txType)
    {
        return (tx, txType) switch
        {
            (0, 0) => Vp9Data.DefaultScan4X4Nb,
            (0, 1) => Vp9Data.ColScan4X4Nb,     // txType 1 → col scan
            (0, 2) => Vp9Data.RowScan4X4Nb,     // txType 2 → row scan
            (0, 3) => Vp9Data.DefaultScan4X4Nb,
            (1, 0) => Vp9Data.DefaultScan8X8Nb,
            (1, 1) => Vp9Data.ColScan8X8Nb,     // txType 1 → col scan
            (1, 2) => Vp9Data.RowScan8X8Nb,     // txType 2 → row scan
            (1, 3) => Vp9Data.DefaultScan8X8Nb,
            (2, 0) => Vp9Data.DefaultScan16X16Nb,
            (2, 1) => Vp9Data.ColScan16X16Nb,   // txType 1 → col scan
            (2, 2) => Vp9Data.RowScan16X16Nb,   // txType 2 → row scan
            (2, 3) => Vp9Data.DefaultScan16X16Nb,
            (3, _) => Vp9Data.DefaultScan32X32Nb,
            (4, _) => Vp9Data.DefaultScan4X4Nb,
            _ => Vp9Data.DefaultScan4X4Nb,
        };
    }

    /// <summary>
    /// Get block width/height in 8-pel units. table=0 for pixels, table=1 for blocks.
    /// ff_vp9_bwh_tab[table][blockSize][dim]
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetBwh(int table, int blockSize, int dim)
    {
        return Vp9Data.BlockWidthHeight[table * 26 + blockSize * 2 + dim];
    }

    // ==================== Fill context helpers ====================

    /// <summary>Set n bytes to value v starting at array[offset].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillContext(byte[] array, int offset, int n, byte value)
    {
        array.AsSpan(offset, n).Fill(value);
    }

    /// <summary>
    /// Set a 2D block of bytes to a value (for segmentation map updates).
    /// w and h are in units, stride is the row stride.
    /// </summary>
    private static void SetCtx2d(byte[] array, int offset, int w, int h, int stride, byte value)
    {
        for (int y = 0; y < h; y++)
        {
            array.AsSpan(offset, w).Fill(value);
            offset += stride;
        }
    }

    // ==================== Coefficient decoding ====================

    /// <summary>
    /// Decode a single transform block's coefficients using the range coder.
    /// Port of FFmpeg decode_coeffs_b_generic (8bpp path only).
    /// Returns the index after the last nonzero coefficient (like eob).
    /// </summary>
    private static int DecodeCoeffsBlock(
        ref Vp9BoolDecoder c,
        scoped Span<short> coef, int nCoeffs, bool isTx32x32,
        ReadOnlySpan<byte> prob,   // [band][nnz][11] → flat [band*6*11 + nnz*11 + k]
        Span<uint> cnt,            // [band][nnz][3]  → flat
        Span<uint> eob,            // [band][nnz][2]  → flat
        int nnz,
        ReadOnlySpan<short> scan,
        ReadOnlySpan<short> neighbors,  // [i*2+0], [i*2+1]
        ReadOnlySpan<short> bandCounts,
        ReadOnlySpan<short> qmul,       // [0]=dc, [1]=ac
        bool traceCoefs = false)
    {
        int i = 0;
        int band = 0;
        int bandLeft = bandCounts[band];
        Span<byte> cache = stackalloc byte[1024];
        cache.Clear();

        int tpBase = band * 6 * 11 + nnz * 11;

        do
        {
            // Read EOB flag
            if (traceCoefs && i < 10)
            {
                var stPre = c.GetState();
                Console.Write($"      [i={i}] EOB prob={prob[tpBase]} h={stPre.High} cw=0x{stPre.CodeWord:X8}→");
            }
            bool notEob = c.GetProbBranchy(prob[tpBase]);
            if (traceCoefs && i < 10)
            {
                var stPost = c.GetState();
                Console.WriteLine($"{(notEob?1:0)} h={stPost.High}");
            }
            eob[band * 6 * 2 + nnz * 2 + (notEob ? 1 : 0)]++;
            if (!notEob)
                break;

        skipEob:
            // Read zero/nonzero flag
            if (!c.GetProbBranchy(prob[tpBase + 1]))
            {
                cnt[band * 6 * 3 + nnz * 3 + 0]++;
                if (--bandLeft == 0 && band + 1 < bandCounts.Length)
                    bandLeft = bandCounts[++band];
                cache[scan[i]] = 0;
                nnz = (1 + cache[neighbors[i * 2]] + cache[neighbors[i * 2 + 1]]) >> 1;
                tpBase = band * 6 * 11 + nnz * 11;
                if (++i == nCoeffs)
                    break;
                goto skipEob;
            }

            int rc = scan[i];
            int val;
            if (!c.GetProbBranchy(prob[tpBase + 2]))
            {
                // value = 1
                cnt[band * 6 * 3 + nnz * 3 + 1]++;
                val = 1;
                cache[rc] = 1;
            }
            else
            {
                cnt[band * 6 * 3 + nnz * 3 + 2]++;
                if (!c.GetProbBranchy(prob[tpBase + 3]))
                {
                    if (!c.GetProbBranchy(prob[tpBase + 4]))
                    {
                        cache[rc] = 2;
                        val = 2;
                    }
                    else
                    {
                        val = 3 + c.GetProb(prob[tpBase + 5]);
                        cache[rc] = 3;
                    }
                }
                else if (!c.GetProbBranchy(prob[tpBase + 6]))
                {
                    cache[rc] = 4;
                    if (!c.GetProbBranchy(prob[tpBase + 7]))
                    {
                        val = c.GetProb(159) + 5;
                    }
                    else
                    {
                        val = (c.GetProb(165) << 1) + 7;
                        val += c.GetProb(145);
                    }
                }
                else
                {
                    cache[rc] = 5;
                    if (!c.GetProbBranchy(prob[tpBase + 8]))
                    {
                        if (!c.GetProbBranchy(prob[tpBase + 9]))
                        {
                            val = 11 + (c.GetProb(173) << 2);
                            val += (c.GetProb(148) << 1);
                            val += c.GetProb(140);
                        }
                        else
                        {
                            val = 19 + (c.GetProb(176) << 3);
                            val += (c.GetProb(155) << 2);
                            val += (c.GetProb(140) << 1);
                            val += c.GetProb(135);
                        }
                    }
                    else if (!c.GetProbBranchy(prob[tpBase + 10]))
                    {
                        val = (c.GetProb(180) << 4) + 35;
                        val += (c.GetProb(157) << 3);
                        val += (c.GetProb(141) << 2);
                        val += (c.GetProb(134) << 1);
                        val += c.GetProb(130);
                    }
                    else
                    {
                        // cat6 (8bpp)
                        val = 67;
                        val += (c.GetProb(254) << 13);
                        val += (c.GetProb(254) << 12);
                        val += (c.GetProb(254) << 11);
                        val += (c.GetProb(252) << 10);
                        val += (c.GetProb(249) << 9);
                        val += (c.GetProb(243) << 8);
                        val += (c.GetProb(230) << 7);
                        val += (c.GetProb(196) << 6);
                        val += (c.GetProb(177) << 5);
                        val += (c.GetProb(153) << 4);
                        val += (c.GetProb(140) << 3);
                        val += (c.GetProb(133) << 2);
                        val += (c.GetProb(130) << 1);
                        val += c.GetProb(129);
                    }
                }
            }

            if (--bandLeft == 0 && band + 1 < bandCounts.Length)
                bandLeft = bandCounts[++band];

            // Read sign and apply quantizer
            int sign = c.Get();
            int signedVal = sign != 0 ? -val : val;
            int q = qmul[i == 0 ? 0 : 1];

            if (isTx32x32)
                coef[rc] = (short)(signedVal * q / 2); // signed division — truncates toward zero
            else
                coef[rc] = (short)(signedVal * q);

            if (traceCoefs && i < 10)
            {
                var st = c.GetState();
                Console.WriteLine($"    coef[{i}] scan={rc} band={band} nnz={nnz} raw={val} sign={sign} dequant={coef[rc]} high={st.High} cw=0x{st.CodeWord:X8} bits={st.Bits} bufPos={st.BufPos}");
            }

            nnz = (1 + cache[neighbors[i * 2]] + cache[neighbors[i * 2 + 1]]) >> 1;
            tpBase = band * 6 * 11 + nnz * 11;
        } while (++i < nCoeffs);

        return i;
    }

    // ==================== Block decode main entry ====================

    /// <summary>
    /// Decode a single block at the given position with the specified block size.
    /// Port of FFmpeg ff_vp9_decode_block.
    /// </summary>
    private void DecodeBlock(
        ref Vp9BoolDecoder c,
        int row, int col,
        Vp9BlockLevel bl, Vp9Partition bp,
        int yOff, int uvOff)
    {
        ref var b = ref ctx.CurrentBlock;
        ref var h = ref ctx.Header;
        int bs = (int)bl * 3 + (int)bp;
        int w4 = GetBwh(1, bs, 0);
        int h4 = GetBwh(1, bs, 1);

        ctx.CurrentRow = row;
        ctx.CurrentCol = col;

        if (DiagTraceBlocks)
            Console.WriteLine($"  BLOCK: row={row} col={col} bl={bl} bp={bp} bs={bs} w4={w4} h4={h4} yOff={yOff} uvOff={uvOff} emuY={((col + w4) * 8 > ctx.Frames[Vp9DecoderContext.CurFrame].LumaStride || (row + h4) > ctx.Rows)}");

        b.BlockSize = (Vp9BlockSize)bs;
        b.Level = bl;
        b.Partition = bp;

        // MV clamping bounds
        ctx.MinMv.X = (short)(-(128 + col * 64));
        ctx.MinMv.Y = (short)(-(128 + row * 64));
        ctx.MaxMv.X = (short)(128 + (ctx.Cols - col - w4) * 64);
        ctx.MaxMv.Y = (short)(128 + (ctx.Rows - row - h4) * 64);

        // Diagnostic: log decode start
        if (ctx.DiagBlockCount < 10 && ctx.DiagCoefLog)
        {
            var st0 = c.GetState();
            Console.WriteLine($"  DecodeBlock r={row},c={col},bs={bs} BEFORE_MODE: high={st0.High} cw=0x{st0.CodeWord:X8} bits={st0.Bits} bufPos={st0.BufPos}");
        }

        // Diagnostic: trace bottom-edge blocks
        bool traceBottom = DiagTraceBlocksMinRow >= 0 && row >= DiagTraceBlocksMinRow;
        if (traceBottom)
        {
            var st0 = c.GetState();
            var (_, bufEnd0, bufEnd1) = c.GetBufferState();
            Console.WriteLine($"  [BOTTOM] DecodeBlock r={row},c={col},bs={bs} w4={w4} h4={h4} BEFORE_MODE: bufPos={st0.BufPos} bufEnd={bufEnd1} high={st0.High} cw=0x{st0.CodeWord:X8} bits={st0.Bits}");
        }

        // Decode mode info
        DecodeMode(ref c, row, col);

        // Compute UV transform size
        byte uvtx = (byte)(b.Tx - (byte)(
            ((ctx.SubsamplingX != 0 && w4 * 2 == (1 << (byte)b.Tx)) ||
             (ctx.SubsamplingY != 0 && h4 * 2 == (1 << (byte)b.Tx))) ? 1 : 0));
        if (uvtx > (byte)b.Tx) uvtx = (byte)b.Tx; // clamp
        b.UvTx = (Vp9TxSize)uvtx;

        if (traceBottom)
        {
            var st1 = c.GetState();
            Console.WriteLine($"  [BOTTOM] r={row},c={col} bs={b.BlockSize} tx={b.Tx} uvtx={b.UvTx} skip={b.Skip} intra={b.Intra} mode0={b.Mode0} uvmode={b.UvMode} AFTER_MODE: bufPos={st1.BufPos}");
        }

        // Diagnostic: log block info
        if (ctx.DiagBlockCount < 30 && ctx.DiagCoefLog)
        {
            var st1 = c.GetState();
            Console.WriteLine($"  DecodeBlock[{ctx.DiagBlockCount}]: r={row} c={col} bs={b.BlockSize} tx={b.Tx} " +
                $"skip={b.Skip} intra={b.Intra} seg={b.SegmentId} mode0={b.Mode0} mode1={b.Mode1} mode2={b.Mode2} mode3={b.Mode3} uvmode={b.UvMode}");
            if (b.Intra == 0)
            {
                var mv0 = b.GetMv(0, 0);
                Console.WriteLine($"    INTER: ref0={b.Ref0} ref1={b.Ref1} comp={b.Comp} filter={b.Filter} mv0=({mv0.X},{mv0.Y})");
            }
            Console.WriteLine($"  AFTER_MODE: high={st1.High} cw=0x{st1.CodeWord:X8} bits={st1.Bits} bufPos={st1.BufPos}");
        }

        // Decode coefficients
        if (!Convert.ToBoolean(b.Skip))
        {
            bool hasCoeffs = DecodeCoeffs(ref c, row, col);
            if (traceBottom)
            {
                var st2 = c.GetState();
                Console.WriteLine($"  [BOTTOM] r={row},c={col} AFTER_COEFS: hasCoeffs={hasCoeffs} bufPos={st2.BufPos}");
            }
            if (!hasCoeffs && bs <= (int)Vp9BlockSize.Bs8x8 && b.Intra == 0)
            {
                b.Skip = 1;
                FillContext(ctx.AboveSkipCtx!, col, w4, 1);
                FillContext(ctx.LeftSkipCtx, row & 7, h4, 1);
            }
        }
        else
        {
            // Skip = 1 → zero all nnz contexts
            int row7 = row & 7;
            ClearNnzContexts(row7, col, w4, h4);
            if (traceBottom)
                Console.WriteLine($"  [BOTTOM] r={row},c={col} SKIP (no coefs decoded)");
        }

        // Reconstruction: intra/inter prediction + transform add
        var curFrame = ctx.Frames[Vp9DecoderContext.CurFrame];
        int lsY = curFrame.LumaStride;
        int lsUv = curFrame.ChromaStride;

        // Frame buffer is allocated to full SB dimensions, so blocks that extend
        // beyond the visible frame area can safely write to the padded region.
        // Both IntraRecon and InterRecon write directly to frame buffer arrays,
        // so we just set ctx.DstY/DstU/DstV to point to the frame data.
        ctx.DstY = curFrame.LumaData!;
        ctx.DstYOffset = yOff;
        ctx.YStride = lsY;
        ctx.DstU = curFrame.ChromaUData!;
        ctx.DstV = curFrame.ChromaVData!;
        ctx.DstUOffset = uvOff;
        ctx.DstVOffset = uvOff;
        ctx.UvStride = lsUv;

        if (b.Intra != 0)
            IntraRecon(yOff, uvOff);
        else
            InterRecon();

        // Loop filter level/mask setup
        // FFmpeg: lvl = s->s.h.segmentation.feat[b->seg_id].lflvl[b->intra ? 0 : b->ref[0]+1][b->mode[3] != ZEROMV]
        if (h.FilterLevel > 0 && ctx.LoopFilterLevels != null)
        {
            int segId = b.SegmentId;
            int refIdx = b.Intra != 0 ? 0 : b.Ref0 + 1;
            int modeIdx = b.Mode3 != (byte)Vp9InterMode.ZeroMv ? 1 : 0;
            int lvl = h.Segmentation.GetFeat(segId).GetLflvl(refIdx, modeIdx);

            if (lvl > 0)
            {
                int col7 = col & 7, row7Lf = row & 7;
                int xEnd = Math.Min(ctx.Cols - col, w4);
                int yEnd = Math.Min(ctx.Rows - row, h4);
                bool skipInter = b.Intra == 0 && b.Skip != 0;
                int lflvlIdx = ctx.CurrentLflvlIdx;

                if (lflvlIdx >= 0 && lflvlIdx < ctx.LoopFilterLevels.Length)
                {
                    var lflvl = ctx.LoopFilterLevels[lflvlIdx];
                    SetCtx2d(lflvl.Level, row7Lf * 8 + col7, w4, h4, 8, (byte)lvl);
                    MaskEdges(lflvl.Mask, 0, 0, 0, row7Lf, col7, xEnd, yEnd, 0, 0,
                              (int)b.Tx, skipInter ? 1 : 0);
                    if (ctx.SubsamplingX != 0 || ctx.SubsamplingY != 0)
                    {
                        int maskEdgeH = (ctx.Cols & 1) != 0 && col + w4 >= ctx.Cols
                            ? ctx.Cols & 7 : 0;
                        int maskEdgeV = (ctx.Rows & 1) != 0 && row + h4 >= ctx.Rows
                            ? ctx.Rows & 7 : 0;
                        MaskEdges(lflvl.Mask, 64, ctx.SubsamplingX, ctx.SubsamplingY,
                                  row7Lf, col7, xEnd, yEnd, maskEdgeH, maskEdgeV,
                                  (int)b.UvTx, skipInter ? 1 : 0);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Clear above and left nnz contexts for a skipped block.
    /// </summary>
    private void ClearNnzContexts(int row7, int col, int w4, int h4)
    {
        // Y nnz
        Array.Clear(ctx.AboveYNnzCtx!, col * 2, w4 * 2);
        Array.Clear(ctx.LeftYNnzCtx, row7 * 2, Math.Min(h4 * 2, ctx.LeftYNnzCtx.Length - row7 * 2));

        // UV nnz — FFmpeg uses col << !ss_h (not col >> ss_h)
        // For 4:2:0 (ss_h=1): offset = col, count = w4
        // For 4:4:4 (ss_h=0): offset = col*2, count = w4*2
        int uvNotSsH = 1 - ctx.SubsamplingX;
        int uvNotSsV = 1 - ctx.SubsamplingY;
        int uvAboveOff = col << uvNotSsH;
        int uvAboveCount = w4 << uvNotSsH;
        int uvLeftOff = row7 << uvNotSsV;
        int uvLeftCount = h4 << uvNotSsV;

        if (uvAboveCount > 0)
        {
            Array.Clear(ctx.AboveUvNnzCtx0!, uvAboveOff, uvAboveCount);
            Array.Clear(ctx.AboveUvNnzCtx1!, uvAboveOff, uvAboveCount);
        }
        if (uvLeftCount > 0)
        {
            Array.Clear(ctx.LeftUvNnzCtx0, uvLeftOff, Math.Min(uvLeftCount, ctx.LeftUvNnzCtx0.Length - uvLeftOff));
            Array.Clear(ctx.LeftUvNnzCtx1, uvLeftOff, Math.Min(uvLeftCount, ctx.LeftUvNnzCtx1.Length - uvLeftOff));
        }
    }

    // ==================== Mode decoding ====================

    /// <summary>
    /// Decode mode information for the current block.
    /// Port of FFmpeg decode_mode (vp9block.c lines 80-802).
    /// </summary>
    private void DecodeMode(ref Vp9BoolDecoder c, int row, int col)
    {
        ref var b = ref ctx.CurrentBlock;
        ref var h = ref ctx.Header;
        int row7 = row & 7;
        int bs = (int)b.BlockSize;
        int maxTx = MaxTxForBlock[bs];
        int bw4 = GetBwh(1, bs, 0);
        int bh4 = GetBwh(1, bs, 1);
        int w4 = Math.Min(ctx.Cols - col, bw4);
        int h4 = Math.Min(ctx.Rows - row, bh4);
        bool haveA = row > 0;
        bool haveL = col > ctx.TileColStart;
        int vref = 0;
        int filterId = 0;

        // --- Segment ID ---
        if (h.Segmentation.Enabled == 0)
        {
            b.SegmentId = 0;
        }
        else if (h.Keyframe != 0 || h.IntraOnly != 0)
        {
            b.SegmentId = h.Segmentation.UpdateMap == 0
                ? (byte)0
                : (byte)c.GetTree(Vp9Data.SegmentationTree, h.Segmentation.Prob);
        }
        else if (h.Segmentation.UpdateMap == 0 ||
                 (h.Segmentation.Temporal != 0 &&
                  c.GetProbBranchy(h.Segmentation.PredProb[
                      ctx.AboveSegpredCtx![col] + ctx.LeftSegpredCtx[row7]])))
        {
            // Use previous frame's segmentation map
            var refSegMap = ctx.Frames[Vp9DecoderContext.RefFrameSegMap].SegmentationMap;
            if (refSegMap != null && h.ErrorRes == 0)
            {
                int pred = 8;
                for (int y = 0; y < h4; y++)
                {
                    int idxBase = (y + row) * 8 * ctx.SuperblockCols + col;
                    for (int x = 0; x < w4; x++)
                        pred = Math.Min(pred, refSegMap[idxBase + x]);
                }
                b.SegmentId = (byte)Math.Min(pred, 7);
            }
            else
            {
                b.SegmentId = 0;
            }
            FillContext(ctx.AboveSegpredCtx!, col, w4, 1);
            FillContext(ctx.LeftSegpredCtx, row7, h4, 1);
        }
        else
        {
            b.SegmentId = (byte)c.GetTree(Vp9Data.SegmentationTree, h.Segmentation.Prob);
            FillContext(ctx.AboveSegpredCtx!, col, w4, 0);
            FillContext(ctx.LeftSegpredCtx, row7, h4, 0);
        }

        // Update segmentation map
        if (h.Segmentation.Enabled != 0 &&
            (h.Segmentation.UpdateMap != 0 || h.Keyframe != 0 || h.IntraOnly != 0))
        {
            var curMap = ctx.Frames[Vp9DecoderContext.CurFrame].SegmentationMap;
            if (curMap != null)
                SetCtx2d(curMap, row * 8 * ctx.SuperblockCols + col, bw4, bh4, 8 * ctx.SuperblockCols, b.SegmentId);
        }

        // --- Skip flag ---
        ref readonly var segFeat = ref h.Segmentation.GetFeat(b.SegmentId);
        b.Skip = (byte)(h.Segmentation.Enabled != 0 && segFeat.SkipEnabled != 0 ? 1 : 0);
        if (b.Skip == 0)
        {
            int skipCtx = ctx.LeftSkipCtx[row7] + ctx.AboveSkipCtx![col];
            if (ctx.DiagCoefLog && ctx.DiagBlockCount == 0)
            {
                Console.WriteLine($"    SKIP: ctx={skipCtx} prob={ctx.ActiveProb.Skip[skipCtx]} boolState={c.GetState()}");
            }
            b.Skip = (byte)c.GetProb(ctx.ActiveProb.Skip[skipCtx]);
            ctx.Counts.Skip[skipCtx * 2 + b.Skip]++;
            if (ctx.DiagCoefLog && ctx.DiagBlockCount == 0)
                Console.WriteLine($"    SKIP → {b.Skip}");
        }

        // --- Intra flag ---
        if (h.Keyframe != 0 || h.IntraOnly != 0)
        {
            b.Intra = 1;
        }
        else if (h.Segmentation.Enabled != 0 && segFeat.RefEnabled != 0)
        {
            b.Intra = (byte)(segFeat.RefVal == 0 ? 1 : 0);
        }
        else
        {
            int intraCtx;
            if (haveA && haveL)
            {
                intraCtx = ctx.AboveIntraCtx![col] + ctx.LeftIntraCtx[row7];
                intraCtx += intraCtx == 2 ? 1 : 0;
            }
            else if (haveA)
                intraCtx = 2 * ctx.AboveIntraCtx![col];
            else if (haveL)
                intraCtx = 2 * ctx.LeftIntraCtx[row7];
            else
                intraCtx = 0;

            if (ctx.DiagCoefLog && ctx.DiagBlockCount == 0)
                Console.WriteLine($"    INTRA: ctx={intraCtx} prob={ctx.ActiveProb.Intra[intraCtx]} boolState={c.GetState()}");
            int bit = c.GetProb(ctx.ActiveProb.Intra[intraCtx]);
            ctx.Counts.Intra[intraCtx * 2 + bit]++;
            b.Intra = (byte)(1 - bit);
            if (ctx.DiagCoefLog && ctx.DiagBlockCount == 0)
                Console.WriteLine($"    INTRA → bit={bit} intra={b.Intra}");
        }

        // --- Transform size ---
        if ((b.Intra != 0 || b.Skip == 0) && h.TxfmMode == (byte)Vp9TxSize.Switchable)
        {
            int txCtx;
            if (haveA)
            {
                if (haveL)
                {
                    txCtx = ((ctx.AboveSkipCtx![col] != 0 ? maxTx : ctx.AboveTxfmCtx![col]) +
                             (ctx.LeftSkipCtx[row7] != 0 ? maxTx : ctx.LeftTxfmCtx[row7])) > maxTx ? 1 : 0;
                }
                else
                {
                    txCtx = ctx.AboveSkipCtx![col] != 0 ? 1 : (ctx.AboveTxfmCtx![col] * 2 > maxTx ? 1 : 0);
                }
            }
            else if (haveL)
            {
                txCtx = ctx.LeftSkipCtx[row7] != 0 ? 1 : (ctx.LeftTxfmCtx[row7] * 2 > maxTx ? 1 : 0);
            }
            else
            {
                txCtx = 1;
            }

            switch (maxTx)
            {
                case 3: // TX_32X32
                    b.Tx = (Vp9TxSize)c.GetProb(ctx.ActiveProb.Tx32p[txCtx * 3]);
                    if ((byte)b.Tx != 0)
                    {
                        b.Tx = (Vp9TxSize)((byte)b.Tx + c.GetProb(ctx.ActiveProb.Tx32p[txCtx * 3 + 1]));
                        if ((byte)b.Tx == 2)
                            b.Tx = (Vp9TxSize)((byte)b.Tx + c.GetProb(ctx.ActiveProb.Tx32p[txCtx * 3 + 2]));
                    }
                    ctx.Counts.Tx32p[txCtx * 4 + (byte)b.Tx]++;
                    break;
                case 2: // TX_16X16
                    b.Tx = (Vp9TxSize)c.GetProb(ctx.ActiveProb.Tx16p[txCtx * 2]);
                    if ((byte)b.Tx != 0)
                        b.Tx = (Vp9TxSize)((byte)b.Tx + c.GetProb(ctx.ActiveProb.Tx16p[txCtx * 2 + 1]));
                    ctx.Counts.Tx16p[txCtx * 3 + (byte)b.Tx]++;
                    break;
                case 1: // TX_8X8
                    b.Tx = (Vp9TxSize)c.GetProb(ctx.ActiveProb.Tx8p[txCtx]);
                    ctx.Counts.Tx8p[txCtx * 2 + (byte)b.Tx]++;
                    break;
                default: // TX_4X4
                    b.Tx = Vp9TxSize.Tx4x4;
                    break;
            }
        }
        else
        {
            b.Tx = (Vp9TxSize)Math.Min(maxTx, h.TxfmMode);
        }

        // --- Intra mode decoding ---
        if (h.Keyframe != 0 || h.IntraOnly != 0)
        {
            DecodeIntraModeKeyframe(ref c, row, col, bs, bw4, bh4, w4, h4);
        }
        else if (b.Intra != 0)
        {
            DecodeIntraModeInterframe(ref c, bs);
        }
        else
        {
            DecodeInterMode(ref c, row, col, bs, bw4, bh4, w4, h4, haveA, haveL, ref vref, ref filterId);
        }

        // --- Update above/left context ---
        SplatModeContext(row, col, bs, w4, h4, row7, vref, filterId);
    }

    /// <summary>Decode intra modes for keyframe/intra-only frames.</summary>
    private void DecodeIntraModeKeyframe(ref Vp9BoolDecoder c, int row, int col, int bs,
                                          int bw4, int bh4, int w4, int h4)
    {
        ref var b = ref ctx.CurrentBlock;
        int row7 = row & 7;
        var above = ctx.AboveModeCtx!;
        var left = ctx.LeftModeCtx;

        b.Comp = 0;
        if (bs > (int)Vp9BlockSize.Bs8x8)
        {
            // Sub-8x8: 4 sub-blocks
            int aOff = col * 2;
            int lOff = row7 * 2;

            byte a0 = above[aOff], l0 = left[lOff];
            b.Mode0 = (byte)c.GetTree(Vp9Data.IntraModeTree,
                Vp9Data.DefaultKfYModeProbs.Slice(a0 * 10 * 9 + l0 * 9, 9));
            above[aOff] = b.Mode0;

            if (bs != (int)Vp9BlockSize.Bs8x4)
            {
                b.Mode1 = (byte)c.GetTree(Vp9Data.IntraModeTree,
                    Vp9Data.DefaultKfYModeProbs.Slice(above[aOff + 1] * 10 * 9 + b.Mode0 * 9, 9));
                left[lOff] = b.Mode1;
                above[aOff + 1] = b.Mode1;
            }
            else
            {
                left[lOff] = b.Mode0;
                above[aOff + 1] = b.Mode0;
                b.Mode1 = b.Mode0;
            }

            if (bs != (int)Vp9BlockSize.Bs4x8)
            {
                b.Mode2 = (byte)c.GetTree(Vp9Data.IntraModeTree,
                    Vp9Data.DefaultKfYModeProbs.Slice(above[aOff] * 10 * 9 + left[lOff + 1] * 9, 9));
                above[aOff] = b.Mode2;

                if (bs != (int)Vp9BlockSize.Bs8x4)
                {
                    b.Mode3 = (byte)c.GetTree(Vp9Data.IntraModeTree,
                        Vp9Data.DefaultKfYModeProbs.Slice(above[aOff + 1] * 10 * 9 + b.Mode2 * 9, 9));
                    left[lOff + 1] = b.Mode3;
                    above[aOff + 1] = b.Mode3;
                }
                else
                {
                    left[lOff + 1] = b.Mode2;
                    above[aOff + 1] = b.Mode2;
                    b.Mode3 = b.Mode2;
                }
            }
            else
            {
                b.Mode2 = b.Mode0;
                left[lOff + 1] = b.Mode1;
                above[aOff + 1] = b.Mode1;
                b.Mode3 = b.Mode1;
            }
        }
        else
        {
            int aIdx = col * 2;
            int lIdx = row7 * 2;
            b.Mode0 = (byte)c.GetTree(Vp9Data.IntraModeTree,
                Vp9Data.DefaultKfYModeProbs.Slice(above[aIdx] * 10 * 9 + left[lIdx] * 9, 9));
            b.Mode1 = b.Mode2 = b.Mode3 = b.Mode0;

            int aboveFill = GetBwh(0, bs, 0);
            int leftFill = GetBwh(0, bs, 1);
            FillContext(above, aIdx, Math.Min(aboveFill, above.Length - aIdx), b.Mode0);
            FillContext(left, lIdx, Math.Min(leftFill, left.Length - lIdx), b.Mode0);
        }

        b.UvMode = (byte)c.GetTree(Vp9Data.IntraModeTree,
            Vp9Data.DefaultKfUvModeProbs.Slice(b.Mode3 * 9, 9));
    }

    /// <summary>Decode intra modes for inter frames.</summary>
    private void DecodeIntraModeInterframe(ref Vp9BoolDecoder c, int bs)
    {
        ref var b = ref ctx.CurrentBlock;

        b.Comp = 0;
        if (bs > (int)Vp9BlockSize.Bs8x8)
        {
            b.Mode0 = (byte)c.GetTree(Vp9Data.IntraModeTree, ctx.ActiveProb.YMode.AsSpan(0, 9));
            ctx.Counts.YMode[0 * 10 + b.Mode0]++;

            if (bs != (int)Vp9BlockSize.Bs8x4)
            {
                b.Mode1 = (byte)c.GetTree(Vp9Data.IntraModeTree, ctx.ActiveProb.YMode.AsSpan(0, 9));
                ctx.Counts.YMode[0 * 10 + b.Mode1]++;
            }
            else
                b.Mode1 = b.Mode0;

            if (bs != (int)Vp9BlockSize.Bs4x8)
            {
                b.Mode2 = (byte)c.GetTree(Vp9Data.IntraModeTree, ctx.ActiveProb.YMode.AsSpan(0, 9));
                ctx.Counts.YMode[0 * 10 + b.Mode2]++;

                if (bs != (int)Vp9BlockSize.Bs8x4)
                {
                    b.Mode3 = (byte)c.GetTree(Vp9Data.IntraModeTree, ctx.ActiveProb.YMode.AsSpan(0, 9));
                    ctx.Counts.YMode[0 * 10 + b.Mode3]++;
                }
                else
                    b.Mode3 = b.Mode2;
            }
            else
            {
                b.Mode2 = b.Mode0;
                b.Mode3 = b.Mode1;
            }
        }
        else
        {
            int sz = SizeGroupLut[bs];
            b.Mode0 = (byte)c.GetTree(Vp9Data.IntraModeTree, ctx.ActiveProb.YMode.AsSpan(sz * 9, 9));
            b.Mode1 = b.Mode2 = b.Mode3 = b.Mode0;
            ctx.Counts.YMode[sz * 10 + b.Mode3]++;
        }

        b.UvMode = (byte)c.GetTree(Vp9Data.IntraModeTree, ctx.ActiveProb.UvMode.AsSpan(b.Mode3 * 9, 9));
        ctx.Counts.UvMode[b.Mode3 * 10 + b.UvMode]++;
    }

    /// <summary>
    /// Decode inter prediction mode, reference frames, MVs, and filter type.
    /// Port of the else (inter) branch of decode_mode.
    /// </summary>
    private void DecodeInterMode(ref Vp9BoolDecoder c, int row, int col, int bs,
                                  int bw4, int bh4, int w4, int h4,
                                  bool haveA, bool haveL, ref int vref, ref int filterId)
    {
        ref var b = ref ctx.CurrentBlock;
        ref var hdr = ref ctx.Header;
        int row7 = row & 7;

        // --- Compound prediction decision ---
        if (hdr.Segmentation.Enabled != 0 && hdr.Segmentation.GetFeat(b.SegmentId).RefEnabled != 0)
        {
            b.Comp = 0;
            b.Ref0 = (byte)(hdr.Segmentation.GetFeat(b.SegmentId).RefVal - 1);
        }
        else
        {
            DecodeCompPredAndRefs(ref c, row, col, haveA, haveL);
        }

        // --- Inter mode for <= 8x8 blocks ---
        if (bs <= (int)Vp9BlockSize.Bs8x8)
        {
            if (hdr.Segmentation.Enabled != 0 && hdr.Segmentation.GetFeat(b.SegmentId).SkipEnabled != 0)
            {
                b.Mode0 = b.Mode1 = b.Mode2 = b.Mode3 = (byte)Vp9InterMode.ZeroMv;
            }
            else
            {
                int off = BlockSizeOffsets[bs];
                int modeCtx = InterModeCtxLut[ctx.AboveModeCtx![col + off], ctx.LeftModeCtx[row7 + off]];
                b.Mode0 = (byte)c.GetTree(Vp9Data.InterModeTree, ctx.ActiveProb.MvMode.AsSpan(modeCtx * 3, 3));
                b.Mode1 = b.Mode2 = b.Mode3 = b.Mode0;
                ctx.Counts.MvMode[modeCtx * 4 + b.Mode0 - 10]++;
            }
        }

        // --- Filter mode ---
        if (hdr.FilterMode == (byte)Vp9FilterMode.Switchable)
        {
            int filterCtx;
            if (haveA && ctx.AboveModeCtx![col] >= (byte)Vp9InterMode.NearestMv)
            {
                if (haveL && ctx.LeftModeCtx[row7] >= (byte)Vp9InterMode.NearestMv)
                {
                    filterCtx = ctx.AboveFilterCtx![col] == ctx.LeftFilterCtx[row7]
                        ? ctx.LeftFilterCtx[row7]
                        : 3;
                }
                else
                    filterCtx = ctx.AboveFilterCtx![col];
            }
            else if (haveL && ctx.LeftModeCtx[row7] >= (byte)Vp9InterMode.NearestMv)
                filterCtx = ctx.LeftFilterCtx[row7];
            else
                filterCtx = 3;

            filterId = c.GetTree(Vp9Data.FilterTree, ctx.ActiveProb.Filter.AsSpan(filterCtx * 2, 2));
            ctx.Counts.Filter[filterCtx * 3 + filterId]++;
            b.Filter = (Vp9FilterMode)Vp9Data.FilterLut[filterId];
        }
        else
        {
            b.Filter = (Vp9FilterMode)hdr.FilterMode;
        }

        // --- Inter mode for > 8x8 blocks (sub-block modes) ---
        if (bs > (int)Vp9BlockSize.Bs8x8)
        {
            int modeCtx = InterModeCtxLut[ctx.AboveModeCtx![col], ctx.LeftModeCtx[row7]];

            b.Mode0 = (byte)c.GetTree(Vp9Data.InterModeTree, ctx.ActiveProb.MvMode.AsSpan(modeCtx * 3, 3));
            ctx.Counts.MvMode[modeCtx * 4 + b.Mode0 - 10]++;
            FillMv(ref c, ref b.Mv0Ref0, ref b.Mv0Ref1, b.Mode0, 0);

            if (bs != (int)Vp9BlockSize.Bs8x4)
            {
                b.Mode1 = (byte)c.GetTree(Vp9Data.InterModeTree, ctx.ActiveProb.MvMode.AsSpan(modeCtx * 3, 3));
                ctx.Counts.MvMode[modeCtx * 4 + b.Mode1 - 10]++;
                FillMv(ref c, ref b.Mv1Ref0, ref b.Mv1Ref1, b.Mode1, 1);
            }
            else
            {
                b.Mode1 = b.Mode0;
                b.Mv1Ref0 = b.Mv0Ref0;
                b.Mv1Ref1 = b.Mv0Ref1;
            }

            if (bs != (int)Vp9BlockSize.Bs4x8)
            {
                b.Mode2 = (byte)c.GetTree(Vp9Data.InterModeTree, ctx.ActiveProb.MvMode.AsSpan(modeCtx * 3, 3));
                ctx.Counts.MvMode[modeCtx * 4 + b.Mode2 - 10]++;
                FillMv(ref c, ref b.Mv2Ref0, ref b.Mv2Ref1, b.Mode2, 2);

                if (bs != (int)Vp9BlockSize.Bs8x4)
                {
                    b.Mode3 = (byte)c.GetTree(Vp9Data.InterModeTree, ctx.ActiveProb.MvMode.AsSpan(modeCtx * 3, 3));
                    ctx.Counts.MvMode[modeCtx * 4 + b.Mode3 - 10]++;
                    FillMv(ref c, ref b.Mv3Ref0, ref b.Mv3Ref1, b.Mode3, 3);
                }
                else
                {
                    b.Mode3 = b.Mode2;
                    b.Mv3Ref0 = b.Mv2Ref0;
                    b.Mv3Ref1 = b.Mv2Ref1;
                }
            }
            else
            {
                b.Mode2 = b.Mode0;
                b.Mv2Ref0 = b.Mv0Ref0;
                b.Mv2Ref1 = b.Mv0Ref1;
                b.Mode3 = b.Mode1;
                b.Mv3Ref0 = b.Mv1Ref0;
                b.Mv3Ref1 = b.Mv1Ref1;
            }
        }
        else
        {
            // >= 8x8 blocks: single mode, single MV
            FillMv(ref c, ref b.Mv0Ref0, ref b.Mv0Ref1, b.Mode0, -1);
            b.Mv1Ref0 = b.Mv2Ref0 = b.Mv3Ref0 = b.Mv0Ref0;
            b.Mv1Ref1 = b.Mv2Ref1 = b.Mv3Ref1 = b.Mv0Ref1;
        }

        vref = b.Ref0;
    }

    /// <summary>
    /// Decode compound prediction flag and reference frame indices.
    /// Port of the compound prediction code in decode_mode.
    /// </summary>
    private void DecodeCompPredAndRefs(ref Vp9BoolDecoder c, int row, int col, bool haveA, bool haveL)
    {
        ref var b = ref ctx.CurrentBlock;
        ref var hdr = ref ctx.Header;
        int row7 = row & 7;

        // Compound prediction flag
        if (hdr.CompPredMode != (byte)Vp9CompPredMode.Switchable)
        {
            b.Comp = (byte)(hdr.CompPredMode == (byte)Vp9CompPredMode.CompRef ? 1 : 0);
        }
        else
        {
            int compCtx = ComputeCompCtx(haveA, haveL, row7, col);
            if (ctx.DiagCoefLog)
                Console.WriteLine($"    COMP_PRED: ctx={compCtx} prob={ctx.ActiveProb.Comp[compCtx]} boolState={c.GetState()}");
            b.Comp = (byte)c.GetProb(ctx.ActiveProb.Comp[compCtx]);
            ctx.Counts.Comp[compCtx * 2 + b.Comp]++;
            if (ctx.DiagCoefLog)
                Console.WriteLine($"    COMP_PRED → comp={b.Comp}");
        }

        // Read reference frames
        if (b.Comp != 0)
        {
            DecodeCompRefs(ref c, row, col, haveA, haveL);
        }
        else
        {
            DecodeSingleRef(ref c, row, col, haveA, haveL);
        }
    }

    /// <summary>Compute compound prediction context.</summary>
    private int ComputeCompCtx(bool haveA, bool haveL, int row7, int col)
    {
        ref var hdr = ref ctx.Header;

        if (haveA)
        {
            if (haveL)
            {
                if (ctx.AboveCompCtx![col] != 0 && ctx.LeftCompCtx[row7] != 0)
                    return 4;
                if (ctx.AboveCompCtx[col] != 0)
                    return 2 + (ctx.LeftIntraCtx![row7] != 0 || ctx.LeftRefCtx![row7] == hdr.FixCompRef ? 1 : 0);
                if (ctx.LeftCompCtx[row7] != 0)
                    return 2 + (ctx.AboveIntraCtx![col] != 0 || ctx.AboveRefCtx![col] == hdr.FixCompRef ? 1 : 0);
                return ((ctx.AboveIntraCtx![col] == 0 && ctx.AboveRefCtx![col] == hdr.FixCompRef) ? 1 : 0)
                     ^ ((ctx.LeftIntraCtx![row7] == 0 && ctx.LeftRefCtx![row7] == hdr.FixCompRef) ? 1 : 0);
            }
            return ctx.AboveCompCtx![col] != 0 ? 3
                : (ctx.AboveIntraCtx![col] == 0 && ctx.AboveRefCtx![col] == hdr.FixCompRef ? 1 : 0);
        }
        if (haveL)
        {
            return ctx.LeftCompCtx[row7] != 0 ? 3
                : (ctx.LeftIntraCtx![row7] == 0 && ctx.LeftRefCtx![row7] == hdr.FixCompRef ? 1 : 0);
        }
        return 1;
    }

    /// <summary>Decode compound reference frames.</summary>
    private void DecodeCompRefs(ref Vp9BoolDecoder c, int row, int col, bool haveA, bool haveL)
    {
        ref var b = ref ctx.CurrentBlock;
        ref var hdr = ref ctx.Header;
        int row7 = row & 7;

        int fixIdx = hdr.GetSignBias(hdr.FixCompRef) != 0 ? 1 : 0; // signbias[fixcompref]
        int varIdx = 1 - fixIdx;

        if (fixIdx == 0)
            b.Ref0 = hdr.FixCompRef;
        else
            b.Ref1 = hdr.FixCompRef;

        // Determine comp_ref context (simplified)
        int compRefCtx = 2; // default
        if (haveA && haveL)
        {
            // Full context derivation (port of large if-else chain)
            compRefCtx = ComputeCompRefCtx(row7, col, haveA, haveL);
        }
        else if (haveA)
        {
            compRefCtx = ComputeCompRefCtxAboveOnly(col);
        }
        else if (haveL)
        {
            compRefCtx = ComputeCompRefCtxLeftOnly(row7);
        }

        int bit = c.GetProb(ctx.ActiveProb.CompRef[compRefCtx]);
        ctx.Counts.CompRef[compRefCtx * 2 + bit]++;

        if (fixIdx == 0)
            b.Ref1 = (byte)(hdr.VarCompRef0 + (bit != 0 ? hdr.VarCompRef1 - hdr.VarCompRef0 : 0));
        else
            b.Ref0 = (byte)(hdr.VarCompRef0 + (bit != 0 ? hdr.VarCompRef1 - hdr.VarCompRef0 : 0));
    }

    private int ComputeCompRefCtx(int row7, int col, bool haveA, bool haveL)
    {
        ref var hdr = ref ctx.Header;
        if (ctx.AboveIntraCtx![col] != 0)
        {
            if (ctx.LeftIntraCtx![row7] != 0) return 2;
            return 1 + 2 * (ctx.LeftRefCtx![row7] != hdr.VarCompRef1 ? 1 : 0);
        }
        if (ctx.LeftIntraCtx![row7] != 0)
            return 1 + 2 * (ctx.AboveRefCtx![col] != hdr.VarCompRef1 ? 1 : 0);

        int refa = ctx.AboveRefCtx![col], refl = ctx.LeftRefCtx![row7];
        if (refl == refa && refa == hdr.VarCompRef1) return 0;
        if (ctx.LeftCompCtx[row7] == 0 && ctx.AboveCompCtx![col] == 0)
        {
            if ((refa == hdr.FixCompRef && refl == hdr.VarCompRef0) ||
                (refl == hdr.FixCompRef && refa == hdr.VarCompRef0))
                return 4;
            return refa == refl ? 3 : 1;
        }
        if (ctx.LeftCompCtx[row7] == 0)
            return refa == hdr.VarCompRef1 && refl != hdr.VarCompRef1 ? 1
                 : (refl == hdr.VarCompRef1 && refa != hdr.VarCompRef1 ? 2 : 4);
        if (ctx.AboveCompCtx![col] == 0)
            return refl == hdr.VarCompRef1 && refa != hdr.VarCompRef1 ? 1
                 : (refa == hdr.VarCompRef1 && refl != hdr.VarCompRef1 ? 2 : 4);
        return refl == refa ? 4 : 2;
    }

    private int ComputeCompRefCtxAboveOnly(int col)
    {
        ref var hdr = ref ctx.Header;
        if (ctx.AboveIntraCtx![col] != 0) return 2;
        if (ctx.AboveCompCtx![col] != 0) return 4 * (ctx.AboveRefCtx![col] != hdr.VarCompRef1 ? 1 : 0);
        return 3 * (ctx.AboveRefCtx![col] != hdr.VarCompRef1 ? 1 : 0);
    }

    private int ComputeCompRefCtxLeftOnly(int row7)
    {
        ref var hdr = ref ctx.Header;
        if (ctx.LeftIntraCtx![row7] != 0) return 2;
        if (ctx.LeftCompCtx[row7] != 0) return 4 * (ctx.LeftRefCtx![row7] != hdr.VarCompRef1 ? 1 : 0);
        return 3 * (ctx.LeftRefCtx![row7] != hdr.VarCompRef1 ? 1 : 0);
    }

    /// <summary>Decode single reference frame index.</summary>
    private void DecodeSingleRef(ref Vp9BoolDecoder c, int row, int col, bool haveA, bool haveL)
    {
        ref var b = ref ctx.CurrentBlock;
        ref var hdr = ref ctx.Header;
        int row7 = row & 7;

        // First bit: last vs golden/altref
        int singleRefCtx0 = ComputeSingleRefCtx0(haveA, haveL, row7, col);
        byte prob0 = ctx.ActiveProb.SingleRef[singleRefCtx0 * 2];
        if (ctx.DiagCoefLog)
            Console.WriteLine($"    SINGLEREF bit0: ctx={singleRefCtx0} prob={prob0} boolState={c.GetState()}");
        int bit0 = c.GetProb(prob0);
        ctx.Counts.SingleRef[singleRefCtx0 * 2 * 2 + 0 * 2 + bit0]++;

        if (bit0 == 0)
        {
            b.Ref0 = 0; // LAST
            if (ctx.DiagCoefLog)
                Console.WriteLine($"    SINGLEREF → bit0={bit0} → LAST");
        }
        else
        {
            // Second bit: golden vs altref
            int singleRefCtx1 = ComputeSingleRefCtx1(haveA, haveL, row7, col);
            byte prob1 = ctx.ActiveProb.SingleRef[singleRefCtx1 * 2 + 1];
            if (ctx.DiagCoefLog)
                Console.WriteLine($"    SINGLEREF bit1: ctx={singleRefCtx1} prob={prob1} boolState={c.GetState()}");
            int bit1 = c.GetProb(prob1);
            ctx.Counts.SingleRef[singleRefCtx1 * 2 * 2 + 1 * 2 + bit1]++;
            b.Ref0 = (byte)(1 + bit1); // GOLDEN or ALTREF
            if (ctx.DiagCoefLog)
                Console.WriteLine($"    SINGLEREF → bit0={bit0} bit1={bit1} → {(bit1 == 0 ? "GOLDEN" : "ALTREF")}");
        }
    }

    /// <summary>Compute single_ref context for first bit (last vs golden/altref).</summary>
    private int ComputeSingleRefCtx0(bool haveA, bool haveL, int row7, int col)
    {
        ref var hdr = ref ctx.Header;

        if (haveA && ctx.AboveIntraCtx![col] == 0)
        {
            if (haveL && ctx.LeftIntraCtx![row7] == 0)
            {
                if (ctx.LeftCompCtx[row7] != 0)
                {
                    if (ctx.AboveCompCtx![col] != 0)
                        return 1 + (hdr.FixCompRef == 0 || ctx.LeftRefCtx![row7] == 0 || ctx.AboveRefCtx![col] == 0 ? 1 : 0);
                    return (3 * (ctx.AboveRefCtx![col] == 0 ? 1 : 0)) + (hdr.FixCompRef == 0 || ctx.LeftRefCtx![row7] == 0 ? 1 : 0);
                }
                if (ctx.AboveCompCtx![col] != 0)
                    return (3 * (ctx.LeftRefCtx![row7] == 0 ? 1 : 0)) + (hdr.FixCompRef == 0 || ctx.AboveRefCtx![col] == 0 ? 1 : 0);
                return 2 * (ctx.LeftRefCtx![row7] == 0 ? 1 : 0) + 2 * (ctx.AboveRefCtx![col] == 0 ? 1 : 0);
            }
            if (ctx.AboveIntraCtx![col] != 0) return 2;
            if (ctx.AboveCompCtx![col] != 0)
                return 1 + (hdr.FixCompRef == 0 || ctx.AboveRefCtx![col] == 0 ? 1 : 0);
            return 4 * (ctx.AboveRefCtx![col] == 0 ? 1 : 0);
        }
        if (haveL && ctx.LeftIntraCtx![row7] == 0)
        {
            if (ctx.LeftIntraCtx[row7] != 0) return 2;
            if (ctx.LeftCompCtx[row7] != 0)
                return 1 + (hdr.FixCompRef == 0 || ctx.LeftRefCtx![row7] == 0 ? 1 : 0);
            return 4 * (ctx.LeftRefCtx![row7] == 0 ? 1 : 0);
        }
        return 2;
    }

    /// <summary>Compute single_ref context for second bit (golden vs altref).</summary>
    private int ComputeSingleRefCtx1(bool haveA, bool haveL, int row7, int col)
    {
        ref var hdr = ref ctx.Header;

        if (haveA)
        {
            if (haveL)
            {
                if (ctx.LeftIntraCtx![row7] != 0)
                {
                    if (ctx.AboveIntraCtx![col] != 0) return 2;
                    if (ctx.AboveCompCtx![col] != 0)
                        return 1 + 2 * (hdr.FixCompRef == 1 || ctx.AboveRefCtx![col] == 1 ? 1 : 0);
                    if (ctx.AboveRefCtx![col] == 0) return 3;
                    return 4 * (ctx.AboveRefCtx![col] == 1 ? 1 : 0);
                }
                if (ctx.AboveIntraCtx![col] != 0)
                {
                    if (ctx.LeftCompCtx[row7] != 0)
                        return 1 + 2 * (hdr.FixCompRef == 1 || ctx.LeftRefCtx![row7] == 1 ? 1 : 0);
                    if (ctx.LeftRefCtx![row7] == 0) return 3;
                    return 4 * (ctx.LeftRefCtx![row7] == 1 ? 1 : 0);
                }
                if (ctx.AboveCompCtx![col] != 0)
                {
                    if (ctx.LeftCompCtx[row7] != 0)
                    {
                        if (ctx.LeftRefCtx![row7] == ctx.AboveRefCtx![col])
                            return 3 * (hdr.FixCompRef == 1 || ctx.LeftRefCtx[row7] == 1 ? 1 : 0);
                        return 2;
                    }
                    if (ctx.LeftRefCtx![row7] == 0)
                        return 1 + 2 * (hdr.FixCompRef == 1 || ctx.AboveRefCtx![col] == 1 ? 1 : 0);
                    return 3 * (ctx.LeftRefCtx[row7] == 1 ? 1 : 0) + (hdr.FixCompRef == 1 || ctx.AboveRefCtx![col] == 1 ? 1 : 0);
                }
                if (ctx.LeftCompCtx[row7] != 0)
                {
                    if (ctx.AboveRefCtx![col] == 0)
                        return 1 + 2 * (hdr.FixCompRef == 1 || ctx.LeftRefCtx![row7] == 1 ? 1 : 0);
                    return 3 * (ctx.AboveRefCtx[col] == 1 ? 1 : 0) + (hdr.FixCompRef == 1 || ctx.LeftRefCtx![row7] == 1 ? 1 : 0);
                }
                if (ctx.AboveRefCtx![col] == 0)
                {
                    if (ctx.LeftRefCtx![row7] == 0) return 3;
                    return 4 * (ctx.LeftRefCtx[row7] == 1 ? 1 : 0);
                }
                if (ctx.LeftRefCtx![row7] == 0)
                    return 4 * (ctx.AboveRefCtx[col] == 1 ? 1 : 0);
                return 2 * (ctx.LeftRefCtx[row7] == 1 ? 1 : 0) + 2 * (ctx.AboveRefCtx[col] == 1 ? 1 : 0);
            }
            // above only
            if (ctx.AboveIntraCtx![col] != 0 || (ctx.AboveCompCtx![col] == 0 && ctx.AboveRefCtx![col] == 0))
                return 2;
            if (ctx.AboveCompCtx![col] != 0)
                return 3 * (hdr.FixCompRef == 1 || ctx.AboveRefCtx![col] == 1 ? 1 : 0);
            return 4 * (ctx.AboveRefCtx![col] == 1 ? 1 : 0);
        }
        if (haveL)
        {
            if (ctx.LeftIntraCtx![row7] != 0 || (ctx.LeftCompCtx[row7] == 0 && ctx.LeftRefCtx![row7] == 0))
                return 2;
            if (ctx.LeftCompCtx[row7] != 0)
                return 3 * (hdr.FixCompRef == 1 || ctx.LeftRefCtx![row7] == 1 ? 1 : 0);
            return 4 * (ctx.LeftRefCtx![row7] == 1 ? 1 : 0);
        }
        return 2;
    }

    /// <summary>
    /// Splat mode context to above/left arrays after decoding a block.
    /// Port of the SPLATctx/SETctxS macros at end of decode_mode.
    /// </summary>
    private void SplatModeContext(int row, int col, int bs, int w4, int h4, int row7,
                                  int vref, int filterId)
    {
        ref var b = ref ctx.CurrentBlock;
        ref var hdr = ref ctx.Header;

        // Above context
        FillContext(ctx.AboveSkipCtx!, col, w4, b.Skip);
        FillContext(ctx.AboveTxfmCtx!, col, w4, (byte)b.Tx);
        FillContext(ctx.AbovePartitionCtx!, col, w4, AboveCtx[bs]);

        // Left context
        FillContext(ctx.LeftSkipCtx, row7, h4, b.Skip);
        FillContext(ctx.LeftTxfmCtx, row7, h4, (byte)b.Tx);
        FillContext(ctx.LeftPartitionCtx, row7, h4, LeftCtx[bs]);

        if (hdr.Keyframe == 0 && hdr.IntraOnly == 0)
        {
            FillContext(ctx.AboveIntraCtx!, col, w4, b.Intra);
            FillContext(ctx.AboveCompCtx!, col, w4, b.Comp);
            FillContext(ctx.AboveModeCtx!, col, w4, b.Mode3);
            FillContext(ctx.LeftIntraCtx, row7, h4, b.Intra);
            FillContext(ctx.LeftCompCtx, row7, h4, b.Comp);
            FillContext(ctx.LeftModeCtx, row7, h4, b.Mode3);

            if (b.Intra == 0)
            {
                FillContext(ctx.AboveRefCtx!, col, w4, (byte)vref);
                FillContext(ctx.LeftRefCtx, row7, h4, (byte)vref);
                if (hdr.FilterMode == (byte)Vp9FilterMode.Switchable)
                {
                    FillContext(ctx.AboveFilterCtx!, col, w4, (byte)filterId);
                    FillContext(ctx.LeftFilterCtx, row7, h4, (byte)filterId);
                }
            }
        }

        // MV context update
        if (hdr.Keyframe == 0 && hdr.IntraOnly == 0)
        {
            UpdateMvContexts(row, col, bs, w4, h4, row7);
        }

        // Update CUR_FRAME.mv table
        UpdateFrameMvTable(row, col, w4, h4);
    }

    /// <summary>Update above/left MV context arrays from decoded block MVs.</summary>
    private void UpdateMvContexts(int row, int col, int bs, int w4, int h4, int row7)
    {
        ref var b = ref ctx.CurrentBlock;

        if (bs > (int)Vp9BlockSize.Bs8x8)
        {
            // For blocks > 8x8, store specific sub-block MVs
            int aboveMvOff = col * 2;
            int leftMvOff = row7 * 2;

            if (ctx.AboveMvCtx != null)
            {
                ctx.AboveMvCtx[aboveMvOff * 2 + 0] = b.Mv2Ref0;
                ctx.AboveMvCtx[aboveMvOff * 2 + 1] = b.Mv2Ref1;
                ctx.AboveMvCtx[(aboveMvOff + 1) * 2 + 0] = b.Mv3Ref0;
                ctx.AboveMvCtx[(aboveMvOff + 1) * 2 + 1] = b.Mv3Ref1;
            }

            ctx.LeftMvCtx[leftMvOff * 2 + 0] = b.Mv1Ref0;
            ctx.LeftMvCtx[leftMvOff * 2 + 1] = b.Mv1Ref1;
            ctx.LeftMvCtx[(leftMvOff + 1) * 2 + 0] = b.Mv3Ref0;
            ctx.LeftMvCtx[(leftMvOff + 1) * 2 + 1] = b.Mv3Ref1;
        }
        else
        {
            // For 8x8 and smaller: fill all with mv[3]
            Vp9Mv mv0 = b.Mv3Ref0, mv1 = b.Mv3Ref1;
            for (int n = 0; n < w4 * 2; n++)
            {
                if (ctx.AboveMvCtx != null)
                {
                    ctx.AboveMvCtx[(col * 2 + n) * 2 + 0] = mv0;
                    ctx.AboveMvCtx[(col * 2 + n) * 2 + 1] = mv1;
                }
            }
            for (int n = 0; n < h4 * 2; n++)
            {
                ctx.LeftMvCtx[(row7 * 2 + n) * 2 + 0] = mv0;
                ctx.LeftMvCtx[(row7 * 2 + n) * 2 + 1] = mv1;
            }
        }
    }

    /// <summary>Update frame-level MV reference pair table.</summary>
    private void UpdateFrameMvTable(int row, int col, int w4, int h4)
    {
        ref var b = ref ctx.CurrentBlock;
        var mvs = ctx.Frames[Vp9DecoderContext.CurFrame].Mvs;
        if (mvs == null) return;

        if (EnableDiagCoefLog && row == 0 && col == 0)
            Console.WriteLine($"UpdateFrameMvTable: row={row} col={col} w4={w4} h4={h4} intra={b.Intra} comp={b.Comp} " +
                $"ref0={b.Ref0} mv3Ref0=({b.Mv3Ref0.X},{b.Mv3Ref0.Y}) bs={b.BlockSize}");

        int sbCols = ctx.SuperblockCols;
        for (int y = 0; y < h4; y++)
        {
            int o = (row + y) * sbCols * 8 + col;
            for (int x = 0; x < w4; x++)
            {
                if (b.Intra != 0)
                {
                    mvs[o + x].Ref0 = -1;
                    mvs[o + x].Ref1 = -1;
                }
                else if (b.Comp != 0)
                {
                    mvs[o + x].Ref0 = (sbyte)b.Ref0;
                    mvs[o + x].Ref1 = (sbyte)b.Ref1;
                    mvs[o + x].Mv0 = b.Mv3Ref0;
                    mvs[o + x].Mv1 = b.Mv3Ref1;
                }
                else
                {
                    mvs[o + x].Ref0 = (sbyte)b.Ref0;
                    mvs[o + x].Ref1 = -1;
                    mvs[o + x].Mv0 = b.Mv3Ref0;
                }
            }
        }
    }

    // ==================== Coefficient decoding (full frame) ====================

    /// <summary>
    /// Decode all coefficients for the current block (Y + UV).
    /// Port of FFmpeg decode_coeffs (8bpp path).
    /// Returns true if any nonzero coefficients were found.
    /// </summary>
    private bool DecodeCoeffs(ref Vp9BoolDecoder c, int row, int col)
    {
        ref var b = ref ctx.CurrentBlock;
        ref var hdr = ref ctx.Header;

        int bTx = (byte)b.Tx;
        if (bTx > 3)
            throw new InvalidOperationException($"VP9 DecodeCoeffs: bTx={bTx} out of range (max 3), bs={(int)b.BlockSize}");
        bool isIntra = b.Intra != 0;
        int bs = (int)b.BlockSize;
        int w4 = GetBwh(1, bs, 0) * 2;
        int h4 = GetBwh(1, bs, 1) * 2;
        int endX = Math.Min(2 * (ctx.Cols - col), w4);
        int endY = Math.Min(2 * (ctx.Rows - row), h4);

        ref readonly var segFeat = ref hdr.Segmentation.GetFeat(b.SegmentId);
        int txLookup = hdr.Lossless != 0 ? 4 : bTx;
        bool totalCoeff = false;

        // --- Y coefficients ---
        byte[] aboveY = ctx.AboveYNnzCtx!;
        byte[] leftY = ctx.LeftYNnzCtx;
        int aBaseY = col * 2;
        int lBaseY = (row & 7) * 2;

        // On the stack, not the heap. DecodeCoeffsBlock takes a ReadOnlySpan, so this array only
        // ever existed to be converted to one - and it was allocated once per block.
        Span<short> qmulY = stackalloc short[2];
        qmulY[0] = segFeat.QmulYDc;
        qmulY[1] = segFeat.QmulYAc;

        int step = 1 << bTx;
        int stepSq = step * step; // FFmpeg: n += step * step

        // Pre-merge NNZ context before decode loop — port of FFmpeg MERGE_CTX macro.
        // This collapses multi-byte NNZ sub-blocks into single merged values at step boundaries.
        // Must happen before the loop because the loop writes back to individual positions.
        if (step >= 2)
        {
            PreMergeNnz(aboveY, aBaseY, endX, step);
            PreMergeNnz(leftY, lBaseY, endY, step);
        }

        int n = 0;
        for (int y = 0; y < endY; y += step)
        {
            for (int x = 0; x < endX; x += step, n += stepSq)
            {
                // Determine transform type from intra mode
                // FFmpeg: TX_4X4 uses mode[bs > BS_8x8 ? n : 0]; TX_8X8+ always uses mode[0]
                int txType;
                if (isIntra)
                {
                    int modeIdx = bTx == 0 && bs > (int)Vp9BlockSize.Bs8x8 ? n : 0;
                    txType = Vp9Data.IntraTxfmType[b.GetMode(modeIdx)];
                }
                else
                    txType = 0; // DCT_DCT for inter

                ReadOnlySpan<short> scan = GetScanOrder(txLookup, txType);
                ReadOnlySpan<short> nb = GetScanNeighbors(txLookup, txType);

                // After pre-merge, just read the single merged position (same as FFmpeg's a[x] + l[y])
                int nnz = aboveY[aBaseY + x] + leftY[lBaseY + y];

                // Get expanded coef probs for this tx/plane/inter
                int probBase = (bTx * 2 * 2 + 0 * 2 + (isIntra ? 0 : 1)) * 6 * 6 * 11;
                int cntBase = (bTx * 2 * 2 + 0 * 2 + (isIntra ? 0 : 1)) * 6 * 6 * 3;
                int eobBase = (bTx * 2 * 2 + 0 * 2 + (isIntra ? 0 : 1)) * 6 * 6 * 2;

                int nCoeffs = 16 * stepSq;

                // We need a temporary coef buffer
                Span<short> coefBuf = stackalloc short[nCoeffs];
                coefBuf.Clear();

                bool traceFirstBlock = ctx.DiagCoefLog && ctx.DiagBlockCount == 0;

                // Diagnostic: dump expanded probs for first block
                if (ctx.DiagCoefLog && ctx.DiagBlockCount == 0)
                {
                    var st = c.GetState();
                    Console.WriteLine($"  BEFORE_COEF_N{n}: high={st.High} cw=0x{st.CodeWord:X8} bits={st.Bits} bufPos={st.BufPos} nnz={nnz} txType={txType}");
                    {
                        var probSpan = ctx.CoefProbs.AsSpan(probBase);
                        // Print probs for the initial band/nnz this sub-block will use
                        int startTP = 0 * 6 * 11 + nnz * 11; // band=0, current nnz
                        Console.Write($"  PROBS band0 nnz{nnz}: ");
                        for (int pi = startTP; pi < startTP + 11; pi++) Console.Write($"{probSpan[pi]} ");
                        Console.WriteLine();
                        // Also print band1 nnz0 and nnz1
                        Console.Write("  PROBS band1 nnz0: ");
                        for (int pi = 66; pi < 77; pi++) Console.Write($"{probSpan[pi]} ");
                        Console.WriteLine();
                        Console.Write("  PROBS band1 nnz1: ");
                        for (int pi = 77; pi < 88; pi++) Console.Write($"{probSpan[pi]} ");
                        Console.WriteLine();
                    }
                }

                // Diagnostic: capture state for shadow comparison (first sub-block only, n==0)
                if (DiagCaptureShadowState && bTx == 3 && ctx.DiagBlockCount == 0 && n == 0)
                {
                    var st = c.GetState();
                    DiagCapturedBoolState = st;
                    var (buf, pos, end) = c.GetBufferState();
                    DiagCapturedBitstream = buf[pos..end];
                    DiagCapturedProbs = ctx.CoefProbs.AsSpan(probBase, 6 * 6 * 11).ToArray();
                    DiagCapturedNnz = nnz;
                    DiagCapturedQmul = qmulY.ToArray();
                }

                int eob = DecodeCoeffsBlock(
                    ref c, coefBuf, nCoeffs, bTx == 3,
                    ctx.CoefProbs.AsSpan(probBase),
                    ctx.Counts.Coef.AsSpan(cntBase),
                    ctx.Counts.Eob.AsSpan(eobBase),
                    nnz, scan, nb, BandCounts[bTx], qmulY, traceFirstBlock);

                // Capture decoded coefficients for shadow comparison (first sub-block only)
                if (DiagCaptureShadowState && bTx == 3 && ctx.DiagBlockCount == 0 && n == 0)
                {
                    DiagCapturedCoefResult = coefBuf.ToArray();
                }

                // Diagnostic: log coefficient summary for first few blocks
                if (ctx.DiagBlockCount < 30 && ctx.DiagCoefLog)
                {
                    Console.Write($"  [B{ctx.DiagBlockCount} r={row},c={col},bs={bs}] Y n={n} x={x} y={y} nnz={nnz} eob={eob} mode={b.GetMode(bs > (int)Vp9BlockSize.Bs8x8 ? n : 0)} bufPos={c.BufferPosition}");
                    Console.Write($" qDc={qmulY[0]} qAc={qmulY[1]}");
                    if (eob > 0)
                    {
                        Console.Write(" coefs=[");
                        for (int ci = 0; ci < Math.Min(eob, nCoeffs); ci++)
                            Console.Write($"{coefBuf[ci]},");
                        Console.Write("]");
                    }
                    Console.WriteLine();
                }

                // Diagnostic: trace bottom-edge sub-block coefficients
                bool traceBottomCoef = DiagTraceBlocksMinRow >= 0 && row >= DiagTraceBlocksMinRow;
                if (traceBottomCoef)
                {
                    Console.Write($"  [BOTTOM-COEF] r={row},c={col} n={n} x={x} y={y} nnz={nnz} eob={eob} bufPos={c.BufferPosition}");
                    if (eob > 0)
                    {
                        Console.Write($" dc={coefBuf[0]}");
                        if (eob > 1) Console.Write($" ac1={coefBuf[1]}");
                    }
                    Console.WriteLine();
                }

                aboveY[aBaseY + x] = (byte)(eob > 0 ? 1 : 0);
                leftY[lBaseY + y] = (byte)(eob > 0 ? 1 : 0);
                totalCoeff |= eob > 0;

                // Store eob and copy coefficients to block storage
                // FFmpeg: td->block + 16 * n, td->eob[n]
                if (ctx.BlockBase != null && ctx.EobBase != null)
                {
                    int blockOffset = n * 16;
                    if (blockOffset + nCoeffs <= ctx.BlockBase.Length)
                        coefBuf.CopyTo(ctx.BlockBase.AsSpan(blockOffset));
                    if (stepSq >= 16) // TX_16X16 or TX_32X32: 16-bit eob
                    {
                        if (n + 1 < ctx.EobBase.Length)
                        {
                            ctx.EobBase[n] = (byte)(eob & 0xFF);
                            ctx.EobBase[n + 1] = (byte)((eob >> 8) & 0xFF);
                        }
                    }
                    else
                    {
                        if (n < ctx.EobBase.Length)
                            ctx.EobBase[n] = (byte)Math.Min(eob, 255);
                    }
                }
            }
        }

        // Splat Y nnz context
        SplatNnzCtx(aboveY, aBaseY, endX, w4, step);
        SplatNnzCtx(leftY, lBaseY, endY, h4, step);

        // --- UV coefficients ---
        int uvTx = (byte)b.UvTx;
        int uvStep = 1 << uvTx;
        int uvW4 = w4 >> ctx.SubsamplingX;
        int uvH4 = h4 >> ctx.SubsamplingY;
        int uvEndX = endX >> ctx.SubsamplingX;
        int uvEndY = endY >> ctx.SubsamplingY;
        int uvTxLookup = hdr.Lossless != 0 ? 4 : uvTx;
        Span<short> qmulUv = stackalloc short[2];
        qmulUv[0] = segFeat.QmulUvDc;
        qmulUv[1] = segFeat.QmulUvAc;

        ReadOnlySpan<short> uvScan = GetScanOrder(uvTxLookup, 0); // DCT_DCT
        ReadOnlySpan<short> uvNb = GetScanNeighbors(uvTxLookup, 0);

        for (int pl = 0; pl < 2; pl++)
        {
            byte[] aboveUv = pl == 0 ? ctx.AboveUvNnzCtx0! : ctx.AboveUvNnzCtx1!;
            byte[] leftUv = pl == 0 ? ctx.LeftUvNnzCtx0 : ctx.LeftUvNnzCtx1;
            // FFmpeg: col << !ss_h, (row&7) << !ss_v — NOT col >> ss_h
            int aBaseUv = col << (1 - ctx.SubsamplingX);
            int lBaseUv = (row & 7) << (1 - ctx.SubsamplingY);

            int uvN = 0;
            int uvStepSq = uvStep * uvStep;
            int probBase = (uvTx * 2 * 2 + 1 * 2 + (isIntra ? 0 : 1)) * 6 * 6 * 11;
            int cntBase = (uvTx * 2 * 2 + 1 * 2 + (isIntra ? 0 : 1)) * 6 * 6 * 3;
            int eobBase = (uvTx * 2 * 2 + 1 * 2 + (isIntra ? 0 : 1)) * 6 * 6 * 2;

            // Pre-merge UV NNZ context
            if (uvStep >= 2)
            {
                PreMergeNnz(aboveUv, aBaseUv, uvEndX, uvStep);
                PreMergeNnz(leftUv, lBaseUv, uvEndY, uvStep);
            }

            for (int y = 0; y < uvEndY; y += uvStep)
            {
                for (int x = 0; x < uvEndX; x += uvStep, uvN += uvStepSq)
                {
                    int nnz = aboveUv[aBaseUv + x] + leftUv[lBaseUv + y];
                    int nCoeffs = 16 * uvStepSq;

                    Span<short> coefBuf = stackalloc short[nCoeffs];
                    coefBuf.Clear();

                    int eob = DecodeCoeffsBlock(
                        ref c, coefBuf, nCoeffs, uvTx == 3,
                        ctx.CoefProbs.AsSpan(probBase),
                        ctx.Counts.Coef.AsSpan(cntBase),
                        ctx.Counts.Eob.AsSpan(eobBase),
                        nnz, uvScan, uvNb, BandCounts[uvTx], qmulUv,
                        traceCoefs: ctx.DiagCoefLog && pl == 1 && ctx.DiagBlockCount < 2);

                    // Diagnostic: UV coefficient logging
                    if (ctx.DiagBlockCount < 10 && ctx.DiagCoefLog)
                        Console.WriteLine($"  [B{ctx.DiagBlockCount} r={row},c={col}] UV pl={pl} x={x},y={y} nnz={nnz} eob={eob} bufPos={c.BufferPosition}");

                    aboveUv[aBaseUv + x] = (byte)(eob > 0 ? 1 : 0);
                    leftUv[lBaseUv + y] = (byte)(eob > 0 ? 1 : 0);
                    totalCoeff |= eob > 0;

                    // Store UV coefficients — FFmpeg: td->uvblock[pl] + 16 * n
                    short[]? uvBlock = pl == 0 ? ctx.UvBlockBase0 : ctx.UvBlockBase1;
                    byte[]? uvEob = pl == 0 ? ctx.UvEobBase0 : ctx.UvEobBase1;
                    if (uvBlock != null && uvEob != null)
                    {
                        int blockOffset = uvN * 16;
                        if (blockOffset + nCoeffs <= uvBlock.Length)
                            coefBuf.CopyTo(uvBlock.AsSpan(blockOffset));
                        if (uvStepSq >= 16)
                        {
                            if (uvN + 1 < uvEob.Length)
                            {
                                uvEob[uvN] = (byte)(eob & 0xFF);
                                uvEob[uvN + 1] = (byte)((eob >> 8) & 0xFF);
                            }
                        }
                        else
                        {
                            if (uvN < uvEob.Length)
                                uvEob[uvN] = (byte)Math.Min(eob, 255);
                        }
                    }
                }
            }

            SplatNnzCtx(aboveUv, aBaseUv, uvEndX, uvW4, uvStep);
            SplatNnzCtx(leftUv, lBaseUv, uvEndY, uvH4, uvStep);
        }

        return totalCoeff;
    }

    /// <summary>
    /// Pre-merge NNZ context before the decode loop — port of FFmpeg MERGE/MERGE_CTX macro.
    /// Reads 'step' bytes at each position, writes back !!OR to the first byte.
    /// For step=2: la[n] = !!(la[n] | la[n+1]) for n=0,2,4,...
    /// For step=4: la[n] = !!(la[n] | la[n+1] | la[n+2] | la[n+3]) for n=0,4,8,...
    /// </summary>
    private static void PreMergeNnz(byte[] arr, int baseOff, int end, int step)
    {
        for (int n = 0; n < end; n += step)
        {
            int merged = 0;
            int limit = Math.Min(step, arr.Length - baseOff - n);
            for (int k = 0; k < limit; k++)
                merged |= arr[baseOff + n + k];
            arr[baseOff + n] = (byte)(merged != 0 ? 1 : 0);
        }
    }

    /// <summary>Splat nnz context after decode (fill sub-positions from decoded positions).</summary>
    private static void SplatNnzCtx(byte[] ctx, int baseOff, int end, int fullSize, int step)
    {
        if (step <= 1) return;

        if (end == fullSize)
        {
            // Full block — splat the full step width (matches FFmpeg's AV_WN32A/AV_WN64A path)
            for (int n = 0; n < end && baseOff + n + step - 1 < ctx.Length; n += step)
            {
                byte v = ctx[baseOff + n];
                for (int k = 1; k < step; k++)
                    ctx[baseOff + n + k] = v;
            }
        }
        else
        {
            // Edge block — limit splat to FFMIN(end - n - 1, step - 1) matching FFmpeg's memset path
            for (int n = 0; n < end; n += step)
            {
                byte v = ctx[baseOff + n];
                int fill = Math.Min(end - n - 1, step - 1);
                for (int k = 1; k <= fill && baseOff + n + k < ctx.Length; k++)
                    ctx[baseOff + n + k] = v;
            }
        }
    }

    // ==================== Superblock partition recursion ====================

    /// <summary>
    /// Recursively decode a superblock partition tree.
    /// Port of FFmpeg decode_sb (vp9.c lines 1116-1194).
    /// </summary>
    private void DecodeSuperblock(ref Vp9BoolDecoder c, int row, int col,
        Vp9BlockLevel bl, int yOff, int uvOff)
    {
        ref var hdr = ref ctx.Header;
        var curFrame = ctx.Frames[Vp9DecoderContext.CurFrame];
        int yStride = curFrame.LumaStride;
        int uvStride = curFrame.ChromaStride;

        int blInt = (int)bl;
        int partCtx = ((ctx.AbovePartitionCtx![col] >> (3 - blInt)) & 1) |
                      (((ctx.LeftPartitionCtx[row & 7] >> (3 - blInt)) & 1) << 1);

        ReadOnlySpan<byte> partProbs = (hdr.Keyframe != 0 || hdr.IntraOnly != 0)
            ? Vp9Data.DefaultKfPartitionProbs.Slice(blInt * 4 * 3 + partCtx * 3, 3)
            : ctx.ActiveProb.Partition.AsSpan(blInt * 4 * 3 + partCtx * 3, 3);

        int hbs = 4 >> blInt;

        // Diagnostic: trace bool state BEFORE and AFTER partition parse
        if (ctx.DiagCoefLog && ctx.DiagBlockCount < 2 && row == 0 && col == 0)
        {
            var stPre = c.GetState();
            Console.WriteLine($"  PRE_PARTITION: bl={bl} partCtx={partCtx} high={stPre.High} cw=0x{stPre.CodeWord:X8} bits={stPre.Bits} bufPos={stPre.BufPos}");
        }

        if (bl == Vp9BlockLevel.Bl8x8)
        {
            var bp = (Vp9Partition)c.GetTree(Vp9Data.PartitionTree, partProbs);
            DecodeBlock(ref c, row, col, bl, bp, yOff, uvOff);
            ctx.Counts.Partition[blInt * 4 * 4 + partCtx * 4 + (int)bp]++;
            return;
        }

        if (col + hbs < ctx.Cols)
        {
            if (row + hbs < ctx.Rows)
            {
                var bp = (Vp9Partition)c.GetTree(Vp9Data.PartitionTree, partProbs);
                if (ctx.DiagCoefLog && ctx.DiagBlockCount < 3)
                {
                    var st = c.GetState();
                    Console.WriteLine($"  PARTITION: bl={bl} partCtx={partCtx} probs=[{partProbs[0]},{partProbs[1]},{partProbs[2]}] result={bp} high={st.High} cw=0x{st.CodeWord:X8} bits={st.Bits} bufPos={st.BufPos}");
                }
                switch (bp)
                {
                    case Vp9Partition.None:
                        DecodeBlock(ref c, row, col, bl, bp, yOff, uvOff);
                        break;

                    case Vp9Partition.Horizontal:
                        DecodeBlock(ref c, row, col, bl, bp, yOff, uvOff);
                        DecodeBlock(ref c, row + hbs, col, bl, bp,
                            yOff + hbs * 8 * yStride,
                            uvOff + (hbs * 8 * uvStride >> ctx.SubsamplingY));
                        break;

                    case Vp9Partition.Vertical:
                        DecodeBlock(ref c, row, col, bl, bp, yOff, uvOff);
                        DecodeBlock(ref c, row, col + hbs, bl, bp,
                            yOff + hbs * 8,
                            uvOff + (hbs * 8 >> ctx.SubsamplingX));
                        break;

                    case Vp9Partition.Split:
                        var nextBl = (Vp9BlockLevel)(blInt + 1);
                        DecodeSuperblock(ref c, row, col, nextBl, yOff, uvOff);
                        DecodeSuperblock(ref c, row, col + hbs, nextBl,
                            yOff + 8 * hbs,
                            uvOff + (8 * hbs >> ctx.SubsamplingX));
                        int yOff2 = yOff + hbs * 8 * yStride;
                        int uvOff2 = uvOff + (hbs * 8 * uvStride >> ctx.SubsamplingY);
                        DecodeSuperblock(ref c, row + hbs, col, nextBl, yOff2, uvOff2);
                        DecodeSuperblock(ref c, row + hbs, col + hbs, nextBl,
                            yOff2 + 8 * hbs,
                            uvOff2 + (8 * hbs >> ctx.SubsamplingX));
                        break;
                }
                ctx.Counts.Partition[blInt * 4 * 4 + partCtx * 4 + (int)bp]++;
            }
            else if (c.GetProbBranchy(partProbs[1]))
            {
                var nextBl = (Vp9BlockLevel)(blInt + 1);
                DecodeSuperblock(ref c, row, col, nextBl, yOff, uvOff);
                DecodeSuperblock(ref c, row, col + hbs, nextBl,
                    yOff + 8 * hbs,
                    uvOff + (8 * hbs >> ctx.SubsamplingX));
                ctx.Counts.Partition[blInt * 4 * 4 + partCtx * 4 + (int)Vp9Partition.Split]++;
            }
            else
            {
                DecodeBlock(ref c, row, col, bl, Vp9Partition.Horizontal, yOff, uvOff);
                ctx.Counts.Partition[blInt * 4 * 4 + partCtx * 4 + (int)Vp9Partition.Horizontal]++;
            }
        }
        else if (row + hbs < ctx.Rows)
        {
            if (c.GetProbBranchy(partProbs[2]))
            {
                var nextBl = (Vp9BlockLevel)(blInt + 1);
                DecodeSuperblock(ref c, row, col, nextBl, yOff, uvOff);
                int yOff2 = yOff + hbs * 8 * yStride;
                int uvOff2 = uvOff + (hbs * 8 * uvStride >> ctx.SubsamplingY);
                DecodeSuperblock(ref c, row + hbs, col, nextBl, yOff2, uvOff2);
                ctx.Counts.Partition[blInt * 4 * 4 + partCtx * 4 + (int)Vp9Partition.Split]++;
            }
            else
            {
                DecodeBlock(ref c, row, col, bl, Vp9Partition.Vertical, yOff, uvOff);
                ctx.Counts.Partition[blInt * 4 * 4 + partCtx * 4 + (int)Vp9Partition.Vertical]++;
            }
        }
        else
        {
            var nextBl = (Vp9BlockLevel)(blInt + 1);
            DecodeSuperblock(ref c, row, col, nextBl, yOff, uvOff);
            ctx.Counts.Partition[blInt * 4 * 4 + partCtx * 4 + (int)Vp9Partition.Split]++;
        }
    }
}



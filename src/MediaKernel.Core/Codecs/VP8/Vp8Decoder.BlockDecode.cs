// VP8 decoder — macroblock mode decode and coefficient decode
// Ported from FFmpeg libavcodec/vp8.c (decode_mb_mode, decode_mb_coeffs, etc.)

using System;
using System.Runtime.CompilerServices;
using MediaKernel.Core.Codecs.Vp9; // Vp9BoolDecoder

namespace MediaKernel.Core.Codecs.VP8;

public sealed partial class Vp8Decoder
{
    // Per-MB working state (equivalent to VP8ThreadData in FFmpeg)

    /// <summary>DCT coefficient blocks: [6 planes (4Y+U+V)][4 sub-blocks][16 coeffs].</summary>
    private readonly short[][][] blockCoeffs = InitBlockCoeffs();

    /// <summary>DC-only block for Y2 (luma DC WHT): [16 coeffs].</summary>
    private readonly short[] blockDc = new short[16];

    /// <summary>Non-zero count cache: [6 planes][4 sub-blocks].</summary>
    private readonly byte[,] nonZeroCountCache = new byte[6, 4];

    /// <summary>Left NNZ context: [9] (4 luma + 2 cb + 2 cr + 1 dc).</summary>
    private readonly byte[] leftNnz = new byte[9];

    /// <summary>Intra 4x4 prediction modes for left column (4 values).</summary>
    private readonly byte[] intra4x4PredModeLeft = new byte[4];

    private static short[][][] InitBlockCoeffs()
    {
        var b = new short[6][][];
        for (int i = 0; i < 6; i++)
        {
            b[i] = new short[4][];
            for (int j = 0; j < 4; j++)
                b[i][j] = new short[16];
        }
        return b;
    }

    // ==================== Mode decode ====================

    /// <summary>
    /// Decode macroblock mode (intra/inter, prediction modes, MVs).
    /// Port of decode_mb_mode (VP8 only, skipping VP7 paths).
    /// </summary>
    private void DecodeMbMode(ref Vp8Macroblock mb, int mbX, int mbY,
                                      ref byte segment, byte refSegment)
    {
        // Segmentation
        if (ctx.Segmentation.UpdateMap)
        {
            int bit = headerCoder.GetProb(ctx.Prob[0].SegmentId[0]);
            segment = (byte)(headerCoder.GetProb(ctx.Prob[0].SegmentId[1 + bit]) + 2 * bit);
        }
        else if (ctx.Segmentation.Enabled)
        {
            segment = refSegment;
        }
        mb.Segment = segment;

        // Skip flag
        mb.Skip = ctx.MbSkipEnabled ? (byte)headerCoder.GetProb(ctx.Prob[0].MbSkip) : (byte)0;

        if (ctx.Keyframe)
        {
            // Keyframe: intra mode from tree
            mb.Mode = (byte)headerCoder.GetTree(
                Vp8Data.Pred16x16TreeIntra, Vp8Data.Pred16x16ProbIntra);

            if (mb.Mode == (byte)Vp8Pred16x16Mode.I4x4)
            {
                DecodeIntra4x4Modes(ref mb, mbX, keyframe: true);
            }
            else
            {
                // Fill intra 4x4 prediction modes with the corresponding 4x4 mode
                byte mode4x4 = Vp8Data.Pred4x4ModeFromChroma[mb.Mode];
                int topBase = 4 * mbX;
                for (int i = 0; i < 4; i++)
                {
                    ctx.Intra4x4PredModeTop![topBase + i] = mode4x4;
                    intra4x4PredModeLeft[i] = mode4x4;
                }
            }

            mb.ChromaPredMode = (byte)headerCoder.GetTree(
                Vp8Data.Pred8x8cTree, Vp8Data.Pred8x8cProbIntra);
            mb.RefFrame = (byte)Vp8FrameType.Current;
        }
        else if (headerCoder.GetProbBranchy(ctx.Prob[0].Intra))
        {
            // Inter MB
            if (headerCoder.GetProbBranchy(ctx.Prob[0].Last))
            {
                mb.RefFrame = headerCoder.GetProb(ctx.Prob[0].Golden) != 0
                    ? (byte)Vp8FrameType.Altref
                    : (byte)Vp8FrameType.Golden;
            }
            else
            {
                mb.RefFrame = (byte)Vp8FrameType.Previous;
            }

            // Motion vectors
            DecodeMvs(ref mb, mbX, mbY);
        }
        else
        {
            // Intra MB on inter frame
            mb.Mode = (byte)headerCoder.GetTree(
                Vp8Data.Pred16x16TreeInter, ctx.Prob[0].Pred16x16);

            if (mb.Mode == (byte)Vp8Pred16x16Mode.I4x4)
                DecodeIntra4x4Modes(ref mb, mbX, keyframe: false);

            mb.ChromaPredMode = (byte)headerCoder.GetTree(
                Vp8Data.Pred8x8cTree, ctx.Prob[0].Pred8x8c);
            mb.RefFrame = (byte)Vp8FrameType.Current;
            mb.Partitioning = (byte)Vp8SplitMvMode.None;
            mb.Mv = Vp8Mv.Zero;
            SetBmv(ref mb, 0, Vp8Mv.Zero);
        }
    }

    /// <summary>
    /// Decode intra 4x4 prediction modes for all 16 sub-blocks.
    /// Port of decode_intra4x4_modes (VP8 only).
    /// </summary>
    private unsafe void DecodeIntra4x4Modes(ref Vp8Macroblock mb, int mbX, bool keyframe)
    {
        if (keyframe)
        {
            int topBase = 4 * mbX;
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    int topMode = ctx.Intra4x4PredModeTop![topBase + x];
                    int leftMode = intra4x4PredModeLeft[y];
                    byte mode = (byte)headerCoder.GetTree(
                        Vp8Data.Pred4x4Tree,
                        Vp8Data.Pred4x4ProbIntraSlice(topMode, leftMode));
                    mb.Intra4x4PredMode[y * 4 + x] = mode;
                    ctx.Intra4x4PredModeTop[topBase + x] = mode;
                    intra4x4PredModeLeft[y] = mode;
                }
            }
        }
        else
        {
            for (int i = 0; i < 16; i++)
                mb.Intra4x4PredMode[i] = (byte)headerCoder.GetTree(
                    Vp8Data.Pred4x4Tree, Vp8Data.Pred4x4ProbInter);
        }
    }

    // ==================== MV decode ====================

    /// <summary>
    /// Decode inter-frame motion vectors.
    /// Port of vp8_decode_mvs.
    /// </summary>
    private unsafe void DecodeMvs(ref Vp8Macroblock mb, int mbX, int mbY)
    {
        // Layout 1: macroblocks[(mbY+1)*(mbWidth+1)+1+mbX], with sentinel row/column at zero
        int stride = ctx.MbWidth + 1;
        int mbIdx = (mbY + 1) * stride + 1 + mbX;
        ref var topMb = ref ctx.Macroblocks![mbIdx - stride];
        ref var leftMb = ref ctx.Macroblocks![mbIdx - 1];
        ref var topLeftMb = ref ctx.Macroblocks![mbIdx - stride - 1];

        int curSignBias = ctx.SignBias[mb.RefFrame];

        // FFmpeg near_mv[4]: indices 0=CNT_ZERO, 1=CNT_NEAREST, 2=CNT_NEAR, 3=CNT_SPLITMV
        Vp8Mv nearMv0 = Vp8Mv.Zero; // CNT_ZERO
        Vp8Mv nearMv1 = Vp8Mv.Zero; // CNT_NEAREST
        Vp8Mv nearMv2 = Vp8Mv.Zero; // CNT_NEAR
        Vp8Mv nearMv3 = Vp8Mv.Zero; // CNT_SPLITMV (3rd distinct MV)
        byte cnt0 = 0; // CNT_ZERO
        byte cnt1 = 0; // CNT_NEAREST
        byte cnt2 = 0; // CNT_NEAR
        byte cnt3 = 0; // CNT_SPLITMV
        int idx = 0;

        // Process top, left, top-left edges (sentinels handle boundaries)
        MvEdgeCheck(ref topMb, curSignBias, ref nearMv0, ref nearMv1, ref nearMv2, ref nearMv3,
                    ref cnt0, ref cnt1, ref cnt2, ref cnt3, ref idx, 2); // top: weight=2

        MvEdgeCheck(ref leftMb, curSignBias, ref nearMv0, ref nearMv1, ref nearMv2, ref nearMv3,
                    ref cnt0, ref cnt1, ref cnt2, ref cnt3, ref idx, 2); // left: weight=2

        MvEdgeCheck(ref topLeftMb, curSignBias, ref nearMv0, ref nearMv1, ref nearMv2, ref nearMv3,
                    ref cnt0, ref cnt1, ref cnt2, ref cnt3, ref idx, 1); // top-left: weight=1

        mb.Partitioning = (byte)Vp8SplitMvMode.None;

        if (headerCoder.GetProbBranchy(Vp8Data.ModeContexts[cnt0, 0]))
        {
            mb.Mode = (byte)Vp8InterMvMode.Mv;

            // If we have three distinct MVs, merge first and last if they're the same
            // FFmpeg: near_mv[1+VP8_EDGE_TOP] == near_mv[1+VP8_EDGE_TOPLEFT] → near_mv[1] == near_mv[3]
            if (cnt3 != 0 && nearMv1.AsUint() == nearMv3.AsUint())
                cnt1++;

            // Swap near and nearest if necessary
            if (cnt2 > cnt1)
            {
                (cnt1, cnt2) = (cnt2, cnt1);
                (nearMv1, nearMv2) = (nearMv2, nearMv1);
            }

            if (headerCoder.GetProbBranchy(Vp8Data.ModeContexts[cnt1, 1]))
            {
                if (headerCoder.GetProbBranchy(Vp8Data.ModeContexts[cnt2, 2]))
                {
                    // Choose the best MV out of 0,0 and the nearest MV
                    mb.Mv = cnt1 >= cnt0 ? nearMv1 : nearMv0;
                    ClampMv(ref mb.Mv, mbX, mbY);

                    cnt3 = (byte)(
                        ((mbX > 0 && leftMb.Mode == (byte)Vp8InterMvMode.Split ? 1 : 0) +
                         (mbY > 0 && topMb.Mode == (byte)Vp8InterMvMode.Split ? 1 : 0)) * 2 +
                        (mbX > 0 && mbY > 0 && topLeftMb.Mode == (byte)Vp8InterMvMode.Split ? 1 : 0));

                    if (headerCoder.GetProbBranchy(Vp8Data.ModeContexts[cnt3, 3]))
                    {
                        mb.Mode = (byte)Vp8InterMvMode.Split;
                        int numSplitMvs = DecodeSplitMvs(ref mb, mbX, mbY);
                        // Last sub-MV becomes the MB MV
                        mb.Mv = GetBmv(ref mb, numSplitMvs - 1);
                    }
                    else
                    {
                        // New MV
                        mb.Mv.Y += (short)ReadMvComponent(ref headerCoder, ctx.Prob[0].Mvc, 0);
                        mb.Mv.X += (short)ReadMvComponent(ref headerCoder, ctx.Prob[0].Mvc, 1);
                        SetBmv(ref mb, 0, mb.Mv);
                    }
                }
                else
                {
                    // Near MV
                    mb.Mv = nearMv2;
                    ClampMv(ref mb.Mv, mbX, mbY);
                    SetBmv(ref mb, 0, mb.Mv);
                }
            }
            else
            {
                // Nearest MV
                mb.Mv = nearMv1;
                ClampMv(ref mb.Mv, mbX, mbY);
                SetBmv(ref mb, 0, mb.Mv);
            }
        }
        else
        {
            // Zero MV
            mb.Mode = (byte)Vp8InterMvMode.Zero;
            mb.Mv = Vp8Mv.Zero;
            SetBmv(ref mb, 0, Vp8Mv.Zero);
        }
    }

    /// <summary>
    /// MV edge check — examine a neighboring MB's MV and accumulate counts.
    /// Port of MV_EDGE_CHECK macro from FFmpeg.
    /// </summary>
    /// <summary>
    /// MV edge check — examine a neighboring MB's MV and accumulate counts.
    /// Port of MV_EDGE_CHECK macro from FFmpeg.
    /// FFmpeg uses near_mv[4] (indices 0-3). We mirror with nearMv0-nearMv3
    /// to avoid overwriting the CNT_NEAR slot when idx reaches 3 (CNT_SPLITMV).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MvEdgeCheck(ref Vp8Macroblock edge, int curSignBias,
                              ref Vp8Mv nearMv0, ref Vp8Mv nearMv1,
                              ref Vp8Mv nearMv2, ref Vp8Mv nearMv3,
                              ref byte cnt0, ref byte cnt1, ref byte cnt2, ref byte cnt3,
                              ref int idx, int weight)
    {
        int edgeRef = edge.RefFrame;
        if (edgeRef == (byte)Vp8FrameType.Current)
            return; // Sentinel or intra MB — no count increment (matches FFmpeg)

        uint mv = edge.Mv.AsUint();
        if (mv == 0)
        {
            cnt0 += (byte)weight;
            return;
        }

        // Sign bias compensation: negate MV if sign bias differs
        if (curSignBias != ctx.SignBias[edgeRef])
            mv = NegateMvSwar(mv);

        // FFmpeg: if (!n || mv != AV_RN32A(&near_mv[idx]))
        //   AV_WN32A(&near_mv[++idx], mv);
        Vp8Mv edgeMv = Vp8Mv.FromUint(mv);
        if (idx == 0 || mv != GetNearMv(idx, ref nearMv0, ref nearMv1, ref nearMv2, ref nearMv3).AsUint())
        {
            idx++;
            SetNearMv(idx, ref nearMv0, ref nearMv1, ref nearMv2, ref nearMv3, edgeMv);
        }

        switch (idx)
        {
            case 0: cnt0 += (byte)weight; break;
            case 1: cnt1 += (byte)weight; break;
            case 2: cnt2 += (byte)weight; break;
            case 3: cnt3 += (byte)weight; break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vp8Mv GetNearMv(int idx, ref Vp8Mv mv0, ref Vp8Mv mv1, ref Vp8Mv mv2, ref Vp8Mv mv3)
        => idx switch { 0 => mv0, 1 => mv1, 2 => mv2, _ => mv3 };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetNearMv(int idx, ref Vp8Mv mv0, ref Vp8Mv mv1, ref Vp8Mv mv2, ref Vp8Mv mv3, Vp8Mv val)
    {
        switch (idx)
        {
            case 0: mv0 = val; break;
            case 1: mv1 = val; break;
            case 2: mv2 = val; break;
            default: mv3 = val; break;
        }
    }

    /// <summary>SWAR negate of packed (x, y) in a uint32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint NegateMvSwar(uint mv)
    {
        mv = ~mv;
        mv = ((mv & 0x7fff7fff) + 0x00010001) ^ (mv & 0x80008000);
        return mv;
    }

    /// <summary>
    /// Decode split motion vectors.
    /// Port of decode_splitmvs (VP8 only).
    /// </summary>
    private unsafe int DecodeSplitMvs(ref Vp8Macroblock mb, int mbX, int mbY)
    {
        int stride = ctx.MbWidth + 1;
        int mbIdx = (mbY + 1) * stride + 1 + mbX;
        ref var leftMb = ref ctx.Macroblocks![mbIdx - 1];
        ref var topMb = ref ctx.Macroblocks![mbIdx - stride];

        // Determine split partition
        // Tree: 0→4x4, 10→8x8, 110→16x8, 111→8x16
        int partIdx;
        if (headerCoder.GetProbBranchy(Vp8Data.MbSplitProb[0]))
        {
            if (headerCoder.GetProbBranchy(Vp8Data.MbSplitProb[1]))
                partIdx = (int)Vp8SplitMvMode.TopBottom + headerCoder.GetProb(Vp8Data.MbSplitProb[2]);
            else
                partIdx = (int)Vp8SplitMvMode.Quarters;
        }
        else
        {
            partIdx = (int)Vp8SplitMvMode.Full4x4;
        }

        int num = Vp8Data.MbSplitCount[partIdx];
        mb.Partitioning = (byte)partIdx;

        for (int n = 0; n < num; n++)
        {
            int k = Vp8Data.MbFirstIdx[partIdx, n];

            // Get left and above MVs for sub-MV prediction
            Vp8Mv leftSub, aboveSub;

            if ((k & 3) == 0)
            {
                // Left edge of MB: use left MB's right-edge sub-block MV
                leftSub = GetSplitMvFromMb(ref leftMb, Vp8Data.MbSplits[leftMb.Partitioning, k + 3]);
            }
            else
            {
                leftSub = GetSplitMvFromMb(ref mb, Vp8Data.MbSplits[partIdx, k - 1]);
            }

            if (k <= 3)
            {
                // Top edge of MB: use top MB's bottom-edge sub-block MV
                aboveSub = GetSplitMvFromMb(ref topMb, Vp8Data.MbSplits[topMb.Partitioning, k + 12]);
            }
            else
            {
                aboveSub = GetSplitMvFromMb(ref mb, Vp8Data.MbSplits[partIdx, k - 4]);
            }

            // Get sub-MV probability context
            uint leftU = leftSub.AsUint();
            uint aboveU = aboveSub.AsUint();
            int probIdx = GetSubMvProbIdx(leftU, aboveU);

            // Decode sub-MV mode
            if (headerCoder.GetProbBranchy(Vp8Data.SubMvProb[probIdx, 0]))
            {
                if (headerCoder.GetProbBranchy(Vp8Data.SubMvProb[probIdx, 1]))
                {
                    if (headerCoder.GetProbBranchy(Vp8Data.SubMvProb[probIdx, 2]))
                    {
                        // New sub-MV
                        short y = (short)(mb.Mv.Y + ReadMvComponent(ref headerCoder, ctx.Prob[0].Mvc, 0));
                        short x = (short)(mb.Mv.X + ReadMvComponent(ref headerCoder, ctx.Prob[0].Mvc, 1));
                        SetBmv(ref mb, n, new Vp8Mv(x, y));
                    }
                    else
                    {
                        // Zero sub-MV
                        SetBmv(ref mb, n, Vp8Mv.Zero);
                    }
                }
                else
                {
                    // Above sub-MV
                    SetBmv(ref mb, n, aboveSub);
                }
            }
            else
            {
                // Left sub-MV
                SetBmv(ref mb, n, leftSub);
            }
        }

        return num;
    }

    /// <summary>Get sub-MV probability table index based on left and above MVs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetSubMvProbIdx(uint left, uint above)
    {
        if (left == above)
            return left != 0 ? 3 : 4;
        if (above == 0)
            return 2;
        return left != 0 ? 0 : 1;
    }

    /// <summary>Get MV from a macroblock's split sub-block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vp8Mv GetSplitMvFromMb(ref Vp8Macroblock mb, int subBlock)
    {
        if (mb.Mode == (byte)Vp8InterMvMode.Split)
            return GetBmv(ref mb, subBlock);
        return new Vp8Mv(mb.Mv.X, mb.Mv.Y);
    }

    /// <summary>Read MV component from MVC probabilities (2D array row).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadMvComponent(ref Vp9BoolDecoder c, byte[,] mvc, int component)
    {
        int x = 0;

        if (c.GetProbBranchy(mvc[component, 0]))
        {
            // Large MV
            for (int i = 0; i < 3; i++)
                x += c.GetProb(mvc[component, 9 + i]) << i;
            for (int i = 9; i > 3; i--)
                x += c.GetProb(mvc[component, 9 + i]) << i;
            if ((x & 0xFFF0) == 0 || c.GetProb(mvc[component, 12]) != 0)
                x += 8;
        }
        else
        {
            // Small MV tree
            int bit = c.GetProb(mvc[component, 2]);
            int ps = 3 + 3 * bit;
            x += 4 * bit;
            bit = c.GetProb(mvc[component, ps]);
            ps += 1 + bit;
            x += 2 * bit;
            x += c.GetProb(mvc[component, ps]);
        }

        return (x != 0 && c.GetProb(mvc[component, 1]) != 0) ? -x : x;
    }

    /// <summary>Clamp MV to frame boundaries.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClampMv(ref Vp8Mv mv, int mbX, int mbY)
    {
        // VP8 MV clamping: MARGIN = 16 << 2 = 64 quarter-pels
        // Per FFmpeg: mv_min = -MARGIN - mb_pos*64, mv_max = (mb_dim-1-mb_pos)*64 + MARGIN
        int minX = -((mbX + 1) * 64);
        int minY = -((mbY + 1) * 64);
        int maxX = (ctx.MbWidth - 1 - mbX) * 64 + 64;
        int maxY = (ctx.MbHeight - 1 - mbY) * 64 + 64;

        mv.X = (short)Math.Clamp(mv.X, minX, maxX);
        mv.Y = (short)Math.Clamp(mv.Y, minY, maxY);
    }

    // ==================== BMV helpers (accessing fixed buffer) ====================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vp8Mv GetBmv(ref Vp8Macroblock mb, int idx)
    {
        return new Vp8Mv(mb.Bmv[idx * 2], mb.Bmv[idx * 2 + 1]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void SetBmv(ref Vp8Macroblock mb, int idx, Vp8Mv mv)
    {
        mb.Bmv[idx * 2] = mv.X;
        mb.Bmv[idx * 2 + 1] = mv.Y;
    }

    // ==================== Coefficient decode ====================

    /// <summary>
    /// Decode all coefficients for a macroblock.
    /// Port of decode_mb_coeffs (VP8 only).
    /// </summary>
    private void DecodeMbCoeffs(ref Vp8Macroblock mb, ref Vp9BoolDecoder coeffCoder,
                                 Span<byte> topNnz, int mbX, int mbY)
    {
        int segment = mb.Segment;
        int lumaStart = 0;
        int lumaCtx = 3; // coeff type for Y without DC
        int blockDcPresent = 0;
        int nnzTotal = 0;

        // Clear coefficient blocks
        ClearBlockCoeffs();

        if (mb.Mode != (byte)Vp8Pred16x16Mode.I4x4 &&
            mb.Mode != (byte)Vp8InterMvMode.Split)
        {
            // Y2 DC block (WHT)
            int nnzPred = topNnz[8] + leftNnz[8];

            int nnz = DecodeBlockCoeffs(ref coeffCoder, blockDc,
                                         ctx.Prob[0].Token, 1, 0, nnzPred,
                                         ctx.Qmat[segment].LumaDcOnlyDcQmul,
                                         ctx.Qmat[segment].LumaDcOnlyAcQmul);

            topNnz[8] = leftNnz[8] = (byte)(nnz != 0 ? 1 : 0);

            if (nnz != 0)
            {
                nnzTotal += nnz;
                blockDcPresent = 1;

                if (nnz == 1)
                    Vp8Dsp.LumaDcWhtDc(blockCoeffs, blockDc);
                else
                    Vp8Dsp.LumaDcWht(blockCoeffs, blockDc);
            }

            lumaStart = 1;
            lumaCtx = 0; // Y with DC (type 0)
        }

        // Luma sub-blocks
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int nnzPred = leftNnz[y] + topNnz[x];
                int nnz = DecodeBlockCoeffs(ref coeffCoder, blockCoeffs[y][x],
                                             ctx.Prob[0].Token, lumaCtx, lumaStart, nnzPred,
                                             ctx.Qmat[segment].LumaDcQmul,
                                             ctx.Qmat[segment].LumaAcQmul);

                nonZeroCountCache[y, x] = (byte)(nnz + blockDcPresent);
                topNnz[x] = leftNnz[y] = (byte)(nnz != 0 ? 1 : 0);
                nnzTotal += nnz;
            }
        }

        // Chroma sub-blocks (Cb=plane4, Cr=plane5)
        for (int i = 4; i < 6; i++)
        {
            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 2; x++)
                {
                    int nnzPred = leftNnz[i + 2 * y] + topNnz[i + 2 * x];
                    int nnz = DecodeBlockCoeffs(ref coeffCoder, blockCoeffs[i][(y << 1) + x],
                                                 ctx.Prob[0].Token, 2, 0, nnzPred,
                                                 ctx.Qmat[segment].ChromaDcQmul,
                                                 ctx.Qmat[segment].ChromaAcQmul);
                    nonZeroCountCache[i, (y << 1) + x] = (byte)nnz;
                    topNnz[i + 2 * x] = leftNnz[i + 2 * y] = (byte)(nnz != 0 ? 1 : 0);
                    nnzTotal += nnz;
                }
            }
        }

        // If no coefficients were decoded, mark as skip
        if (nnzTotal == 0)
            mb.Skip = 1;
    }

    /// <summary>
    /// Decode one 4x4 block's coefficients.
    /// Port of decode_block_coeffs + decode_block_coeffs_internal (VP8 only).
    /// </summary>
    /// <returns>0 if EOB immediately, otherwise last coeff index + 1.</returns>
    private int DecodeBlockCoeffs(ref Vp9BoolDecoder c, short[] block,
                                   byte[,,,] tokenProbs, int coeffType,
                                   int startIdx, int nnzPred,
                                   short dcQmul, short acQmul)
    {
        // Initial token probability from context
        int ctx0 = Math.Min(nnzPred, 2);
        byte p0 = tokenProbs[coeffType, startIdx, ctx0, 0];

        // Check for immediate EOB
        if (!c.GetProbBranchy(p0))
            return 0;

        return DecodeBlockCoeffsInternal(ref c, block, tokenProbs, coeffType,
                                          startIdx, ctx0, dcQmul, acQmul);
    }

    /// <summary>
    /// Inner coefficient decode loop.
    /// Port of decode_block_coeffs_internal (VP8 variant, using zigzag scan).
    /// </summary>
    private static int DecodeBlockCoeffsInternal(ref Vp9BoolDecoder c, short[] block,
                                            byte[,,,] probs, int coeffType,
                                            int i, int ctx0,
                                            short dcQmul, short acQmul)
    {
        ReadOnlySpan<byte> scan = Vp8Data.DefaultScanOrder;
        int tokenCtx = ctx0;
        bool skipEob = true;

        do
        {
            int coeff;

            // Check EOB (skipped on first iteration — first token after nonzero is never EOB)
            if (!skipEob)
            {
                if (!c.GetProbBranchy(probs[coeffType, i, tokenCtx, 0]))
                    break;
            }
            skipEob = false;

            // Check DCT_0 (zero coefficient)
            if (!c.GetProbBranchy(probs[coeffType, i, tokenCtx, 1]))
            {
                tokenCtx = 0;
                skipEob = true;
                continue;
            }

            // Non-zero coefficient
            if (!c.GetProbBranchy(probs[coeffType, i, tokenCtx, 2]))
            {
                // DCT_1
                coeff = 1;
                tokenCtx = 1;
            }
            else
            {
                if (!c.GetProbBranchy(probs[coeffType, i, tokenCtx, 3]))
                {
                    // DCT_2, DCT_3, DCT_4
                    coeff = c.GetProbBranchy(probs[coeffType, i, tokenCtx, 4]) ? 1 : 0;
                    if (coeff != 0)
                        coeff += c.GetProb(probs[coeffType, i, tokenCtx, 5]);
                    coeff += 2;
                }
                else
                {
                    // DCT_CAT1..DCT_CAT6
                    if (!c.GetProbBranchy(probs[coeffType, i, tokenCtx, 6]))
                    {
                        if (!c.GetProbBranchy(probs[coeffType, i, tokenCtx, 7]))
                        {
                            // DCT_CAT1: 5 + extra
                            coeff = 5 + c.GetProb(Vp8Data.DctCat1Prob[0]);
                        }
                        else
                        {
                            // DCT_CAT2: 7 + extra
                            coeff = 7;
                            coeff += c.GetProb(Vp8Data.DctCat2Prob[0]) << 1;
                            coeff += c.GetProb(Vp8Data.DctCat2Prob[1]);
                        }
                    }
                    else
                    {
                        // DCT_CAT3..DCT_CAT6
                        int a = c.GetProb(probs[coeffType, i, tokenCtx, 8]);
                        int b = c.GetProb(probs[coeffType, i, tokenCtx, 9 + a]);
                        int cat = (a << 1) + b;
                        coeff = 3 + (8 << cat);
                        coeff += RacGetCoeff(ref c, Vp8Data.DctCatProb(cat));
                    }
                }
                tokenCtx = 2;
            }

            // Sign bit and dequantize
            int sign = c.Get();
            int qmul = i == 0 ? dcQmul : acQmul;
            block[scan[i]] = (short)((sign != 0 ? -coeff : coeff) * qmul);

        } while (++i < 16);

        return i;
    }

    private void ClearBlockCoeffs()
    {
        for (int i = 0; i < 6; i++)
            for (int j = 0; j < 4; j++)
                Array.Clear(blockCoeffs[i][j]);
        Array.Clear(blockDc);
    }
}

// VP9 decoder — motion vector derivation
// Ported from FFmpeg libavcodec/vp9mvs.c

using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vp9;

public sealed partial class Vp9Decoder
{
    // MV reference block offsets per block size: [13 block sizes][8 candidates][2: col,row]
    // Flattened from FFmpeg's mv_ref_blk_off[N_BS_SIZES][8][2]
    private static ReadOnlySpan<sbyte> MvRefBlkOff => new sbyte[]
    {
        // BS_64x64
         3,-1,  -1, 3,   4,-1,  -1, 4,  -1,-1,   0,-1,  -1, 0,   6,-1,
        // BS_64x32
         0,-1,  -1, 0,   4,-1,  -1, 2,  -1,-1,   0,-3,  -3, 0,   2,-1,
        // BS_32x64
        -1, 0,   0,-1,  -1, 4,   2,-1,  -1,-1,  -3, 0,   0,-3,  -1, 2,
        // BS_32x32
         1,-1,  -1, 1,   2,-1,  -1, 2,  -1,-1,   0,-3,  -3, 0,  -3,-3,
        // BS_32x16
         0,-1,  -1, 0,   2,-1,  -1,-1,  -1, 1,   0,-3,  -3, 0,  -3,-3,
        // BS_16x32
        -1, 0,   0,-1,  -1, 2,  -1,-1,   1,-1,  -3, 0,   0,-3,  -3,-3,
        // BS_16x16
         0,-1,  -1, 0,   1,-1,  -1, 1,  -1,-1,   0,-3,  -3, 0,  -3,-3,
        // BS_16x8
         0,-1,  -1, 0,   1,-1,  -1,-1,   0,-2,  -2, 0,  -2,-1,  -1,-2,
        // BS_8x16
        -1, 0,   0,-1,  -1, 1,  -1,-1,  -2, 0,   0,-2,  -1,-2,  -2,-1,
        // BS_8x8
         0,-1,  -1, 0,  -1,-1,   0,-2,  -2, 0,  -1,-2,  -2,-1,  -2,-2,
        // BS_8x4
         0,-1,  -1, 0,  -1,-1,   0,-2,  -2, 0,  -1,-2,  -2,-1,  -2,-2,
        // BS_4x8
         0,-1,  -1, 0,  -1,-1,   0,-2,  -2, 0,  -1,-2,  -2,-1,  -2,-2,
        // BS_4x4
         0,-1,  -1, 0,  -1,-1,   0,-2,  -2, 0,  -1,-2,  -2,-1,  -2,-2,
    };

    private const uint InvalidMv = 0x80008000u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vp9Mv ClampMv(Vp9Mv src, Vp9Mv minMv, Vp9Mv maxMv)
    {
        return new Vp9Mv
        {
            X = Math.Clamp(src.X, minMv.X, maxMv.X),
            Y = Math.Clamp(src.Y, minMv.Y, maxMv.Y),
        };
    }

    /// <summary>
    /// Find reference motion vectors for the current block.
    /// Port of FFmpeg find_ref_mvs().
    /// </summary>
    /// <param name="pmv">Output MV prediction</param>
    /// <param name="refFrame">Reference frame index to search for</param>
    /// <param name="z">Which MV component (0=first ref, 1=second ref)</param>
    /// <param name="idx">0 = nearest, 1 = near</param>
    /// <param name="sb">Sub-block index: -1 for >=8x8, 0..3 for 4x4 sub-blocks</param>
    private void FindRefMvs(ref Vp9Mv pmv, int refFrame, int z, int idx, int sb)
    {
        ref var b = ref ctx.CurrentBlock;
        ref readonly var h = ref ctx.Header;
        int row = ctx.CurrentRow;
        int col = ctx.CurrentCol;
        int row7 = row & 7;

        // Get candidate offsets for this block size
        int bsIdx = (int)b.BlockSize;
        int offBase = bsIdx * 16; // 8 candidates * 2 coords

        uint mem = InvalidMv;
        uint memSub8x8 = InvalidMv;
        int i;

        var curMvs = ctx.Frames[Vp9DecoderContext.CurFrame].Mvs;
        int mvStride = ctx.SuperblockCols * 8;

        if (sb >= 0)
        {
            // Sub-8x8 block: check previously decoded sub-blocks first
            if (sb == 2 || sb == 1)
            {
                // RETURN_DIRECT_MV(b->mv[0][z])
                uint m = b.GetMv(0, z).AsUint32();
                if (idx == 0) { Unsafe.As<Vp9Mv, uint>(ref pmv) = m; return; }
                else if (mem == InvalidMv) mem = m;
                else if (m != mem) { Unsafe.As<Vp9Mv, uint>(ref pmv) = m; return; }
            }
            else if (sb == 3)
            {
                // RETURN_DIRECT_MV(b->mv[2][z])
                uint m = b.GetMv(2, z).AsUint32();
                if (idx == 0) { Unsafe.As<Vp9Mv, uint>(ref pmv) = m; return; }
                else if (mem == InvalidMv) mem = m;
                else if (m != mem) { Unsafe.As<Vp9Mv, uint>(ref pmv) = m; return; }

                // RETURN_DIRECT_MV(b->mv[1][z])
                m = b.GetMv(1, z).AsUint32();
                if (idx == 0) { Unsafe.As<Vp9Mv, uint>(ref pmv) = m; return; }
                else if (mem == InvalidMv) mem = m;
                else if (m != mem) { Unsafe.As<Vp9Mv, uint>(ref pmv) = m; return; }

                // RETURN_DIRECT_MV(b->mv[0][z])
                m = b.GetMv(0, z).AsUint32();
                if (idx == 0) { Unsafe.As<Vp9Mv, uint>(ref pmv) = m; return; }
                else if (mem == InvalidMv) mem = m;
                else if (m != mem) { Unsafe.As<Vp9Mv, uint>(ref pmv) = m; return; }
            }

            // Check above MV context
            if (row > 0 && curMvs != null)
            {
                ref var mvPair = ref curMvs[(row - 1) * mvStride + col];
                int aboveMvIdx = (2 * col + (sb & 1)) * 2;
                if (mvPair.Ref0 == refFrame)
                {
                    if (ReturnMv(ctx.AboveMvCtx![aboveMvIdx], ref pmv, ref mem, ref memSub8x8, sb, idx))
                        return;
                }
                else if (mvPair.Ref1 == refFrame)
                {
                    if (ReturnMv(ctx.AboveMvCtx![aboveMvIdx + 1], ref pmv, ref mem, ref memSub8x8, sb, idx))
                        return;
                }
            }

            // Check left MV context
            if (col > ctx.TileColStart && curMvs != null)
            {
                ref var mvPair = ref curMvs[row * mvStride + col - 1];
                int leftMvIdx = (2 * row7 + (sb >> 1)) * 2;
                if (mvPair.Ref0 == refFrame)
                {
                    if (ReturnMv(ctx.LeftMvCtx[leftMvIdx], ref pmv, ref mem, ref memSub8x8, sb, idx))
                        return;
                }
                else if (mvPair.Ref1 == refFrame)
                {
                    if (ReturnMv(ctx.LeftMvCtx[leftMvIdx + 1], ref pmv, ref mem, ref memSub8x8, sb, idx))
                        return;
                }
            }

            i = 2;
        }
        else
        {
            i = 0;
        }

        // Previously coded MVs in this neighborhood, using same reference frame
        for (; i < 8; i++)
        {
            int c = MvRefBlkOff[offBase + i * 2] + col;
            int r = MvRefBlkOff[offBase + i * 2 + 1] + row;

            if (c >= ctx.TileColStart && c < ctx.Cols &&
                r >= 0 && r < ctx.Rows && curMvs != null)
            {
                ref var mvPair = ref curMvs[r * mvStride + c];
                if (mvPair.Ref0 == refFrame)
                {
                    if (ReturnMv(mvPair.Mv0, ref pmv, ref mem, ref memSub8x8, sb, idx))
                        return;
                }
                else if (mvPair.Ref1 == refFrame)
                {
                    if (ReturnMv(mvPair.Mv1, ref pmv, ref mem, ref memSub8x8, sb, idx))
                        return;
                }
            }
        }

        // MV at this position in previous frame, using same reference frame
        if (h.UseLastFrameMvs != 0)
        {
            var prevMvs = ctx.Frames[Vp9DecoderContext.RefFrameMvPair].Mvs;
            if (prevMvs != null)
            {
                ref var mvPair = ref prevMvs[row * mvStride + col];
                if (EnableDiagCoefLog && row == 0 && col == 0)
                    Console.WriteLine($"FindRefMvs temporal: row={row} col={col} sb={sb} idx={idx} ref={refFrame} " +
                        $"prevRef0={mvPair.Ref0} prevMv0=({mvPair.Mv0.X},{mvPair.Mv0.Y}) " +
                        $"prevRef1={mvPair.Ref1} prevMv1=({mvPair.Mv1.X},{mvPair.Mv1.Y})");
                if (mvPair.Ref0 == refFrame)
                {
                    if (ReturnMv(mvPair.Mv0, ref pmv, ref mem, ref memSub8x8, sb, idx))
                        return;
                }
                else if (mvPair.Ref1 == refFrame)
                {
                    if (ReturnMv(mvPair.Mv1, ref pmv, ref mem, ref memSub8x8, sb, idx))
                        return;
                }
            }
        }

        // Previously coded MVs in neighborhood, using DIFFERENT reference frame (with sign scaling)
        for (i = 0; i < 8; i++)
        {
            int c = MvRefBlkOff[offBase + i * 2] + col;
            int r = MvRefBlkOff[offBase + i * 2 + 1] + row;

            if (c >= ctx.TileColStart && c < ctx.Cols &&
                r >= 0 && r < ctx.Rows && curMvs != null)
            {
                ref var mvPair = ref curMvs[r * mvStride + c];

                if (mvPair.Ref0 != refFrame && mvPair.Ref0 >= 0)
                {
                    bool scale = h.GetSignBias(mvPair.Ref0) != h.GetSignBias(refFrame);
                    Vp9Mv scaled = scale ? new Vp9Mv { X = (short)-mvPair.Mv0.X, Y = (short)-mvPair.Mv0.Y } : mvPair.Mv0;
                    if (ReturnMv(scaled, ref pmv, ref mem, ref memSub8x8, sb, idx))
                        return;
                }

                if (mvPair.Ref1 != refFrame && mvPair.Ref1 >= 0 &&
                    mvPair.Mv0 != mvPair.Mv1)
                {
                    bool scale = h.GetSignBias(mvPair.Ref1) != h.GetSignBias(refFrame);
                    Vp9Mv scaled = scale ? new Vp9Mv { X = (short)-mvPair.Mv1.X, Y = (short)-mvPair.Mv1.Y } : mvPair.Mv1;
                    if (ReturnMv(scaled, ref pmv, ref mem, ref memSub8x8, sb, idx))
                        return;
                }
            }
        }

        // MV at this position in previous frame, using different reference frame
        if (h.UseLastFrameMvs != 0)
        {
            var prevMvs = ctx.Frames[Vp9DecoderContext.RefFrameMvPair].Mvs;
            if (prevMvs != null)
            {
                ref var mvPair = ref prevMvs[row * mvStride + col];

                if (mvPair.Ref0 != refFrame && mvPair.Ref0 >= 0)
                {
                    bool scale = h.GetSignBias(mvPair.Ref0) != h.GetSignBias(refFrame);
                    Vp9Mv scaled = scale ? new Vp9Mv { X = (short)-mvPair.Mv0.X, Y = (short)-mvPair.Mv0.Y } : mvPair.Mv0;
                    if (ReturnMv(scaled, ref pmv, ref mem, ref memSub8x8, sb, idx))
                        return;
                }

                if (mvPair.Ref1 != refFrame && mvPair.Ref1 >= 0 &&
                    mvPair.Mv0 != mvPair.Mv1)
                {
                    bool scale = h.GetSignBias(mvPair.Ref1) != h.GetSignBias(refFrame);
                    Vp9Mv scaled = scale ? new Vp9Mv { X = (short)-mvPair.Mv1.X, Y = (short)-mvPair.Mv1.Y } : mvPair.Mv1;
                    if (ReturnMv(scaled, ref pmv, ref mem, ref memSub8x8, sb, idx))
                        return;
                }
            }
        }

        // No MV found — zero
        pmv = default;
        pmv = ClampMv(pmv, ctx.MinMv, ctx.MaxMv);
    }

    /// <summary>
    /// RETURN_MV macro translation: checks candidate MV, clamps it, and returns true if we found a result.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ReturnMv(Vp9Mv mv, ref Vp9Mv pmv, ref uint mem, ref uint memSub8x8, int sb, int idx)
    {
        if (sb > 0)
        {
            // Sub-8x8 path (idx must be 1, mem must be valid)
            if (memSub8x8 == InvalidMv)
            {
                var tmp = ClampMv(mv, ctx.MinMv, ctx.MaxMv);
                uint m = tmp.AsUint32();
                if (m != mem)
                {
                    pmv = tmp;
                    return true;
                }
                memSub8x8 = mv.AsUint32();
            }
            else if (memSub8x8 != mv.AsUint32())
            {
                var tmp = ClampMv(mv, ctx.MinMv, ctx.MaxMv);
                uint m = tmp.AsUint32();
                if (m != mem)
                    pmv = tmp;
                else
                    pmv = default; // Zero MV (matches FFmpeg's BUG comment)
                return true;
            }
        }
        else
        {
            // Normal path (>=8x8 blocks)
            uint m = mv.AsUint32();
            if (idx == 0)
            {
                pmv = ClampMv(mv, ctx.MinMv, ctx.MaxMv);
                return true;
            }
            else if (mem == InvalidMv)
            {
                mem = m;
            }
            else if (m != mem)
            {
                pmv = ClampMv(mv, ctx.MinMv, ctx.MaxMv);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Read a single MV component (x or y) from the bitstream.
    /// Port of FFmpeg read_mv_component().
    /// </summary>
    private int ReadMvComponent(ref Vp9BoolDecoder c, int idx, int hp)
    {
        var prob = ctx.ActiveProb;
        var counts = ctx.Counts;

        // MV comp probabilities layout in flat array:
        // Each component has 33 bytes: sign(1), classes(10), class0(1), bits(10), class0_fp(6), fp(3), class0_hp(1), hp(1)
        int compBase = idx * 33;

        int sign = c.GetProb(prob.MvComp[compBase + 0]); // sign
        counts.MvSign[idx * 2 + sign]++;

        int cls = c.GetTree(Vp9Data.MvClassTree, prob.MvComp.AsSpan(compBase + 1, 10)); // classes[10]
        counts.MvClasses[idx * 11 + cls]++;

        int n;
        if (cls != 0)
        {
            int m = 0;
            for (int bit = 0; bit < cls; bit++)
            {
                int b = c.GetProb(prob.MvComp[compBase + 12 + bit]); // bits[bit]
                m |= b << bit;
                counts.MvBits[idx * 20 + bit * 2 + b]++;
            }
            m <<= 3;

            int fpBit = c.GetTree(Vp9Data.MvFpTree, prob.MvComp.AsSpan(compBase + 28, 3)); // fp[3]
            m |= fpBit << 1;
            counts.MvFp[idx * 4 + fpBit]++;

            if (hp != 0)
            {
                int hpBit = c.GetProb(prob.MvComp[compBase + 32]); // hp
                counts.MvHp[idx * 2 + hpBit]++;
                m |= hpBit;
            }
            else
            {
                m |= 1;
                counts.MvHp[idx * 2 + 1]++; // count even if not coded (FFmpeg bug compat)
            }

            n = m + (8 << cls);
        }
        else
        {
            // Class 0
            n = c.GetProb(prob.MvComp[compBase + 11]); // class0
            counts.MvClass0[idx * 2 + n]++;

            // class0_fp: [2][3] = 6 bytes at compBase + 22
            int fpBase = compBase + 22 + n * 3;
            int fpBit = c.GetTree(Vp9Data.MvFpTree, prob.MvComp.AsSpan(fpBase, 3));
            counts.MvClass0Fp[idx * 8 + n * 4 + fpBit]++;
            n = (n << 3) | (fpBit << 1);

            if (hp != 0)
            {
                int hpBit = c.GetProb(prob.MvComp[compBase + 31]); // class0_hp
                counts.MvClass0Hp[idx * 2 + hpBit]++;
                n |= hpBit;
            }
            else
            {
                n |= 1;
                counts.MvClass0Hp[idx * 2 + 1]++; // count even if not coded
            }
        }

        return sign != 0 ? -(n + 1) : (n + 1);
    }

    /// <summary>
    /// Fill motion vectors for the current block.
    /// Port of FFmpeg ff_vp9_fill_mv().
    /// Called from block decode after mode/ref parsing is complete.
    /// </summary>
    /// <param name="c">Bool decoder for NEWMV reading</param>
    /// <param name="mv0">Output MV for ref[0]</param>
    /// <param name="mv1">Output MV for ref[1]</param>
    /// <param name="mode">Inter mode: NEARESTMV, NEARMV, ZEROMV, NEWMV</param>
    /// <param name="sb">Sub-block index (-1 for >=8x8, 0..3 for sub-blocks)</param>
    private void FillMv(ref Vp9BoolDecoder c, ref Vp9Mv mv0, ref Vp9Mv mv1, int mode, int sb)
    {
        ref var b = ref ctx.CurrentBlock;
        ref readonly var h = ref ctx.Header;

        if (mode == (int)Vp9InterMode.ZeroMv)
        {
            mv0 = default;
            mv1 = default;
        }
        else
        {
            int hp;

            // Find reference MV for ref[0]
            FindRefMvs(ref mv0, b.Ref0, 0,
                       mode == (int)Vp9InterMode.NearMv ? 1 : 0,
                       mode == (int)Vp9InterMode.NewMv ? -1 : sb);

            // Round to half-pel if high precision not enabled or MV is large
            hp = h.HighPrecisionMvs != 0 && Math.Abs(mv0.X) < 64 && Math.Abs(mv0.Y) < 64 ? 1 : 0;
            if (mode == (int)Vp9InterMode.NewMv || sb == -1)
            {
                if (hp == 0)
                    RoundMvToHalfPel(ref mv0);
            }

            if (mode == (int)Vp9InterMode.NewMv)
            {
                int j = c.GetTree(Vp9Data.MvJointTree, ctx.ActiveProb.MvJoint);
                ctx.Counts.MvJoint[j]++;

                if (j >= (int)Vp9MvJoint.Vertical)
                    mv0.Y = (short)(mv0.Y + ReadMvComponent(ref c, 0, hp));
                if ((j & 1) != 0)
                    mv0.X = (short)(mv0.X + ReadMvComponent(ref c, 1, hp));
            }

            if (b.Comp != 0)
            {
                // Find reference MV for ref[1]
                FindRefMvs(ref mv1, b.Ref1, 1,
                           mode == (int)Vp9InterMode.NearMv ? 1 : 0,
                           mode == (int)Vp9InterMode.NewMv ? -1 : sb);

                hp = h.HighPrecisionMvs != 0 && Math.Abs(mv1.X) < 64 && Math.Abs(mv1.Y) < 64 ? 1 : 0;
                if (mode == (int)Vp9InterMode.NewMv || sb == -1)
                {
                    if (hp == 0)
                        RoundMvToHalfPel(ref mv1);
                }

                if (mode == (int)Vp9InterMode.NewMv)
                {
                    int j = c.GetTree(Vp9Data.MvJointTree, ctx.ActiveProb.MvJoint);
                    ctx.Counts.MvJoint[j]++;

                    if (j >= (int)Vp9MvJoint.Vertical)
                        mv1.Y = (short)(mv1.Y + ReadMvComponent(ref c, 0, hp));
                    if ((j & 1) != 0)
                        mv1.X = (short)(mv1.X + ReadMvComponent(ref c, 1, hp));
                }
            }
        }
    }

    /// <summary>Round MV to half-pel precision (remove quarter-pel).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RoundMvToHalfPel(ref Vp9Mv mv)
    {
        if ((mv.Y & 1) != 0)
            mv.Y = (short)(mv.Y < 0 ? mv.Y + 1 : mv.Y - 1);
        if ((mv.X & 1) != 0)
            mv.X = (short)(mv.X < 0 ? mv.X + 1 : mv.X - 1);
    }

    /// <summary>
    /// Compute MV clamping bounds for the current block position.
    /// Must be called before FindRefMvs.
    /// </summary>
    private void ComputeMvBounds()
    {
        ref var b = ref ctx.CurrentBlock;
        int row = ctx.CurrentRow;
        int col = ctx.CurrentCol;

        int bw4 = BlockDimension4x4[(int)b.BlockSize * 2];
        int bh4 = BlockDimension4x4[(int)b.BlockSize * 2 + 1];

        // Clamp range: ±((1<<14)-1) pixels in 1/8-pel units, adjusted for block position + size
        const int mvBorder = (16 << 3); // 16 pixels in 1/8-pel
        ctx.MinMv.X = (short)(-(col * 32 + mvBorder));
        ctx.MinMv.Y = (short)(-(row * 32 + mvBorder));
        ctx.MaxMv.X = (short)((ctx.Cols - col - bw4) * 32 + mvBorder);
        ctx.MaxMv.Y = (short)((ctx.Rows - row - bh4) * 32 + mvBorder);
    }

    // Block dimensions in 4x4 units: [blockSize*2] = width, [blockSize*2+1] = height
    private static ReadOnlySpan<byte> BlockDimension4x4 => new byte[]
    {
        // 64x64, 64x32, 32x64, 32x32, 32x16, 16x32, 16x16, 16x8, 8x16, 8x8, 8x4, 4x8, 4x4
        16, 16,  16, 8,  8, 16,  8, 8,  8, 4,  4, 8,  4, 4,  4, 2,  2, 4,  2, 2,  2, 1,  1, 2,  1, 1,
    };
}

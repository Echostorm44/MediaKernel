// VP9 probability adaptation — port of FFmpeg vp9prob.c
// After decoding each frame, adapt probabilities using collected symbol counts.

using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vp9;

public partial class Vp9Decoder
{
    /// <summary>
    /// Adapt a single probability based on observed counts.
    /// Port of FFmpeg adapt_prob().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AdaptProb(ref byte p, uint ct0, uint ct1, int maxCount, int updateFactor)
    {
        uint ct = ct0 + ct1;
        if (ct == 0) return;

        uint minCt = ct < (uint)maxCount ? ct : (uint)maxCount;
        updateFactor = (int)(updateFactor * minCt / (uint)maxCount);
        int p1 = p;
        int p2 = (int)((((long)ct0 << 8) + (ct >> 1)) / ct);
        p2 = Math.Clamp(p2, 1, 255);

        p = (byte)(p1 + (((p2 - p1) * updateFactor + 128) >> 8));
    }

    /// <summary>
    /// Adapt all probabilities after decoding a frame.
    /// Port of FFmpeg ff_vp9_adapt_probs().
    /// </summary>
    private void AdaptProbs()
    {
        ref readonly var h = ref ctx.Header;
        var p = ctx.ProbCtx[h.FrameCtxId];
        var c = ctx.Counts;
        int uf = (h.Keyframe != 0 || h.IntraOnly != 0 || ctx.LastKeyframe == 0) ? 112 : 128;

        if (EnableDiagCoefLog)
        {
            Console.WriteLine($"  ADAPT_PROBS: frameCtxId={h.FrameCtxId} uf={uf} keyframe={h.Keyframe} intraOnly={h.IntraOnly} lastKeyframe={ctx.LastKeyframe}");
            Console.Write("    PRE_ADAPT Skip: ");
            for (int i = 0; i < 3; i++) Console.Write($"{p.Skip[i]} ");
            Console.Write(" Counts: ");
            for (int i = 0; i < 6; i++) Console.Write($"{c.Skip[i]} ");
            Console.WriteLine();
            Console.Write("    PRE_ADAPT Intra: ");
            for (int i = 0; i < 4; i++) Console.Write($"{p.Intra[i]} ");
            Console.Write(" Counts: ");
            for (int i = 0; i < 8; i++) Console.Write($"{c.Intra[i]} ");
            Console.WriteLine();
            Console.Write("    PRE_ADAPT Comp: ");
            for (int i = 0; i < 5; i++) Console.Write($"{p.Comp[i]} ");
            Console.Write(" Counts: ");
            for (int i = 0; i < 10; i++) Console.Write($"{c.Comp[i]} ");
            Console.WriteLine();
            Console.Write("    PRE_ADAPT Partition[0..11]: ");
            for (int i = 0; i < 12; i++) Console.Write($"{p.Partition[i]} ");
            Console.Write(" Counts: ");
            for (int i = 0; i < 16; i++) Console.Write($"{c.Partition[i]} ");
            Console.WriteLine();

            // Dump UvMode counts (10 contexts × 10 modes)
            uint uvTotal = 0;
            for (int i = 0; i < 100; i++) uvTotal += c.UvMode[i];
            Console.Write($"    PRE_ADAPT UvMode total={uvTotal} counts: ");
            for (int i = 0; i < 100; i++)
                if (c.UvMode[i] > 0) Console.Write($"[{i/10},{i%10}]={c.UvMode[i]} ");
            Console.WriteLine();

            // Dump SingleRef counts
            uint srTotal = 0;
            for (int i = 0; i < 20; i++) srTotal += c.SingleRef[i];
            Console.Write($"    PRE_ADAPT SingleRef total={srTotal} counts: ");
            for (int i = 0; i < 20; i++)
                if (c.SingleRef[i] > 0) Console.Write($"[{i}]={c.SingleRef[i]} ");
            Console.WriteLine();

            // Dump TX counts
            uint tx8Total = 0, tx16Total = 0, tx32Total = 0;
            for (int i = 0; i < 4; i++) tx8Total += c.Tx8p[i];
            for (int i = 0; i < 6; i++) tx16Total += c.Tx16p[i];
            for (int i = 0; i < 8; i++) tx32Total += c.Tx32p[i];
            Console.Write($"    PRE_ADAPT Tx8p total={tx8Total} counts: ");
            for (int i = 0; i < 4; i++) Console.Write($"{c.Tx8p[i]} ");
            Console.Write($"  Tx16p total={tx16Total}: ");
            for (int i = 0; i < 6; i++) Console.Write($"{c.Tx16p[i]} ");
            Console.Write($"  Tx32p total={tx32Total}: ");
            for (int i = 0; i < 8; i++) Console.Write($"{c.Tx32p[i]} ");
            Console.WriteLine();
        }

        // Coefficient probabilities
        if (DiagSkipAdaptCategory != 1)
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 2; j++)
                for (int k = 0; k < 2; k++)
                    for (int l = 0; l < 6; l++)
                        for (int m = 0; m < 6; m++)
                        {
                            if (l == 0 && m >= 3) break;

                            int coefIdx = ((((i * 2 + j) * 2 + k) * 6 + l) * 6 + m) * 3;
                            int eobIdx = ((((i * 2 + j) * 2 + k) * 6 + l) * 6 + m) * 2;

                            AdaptProb(ref p.Coef[coefIdx + 0], c.Eob[eobIdx + 0], c.Eob[eobIdx + 1], 24, uf);
                            AdaptProb(ref p.Coef[coefIdx + 1], c.Coef[coefIdx + 0], c.Coef[coefIdx + 1] + c.Coef[coefIdx + 2], 24, uf);
                            AdaptProb(ref p.Coef[coefIdx + 2], c.Coef[coefIdx + 1], c.Coef[coefIdx + 2], 24, uf);
                        }

        if (h.Keyframe != 0 || h.IntraOnly != 0)
        {
            // For keyframes/intra-only, copy skip and tx probs directly
            ctx.ActiveProb.Skip.AsSpan().CopyTo(p.Skip);
            ctx.ActiveProb.Tx32p.AsSpan().CopyTo(p.Tx32p);
            ctx.ActiveProb.Tx16p.AsSpan().CopyTo(p.Tx16p);
            ctx.ActiveProb.Tx8p.AsSpan().CopyTo(p.Tx8p);
            return;
        }

        // Skip flag
        if (DiagSkipAdaptCategory != 2)
        for (int i = 0; i < 3; i++)
            AdaptProb(ref p.Skip[i], c.Skip[i * 2], c.Skip[i * 2 + 1], 20, 128);

        // Intra/inter flag
        if (DiagSkipAdaptCategory != 3)
        for (int i = 0; i < 4; i++)
            AdaptProb(ref p.Intra[i], c.Intra[i * 2], c.Intra[i * 2 + 1], 20, 128);

        // Compound prediction flag
        if (h.CompPredMode == 2 && DiagSkipAdaptCategory != 4) // SWITCHABLE
        {
            for (int i = 0; i < 5; i++)
                AdaptProb(ref p.Comp[i], c.Comp[i * 2], c.Comp[i * 2 + 1], 20, 128);
        }

        // Compound reference: adapt when NOT SINGLEREF (mode != 0)
        // FFmpeg: if (comppredmode != PRED_SINGLEREF)
        if (h.CompPredMode != 0 && DiagSkipAdaptCategory != 5)
        {
            for (int i = 0; i < 5; i++)
                AdaptProb(ref p.CompRef[i], c.CompRef[i * 2], c.CompRef[i * 2 + 1], 20, 128);
        }

        // Single reference: adapt when NOT COMPREF (mode != 1)
        // FFmpeg: if (comppredmode != PRED_COMPREF)
        if (h.CompPredMode != 1 && DiagSkipAdaptCategory != 6)
        {
            for (int i = 0; i < 5; i++)
            {
                AdaptProb(ref p.SingleRef[i * 2 + 0], c.SingleRef[i * 4 + 0], c.SingleRef[i * 4 + 1], 20, 128);
                AdaptProb(ref p.SingleRef[i * 2 + 1], c.SingleRef[i * 4 + 2], c.SingleRef[i * 4 + 3], 20, 128);
            }
        }

        // Block partitioning
        if (DiagSkipAdaptCategory != 7)
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                int pIdx = (i * 4 + j) * 3;
                int cIdx = (i * 4 + j) * 4;
                AdaptProb(ref p.Partition[pIdx + 0], c.Partition[cIdx + 0], c.Partition[cIdx + 1] + c.Partition[cIdx + 2] + c.Partition[cIdx + 3], 20, 128);
                AdaptProb(ref p.Partition[pIdx + 1], c.Partition[cIdx + 1], c.Partition[cIdx + 2] + c.Partition[cIdx + 3], 20, 128);
                AdaptProb(ref p.Partition[pIdx + 2], c.Partition[cIdx + 2], c.Partition[cIdx + 3], 20, 128);
            }

        // TX size
        if (h.TxfmMode == 4 && DiagSkipAdaptCategory != 8) // TX_SWITCHABLE
        {
            for (int i = 0; i < 2; i++)
            {
                AdaptProb(ref p.Tx8p[i], c.Tx8p[i * 2], c.Tx8p[i * 2 + 1], 20, 128);

                int c16Idx = i * 3;
                AdaptProb(ref p.Tx16p[i * 2 + 0], c.Tx16p[c16Idx + 0], c.Tx16p[c16Idx + 1] + c.Tx16p[c16Idx + 2], 20, 128);
                AdaptProb(ref p.Tx16p[i * 2 + 1], c.Tx16p[c16Idx + 1], c.Tx16p[c16Idx + 2], 20, 128);

                int c32Idx = i * 4;
                AdaptProb(ref p.Tx32p[i * 3 + 0], c.Tx32p[c32Idx + 0], c.Tx32p[c32Idx + 1] + c.Tx32p[c32Idx + 2] + c.Tx32p[c32Idx + 3], 20, 128);
                AdaptProb(ref p.Tx32p[i * 3 + 1], c.Tx32p[c32Idx + 1], c.Tx32p[c32Idx + 2] + c.Tx32p[c32Idx + 3], 20, 128);
                AdaptProb(ref p.Tx32p[i * 3 + 2], c.Tx32p[c32Idx + 2], c.Tx32p[c32Idx + 3], 20, 128);
            }
        }

        // Interpolation filter
        if (h.FilterMode == 4 && DiagSkipAdaptCategory != 9) // FILTER_SWITCHABLE (= N_FILTERS = 4)
        {
            for (int i = 0; i < 4; i++)
            {
                int pIdx = i * 2;
                int cIdx = i * 3;
                AdaptProb(ref p.Filter[pIdx + 0], c.Filter[cIdx + 0], c.Filter[cIdx + 1] + c.Filter[cIdx + 2], 20, 128);
                AdaptProb(ref p.Filter[pIdx + 1], c.Filter[cIdx + 1], c.Filter[cIdx + 2], 20, 128);
            }
        }

        // Inter modes
        if (DiagSkipAdaptCategory != 10)
        for (int i = 0; i < 7; i++)
        {
            int pIdx = i * 3;
            int cIdx = i * 4;
            AdaptProb(ref p.MvMode[pIdx + 0], c.MvMode[cIdx + 2], c.MvMode[cIdx + 1] + c.MvMode[cIdx + 0] + c.MvMode[cIdx + 3], 20, 128);
            AdaptProb(ref p.MvMode[pIdx + 1], c.MvMode[cIdx + 0], c.MvMode[cIdx + 1] + c.MvMode[cIdx + 3], 20, 128);
            AdaptProb(ref p.MvMode[pIdx + 2], c.MvMode[cIdx + 1], c.MvMode[cIdx + 3], 20, 128);
        }

        // MV joints
        if (DiagSkipAdaptCategory != 11)
        {
        AdaptProb(ref p.MvJoint[0], c.MvJoint[0], c.MvJoint[1] + c.MvJoint[2] + c.MvJoint[3], 20, 128);
        AdaptProb(ref p.MvJoint[1], c.MvJoint[1], c.MvJoint[2] + c.MvJoint[3], 20, 128);
        AdaptProb(ref p.MvJoint[2], c.MvJoint[2], c.MvJoint[3], 20, 128);
        }

        // MV components
        if (DiagSkipAdaptCategory != 12)
        for (int i = 0; i < 2; i++)
        {
            int mvOff = i * 33; // offset into MvComp for this component

            // Sign
            AdaptProb(ref p.MvComp[mvOff + 0], c.MvSign[i * 2], c.MvSign[i * 2 + 1], 20, 128);

            // Classes
            int clOff = i * 11;
            uint sum = c.MvClasses[clOff + 1] + c.MvClasses[clOff + 2] + c.MvClasses[clOff + 3] +
                       c.MvClasses[clOff + 4] + c.MvClasses[clOff + 5] + c.MvClasses[clOff + 6] +
                       c.MvClasses[clOff + 7] + c.MvClasses[clOff + 8] + c.MvClasses[clOff + 9] +
                       c.MvClasses[clOff + 10];
            AdaptProb(ref p.MvComp[mvOff + 1], c.MvClasses[clOff + 0], sum, 20, 128);
            sum -= c.MvClasses[clOff + 1];
            AdaptProb(ref p.MvComp[mvOff + 2], c.MvClasses[clOff + 1], sum, 20, 128);
            sum -= c.MvClasses[clOff + 2] + c.MvClasses[clOff + 3];
            AdaptProb(ref p.MvComp[mvOff + 3], c.MvClasses[clOff + 2] + c.MvClasses[clOff + 3], sum, 20, 128);
            AdaptProb(ref p.MvComp[mvOff + 4], c.MvClasses[clOff + 2], c.MvClasses[clOff + 3], 20, 128);
            sum -= c.MvClasses[clOff + 4] + c.MvClasses[clOff + 5];
            AdaptProb(ref p.MvComp[mvOff + 5], c.MvClasses[clOff + 4] + c.MvClasses[clOff + 5], sum, 20, 128);
            AdaptProb(ref p.MvComp[mvOff + 6], c.MvClasses[clOff + 4], c.MvClasses[clOff + 5], 20, 128);
            sum -= c.MvClasses[clOff + 6];
            AdaptProb(ref p.MvComp[mvOff + 7], c.MvClasses[clOff + 6], sum, 20, 128);
            AdaptProb(ref p.MvComp[mvOff + 8], c.MvClasses[clOff + 7] + c.MvClasses[clOff + 8],
                      c.MvClasses[clOff + 9] + c.MvClasses[clOff + 10], 20, 128);
            AdaptProb(ref p.MvComp[mvOff + 9], c.MvClasses[clOff + 7], c.MvClasses[clOff + 8], 20, 128);
            AdaptProb(ref p.MvComp[mvOff + 10], c.MvClasses[clOff + 9], c.MvClasses[clOff + 10], 20, 128);

            // Class0
            AdaptProb(ref p.MvComp[mvOff + 11], c.MvClass0[i * 2], c.MvClass0[i * 2 + 1], 20, 128);

            // Bits
            int bitsCountOff = i * 20; // MvBits is [2][10][2]
            for (int j = 0; j < 10; j++)
                AdaptProb(ref p.MvComp[mvOff + 12 + j], c.MvBits[bitsCountOff + j * 2], c.MvBits[bitsCountOff + j * 2 + 1], 20, 128);

            // Class0 FP
            int c0fpCountOff = i * 8; // MvClass0Fp is [2][2][4]
            for (int j = 0; j < 2; j++)
            {
                int fpPIdx = mvOff + 22 + j * 3;
                int fpCIdx = c0fpCountOff + j * 4;
                AdaptProb(ref p.MvComp[fpPIdx + 0], c.MvClass0Fp[fpCIdx + 0], c.MvClass0Fp[fpCIdx + 1] + c.MvClass0Fp[fpCIdx + 2] + c.MvClass0Fp[fpCIdx + 3], 20, 128);
                AdaptProb(ref p.MvComp[fpPIdx + 1], c.MvClass0Fp[fpCIdx + 1], c.MvClass0Fp[fpCIdx + 2] + c.MvClass0Fp[fpCIdx + 3], 20, 128);
                AdaptProb(ref p.MvComp[fpPIdx + 2], c.MvClass0Fp[fpCIdx + 2], c.MvClass0Fp[fpCIdx + 3], 20, 128);
            }

            // FP
            int fpCountOff = i * 4; // MvFp is [2][4]
            AdaptProb(ref p.MvComp[mvOff + 28], c.MvFp[fpCountOff + 0], c.MvFp[fpCountOff + 1] + c.MvFp[fpCountOff + 2] + c.MvFp[fpCountOff + 3], 20, 128);
            AdaptProb(ref p.MvComp[mvOff + 29], c.MvFp[fpCountOff + 1], c.MvFp[fpCountOff + 2] + c.MvFp[fpCountOff + 3], 20, 128);
            AdaptProb(ref p.MvComp[mvOff + 30], c.MvFp[fpCountOff + 2], c.MvFp[fpCountOff + 3], 20, 128);

            // Class0 HP and HP
            if (h.HighPrecisionMvs != 0)
            {
                AdaptProb(ref p.MvComp[mvOff + 31], c.MvClass0Hp[i * 2], c.MvClass0Hp[i * 2 + 1], 20, 128);
                AdaptProb(ref p.MvComp[mvOff + 32], c.MvHp[i * 2], c.MvHp[i * 2 + 1], 20, 128);
            }
        }

        // Y intra modes
        if (DiagSkipAdaptCategory != 13)
        for (int i = 0; i < 4; i++)
        {
            int pIdx = i * 9;
            int cIdx = i * 10;

            uint sum2;
            // Count indices: VERT=0, HOR=1, DC=2, DDL=3, DDR=4, VR=5, HD=6, VL=7, HU=8, TM=9
            uint ySum = c.YMode[cIdx + 0] + c.YMode[cIdx + 1] + c.YMode[cIdx + 3] +
                        c.YMode[cIdx + 4] + c.YMode[cIdx + 5] + c.YMode[cIdx + 6] +
                        c.YMode[cIdx + 7] + c.YMode[cIdx + 8] + c.YMode[cIdx + 9];
            AdaptProb(ref p.YMode[pIdx + 0], c.YMode[cIdx + 2], ySum, 20, 128); // DC
            ySum -= c.YMode[cIdx + 9]; // -= TM
            AdaptProb(ref p.YMode[pIdx + 1], c.YMode[cIdx + 9], ySum, 20, 128); // TM
            ySum -= c.YMode[cIdx + 0]; // -= VERT
            AdaptProb(ref p.YMode[pIdx + 2], c.YMode[cIdx + 0], ySum, 20, 128); // VERT
            sum2 = c.YMode[cIdx + 1] + c.YMode[cIdx + 4] + c.YMode[cIdx + 5]; // HOR+DDR+VR
            ySum -= sum2;
            AdaptProb(ref p.YMode[pIdx + 3], sum2, ySum, 20, 128);
            sum2 -= c.YMode[cIdx + 1]; // -= HOR
            AdaptProb(ref p.YMode[pIdx + 4], c.YMode[cIdx + 1], sum2, 20, 128); // HOR
            AdaptProb(ref p.YMode[pIdx + 5], c.YMode[cIdx + 4], c.YMode[cIdx + 5], 20, 128); // DDR vs VR
            ySum -= c.YMode[cIdx + 3]; // -= DDL
            AdaptProb(ref p.YMode[pIdx + 6], c.YMode[cIdx + 3], ySum, 20, 128); // DDL
            ySum -= c.YMode[cIdx + 7]; // -= VL
            AdaptProb(ref p.YMode[pIdx + 7], c.YMode[cIdx + 7], ySum, 20, 128); // VL
            AdaptProb(ref p.YMode[pIdx + 8], c.YMode[cIdx + 6], c.YMode[cIdx + 8], 20, 128); // HD vs HU
        }

        // UV intra modes
        if (DiagSkipAdaptCategory != 14)
        for (int i = 0; i < 10; i++)
        {
            int pIdx = i * 9;
            int cIdx = i * 10;

            uint sum2;
            // Count indices: VERT=0, HOR=1, DC=2, DDL=3, DDR=4, VR=5, HD=6, VL=7, HU=8, TM=9
            uint uvSum = c.UvMode[cIdx + 0] + c.UvMode[cIdx + 1] + c.UvMode[cIdx + 3] +
                         c.UvMode[cIdx + 4] + c.UvMode[cIdx + 5] + c.UvMode[cIdx + 6] +
                         c.UvMode[cIdx + 7] + c.UvMode[cIdx + 8] + c.UvMode[cIdx + 9];
            AdaptProb(ref p.UvMode[pIdx + 0], c.UvMode[cIdx + 2], uvSum, 20, 128); // DC
            uvSum -= c.UvMode[cIdx + 9]; // -= TM
            AdaptProb(ref p.UvMode[pIdx + 1], c.UvMode[cIdx + 9], uvSum, 20, 128); // TM
            uvSum -= c.UvMode[cIdx + 0]; // -= VERT
            AdaptProb(ref p.UvMode[pIdx + 2], c.UvMode[cIdx + 0], uvSum, 20, 128); // VERT
            sum2 = c.UvMode[cIdx + 1] + c.UvMode[cIdx + 4] + c.UvMode[cIdx + 5]; // HOR+DDR+VR
            uvSum -= sum2;
            AdaptProb(ref p.UvMode[pIdx + 3], sum2, uvSum, 20, 128);
            sum2 -= c.UvMode[cIdx + 1]; // -= HOR
            AdaptProb(ref p.UvMode[pIdx + 4], c.UvMode[cIdx + 1], sum2, 20, 128); // HOR
            AdaptProb(ref p.UvMode[pIdx + 5], c.UvMode[cIdx + 4], c.UvMode[cIdx + 5], 20, 128); // DDR vs VR
            uvSum -= c.UvMode[cIdx + 3]; // -= DDL
            AdaptProb(ref p.UvMode[pIdx + 6], c.UvMode[cIdx + 3], uvSum, 20, 128); // DDL
            uvSum -= c.UvMode[cIdx + 7]; // -= VL
            AdaptProb(ref p.UvMode[pIdx + 7], c.UvMode[cIdx + 7], uvSum, 20, 128); // VL
            AdaptProb(ref p.UvMode[pIdx + 8], c.UvMode[cIdx + 6], c.UvMode[cIdx + 8], 20, 128); // HD vs HU
        }

        if (EnableDiagCoefLog)
        {
            Console.Write("    POST_ADAPT Skip: ");
            for (int i = 0; i < 3; i++) Console.Write($"{p.Skip[i]} ");
            Console.Write(" Intra: ");
            for (int i = 0; i < 4; i++) Console.Write($"{p.Intra[i]} ");
            Console.Write(" Partition[0..11]: ");
            for (int i = 0; i < 12; i++) Console.Write($"{p.Partition[i]} ");
            Console.WriteLine();
            Console.Write("    POST_ADAPT MvMode[0..20]: ");
            for (int i = 0; i < 21; i++) Console.Write($"{p.MvMode[i]} ");
            Console.Write(" MvJoint: ");
            for (int i = 0; i < 3; i++) Console.Write($"{p.MvJoint[i]} ");
            Console.WriteLine();

            // UvMode adapted probs for all 10 contexts
            Console.Write("    POST_ADAPT UvMode ctx2: ");
            for (int i = 0; i < 9; i++) Console.Write($"{p.UvMode[2 * 9 + i]} ");
            Console.Write("  ctx9: ");
            for (int i = 0; i < 9; i++) Console.Write($"{p.UvMode[9 * 9 + i]} ");
            Console.WriteLine();

            // SingleRef adapted probs
            Console.Write("    POST_ADAPT SingleRef: ");
            for (int i = 0; i < 10; i++) Console.Write($"{p.SingleRef[i]} ");
            Console.WriteLine();
        }
    }
}

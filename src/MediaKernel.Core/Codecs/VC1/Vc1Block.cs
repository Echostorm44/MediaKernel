// VC-1's block layer - a translation of ffmpeg-master/libavcodec/vc1_block.c.
//
// Translated so far: the tables, the block index helpers, the two macros that read a
// macroblock quantiser and a motion vector differential, the interlaced vector reader, the two
// DC predictors, the coded-block predictor, the AC coefficient reader, the four block
// decoders, the two helpers the macroblock decoders share, and the three P macroblock
// decoders. The three B macroblock decoders and the picture loops are still to come; see
// the port checklist's vc1 section.
//
// GET_MQUANT and GET_MVDATA are macros in the C because they write several of the caller's
// locals at once. They are methods here that return those values, which is the one shape
// change in this file; the arithmetic is unchanged, and the registry records it.

using System;
using MediaKernel.Core.Codecs.MpegVideo;
using Mpeg2 = MediaKernel.Core.Codecs.Mpeg2;

namespace MediaKernel.Core.Codecs.Vc1Video;

/// <summary>
/// FFmpeg's <c>vc1_block.c</c>: the block and macroblock layer, from the quantiser and vector
/// differentials a macroblock header carries down to one AC coefficient at a time.
/// </summary>
public static class Vc1Block
{
    /// <summary><c>offset_table[2][9]</c> - the vector differential's base by size class.</summary>
    public static ReadOnlySpan<byte> OffsetTable =>
    [
        0, 1, 2, 4, 8, 16, 32, 64, 128,
        0, 1, 3, 7, 15, 31, 63, 127, 255,
    ];

    /// <summary><c>block_map[6]</c> - mapping to the internal block representation.</summary>
    public static ReadOnlySpan<int> BlockMap => [0, 2, 1, 3, 4, 5];

    /// <summary><c>size_table[6]</c> - bits read for a vector differential, by size class.</summary>
    public static ReadOnlySpan<byte> SizeTable => [0, 2, 3, 4, 5, 8];

    /// <summary>
    /// <c>init_block_index</c> - positions the destination pointers for this macroblock row,
    /// and steps them on by a line for the second field of a bottom-field-first picture.
    /// </summary>
    public static void InitBlockIndex(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        MpegVideoDec.InitBlockIndex(ref s);
        if (v.FieldMode != 0 && (v.SecondField ^ v.Tff) == 0)
        {
            ref MPVPicture pic = ref s.BufferPools.Pool!.Picture(s.CurPic.Ptr);
            s.Dest[0] += pic.Linesize[0];
            s.Dest[1] += pic.Linesize[1];
            s.Dest[2] += pic.Linesize[2];
        }
    }

    /// <summary>
    /// <c>update_block_index</c> - steps the destination pointers on by one macroblock.
    /// </summary>
    /// <remarks>
    /// VC-1 is always 4:2:0 except under <c>AV_CODEC_FLAG_GRAY</c> or a hardware accelerator,
    /// neither of which this port has, so the block size is always 8.
    /// </remarks>
    public static void UpdateBlockIndex(ref MpegEncContext s) =>
        MpegVideoDec.UpdateBlockIndex(ref s, 8, s.ChromaXShift);

    /// <summary><c>inc_blk_idx</c> - steps a block group index, wrapping at the end.</summary>
    public static int IncBlkIdx(Vc1Context v, int idx)
    {
        idx++;
        if (idx >= v.NAllocatedBlks)
            idx = 0;
        return idx;
    }

    /// <summary>
    /// <c>GET_MQUANT</c> - the macroblock quantiser, which a picture may vary per macroblock
    /// or per picture edge.
    /// </summary>
    /// <remarks>
    /// A macro in the C because it assigns the caller's <c>mquant</c> in place and leaves it
    /// alone when <c>dquantfrm</c> is clear. Here the caller passes the value in and takes the
    /// result back, which is the same thing written as a function.
    /// <para>
    /// A negative result means the alternate quantiser applies; the callers read the sign.
    /// </para>
    /// </remarks>
    public static int GetMquant(Vc1Context v, ref GetBitContext gb, int mquant)
    {
        ref MpegEncContext s = ref v.S;

        if (v.Dquantfrm != 0)
        {
            int edges = 0;
            if (v.Dqprofile == (int)DqProfile.AllMbs)
            {
                if (v.Dqbilevel != 0)
                {
                    mquant = gb.GetBits1() != 0 ? -v.Altpq : v.Pq;
                }
                else
                {
                    int mqdiff = (int)gb.GetBits(3);
                    if (mqdiff != 7)
                        mquant = -v.Pq - mqdiff;
                    else
                        mquant = -(int)gb.GetBits(5);
                }
            }
            if (v.Dqprofile == (int)DqProfile.SingleEdge)
                edges = 1 << v.Dqsbedge;
            else if (v.Dqprofile == (int)DqProfile.DoubleEdges)
                edges = (3 << v.Dqsbedge) % 15;
            else if (v.Dqprofile == (int)DqProfile.FourEdges)
                edges = 15;

            if ((edges & 1) != 0 && s.MbX == 0)
                mquant = -v.Altpq;
            if ((edges & 2) != 0 && s.MbY == 0)
                mquant = -v.Altpq;
            if ((edges & 4) != 0 && s.MbX == s.MbWidth - 1)
                mquant = -v.Altpq;
            if ((edges & 8) != 0 && s.MbY == (s.MbHeight >> v.FieldMode) - 1)
                mquant = -v.Altpq;

            if (mquant == 0 || mquant > 31 || mquant < -31)
            {
                // FFmpeg logs "Overriding invalid mquant %d" and carries on with 1.
                mquant = 1;
            }
        }

        return mquant;
    }

    /// <summary>The four values <c>GET_MVDATA</c> assigns into its caller's locals.</summary>
    public struct MvData
    {
        /// <summary>Horizontal differential for the decoded motion vector.</summary>
        public int DmvX;

        /// <summary>Vertical differential for the decoded motion vector.</summary>
        public int DmvY;

        /// <summary>Whether the macroblock carries coefficients as well as a vector.</summary>
        public int MbHasCoeffs;

        /// <summary>Whether the macroblock turned out to be intra coded.</summary>
        public int MbIntra;
    }

    /// <summary>
    /// <c>GET_MVDATA</c> - a motion vector differential, and what the escape codes around it
    /// say about the macroblock.
    /// </summary>
    /// <remarks>
    /// One VLC code carries three things at once: the two differentials' size classes, whether
    /// the macroblock has coefficients, and whether it is intra. Index 35 is an escape to fixed
    /// width fields, 36 means intra, and everything else splits as <c>index % 6</c> and
    /// <c>index / 6</c> into a size class per axis.
    /// </remarks>
    public static MvData GetMvdata(Vc1Context v, ref GetBitContext gb)
    {
        ref MpegEncContext s = ref v.S;
        var r = default(MvData);

        int index = 1 + gb.GetVlc2(Vc1Data.Vc1MvDiffVlc[v.MvTableIndex],
                                   Vc1Data.MvDiffVlcBits, 2);
        if (index > 36)
        {
            r.MbHasCoeffs = 1;
            index -= 37;
        }
        else
        {
            r.MbHasCoeffs = 0;
        }
        r.MbIntra = 0;

        if (index == 0)
        {
            r.DmvX = r.DmvY = 0;
        }
        else if (index == 35)
        {
            r.DmvX = (int)gb.GetBits(v.KX - 1 + s.QuarterSample);
            r.DmvY = (int)gb.GetBits(v.KY - 1 + s.QuarterSample);
        }
        else if (index == 36)
        {
            r.DmvX = 0;
            r.DmvY = 0;
            r.MbIntra = 1;
        }
        else
        {
            int index1 = index % 6;
            r.DmvX = OffsetTable[9 + index1];
            int val = SizeTable[index1] - (s.QuarterSample == 0 && index1 == 5 ? 1 : 0);
            if (val > 0)
            {
                val = (int)gb.GetBits(val);
                int sign = 0 - (val & 1);
                r.DmvX = (sign ^ ((val >> 1) + r.DmvX)) - sign;
            }

            index1 = index / 6;
            r.DmvY = OffsetTable[9 + index1];
            val = SizeTable[index1] - (s.QuarterSample == 0 && index1 == 5 ? 1 : 0);
            if (val > 0)
            {
                val = (int)gb.GetBits(val);
                int sign = 0 - (val & 1);
                r.DmvY = (sign ^ ((val >> 1) + r.DmvY)) - sign;
            }
        }

        return r;
    }

    /// <summary>
    /// <c>get_mvdata_interlaced</c> - the same for an interlaced picture, where the code also
    /// carries which of two reference fields the vector points into.
    /// </summary>
    public static void GetMvdataInterlaced(Vc1Context v, ref GetBitContext gb,
                                           out int dmvX, out int dmvY, ref int predFlag,
                                           bool hasPredFlag)
    {
        int bits, esc;
        if (v.Numref != 0)
        {
            bits = Vc1Data.TwoRefMvdataVlcBits;
            esc = 125;
        }
        else
        {
            bits = Vc1Data.OneRefMvdataVlcBits;
            esc = 71;
        }

        int extendX = v.Dmvrange & 1;
        int extendY = (v.Dmvrange >> 1) & 1;
        int index = gb.GetVlc2(v.ImvVlc!, bits, 3);

        if (index == esc)
        {
            dmvX = (int)gb.GetBits(v.KX);
            dmvY = (int)gb.GetBits(v.KY);
            if (v.Numref != 0)
            {
                if (hasPredFlag)
                    predFlag = dmvY & 1;
                dmvY = (dmvY + (dmvY & 1)) >> 1;
            }
        }
        else
        {
            int index1 = (index + 1) % 9;
            if (index1 != 0)
            {
                int val = (int)gb.GetBits(index1 + extendX);
                int sign = 0 - (val & 1);
                dmvX = (sign ^ ((val >> 1) + OffsetTable[extendX * 9 + index1])) - sign;
            }
            else
            {
                dmvX = 0;
            }

            index1 = (index + 1) / 9;
            if (index1 > v.Numref)
            {
                int val = (int)gb.GetBits((index1 >> v.Numref) + extendY);
                int sign = 0 - (val & 1);
                dmvY = (sign ^ ((val >> 1)
                        + OffsetTable[extendY * 9 + (index1 >> v.Numref)])) - sign;
            }
            else
            {
                dmvY = 0;
            }

            if (v.Numref != 0 && hasPredFlag)
                predFlag = index1 & 1;
        }
    }

    /// <summary><c>dcpred[32]</c> - the DC value an unavailable neighbour stands in with.</summary>
    private static ReadOnlySpan<ushort> DcPred =>
    [
        0xFFFF, 1024, 512, 341, 256, 205, 171, 146, 128,
                 114, 102,  93,  85,  79,  73,  68,  64,
                  60,  57,  54,  51,  49,  47,  45,  43,
                  41,  39,  38,  37,  35,  34,  33,
    ];

    /// <summary>
    /// <c>vc1_i_pred_dc</c> - the DC predictor for an I picture, which has no quantiser
    /// rescaling to do because every macroblock shares one.
    /// </summary>
    /// <returns>The predicted DC; <paramref name="dirPtr"/> is 1 for left, 0 for top.</returns>
    public static int Vc1IPredDc(ref MpegEncContext s, int overlap, int pq, int n,
                                 out int dcValIndex, out int dirPtr)
    {
        // wmv3_dc_scale always used here in fact
        int scale = s.YDcScale;

        int wrap = s.BlockWrap[n];
        short[] dcValBase = s.DcValBase!;
        int dcVal = s.DcValOffset + s.BlockIndex[n];

        //  B A
        //  C X
        int c = dcValBase[dcVal - 1];
        int b = dcValBase[dcVal - 1 - wrap];
        int a = dcValBase[dcVal - wrap];

        if (pq < 9 || overlap == 0)
        {
            // Set outer values
            if (s.FirstSliceLine != 0 && n != 2 && n != 3)
                b = a = DcPred[scale];
            if (s.MbX == 0 && n != 1 && n != 3)
                b = c = DcPred[scale];
        }
        else
        {
            // Set outer values
            if (s.FirstSliceLine != 0 && n != 2 && n != 3)
                b = a = 0;
            if (s.MbX == 0 && n != 1 && n != 3)
                b = c = 0;
        }

        int pred;
        if (Math.Abs(a - b) <= Math.Abs(b - c))
        {
            pred = c;
            dirPtr = 1;   // left
        }
        else
        {
            pred = a;
            dirPtr = 0;   // top
        }

        // update predictor
        dcValIndex = dcVal;
        return pred;
    }

    /// <summary>
    /// <c>ff_vc1_pred_dc</c> - the DC predictor for every other picture type, where each
    /// neighbour may have been quantised differently and is rescaled before being compared.
    /// </summary>
    /// <returns>The predicted DC; <paramref name="dirPtr"/> is 1 for left, 0 for top.</returns>
    public static int Vc1PredDc(ref MpegEncContext s, int overlap, int pq, int n,
                                int aAvail, int cAvail,
                                out int dcValIndex, out int dirPtr)
    {
        int mbPos = s.MbX + s.MbY * s.MbStride;
        sbyte[] qscale = s.CurPic.QscaleTable!;
        int qt = s.CurPic.QscaleTableOffset;

        // scale predictors if needed
        int q1 = Math.Abs(qscale[qt + mbPos]);
        int dqscaleIndex = Vc1Data.Wmv3DcScaleTable[q1] - 1;
        if (dqscaleIndex < 0)
        {
            dcValIndex = 0;
            dirPtr = 0;
            return 0;
        }

        int wrap = s.BlockWrap[n];
        short[] dcValBase = s.DcValBase!;
        int dcVal = s.DcValOffset + s.BlockIndex[n];

        //  B A
        //  C X
        int c = dcValBase[dcVal - 1];
        int b = dcValBase[dcVal - 1 - wrap];
        int a = dcValBase[dcVal - wrap];

        int q2;
        if (cAvail != 0 && n != 1 && n != 3)
        {
            q2 = Math.Abs(qscale[qt + mbPos - 1]);
            if (q2 != 0 && q2 != q1)
                c = (int)((uint)c * Vc1Data.Wmv3DcScaleTable[q2]
                          * Vc1Data.Vc1Dqscale[dqscaleIndex] + 0x20000) >> 18;
        }
        if (aAvail != 0 && n != 2 && n != 3)
        {
            q2 = Math.Abs(qscale[qt + mbPos - s.MbStride]);
            if (q2 != 0 && q2 != q1)
                a = (int)((uint)a * Vc1Data.Wmv3DcScaleTable[q2]
                          * Vc1Data.Vc1Dqscale[dqscaleIndex] + 0x20000) >> 18;
        }
        if (aAvail != 0 && cAvail != 0 && n != 3)
        {
            int off = mbPos;
            if (n != 1)
                off--;
            if (n != 2)
                off -= s.MbStride;
            q2 = Math.Abs(qscale[qt + off]);
            if (q2 != 0 && q2 != q1)
                b = (int)((uint)b * Vc1Data.Wmv3DcScaleTable[q2]
                          * Vc1Data.Vc1Dqscale[dqscaleIndex] + 0x20000) >> 18;
        }

        int pred;
        if (cAvail != 0 && (aAvail == 0 || Math.Abs(a - b) <= Math.Abs(b - c)))
        {
            pred = c;
            dirPtr = 1;   // left
        }
        else if (aAvail != 0)
        {
            pred = a;
            dirPtr = 0;   // top
        }
        else
        {
            pred = 0;
            dirPtr = 1;   // left
        }

        // update predictor
        dcValIndex = dcVal;
        return pred;
    }

    /// <summary>
    /// <c>vc1_coded_block_pred</c> - predicts whether a block is coded from its three
    /// neighbours, and stores the result for the blocks that will predict from this one.
    /// </summary>
    public static int Vc1CodedBlockPred(ref MpegEncContext s, int n, int diff)
    {
        int xy = s.BlockIndex[n];
        int wrap = s.B8Stride;
        byte[] coded = s.CodedBlockBase!;
        int cb = s.CodedBlockOffset;

        //  B C
        //  A X
        int a = coded[cb + xy - 1];
        int b = coded[cb + xy - 1 - wrap];
        int c = coded[cb + xy - wrap];

        int pred = b == c ? a : c;

        // store value
        coded[cb + xy] = (byte)(pred ^ diff);

        return pred ^ diff;
    }

    /// <summary>
    /// <c>vc1_index_decode_table[codingset][index][which]</c>, which the port holds flattened
    /// from [8][185][2].
    /// </summary>
    private static int IndexDecode(int codingset, int index, int which) =>
        Vc1AcData.Vc1IndexDecodeTable[(codingset * 185 + index) * 2 + which];

    /// <summary>One AC coefficient, as <c>vc1_decode_ac_coeff</c> reports it.</summary>
    public struct AcCoeff
    {
        /// <summary>Whether this is the last coefficient of the block.</summary>
        public int Last;

        /// <summary>How many zero coefficients to skip before this one.</summary>
        public int Skip;

        /// <summary>The signed coefficient.</summary>
        public int Value;
    }

    /// <summary>
    /// <c>vc1_decode_ac_coeff</c> - one AC coefficient, as a run of zeroes, a level and a
    /// last-coefficient flag.
    /// </summary>
    /// <remarks>
    /// Three layers of escape. The common case is one VLC code into a run/level pair. The first
    /// escape re-reads the code and adds a per-coding-set delta to either the level or the run.
    /// The second reads run and level as fixed width fields, whose widths are themselves read
    /// once per picture and cached in <c>esc3_level_length</c> and <c>esc3_run_length</c> -
    /// state that outlives the call, which is why this takes the context and not just the
    /// reader.
    /// </remarks>
    public static int Vc1DecodeAcCoeff(Vc1Context v, ref GetBitContext gb,
                                       out AcCoeff coeff, int codingset)
    {
        coeff = default;
        int run, level, lst, sign;

        int index = gb.GetVlc2(Vc1Data.Vc1AcCoeffTable[codingset], Vc1Data.AcVlcBits, 3);
        if (index < 0)
            return index;

        if (index != Vc1VlcData.Vc1AcSizes[codingset] - 1)
        {
            run = IndexDecode(codingset, index, 0);
            level = IndexDecode(codingset, index, 1);
            lst = index >= Vc1AcData.Vc1LastDecodeTable[codingset]
                  || gb.GetBitsLeft() < 0 ? 1 : 0;
            sign = (int)gb.GetBits1();
        }
        else
        {
            int escape = gb.Decode210();
            if (escape != 2)
            {
                index = gb.GetVlc2(Vc1Data.Vc1AcCoeffTable[codingset], Vc1Data.AcVlcBits, 3);
                if ((uint)index >= (uint)(Vc1VlcData.Vc1AcSizes[codingset] - 1))
                    return Vc1.AverrorInvalidData;
                run = IndexDecode(codingset, index, 0);
                level = IndexDecode(codingset, index, 1);
                lst = index >= Vc1AcData.Vc1LastDecodeTable[codingset] ? 1 : 0;
                if (escape == 0)
                {
                    if (lst != 0)
                        level += Vc1AcData.Vc1LastDeltaLevelTable[codingset * 44 + run];
                    else
                        level += Vc1AcData.Vc1DeltaLevelTable[codingset * 31 + run];
                }
                else
                {
                    if (lst != 0)
                        run += Vc1AcData.Vc1LastDeltaRunTable[codingset * 10 + level] + 1;
                    else
                        run += Vc1AcData.Vc1DeltaRunTable[codingset * 57 + level] + 1;
                }
                sign = (int)gb.GetBits1();
            }
            else
            {
                lst = (int)gb.GetBits1();
                if (v.Esc3LevelLength == 0)
                {
                    if (v.Pq < 8 || v.Dquantfrm != 0)
                    {
                        // table 59
                        v.Esc3LevelLength = (int)gb.GetBits(3);
                        if (v.Esc3LevelLength == 0)
                            v.Esc3LevelLength = (int)gb.GetBits(2) + 8;
                    }
                    else
                    {
                        // table 60
                        v.Esc3LevelLength = gb.GetUnary(1, 6) + 2;
                    }
                    v.Esc3RunLength = 3 + (int)gb.GetBits(2);
                }
                run = (int)gb.GetBits(v.Esc3RunLength);
                sign = (int)gb.GetBits1();
                level = (int)gb.GetBits(v.Esc3LevelLength);
            }
        }

        coeff.Last = lst;
        coeff.Skip = run;
        coeff.Value = (level ^ -sign) + sign;

        return 0;
    }
    // --- The block decoders ---------------------------------------------------------------
    //
    // Four of them, because the same block means four different things depending on the
    // picture: an I picture's intra block, an advanced profile I picture's, an intra block
    // inside an inter picture, and an inter picture's residual. They share a shape - read the
    // DC differential, predict it, read AC coefficients until the last one, apply AC
    // prediction from the neighbour the DC came from, then scale - and differ in which
    // neighbour is available, whether the neighbour needs rescaling for a different
    // quantiser, and which zigzag the coefficients are read through.
    //
    // The block is a span the caller has already sliced, because the C passes
    // v->block[idx][n]; dc_val and ac_val are indices into the context's allocations, because
    // the C's are interior pointers that step backwards by a block row.

    /// <summary>
    /// Reads one DC differential, which the four decoders all start with.
    /// </summary>
    /// <remarks>
    /// Index 119 escapes to a fixed width field. Otherwise, at the two coarsest quantisers,
    /// the index carries only the top bits and <c>m</c> more follow.
    /// </remarks>
    private static int ReadDcDifferential(Vc1Context v, ref GetBitContext gb, int n, int quant)
    {
        // Msmp4DcVlc is [2][2] flattened, luma and chroma for each of the two tables.
        int dcdiff = gb.GetVlc2(
            MsMpeg4Vc1Data.Msmp4DcVlc[v.DcTableIndex * 2 + (n >= 4 ? 1 : 0)],
            MsMpeg4Vc1Data.DcVlcBits, 3);
        if (dcdiff != 0)
        {
            int m = (quant == 1 || quant == 2) ? 3 - quant : 0;
            if (dcdiff == 119)
            {
                // ESC index value
                dcdiff = (int)gb.GetBits(8 + m);
            }
            else if (m != 0)
            {
                dcdiff = (dcdiff << m) + (int)gb.GetBits(m) - ((1 << m) - 1);
            }
            if (gb.GetBits1() != 0)
                dcdiff = -dcdiff;
        }
        return dcdiff;
    }

    /// <summary>
    /// The quantiser a predictor from a neighbour is rescaled through, as the three advanced
    /// decoders compute it: doubled, biased by HALFPQ when the quantiser is not the
    /// alternate one, and one less.
    /// </summary>
    private static int PredictorQuant(int q, int halfpq) =>
        Math.Abs(q) * 2 + (q < 0 ? 0 : halfpq) - 1;

    /// <summary>
    /// <c>vc1_decode_i_block</c> - an intra block of a simple or main profile I picture,
    /// where every macroblock shares one quantiser so no predictor ever needs rescaling.
    /// </summary>
    public static int Vc1DecodeIBlock(Vc1Context v, Span<short> block, int n,
                                      int coded, int codingset)
    {
        ref MpegEncContext s = ref v.S;
        ref GetBitContext gb = ref v.Gb;

        int dcdiff = ReadDcDifferential(v, ref gb, n, v.Pq);

        // Prediction
        dcdiff += Vc1IPredDc(ref s, v.Overlap, v.Pq, n, out int dcValIndex, out int dcPredDir);
        s.DcValBase![dcValIndex] = (short)dcdiff;

        // Store the quantized DC coeff, used for prediction
        block[0] = (short)(dcdiff * s.YDcScale);

        short[] acBase = s.AcValBase!;
        int acVal2 = s.AcValOffset + s.BlockIndex[n] * 16;
        int acVal = acVal2;
        if (dcPredDir != 0)        // left
            acVal -= 16;
        else                       // top
            acVal -= 16 * s.BlockWrap[n];

        int scale = v.Pq * 2 + v.Halfpq;

        // AC Decoding
        if (coded != 0)
        {
            int last = 0;
            ReadOnlySpan<byte> zzTable;

            if (s.AcPred != 0)
                zzTable = dcPredDir == 0 ? v.Zz8x8.AsSpan(2 * 64, 64) : v.Zz8x8.AsSpan(3 * 64, 64);
            else
                zzTable = v.Zz8x8.AsSpan(1 * 64, 64);

            for (int i = 1; last == 0; ++i)
            {
                int ret = Vc1DecodeAcCoeff(v, ref gb, out AcCoeff c, codingset);
                if (ret < 0)
                    return ret;
                last = c.Last;
                i += c.Skip;
                if (i > 63)
                    break;
                block[zzTable[i]] = (short)c.Value;
            }

            // apply AC prediction if needed
            if (s.AcPred != 0)
            {
                int sh;
                if (dcPredDir != 0)
                {
                    sh = v.LeftBlkSh;
                }
                else
                {
                    sh = v.TopBlkSh;
                    acVal += 8;
                }
                for (int k = 1; k < 8; k++)
                    block[k << sh] += acBase[acVal + k];
            }

            // save AC coeffs for further prediction
            for (int k = 1; k < 8; k++)
            {
                acBase[acVal2 + k] = block[k << v.LeftBlkSh];
                acBase[acVal2 + k + 8] = block[k << v.TopBlkSh];
            }

            // scale AC coeffs
            for (int k = 1; k < 64; k++)
                if (block[k] != 0)
                {
                    block[k] = (short)(block[k] * scale);
                    if (v.Pquantizer == 0)
                        block[k] += (short)(block[k] < 0 ? -v.Pq : v.Pq);
                }
        }
        else
        {
            acBase.AsSpan(acVal2, 16).Clear();

            // apply AC prediction if needed
            if (s.AcPred != 0)
            {
                int sh;
                if (dcPredDir != 0)
                {
                    sh = v.LeftBlkSh;
                }
                else
                {
                    sh = v.TopBlkSh;
                    acVal += 8;
                    acVal2 += 8;
                }
                acBase.AsSpan(acVal, 8).CopyTo(acBase.AsSpan(acVal2, 8));
                for (int k = 1; k < 8; k++)
                {
                    block[k << sh] = (short)(acBase[acVal + k] * scale);
                    if (v.Pquantizer == 0 && block[k << sh] != 0)
                        block[k << sh] += (short)(block[k << sh] < 0 ? -v.Pq : v.Pq);
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// <c>vc1_decode_i_block_adv</c> - an intra block of an advanced profile I picture, where
    /// the macroblock's quantiser can differ from its neighbours' and the predictor is
    /// rescaled when it does.
    /// </summary>
    public static int Vc1DecodeIBlockAdv(Vc1Context v, Span<short> block, int n,
                                         int coded, int codingset, int mquant)
    {
        ref MpegEncContext s = ref v.S;
        ref GetBitContext gb = ref v.Gb;
        int aAvail = v.AAvail, cAvail = v.CAvail;
        int usePred = s.AcPred;
        int mbPos = s.MbX + s.MbY * s.MbStride;
        int quant = Math.Abs(mquant);

        int dcdiff = ReadDcDifferential(v, ref gb, n, quant);

        // Prediction
        dcdiff += Vc1PredDc(ref s, v.Overlap, quant, n, v.AAvail, v.CAvail,
                            out int dcValIndex, out int dcPredDir);
        s.DcValBase![dcValIndex] = (short)dcdiff;

        // Store the quantized DC coeff, used for prediction
        block[0] = (short)(dcdiff * s.YDcScale);

        // check if AC is needed at all
        if (aAvail == 0 && cAvail == 0)
            usePred = 0;

        int scale = quant * 2 + (mquant < 0 ? 0 : v.Halfpq);

        short[] acBase = s.AcValBase!;
        int acVal2 = s.AcValOffset + s.BlockIndex[n] * 16;
        int acVal = acVal2;
        if (dcPredDir != 0)        // left
            acVal -= 16;
        else                       // top
            acVal -= 16 * s.BlockWrap[n];

        sbyte[] qscale = s.CurPic.QscaleTable!;
        int qt = s.CurPic.QscaleTableOffset;
        int q1 = qscale[qt + mbPos], q2 = 0;
        if (n == 3)
        {
            q2 = q1;
        }
        else if (dcPredDir != 0)
        {
            if (n == 1)
                q2 = q1;
            else if (cAvail != 0 && mbPos != 0)
                q2 = qscale[qt + mbPos - 1];
        }
        else
        {
            if (n == 2)
                q2 = q1;
            else if (aAvail != 0 && mbPos >= s.MbStride)
                q2 = qscale[qt + mbPos - s.MbStride];
        }

        // AC Decoding
        if (coded != 0)
        {
            int last = 0;
            ReadOnlySpan<byte> zzTable;

            if (s.AcPred != 0)
            {
                if (usePred == 0 && v.Fcm == FrameCodingMode.IlaceFrame)
                    zzTable = v.Zzi8x8;
                else
                    zzTable = dcPredDir == 0 ? v.Zz8x8.AsSpan(2 * 64, 64) : v.Zz8x8.AsSpan(3 * 64, 64);
            }
            else
            {
                zzTable = v.Fcm != FrameCodingMode.IlaceFrame ? v.Zz8x8.AsSpan(1 * 64, 64) : v.Zzi8x8;
            }

            for (int i = 1; last == 0; ++i)
            {
                int ret = Vc1DecodeAcCoeff(v, ref gb, out AcCoeff c, codingset);
                if (ret < 0)
                    return ret;
                last = c.Last;
                i += c.Skip;
                if (i > 63)
                    break;
                block[zzTable[i]] = (short)c.Value;
            }

            // apply AC prediction if needed
            if (usePred != 0)
            {
                int sh;
                if (dcPredDir != 0)
                {
                    sh = v.LeftBlkSh;
                }
                else
                {
                    sh = v.TopBlkSh;
                    acVal += 8;
                }
                // scale predictors if needed
                q1 = PredictorQuant(q1, v.Halfpq);
                if (q1 < 1)
                    return Vc1.AverrorInvalidData;
                if (q2 != 0)
                    q2 = PredictorQuant(q2, v.Halfpq);
                if (q2 != 0 && q1 != q2)
                {
                    for (int k = 1; k < 8; k++)
                        block[k << sh] += (short)((int)(acBase[acVal + k] * (uint)q2
                            * Vc1Data.Vc1Dqscale[q1 - 1] + 0x20000) >> 18);
                }
                else
                {
                    for (int k = 1; k < 8; k++)
                        block[k << sh] += acBase[acVal + k];
                }
            }

            // save AC coeffs for further prediction
            for (int k = 1; k < 8; k++)
            {
                acBase[acVal2 + k] = block[k << v.LeftBlkSh];
                acBase[acVal2 + k + 8] = block[k << v.TopBlkSh];
            }

            // scale AC coeffs
            for (int k = 1; k < 64; k++)
                if (block[k] != 0)
                {
                    block[k] = (short)(block[k] * scale);
                    if (v.Pquantizer == 0)
                        block[k] += (short)(block[k] < 0 ? -quant : quant);
                }
        }
        else
        {
            // no AC coeffs
            acBase.AsSpan(acVal2, 16).Clear();

            // apply AC prediction if needed
            if (usePred != 0)
            {
                int sh;
                if (dcPredDir != 0)
                {
                    sh = v.LeftBlkSh;
                }
                else
                {
                    sh = v.TopBlkSh;
                    acVal += 8;
                    acVal2 += 8;
                }
                acBase.AsSpan(acVal, 8).CopyTo(acBase.AsSpan(acVal2, 8));
                q1 = PredictorQuant(q1, v.Halfpq);
                if (q1 < 1)
                    return Vc1.AverrorInvalidData;
                if (q2 != 0)
                    q2 = PredictorQuant(q2, v.Halfpq);
                if (q2 != 0 && q1 != q2)
                {
                    for (int k = 1; k < 8; k++)
                        acBase[acVal2 + k] = (short)((int)(acBase[acVal2 + k] * q2
                            * (uint)Vc1Data.Vc1Dqscale[q1 - 1] + 0x20000) >> 18);
                }
                for (int k = 1; k < 8; k++)
                {
                    block[k << sh] = (short)(acBase[acVal2 + k] * scale);
                    if (v.Pquantizer == 0 && block[k << sh] != 0)
                        block[k << sh] += (short)(block[k << sh] < 0 ? -quant : quant);
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// <c>vc1_decode_intra_block</c> - an intra block inside an inter picture. More general
    /// than <see cref="Vc1DecodeIBlock"/>: it clears the block itself, sets the DC scale from
    /// its own quantiser, and keeps the left and top predictors in separate halves of
    /// <c>ac_val</c> rather than choosing one of them up front.
    /// </summary>
    public static int Vc1DecodeIntraBlock(Vc1Context v, Span<short> block, int n,
                                          int coded, int mquant, int codingset)
    {
        ref MpegEncContext s = ref v.S;
        ref GetBitContext gb = ref v.Gb;
        int mbPos = s.MbX + s.MbY * s.MbStride;
        int aAvail = v.AAvail, cAvail = v.CAvail;
        int usePred = s.AcPred;
        int quant = Math.Abs(mquant);

        BlockDsp.ClearBlock(block);

        // XXX: Guard against dumb values of mquant
        quant = quant > 31 ? 31 : quant;

        // Set DC scale - y and c use the same so we only set y
        s.YDcScale = Vc1Data.Wmv3DcScaleTable[quant];

        int dcdiff = ReadDcDifferential(v, ref gb, n, quant);

        // Prediction
        dcdiff += Vc1PredDc(ref s, v.Overlap, quant, n, aAvail, cAvail,
                            out int dcValIndex, out int dcPredDir);
        s.DcValBase![dcValIndex] = (short)dcdiff;

        // Store the quantized DC coeff, used for prediction
        block[0] = (short)(dcdiff * s.YDcScale);

        // AC Decoding

        // check if AC is needed at all and adjust direction if needed
        if (aAvail == 0)
            dcPredDir = 1;
        if (cAvail == 0)
            dcPredDir = 0;
        if (aAvail == 0 && cAvail == 0)
            usePred = 0;

        short[] acBase = s.AcValBase!;
        int acVal2 = s.AcValOffset + s.BlockIndex[n] * 16;
        int acVal = acVal2;

        int scale = quant * 2 + (mquant < 0 ? 0 : v.Halfpq);

        if (dcPredDir != 0)        // left
            acVal -= 16;
        else                       // top
            acVal -= 16 * s.BlockWrap[n];

        sbyte[] qscale = s.CurPic.QscaleTable!;
        int qt = s.CurPic.QscaleTableOffset;
        int q1 = qscale[qt + mbPos], q2 = 0;
        if (dcPredDir != 0 && cAvail != 0 && mbPos != 0)
            q2 = qscale[qt + mbPos - 1];
        if (dcPredDir == 0 && aAvail != 0 && mbPos >= s.MbStride)
            q2 = qscale[qt + mbPos - s.MbStride];
        if (dcPredDir != 0 && n == 1)
            q2 = q1;
        if (dcPredDir == 0 && n == 2)
            q2 = q1;
        if (n == 3)
            q2 = q1;

        if (coded != 0)
        {
            int last = 0;

            for (int i = 1; last == 0; ++i)
            {
                int ret = Vc1DecodeAcCoeff(v, ref gb, out AcCoeff c, codingset);
                if (ret < 0)
                    return ret;
                last = c.Last;
                i += c.Skip;
                if (i > 63)
                    break;
                if (v.Fcm == FrameCodingMode.Progressive)
                {
                    block[v.Zz8x8.AsSpan(0 * 64, 64)[i]] = (short)c.Value;
                }
                else if (usePred != 0 && v.Fcm == FrameCodingMode.IlaceFrame)
                {
                    block[dcPredDir == 0 ? v.Zz8x8.AsSpan(2 * 64, 64)[i] : v.Zz8x8.AsSpan(3 * 64, 64)[i]] = (short)c.Value;
                }
                else
                {
                    block[v.Zzi8x8[i]] = (short)c.Value;
                }
            }

            // apply AC prediction if needed
            if (usePred != 0)
            {
                // scale predictors if needed
                q1 = PredictorQuant(q1, v.Halfpq);
                if (q1 < 1)
                    return Vc1.AverrorInvalidData;
                if (q2 != 0)
                    q2 = PredictorQuant(q2, v.Halfpq);
                if (q2 != 0 && q1 != q2)
                {
                    if (dcPredDir != 0)
                        for (int k = 1; k < 8; k++)
                            block[k << v.LeftBlkSh] += (short)((int)(acBase[acVal + k] * q2
                                * (uint)Vc1Data.Vc1Dqscale[q1 - 1] + 0x20000) >> 18);
                    else
                        for (int k = 1; k < 8; k++)
                            block[k << v.TopBlkSh] += (short)((int)(acBase[acVal + k + 8] * q2
                                * (uint)Vc1Data.Vc1Dqscale[q1 - 1] + 0x20000) >> 18);
                }
                else
                {
                    if (dcPredDir != 0)
                        for (int k = 1; k < 8; k++)
                            block[k << v.LeftBlkSh] += acBase[acVal + k];
                    else
                        for (int k = 1; k < 8; k++)
                            block[k << v.TopBlkSh] += acBase[acVal + k + 8];
                }
            }

            // save AC coeffs for further prediction
            for (int k = 1; k < 8; k++)
            {
                acBase[acVal2 + k] = block[k << v.LeftBlkSh];
                acBase[acVal2 + k + 8] = block[k << v.TopBlkSh];
            }

            // scale AC coeffs
            for (int k = 1; k < 64; k++)
                if (block[k] != 0)
                {
                    block[k] = (short)(block[k] * scale);
                    if (v.Pquantizer == 0)
                        block[k] += (short)(block[k] < 0 ? -quant : quant);
                }
        }
        else
        {
            // no AC coeffs
            acBase.AsSpan(acVal2, 16).Clear();
            if (dcPredDir != 0)
            {
                // left
                if (usePred != 0)
                {
                    acBase.AsSpan(acVal, 8).CopyTo(acBase.AsSpan(acVal2, 8));
                    q1 = PredictorQuant(q1, v.Halfpq);
                    if (q1 < 1)
                        return Vc1.AverrorInvalidData;
                    if (q2 != 0)
                        q2 = PredictorQuant(q2, v.Halfpq);
                    if (q2 != 0 && q1 != q2)
                        for (int k = 1; k < 8; k++)
                            acBase[acVal2 + k] = (short)((int)(acBase[acVal2 + k] * (uint)q2
                                * Vc1Data.Vc1Dqscale[q1 - 1] + 0x20000) >> 18);
                }
            }
            else
            {
                // top
                if (usePred != 0)
                {
                    acBase.AsSpan(acVal + 8, 8).CopyTo(acBase.AsSpan(acVal2 + 8, 8));
                    q1 = PredictorQuant(q1, v.Halfpq);
                    if (q1 < 1)
                        return Vc1.AverrorInvalidData;
                    if (q2 != 0)
                        q2 = PredictorQuant(q2, v.Halfpq);
                    if (q2 != 0 && q1 != q2)
                        for (int k = 1; k < 8; k++)
                            acBase[acVal2 + k + 8] = (short)((int)(acBase[acVal2 + k + 8]
                                * (uint)q2 * Vc1Data.Vc1Dqscale[q1 - 1] + 0x20000) >> 18);
                }
            }

            // apply AC prediction if needed
            if (usePred != 0)
            {
                if (dcPredDir != 0)
                {
                    // left
                    for (int k = 1; k < 8; k++)
                    {
                        block[k << v.LeftBlkSh] = (short)(acBase[acVal2 + k] * scale);
                        if (v.Pquantizer == 0 && block[k << v.LeftBlkSh] != 0)
                            block[k << v.LeftBlkSh] +=
                                (short)(block[k << v.LeftBlkSh] < 0 ? -quant : quant);
                    }
                }
                else
                {
                    // top
                    for (int k = 1; k < 8; k++)
                    {
                        block[k << v.TopBlkSh] = (short)(acBase[acVal2 + k + 8] * scale);
                        if (v.Pquantizer == 0 && block[k << v.TopBlkSh] != 0)
                            block[k << v.TopBlkSh] +=
                                (short)(block[k << v.TopBlkSh] < 0 ? -quant : quant);
                    }
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// <c>vc1_decode_p_block</c> - an inter picture's residual block, which unlike the three
    /// intra decoders inverse-transforms and adds itself to the destination as it goes.
    /// </summary>
    /// <remarks>
    /// The transform type decides everything: one 8x8, four 4x4, or two halves either way,
    /// each with its own zigzag, its own coefficient limit and its own sub-block pattern
    /// saying which of them are coded at all. A sub-block the pattern says is absent is
    /// skipped without being transformed.
    /// </remarks>
    /// <returns>The coded pattern, or a negative error.</returns>
    public static int Vc1DecodePBlock(Vc1Context v, Span<short> block, int n,
                                      int mquant, int ttmb, int firstBlock,
                                      Span<byte> dst, int dstOff, int linesize,
                                      int skipBlock, ref int ttmbOut, bool hasTtmbOut)
    {
        ref MpegEncContext s = ref v.S;
        ref GetBitContext gb = ref v.Gb;
        int subblkpat = 0;
        int ttblk = ttmb & 7;
        int pat = 0;
        int quant = Math.Abs(mquant);

        BlockDsp.ClearBlock(block);

        if (ttmb == -1)
        {
            ttblk = Vc1Data.Vc1TtblkToTt[v.TtIndex * 8
                + gb.GetVlc2(Vc1Data.Vc1TtblkVlc[v.TtIndex], Vc1Data.TtblkVlcBits, 1)];
        }
        if (ttblk == (int)TransformType.Tt4X4)
        {
            subblkpat = ~(gb.GetVlc2(Vc1Data.Vc1SubblkpatVlc[v.TtIndex],
                                     Vc1Data.SubblkpatVlcBits, 1) + 1);
        }
        if (ttblk != (int)TransformType.Tt8X8 && ttblk != (int)TransformType.Tt4X4
            && ((v.Ttmbf != 0 || (ttmb != -1 && (ttmb & 8) != 0 && firstBlock == 0))
                || (v.ResRtmFlag == 0 && firstBlock == 0)))
        {
            subblkpat = gb.Decode012();
            if (subblkpat != 0)
                subblkpat ^= 3;   // swap decoded pattern bits
            if (ttblk == (int)TransformType.Tt8X4Top || ttblk == (int)TransformType.Tt8X4Bottom)
                ttblk = (int)TransformType.Tt8X4;
            if (ttblk == (int)TransformType.Tt4X8Right || ttblk == (int)TransformType.Tt4X8Left)
                ttblk = (int)TransformType.Tt4X8;
        }
        int scale = quant * 2 + (mquant < 0 ? 0 : v.Halfpq);

        // convert transforms like 8X4_TOP to generic TT and SUBBLKPAT
        if (ttblk == (int)TransformType.Tt8X4Top || ttblk == (int)TransformType.Tt8X4Bottom)
        {
            subblkpat = 2 - (ttblk == (int)TransformType.Tt8X4Top ? 1 : 0);
            ttblk = (int)TransformType.Tt8X4;
        }
        if (ttblk == (int)TransformType.Tt4X8Right || ttblk == (int)TransformType.Tt4X8Left)
        {
            subblkpat = 2 - (ttblk == (int)TransformType.Tt4X8Left ? 1 : 0);
            ttblk = (int)TransformType.Tt4X8;
        }

        int i, last, idx, off;
        switch (ttblk)
        {
            case (int)TransformType.Tt8X8:
                pat = 0xF;
                i = 0;
                last = 0;
                while (last == 0)
                {
                    int ret = Vc1DecodeAcCoeff(v, ref gb, out AcCoeff c, v.Codingset2);
                    if (ret < 0)
                        return ret;
                    last = c.Last;
                    i += c.Skip;
                    if (i > 63)
                        break;
                    idx = v.Fcm == FrameCodingMode.Progressive ? v.Zz8x8.AsSpan(0 * 64, 64)[i++] : v.Zzi8x8[i++];
                    block[idx] = (short)(c.Value * scale);
                    if (v.Pquantizer == 0)
                        block[idx] += (short)(block[idx] < 0 ? -quant : quant);
                }
                if (skipBlock == 0)
                {
                    if (i == 1)
                    {
                        Vc1Dsp.Vc1InvTrans8x8Dc(dst, dstOff, linesize, block);
                    }
                    else
                    {
                        Vc1Dsp.Vc1InvTrans8x8(block);
                        IdctDsp.AddPixelsClamped(block, dst, dstOff, linesize);
                    }
                }
                break;

            case (int)TransformType.Tt4X4:
                pat = ~subblkpat & 0xF;
                for (int j = 0; j < 4; j++)
                {
                    last = subblkpat & (1 << (3 - j));
                    i = 0;
                    off = (j & 1) * 4 + (j & 2) * 16;
                    while (last == 0)
                    {
                        int ret = Vc1DecodeAcCoeff(v, ref gb, out AcCoeff c, v.Codingset2);
                        if (ret < 0)
                            return ret;
                        last = c.Last;
                        i += c.Skip;
                        if (i > 15)
                            break;
                        idx = v.Fcm == FrameCodingMode.Progressive
                            ? Vc1Data.Vc1SimpleProgressive4x4Zz[i++]
                            : Vc1Data.Vc1AdvInterlaced4x4Zz[i++];
                        block[idx + off] = (short)(c.Value * scale);
                        if (v.Pquantizer == 0)
                            block[idx + off] += (short)(block[idx + off] < 0 ? -quant : quant);
                    }
                    if ((subblkpat & (1 << (3 - j))) == 0 && skipBlock == 0)
                    {
                        int at = dstOff + (j & 1) * 4 + (j & 2) * 2 * linesize;
                        if (i == 1)
                            Vc1Dsp.Vc1InvTrans4x4Dc(dst, at, linesize, block[off..]);
                        else
                            Vc1Dsp.Vc1InvTrans4x4(dst, at, linesize, block[off..]);
                    }
                }
                break;

            case (int)TransformType.Tt8X4:
                pat = ~((subblkpat & 2) * 6 + (subblkpat & 1) * 3) & 0xF;
                for (int j = 0; j < 2; j++)
                {
                    last = subblkpat & (1 << (1 - j));
                    i = 0;
                    off = j * 32;
                    while (last == 0)
                    {
                        int ret = Vc1DecodeAcCoeff(v, ref gb, out AcCoeff c, v.Codingset2);
                        if (ret < 0)
                            return ret;
                        last = c.Last;
                        i += c.Skip;
                        if (i > 31)
                            break;
                        idx = (v.Fcm == FrameCodingMode.Progressive
                            ? v.Zz8x4[i++]
                            : Vc1Data.Vc1AdvInterlaced8x4Zz[i++]) + off;
                        block[idx] = (short)(c.Value * scale);
                        if (v.Pquantizer == 0)
                            block[idx] += (short)(block[idx] < 0 ? -quant : quant);
                    }
                    if ((subblkpat & (1 << (1 - j))) == 0 && skipBlock == 0)
                    {
                        int at = dstOff + j * 4 * linesize;
                        if (i == 1)
                            Vc1Dsp.Vc1InvTrans8x4Dc(dst, at, linesize, block[off..]);
                        else
                            Vc1Dsp.Vc1InvTrans8x4(dst, at, linesize, block[off..]);
                    }
                }
                break;

            case (int)TransformType.Tt4X8:
                pat = ~(subblkpat * 5) & 0xF;
                for (int j = 0; j < 2; j++)
                {
                    last = subblkpat & (1 << (1 - j));
                    i = 0;
                    off = j * 4;
                    while (last == 0)
                    {
                        int ret = Vc1DecodeAcCoeff(v, ref gb, out AcCoeff c, v.Codingset2);
                        if (ret < 0)
                            return ret;
                        last = c.Last;
                        i += c.Skip;
                        if (i > 31)
                            break;
                        idx = (v.Fcm == FrameCodingMode.Progressive
                            ? v.Zz4x8[i++]
                            : Vc1Data.Vc1AdvInterlaced4x8Zz[i++]) + off;
                        block[idx] = (short)(c.Value * scale);
                        if (v.Pquantizer == 0)
                            block[idx] += (short)(block[idx] < 0 ? -quant : quant);
                    }
                    if ((subblkpat & (1 << (1 - j))) == 0 && skipBlock == 0)
                    {
                        int at = dstOff + j * 4;
                        if (i == 1)
                            Vc1Dsp.Vc1InvTrans4x8Dc(dst, at, linesize, block[off..]);
                        else
                            Vc1Dsp.Vc1InvTrans4x8(dst, at, linesize, block[off..]);
                    }
                }
                break;
        }

        if (hasTtmbOut)
            ttmbOut |= ttblk << (n * 4);
        return pat;
    }
    // --- The two helpers the macroblock decoders share -------------------------------------

    /// <summary>
    /// <c>vc1_put_blocks_clamped</c> - writes the reconstructed coefficient blocks of an
    /// already-overlap-filtered macroblock into the picture.
    /// </summary>
    /// <remarks>
    /// This trails the decoding loop, because a block can only be written once overlap
    /// filtering across its edges is finished: one macroblock row and one column behind for a
    /// progressive picture, and only one column behind for an interlaced frame picture, which
    /// needs no vertical overlap filtering. Each arm therefore reads a different one of the
    /// four block groups - top-left, top, left, current - and tests a different neighbour's
    /// <c>mb_type</c> to decide whether that macroblock was intra and so has blocks to write
    /// at all.
    /// <para>
    /// <paramref name="putSigned"/> picks between the two clamping writes: a range-reduced
    /// picture's coefficients are signed around zero rather than biased to 128.
    /// </para>
    /// </remarks>
    public static void Vc1PutBlocksClamped(Vc1Context v, int putSigned)
    {
        ref MpegEncContext s = ref v.S;
        byte[] frame = s.CurPic.Frame!;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;
        const int blockCount = 6;
        int fieldtx = 0;

        short[] block = v.Block!;
        byte[] mbType = v.MbTypeBase!;
        int mt = v.MbTypeOffset;

        // One group of six 64-coefficient blocks, by its index in v->block.
        static int Group(int blkIdx) => blkIdx * 6 * 64;

        void Put(int blkIdx, int i, int dest, int stride)
        {
            int at = Group(blkIdx) + BlockMap[i] * 64;
            if (putSigned != 0)
                IdctDsp.PutSignedPixelsClamped(block.AsSpan(at, 64), frame, dest, stride);
            else
                IdctDsp.PutPixelsClamped(block.AsSpan(at, 64), frame, dest, stride);
        }

        if (s.FirstSliceLine == 0 && v.Fcm != FrameCodingMode.IlaceFrame)
        {
            if (s.MbX != 0)
            {
                for (int i = 0; i < blockCount; i++)
                {
                    if (mbType[mt + (i > 3
                            ? s.BlockIndex[i] - s.BlockWrap[i] - 1
                            : s.BlockIndex[i] - 2 * s.BlockWrap[i] - 2)] != 0)
                    {
                        int dest = s.Dest[0] + ((i & 2) - 4) * 4 * linesize + ((i & 1) - 2) * 8;
                        Put(v.TopleftBlkIdx, i,
                            i > 3 ? s.Dest[i - 3] - 8 * uvlinesize - 8 : dest,
                            i > 3 ? uvlinesize : linesize);
                    }
                }
            }
            if (s.MbX == v.EndMbX - 1)
            {
                for (int i = 0; i < blockCount; i++)
                {
                    if (mbType[mt + (i > 3
                            ? s.BlockIndex[i] - s.BlockWrap[i]
                            : s.BlockIndex[i] - 2 * s.BlockWrap[i])] != 0)
                    {
                        int dest = s.Dest[0] + ((i & 2) - 4) * 4 * linesize + (i & 1) * 8;
                        Put(v.TopBlkIdx, i,
                            i > 3 ? s.Dest[i - 3] - 8 * uvlinesize : dest,
                            i > 3 ? uvlinesize : linesize);
                    }
                }
            }
        }

        if (s.MbY == s.EndMbY - 1 || v.Fcm == FrameCodingMode.IlaceFrame)
        {
            if (s.MbX != 0)
            {
                if (v.Fcm == FrameCodingMode.IlaceFrame)
                    fieldtx = v.FieldtxPlane![s.MbY * s.MbStride + s.MbX - 1];
                for (int i = 0; i < blockCount; i++)
                {
                    if (mbType[mt + (i > 3 ? s.BlockIndex[i] - 1 : s.BlockIndex[i] - 2)] != 0)
                    {
                        int dest = fieldtx != 0
                            ? s.Dest[0] + ((i & 2) >> 1) * linesize + ((i & 1) - 2) * 8
                            : s.Dest[0] + (i & 2) * 4 * linesize + ((i & 1) - 2) * 8;
                        Put(v.LeftBlkIdx, i,
                            i > 3 ? s.Dest[i - 3] - 8 : dest,
                            i > 3 ? uvlinesize : linesize << fieldtx);
                    }
                }
            }
            if (s.MbX == v.EndMbX - 1)
            {
                if (v.Fcm == FrameCodingMode.IlaceFrame)
                    fieldtx = v.FieldtxPlane![s.MbY * s.MbStride + s.MbX];
                for (int i = 0; i < blockCount; i++)
                {
                    if (mbType[mt + s.BlockIndex[i]] != 0)
                    {
                        int dest = fieldtx != 0
                            ? s.Dest[0] + ((i & 2) >> 1) * linesize + (i & 1) * 8
                            : s.Dest[0] + (i & 2) * 4 * linesize + (i & 1) * 8;
                        Put(v.CurBlkIdx, i,
                            i > 3 ? s.Dest[i - 3] : dest,
                            i > 3 ? uvlinesize : linesize << fieldtx);
                    }
                }
            }
        }
    }

    /// <summary>
    /// <c>vc1_b_mc</c> - motion compensation for one B macroblock, which is one or two of the
    /// entry points depending on how the macroblock refers to its two references.
    /// </summary>
    /// <remarks>
    /// <paramref name="dmvX"/> and <paramref name="dmvY"/> are unused: FFmpeg declares them
    /// and the vectors have already been written into <c>s->mv</c> by the caller, which is
    /// where the entry points read them from. They are kept so the signature matches, and
    /// recorded in the registry rather than quietly dropped.
    /// </remarks>
    public static void Vc1BMc(Vc1Context v, ReadOnlySpan<int> dmvX, ReadOnlySpan<int> dmvY,
                              int direct, int mode)
    {
        if (direct != 0)
        {
            Vc1Mc.Vc1Mc1mv(v, 0);
            Vc1Mc.Vc1InterpMc(v);
            return;
        }
        if (mode == Vc1Pred.BmvType.Interpolated)
        {
            Vc1Mc.Vc1Mc1mv(v, 0);
            Vc1Mc.Vc1InterpMc(v);
            return;
        }

        Vc1Mc.Vc1Mc1mv(v, mode == Vc1Pred.BmvType.Backward ? 1 : 0);
    }
    // --- The macroblock decoders -----------------------------------------------------------
    //
    // These are the layer that reads a macroblock header and drives everything else: the
    // vector differential, the quantiser, the coded block pattern, then motion compensation
    // for the inter blocks and the intra decoder for the intra ones, and finally the overlap
    // filter and the write into the picture. There is one per picture type and coding mode,
    // and they differ mostly in how many vectors a macroblock carries and which of the
    // predictors applies.

    /// <summary>The destination offset of block <paramref name="i"/> within a macroblock.</summary>
    private static int BlockOffset(int i, int linesize) =>
        (i & 4) != 0 ? 0 : (i & 1) * 8 + (i & 2) * 4 * linesize;

    /// <summary><c>s-&gt;mv[i][j][k]</c> flattened, as <c>Vc1Mc</c> flattens it.</summary>
    private static int MvIndex(int i, int j, int k) => i * 8 + j * 2 + k;

    /// <summary>
    /// Whether the A (above) and C (left) intra predictors are available for block
    /// <paramref name="i"/>, which the intra decoders read from <c>v-&gt;a_avail</c> and
    /// <c>v-&gt;c_avail</c>.
    /// </summary>
    /// <remarks>
    /// Blocks 2 and 3 are below blocks 0 and 1 inside the same macroblock, so their A
    /// neighbour is always present; 1 and 3 are to the right of 0 and 2, so their C neighbour
    /// is. Only the macroblock's own edges depend on where it sits in the picture.
    /// </remarks>
    private static void SetIntraAvailability(Vc1Context v, int i)
    {
        ref MpegEncContext s = ref v.S;
        byte[] mbType = v.MbTypeBase!;
        int mt = v.MbTypeOffset;

        v.AAvail = v.CAvail = 0;
        if (i == 2 || i == 3 || s.FirstSliceLine == 0)
            v.AAvail = mbType[mt + s.BlockIndex[i] - s.BlockWrap[i]];
        if (i == 1 || i == 3 || s.MbX != 0)
            v.CAvail = mbType[mt + s.BlockIndex[i] - 1];
    }

    /// <summary>
    /// <c>vc1_decode_p_mb</c> - one macroblock of a progressive P picture, in either
    /// one-vector or four-vector mode.
    /// </summary>
    public static int Vc1DecodePMb(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        ref GetBitContext gb = ref v.Gb;
        int mbPos = s.MbX + s.MbY * s.MbStride;
        int cbp;
        int ttmb = v.Ttfrm;          // MB Transform type

        int mbHasCoeffs = 1;         // last_flag
        int firstBlock = 1;
        int blockCbp = 0, blockTt = 0, blockIntra = 0;

        int mquant = v.Pq;           // lossy initialization

        short[] block = v.Block!;
        byte[] mbType = v.MbTypeBase!;
        int mt = v.MbTypeOffset;
        short[] mvL1 = s.CurPic.MotionValL1!;
        int mo1 = s.CurPic.MotionValOffsetL1;
        short[] dcValBase = s.DcValBase!;
        int dcOff = s.DcValOffset;
        sbyte[] qscale = s.CurPic.QscaleTable!;
        int qt = s.CurPic.QscaleTableOffset;
        uint[] picMbType = s.CurPic.MbType!;
        int pmt = s.CurPic.MbTypeOffset;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;
        Span<byte> frame = s.CurPic.Frame!;

        int fourmv = v.MvTypeIsRaw != 0
            ? (int)gb.GetBits1()
            : v.MvTypeMbPlane![mbPos];
        int skipped = v.SkipIsRaw != 0
            ? (int)gb.GetBits1()
            : s.MbskipTable![mbPos];

        // The block group this macroblock's coefficients go into.
        int cur = v.CurBlkIdx * 6 * 64;

        if (fourmv == 0)
        {
            // 1MV mode
            if (skipped == 0)
            {
                MvData md = GetMvdata(v, ref gb);
                int dmvX = md.DmvX, dmvY = md.DmvY;
                mbHasCoeffs = md.MbHasCoeffs;
                s.MbIntra = md.MbIntra;

                if (s.MbIntra != 0)
                {
                    mvL1[mo1 + s.BlockIndex[0] * 2] = 0;
                    mvL1[mo1 + s.BlockIndex[0] * 2 + 1] = 0;
                }
                picMbType[pmt + mbPos] =
                    (uint)(s.MbIntra != 0 ? Mpeg2.MbType.Intra : Mpeg2.MbType.Size16X16);
                Vc1Pred.Vc1PredMv(v, 0, dmvX, dmvY, 1, v.RangeX, v.RangeY, mbType, mt, 0, 0);

                // FIXME Set DC val for inter block ?
                if (s.MbIntra != 0 && mbHasCoeffs == 0)
                {
                    mquant = GetMquant(v, ref gb, mquant);
                    s.AcPred = (int)gb.GetBits1();
                    cbp = 0;
                }
                else if (mbHasCoeffs != 0)
                {
                    if (s.MbIntra != 0)
                        s.AcPred = (int)gb.GetBits1();
                    cbp = gb.GetVlc2(v.CbpcyVlc!, Vc1Data.CbpcyPVlcBits, 2);
                    mquant = GetMquant(v, ref gb, mquant);
                }
                else
                {
                    mquant = v.Pq;
                    cbp = 0;
                }
                qscale[qt + mbPos] = (sbyte)mquant;

                if (v.Ttmbf == 0 && s.MbIntra == 0 && mbHasCoeffs != 0)
                    ttmb = gb.GetVlc2(Vc1Data.Vc1TtmbVlc[v.TtIndex], Vc1Data.TtmbVlcBits, 2);
                if (s.MbIntra == 0)
                    Vc1Mc.Vc1Mc1mv(v, 0);

                int dstIdx = 0;
                for (int i = 0; i < 6; i++)
                {
                    dcValBase[dcOff + s.BlockIndex[i]] = 0;
                    dstIdx += i >> 2;
                    int val = (cbp >> (5 - i)) & 1;
                    int off = BlockOffset(i, linesize);
                    mbType[mt + s.BlockIndex[i]] = (byte)s.MbIntra;
                    if (s.MbIntra != 0)
                    {
                        SetIntraAvailability(v, i);

                        int at = cur + BlockMap[i] * 64;
                        int ret = Vc1DecodeIntraBlock(v, block.AsSpan(at, 64), i, val, mquant,
                                                      (i & 4) != 0 ? v.Codingset2 : v.Codingset);
                        if (ret < 0)
                            return ret;
                        Vc1Dsp.Vc1InvTrans8x8(block.AsSpan(at, 64));
                        if (v.Rangeredfrm != 0)
                            for (int j = 0; j < 64; j++)
                                block[at + j] *= 2;
                        blockCbp |= 0xF << (i << 2);
                        blockIntra |= 1 << i;
                    }
                    else if (val != 0)
                    {
                        int at = cur + BlockMap[i] * 64;
                        int pat = Vc1DecodePBlock(v, block.AsSpan(at, 64), i, mquant, ttmb,
                                                  firstBlock, frame, s.Dest[dstIdx] + off,
                                                  (i & 4) != 0 ? uvlinesize : linesize,
                                                  0, ref blockTt, hasTtmbOut: true);
                        if (pat < 0)
                            return pat;
                        blockCbp |= pat << (i << 2);
                        if (v.Ttmbf == 0 && ttmb < 8)
                            ttmb = -1;
                        firstBlock = 0;
                    }
                }
            }
            else
            {
                // skipped
                s.MbIntra = 0;
                for (int i = 0; i < 6; i++)
                {
                    mbType[mt + s.BlockIndex[i]] = 0;
                    dcValBase[dcOff + s.BlockIndex[i]] = 0;
                }
                picMbType[pmt + mbPos] = Mpeg2.MbType.Skip;
                qscale[qt + mbPos] = 0;
                Vc1Pred.Vc1PredMv(v, 0, 0, 0, 1, v.RangeX, v.RangeY, mbType, mt, 0, 0);
                Vc1Mc.Vc1Mc1mv(v, 0);
            }
        }
        else
        {
            // 4MV mode
            if (skipped == 0)
            {
                // unskipped MB
                int intraCount = 0, codedInter = 0;
                Span<int> isIntra = stackalloc int[6];
                Span<int> isCoded = stackalloc int[6];

                // Get CBPCY
                cbp = gb.GetVlc2(v.CbpcyVlc!, Vc1Data.CbpcyPVlcBits, 2);
                for (int i = 0; i < 6; i++)
                {
                    int val = (cbp >> (5 - i)) & 1;
                    dcValBase[dcOff + s.BlockIndex[i]] = 0;
                    s.MbIntra = 0;
                    if (i < 4)
                    {
                        int dmvX = 0, dmvY = 0;
                        s.MbIntra = 0;
                        mbHasCoeffs = 0;
                        if (val != 0)
                        {
                            MvData md = GetMvdata(v, ref gb);
                            dmvX = md.DmvX;
                            dmvY = md.DmvY;
                            mbHasCoeffs = md.MbHasCoeffs;
                            s.MbIntra = md.MbIntra;
                        }
                        Vc1Pred.Vc1PredMv(v, i, dmvX, dmvY, 0, v.RangeX, v.RangeY,
                                          mbType, mt, 0, 0);
                        if (s.MbIntra == 0)
                            Vc1Mc.Vc1Mc4mvLuma(v, i, 0, 0);
                        intraCount += s.MbIntra;
                        isIntra[i] = s.MbIntra;
                        isCoded[i] = mbHasCoeffs;
                    }
                    if ((i & 4) != 0)
                    {
                        isIntra[i] = intraCount >= 3 ? 1 : 0;
                        isCoded[i] = val;
                    }
                    if (i == 4)
                        Vc1Mc.Vc1Mc4mvChroma(v, 0);
                    mbType[mt + s.BlockIndex[i]] = (byte)isIntra[i];
                    if (codedInter == 0)
                        codedInter = (isIntra[i] == 0 ? 1 : 0) & isCoded[i];
                }

                // if there are no coded blocks then don't do anything more
                int dstIdx = 0;
                if (intraCount != 0 || codedInter != 0)
                {
                    mquant = GetMquant(v, ref gb, mquant);
                    qscale[qt + mbPos] = (sbyte)mquant;

                    // test if block is intra and has pred
                    {
                        int intrapred = 0;
                        for (int i = 0; i < 6; i++)
                            if (isIntra[i] != 0)
                            {
                                if (((s.FirstSliceLine == 0 || i == 2 || i == 3)
                                        && mbType[mt + s.BlockIndex[i] - s.BlockWrap[i]] != 0)
                                    || ((s.MbX != 0 || i == 1 || i == 3)
                                        && mbType[mt + s.BlockIndex[i] - 1] != 0))
                                {
                                    intrapred = 1;
                                    break;
                                }
                            }
                        s.AcPred = intrapred != 0 ? (int)gb.GetBits1() : 0;
                    }

                    if (v.Ttmbf == 0 && codedInter != 0)
                        ttmb = gb.GetVlc2(Vc1Data.Vc1TtmbVlc[v.TtIndex],
                                          Vc1Data.TtmbVlcBits, 2);

                    for (int i = 0; i < 6; i++)
                    {
                        dstIdx += i >> 2;
                        int off = BlockOffset(i, linesize);
                        s.MbIntra = isIntra[i];
                        if (isIntra[i] != 0)
                        {
                            SetIntraAvailability(v, i);

                            int at = cur + BlockMap[i] * 64;
                            int ret = Vc1DecodeIntraBlock(
                                v, block.AsSpan(at, 64), i, isCoded[i], mquant,
                                (i & 4) != 0 ? v.Codingset2 : v.Codingset);
                            if (ret < 0)
                                return ret;
                            Vc1Dsp.Vc1InvTrans8x8(block.AsSpan(at, 64));
                            if (v.Rangeredfrm != 0)
                                for (int j = 0; j < 64; j++)
                                    block[at + j] *= 2;
                            blockCbp |= 0xF << (i << 2);
                            blockIntra |= 1 << i;
                        }
                        else if (isCoded[i] != 0)
                        {
                            int at = cur + BlockMap[i] * 64;
                            int pat = Vc1DecodePBlock(
                                v, block.AsSpan(at, 64), i, mquant, ttmb, firstBlock,
                                frame, s.Dest[dstIdx] + off,
                                (i & 4) != 0 ? uvlinesize : linesize,
                                0, ref blockTt, hasTtmbOut: true);
                            if (pat < 0)
                                return pat;
                            blockCbp |= pat << (i << 2);
                            if (v.Ttmbf == 0 && ttmb < 8)
                                ttmb = -1;
                            firstBlock = 0;
                        }
                    }
                }
            }
            else
            {
                // skipped MB
                s.MbIntra = 0;
                qscale[qt + mbPos] = 0;
                for (int i = 0; i < 6; i++)
                {
                    mbType[mt + s.BlockIndex[i]] = 0;
                    dcValBase[dcOff + s.BlockIndex[i]] = 0;
                }
                for (int i = 0; i < 4; i++)
                {
                    Vc1Pred.Vc1PredMv(v, i, 0, 0, 0, v.RangeX, v.RangeY, mbType, mt, 0, 0);
                    Vc1Mc.Vc1Mc4mvLuma(v, i, 0, 0);
                }
                Vc1Mc.Vc1Mc4mvChroma(v, 0);
                qscale[qt + mbPos] = 0;
            }
        }

        // The C reaches here by falling through or by `goto end` when a four-vector
        // macroblock turns out to have no coded blocks at all.
        if (v.Overlap != 0 && v.Pq >= 9)
            Vc1LoopFilter.Vc1POverlapFilter(v);
        Vc1PutBlocksClamped(v, 1);

        v.CbpBase![v.CbpOffset + s.MbX] = (uint)blockCbp;
        v.TtblkBase![v.TtblkOffset + s.MbX] = blockTt;
        v.IsIntraBase![v.IsIntraOffset + s.MbX] = (byte)blockIntra;

        return 0;
    }
    /// <summary>
    /// <c>ff_vc1_mbmode_intfrp[fourmvswitch][idx][field]</c>, which the port holds flattened
    /// from [2][15][4].
    /// </summary>
    private static int MbmodeIntfrp(int fourmvswitch, int idx, int field) =>
        Vc1Data.Vc1MbmodeIntfrp[(fourmvswitch * 15 + idx) * 4 + field];

    /// <summary>
    /// <c>vc1_decode_p_mb_intfr</c> - one macroblock of a P picture in an interlaced frame.
    /// </summary>
    /// <remarks>
    /// Where the progressive decoder reads a vector differential and infers the mode from it,
    /// this one reads a <em>macroblock mode</em> first, and that mode says everything: how
    /// many vectors the macroblock carries, whether each luma block's two fields are coded as
    /// alternate rows (<c>blk_mv_type</c>), whether the macroblock is intra, whether it has
    /// coefficients, and whether FIELDTX applies. The vectors that follow are then read
    /// through a block pattern rather than one per block.
    /// </remarks>
    public static int Vc1DecodePMbIntfr(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        ref GetBitContext gb = ref v.Gb;
        int mbPos = s.MbX + s.MbY * s.MbStride;
        int cbp = 0;
        int ttmb = v.Ttfrm;

        int mbHasCoeffs = 1;
        int firstBlock = 1;
        int fourmv = 0, twomv = 0;
        int blockCbp = 0, blockTt = 0;
        int idxMbmode = 0, mvbp;
        int fieldtx;

        int mquant = v.Pq;           // Lossy initialization

        short[] block = v.Block!;
        byte[] mbType = v.MbTypeBase!;
        int mt = v.MbTypeOffset;
        byte[] blkMvType = v.BlkMvTypeBase!;
        int bmt = v.BlkMvTypeOffset;
        short[] mvL1 = s.CurPic.MotionValL1!;
        int mo1 = s.CurPic.MotionValOffsetL1;
        short[] dcValBase = s.DcValBase!;
        int dcOff = s.DcValOffset;
        sbyte[] qscale = s.CurPic.QscaleTable!;
        int qt = s.CurPic.QscaleTableOffset;
        uint[] picMbType = s.CurPic.MbType!;
        int pmt = s.CurPic.MbTypeOffset;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;
        Span<byte> frame = s.CurPic.Frame!;
        int cur = v.CurBlkIdx * 6 * 64;

        int skipped = v.SkipIsRaw != 0
            ? (int)gb.GetBits1()
            : s.MbskipTable![mbPos];

        if (skipped == 0)
        {
            idxMbmode = v.Fourmvswitch != 0
                ? gb.GetVlc2(v.MbmodeVlc!, Vc1Data.Intfr4MvMbmodeVlcBits, 2)
                : gb.GetVlc2(v.MbmodeVlc!, Vc1Data.IntfrNon4MvMbmodeVlcBits, 2);

            int mode = MbmodeIntfrp(v.Fourmvswitch, idxMbmode, 0);
            switch (mode)
            {
                // store the motion vector type in a flag (useful later)
                case (int)MbModesIntfr.FourMv:
                    fourmv = 1;
                    for (int i = 0; i < 4; i++)
                        blkMvType[bmt + s.BlockIndex[i]] = 0;
                    break;
                case (int)MbModesIntfr.FourMvField:
                    fourmv = 1;
                    for (int i = 0; i < 4; i++)
                        blkMvType[bmt + s.BlockIndex[i]] = 1;
                    break;
                case (int)MbModesIntfr.TwoMvField:
                    twomv = 1;
                    for (int i = 0; i < 4; i++)
                        blkMvType[bmt + s.BlockIndex[i]] = 1;
                    break;
                case (int)MbModesIntfr.OneMv:
                    for (int i = 0; i < 4; i++)
                        blkMvType[bmt + s.BlockIndex[i]] = 0;
                    break;
            }

            if (mode == (int)MbModesIntfr.Intra)
            {
                // intra MB
                for (int i = 0; i < 4; i++)
                {
                    mvL1[mo1 + s.BlockIndex[i] * 2] = 0;
                    mvL1[mo1 + s.BlockIndex[i] * 2 + 1] = 0;
                }
                v.IsIntraBase![v.IsIntraOffset + s.MbX] = 0x3f;   // all 1
                s.MbIntra = 1;
                picMbType[pmt + mbPos] = Mpeg2.MbType.Intra;
                fieldtx = v.FieldtxPlane![mbPos] = (byte)gb.GetBits1();
                mbHasCoeffs = (int)gb.GetBits1();
                if (mbHasCoeffs != 0)
                    cbp = 1 + gb.GetVlc2(v.CbpcyVlc!, Vc1Data.CbpcyPVlcBits, 2);
                s.AcPred = v.AcpredPlane![mbPos] = (byte)gb.GetBits1();
                mquant = GetMquant(v, ref gb, mquant);
                qscale[qt + mbPos] = (sbyte)mquant;
                // Set DC scale - y and c use the same so we only set y
                s.YDcScale = Vc1Data.Wmv3DcScaleTable[Math.Abs(mquant)];

                for (int i = 0; i < 6; i++)
                {
                    v.AAvail = v.CAvail = 0;
                    mbType[mt + s.BlockIndex[i]] = 1;
                    dcValBase[dcOff + s.BlockIndex[i]] = 0;
                    int val = (cbp >> (5 - i)) & 1;
                    if (i == 2 || i == 3 || s.FirstSliceLine == 0)
                        v.AAvail = mbType[mt + s.BlockIndex[i] - s.BlockWrap[i]];
                    if (i == 1 || i == 3 || s.MbX != 0)
                        v.CAvail = mbType[mt + s.BlockIndex[i] - 1];

                    int at = cur + BlockMap[i] * 64;
                    int ret = Vc1DecodeIntraBlock(v, block.AsSpan(at, 64), i, val, mquant,
                                                  (i & 4) != 0 ? v.Codingset2 : v.Codingset);
                    if (ret < 0)
                        return ret;
                    Vc1Dsp.Vc1InvTrans8x8(block.AsSpan(at, 64));
                    blockCbp |= 0xf << (i << 2);
                }
            }
            else
            {
                // inter MB
                mbHasCoeffs = MbmodeIntfrp(v.Fourmvswitch, idxMbmode, 3);
                if (mbHasCoeffs != 0)
                    cbp = 1 + gb.GetVlc2(v.CbpcyVlc!, Vc1Data.CbpcyPVlcBits, 2);
                if (mode == (int)MbModesIntfr.TwoMvField)
                {
                    v.Twomvbp = (byte)gb.GetVlc2(v.TwomvbpVlc!,
                                                 Vc1Data.TwoMvBlockPatternVlcBits, 1);
                }
                else if (mode == (int)MbModesIntfr.FourMv
                         || mode == (int)MbModesIntfr.FourMvField)
                {
                    v.Fourmvbp = (byte)gb.GetVlc2(v.FourmvbpVlc!,
                                                  Vc1Data.FourMvBlockPatternVlcBits, 1);
                }

                s.MbIntra = 0;
                v.IsIntraBase![v.IsIntraOffset + s.MbX] = 0;
                for (int i = 0; i < 6; i++)
                    mbType[mt + s.BlockIndex[i]] = 0;
                fieldtx = v.FieldtxPlane![mbPos] =
                    (byte)MbmodeIntfrp(v.Fourmvswitch, idxMbmode, 1);

                // for all motion vectors read MVDATA and motion compensate each block
                int predFlag = 0;
                if (fourmv != 0)
                {
                    mvbp = v.Fourmvbp;
                    for (int i = 0; i < 4; i++)
                    {
                        int dmvX = 0, dmvY = 0;
                        if ((mvbp & (8 >> i)) != 0)
                            GetMvdataInterlaced(v, ref gb, out dmvX, out dmvY,
                                                ref predFlag, hasPredFlag: false);
                        Vc1Pred.Vc1PredMvIntfr(v, i, dmvX, dmvY, 0, v.RangeX, v.RangeY, 0);
                        Vc1Mc.Vc1Mc4mvLuma(v, i, 0, 0);
                    }
                    Vc1Mc.Vc1Mc4mvChroma4(v, 0, 0, 0);
                }
                else if (twomv != 0)
                {
                    mvbp = v.Twomvbp;
                    int dmvX = 0, dmvY = 0;
                    if ((mvbp & 2) != 0)
                        GetMvdataInterlaced(v, ref gb, out dmvX, out dmvY,
                                            ref predFlag, hasPredFlag: false);
                    Vc1Pred.Vc1PredMvIntfr(v, 0, dmvX, dmvY, 2, v.RangeX, v.RangeY, 0);
                    Vc1Mc.Vc1Mc4mvLuma(v, 0, 0, 0);
                    Vc1Mc.Vc1Mc4mvLuma(v, 1, 0, 0);
                    dmvX = dmvY = 0;
                    if ((mvbp & 1) != 0)
                        GetMvdataInterlaced(v, ref gb, out dmvX, out dmvY,
                                            ref predFlag, hasPredFlag: false);
                    Vc1Pred.Vc1PredMvIntfr(v, 2, dmvX, dmvY, 2, v.RangeX, v.RangeY, 0);
                    Vc1Mc.Vc1Mc4mvLuma(v, 2, 0, 0);
                    Vc1Mc.Vc1Mc4mvLuma(v, 3, 0, 0);
                    Vc1Mc.Vc1Mc4mvChroma4(v, 0, 0, 0);
                }
                else
                {
                    mvbp = MbmodeIntfrp(v.Fourmvswitch, idxMbmode, 2);
                    int dmvX = 0, dmvY = 0;
                    if (mvbp != 0)
                        GetMvdataInterlaced(v, ref gb, out dmvX, out dmvY,
                                            ref predFlag, hasPredFlag: false);
                    Vc1Pred.Vc1PredMvIntfr(v, 0, dmvX, dmvY, 1, v.RangeX, v.RangeY, 0);
                    Vc1Mc.Vc1Mc1mv(v, 0);
                }

                if (cbp != 0)
                    mquant = GetMquant(v, ref gb, mquant);   // p. 227
                qscale[qt + mbPos] = (sbyte)mquant;
                if (v.Ttmbf == 0 && cbp != 0)
                    ttmb = gb.GetVlc2(Vc1Data.Vc1TtmbVlc[v.TtIndex], Vc1Data.TtmbVlcBits, 2);

                int dstIdx = 0;
                for (int i = 0; i < 6; i++)
                {
                    dcValBase[dcOff + s.BlockIndex[i]] = 0;
                    dstIdx += i >> 2;
                    int val = (cbp >> (5 - i)) & 1;
                    int off;
                    if (fieldtx == 0)
                        off = (i & 4) != 0 ? 0 : (i & 1) * 8 + (i & 2) * 4 * linesize;
                    else
                        off = (i & 4) != 0 ? 0 : (i & 1) * 8 + (i > 1 ? 1 : 0) * linesize;
                    if (val != 0)
                    {
                        int at = cur + BlockMap[i] * 64;
                        int pat = Vc1DecodePBlock(
                            v, block.AsSpan(at, 64), i, mquant, ttmb, firstBlock,
                            frame, s.Dest[dstIdx] + off,
                            (i & 4) != 0 ? uvlinesize : linesize << fieldtx,
                            0, ref blockTt, hasTtmbOut: true);
                        if (pat < 0)
                            return pat;
                        blockCbp |= pat << (i << 2);
                        if (v.Ttmbf == 0 && ttmb < 8)
                            ttmb = -1;
                        firstBlock = 0;
                    }
                }
            }
        }
        else
        {
            // skipped
            s.MbIntra = 0;
            v.IsIntraBase![v.IsIntraOffset + s.MbX] = 0;
            for (int i = 0; i < 6; i++)
            {
                mbType[mt + s.BlockIndex[i]] = 0;
                dcValBase[dcOff + s.BlockIndex[i]] = 0;
            }
            picMbType[pmt + mbPos] = Mpeg2.MbType.Skip;
            qscale[qt + mbPos] = 0;
            for (int i = 0; i < 4; i++)
                blkMvType[bmt + s.BlockIndex[i]] = 0;
            Vc1Pred.Vc1PredMvIntfr(v, 0, 0, 0, 1, v.RangeX, v.RangeY, 0);
            Vc1Mc.Vc1Mc1mv(v, 0);
            v.FieldtxPlane![mbPos] = 0;
        }

        if (v.Overlap != 0 && v.Pq >= 9)
            Vc1LoopFilter.Vc1POverlapFilter(v);
        Vc1PutBlocksClamped(v, 1);

        v.CbpBase![v.CbpOffset + s.MbX] = (uint)blockCbp;
        v.TtblkBase![v.TtblkOffset + s.MbX] = blockTt;

        return 0;
    }
    /// <summary>
    /// <c>vc1_decode_p_mb_intfi</c> - one macroblock of a P picture in an interlaced field.
    /// </summary>
    /// <remarks>
    /// A field picture's rows are contiguous, so there is no FIELDTX and no field-coded block
    /// type; what the macroblock mode carries instead is whether the macroblock is intra
    /// (index 0 or 1), one-vector (2 to 5) or four-vector (6 and up), with the low bit of the
    /// index saying whether coefficients follow. Each vector also carries which of the two
    /// reference fields it points into, which is what <c>pred_flag</c> is.
    /// </remarks>
    public static int Vc1DecodePMbIntfi(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        ref GetBitContext gb = ref v.Gb;
        int mbPos = s.MbX + s.MbY * s.MbStride;
        int cbp = 0;
        int ttmb = v.Ttfrm;

        int mbHasCoeffs = 1;
        int firstBlock = 1;
        int blockCbp = 0, blockTt = 0;

        int mquant = v.Pq;           // Lossy initialization

        short[] block = v.Block!;
        byte[] mbType = v.MbTypeBase!;
        int mt = v.MbTypeOffset;
        short[] mvL1 = s.CurPic.MotionValL1!;
        int mo1 = s.CurPic.MotionValOffsetL1;
        short[] dcValBase = s.DcValBase!;
        int dcOff = s.DcValOffset;
        sbyte[] qscale = s.CurPic.QscaleTable!;
        int qt = s.CurPic.QscaleTableOffset;
        uint[] picMbType = s.CurPic.MbType!;
        int pmt = s.CurPic.MbTypeOffset;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;
        Span<byte> frame = s.CurPic.Frame!;
        int cur = v.CurBlkIdx * 6 * 64;

        // VC1_IF_MBMODE_VLC_BITS is a placeholder for VC1_IF_MMV_MBMODE_VLC_BITS in the C.
        int idxMbmode = gb.GetVlc2(v.MbmodeVlc!, Vc1Data.IfMmvMbmodeVlcBits, 2);

        if (idxMbmode <= 1)
        {
            // intra MB
            v.IsIntraBase![v.IsIntraOffset + s.MbX] = 0x3f;   // all 1
            s.MbIntra = 1;
            mvL1[mo1 + (s.BlockIndex[0] + v.BlocksOff) * 2] = 0;
            mvL1[mo1 + (s.BlockIndex[0] + v.BlocksOff) * 2 + 1] = 0;
            picMbType[pmt + mbPos + v.MbOff] = Mpeg2.MbType.Intra;
            mquant = GetMquant(v, ref gb, mquant);
            qscale[qt + mbPos] = (sbyte)mquant;
            // Set DC scale - y and c use the same so we only set y
            s.YDcScale = Vc1Data.Wmv3DcScaleTable[Math.Abs(mquant)];
            s.AcPred = v.AcpredPlane![mbPos] = (byte)gb.GetBits1();
            mbHasCoeffs = idxMbmode & 1;
            if (mbHasCoeffs != 0)
                cbp = 1 + gb.GetVlc2(v.CbpcyVlc!, Vc1Data.IcbpcyVlcBits, 2);

            for (int i = 0; i < 6; i++)
            {
                v.AAvail = v.CAvail = 0;
                mbType[mt + s.BlockIndex[i]] = 1;
                dcValBase[dcOff + s.BlockIndex[i]] = 0;
                int val = (cbp >> (5 - i)) & 1;
                if (i == 2 || i == 3 || s.FirstSliceLine == 0)
                    v.AAvail = mbType[mt + s.BlockIndex[i] - s.BlockWrap[i]];
                if (i == 1 || i == 3 || s.MbX != 0)
                    v.CAvail = mbType[mt + s.BlockIndex[i] - 1];

                int at = cur + BlockMap[i] * 64;
                int ret = Vc1DecodeIntraBlock(v, block.AsSpan(at, 64), i, val, mquant,
                                              (i & 4) != 0 ? v.Codingset2 : v.Codingset);
                if (ret < 0)
                    return ret;
                Vc1Dsp.Vc1InvTrans8x8(block.AsSpan(at, 64));
                blockCbp |= 0xf << (i << 2);
            }
        }
        else
        {
            s.MbIntra = 0;
            v.IsIntraBase![v.IsIntraOffset + s.MbX] = 0;
            picMbType[pmt + mbPos + v.MbOff] = (uint)Mpeg2.MbType.Size16X16;
            for (int i = 0; i < 6; i++)
                mbType[mt + s.BlockIndex[i]] = 0;

            if (idxMbmode <= 5)
            {
                // 1-MV
                int dmvX = 0, dmvY = 0, predFlag = 0;
                if ((idxMbmode & 1) != 0)
                    GetMvdataInterlaced(v, ref gb, out dmvX, out dmvY,
                                        ref predFlag, hasPredFlag: true);
                Vc1Pred.Vc1PredMv(v, 0, dmvX, dmvY, 1, v.RangeX, v.RangeY,
                                  mbType, mt, predFlag, 0);
                Vc1Mc.Vc1Mc1mv(v, 0);
                mbHasCoeffs = (idxMbmode & 2) == 0 ? 1 : 0;
            }
            else
            {
                // 4-MV
                v.Fourmvbp = (byte)gb.GetVlc2(v.FourmvbpVlc!,
                                              Vc1Data.FourMvBlockPatternVlcBits, 1);
                for (int i = 0; i < 4; i++)
                {
                    int dmvX = 0, dmvY = 0, predFlag = 0;
                    if ((v.Fourmvbp & (8 >> i)) != 0)
                        GetMvdataInterlaced(v, ref gb, out dmvX, out dmvY,
                                            ref predFlag, hasPredFlag: true);
                    Vc1Pred.Vc1PredMv(v, i, dmvX, dmvY, 0, v.RangeX, v.RangeY,
                                      mbType, mt, predFlag, 0);
                    Vc1Mc.Vc1Mc4mvLuma(v, i, 0, 0);
                }
                Vc1Mc.Vc1Mc4mvChroma(v, 0);
                mbHasCoeffs = idxMbmode & 1;
            }

            if (mbHasCoeffs != 0)
                cbp = 1 + gb.GetVlc2(v.CbpcyVlc!, Vc1Data.CbpcyPVlcBits, 2);
            if (cbp != 0)
                mquant = GetMquant(v, ref gb, mquant);
            qscale[qt + mbPos] = (sbyte)mquant;
            if (v.Ttmbf == 0 && cbp != 0)
                ttmb = gb.GetVlc2(Vc1Data.Vc1TtmbVlc[v.TtIndex], Vc1Data.TtmbVlcBits, 2);

            int dstIdx = 0;
            for (int i = 0; i < 6; i++)
            {
                dcValBase[dcOff + s.BlockIndex[i]] = 0;
                dstIdx += i >> 2;
                int val = (cbp >> (5 - i)) & 1;
                int off = BlockOffset(i, linesize);
                if (val != 0)
                {
                    int at = cur + BlockMap[i] * 64;
                    int pat = Vc1DecodePBlock(
                        v, block.AsSpan(at, 64), i, mquant, ttmb, firstBlock,
                        frame, s.Dest[dstIdx] + off,
                        (i & 4) != 0 ? uvlinesize : linesize,
                        0, ref blockTt, hasTtmbOut: true);
                    if (pat < 0)
                        return pat;
                    blockCbp |= pat << (i << 2);
                    if (v.Ttmbf == 0 && ttmb < 8)
                        ttmb = -1;
                    firstBlock = 0;
                }
            }
        }

        if (v.Overlap != 0 && v.Pq >= 9)
            Vc1LoopFilter.Vc1POverlapFilter(v);
        Vc1PutBlocksClamped(v, 1);

        v.CbpBase![v.CbpOffset + s.MbX] = (uint)blockCbp;
        v.TtblkBase![v.TtblkOffset + s.MbX] = blockTt;

        return 0;
    }
    /// <summary>
    /// <c>vc1_decode_b_mb</c> - one macroblock of a progressive B picture.
    /// </summary>
    /// <remarks>
    /// A B macroblock carries two vectors, and the <c>bmvtype</c> symbol says which of them
    /// the picture actually uses: forward only, backward only, or both interpolated. Which
    /// way round the first two map depends on where the picture sits between its references,
    /// which is what <c>bfraction</c> measures. The direct and skipped flags are bitplanes,
    /// so each is either read here a bit at a time or taken from the plane decoded for the
    /// whole picture.
    /// <para>
    /// Unlike the P decoders this one writes through <c>v-&gt;blocks</c> and puts each block
    /// as it finishes, with no overlap filter and no one-macroblock delay: a B picture is
    /// never a reference, so nothing reads back what it wrote.
    /// </para>
    /// </remarks>
    public static int Vc1DecodeBMb(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        ref GetBitContext gb = ref v.Gb;
        int mbPos = s.MbX + s.MbY * s.MbStride;
        int cbp = 0;
        int ttmb = v.Ttfrm;
        int mbHasCoeffs = 0;
        int firstBlock = 1;
        int bmvtype = Vc1Pred.BmvType.Backward;

        int mquant = v.Pq;           // lossy initialization
        s.MbIntra = 0;

        short[] blocks = v.Blocks;
        byte[] mbType = v.MbTypeBase!;
        int mt = v.MbTypeOffset;
        short[] dcValBase = s.DcValBase!;
        int dcOff = s.DcValOffset;
        sbyte[] qscale = s.CurPic.QscaleTable!;
        int qt = s.CurPic.QscaleTableOffset;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;
        byte[] frame = s.CurPic.Frame!;

        int direct = v.DmbIsRaw != 0 ? (int)gb.GetBits1() : v.DirectMbPlane![mbPos];
        int skipped = v.SkipIsRaw != 0 ? (int)gb.GetBits1() : s.MbskipTable![mbPos];

        Span<int> dmvX = stackalloc int[2];
        Span<int> dmvY = stackalloc int[2];
        dmvX[0] = dmvX[1] = dmvY[0] = dmvY[1] = 0;
        for (int i = 0; i < 6; i++)
        {
            mbType[mt + s.BlockIndex[i]] = 0;
            dcValBase[dcOff + s.BlockIndex[i]] = 0;
        }
        qscale[qt + mbPos] = 0;

        if (direct == 0)
        {
            if (skipped == 0)
            {
                MvData md = GetMvdata(v, ref gb);
                dmvX[0] = md.DmvX;
                dmvY[0] = md.DmvY;
                mbHasCoeffs = md.MbHasCoeffs;
                s.MbIntra = md.MbIntra;
                dmvX[1] = dmvX[0];
                dmvY[1] = dmvY[0];
            }
            if (skipped != 0 || s.MbIntra == 0)
            {
                bmvtype = gb.Decode012();
                switch (bmvtype)
                {
                    case 0:
                        // B_FRACTION_DEN is 256, so half of it is 128.
                        bmvtype = v.Bfraction >= 128 ? Vc1Pred.BmvType.Backward
                                                     : Vc1Pred.BmvType.Forward;
                        break;
                    case 1:
                        bmvtype = v.Bfraction >= 128 ? Vc1Pred.BmvType.Forward
                                                     : Vc1Pred.BmvType.Backward;
                        break;
                    case 2:
                        bmvtype = Vc1Pred.BmvType.Interpolated;
                        dmvX[0] = dmvY[0] = 0;
                        break;
                }
            }
        }
        for (int i = 0; i < 6; i++)
            mbType[mt + s.BlockIndex[i]] = (byte)s.MbIntra;

        if (skipped != 0)
        {
            if (direct != 0)
                bmvtype = Vc1Pred.BmvType.Interpolated;
            Vc1Pred.Vc1PredBMv(v, dmvX, dmvY, direct, bmvtype);
            Vc1BMc(v, dmvX, dmvY, direct, bmvtype);
            return 0;
        }
        if (direct != 0)
        {
            cbp = gb.GetVlc2(v.CbpcyVlc!, Vc1Data.CbpcyPVlcBits, 2);
            mquant = GetMquant(v, ref gb, mquant);
            s.MbIntra = 0;
            qscale[qt + mbPos] = (sbyte)mquant;
            if (v.Ttmbf == 0)
                ttmb = gb.GetVlc2(Vc1Data.Vc1TtmbVlc[v.TtIndex], Vc1Data.TtmbVlcBits, 2);
            dmvX[0] = dmvY[0] = dmvX[1] = dmvY[1] = 0;
            Vc1Pred.Vc1PredBMv(v, dmvX, dmvY, direct, bmvtype);
            Vc1BMc(v, dmvX, dmvY, direct, bmvtype);
        }
        else
        {
            if (mbHasCoeffs == 0 && s.MbIntra == 0)
            {
                // no coded blocks - effectively skipped
                Vc1Pred.Vc1PredBMv(v, dmvX, dmvY, direct, bmvtype);
                Vc1BMc(v, dmvX, dmvY, direct, bmvtype);
                return 0;
            }
            if (s.MbIntra != 0 && mbHasCoeffs == 0)
            {
                mquant = GetMquant(v, ref gb, mquant);
                qscale[qt + mbPos] = (sbyte)mquant;
                s.AcPred = (byte)gb.GetBits1();
                cbp = 0;
                Vc1Pred.Vc1PredBMv(v, dmvX, dmvY, direct, bmvtype);
            }
            else
            {
                if (bmvtype == Vc1Pred.BmvType.Interpolated)
                {
                    MvData md = GetMvdata(v, ref gb);
                    dmvX[0] = md.DmvX;
                    dmvY[0] = md.DmvY;
                    mbHasCoeffs = md.MbHasCoeffs;
                    s.MbIntra = md.MbIntra;
                    if (mbHasCoeffs == 0)
                    {
                        // interpolated skipped block
                        Vc1Pred.Vc1PredBMv(v, dmvX, dmvY, direct, bmvtype);
                        Vc1BMc(v, dmvX, dmvY, direct, bmvtype);
                        return 0;
                    }
                }
                Vc1Pred.Vc1PredBMv(v, dmvX, dmvY, direct, bmvtype);
                if (s.MbIntra == 0)
                    Vc1BMc(v, dmvX, dmvY, direct, bmvtype);
                if (s.MbIntra != 0)
                    s.AcPred = (byte)gb.GetBits1();
                cbp = gb.GetVlc2(v.CbpcyVlc!, Vc1Data.CbpcyPVlcBits, 2);
                mquant = GetMquant(v, ref gb, mquant);
                qscale[qt + mbPos] = (sbyte)mquant;
                if (v.Ttmbf == 0 && s.MbIntra == 0 && mbHasCoeffs != 0)
                    ttmb = gb.GetVlc2(Vc1Data.Vc1TtmbVlc[v.TtIndex], Vc1Data.TtmbVlcBits, 2);
            }
        }

        int dstIdx = 0;
        for (int i = 0; i < 6; i++)
        {
            dcValBase[dcOff + s.BlockIndex[i]] = 0;
            dstIdx += i >> 2;
            int val = (cbp >> (5 - i)) & 1;
            int off = BlockOffset(i, linesize);
            mbType[mt + s.BlockIndex[i]] = (byte)s.MbIntra;
            if (s.MbIntra != 0)
            {
                SetIntraAvailability(v, i);

                int ret = Vc1DecodeIntraBlock(v, blocks.AsSpan(i * 64, 64), i, val, mquant,
                                              (i & 4) != 0 ? v.Codingset2 : v.Codingset);
                if (ret < 0)
                    return ret;
                Vc1Dsp.Vc1InvTrans8x8(blocks.AsSpan(i * 64, 64));
                if (v.Rangeredfrm != 0)
                    for (int j = 0; j < 64; j++)
                        blocks[i * 64 + j] *= 2;
                IdctDsp.PutSignedPixelsClamped(blocks.AsSpan(i * 64, 64), frame,
                                               s.Dest[dstIdx] + off,
                                               (i & 4) != 0 ? uvlinesize : linesize);
            }
            else if (val != 0)
            {
                int unused = 0;
                int pat = Vc1DecodePBlock(v, blocks.AsSpan(i * 64, 64), i, mquant, ttmb,
                                          firstBlock, frame, s.Dest[dstIdx] + off,
                                          (i & 4) != 0 ? uvlinesize : linesize,
                                          0, ref unused, hasTtmbOut: false);
                if (pat < 0)
                    return pat;
                if (v.Ttmbf == 0 && ttmb < 8)
                    ttmb = -1;
                firstBlock = 0;
            }
        }
        return 0;
    }
    /// <summary>
    /// <c>vc1_decode_b_mb_intfi</c> - one macroblock of a B picture in an interlaced field.
    /// </summary>
    /// <remarks>
    /// The macroblock mode says intra, one-vector or four-vector exactly as it does in a P
    /// field picture; what a B field picture adds is the <c>forward</c> bitplane, which when
    /// set fixes the macroblock forward and skips the vector-type symbol altogether. When it
    /// is clear the symbol can also mean <c>DIRECT</c>, which reads no differential at all,
    /// or <c>INTERPOLATED</c>, which reads a second one behind its own flag.
    /// </remarks>
    public static int Vc1DecodeBMbIntfi(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        ref GetBitContext gb = ref v.Gb;
        int mbPos = s.MbX + s.MbY * s.MbStride;
        int cbp = 0;
        int ttmb = v.Ttfrm;
        int mbHasCoeffs = 0;
        int firstBlock = 1;
        int bmvtype = Vc1Pred.BmvType.Backward;
        int blockCbp = 0, blockTt = 0;

        int mquant = v.Pq;           // Lossy initialization
        s.MbIntra = 0;

        short[] blocks = v.Blocks;
        byte[] mbType = v.MbTypeBase!;
        int mt = v.MbTypeOffset;
        short[] mvL1 = s.CurPic.MotionValL1!;
        int mo1 = s.CurPic.MotionValOffsetL1;
        short[] dcValBase = s.DcValBase!;
        int dcOff = s.DcValOffset;
        sbyte[] qscale = s.CurPic.QscaleTable!;
        int qt = s.CurPic.QscaleTableOffset;
        uint[] picMbType = s.CurPic.MbType!;
        int pmt = s.CurPic.MbTypeOffset;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;
        byte[] frame = s.CurPic.Frame!;

        Span<int> dmvX = stackalloc int[2];
        Span<int> dmvY = stackalloc int[2];
        Span<int> predFlag = stackalloc int[2];

        // VC1_IF_MBMODE_VLC_BITS is a placeholder for VC1_IF_MMV_MBMODE_VLC_BITS in the C.
        int idxMbmode = gb.GetVlc2(v.MbmodeVlc!, Vc1Data.IfMmvMbmodeVlcBits, 2);

        if (idxMbmode <= 1)
        {
            // intra MB
            v.IsIntraBase![v.IsIntraOffset + s.MbX] = 0x3f;   // all 1
            s.MbIntra = 1;
            mvL1[mo1 + s.BlockIndex[0] * 2] = 0;
            mvL1[mo1 + s.BlockIndex[0] * 2 + 1] = 0;
            picMbType[pmt + mbPos + v.MbOff] = Mpeg2.MbType.Intra;
            mquant = GetMquant(v, ref gb, mquant);
            qscale[qt + mbPos] = (sbyte)mquant;
            // Set DC scale - y and c use the same so we only set y
            s.YDcScale = Vc1Data.Wmv3DcScaleTable[Math.Abs(mquant)];
            s.AcPred = v.AcpredPlane![mbPos] = (byte)gb.GetBits1();
            mbHasCoeffs = idxMbmode & 1;
            if (mbHasCoeffs != 0)
                cbp = 1 + gb.GetVlc2(v.CbpcyVlc!, Vc1Data.IcbpcyVlcBits, 2);

            int dstIdx = 0;
            for (int i = 0; i < 6; i++)
            {
                v.AAvail = v.CAvail = 0;
                mbType[mt + s.BlockIndex[i]] = 1;
                dcValBase[dcOff + s.BlockIndex[i]] = 0;
                dstIdx += i >> 2;
                int val = (cbp >> (5 - i)) & 1;
                if (i == 2 || i == 3 || s.FirstSliceLine == 0)
                    v.AAvail = mbType[mt + s.BlockIndex[i] - s.BlockWrap[i]];
                if (i == 1 || i == 3 || s.MbX != 0)
                    v.CAvail = mbType[mt + s.BlockIndex[i] - 1];

                int ret = Vc1DecodeIntraBlock(v, blocks.AsSpan(i * 64, 64), i, val, mquant,
                                              (i & 4) != 0 ? v.Codingset2 : v.Codingset);
                if (ret < 0)
                    return ret;
                Vc1Dsp.Vc1InvTrans8x8(blocks.AsSpan(i * 64, 64));
                if (v.Rangeredfrm != 0)
                    for (int j = 0; j < 64; j++)
                        blocks[i * 64 + j] <<= 1;
                int off = BlockOffset(i, linesize);
                IdctDsp.PutSignedPixelsClamped(blocks.AsSpan(i * 64, 64), frame,
                                               s.Dest[dstIdx] + off,
                                               (i & 4) != 0 ? uvlinesize : linesize);
            }
        }
        else
        {
            s.MbIntra = 0;
            v.IsIntraBase![v.IsIntraOffset + s.MbX] = 0;
            picMbType[pmt + mbPos + v.MbOff] = (uint)Mpeg2.MbType.Size16X16;
            for (int i = 0; i < 6; i++)
                mbType[mt + s.BlockIndex[i]] = 0;

            int fwd = v.FmbIsRaw != 0
                ? v.ForwardMbPlane![mbPos] = (byte)gb.GetBits1()
                : v.ForwardMbPlane![mbPos];

            if (idxMbmode <= 5)
            {
                // 1-MV
                int interpmvp = 0;
                dmvX[0] = dmvX[1] = dmvY[0] = dmvY[1] = 0;
                predFlag[0] = predFlag[1] = 0;
                if (fwd != 0)
                {
                    bmvtype = Vc1Pred.BmvType.Forward;
                }
                else
                {
                    bmvtype = gb.Decode012();
                    switch (bmvtype)
                    {
                        case 0:
                            bmvtype = Vc1Pred.BmvType.Backward;
                            break;
                        case 1:
                            bmvtype = Vc1Pred.BmvType.Direct;
                            break;
                        case 2:
                            bmvtype = Vc1Pred.BmvType.Interpolated;
                            interpmvp = (int)gb.GetBits1();
                            break;
                    }
                }
                v.Bmvtype = bmvtype;
                if (bmvtype != Vc1Pred.BmvType.Direct && (idxMbmode & 1) != 0)
                {
                    int k = bmvtype == Vc1Pred.BmvType.Backward ? 1 : 0;
                    GetMvdataInterlaced(v, ref gb, out int dx, out int dy,
                                        ref predFlag[k], hasPredFlag: true);
                    dmvX[k] = dx;
                    dmvY[k] = dy;
                }
                if (interpmvp != 0)
                {
                    GetMvdataInterlaced(v, ref gb, out int dx, out int dy,
                                        ref predFlag[1], hasPredFlag: true);
                    dmvX[1] = dx;
                    dmvY[1] = dy;
                }
                if (bmvtype == Vc1Pred.BmvType.Direct)
                {
                    // Reproduced as written: the C clears pred_flag[0] twice and never
                    // pred_flag[1], which cannot matter because both are already zero on
                    // every path that reaches here - the one-vector arm zeroes them before
                    // the symbol is read, and DIRECT reads no differential.
                    dmvX[0] = dmvY[0] = predFlag[0] = 0;
                    dmvX[1] = dmvY[1] = predFlag[0] = 0;
                    if (s.BufferPools.Pool!.Picture(s.NextPic.Ptr).FieldPicture == 0)
                        return Vc1.AverrorInvalidData;
                }
                Vc1Pred.Vc1PredBMvIntfi(v, 0, dmvX, dmvY, 1, predFlag);
                Vc1BMc(v, dmvX, dmvY, bmvtype == Vc1Pred.BmvType.Direct ? 1 : 0, bmvtype);
                mbHasCoeffs = (idxMbmode & 2) == 0 ? 1 : 0;
            }
            else
            {
                // 4-MV
                if (fwd != 0)
                    bmvtype = Vc1Pred.BmvType.Forward;
                v.Bmvtype = bmvtype;
                v.Fourmvbp = (byte)gb.GetVlc2(v.FourmvbpVlc!,
                                              Vc1Data.FourMvBlockPatternVlcBits, 1);
                int back = bmvtype == Vc1Pred.BmvType.Backward ? 1 : 0;
                for (int i = 0; i < 4; i++)
                {
                    dmvX[0] = dmvY[0] = predFlag[0] = 0;
                    dmvX[1] = dmvY[1] = predFlag[1] = 0;
                    if ((v.Fourmvbp & (8 >> i)) != 0)
                    {
                        GetMvdataInterlaced(v, ref gb, out int dx, out int dy,
                                            ref predFlag[back], hasPredFlag: true);
                        dmvX[back] = dx;
                        dmvY[back] = dy;
                    }
                    Vc1Pred.Vc1PredBMvIntfi(v, i, dmvX, dmvY, 0, predFlag);
                    Vc1Mc.Vc1Mc4mvLuma(v, i, back, 0);
                }
                Vc1Mc.Vc1Mc4mvChroma(v, back);
                mbHasCoeffs = idxMbmode & 1;
            }

            if (mbHasCoeffs != 0)
                cbp = 1 + gb.GetVlc2(v.CbpcyVlc!, Vc1Data.CbpcyPVlcBits, 2);
            if (cbp != 0)
                mquant = GetMquant(v, ref gb, mquant);
            qscale[qt + mbPos] = (sbyte)mquant;
            if (v.Ttmbf == 0 && cbp != 0)
                ttmb = gb.GetVlc2(Vc1Data.Vc1TtmbVlc[v.TtIndex], Vc1Data.TtmbVlcBits, 2);

            int dstIdx2 = 0;
            for (int i = 0; i < 6; i++)
            {
                dcValBase[dcOff + s.BlockIndex[i]] = 0;
                dstIdx2 += i >> 2;
                int val = (cbp >> (5 - i)) & 1;
                int off = BlockOffset(i, linesize);
                if (val != 0)
                {
                    int pat = Vc1DecodePBlock(v, blocks.AsSpan(i * 64, 64), i, mquant, ttmb,
                                              firstBlock, frame, s.Dest[dstIdx2] + off,
                                              (i & 4) != 0 ? uvlinesize : linesize,
                                              0, ref blockTt, hasTtmbOut: true);
                    if (pat < 0)
                        return pat;
                    blockCbp |= pat << (i << 2);
                    if (v.Ttmbf == 0 && ttmb < 8)
                        ttmb = -1;
                    firstBlock = 0;
                }
            }
        }

        v.CbpBase![v.CbpOffset + s.MbX] = (uint)blockCbp;
        v.TtblkBase![v.TtblkOffset + s.MbX] = blockTt;

        return 0;
    }
    /// <summary>
    /// <c>vc1_decode_b_mb_intfr</c> - one macroblock of a B picture in an interlaced frame.
    /// </summary>
    /// <remarks>
    /// This is the widest of the macroblock decoders: the macroblock mode can ask for one
    /// vector or two field vectors, the picture can be in direct mode, the vector type can
    /// name either reference or both interpolated, and a two-vector macroblock can also carry
    /// a switch that sends its second pair at the <em>other</em> reference. Each combination
    /// is its own arm, and they differ in how many differentials they read, which predictor
    /// they call and which compensation entry point follows.
    /// </remarks>
    public static int Vc1DecodeBMbIntfr(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        ref GetBitContext gb = ref v.Gb;
        int mbPos = s.MbX + s.MbY * s.MbStride;
        int cbp = 0;
        int ttmb = v.Ttfrm;
        int mvsw = 0;
        int mbHasCoeffs = 1;
        int firstBlock = 1;
        int twomv = 0;
        int blockCbp = 0, blockTt = 0;
        int idxMbmode = 0;
        int fieldtx;
        int bmvtype = Vc1Pred.BmvType.Backward;

        int mquant = v.Pq;           // Lossy initialization
        s.MbIntra = 0;

        short[] blocks = v.Blocks;
        byte[] mbType = v.MbTypeBase!;
        int mt = v.MbTypeOffset;
        short[] mvL0 = s.CurPic.MotionValL0!;
        int mo0 = s.CurPic.MotionValOffsetL0;
        short[] mvL1 = s.CurPic.MotionValL1!;
        int mo1 = s.CurPic.MotionValOffsetL1;
        short[] nextL1 = s.NextPic.MotionValL1!;
        int nmo1 = s.NextPic.MotionValOffsetL1;
        byte[] blkMvType = v.BlkMvTypeBase!;
        int bmt = v.BlkMvTypeOffset;
        short[] dcValBase = s.DcValBase!;
        int dcOff = s.DcValOffset;
        sbyte[] qscale = s.CurPic.QscaleTable!;
        int qt = s.CurPic.QscaleTableOffset;
        uint[] picMbType = s.CurPic.MbType!;
        int pmt = s.CurPic.MbTypeOffset;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;
        byte[] frame = s.CurPic.Frame!;

        int skipped = v.SkipIsRaw != 0 ? (int)gb.GetBits1() : s.MbskipTable![mbPos];

        if (skipped == 0)
        {
            idxMbmode = gb.GetVlc2(v.MbmodeVlc!, Vc1Data.IntfrNon4MvMbmodeVlcBits, 2);
            if (MbmodeIntfrp(0, idxMbmode, 0) == (int)MbModesIntfr.TwoMvField)
            {
                twomv = 1;
                for (int i = 0; i < 4; i++)
                    blkMvType[bmt + s.BlockIndex[i]] = 1;
            }
            else
            {
                for (int i = 0; i < 4; i++)
                    blkMvType[bmt + s.BlockIndex[i]] = 0;
            }
        }

        if (MbmodeIntfrp(0, idxMbmode, 0) == (int)MbModesIntfr.Intra)
        {
            // intra MB
            for (int i = 0; i < 4; i++)
            {
                s.Mv[MvIndex(0, i, 0)] = mvL0[mo0 + s.BlockIndex[i] * 2] = 0;
                s.Mv[MvIndex(0, i, 1)] = mvL0[mo0 + s.BlockIndex[i] * 2 + 1] = 0;
                s.Mv[MvIndex(1, i, 0)] = mvL1[mo1 + s.BlockIndex[i] * 2] = 0;
                s.Mv[MvIndex(1, i, 1)] = mvL1[mo1 + s.BlockIndex[i] * 2 + 1] = 0;
            }
            v.IsIntraBase![v.IsIntraOffset + s.MbX] = 0x3f;   // all 1
            s.MbIntra = 1;
            picMbType[pmt + mbPos] = Mpeg2.MbType.Intra;
            fieldtx = v.FieldtxPlane![mbPos] = (byte)gb.GetBits1();
            mbHasCoeffs = (int)gb.GetBits1();
            if (mbHasCoeffs != 0)
                cbp = 1 + gb.GetVlc2(v.CbpcyVlc!, Vc1Data.CbpcyPVlcBits, 2);
            s.AcPred = v.AcpredPlane![mbPos] = (byte)gb.GetBits1();
            mquant = GetMquant(v, ref gb, mquant);
            qscale[qt + mbPos] = (sbyte)mquant;
            // Set DC scale - y and c use the same so we only set y
            s.YDcScale = Vc1Data.Wmv3DcScaleTable[Math.Abs(mquant)];

            int dstIdx = 0;
            for (int i = 0; i < 6; i++)
            {
                v.AAvail = v.CAvail = 0;
                mbType[mt + s.BlockIndex[i]] = 1;
                dcValBase[dcOff + s.BlockIndex[i]] = 0;
                dstIdx += i >> 2;
                int val = (cbp >> (5 - i)) & 1;
                if (i == 2 || i == 3 || s.FirstSliceLine == 0)
                    v.AAvail = mbType[mt + s.BlockIndex[i] - s.BlockWrap[i]];
                if (i == 1 || i == 3 || s.MbX != 0)
                    v.CAvail = mbType[mt + s.BlockIndex[i] - 1];

                int ret = Vc1DecodeIntraBlock(v, blocks.AsSpan(i * 64, 64), i, val, mquant,
                                              (i & 4) != 0 ? v.Codingset2 : v.Codingset);
                if (ret < 0)
                    return ret;
                Vc1Dsp.Vc1InvTrans8x8(blocks.AsSpan(i * 64, 64));

                int strideY, off;
                if (i < 4)
                {
                    strideY = linesize << fieldtx;
                    off = fieldtx != 0
                        ? ((i & 1) * 8) + ((i & 2) >> 1) * linesize
                        : (i & 1) * 8 + 4 * (i & 2) * linesize;
                }
                else
                {
                    strideY = uvlinesize;
                    off = 0;
                }
                IdctDsp.PutSignedPixelsClamped(blocks.AsSpan(i * 64, 64), frame,
                                               s.Dest[dstIdx] + off, strideY);
            }
        }
        else
        {
            s.MbIntra = 0;
            v.IsIntraBase![v.IsIntraOffset + s.MbX] = 0;

            int direct = v.DmbIsRaw != 0 ? (int)gb.GetBits1() : v.DirectMbPlane![mbPos];

            if (direct != 0)
            {
                int b0 = s.BlockIndex[0] * 2;
                s.Mv[MvIndex(0, 0, 0)] = (short)(mvL0[mo0 + b0] = (short)Vc1Pred.ScaleMv(
                    nextL1[nmo1 + b0], v.Bfraction, 0, s.QuarterSample));
                s.Mv[MvIndex(0, 0, 1)] = (short)(mvL0[mo0 + b0 + 1] = (short)Vc1Pred.ScaleMv(
                    nextL1[nmo1 + b0 + 1], v.Bfraction, 0, s.QuarterSample));
                s.Mv[MvIndex(1, 0, 0)] = (short)(mvL1[mo1 + b0] = (short)Vc1Pred.ScaleMv(
                    nextL1[nmo1 + b0], v.Bfraction, 1, s.QuarterSample));
                s.Mv[MvIndex(1, 0, 1)] = (short)(mvL1[mo1 + b0 + 1] = (short)Vc1Pred.ScaleMv(
                    nextL1[nmo1 + b0 + 1], v.Bfraction, 1, s.QuarterSample));

                if (twomv != 0)
                {
                    int b2 = s.BlockIndex[2] * 2;
                    s.Mv[MvIndex(0, 2, 0)] = (short)(mvL0[mo0 + b2] = (short)Vc1Pred.ScaleMv(
                        nextL1[nmo1 + b2], v.Bfraction, 0, s.QuarterSample));
                    s.Mv[MvIndex(0, 2, 1)] = (short)(mvL0[mo0 + b2 + 1] = (short)Vc1Pred.ScaleMv(
                        nextL1[nmo1 + b2 + 1], v.Bfraction, 0, s.QuarterSample));
                    s.Mv[MvIndex(1, 2, 0)] = (short)(mvL1[mo1 + b2] = (short)Vc1Pred.ScaleMv(
                        nextL1[nmo1 + b2], v.Bfraction, 1, s.QuarterSample));
                    s.Mv[MvIndex(1, 2, 1)] = (short)(mvL1[mo1 + b2 + 1] = (short)Vc1Pred.ScaleMv(
                        nextL1[nmo1 + b2 + 1], v.Bfraction, 1, s.QuarterSample));

                    for (int i = 1; i < 4; i += 2)
                    {
                        int bi = s.BlockIndex[i] * 2;
                        s.Mv[MvIndex(0, i, 0)] = mvL0[mo0 + bi] = (short)s.Mv[MvIndex(0, i - 1, 0)];
                        s.Mv[MvIndex(0, i, 1)] = mvL0[mo0 + bi + 1] = (short)s.Mv[MvIndex(0, i - 1, 1)];
                        s.Mv[MvIndex(1, i, 0)] = mvL1[mo1 + bi] = (short)s.Mv[MvIndex(1, i - 1, 0)];
                        s.Mv[MvIndex(1, i, 1)] = mvL1[mo1 + bi + 1] = (short)s.Mv[MvIndex(1, i - 1, 1)];
                    }
                }
                else
                {
                    for (int i = 1; i < 4; i++)
                    {
                        int bi = s.BlockIndex[i] * 2;
                        s.Mv[MvIndex(0, i, 0)] = mvL0[mo0 + bi] = (short)s.Mv[MvIndex(0, 0, 0)];
                        s.Mv[MvIndex(0, i, 1)] = mvL0[mo0 + bi + 1] = (short)s.Mv[MvIndex(0, 0, 1)];
                        s.Mv[MvIndex(1, i, 0)] = mvL1[mo1 + bi] = (short)s.Mv[MvIndex(1, 0, 0)];
                        s.Mv[MvIndex(1, i, 1)] = mvL1[mo1 + bi + 1] = (short)s.Mv[MvIndex(1, 0, 1)];
                    }
                }
            }

            if (direct == 0)
            {
                if (skipped != 0 || s.MbIntra == 0)
                {
                    bmvtype = gb.Decode012();
                    switch (bmvtype)
                    {
                        case 0:
                            // B_FRACTION_DEN is 256, so half of it is 128.
                            bmvtype = v.Bfraction >= 128 ? Vc1Pred.BmvType.Backward
                                                         : Vc1Pred.BmvType.Forward;
                            break;
                        case 1:
                            bmvtype = v.Bfraction >= 128 ? Vc1Pred.BmvType.Forward
                                                         : Vc1Pred.BmvType.Backward;
                            break;
                        case 2:
                            bmvtype = Vc1Pred.BmvType.Interpolated;
                            break;
                    }
                }

                if (twomv != 0 && bmvtype != Vc1Pred.BmvType.Interpolated)
                    mvsw = (int)gb.GetBits1();
            }

            if (skipped == 0)
            {
                // inter MB
                mbHasCoeffs = MbmodeIntfrp(0, idxMbmode, 3);
                if (mbHasCoeffs != 0)
                    cbp = 1 + gb.GetVlc2(v.CbpcyVlc!, Vc1Data.CbpcyPVlcBits, 2);
                if (direct == 0)
                {
                    if (bmvtype == Vc1Pred.BmvType.Interpolated && twomv != 0)
                        v.Fourmvbp = (byte)gb.GetVlc2(v.FourmvbpVlc!,
                                                      Vc1Data.FourMvBlockPatternVlcBits, 1);
                    else if (bmvtype == Vc1Pred.BmvType.Interpolated || twomv != 0)
                        v.Twomvbp = (byte)gb.GetVlc2(v.TwomvbpVlc!,
                                                     Vc1Data.TwoMvBlockPatternVlcBits, 1);
                }

                for (int i = 0; i < 6; i++)
                    mbType[mt + s.BlockIndex[i]] = 0;
                fieldtx = v.FieldtxPlane![mbPos] = (byte)MbmodeIntfrp(0, idxMbmode, 1);

                // for all motion vector read MVDATA and motion compensate each block
                int dstIdx = 0;
                if (direct != 0)
                {
                    if (twomv != 0)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            Vc1Mc.Vc1Mc4mvLuma(v, i, 0, 0);
                            Vc1Mc.Vc1Mc4mvLuma(v, i, 1, 1);
                        }
                        Vc1Mc.Vc1Mc4mvChroma4(v, 0, 0, 0);
                        Vc1Mc.Vc1Mc4mvChroma4(v, 1, 1, 1);
                    }
                    else
                    {
                        Vc1Mc.Vc1Mc1mv(v, 0);
                        Vc1Mc.Vc1InterpMc(v);
                    }
                }
                else if (twomv != 0 && bmvtype == Vc1Pred.BmvType.Interpolated)
                {
                    int mvbp = v.Fourmvbp;
                    for (int i = 0; i < 4; i++)
                    {
                        int dir = i == 1 || i == 3 ? 1 : 0;
                        int dmvX = 0, dmvY = 0, unusedFlag = 0;
                        int val = (mvbp >> (3 - i)) & 1;
                        if (val != 0)
                            GetMvdataInterlaced(v, ref gb, out dmvX, out dmvY,
                                                ref unusedFlag, hasPredFlag: false);
                        int j = i > 1 ? 2 : 0;
                        Vc1Pred.Vc1PredMvIntfr(v, j, dmvX, dmvY, 2, v.RangeX, v.RangeY, dir);
                        Vc1Mc.Vc1Mc4mvLuma(v, j, dir, dir);
                        Vc1Mc.Vc1Mc4mvLuma(v, j + 1, dir, dir);
                    }

                    Vc1Mc.Vc1Mc4mvChroma4(v, 0, 0, 0);
                    Vc1Mc.Vc1Mc4mvChroma4(v, 1, 1, 1);
                }
                else if (bmvtype == Vc1Pred.BmvType.Interpolated)
                {
                    int mvbp = v.Twomvbp;
                    int dmvX = 0, dmvY = 0, unusedFlag = 0;
                    if ((mvbp & 2) != 0)
                        GetMvdataInterlaced(v, ref gb, out dmvX, out dmvY,
                                            ref unusedFlag, hasPredFlag: false);

                    Vc1Pred.Vc1PredMvIntfr(v, 0, dmvX, dmvY, 1, v.RangeX, v.RangeY, 0);
                    Vc1Mc.Vc1Mc1mv(v, 0);

                    dmvX = dmvY = 0;
                    if ((mvbp & 1) != 0)
                        GetMvdataInterlaced(v, ref gb, out dmvX, out dmvY,
                                            ref unusedFlag, hasPredFlag: false);

                    Vc1Pred.Vc1PredMvIntfr(v, 0, dmvX, dmvY, 1, v.RangeX, v.RangeY, 1);
                    Vc1Mc.Vc1InterpMc(v);
                }
                else if (twomv != 0)
                {
                    int dir = bmvtype == Vc1Pred.BmvType.Backward ? 1 : 0;
                    int dir2 = dir;
                    if (mvsw != 0)
                        dir2 = dir == 0 ? 1 : 0;
                    int mvbp = v.Twomvbp;
                    int dmvX = 0, dmvY = 0, unusedFlag = 0;
                    if ((mvbp & 2) != 0)
                        GetMvdataInterlaced(v, ref gb, out dmvX, out dmvY,
                                            ref unusedFlag, hasPredFlag: false);
                    Vc1Pred.Vc1PredMvIntfr(v, 0, dmvX, dmvY, 2, v.RangeX, v.RangeY, dir);

                    dmvX = dmvY = 0;
                    if ((mvbp & 1) != 0)
                        GetMvdataInterlaced(v, ref gb, out dmvX, out dmvY,
                                            ref unusedFlag, hasPredFlag: false);
                    Vc1Pred.Vc1PredMvIntfr(v, 2, dmvX, dmvY, 2, v.RangeX, v.RangeY, dir2);

                    if (mvsw != 0)
                    {
                        short[] mvDir = dir != 0 ? mvL1 : mvL0;
                        int moDir = dir != 0 ? mo1 : mo0;
                        short[] mvDir2 = dir2 != 0 ? mvL1 : mvL0;
                        int moDir2 = dir2 != 0 ? mo1 : mo0;
                        for (int i = 0; i < 2; i++)
                        {
                            int bi = s.BlockIndex[i] * 2;
                            int bi2 = s.BlockIndex[i + 2] * 2;
                            s.Mv[MvIndex(dir, i + 2, 0)] = s.Mv[MvIndex(dir, i, 0)] =
                                mvDir[moDir + bi2] = mvDir[moDir + bi];
                            s.Mv[MvIndex(dir, i + 2, 1)] = s.Mv[MvIndex(dir, i, 1)] =
                                mvDir[moDir + bi2 + 1] = mvDir[moDir + bi + 1];
                            s.Mv[MvIndex(dir2, i + 2, 0)] = s.Mv[MvIndex(dir2, i, 0)] =
                                mvDir2[moDir2 + bi] = mvDir2[moDir2 + bi2];
                            s.Mv[MvIndex(dir2, i + 2, 1)] = s.Mv[MvIndex(dir2, i, 1)] =
                                mvDir2[moDir2 + bi + 1] = mvDir2[moDir2 + bi2 + 1];
                        }
                    }
                    else
                    {
                        int notDir = dir == 0 ? 1 : 0;
                        Vc1Pred.Vc1PredMvIntfr(v, 0, 0, 0, 2, v.RangeX, v.RangeY, notDir);
                        Vc1Pred.Vc1PredMvIntfr(v, 2, 0, 0, 2, v.RangeX, v.RangeY, notDir);
                    }

                    Vc1Mc.Vc1Mc4mvLuma(v, 0, dir, 0);
                    Vc1Mc.Vc1Mc4mvLuma(v, 1, dir, 0);
                    Vc1Mc.Vc1Mc4mvLuma(v, 2, dir2, 0);
                    Vc1Mc.Vc1Mc4mvLuma(v, 3, dir2, 0);
                    Vc1Mc.Vc1Mc4mvChroma4(v, dir, dir2, 0);
                }
                else
                {
                    int dir = bmvtype == Vc1Pred.BmvType.Backward ? 1 : 0;
                    int notDir = dir == 0 ? 1 : 0;

                    int mvbp = MbmodeIntfrp(0, idxMbmode, 2);
                    int dmvX = 0, dmvY = 0, unusedFlag = 0;
                    if (mvbp != 0)
                        GetMvdataInterlaced(v, ref gb, out dmvX, out dmvY,
                                            ref unusedFlag, hasPredFlag: false);

                    Vc1Pred.Vc1PredMvIntfr(v, 0, dmvX, dmvY, 1, v.RangeX, v.RangeY, dir);
                    for (int i = 0; i < 4; i++)
                        blkMvType[bmt + s.BlockIndex[i]] = 1;
                    Vc1Pred.Vc1PredMvIntfr(v, 0, 0, 0, 2, v.RangeX, v.RangeY, notDir);
                    short[] mvOther = notDir != 0 ? mvL1 : mvL0;
                    int moOther = notDir != 0 ? mo1 : mo0;
                    for (int i = 0; i < 2; i++)
                    {
                        int bi = s.BlockIndex[i] * 2;
                        int bi2 = s.BlockIndex[i + 2] * 2;
                        s.Mv[MvIndex(notDir, i + 2, 0)] = s.Mv[MvIndex(notDir, i, 0)] =
                            mvOther[moOther + bi2] = mvOther[moOther + bi];
                        s.Mv[MvIndex(notDir, i + 2, 1)] = s.Mv[MvIndex(notDir, i, 1)] =
                            mvOther[moOther + bi2 + 1] = mvOther[moOther + bi + 1];
                    }
                    Vc1Mc.Vc1Mc1mv(v, dir);
                }

                if (cbp != 0)
                    mquant = GetMquant(v, ref gb, mquant);   // p. 227
                qscale[qt + mbPos] = (sbyte)mquant;
                if (v.Ttmbf == 0 && cbp != 0)
                    ttmb = gb.GetVlc2(Vc1Data.Vc1TtmbVlc[v.TtIndex], Vc1Data.TtmbVlcBits, 2);
                for (int i = 0; i < 6; i++)
                {
                    dcValBase[dcOff + s.BlockIndex[i]] = 0;
                    dstIdx += i >> 2;
                    int val = (cbp >> (5 - i)) & 1;
                    int off = fieldtx == 0
                        ? ((i & 4) != 0 ? 0 : (i & 1) * 8 + (i & 2) * 4 * linesize)
                        : ((i & 4) != 0 ? 0 : (i & 1) * 8 + (i > 1 ? 1 : 0) * linesize);
                    if (val != 0)
                    {
                        int pat = Vc1DecodePBlock(
                            v, blocks.AsSpan(i * 64, 64), i, mquant, ttmb, firstBlock,
                            frame, s.Dest[dstIdx] + off,
                            (i & 4) != 0 ? uvlinesize : linesize << fieldtx,
                            0, ref blockTt, hasTtmbOut: true);
                        if (pat < 0)
                            return pat;
                        blockCbp |= pat << (i << 2);
                        if (v.Ttmbf == 0 && ttmb < 8)
                            ttmb = -1;
                        firstBlock = 0;
                    }
                }
            }
            else
            {
                // skipped
                int dir = 0;
                for (int i = 0; i < 6; i++)
                {
                    mbType[mt + s.BlockIndex[i]] = 0;
                    dcValBase[dcOff + s.BlockIndex[i]] = 0;
                }
                picMbType[pmt + mbPos] = Mpeg2.MbType.Skip;
                qscale[qt + mbPos] = 0;
                for (int i = 0; i < 4; i++)
                    blkMvType[bmt + s.BlockIndex[i]] = 0;

                if (direct == 0)
                {
                    if (bmvtype == Vc1Pred.BmvType.Interpolated)
                    {
                        Vc1Pred.Vc1PredMvIntfr(v, 0, 0, 0, 1, v.RangeX, v.RangeY, 0);
                        Vc1Pred.Vc1PredMvIntfr(v, 0, 0, 0, 1, v.RangeX, v.RangeY, 1);
                    }
                    else
                    {
                        dir = bmvtype == Vc1Pred.BmvType.Backward ? 1 : 0;
                        Vc1Pred.Vc1PredMvIntfr(v, 0, 0, 0, 1, v.RangeX, v.RangeY, dir);
                        if (mvsw != 0)
                        {
                            int dir2 = dir == 0 ? 1 : 0;
                            short[] mvDir = dir != 0 ? mvL1 : mvL0;
                            int moDir = dir != 0 ? mo1 : mo0;
                            short[] mvDir2 = dir2 != 0 ? mvL1 : mvL0;
                            int moDir2 = dir2 != 0 ? mo1 : mo0;
                            for (int i = 0; i < 2; i++)
                            {
                                int bi = s.BlockIndex[i] * 2;
                                int bi2 = s.BlockIndex[i + 2] * 2;
                                s.Mv[MvIndex(dir, i + 2, 0)] = s.Mv[MvIndex(dir, i, 0)] =
                                    mvDir[moDir + bi2] = mvDir[moDir + bi];
                                s.Mv[MvIndex(dir, i + 2, 1)] = s.Mv[MvIndex(dir, i, 1)] =
                                    mvDir[moDir + bi2 + 1] = mvDir[moDir + bi + 1];
                                s.Mv[MvIndex(dir2, i + 2, 0)] = s.Mv[MvIndex(dir2, i, 0)] =
                                    mvDir2[moDir2 + bi] = mvDir2[moDir2 + bi2];
                                s.Mv[MvIndex(dir2, i + 2, 1)] = s.Mv[MvIndex(dir2, i, 1)] =
                                    mvDir2[moDir2 + bi + 1] = mvDir2[moDir2 + bi2 + 1];
                            }
                        }
                        else
                        {
                            int notDir = dir == 0 ? 1 : 0;
                            for (int i = 0; i < 4; i++)
                                blkMvType[bmt + s.BlockIndex[i]] = 1;
                            Vc1Pred.Vc1PredMvIntfr(v, 0, 0, 0, 2, v.RangeX, v.RangeY, notDir);
                            short[] mvOther = notDir != 0 ? mvL1 : mvL0;
                            int moOther = notDir != 0 ? mo1 : mo0;
                            for (int i = 0; i < 2; i++)
                            {
                                int bi = s.BlockIndex[i] * 2;
                                int bi2 = s.BlockIndex[i + 2] * 2;
                                s.Mv[MvIndex(notDir, i + 2, 0)] = s.Mv[MvIndex(notDir, i, 0)] =
                                    mvOther[moOther + bi2] = mvOther[moOther + bi];
                                s.Mv[MvIndex(notDir, i + 2, 1)] = s.Mv[MvIndex(notDir, i, 1)] =
                                    mvOther[moOther + bi2 + 1] = mvOther[moOther + bi + 1];
                            }
                        }
                    }
                }

                Vc1Mc.Vc1Mc1mv(v, dir);
                if (direct != 0 || bmvtype == Vc1Pred.BmvType.Interpolated)
                    Vc1Mc.Vc1InterpMc(v);
                v.FieldtxPlane![mbPos] = 0;
            }
        }

        v.CbpBase![v.CbpOffset + s.MbX] = (uint)blockCbp;
        v.TtblkBase![v.TtblkOffset + s.MbX] = blockTt;

        return 0;
    }
    // --- The picture loops -----------------------------------------------------------------
    //
    // One per picture type and profile. Each selects the two AC coding sets from the table
    // indices and the quantiser index, then walks the macroblock grid, calling the macroblock
    // decoder for its type and the loop filter if the picture asks for one, and rotating the
    // four block-group indices so that each macroblock's coefficients are still readable while
    // its neighbours are decoded.

    /// <summary>
    /// The AC coding set an intra block reads through, from a table index and the quantiser
    /// index. All five picture loops choose it the same way.
    /// </summary>
    private static int IntraCodingSet(int tableIndex, int pqindex) => tableIndex switch
    {
        0 => pqindex <= 8 ? (int)CodingSet.HighRateIntra : (int)CodingSet.LowMotIntra,
        1 => (int)CodingSet.HighMotIntra,
        2 => (int)CodingSet.MidRateIntra,
        _ => throw new InvalidOperationException(
            $"VC-1: AC table index {tableIndex} is outside the two bits that carry it"),
    };

    /// <summary>The same for an inter block.</summary>
    private static int InterCodingSet(int tableIndex, int pqindex) => tableIndex switch
    {
        0 => pqindex <= 8 ? (int)CodingSet.HighRateInter : (int)CodingSet.LowMotInter,
        1 => (int)CodingSet.HighMotInter,
        2 => (int)CodingSet.MidRateInter,
        _ => throw new InvalidOperationException(
            $"VC-1: AC table index {tableIndex} is outside the two bits that carry it"),
    };

    /// <summary>Advances all four block-group indices by one macroblock.</summary>
    private static void RotateBlockGroups(Vc1Context v)
    {
        v.TopleftBlkIdx = IncBlkIdx(v, v.TopleftBlkIdx);
        v.TopBlkIdx = IncBlkIdx(v, v.TopBlkIdx);
        v.LeftBlkIdx = IncBlkIdx(v, v.LeftBlkIdx);
        v.CurBlkIdx = IncBlkIdx(v, v.CurBlkIdx);
    }

    /// <summary>
    /// <c>vc1_decode_i_blocks</c> - a whole I picture of a simple or main profile stream.
    /// </summary>
    public static void Vc1DecodeIBlocks(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;

        // select coding mode used for VLC tables selection
        v.Codingset = IntraCodingSet(v.YAcTableIndex, v.Pqindex);
        v.Codingset2 = InterCodingSet(v.CAcTableIndex, v.Pqindex);

        // Set DC scale - y and c use the same so we only set y
        s.YDcScale = Vc1Data.Wmv3DcScaleTable[v.Pq];

        short[] block = v.Block!;
        short[] mvL1 = s.CurPic.MotionValL1!;
        int mo1 = s.CurPic.MotionValOffsetL1;
        byte[] mbType = v.MbTypeBase!;
        int mt = v.MbTypeOffset;
        uint[] picMbType = s.CurPic.MbType!;
        int pmt = s.CurPic.MbTypeOffset;
        sbyte[] qscale = s.CurPic.QscaleTable!;
        int qt = s.CurPic.QscaleTableOffset;

        // do frame decode
        s.MbX = s.MbY = 0;
        s.MbIntra = 1;
        s.FirstSliceLine = 1;
        for (s.MbY = s.StartMbY; s.MbY < s.EndMbY; s.MbY++)
        {
            s.MbX = 0;
            InitBlockIndex(v);
            for (; s.MbX < v.EndMbX; s.MbX++)
            {
                UpdateBlockIndex(ref s);
                int cur = v.CurBlkIdx * 6 * 64;
                BlockDsp.ClearBlocks(block.AsSpan(cur, 6 * 64));
                int mbPos = s.MbX + s.MbY * s.MbWidth;
                picMbType[pmt + mbPos] = Mpeg2.MbType.Intra;
                qscale[qt + mbPos] = (sbyte)v.Pq;
                for (int i = 0; i < 4; i++)
                {
                    mvL1[mo1 + s.BlockIndex[i] * 2] = 0;
                    mvL1[mo1 + s.BlockIndex[i] * 2 + 1] = 0;
                }

                // do actual MB decoding and displaying
                int cbp = v.Gb.GetVlc2(MsMpeg4Vc1Data.Msmp4MbIVlc, MsMpeg4Vc1Data.MbIntraVlcBits, 2);
                s.AcPred = (byte)v.Gb.GetBits1();

                for (int k = 0; k < 6; k++)
                {
                    mbType[mt + s.BlockIndex[k]] = 1;

                    int val = (cbp >> (5 - k)) & 1;

                    if (k < 4)
                        val = Vc1CodedBlockPred(ref s, k, val);
                    cbp |= val << (5 - k);

                    int at = cur + BlockMap[k] * 64;
                    Vc1DecodeIBlock(v, block.AsSpan(at, 64), k, val,
                                    k < 4 ? v.Codingset : v.Codingset2);
                    Vc1Dsp.Vc1InvTrans8x8(block.AsSpan(at, 64));
                }

                if (v.Overlap != 0 && v.Pq >= 9)
                {
                    Vc1LoopFilter.Vc1IOverlapFilter(v);
                    if (v.Rangeredfrm != 0)
                        for (int k = 0; k < 6; k++)
                            for (int j = 0; j < 64; j++)
                                block[cur + BlockMap[k] * 64 + j] *= 2;
                    Vc1PutBlocksClamped(v, 1);
                }
                else
                {
                    if (v.Rangeredfrm != 0)
                        for (int k = 0; k < 6; k++)
                            for (int j = 0; j < 64; j++)
                            {
                                int at = cur + BlockMap[k] * 64 + j;
                                block[at] = (short)((block[at] - 64) * 2);
                            }
                    Vc1PutBlocksClamped(v, 0);
                }

                if (v.LoopFilter != 0)
                    Vc1LoopFilter.Vc1ILoopFilter(v);

                if (v.Gb.GetBitsLeft() < 0)
                {
                    ErrorResilience.ErAddSlice(ref s.Er, 0, 0, s.MbX, s.MbY, ErStatus.MbError);
                    return;
                }

                RotateBlockGroups(v);
            }

            s.FirstSliceLine = 0;
        }

        // This is intentionally mb_height and not end_mb_y - unlike in advanced profile,
        // these only differ are when decoding MSS2 rectangles.
        ErrorResilience.ErAddSlice(ref s.Er, 0, 0, s.MbWidth - 1, s.MbHeight - 1,
                                   ErStatus.MbEnd);
    }

    /// <summary>
    /// <c>vc1_decode_i_blocks_adv</c> - a whole I picture of an advanced profile stream, or a
    /// BI picture of one.
    /// </summary>
    /// <remarks>
    /// Where the simple and main profile loop takes the quantiser from the picture header and
    /// the AC prediction flag from the bitstream, this one re-reads the quantiser per
    /// macroblock and can take FIELDTX, the AC prediction flag and the overlap flag from
    /// bitplanes instead.
    /// </remarks>
    public static int Vc1DecodeIBlocksAdv(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        ref GetBitContext gb = ref v.Gb;

        if (gb.GetBitsLeft() <= 1)
            return Vc1.AverrorInvalidData;

        // select coding mode used for VLC tables selection
        v.Codingset = IntraCodingSet(v.YAcTableIndex, v.Pqindex);
        v.Codingset2 = InterCodingSet(v.CAcTableIndex, v.Pqindex);

        short[] block = v.Block!;
        short[] mvL1 = s.CurPic.MotionValL1!;
        int mo1 = s.CurPic.MotionValOffsetL1;
        byte[] mbType = v.MbTypeBase!;
        int mt = v.MbTypeOffset;
        uint[] picMbType = s.CurPic.MbType!;
        int pmt = s.CurPic.MbTypeOffset;
        sbyte[] qscale = s.CurPic.QscaleTable!;
        int qt = s.CurPic.QscaleTableOffset;
        byte[] coded = s.CodedBlockBase!;
        int cb = s.CodedBlockOffset;

        // do frame decode
        s.MbIntra = 1;
        s.FirstSliceLine = 1;
        s.MbX = 0;
        s.MbY = s.StartMbY;
        if (s.StartMbY != 0)
            coded.AsSpan(cb + (2 * s.MbY - 1) * s.B8Stride - 2, 1 + s.B8Stride).Clear();

        for (; s.MbY < s.EndMbY; s.MbY++)
        {
            s.MbX = 0;
            InitBlockIndex(v);
            for (; s.MbX < s.MbWidth; s.MbX++)
            {
                int mquant = v.Pq;
                UpdateBlockIndex(ref s);
                int cur = v.CurBlkIdx * 6 * 64;
                BlockDsp.ClearBlocks(block.AsSpan(cur, 6 * 64));
                int mbPos = s.MbX + s.MbY * s.MbStride;
                picMbType[pmt + mbPos + v.MbOff] = Mpeg2.MbType.Intra;
                for (int i = 0; i < 4; i++)
                {
                    mvL1[mo1 + (s.BlockIndex[i] + v.BlocksOff) * 2] = 0;
                    mvL1[mo1 + (s.BlockIndex[i] + v.BlocksOff) * 2 + 1] = 0;
                }

                // do actual MB decoding and displaying
                if (v.FieldtxIsRaw != 0)
                    v.FieldtxPlane![mbPos] = (byte)gb.GetBits1();
                if (gb.GetBitsLeft() <= 1)
                {
                    ErrorResilience.ErAddSlice(ref s.Er, 0, s.StartMbY, s.MbX, s.MbY,
                                               ErStatus.MbError);
                    return 0;
                }

                int cbp = gb.GetVlc2(MsMpeg4Vc1Data.Msmp4MbIVlc, MsMpeg4Vc1Data.MbIntraVlcBits, 2);
                s.AcPred = v.AcpredIsRaw != 0 ? (byte)gb.GetBits1() : v.AcpredPlane![mbPos];

                if (v.Condover == (byte)CoTypes.Select && v.OverflgIsRaw != 0)
                    v.OverFlagsPlane![mbPos] = (byte)gb.GetBits1();

                mquant = GetMquant(v, ref gb, mquant);

                qscale[qt + mbPos] = (sbyte)mquant;
                // Set DC scale - y and c use the same so we only set y
                s.YDcScale = Vc1Data.Wmv3DcScaleTable[Math.Abs(mquant)];

                for (int k = 0; k < 6; k++)
                {
                    mbType[mt + s.BlockIndex[k]] = 1;

                    int val = (cbp >> (5 - k)) & 1;

                    if (k < 4)
                        val = Vc1CodedBlockPred(ref s, k, val);
                    cbp |= val << (5 - k);

                    v.AAvail = s.FirstSliceLine == 0 || k == 2 || k == 3 ? 1 : 0;
                    v.CAvail = s.MbX != 0 || k == 1 || k == 3 ? 1 : 0;

                    int at = cur + BlockMap[k] * 64;
                    Vc1DecodeIBlockAdv(v, block.AsSpan(at, 64), k, val,
                                       k < 4 ? v.Codingset : v.Codingset2, mquant);
                    Vc1Dsp.Vc1InvTrans8x8(block.AsSpan(at, 64));
                }

                if (v.Overlap != 0 && (v.Pq >= 9 || v.Condover != (byte)CoTypes.None))
                    Vc1LoopFilter.Vc1IOverlapFilter(v);
                Vc1PutBlocksClamped(v, 1);
                if (v.LoopFilter != 0)
                    Vc1LoopFilter.Vc1ILoopFilter(v);

                if (gb.GetBitsLeft() < 0)
                {
                    // TODO: may need modification to handle slice coding
                    ErrorResilience.ErAddSlice(ref s.Er, 0, s.StartMbY, s.MbX, s.MbY,
                                               ErStatus.MbError);
                    return 0;
                }
                RotateBlockGroups(v);
            }
            s.FirstSliceLine = 0;
        }

        ErrorResilience.ErAddSlice(ref s.Er, 0, s.StartMbY << v.FieldMode, s.MbWidth - 1,
                                   (s.EndMbY << v.FieldMode) - 1, ErStatus.MbEnd);
        return 0;
    }

    /// <summary>
    /// <c>vc1_decode_p_blocks</c> - a whole P picture, whichever coding mode it is in.
    /// </summary>
    public static void Vc1DecodePBlocks(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;

        // select coding mode used for VLC tables selection
        // FFmpeg reads c_ac_table_index for both coding sets here, where the I picture loops
        // read y_ac_table_index for the first. Translated as written.
        v.Codingset = IntraCodingSet(v.CAcTableIndex, v.Pqindex);
        v.Codingset2 = InterCodingSet(v.CAcTableIndex, v.Pqindex);

        bool applyLoopFilter = v.LoopFilter != 0
                            && v.Avctx.SkipLoopFilter < AvDiscard.Nonkey;
        s.FirstSliceLine = 1;
        v.CbpBase!.AsSpan(0, 3 * s.MbStride).Clear();
        for (s.MbY = s.StartMbY; s.MbY < s.EndMbY; s.MbY++)
        {
            s.MbX = 0;
            InitBlockIndex(v);
            for (; s.MbX < s.MbWidth; s.MbX++)
            {
                UpdateBlockIndex(ref s);

                if ((v.Fcm == FrameCodingMode.IlaceField
                     || (v.Fcm == FrameCodingMode.Progressive && v.MvTypeIsRaw != 0)
                     || v.SkipIsRaw != 0)
                    && v.Gb.GetBitsLeft() <= 1)
                {
                    ErrorResilience.ErAddSlice(ref s.Er, 0, s.StartMbY, s.MbX, s.MbY,
                                               ErStatus.MbError);
                    return;
                }

                int ret;
                if (v.Fcm == FrameCodingMode.IlaceField)
                {
                    ret = Vc1DecodePMbIntfi(v);
                    if (applyLoopFilter)
                        Vc1LoopFilter.Vc1PLoopFilter(v);
                }
                else if (v.Fcm == FrameCodingMode.IlaceFrame)
                {
                    ret = Vc1DecodePMbIntfr(v);
                    if (applyLoopFilter)
                        Vc1LoopFilter.Vc1PIntfrLoopFilter(v);
                }
                else
                {
                    ret = Vc1DecodePMb(v);
                    if (applyLoopFilter)
                        Vc1LoopFilter.Vc1PLoopFilter(v);
                }
                if (ret < 0 || v.Gb.GetBitsLeft() < 0 || v.Gb.GetBitsCount() < 0)
                {
                    // TODO: may need modification to handle slice coding
                    ErrorResilience.ErAddSlice(ref s.Er, 0, s.StartMbY, s.MbX, s.MbY,
                                               ErStatus.MbError);
                    return;
                }
                RotateBlockGroups(v);
            }
            SlideRowState(v, ref s, slideLumaMv: true);
            s.FirstSliceLine = 0;
        }
        ErrorResilience.ErAddSlice(ref s.Er, 0, s.StartMbY << v.FieldMode, s.MbWidth - 1,
                                   (s.EndMbY << v.FieldMode) - 1, ErStatus.MbEnd);
    }

    /// <summary>
    /// <c>vc1_decode_b_blocks</c> - a whole B picture, whichever coding mode it is in.
    /// </summary>
    public static void Vc1DecodeBBlocks(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;

        // select coding mode used for VLC tables selection
        v.Codingset = IntraCodingSet(v.CAcTableIndex, v.Pqindex);
        v.Codingset2 = InterCodingSet(v.CAcTableIndex, v.Pqindex);

        s.FirstSliceLine = 1;
        for (s.MbY = s.StartMbY; s.MbY < s.EndMbY; s.MbY++)
        {
            s.MbX = 0;
            InitBlockIndex(v);
            for (; s.MbX < s.MbWidth; s.MbX++)
            {
                UpdateBlockIndex(ref s);

                if ((v.Fcm == FrameCodingMode.IlaceField || v.SkipIsRaw != 0
                     || v.DmbIsRaw != 0)
                    && v.Gb.GetBitsLeft() <= 1)
                {
                    ErrorResilience.ErAddSlice(ref s.Er, 0, s.StartMbY, s.MbX, s.MbY,
                                               ErStatus.MbError);
                    return;
                }

                if (v.Fcm == FrameCodingMode.IlaceField)
                {
                    Vc1DecodeBMbIntfi(v);
                    if (v.LoopFilter != 0)
                        Vc1LoopFilter.Vc1BIntfiLoopFilter(v);
                }
                else if (v.Fcm == FrameCodingMode.IlaceFrame)
                {
                    Vc1DecodeBMbIntfr(v);
                    if (v.LoopFilter != 0)
                        Vc1LoopFilter.Vc1PIntfrLoopFilter(v);
                }
                else
                {
                    Vc1DecodeBMb(v);
                    if (v.LoopFilter != 0)
                        Vc1LoopFilter.Vc1ILoopFilter(v);
                }
                if (v.Gb.GetBitsLeft() < 0 || v.Gb.GetBitsCount() < 0)
                {
                    // TODO: may need modification to handle slice coding
                    ErrorResilience.ErAddSlice(ref s.Er, 0, s.StartMbY, s.MbX, s.MbY,
                                               ErStatus.MbError);
                    return;
                }
            }
            // A B picture does not slide luma_mv: nothing reads it across rows here.
            SlideRowState(v, ref s, slideLumaMv: false);
            s.FirstSliceLine = 0;
        }
        ErrorResilience.ErAddSlice(ref s.Er, 0, s.StartMbY << v.FieldMode, s.MbWidth - 1,
                                   (s.EndMbY << v.FieldMode) - 1, ErStatus.MbEnd);
    }

    /// <summary>
    /// Slides the per-row arrays the interlaced predictors read two macroblock rows back, so
    /// that the current row's entries land where the next row expects its history.
    /// </summary>
    /// <remarks>
    /// FFmpeg does this with four <c>memmove</c>s of <c>2 * mb_stride</c> from
    /// <c>x - mb_stride</c> to <c>x_base</c>, and the regions overlap, so the copy direction
    /// matters: <see cref="Array.Copy(Array, int, Array, int, int)"/> has <c>memmove</c>
    /// semantics, which is what these need.
    /// </remarks>
    private static void SlideRowState(Vc1Context v, ref MpegEncContext s, bool slideLumaMv)
    {
        int n = 2 * s.MbStride;
        Array.Copy(v.CbpBase!, v.CbpOffset - s.MbStride, v.CbpBase, 0, n);
        Array.Copy(v.TtblkBase!, v.TtblkOffset - s.MbStride, v.TtblkBase!, 0, n);
        Array.Copy(v.IsIntraBase!, v.IsIntraOffset - s.MbStride, v.IsIntraBase!, 0, n);
        if (slideLumaMv)
            Array.Copy(v.LumaMvBase!, v.LumaMvOffset - s.MbStride * 2, v.LumaMvBase!, 0,
                       n * 2);
    }

    /// <summary>
    /// <c>vc1_decode_skip_blocks</c> - a P picture the header said carries no macroblocks at
    /// all, which is the previous picture repeated.
    /// </summary>
    public static void Vc1DecodeSkipBlocks(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;

        if (s.LastPic.Frame is null)
            return;

        ErrorResilience.ErAddSlice(ref s.Er, 0, s.StartMbY, s.MbWidth - 1, s.EndMbY - 1,
                                   ErStatus.MbEnd);
        s.FirstSliceLine = 1;
        byte[] frame = s.CurPic.Frame!;
        byte[] last = s.LastPic.Frame!;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;
        for (s.MbY = s.StartMbY; s.MbY < s.EndMbY; s.MbY++)
        {
            s.MbX = 0;
            InitBlockIndex(v);
            UpdateBlockIndex(ref s);
            last.AsSpan(s.LastPic.Data[0] + s.MbY * 16 * linesize, linesize * 16)
                .CopyTo(frame.AsSpan(s.Dest[0], linesize * 16));
            last.AsSpan(s.LastPic.Data[1] + s.MbY * 8 * uvlinesize, uvlinesize * 8)
                .CopyTo(frame.AsSpan(s.Dest[1], uvlinesize * 8));
            last.AsSpan(s.LastPic.Data[2] + s.MbY * 8 * uvlinesize, uvlinesize * 8)
                .CopyTo(frame.AsSpan(s.Dest[2], uvlinesize * 8));
            s.FirstSliceLine = 0;
        }
    }

    /// <summary>
    /// <c>ff_vc1_decode_blocks</c> - decodes the macroblocks of one picture, dispatching on
    /// its type and profile.
    /// </summary>
    public static void Vc1DecodeBlocks(Vc1Context v)
    {
        v.Esc3LevelLength = 0;
        if (v.X8Type != 0)
        {
            // WMV3's Complex Profile and WMV2 reach the IntraX8 decoder, and neither is in
            // scope. Silently producing a blank picture here would look like a decode.
            throw new NotSupportedException(
                "VC-1: the IntraX8 picture path is WMV3 Complex Profile or WMV2, "
              + "neither of which this decoder supports");
        }

        v.CurBlkIdx = 0;
        v.LeftBlkIdx = -1;
        v.TopleftBlkIdx = 1;
        v.TopBlkIdx = 2;
        switch (v.S.PictType)
        {
            case AvPictureType.I:
                if (v.Profile == (int)Vc1Profile.Advanced)
                    Vc1DecodeIBlocksAdv(v);
                else
                    Vc1DecodeIBlocks(v);
                break;
            case AvPictureType.P:
                if (v.PFrameSkipped != 0)
                    Vc1DecodeSkipBlocks(v);
                else
                    Vc1DecodePBlocks(v);
                break;
            case AvPictureType.B:
                if (v.BiType != 0)
                {
                    if (v.Profile == (int)Vc1Profile.Advanced)
                        Vc1DecodeIBlocksAdv(v);
                    else
                        Vc1DecodeIBlocks(v);
                }
                else
                {
                    Vc1DecodeBBlocks(v);
                }
                break;
            default:
                throw new InvalidOperationException(
                    $"VC-1: picture type {v.S.PictType} has no macroblock layer");
        }
    }
}

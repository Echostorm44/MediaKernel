using System;
using MediaKernel.Core.Codecs.Mpeg2;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Mpeg4Video;

/// <summary>
/// FFmpeg's <c>h263.c</c> and the inline helpers of <c>h263.h</c>: the part of H.263 that
/// MPEG-4 Part 2 and the MSMPEG-4 family share - motion-vector prediction, the write-back of a
/// macroblock's motion vector, and the loop filter's per-macroblock edge schedule.
/// </summary>
public static class H263
{
    /// <summary>
    /// <c>ff_h263_rl_inter</c> with its derived tables filled in, built on first use.
    /// </summary>
    /// <remarks>
    /// <c>ff_h263_init_rl_inter</c> is an <c>AVOnce</c> around <c>h263_init_rl_inter</c>, which
    /// calls <c>ff_rl_init</c> on a file-static store. A Lazy is the same thing: one
    /// initialisation however many decoders ask, and every caller gets the same table, which is
    /// the point - the derived tables are read-only after construction and are shared by
    /// reference.
    /// </remarks>
    private static readonly Lazy<RlTable> h263RlInter = new(() =>
    {
        RlTable rl = H263Data.H263RlInter();
        Rl.Init(rl);
        return rl;
    }, isThreadSafe: true);

    /// <summary>
    /// <c>ff_h263_init_rl_inter</c>, and the <c>h263_init_rl_inter</c> it runs once - the two
    /// are one member here because the only thing the outer function adds is the AVOnce that
    /// <see cref="Lazy{T}"/> already is.
    /// </summary>
    public static RlTable H263InitRlInter() => h263RlInter.Value;

    /// <summary>
    /// <c>ff_h263_round_chroma</c> - the chroma rounding table H.263 uses when it halves a
    /// luma motion vector.
    /// </summary>
    public static int H263RoundChroma(int x)
    {
        ReadOnlySpan<byte> h263ChromaRoundtab =
        [
            //  0  1  2  3  4  5  6  7  8  9 10 11 12 13 14 15
            0, 0, 0, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 1, 1,
        ];
        return h263ChromaRoundtab[x & 0xf] + (x >> 3);
    }

    /// <summary>
    /// <c>ff_h263_clean_intra_table_entries</c> - clears the prediction entries once per
    /// macroblock, and only the first time, which is what <c>mbintra_table</c> records.
    /// </summary>
    public static void H263CleanIntraTableEntries(ref MpegEncContext s, int xy)
    {
        if (s.MbintraTable![xy] != 0)
        {
            s.MbintraTable[xy] = 0;
            MpegVideoDec.CleanIntraTableEntries(ref s);
        }
    }

    /// <summary>
    /// <c>ff_h263_update_motion_val</c> - writes the macroblock's motion vector into all four
    /// 8x8 entries of the picture's motion-vector field.
    /// </summary>
    /// <remarks>
    /// The 8x8 case is absent on purpose: its four vectors were already stored as they were
    /// parsed, so there is nothing to broadcast.
    /// </remarks>
    public static void H263UpdateMotionVal(ref MpegEncContext s)
    {
        int mbXy = s.MbY * s.MbStride + s.MbX;
        int wrap = s.B8Stride;
        int xy = s.BlockIndex[0];

        if (s.MvType == MpegVideoConstants.MvType8X8)
            return;

        int motionX, motionY;

        if (s.MbIntra != 0)
        {
            motionX = 0;
            motionY = 0;
        }
        else if (s.MvType == MpegVideoConstants.MvType16X16)
        {
            motionX = s.Mv[MvIndex(0, 0, 0)];
            motionY = s.Mv[MvIndex(0, 0, 1)];
        }
        else /* MV_TYPE_FIELD */
        {
            motionX = s.Mv[MvIndex(0, 0, 0)] + s.Mv[MvIndex(0, 1, 0)];
            motionY = s.Mv[MvIndex(0, 0, 1)] + s.Mv[MvIndex(0, 1, 1)];
            // The C halves x but not y, and ORs the low bit back in so the halving rounds away
            // from zero rather than toward it.
            motionX = (motionX >> 1) | (motionX & 1);

            short[] fieldMv = s.PFieldMvTableBase!;
            for (int i = 0; i < 2; i++)
            {
                int bse = s.PFieldMvTableOffset[i * 2 + 0] + mbXy * 2;
                fieldMv[bse + 0] = (short)s.Mv[MvIndex(0, i, 0)];
                fieldMv[bse + 1] = (short)s.Mv[MvIndex(0, i, 1)];
            }

            sbyte[] refIndex = s.CurPic.RefIndexL0!;
            refIndex[4 * mbXy + 0] =
            refIndex[4 * mbXy + 1] = (sbyte)s.FieldSelect[FieldSelectIndex(0, 0)];
            refIndex[4 * mbXy + 2] =
            refIndex[4 * mbXy + 3] = (sbyte)s.FieldSelect[FieldSelectIndex(0, 1)];
        }

        short[] mv = s.CurPic.MotionValL0!;
        int mo = s.CurPic.MotionValOffsetL0;

        mv[mo + (xy) * 2 + 0] = (short)motionX;
        mv[mo + (xy) * 2 + 1] = (short)motionY;
        mv[mo + (xy + 1) * 2 + 0] = (short)motionX;
        mv[mo + (xy + 1) * 2 + 1] = (short)motionY;
        mv[mo + (xy + wrap) * 2 + 0] = (short)motionX;
        mv[mo + (xy + wrap) * 2 + 1] = (short)motionY;
        mv[mo + (xy + 1 + wrap) * 2 + 0] = (short)motionX;
        mv[mo + (xy + 1 + wrap) * 2 + 1] = (short)motionY;
    }

    /// <summary>
    /// <c>ff_h263_loop_filter</c> - the per-macroblock edge schedule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filter runs one macroblock behind the picture's leading edge, so each call filters
    /// the edges this macroblock shares with the ones already reconstructed above and to its
    /// left, plus its own interior cross. The C's comment names the four it reasons about:
    /// Diag, Top, Left and Center.
    /// </para>
    /// <para>
    /// A skipped macroblock contributes qp 0, which means "do not filter": the strength comes
    /// from whichever neighbour is not skipped, and when both are, the edge is left alone. The
    /// bottom row is the one place the C filters an edge twice, because there is no macroblock
    /// below to come back for it.
    /// </para>
    /// </remarks>
    public static void H263LoopFilter(ref MpegEncContext s)
    {
        int qpC;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;
        int xy = s.MbY * s.MbStride + s.MbX;

        Span<byte> frame = s.CurPic.Frame;
        int destY = s.Dest[0];
        int destCb = s.Dest[1];
        int destCr = s.Dest[2];

        uint[] mbType = s.CurPic.MbType!;
        int mt = s.CurPic.MbTypeOffset;
        sbyte[] qscaleTable = s.CurPic.QscaleTable!;
        int qt = s.CurPic.QscaleTableOffset;

        if (!MbType.IsSkip(mbType[mt + xy]))
        {
            qpC = s.Qscale;
            H263Dsp.H263VLoopFilterC(frame, destY + 8 * linesize, linesize, qpC);
            H263Dsp.H263VLoopFilterC(frame, destY + 8 * linesize + 8, linesize, qpC);
        }
        else
        {
            qpC = 0;
        }

        if (s.MbY != 0)
        {
            int qpTt;

            if (MbType.IsSkip(mbType[mt + xy - s.MbStride]))
                qpTt = 0;
            else
                qpTt = qscaleTable[qt + xy - s.MbStride];

            int qpTc = qpC != 0 ? qpC : qpTt;

            if (qpTc != 0)
            {
                int chromaQp = s.ChromaQscaleTable![qpTc];
                H263Dsp.H263VLoopFilterC(frame, destY, linesize, qpTc);
                H263Dsp.H263VLoopFilterC(frame, destY + 8, linesize, qpTc);

                H263Dsp.H263VLoopFilterC(frame, destCb, uvlinesize, chromaQp);
                H263Dsp.H263VLoopFilterC(frame, destCr, uvlinesize, chromaQp);
            }

            if (qpTt != 0)
                H263Dsp.H263HLoopFilterC(frame, destY - 8 * linesize + 8, linesize, qpTt);

            if (s.MbX != 0)
            {
                int qpDt;
                if (qpTt != 0 || MbType.IsSkip(mbType[mt + xy - 1 - s.MbStride]))
                    qpDt = qpTt;
                else
                    qpDt = qscaleTable[qt + xy - 1 - s.MbStride];

                if (qpDt != 0)
                {
                    int chromaQp = s.ChromaQscaleTable![qpDt];
                    H263Dsp.H263HLoopFilterC(frame, destY - 8 * linesize, linesize, qpDt);
                    H263Dsp.H263HLoopFilterC(frame, destCb - 8 * uvlinesize, uvlinesize, chromaQp);
                    H263Dsp.H263HLoopFilterC(frame, destCr - 8 * uvlinesize, uvlinesize, chromaQp);
                }
            }
        }

        if (qpC != 0)
        {
            H263Dsp.H263HLoopFilterC(frame, destY + 8, linesize, qpC);
            if (s.MbY + 1 == s.MbHeight)
                H263Dsp.H263HLoopFilterC(frame, destY + 8 * linesize + 8, linesize, qpC);
        }

        if (s.MbX != 0)
        {
            int qpLc;
            if (qpC != 0 || MbType.IsSkip(mbType[mt + xy - 1]))
                qpLc = qpC;
            else
                qpLc = qscaleTable[qt + xy - 1];

            if (qpLc != 0)
            {
                H263Dsp.H263HLoopFilterC(frame, destY, linesize, qpLc);
                if (s.MbY + 1 == s.MbHeight)
                {
                    int chromaQp = s.ChromaQscaleTable![qpLc];
                    H263Dsp.H263HLoopFilterC(frame, destY + 8 * linesize, linesize, qpLc);
                    H263Dsp.H263HLoopFilterC(frame, destCb, uvlinesize, chromaQp);
                    H263Dsp.H263HLoopFilterC(frame, destCr, uvlinesize, chromaQp);
                }
            }
        }
    }

    /// <summary>
    /// <c>ff_h263_pred_motion</c> - the median of the left, top and top-right motion vectors,
    /// with the special cases the first line of a slice needs.
    /// </summary>
    /// <returns>
    /// The index into <c>CurPic.MotionValL0/L1</c> of this block's own pair, which is where the
    /// caller writes the decoded vector. The C returns a pointer to it.
    /// </returns>
    /// <remarks>
    /// The <c>block == 2</c> branch <b>writes</b> through A, zeroing the left neighbour in the
    /// picture's motion-vector field rather than only in the prediction. That is not a slip in
    /// the C: the resync case has to make the vectors to the left of the marker unavailable to
    /// everything that reads them later, not just to this median.
    /// </remarks>
    public static int H263PredMotion(ref MpegEncContext s, int block, int dir,
                                     out int px, out int py)
    {
        ReadOnlySpan<int> off = [2, 1, 1, -1];

        int wrap = s.B8Stride;
        short[] mv = dir != 0 ? s.CurPic.MotionValL1! : s.CurPic.MotionValL0!;
        int motVal = (dir != 0 ? s.CurPic.MotionValOffsetL1 : s.CurPic.MotionValOffsetL0)
                   + s.BlockIndex[block] * 2;

        int a = motVal - 1 * 2;

        if (s.FirstSliceLine != 0 && block < 3)
        {
            // We cannot just change some MVs to simulate that, because the B-frames need them.
            if (block == 0)
            {
                if (s.MbX == s.ResyncMbX)
                {
                    px = py = 0;
                }
                else if (s.MbX + 1 == s.ResyncMbX && s.H263Pred != 0)
                {
                    int c = motVal + (off[block] - wrap) * 2;
                    if (s.MbX == 0)
                    {
                        px = mv[c + 0];
                        py = mv[c + 1];
                    }
                    else
                    {
                        px = MathOps.MidPred(mv[a + 0], 0, mv[c + 0]);
                        py = MathOps.MidPred(mv[a + 1], 0, mv[c + 1]);
                    }
                }
                else
                {
                    px = mv[a + 0];
                    py = mv[a + 1];
                }
            }
            else if (block == 1)
            {
                if (s.MbX + 1 == s.ResyncMbX && s.H263Pred != 0)
                {
                    int c = motVal + (off[block] - wrap) * 2;
                    px = MathOps.MidPred(mv[a + 0], 0, mv[c + 0]);
                    py = MathOps.MidPred(mv[a + 1], 0, mv[c + 1]);
                }
                else
                {
                    px = mv[a + 0];
                    py = mv[a + 1];
                }
            }
            else /* block == 2 */
            {
                int b = motVal - wrap * 2;
                int c = motVal + (off[block] - wrap) * 2;
                if (s.MbX == s.ResyncMbX)
                    mv[a + 0] = mv[a + 1] = 0;

                px = MathOps.MidPred(mv[a + 0], mv[b + 0], mv[c + 0]);
                py = MathOps.MidPred(mv[a + 1], mv[b + 1], mv[c + 1]);
            }
        }
        else
        {
            int b = motVal - wrap * 2;
            int c = motVal + (off[block] - wrap) * 2;
            px = MathOps.MidPred(mv[a + 0], mv[b + 0], mv[c + 0]);
            py = MathOps.MidPred(mv[a + 1], mv[b + 1], mv[c + 1]);
        }

        return motVal;
    }

    /// <summary><c>mv[i][j][k]</c> on the context.</summary>
    private static int MvIndex(int i, int j, int k) => i * 8 + j * 2 + k;

    /// <summary><c>field_select[i][j]</c>.</summary>
    private static int FieldSelectIndex(int i, int j) => i * 2 + j;
}

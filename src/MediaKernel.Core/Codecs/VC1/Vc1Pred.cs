// VC-1's motion vector prediction - a translation of ffmpeg-master/libavcodec/vc1_pred.c and
// the scale_mv in vc1_pred.h.
//
// Four predictors, one per picture structure: progressive and interlaced field (ff_vc1_pred_mv),
// interlaced frame (ff_vc1_pred_mv_intfr), progressive B (ff_vc1_pred_b_mv) and interlaced
// field B (ff_vc1_pred_b_mv_intfi). What they share is the three neighbours A, B and C, a
// median, and a pullback that keeps the vector inside the picture; what differs is which
// neighbours exist and how a vector from the other field is rescaled to this one.
//
// FFmpeg's motion_val is int16_t (*)[2] - an array of pairs - and here it is a flat short[]
// with the pair index doubled, which is how MPVWorkPicture already carries it. Every "xy" in
// the C is therefore "xy * 2" here when it indexes a vector and plain when it indexes one of
// the per-block byte planes.

using System;
using System.Runtime.CompilerServices;
using MediaKernel.Core.Codecs.Mpeg2;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Vc1Video;

/// <summary>
/// FFmpeg's <c>vc1_pred.c</c>: motion vector prediction for the four VC-1 picture structures.
/// </summary>
public static class Vc1Pred
{
    /// <summary>The <c>BMV_TYPE_*</c> values a B macroblock's motion can take.</summary>
    public static class BmvType
    {
        /// <summary><c>BMV_TYPE_BACKWARD</c>.</summary>
        public const int Backward = 0;

        /// <summary><c>BMV_TYPE_FORWARD</c>.</summary>
        public const int Forward = 1;

        /// <summary><c>BMV_TYPE_INTERPOLATED</c>.</summary>
        public const int Interpolated = 2;

        /// <summary><c>BMV_TYPE_DIRECT</c>.</summary>
        public const int Direct = 3;
    }

    /// <summary>
    /// <c>scale_mv</c> (vc1_pred.h) - scale a vector of the next picture by BFRACTION, for
    /// direct mode. <paramref name="inv"/> selects the backward half of the split.
    /// </summary>
    /// <remarks>
    /// Only the <c>B_FRACTION_DEN == 256</c> arm is translated, which is the live one:
    /// vc1data.h defines B_FRACTION_DEN as 256 and the other arm is dead code there too.
    /// </remarks>
    public static int ScaleMv(int value, int bfrac, int inv, int qs)
    {
        int n = bfrac;

        if (inv != 0)
            n -= 256;
        if (qs == 0)
            return 2 * ((value * n + 255) >> 9);
        return (value * n + 128) >> 8;
    }

    /// <summary>
    /// <c>cur_pic.motion_val[dir]</c> - the plane as a span already sliced at its offset, so
    /// that an index here means what the same index means in the C.
    /// </summary>
    /// <remarks>
    /// FFmpeg's <c>motion_val</c> is <c>int16_t (*)[2]</c>, an interior pointer into a larger
    /// allocation, and this port carries it as a base array plus an offset in elements.
    /// Slicing at the offset is what lets every expression below keep the C's index
    /// arithmetic, including the reads two blocks back and two rows up.
    /// </remarks>
    private static MvPlane MotionVal(ref MpegEncContext s, int dir) =>
        dir == 0
            ? new MvPlane(s.CurPic.MotionValL0, s.CurPic.MotionValOffsetL0)
            : new MvPlane(s.CurPic.MotionValL1, s.CurPic.MotionValOffsetL1);

    /// <summary><c>next_pic.motion_val[dir]</c>, likewise.</summary>
    private static MvPlane NextMotionVal(ref MpegEncContext s, int dir) =>
        dir == 0
            ? new MvPlane(s.NextPic.MotionValL0, s.NextPic.MotionValOffsetL0)
            : new MvPlane(s.NextPic.MotionValL1, s.NextPic.MotionValOffsetL1);

    /// <summary>
    /// A motion vector plane at its interior offset, so an index here means what the same
    /// index means in the C - negative ones included.
    /// </summary>
    /// <remarks>
    /// A slice at the offset would not do: the four pairs of slack that
    /// <c>ff_mpv_init_context_frame</c> puts in front of <c>motion_val</c> exist precisely so
    /// that <c>motion_val[xy - 2]</c> is legal at the left edge, and a span sliced at the
    /// offset cannot be indexed below zero. Adding the offset per access keeps both.
    /// </remarks>
    private readonly ref struct MvPlane(Span<short> data, int offset)
    {
        private readonly Span<short> data = data;
        private readonly int offset = offset;

        public short this[int i]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => data[offset + i];
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => data[offset + i] = value;
        }
    }

    /// <summary><c>av_clip</c>.</summary>
    private static int Clip(int a, int amin, int amax) => a < amin ? amin : a > amax ? amax : a;

    // --- the field rescalers, 10.3.5.4.3 -----------------------------------------------------

    /// <summary>
    /// <c>scaleforsame_x</c> - rescale a horizontal vector that points at the same field.
    /// </summary>
    private static int ScaleforsameX(Vc1Context v, int n, int dir)
    {
        int tableIndex = dir ^ v.SecondField;
        int refdist = v.S.PictType != AvPictureType.B ? v.Refdist : (dir != 0 ? v.Brfd : v.Frfd);
        if (refdist > 3)
            refdist = 3;

        // ff_vc1_field_mvpred_scales is [2][7][4], flattened.
        int b = tableIndex * 28;
        int scalesame1 = Vc1Data.Vc1FieldMvpredScales[b + 1 * 4 + refdist];
        int scalesame2 = Vc1Data.Vc1FieldMvpredScales[b + 2 * 4 + refdist];
        int scalezone1X = Vc1Data.Vc1FieldMvpredScales[b + 3 * 4 + refdist];
        int zone1offsetX = Vc1Data.Vc1FieldMvpredScales[b + 5 * 4 + refdist];

        int scaledvalue;
        if (Math.Abs(n) > 255)
        {
            scaledvalue = n;
        }
        else if (Math.Abs(n) < scalezone1X)
        {
            scaledvalue = (n * scalesame1) >> 8;
        }
        else if (n < 0)
        {
            scaledvalue = ((n * scalesame2) >> 8) - zone1offsetX;
        }
        else
        {
            scaledvalue = ((n * scalesame2) >> 8) + zone1offsetX;
        }
        return Clip(scaledvalue, -v.RangeX, v.RangeX - 1);
    }

    /// <summary><c>scaleforsame_y</c>.</summary>
    private static int ScaleforsameY(Vc1Context v, int n, int dir)
    {
        int tableIndex = dir ^ v.SecondField;
        int refdist = v.S.PictType != AvPictureType.B ? v.Refdist : (dir != 0 ? v.Brfd : v.Frfd);
        if (refdist > 3)
            refdist = 3;

        int b = tableIndex * 28;
        int scalesame1 = Vc1Data.Vc1FieldMvpredScales[b + 1 * 4 + refdist];
        int scalesame2 = Vc1Data.Vc1FieldMvpredScales[b + 2 * 4 + refdist];
        int scalezone1Y = Vc1Data.Vc1FieldMvpredScales[b + 4 * 4 + refdist];
        int zone1offsetY = Vc1Data.Vc1FieldMvpredScales[b + 6 * 4 + refdist];

        int scaledvalue;
        if (Math.Abs(n) > 63)
        {
            scaledvalue = n;
        }
        else if (Math.Abs(n) < scalezone1Y)
        {
            scaledvalue = (n * scalesame1) >> 8;
        }
        else if (n < 0)
        {
            scaledvalue = ((n * scalesame2) >> 8) - zone1offsetY;
        }
        else
        {
            scaledvalue = ((n * scalesame2) >> 8) + zone1offsetY;
        }

        return v.CurFieldType != 0 && v.RefFieldType[dir] == 0
            ? Clip(scaledvalue, -v.RangeY / 2 + 1, v.RangeY / 2)
            : Clip(scaledvalue, -v.RangeY / 2, v.RangeY / 2 - 1);
    }

    /// <summary>
    /// <c>scaleforopp_x</c> - rescale a horizontal vector that points at the other field.
    /// </summary>
    private static int ScaleforoppX(Vc1Context v, int n)
    {
        int brfd = Math.Min(v.Brfd, 3);
        // ff_vc1_b_field_mvpred_scales is [7][4], flattened.
        int scalezone1X = Vc1Data.Vc1BFieldMvpredScales[3 * 4 + brfd];
        int zone1offsetX = Vc1Data.Vc1BFieldMvpredScales[5 * 4 + brfd];
        int scaleopp1 = Vc1Data.Vc1BFieldMvpredScales[1 * 4 + brfd];
        int scaleopp2 = Vc1Data.Vc1BFieldMvpredScales[2 * 4 + brfd];

        int scaledvalue;
        if (Math.Abs(n) > 255)
        {
            scaledvalue = n;
        }
        else if (Math.Abs(n) < scalezone1X)
        {
            scaledvalue = (n * scaleopp1) >> 8;
        }
        else if (n < 0)
        {
            scaledvalue = ((n * scaleopp2) >> 8) - zone1offsetX;
        }
        else
        {
            scaledvalue = ((n * scaleopp2) >> 8) + zone1offsetX;
        }
        return Clip(scaledvalue, -v.RangeX, v.RangeX - 1);
    }

    /// <summary><c>scaleforopp_y</c>.</summary>
    private static int ScaleforoppY(Vc1Context v, int n, int dir)
    {
        int brfd = Math.Min(v.Brfd, 3);
        int scalezone1Y = Vc1Data.Vc1BFieldMvpredScales[4 * 4 + brfd];
        int zone1offsetY = Vc1Data.Vc1BFieldMvpredScales[6 * 4 + brfd];
        int scaleopp1 = Vc1Data.Vc1BFieldMvpredScales[1 * 4 + brfd];
        int scaleopp2 = Vc1Data.Vc1BFieldMvpredScales[2 * 4 + brfd];

        int scaledvalue;
        if (Math.Abs(n) > 63)
        {
            scaledvalue = n;
        }
        else if (Math.Abs(n) < scalezone1Y)
        {
            scaledvalue = (n * scaleopp1) >> 8;
        }
        else if (n < 0)
        {
            scaledvalue = ((n * scaleopp2) >> 8) - zone1offsetY;
        }
        else
        {
            scaledvalue = ((n * scaleopp2) >> 8) + zone1offsetY;
        }

        return v.CurFieldType != 0 && v.RefFieldType[dir] == 0
            ? Clip(scaledvalue, -v.RangeY / 2 + 1, v.RangeY / 2)
            : Clip(scaledvalue, -v.RangeY / 2, v.RangeY / 2 - 1);
    }

    /// <summary>
    /// <c>scaleforsame</c> - the same-field rescale, at whatever pel resolution the picture
    /// uses. <paramref name="dim"/> is 0 for x and 1 for y.
    /// </summary>
    private static int Scaleforsame(Vc1Context v, int n, int dim, int dir)
    {
        int hpel = 1 - v.S.QuarterSample;

        n >>= hpel;
        if (v.S.PictType != AvPictureType.B || v.SecondField != 0 || dir == 0)
        {
            n = dim != 0
                ? ScaleforsameY(v, n, dir) * (1 << hpel)
                : ScaleforsameX(v, n, dir) * (1 << hpel);
            return n;
        }
        int brfd = Math.Min(v.Brfd, 3);
        int scalesame = Vc1Data.Vc1BFieldMvpredScales[0 * 4 + brfd];

        n = (n * scalesame >> 8) * (1 << hpel);
        return n;
    }

    /// <summary><c>scaleforopp</c>.</summary>
    private static int Scaleforopp(Vc1Context v, int n, int dim, int dir)
    {
        int hpel = 1 - v.S.QuarterSample;

        n >>= hpel;
        if (v.S.PictType == AvPictureType.B && v.SecondField == 0 && dir == 1)
        {
            n = dim != 0
                ? ScaleforoppY(v, n, dir) * (1 << hpel)
                : ScaleforoppX(v, n) * (1 << hpel);
            return n;
        }
        int refdist = v.S.PictType != AvPictureType.B ? v.Refdist : (dir != 0 ? v.Brfd : v.Frfd);
        refdist = Math.Min(refdist, 3);
        int scaleopp = Vc1Data.Vc1FieldMvpredScales[(dir ^ v.SecondField) * 28 + 0 * 4 + refdist];

        n = (n * scaleopp >> 8) * (1 << hpel);
        return n;
    }

    // --- the predictors ------------------------------------------------------------------------

    /// <summary>
    /// <c>ff_vc1_pred_mv</c> - predict and store the motion vector of a progressive or
    /// interlaced field macroblock or block.
    /// </summary>
    /// <param name="isIntra">
    /// <c>is_intra</c>, which the C passes as a bare pointer; the caller's plane and the offset
    /// it is indexed from.
    /// </param>
    public static void Vc1PredMv(Vc1Context v, int n, int dmvX, int dmvY, int mv1,
                                 int rX, int rY, byte[] isIntra, int isIntraOffset,
                                 int predFlag, int dir)
    {
        ref MpegEncContext s = ref v.S;
        int off = 0;
        int px, py;
        int sum;
        int numSamefield = 0, numOppfield = 0;
        int aF, bF, cF;
        Span<short> fieldPredA = stackalloc short[2];
        Span<short> fieldPredB = stackalloc short[2];
        Span<short> fieldPredC = stackalloc short[2];
        int yBias = 0;

        int mixedmvPic =
            v.MvMode == (byte)MvModes.MixedMv ||
            (v.MvMode == (byte)MvModes.IntensityComp && v.MvMode2 == (byte)MvModes.MixedMv)
                ? 1 : 0;

        // Scale MV difference to be quad-pel.
        if (s.QuarterSample == 0)
        {
            dmvX *= 2;
            dmvY *= 2;
        }

        int wrap = s.B8Stride;
        int xy = s.BlockIndex[n];

        MvPlane mvCur = MotionVal(ref s, 0);
        MvPlane mvOther = MotionVal(ref s, 1);

        if (s.MbIntra != 0)
        {
            s.Mv[0 * 8 + n * 2 + 0] = 0;
            s.Mv[0 * 8 + n * 2 + 1] = 0;
            Pair(mvCur, xy + v.BlocksOff, 0, 0);
            Pair(mvOther, xy + v.BlocksOff, 0, 0);
            if (mv1 != 0)  // Duplicate motion data for a 1-MV block.
            {
                Pair(mvCur, xy + 1 + v.BlocksOff, 0, 0);
                Pair(mvCur, xy + wrap + v.BlocksOff, 0, 0);
                Pair(mvCur, xy + wrap + 1 + v.BlocksOff, 0, 0);
                v.LumaMvBase![v.LumaMvOffset + s.MbX * 2 + 0] = 0;
                v.LumaMvBase[v.LumaMvOffset + s.MbX * 2 + 1] = 0;
                Pair(mvOther, xy + 1 + v.BlocksOff, 0, 0);
                Pair(mvOther, xy + wrap + v.BlocksOff, 0, 0);
                Pair(mvOther, xy + wrap + 1 + v.BlocksOff, 0, 0);
            }
            return;
        }

        bool aValid = s.FirstSliceLine == 0 || n == 2 || n == 3;
        bool bValid = aValid;
        bool cValid = s.MbX != 0 || n == 1 || n == 3;
        if (mv1 != 0)
        {
            if (v.FieldMode != 0 && mixedmvPic != 0)
                off = s.MbX == s.MbWidth - 1 ? -2 : 2;
            else
                off = s.MbX == s.MbWidth - 1 ? -1 : 2;
            bValid = bValid && s.MbWidth > 1;
        }
        else
        {
            // In 4-MV mode different blocks have different B predictor positions.
            switch (n)
            {
                case 0:
                    off = v.ResRtmFlag != 0
                        ? (s.MbX != 0 ? -1 : 1)
                        : (s.MbX != 0 ? -1 : 2 * s.MbWidth - wrap - 1);
                    break;
                case 1:
                    off = s.MbX == s.MbWidth - 1 ? -1 : 1;
                    break;
                case 2:
                    off = 1;
                    break;
                case 3:
                    off = -1;
                    break;
                default:
                    break;
            }
            if (v.FieldMode != 0 && s.MbWidth == 1)
                bValid = bValid && cValid;
        }

        if (v.FieldMode != 0)
        {
            aValid = aValid && isIntra[isIntraOffset + xy - wrap] == 0;
            bValid = bValid && isIntra[isIntraOffset + xy - wrap + off] == 0;
            cValid = cValid && isIntra[isIntraOffset + xy - 1] == 0;
        }

        MvPlane mvDir = MotionVal(ref s, dir);
        byte[] mvF = v.MvFBase!;
        int mvFOff = v.MvFOffset[dir];

        if (aValid)
        {
            int a = (xy - wrap + v.BlocksOff) * 2;
            aF = mvF[mvFOff + xy - wrap + v.BlocksOff];
            numOppfield += aF;
            numSamefield += 1 - aF;
            fieldPredA[0] = mvDir[a];
            fieldPredA[1] = mvDir[a + 1];
        }
        else
        {
            fieldPredA[0] = fieldPredA[1] = 0;
            aF = 0;
        }
        if (bValid)
        {
            int b = (xy - wrap + off + v.BlocksOff) * 2;
            bF = mvF[mvFOff + xy - wrap + off + v.BlocksOff];
            numOppfield += bF;
            numSamefield += 1 - bF;
            fieldPredB[0] = mvDir[b];
            fieldPredB[1] = mvDir[b + 1];
        }
        else
        {
            fieldPredB[0] = fieldPredB[1] = 0;
            bF = 0;
        }
        if (cValid)
        {
            int c = (xy - 1 + v.BlocksOff) * 2;
            cF = mvF[mvFOff + xy - 1 + v.BlocksOff];
            numOppfield += cF;
            numSamefield += 1 - cF;
            fieldPredC[0] = mvDir[c];
            fieldPredC[1] = mvDir[c + 1];
        }
        else
        {
            fieldPredC[0] = fieldPredC[1] = 0;
            cF = 0;
        }

        int opposite;
        if (v.FieldMode != 0)
        {
            if (v.Numref == 0)
            {
                // REFFIELD determines whether the last field or the second-last is the
                // reference.
                opposite = 1 - v.Reffield;
            }
            else
            {
                opposite = numSamefield <= numOppfield ? 1 - predFlag : predFlag;
            }
        }
        else
        {
            opposite = 0;
        }

        if (opposite != 0)
        {
            mvF[mvFOff + xy + v.BlocksOff] = 1;
            v.RefFieldType[dir] = v.CurFieldType != 0 ? 0 : 1;
            if (aValid && aF == 0)
            {
                fieldPredA[0] = (short)Scaleforopp(v, fieldPredA[0], 0, dir);
                fieldPredA[1] = (short)Scaleforopp(v, fieldPredA[1], 1, dir);
            }
            if (bValid && bF == 0)
            {
                fieldPredB[0] = (short)Scaleforopp(v, fieldPredB[0], 0, dir);
                fieldPredB[1] = (short)Scaleforopp(v, fieldPredB[1], 1, dir);
            }
            if (cValid && cF == 0)
            {
                fieldPredC[0] = (short)Scaleforopp(v, fieldPredC[0], 0, dir);
                fieldPredC[1] = (short)Scaleforopp(v, fieldPredC[1], 1, dir);
            }
        }
        else
        {
            mvF[mvFOff + xy + v.BlocksOff] = 0;
            v.RefFieldType[dir] = v.CurFieldType;
            if (aValid && aF != 0)
            {
                fieldPredA[0] = (short)Scaleforsame(v, fieldPredA[0], 0, dir);
                fieldPredA[1] = (short)Scaleforsame(v, fieldPredA[1], 1, dir);
            }
            if (bValid && bF != 0)
            {
                fieldPredB[0] = (short)Scaleforsame(v, fieldPredB[0], 0, dir);
                fieldPredB[1] = (short)Scaleforsame(v, fieldPredB[1], 1, dir);
            }
            if (cValid && cF != 0)
            {
                fieldPredC[0] = (short)Scaleforsame(v, fieldPredC[0], 0, dir);
                fieldPredC[1] = (short)Scaleforsame(v, fieldPredC[1], 1, dir);
            }
        }

        if (aValid)
        {
            px = fieldPredA[0];
            py = fieldPredA[1];
        }
        else if (cValid)
        {
            px = fieldPredC[0];
            py = fieldPredC[1];
        }
        else if (bValid)
        {
            px = fieldPredB[0];
            py = fieldPredB[1];
        }
        else
        {
            px = 0;
            py = 0;
        }

        if (numSamefield + numOppfield > 1)
        {
            px = MathOps.MidPred(fieldPredA[0], fieldPredB[0], fieldPredC[0]);
            py = MathOps.MidPred(fieldPredA[1], fieldPredB[1], fieldPredC[1]);
        }

        // Pullback MV as specified in 8.3.5.3.4.
        if (v.FieldMode == 0)
        {
            int mv = mv1 != 0 ? -60 : -28;
            int qx = (s.MbX << 6) + (n == 1 || n == 3 ? 32 : 0);
            int qy = (s.MbY << 6) + (n == 2 || n == 3 ? 32 : 0);
            int bigX = (s.MbWidth << 6) - 4;
            int bigY = (s.MbHeight << 6) - 4;
            if (qx + px < mv) px = mv - qx;
            if (qy + py < mv) py = mv - qy;
            if (qx + px > bigX) px = bigX - qx;
            if (qy + py > bigY) py = bigY - qy;
        }

        if (v.FieldMode == 0 || s.PictType != AvPictureType.B)
        {
            // Hybrid prediction as specified in 8.3.5.3.5 (also 10.3.5.4.3.5).
            const int hybridmvThresh = 32;
            if (aValid && cValid)
            {
                sum = isIntra[isIntraOffset + xy - wrap] != 0
                    ? Math.Abs(px) + Math.Abs(py)
                    : Math.Abs(px - fieldPredA[0]) + Math.Abs(py - fieldPredA[1]);
                if (sum > hybridmvThresh)
                {
                    if (v.Gb.GetBits1() != 0)  // read HYBRIDPRED bit
                    {
                        px = fieldPredA[0];
                        py = fieldPredA[1];
                    }
                    else
                    {
                        px = fieldPredC[0];
                        py = fieldPredC[1];
                    }
                }
                else
                {
                    sum = isIntra[isIntraOffset + xy - 1] != 0
                        ? Math.Abs(px) + Math.Abs(py)
                        : Math.Abs(px - fieldPredC[0]) + Math.Abs(py - fieldPredC[1]);
                    if (sum > hybridmvThresh)
                    {
                        if (v.Gb.GetBits1() != 0)
                        {
                            px = fieldPredA[0];
                            py = fieldPredA[1];
                        }
                        else
                        {
                            px = fieldPredC[0];
                            py = fieldPredC[1];
                        }
                    }
                }
            }
        }

        if (v.FieldMode != 0 && v.Numref != 0)
            rY >>= 1;
        if (v.FieldMode != 0 && v.CurFieldType != 0 && v.RefFieldType[dir] == 0)
            yBias = 1;

        // Store MV using the signed modulus of the MV range defined in 4.11.
        short storedX = (short)(((px + dmvX + rX) & ((rX << 1) - 1)) - rX);
        short storedY = (short)(((py + dmvY + rY - yBias) & ((rY << 1) - 1)) - rY + yBias);
        s.Mv[dir * 8 + n * 2 + 0] = storedX;
        s.Mv[dir * 8 + n * 2 + 1] = storedY;
        Pair(mvDir, xy + v.BlocksOff, storedX, storedY);
        if (mv1 != 0)  // Duplicate motion data for a 1-MV block.
        {
            Pair(mvDir, xy + 1 + v.BlocksOff, storedX, storedY);
            Pair(mvDir, xy + wrap + v.BlocksOff, storedX, storedY);
            Pair(mvDir, xy + wrap + 1 + v.BlocksOff, storedX, storedY);
            mvF[mvFOff + xy + 1 + v.BlocksOff] = mvF[mvFOff + xy + v.BlocksOff];
            mvF[mvFOff + xy + wrap + 1 + v.BlocksOff] = mvF[mvFOff + xy + v.BlocksOff];
            mvF[mvFOff + xy + wrap + v.BlocksOff] = mvF[mvFOff + xy + wrap + 1 + v.BlocksOff];
        }
    }

    /// <summary>
    /// Writes one motion vector pair at <paramref name="index"/>. The components are ints here
    /// and int16_t in the C, and the narrowing is the same one the C's assignment does.
    /// </summary>
    private static void Pair(MvPlane mv, int index, int x, int y)
    {
        mv[index * 2] = (short)x;
        mv[index * 2 + 1] = (short)y;
    }

    /// <summary>
    /// <c>ff_vc1_pred_mv_intfr</c> - the predictor for an interlaced frame picture, where a
    /// neighbour may carry a frame vector while this block carries two field vectors, or the
    /// other way round; the mismatched cases are averaged.
    /// </summary>
    public static void Vc1PredMvIntfr(Vc1Context v, int n, int dmvX, int dmvY, int mvn,
                                      int rX, int rY, int dir)
    {
        ref MpegEncContext s = ref v.S;
        Span<int> a = stackalloc int[2];
        Span<int> b = stackalloc int[2];
        Span<int> c = stackalloc int[2];
        int px = 0, py = 0;
        bool aValid = false, bValid = false, cValid = false;
        int fieldA, fieldB, fieldC;  // 0: same, 1: opposite
        int posC, posB, nAdj;

        int wrap = s.B8Stride;
        int xy = s.BlockIndex[n];

        MvPlane mv0 = MotionVal(ref s, 0);
        MvPlane mv1a = MotionVal(ref s, 1);

        if (s.MbIntra != 0)
        {
            s.Mv[0 * 8 + n * 2 + 0] = 0;
            s.Mv[0 * 8 + n * 2 + 1] = 0;
            Pair(mv0, xy, 0, 0);
            Pair(mv1a, xy, 0, 0);
            if (mvn == 1)  // Duplicate motion data for a 1-MV block.
            {
                Pair(mv0, xy + 1, 0, 0);
                Pair(mv0, xy + wrap, 0, 0);
                Pair(mv0, xy + wrap + 1, 0, 0);
                v.LumaMvBase![v.LumaMvOffset + s.MbX * 2 + 0] = 0;
                v.LumaMvBase[v.LumaMvOffset + s.MbX * 2 + 1] = 0;
                Pair(mv1a, xy + 1, 0, 0);
                Pair(mv1a, xy + wrap, 0, 0);
                Pair(mv1a, xy + wrap + 1, 0, 0);
            }
            return;
        }

        MvPlane mv = MotionVal(ref s, dir);
        byte[] blkMvType = v.BlkMvTypeBase!;
        int blkOff = v.BlkMvTypeOffset;
        byte[] isIntra = v.IsIntraBase!;
        int isIntraOff = v.IsIntraOffset;

        int off = n == 0 || n == 1 ? 1 : -1;

        // Predict A.
        if (s.MbX != 0 || n == 1 || n == 3)
        {
            if (blkMvType[blkOff + xy] != 0 ||  // current block (MB) has a field MV
                (blkMvType[blkOff + xy] == 0 && blkMvType[blkOff + xy - 1] == 0))
            {
                a[0] = mv[(xy - 1) * 2];
                a[1] = mv[(xy - 1) * 2 + 1];
                aValid = true;
            }
            else
            {
                // Current block has a frame MV and the candidate a field MV, so average.
                a[0] = (mv[(xy - 1) * 2] + mv[(xy - 1 + off * wrap) * 2] + 1) >> 1;
                a[1] = (mv[(xy - 1) * 2 + 1] + mv[(xy - 1 + off * wrap) * 2 + 1] + 1) >> 1;
                aValid = true;
            }
            if ((n & 1) == 0 && isIntra[isIntraOff + s.MbX - 1] != 0)
            {
                aValid = false;
                a[0] = a[1] = 0;
            }
        }
        else
        {
            a[0] = a[1] = 0;
        }

        // Predict B and C.
        b[0] = b[1] = c[0] = c[1] = 0;
        if (n == 0 || n == 1 || blkMvType[blkOff + xy] != 0)
        {
            if (s.FirstSliceLine == 0)
            {
                if (isIntra[isIntraOff + s.MbX - s.MbStride] == 0)
                {
                    bValid = true;
                    nAdj = n | 2;
                    posB = s.BlockIndex[nAdj] - 2 * wrap;
                    if (blkMvType[blkOff + posB] != 0 && blkMvType[blkOff + xy] != 0)
                        nAdj = (n & 2) | (n & 1);
                    b[0] = mv[(s.BlockIndex[nAdj] - 2 * wrap) * 2];
                    b[1] = mv[(s.BlockIndex[nAdj] - 2 * wrap) * 2 + 1];
                    if (blkMvType[blkOff + posB] != 0 && blkMvType[blkOff + xy] == 0)
                    {
                        b[0] = (b[0] + mv[(s.BlockIndex[nAdj ^ 2] - 2 * wrap) * 2] + 1) >> 1;
                        b[1] = (b[1] + mv[(s.BlockIndex[nAdj ^ 2] - 2 * wrap) * 2 + 1] + 1) >> 1;
                    }
                }
                if (s.MbWidth > 1)
                {
                    if (isIntra[isIntraOff + s.MbX - s.MbStride + 1] == 0)
                    {
                        cValid = true;
                        nAdj = 2;
                        posC = s.BlockIndex[2] - 2 * wrap + 2;
                        if (blkMvType[blkOff + posC] != 0 && blkMvType[blkOff + xy] != 0)
                            nAdj = n & 2;
                        c[0] = mv[(s.BlockIndex[nAdj] - 2 * wrap + 2) * 2];
                        c[1] = mv[(s.BlockIndex[nAdj] - 2 * wrap + 2) * 2 + 1];
                        if (blkMvType[blkOff + posC] != 0 && blkMvType[blkOff + xy] == 0)
                        {
                            c[0] = (1 + c[0]
                                    + mv[(s.BlockIndex[nAdj ^ 2] - 2 * wrap + 2) * 2]) >> 1;
                            c[1] = (1 + c[1]
                                    + mv[(s.BlockIndex[nAdj ^ 2] - 2 * wrap + 2) * 2 + 1]) >> 1;
                        }
                        if (s.MbX == s.MbWidth - 1)
                        {
                            if (isIntra[isIntraOff + s.MbX - s.MbStride - 1] == 0)
                            {
                                cValid = true;
                                nAdj = 3;
                                posC = s.BlockIndex[3] - 2 * wrap - 2;
                                if (blkMvType[blkOff + posC] != 0 && blkMvType[blkOff + xy] != 0)
                                    nAdj = n | 1;
                                c[0] = mv[(s.BlockIndex[nAdj] - 2 * wrap - 2) * 2];
                                c[1] = mv[(s.BlockIndex[nAdj] - 2 * wrap - 2) * 2 + 1];
                                if (blkMvType[blkOff + posC] != 0 &&
                                    blkMvType[blkOff + xy] == 0)
                                {
                                    c[0] = (1 + c[0]
                                            + mv[(s.BlockIndex[1] - 2 * wrap - 2) * 2]) >> 1;
                                    c[1] = (1 + c[1]
                                            + mv[(s.BlockIndex[1] - 2 * wrap - 2) * 2 + 1]) >> 1;
                                }
                            }
                            else
                            {
                                cValid = false;
                            }
                        }
                    }
                }
            }
        }
        else
        {
            posB = s.BlockIndex[1];
            bValid = true;
            b[0] = mv[posB * 2];
            b[1] = mv[posB * 2 + 1];
            posC = s.BlockIndex[0];
            cValid = true;
            c[0] = mv[posC * 2];
            c[1] = mv[posC * 2 + 1];
        }

        int totalValid = (aValid ? 1 : 0) + (bValid ? 1 : 0) + (cValid ? 1 : 0);
        // Check whether predictor A is out of bounds.
        if (s.MbX == 0 && !(n == 1 || n == 3))
            a[0] = a[1] = 0;
        // Check whether predictor B is out of bounds.
        if ((s.FirstSliceLine != 0 && blkMvType[blkOff + xy] != 0) ||
            (s.FirstSliceLine != 0 && (n & 2) == 0))
        {
            b[0] = b[1] = c[0] = c[1] = 0;
        }

        if (blkMvType[blkOff + xy] == 0)
        {
            if (s.MbWidth == 1)
            {
                px = b[0];
                py = b[1];
            }
            else if (totalValid >= 2)
            {
                px = MathOps.MidPred(a[0], b[0], c[0]);
                py = MathOps.MidPred(a[1], b[1], c[1]);
            }
            else if (totalValid != 0)
            {
                if (aValid) { px = a[0]; py = a[1]; }
                else if (bValid) { px = b[0]; py = b[1]; }
                else { px = c[0]; py = c[1]; }
            }
        }
        else
        {
            fieldA = aValid && (a[1] & 4) != 0 ? 1 : 0;
            fieldB = bValid && (b[1] & 4) != 0 ? 1 : 0;
            fieldC = cValid && (c[1] & 4) != 0 ? 1 : 0;

            int numOppfield = fieldA + fieldB + fieldC;
            int numSamefield = totalValid - numOppfield;
            if (totalValid == 3)
            {
                if (numSamefield == 3 || numOppfield == 3)
                {
                    px = MathOps.MidPred(a[0], b[0], c[0]);
                    py = MathOps.MidPred(a[1], b[1], c[1]);
                }
                else if (numSamefield >= numOppfield)
                {
                    // Take one MV from the same field set, by priority; the check for B may
                    // not be necessary.
                    px = fieldA == 0 ? a[0] : b[0];
                    py = fieldA == 0 ? a[1] : b[1];
                }
                else
                {
                    px = fieldA != 0 ? a[0] : b[0];
                    py = fieldA != 0 ? a[1] : b[1];
                }
            }
            else if (totalValid == 2)
            {
                if (numSamefield >= numOppfield)
                {
                    if (fieldA == 0 && aValid)
                    {
                        px = a[0];
                        py = a[1];
                    }
                    else if (fieldB == 0 && bValid)
                    {
                        px = b[0];
                        py = b[1];
                    }
                    else
                    {
                        if (!cValid)
                            throw new InvalidOperationException(
                                "two valid MV predictors but neither A nor B is usable and C "
                                + "is invalid (av_assert1 in ff_vc1_pred_mv_intfr)");
                        px = c[0];
                        py = c[1];
                    }
                }
                else
                {
                    if (fieldA != 0 && aValid)
                    {
                        px = a[0];
                        py = a[1];
                    }
                    else
                    {
                        if (!(fieldB != 0 && bValid))
                            throw new InvalidOperationException(
                                "two valid MV predictors, more opposite than same, and neither "
                                + "A nor B is an opposite-field candidate (av_assert1 in "
                                + "ff_vc1_pred_mv_intfr)");
                        px = b[0];
                        py = b[1];
                    }
                }
            }
            else if (totalValid == 1)
            {
                px = aValid ? a[0] : bValid ? b[0] : c[0];
                py = aValid ? a[1] : bValid ? b[1] : c[1];
            }
        }

        // Store MV using the signed modulus of the MV range defined in 4.11.
        short storedX = (short)(((px + dmvX + rX) & ((rX << 1) - 1)) - rX);
        short storedY = (short)(((py + dmvY + rY) & ((rY << 1) - 1)) - rY);
        s.Mv[dir * 8 + n * 2 + 0] = storedX;
        s.Mv[dir * 8 + n * 2 + 1] = storedY;
        Pair(mv, xy, storedX, storedY);
        if (mvn == 1)  // Duplicate motion data for a 1-MV block.
        {
            Pair(mv, xy + 1, storedX, storedY);
            Pair(mv, xy + wrap, storedX, storedY);
            Pair(mv, xy + wrap + 1, storedX, storedY);
        }
        else if (mvn == 2)  // Duplicate motion data for a 2-field-MV block.
        {
            Pair(mv, xy + 1, storedX, storedY);
            s.Mv[dir * 8 + (n + 1) * 2 + 0] = storedX;
            s.Mv[dir * 8 + (n + 1) * 2 + 1] = storedY;
        }
    }

    /// <summary>
    /// <c>ff_vc1_pred_b_mv</c> - the predictor for a progressive B macroblock, which runs the
    /// forward and backward halves independently and has a direct mode that scales the
    /// co-located vector of the next picture.
    /// </summary>
    public static void Vc1PredBMv(Vc1Context v, Span<int> dmvX, Span<int> dmvY,
                                  int direct, int mvtype)
    {
        ref MpegEncContext s = ref v.S;

        if (v.FieldMode != 0)
        {
            throw new InvalidOperationException(
                "ff_vc1_pred_b_mv was reached with field_mode set (av_assert0)");
        }

        int rX = v.RangeX;
        int rY = v.RangeY;
        // Scale MV difference to be quad-pel.
        if (s.QuarterSample == 0)
        {
            dmvX[0] *= 2;
            dmvY[0] *= 2;
            dmvX[1] *= 2;
            dmvY[1] *= 2;
        }

        int wrap = s.B8Stride;
        int xy = s.BlockIndex[0];

        MvPlane mvFwd = MotionVal(ref s, 0);
        MvPlane mvBwd = MotionVal(ref s, 1);

        if (s.MbIntra != 0)
        {
            Pair(mvFwd, xy, 0, 0);
            Pair(mvBwd, xy, 0, 0);
            return;
        }
        // The C warns here when a direct-mode B picture references a field picture; it is a
        // log and no state.

        MvPlane next = NextMotionVal(ref s, 1);
        s.Mv[0 * 8 + 0] = (short)ScaleMv(next[xy * 2], v.Bfraction, 0, s.QuarterSample);
        s.Mv[0 * 8 + 1] = (short)ScaleMv(next[xy * 2 + 1], v.Bfraction, 0, s.QuarterSample);
        s.Mv[1 * 8 + 0] = (short)ScaleMv(next[xy * 2], v.Bfraction, 1, s.QuarterSample);
        s.Mv[1 * 8 + 1] = (short)ScaleMv(next[xy * 2 + 1], v.Bfraction, 1, s.QuarterSample);

        // Pull back the predicted motion vectors as specified in 8.4.5.4.
        s.Mv[0 * 8 + 0] = (short)Clip(s.Mv[0 * 8 + 0],
            -60 - (s.MbX << 6), (s.MbWidth << 6) - 4 - (s.MbX << 6));
        s.Mv[0 * 8 + 1] = (short)Clip(s.Mv[0 * 8 + 1],
            -60 - (s.MbY << 6), (s.MbHeight << 6) - 4 - (s.MbY << 6));
        s.Mv[1 * 8 + 0] = (short)Clip(s.Mv[1 * 8 + 0],
            -60 - (s.MbX << 6), (s.MbWidth << 6) - 4 - (s.MbX << 6));
        s.Mv[1 * 8 + 1] = (short)Clip(s.Mv[1 * 8 + 1],
            -60 - (s.MbY << 6), (s.MbHeight << 6) - 4 - (s.MbY << 6));

        if (direct != 0)
        {
            Pair(mvFwd, xy, s.Mv[0 * 8 + 0], s.Mv[0 * 8 + 1]);
            Pair(mvBwd, xy, s.Mv[1 * 8 + 0], s.Mv[1 * 8 + 1]);
            return;
        }

        if (mvtype == BmvType.Forward || mvtype == BmvType.Interpolated)
            PredBMvHalf(v, ref s, 0, xy, wrap, dmvX[0], dmvY[0], rX, rY);
        if (mvtype == BmvType.Backward || mvtype == BmvType.Interpolated)
            PredBMvHalf(v, ref s, 1, xy, wrap, dmvX[1], dmvY[1], rX, rY);

        Pair(mvFwd, xy, s.Mv[0 * 8 + 0], s.Mv[0 * 8 + 1]);
        Pair(mvBwd, xy, s.Mv[1 * 8 + 0], s.Mv[1 * 8 + 1]);
    }

    /// <summary>
    /// One half of <c>ff_vc1_pred_b_mv</c>: the forward and backward arms are the same sixty
    /// lines with <c>motion_val[0]</c> swapped for <c>motion_val[1]</c>.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE, recorded: two copies in the C, written once. The hybrid-prediction block
    /// both copies carry is dead in both - its condition begins <c>if (0 &amp;&amp; ...)</c> -
    /// and is not translated; the comment marks where it was.
    /// </remarks>
    private static void PredBMvHalf(Vc1Context v, ref MpegEncContext s, int dir, int xy,
                                    int wrap, int dmvX, int dmvY, int rX, int rY)
    {
        MvPlane mv = MotionVal(ref s, dir);
        int cIdx = (xy - 2) * 2;
        int aIdx = (xy - wrap * 2) * 2;
        int off = s.MbX == s.MbWidth - 1 ? -2 : 2;
        int bIdx = (xy - wrap * 2 + off) * 2;
        int px, py;

        if (s.MbX == 0)
            mv[cIdx] = mv[cIdx + 1] = 0;
        if (s.FirstSliceLine == 0)  // Predictor A is not out of bounds.
        {
            if (s.MbWidth == 1)
            {
                px = mv[aIdx];
                py = mv[aIdx + 1];
            }
            else
            {
                px = MathOps.MidPred(mv[aIdx], mv[bIdx], mv[cIdx]);
                py = MathOps.MidPred(mv[aIdx + 1], mv[bIdx + 1], mv[cIdx + 1]);
            }
        }
        else if (s.MbX != 0)  // Predictor C is not out of bounds.
        {
            px = mv[cIdx];
            py = mv[cIdx + 1];
        }
        else
        {
            px = py = 0;
        }

        // Pullback MV as specified in 8.3.5.3.4.
        {
            int sh = v.Profile < (int)Vc1Profile.Advanced ? 5 : 6;
            int mvMin = 4 - (1 << sh);
            int qx = s.MbX << sh;
            int qy = s.MbY << sh;
            int bigX = (s.MbWidth << sh) - 4;
            int bigY = (s.MbHeight << sh) - 4;
            if (qx + px < mvMin) px = mvMin - qx;
            if (qy + py < mvMin) py = mvMin - qy;
            if (qx + px > bigX) px = bigX - qx;
            if (qy + py > bigY) py = bigY - qy;
        }

        // The C has the hybrid prediction of 8.3.5.3.5 here, behind "if (0 && ...)". It is
        // dead in ffmpeg and is not translated; translating it would change the bit count.

        // Store MV using the signed modulus of the MV range defined in 4.11.
        s.Mv[dir * 8 + 0] = (short)(((px + dmvX + rX) & ((rX << 1) - 1)) - rX);
        s.Mv[dir * 8 + 1] = (short)(((py + dmvY + rY) & ((rY << 1) - 1)) - rY);
    }

    /// <summary>
    /// <c>ff_vc1_pred_b_mv_intfi</c> - the predictor for an interlaced field B macroblock,
    /// which dispatches to <see cref="Vc1PredMv"/> for everything except direct mode.
    /// </summary>
    public static void Vc1PredBMvIntfi(Vc1Context v, int n, Span<int> dmvX, Span<int> dmvY,
                                       int mv1, Span<int> predFlag)
    {
        int dir = v.Bmvtype == BmvType.Backward ? 1 : 0;
        ref MpegEncContext s = ref v.S;
        int mbPos = s.MbX + s.MbY * s.MbStride;

        if (v.Bmvtype == BmvType.Direct)
        {
            int f;
            MvPlane mvFwd = MotionVal(ref s, 0);
            MvPlane mvBwd = MotionVal(ref s, 1);
            if (s.NextPic.MbType![s.NextPic.MbTypeOffset + mbPos + v.MbOff] != MbType.Intra)
            {
                MvPlane next = NextMotionVal(ref s, 1);
                int b0 = (s.BlockIndex[0] + v.BlocksOff) * 2;
                s.Mv[0 * 8 + 0] = (short)ScaleMv(next[b0], v.Bfraction, 0, s.QuarterSample);
                s.Mv[0 * 8 + 1] = (short)ScaleMv(next[b0 + 1], v.Bfraction, 0, s.QuarterSample);
                s.Mv[1 * 8 + 0] = (short)ScaleMv(next[b0], v.Bfraction, 1, s.QuarterSample);
                s.Mv[1 * 8 + 1] = (short)ScaleMv(next[b0 + 1], v.Bfraction, 1, s.QuarterSample);

                byte[] mvFNext = v.MvFNextBase!;
                int nextOff = v.MvFNextOffset[0];
                int totalOpp = mvFNext[nextOff + s.BlockIndex[0] + v.BlocksOff]
                             + mvFNext[nextOff + s.BlockIndex[1] + v.BlocksOff]
                             + mvFNext[nextOff + s.BlockIndex[2] + v.BlocksOff]
                             + mvFNext[nextOff + s.BlockIndex[3] + v.BlocksOff];
                f = totalOpp > 2 ? 1 : 0;
            }
            else
            {
                s.Mv[0 * 8 + 0] = s.Mv[0 * 8 + 1] = 0;
                s.Mv[1 * 8 + 0] = s.Mv[1 * 8 + 1] = 0;
                f = 0;
            }
            v.RefFieldType[0] = v.RefFieldType[1] = v.CurFieldType ^ f;
            byte[] mvF = v.MvFBase!;
            for (int k = 0; k < 4; k++)
            {
                int idx = s.BlockIndex[k] + v.BlocksOff;
                Pair(mvFwd, idx, s.Mv[0 * 8 + 0], s.Mv[0 * 8 + 1]);
                Pair(mvBwd, idx, s.Mv[1 * 8 + 0], s.Mv[1 * 8 + 1]);
                mvF[v.MvFOffset[0] + idx] = (byte)f;
                mvF[v.MvFOffset[1] + idx] = (byte)f;
            }
            return;
        }

        byte[] mbType = v.MbTypeBase!;
        int mbTypeOff = v.MbTypeOffset;

        if (v.Bmvtype == BmvType.Interpolated)
        {
            Vc1PredMv(v, 0, dmvX[0], dmvY[0], 1, v.RangeX, v.RangeY, mbType, mbTypeOff,
                      predFlag[0], 0);
            Vc1PredMv(v, 0, dmvX[1], dmvY[1], 1, v.RangeX, v.RangeY, mbType, mbTypeOff,
                      predFlag[1], 1);
            return;
        }
        if (dir != 0)  // backward
        {
            Vc1PredMv(v, n, dmvX[1], dmvY[1], mv1, v.RangeX, v.RangeY, mbType, mbTypeOff,
                      predFlag[1], 1);
            if (n == 3 || mv1 != 0)
            {
                Vc1PredMv(v, 0, dmvX[0], dmvY[0], 1, v.RangeX, v.RangeY, mbType, mbTypeOff,
                          0, 0);
            }
        }
        else  // forward
        {
            Vc1PredMv(v, n, dmvX[0], dmvY[0], mv1, v.RangeX, v.RangeY, mbType, mbTypeOff,
                      predFlag[0], 0);
            if (n == 3 || mv1 != 0)
            {
                Vc1PredMv(v, 0, dmvX[1], dmvY[1], 1, v.RangeX, v.RangeY, mbType, mbTypeOff,
                          0, 1);
            }
        }
    }
}

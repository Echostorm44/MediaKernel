// FFmpeg's mpeg4video.c - the handful of helpers the MPEG-4 encoder and decoder share, plus
// the direct-mode motion vector derivation that RV10 also uses.

using System;
using MediaKernel.Core.Codecs.Mpeg2;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Mpeg4Video;

/// <summary>The shared MPEG-4 Part 2 helpers (<c>mpeg4video.c</c>).</summary>
public static class Mpeg4Video
{
    /// <summary>
    /// <c>tab_size</c> - <c>FF_ARRAY_ELEMS(s->direct_scale_mv[0])</c>. The table is
    /// <c>[2][64]</c>, flattened here, so a row is 64 wide.
    /// </summary>
    private const int TabSize = 64;

    /// <summary><c>tab_bias</c>.</summary>
    private const int TabBias = TabSize / 2;

    /// <summary><c>ff_mpeg4_get_video_packet_prefix_length</c>.</summary>
    public static int Mpeg4GetVideoPacketPrefixLength(AvPictureType pictType, int fCode,
                                                      int bCode)
    {
        switch (pictType)
        {
            case AvPictureType.I:
                return 16;
            case AvPictureType.P:
            case AvPictureType.S:
                return fCode + 15;
            case AvPictureType.B:
                return Max3(fCode, bCode, 2) + 15;
            default:
                return -1;
        }
    }

    private static int Max3(int a, int b, int c) => a > b ? (a > c ? a : c) : (b > c ? b : c);

    /// <summary>
    /// <c>ff_mpeg4_clean_buffers</c> - clears the AC predictors around the current macroblock
    /// at a resync point, and the two 16x16 vector predictors.
    /// </summary>
    /// <remarks>
    /// The motion vectors themselves are deliberately left alone: a B picture may still need
    /// them. Only <c>last_mv</c>, the predictor, is reset.
    /// </remarks>
    public static void Mpeg4CleanBuffers(ref MpegEncContext s)
    {
        int mbHeight = s.MbHeight;
        int lWrap = s.B8Stride;
        int lXy = (2 * s.MbY - 1) * lWrap + s.MbX * 2 - 1;
        int cWrap = s.MbStride;
        int uXy = 2 * mbHeight * lWrap + s.MbY * cWrap + s.MbX - 1;
        int vXy = uXy + cWrap * (mbHeight + 1);

        // ac_val is int16_t[...][16]; AcValOffset is already in shorts, so each entry is 16
        // shorts wide here too.
        short[] acVal = s.AcValBase!;
        int baseOffset = s.AcValOffset;

        Clear16(acVal, baseOffset + lXy * 16, lWrap * 2 + 1);
        Clear16(acVal, baseOffset + uXy * 16, cWrap + 1);
        Clear16(acVal, baseOffset + vXy * 16, cWrap + 1);

        // last_mv[2][2][2] flattened: index is ((dir * 2) + i) * 2 + component.
        s.LastMv[0] = 0;    // last_mv[0][0][0]
        s.LastMv[1] = 0;    // last_mv[0][0][1]
        s.LastMv[4] = 0;    // last_mv[1][0][0]
        s.LastMv[5] = 0;    // last_mv[1][0][1]
    }

    private static void Clear16(short[] acVal, int start, int entries)
    {
        acVal.AsSpan(start, entries * 16).Clear();
    }

    /// <summary>
    /// <c>ff_mpeg4_init_direct_mv</c> - the lookup that turns a colocated vector into this
    /// B picture's forward and backward halves.
    /// </summary>
    /// <remarks>
    /// Both rows are integer divisions by <c>pp_time</c> done once per picture, which is the
    /// point of the table: direct mode would otherwise divide twice per vector component.
    /// </remarks>
    public static void Mpeg4InitDirectMv(ref MpegEncContext s)
    {
        for (int i = 0; i < TabSize; i++)
        {
            s.DirectScaleMv[0 * TabSize + i] = (short)((i - TabBias) * s.PbTime / s.PpTime);
            s.DirectScaleMv[1 * TabSize + i] =
                (short)((i - TabBias) * (s.PbTime - s.PpTime) / s.PpTime);
        }
    }

    /// <summary><c>s->mv[i][j][k]</c>, flattened as <c>MpegEncContext.Mv</c> holds it.</summary>
    private static int MvIndex(int dir, int i, int component) => dir * 8 + i * 2 + component;

    /// <summary>
    /// <c>ff_mpeg4_set_one_direct_mv</c> - one of the four colocated vectors, split into this
    /// B picture's forward and backward halves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The forward half is the colocated vector scaled by how far through the P-to-P interval
    /// this B picture sits. The backward half is <em>not</em> the complement of that when a
    /// delta was transmitted: with a non-zero delta it is the forward vector minus the whole
    /// colocated vector, so the delta moves both halves together; only with a zero delta is it
    /// the scaled negative remainder. The C writes that as a ternary on <c>mx</c> and the two
    /// components are decided independently - a zero x with a non-zero y takes different arms.
    /// </para>
    /// <para>
    /// The table covers colocated components in [-32, 32), which is almost all of them; the
    /// else arm is the same arithmetic done long-hand for the rest.
    /// </para>
    /// </remarks>
    private static void Mpeg4SetOneDirectMv(ref MpegEncContext s, int mx, int my, int i)
    {
        int xy = s.BlockIndex[i];
        ushort timePp = (ushort)s.PpTime;
        ushort timePb = (ushort)s.PbTime;

        short[] nextMv = s.NextPic.MotionValL0!;
        int at = s.NextPic.MotionValOffsetL0 + xy * 2;

        int pMx = nextMv[at + 0];
        if ((uint)(pMx + TabBias) < TabSize)
        {
            s.Mv[MvIndex(0, i, 0)] = (short)(s.DirectScaleMv[0 * TabSize + pMx + TabBias] + mx);
            s.Mv[MvIndex(1, i, 0)] = (short)(mx != 0
                ? s.Mv[MvIndex(0, i, 0)] - pMx
                : s.DirectScaleMv[1 * TabSize + pMx + TabBias]);
        }
        else
        {
            s.Mv[MvIndex(0, i, 0)] = (short)(pMx * timePb / timePp + mx);
            s.Mv[MvIndex(1, i, 0)] = (short)(mx != 0
                ? s.Mv[MvIndex(0, i, 0)] - pMx
                : pMx * (timePb - timePp) / timePp);
        }

        int pMy = nextMv[at + 1];
        if ((uint)(pMy + TabBias) < TabSize)
        {
            s.Mv[MvIndex(0, i, 1)] = (short)(s.DirectScaleMv[0 * TabSize + pMy + TabBias] + my);
            s.Mv[MvIndex(1, i, 1)] = (short)(my != 0
                ? s.Mv[MvIndex(0, i, 1)] - pMy
                : s.DirectScaleMv[1 * TabSize + pMy + TabBias]);
        }
        else
        {
            s.Mv[MvIndex(0, i, 1)] = (short)(pMy * timePb / timePp + my);
            s.Mv[MvIndex(1, i, 1)] = (short)(my != 0
                ? s.Mv[MvIndex(0, i, 1)] - pMy
                : pMy * (timePb - timePp) / timePp);
        }
    }

    /// <summary>
    /// <c>ff_mpeg4_set_direct_mv</c> - direct mode: the whole macroblock's motion taken from the
    /// colocated macroblock of the following P picture, plus a transmitted delta.
    /// </summary>
    /// <returns>The macroblock type, which the caller ORs into the one it already has.</returns>
    /// <remarks>
    /// The <em>colocated</em> macroblock decides the partitioning, not this one: four vectors
    /// there means four here. The interlaced arm is not translated - interlaced MPEG-4 throws
    /// in the header - and falling through to the frame arm would read <c>p_field_mv_table</c>
    /// that nothing fills.
    /// </remarks>
    public static int Mpeg4SetDirectMv(ref MpegEncContext s, int mx, int my)
    {
        int mbIndex = s.MbX + s.MbY * s.MbStride;
        uint colocatedMbType = s.NextPic.MbType![s.NextPic.MbTypeOffset + mbIndex];

        if ((colocatedMbType & MbType.Size8X8) != 0)
        {
            s.MvType = MpegVideoConstants.MvType8X8;
            for (int i = 0; i < 4; i++)
                Mpeg4SetOneDirectMv(ref s, mx, my, i);
            return MbType.Direct2 | MbType.Size8X8 | MbType.BidirMv;
        }

        if ((colocatedMbType & MbType.Interlaced) != 0)
        {
            throw new NotSupportedException(
                "ff_mpeg4_set_direct_mv: the interlaced arm is not translated.");
        }

        Mpeg4SetOneDirectMv(ref s, mx, my, 0);
        for (int i = 1; i < 4; i++)
        {
            s.Mv[MvIndex(0, i, 0)] = s.Mv[MvIndex(0, 0, 0)];
            s.Mv[MvIndex(0, i, 1)] = s.Mv[MvIndex(0, 0, 1)];
            s.Mv[MvIndex(1, i, 0)] = s.Mv[MvIndex(1, 0, 0)];
            s.Mv[MvIndex(1, i, 1)] = s.Mv[MvIndex(1, 0, 1)];
        }

        // One vector, but at quarter-sample accuracy it is applied as four 8x8 blocks: the
        // rounding of a qpel vector differs between the two paths, and FF_BUG_DIRECT_BLOCKSIZE
        // marks the encoders that assumed otherwise.
        s.MvType = (s.WorkaroundBugs & Mpeg4VideoDec.FfBugDirectBlocksize) != 0 ||
                   s.QuarterSample == 0
            ? MpegVideoConstants.MvType16X16
            : MpegVideoConstants.MvType8X8;

        return MbType.Direct2 | MbType.Size16X16 | MbType.BidirMv;
    }
}

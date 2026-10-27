// Error resilience - a translation of ffmpeg-master/libavcodec/error_resilience.c and its header.
//
// What this is for. A damaged stream reaches the decoder with macroblocks missing: a slice whose
// bits are corrupt, or a slice that never arrived at all. Without concealment those macroblocks
// keep whatever was in the buffer, and the damage propagates through every picture that predicts
// from them. Concealment guesses: a motion vector from the neighbours for an inter macroblock, a
// DC level interpolated from the undamaged blocks around it for an intra one, then a light
// deblocking pass across the repaired boundaries.
//
// Recorded divergences, all of them in the port checklist as well:
//
//  * ERContext's decode_mb function pointer and opaque pointer are gone. There is exactly one
//    implementation in this port's scope - MpegEr.MpegErDecodeMb - and the user's standing
//    decision for the DSP contexts was direct static calls rather than indirection, so the same
//    applies here. The ER functions take `ref MpegEncContext` and reach the context directly.
//  * The `sad` function pointer is likewise a direct call to MeCmp.PixAbs16.
//  * er_temp_buffer is one allocation in C that three arrays of different element types are
//    carved out of. Here they are three arrays.
//  * dc_val[3] is one allocation with computed offsets in C; here it is three arrays.
//  * atomic_int error_count is a plain int. The port is single-threaded; slice threading is not
//    implemented and is marked todo on its own entries.
//  * ERPicture holds an AVFrame pointer; here it holds the fields of the frame that ER reads,
//    copied in. ff_er_frame_end rewrites cur_pic's motion vector pointers without touching the
//    real picture, which only works because ERPicture is a copy in C too.
//  * The scratch buffers guess_dc and ff_er_frame_end malloc and free per call are fields here,
//    allocated once and zeroed on use. Per-frame allocation is a defect in this project.
//  * Thread progress waits have no counterpart and are absent.

using System;
using MediaKernel.Core.Buffers;
using MediaKernel.Core.Codecs.Mpeg2;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>Error status bits (<c>error_resilience.h</c>), one byte per macroblock.</summary>
public static class ErStatus
{
    /// <summary><c>VP_START</c> - this macroblock is the first after a resync marker.</summary>
    public const int VpStart = 1;

    /// <summary><c>ER_AC_ERROR</c>.</summary>
    public const int AcError = 2;

    /// <summary><c>ER_DC_ERROR</c>.</summary>
    public const int DcError = 4;

    /// <summary><c>ER_MV_ERROR</c>.</summary>
    public const int MvError = 8;

    /// <summary><c>ER_AC_END</c>.</summary>
    public const int AcEnd = 16;

    /// <summary><c>ER_DC_END</c>.</summary>
    public const int DcEnd = 32;

    /// <summary><c>ER_MV_END</c>.</summary>
    public const int MvEnd = 64;

    /// <summary><c>ER_MB_ERROR</c>.</summary>
    public const int MbError = AcError | DcError | MvError;

    /// <summary><c>ER_MB_END</c>.</summary>
    public const int MbEnd = AcEnd | DcEnd | MvEnd;
}

/// <summary>
/// The <c>error_concealment</c> flags of <c>AVCodecContext</c> (<c>avcodec.h</c>). FFmpeg's
/// default is both of them, and this port matches it so that a comparison against ffmpeg
/// compares the same thing.
/// </summary>
[Flags]
public enum ErrorConcealment
{
    /// <summary>Concealment off.</summary>
    None = 0,

    /// <summary><c>FF_EC_GUESS_MVS</c> - search the neighbours for a motion vector.</summary>
    GuessMvs = 1,

    /// <summary><c>FF_EC_DEBLOCK</c> - deblock across concealed boundaries.</summary>
    Deblock = 2,

    /// <summary><c>FF_EC_FAVOR_INTER</c> - prefer temporal concealment even in an I picture.</summary>
    FavorInter = 256,

    /// <summary>FFmpeg's default for <c>error_concealment</c>.</summary>
    Default = GuessMvs | Deblock,
}

/// <summary>
/// <c>ERPicture</c> (<c>error_resilience.h:43</c>) - everything error resilience reads of a
/// picture, copied out so that the repair can retarget its own pointers.
/// </summary>
public struct ErPicture
{
    /// <summary>The sample allocation (<c>f</c>), or null for "no picture".</summary>
    public byte[]? Frame;

    /// <summary><c>f->data</c>, as offsets into <see cref="Frame"/>.</summary>
    public MPVPicture.PlaneOffsets Data;

    /// <summary><c>f->linesize</c>.</summary>
    public MPVPicture.PlaneStrides Linesize;

    /// <summary><c>f->width</c>.</summary>
    public int Width;

    /// <summary><c>f->height</c>.</summary>
    public int Height;

    /// <summary><c>f->pict_type</c>.</summary>
    public AvPictureType PictType;

    /// <summary>Whether the picture carries a third plane, which <c>f->data[2]</c> tests.</summary>
    public bool HasChroma;

    /// <summary><c>motion_val[0]</c> and its offset; each entry is an x,y pair of shorts.</summary>
    public short[]? MotionValL0;

    /// <inheritdoc cref="MotionValL0"/>
    public short[]? MotionValL1;

    /// <summary>Offsets into the two motion vector tables, counted in shorts.</summary>
    public int MotionValOffsetL0;

    /// <inheritdoc cref="MotionValOffsetL0"/>
    public int MotionValOffsetL1;

    /// <summary><c>ref_index[0]</c> - four entries per macroblock.</summary>
    public sbyte[]? RefIndexL0;

    /// <inheritdoc cref="RefIndexL0"/>
    public sbyte[]? RefIndexL1;

    /// <summary><c>mb_type</c> and its offset.</summary>
    public uint[]? MbType;

    /// <inheritdoc cref="MbType"/>
    public int MbTypeOffset;

    /// <summary><c>field_picture</c>.</summary>
    public int FieldPicture;
}

/// <summary>
/// <c>ERContext</c> (<c>error_resilience.h:54</c>) - the error status of the picture being
/// decoded and the buffers the repair works in.
/// </summary>
public struct ErContext
{
    /// <summary>
    /// <c>avctx->error_concealment</c>, which lives on the codec context in FFmpeg. Held here
    /// because this port has no AVCodecContext and ER is the only reader.
    /// </summary>
    public ErrorConcealment ErrorConcealment;

    /// <summary><c>mb_index2xy</c>.</summary>
    public int[]? MbIndex2Xy;

    /// <summary><c>mb_num</c>.</summary>
    public int MbNum;

    /// <summary><c>mb_width</c>.</summary>
    public int MbWidth;

    /// <summary><c>mb_height</c>.</summary>
    public int MbHeight;

    /// <summary><c>mb_stride</c>.</summary>
    public int MbStride;

    /// <summary><c>b8_stride</c>.</summary>
    public int B8Stride;

    /// <summary><c>error_count</c>. Three per macroblock - AC, DC and MV - counted down as
    /// slices report themselves complete, so zero means the whole picture arrived.</summary>
    public int ErrorCount;

    /// <summary><c>error_occurred</c>.</summary>
    public int ErrorOccurred;

    /// <summary><c>error_status_table</c> - one byte of <see cref="ErStatus"/> bits per
    /// macroblock.</summary>
    public byte[]? ErrorStatusTable;

    /// <summary><c>dc_val[0]</c> - luma, one DC per 8x8 block, strided by b8_stride.</summary>
    public short[]? DcValY;

    /// <summary><c>dc_val[1]</c> - Cb, one per macroblock, strided by mb_stride.</summary>
    public short[]? DcValCb;

    /// <summary><c>dc_val[2]</c> - Cr.</summary>
    public short[]? DcValCr;

    /// <summary><c>mbskip_table</c> - borrowed from the context.</summary>
    public byte[]? MbskipTable;

    /// <summary><c>mbintra_table</c> - borrowed from the context.</summary>
    public byte[]? MbintraTable;

    /// <summary><c>mv[2][4][2]</c> - the vector handed to the reconstruction callback.</summary>
    public MpegEncContext.Int16Flat Mv;

    /// <summary><c>cur_pic</c>.</summary>
    public ErPicture CurPic;

    /// <summary><c>last_pic</c>.</summary>
    public ErPicture LastPic;

    /// <summary><c>next_pic</c>.</summary>
    public ErPicture NextPic;

    /// <summary><c>ref_index[2]</c> - ER's own, used when the decoder kept none.</summary>
    public sbyte[]? OwnRefIndexL0;

    /// <inheritdoc cref="OwnRefIndexL0"/>
    public sbyte[]? OwnRefIndexL1;

    /// <summary><c>motion_val_base[2]</c> - ER's own, used when the decoder kept none.</summary>
    public short[]? OwnMotionValBaseL0;

    /// <inheritdoc cref="OwnMotionValBaseL0"/>
    public short[]? OwnMotionValBaseL1;

    /// <summary><c>pp_time</c>.</summary>
    public ushort PpTime;

    /// <summary><c>pb_time</c>.</summary>
    public ushort PbTime;

    /// <summary><c>quarter_sample</c>.</summary>
    public int QuarterSample;

    /// <summary><c>partitioned_frame</c>.</summary>
    public int PartitionedFrame;

    // ----- the scratch buffers, which FFmpeg mallocs and frees per call -----

    /// <summary>The first half of <c>er_temp_buffer</c>: <c>blocklist</c>, x and y interleaved.</summary>
    public int[]? Blocklist;

    /// <summary>The second half: <c>next_blocklist</c>.</summary>
    public int[]? NextBlocklist;

    /// <summary>The tail of <c>er_temp_buffer</c>: <c>fixed</c>, one byte per macroblock.</summary>
    public byte[]? Fixed;

    /// <summary><c>guess_dc</c>'s <c>col</c>, four entries per block.</summary>
    public short[]? GuessDcCol;

    /// <summary><c>guess_dc</c>'s <c>dist</c>.</summary>
    public uint[]? GuessDcDist;

    /// <summary><c>guess_mv</c>'s <c>mv_predictor[8][2]</c>, x component.</summary>
    public int[]? MvPredictorX;

    /// <inheritdoc cref="MvPredictorX"/>
    public int[]? MvPredictorY;

    /// <summary><c>guess_mv</c>'s <c>ref[8]</c>.</summary>
    public int[]? PredictorRef;
}

/// <summary>Error resilience and concealment (<c>error_resilience.c</c>).</summary>
public static class ErrorResilience
{
    private const int MvFrozen = 8;
    private const int MvChanged = 4;
    private const int MvUnchanged = 2;
    private const int MvListed = 1;

    /// <summary><c>threshold_part[4]</c> - only the first three are ever read.</summary>
    private static readonly int[] ThresholdPart = [100, 100, 100];

    /// <summary>
    /// <c>ff_er_frame_start</c> - marks the whole picture as missing, so that every macroblock a
    /// slice does not claim counts as damaged.
    /// </summary>
    public static void ErFrameStart(ref ErContext s)
    {
        if (s.ErrorConcealment == ErrorConcealment.None)
            return;

        // ff_me_cmp_init and the mecc_inited flag have no counterpart: the SAD is a direct call.
        s.ErrorStatusTable!.AsSpan(0, s.MbStride * s.MbHeight)
            .Fill(ErStatus.MbError | ErStatus.VpStart | ErStatus.MbEnd);

        s.ErrorCount = 3 * s.MbNum;
        s.ErrorOccurred = 0;
    }

    /// <summary><c>er_supported</c> - field pictures are not concealed.</summary>
    private static bool ErSupported(ref ErContext s)
        => s.CurPic.Frame is not null && s.CurPic.FieldPicture == 0;

    /// <summary>
    /// <c>ff_er_add_slice</c> - records that the macroblocks from one position to another either
    /// decoded cleanly or did not.
    /// </summary>
    /// <param name="endx">
    /// x of the LAST macroblock, which may be -1 meaning the last of the previous row.
    /// </param>
    /// <param name="status">
    /// The status at the end: <see cref="ErStatus.MbEnd"/> for a clean slice, or
    /// <see cref="ErStatus.MbError"/> for a damaged one.
    /// </param>
    public static void ErAddSlice(ref ErContext s, int startx, int starty, int endx, int endy, int status)
    {
        int startI = Math.Clamp(startx + starty * s.MbWidth, 0, s.MbNum - 1);
        int endI = Math.Clamp(endx + endy * s.MbWidth, 0, s.MbNum);
        int startXy = s.MbIndex2Xy![startI];
        int endXy = s.MbIndex2Xy[endI];
        int mask = -1;

        if (startI > endI || startXy > endXy)
        {
            // FFmpeg logs "internal error, slice end before start" and returns. A slice whose
            // end precedes its start says nothing about either, so there is nothing to record.
            return;
        }

        if (s.ErrorConcealment == ErrorConcealment.None)
            return;

        mask &= ~ErStatus.VpStart;

        if ((status & (ErStatus.AcError | ErStatus.AcEnd)) != 0)
        {
            mask &= ~(ErStatus.AcError | ErStatus.AcEnd);
            s.ErrorCount += startI - endI - 1;
        }

        if ((status & (ErStatus.DcError | ErStatus.DcEnd)) != 0)
        {
            mask &= ~(ErStatus.DcError | ErStatus.DcEnd);
            s.ErrorCount += startI - endI - 1;
        }

        if ((status & (ErStatus.MvError | ErStatus.MvEnd)) != 0)
        {
            mask &= ~(ErStatus.MvError | ErStatus.MvEnd);
            s.ErrorCount += startI - endI - 1;
        }

        if ((status & ErStatus.MbError) != 0)
        {
            s.ErrorOccurred = 1;
            s.ErrorCount = int.MaxValue;
        }

        byte[] table = s.ErrorStatusTable!;

        if (mask == ~0x7F)
        {
            table.AsSpan(startXy, endXy - startXy).Clear();
        }
        else
        {
            for (int i = startXy; i < endXy; i++)
                table[i] &= (byte)mask;
        }

        if (endI == s.MbNum)
        {
            s.ErrorCount = int.MaxValue;
        }
        else
        {
            table[endXy] &= (byte)mask;
            table[endXy] |= (byte)status;
        }

        table[startXy] |= ErStatus.VpStart;

        // skip_top is always zero here, so the C's `s->avctx->skip_top * s->mb_width < start_i`
        // reduces to start_i > 0, which start_xy > 0 already implies for this port's index map.
        if (startXy > 0 && ErSupported(ref s) && startI > 0)
        {
            int prevStatus = table[s.MbIndex2Xy[startI - 1]];
            prevStatus &= ~ErStatus.VpStart;

            if (prevStatus != (ErStatus.MvEnd | ErStatus.DcEnd | ErStatus.AcEnd))
            {
                s.ErrorOccurred = 1;
                s.ErrorCount = int.MaxValue;
            }
        }
    }

    /// <summary><c>set_mv_strides</c> - MPEG-1/2 stores one vector per 8x8 block.</summary>
    /// <remarks>
    /// The H.264 arm is absent; H.264 does not use this layer in this port. It is recorded as a
    /// divergence rather than left as dead code that no stream can reach.
    /// </remarks>
    private static void SetMvStrides(ref ErContext s, out int mvStep, out int stride)
    {
        mvStep = 2;
        stride = s.B8Stride;
    }

    /// <summary><c>put_dc</c> - replaces a macroblock with a flat DC-only version.</summary>
    private static void PutDc(ref ErContext s, Span<byte> frame, int destY, int destCb, int destCr,
                              bool hasChroma, int mbX, int mbY)
    {
        int linesizeY = s.CurPic.Linesize[0];
        int linesizeCb = s.CurPic.Linesize[1];
        int linesizeCr = s.CurPic.Linesize[2];

        short[] dcY = s.DcValY!;

        for (int i = 0; i < 4; i++)
        {
            int dc = dcY[mbX * 2 + (i & 1) + (mbY * 2 + (i >> 1)) * s.B8Stride];
            dc = Math.Clamp(dc, 0, 2040);

            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    frame[destY + x + (i & 1) * 8 + (y + (i >> 1) * 8) * linesizeY] = (byte)(dc / 8);
        }

        int dcu = s.DcValCb![mbX + mbY * s.MbStride];
        int dcv = s.DcValCr![mbX + mbY * s.MbStride];
        dcu = Math.Clamp(dcu, 0, 2040);
        dcv = Math.Clamp(dcv, 0, 2040);

        if (!hasChroma)
            return;

        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                frame[destCb + x + y * linesizeCb] = (byte)(dcu / 8);
                frame[destCr + x + y * linesizeCr] = (byte)(dcv / 8);
            }
    }

    /// <summary>
    /// <c>filter181</c> - a sharpening pass over the luma DC field, horizontally then
    /// vertically.
    /// </summary>
    /// <remarks>
    /// The 1/-8/1 kernel is normalised by multiplying by 10923 and shifting 16, which is 1/6
    /// in fixed point; the clip keeps that multiply inside an int, exactly as the C does.
    /// </remarks>
    private static void Filter181(short[] data, int width, int height, int stride)
    {
        const int lo = int.MinValue / 10923;
        const int hi = (int.MaxValue / 10923) - 32768;

        for (int y = 1; y < height - 1; y++)
        {
            int prevDc = data[0 + y * stride];

            for (int x = 1; x < width - 1; x++)
            {
                int dc = -prevDc + data[x + y * stride] * 8 - data[x + 1 + y * stride];
                dc = (Math.Clamp(dc, lo, hi) * 10923 + 32768) >> 16;
                prevDc = data[x + y * stride];
                data[x + y * stride] = (short)dc;
            }
        }

        for (int x = 1; x < width - 1; x++)
        {
            int prevDc = data[x];

            for (int y = 1; y < height - 1; y++)
            {
                int dc = -prevDc + data[x + y * stride] * 8 - data[x + (y + 1) * stride];
                dc = (Math.Clamp(dc, lo, hi) * 10923 + 32768) >> 16;
                prevDc = data[x + y * stride];
                data[x + y * stride] = (short)dc;
            }
        }
    }

    /// <summary>
    /// <c>guess_dc</c> - fills in the DC of every block whose own DC was lost, by interpolating
    /// from the nearest undamaged block in each of the four directions.
    /// </summary>
    /// <param name="w">Width in 8-pixel blocks.</param>
    /// <param name="h">Height in 8-pixel blocks.</param>
    private static void GuessDc(ref ErContext s, short[] dc, int w, int h, int stride, int isLuma)
    {
        int entries = stride * h * 4;

        if (s.GuessDcCol is null || s.GuessDcCol.Length < entries)
            s.GuessDcCol = FrameBufferPool<short>.Rent(entries);
        if (s.GuessDcDist is null || s.GuessDcDist.Length < entries)
            s.GuessDcDist = FrameBufferPool<uint>.Rent(entries);

        short[] col = s.GuessDcCol;
        uint[] dist = s.GuessDcDist;
        byte[] status = s.ErrorStatusTable!;
        uint[] mbType = s.CurPic.MbType!;
        int mbTypeOffset = s.CurPic.MbTypeOffset;

        for (int bY = 0; bY < h; bY++)
        {
            int color = 1024;
            int distance = -1;

            for (int bX = 0; bX < w; bX++)
            {
                int mbIndexJ = (bX >> isLuma) + (bY >> isLuma) * s.MbStride;
                int errorJ = status[mbIndexJ];
                bool intraJ = MbType.IsIntra(mbType[mbTypeOffset + mbIndexJ]);

                if (!intraJ || (errorJ & ErStatus.DcError) == 0)
                {
                    color = dc[bX + bY * stride];
                    distance = bX;
                }

                col[(bX + bY * stride) * 4 + 1] = (short)color;
                dist[(bX + bY * stride) * 4 + 1] = distance >= 0 ? (uint)(bX - distance) : 9999;
            }

            color = 1024;
            distance = -1;

            for (int bX = w - 1; bX >= 0; bX--)
            {
                int mbIndexJ = (bX >> isLuma) + (bY >> isLuma) * s.MbStride;
                int errorJ = status[mbIndexJ];
                bool intraJ = MbType.IsIntra(mbType[mbTypeOffset + mbIndexJ]);

                if (!intraJ || (errorJ & ErStatus.DcError) == 0)
                {
                    color = dc[bX + bY * stride];
                    distance = bX;
                }

                col[(bX + bY * stride) * 4 + 0] = (short)color;
                dist[(bX + bY * stride) * 4 + 0] = distance >= 0 ? (uint)(distance - bX) : 9999;
            }
        }

        for (int bX = 0; bX < w; bX++)
        {
            int color = 1024;
            int distance = -1;

            for (int bY = 0; bY < h; bY++)
            {
                int mbIndexJ = (bX >> isLuma) + (bY >> isLuma) * s.MbStride;
                int errorJ = status[mbIndexJ];
                bool intraJ = MbType.IsIntra(mbType[mbTypeOffset + mbIndexJ]);

                if (!intraJ || (errorJ & ErStatus.DcError) == 0)
                {
                    color = dc[bX + bY * stride];
                    distance = bY;
                }

                col[(bX + bY * stride) * 4 + 3] = (short)color;
                dist[(bX + bY * stride) * 4 + 3] = distance >= 0 ? (uint)(bY - distance) : 9999;
            }

            color = 1024;
            distance = -1;

            for (int bY = h - 1; bY >= 0; bY--)
            {
                int mbIndexJ = (bX >> isLuma) + (bY >> isLuma) * s.MbStride;
                int errorJ = status[mbIndexJ];
                bool intraJ = MbType.IsIntra(mbType[mbTypeOffset + mbIndexJ]);

                if (!intraJ || (errorJ & ErStatus.DcError) == 0)
                {
                    color = dc[bX + bY * stride];
                    distance = bY;
                }

                col[(bX + bY * stride) * 4 + 2] = (short)color;
                dist[(bX + bY * stride) * 4 + 2] = distance >= 0 ? (uint)(distance - bY) : 9999;
            }
        }

        for (int bY = 0; bY < h; bY++)
        {
            for (int bX = 0; bX < w; bX++)
            {
                int mbIndex = (bX >> isLuma) + (bY >> isLuma) * s.MbStride;
                int error = status[mbIndex];

                if (MbType.IsInter(mbType[mbTypeOffset + mbIndex]))
                    continue;
                if ((error & ErStatus.DcError) == 0)
                    continue;

                long weightSum = 0;
                long guess = 0;

                for (int j = 0; j < 4; j++)
                {
                    long weight = 256L * 256 * 256 * 16 / Math.Max(dist[(bX + bY * stride) * 4 + j], 1u);
                    guess += weight * col[(bX + bY * stride) * 4 + j];
                    weightSum += weight;
                }

                guess = (guess + weightSum / 2) / weightSum;
                dc[bX + bY * stride] = (short)guess;
            }
        }
    }

    /// <summary>
    /// <c>h_block_filter</c> - a simple horizontal deblock used only across concealed
    /// boundaries.
    /// </summary>
    private static void HBlockFilter(ref ErContext s, Span<byte> dst, int dstOffset,
                                     int w, int h, int stride, int isLuma)
    {
        SetMvStrides(ref s, out int mvxStride, out int mvyStride);
        mvxStride >>= isLuma;
        mvyStride *= mvxStride;

        byte[] status = s.ErrorStatusTable!;
        uint[] mbType = s.CurPic.MbType!;
        int mbTypeOffset = s.CurPic.MbTypeOffset;
        short[] mv = s.CurPic.MotionValL0!;
        int mvOffset = s.CurPic.MotionValOffsetL0;

        for (int bY = 0; bY < h; bY++)
        {
            for (int bX = 0; bX < w - 1; bX++)
            {
                int leftIndex = (bX >> isLuma) + (bY >> isLuma) * s.MbStride;
                int rightIndex = ((bX + 1) >> isLuma) + (bY >> isLuma) * s.MbStride;

                int leftStatus = status[leftIndex];
                int rightStatus = status[rightIndex];
                bool leftIntra = MbType.IsIntra(mbType[mbTypeOffset + leftIndex]);
                bool rightIntra = MbType.IsIntra(mbType[mbTypeOffset + rightIndex]);
                int leftDamage = leftStatus & ErStatus.MbError;
                int rightDamage = rightStatus & ErStatus.MbError;
                int offset = dstOffset + bX * 8 + bY * stride * 8;

                int leftMv = mvOffset + 2 * (mvyStride * bY + mvxStride * bX);
                int rightMv = mvOffset + 2 * (mvyStride * bY + mvxStride * (bX + 1));

                if (leftDamage == 0 && rightDamage == 0)
                    continue;

                if (!leftIntra && !rightIntra &&
                    Math.Abs(mv[leftMv] - mv[rightMv]) +
                    Math.Abs(mv[leftMv + 1] + mv[rightMv + 1]) < 2)
                {
                    continue;
                }

                for (int y = 0; y < 8; y++)
                {
                    int a = dst[offset + 7 + y * stride] - dst[offset + 6 + y * stride];
                    int b = dst[offset + 8 + y * stride] - dst[offset + 7 + y * stride];
                    int c = dst[offset + 9 + y * stride] - dst[offset + 8 + y * stride];

                    int d = Math.Abs(b) - ((Math.Abs(a) + Math.Abs(c) + 1) >> 1);
                    d = Math.Max(d, 0);
                    if (b < 0) d = -d;
                    if (d == 0) continue;

                    if (!(leftDamage != 0 && rightDamage != 0))
                        d = d * 16 / 9;

                    if (leftDamage != 0)
                    {
                        dst[offset + 7 + y * stride] = Clip255(dst[offset + 7 + y * stride] + ((d * 7) >> 4));
                        dst[offset + 6 + y * stride] = Clip255(dst[offset + 6 + y * stride] + ((d * 5) >> 4));
                        dst[offset + 5 + y * stride] = Clip255(dst[offset + 5 + y * stride] + ((d * 3) >> 4));
                        dst[offset + 4 + y * stride] = Clip255(dst[offset + 4 + y * stride] + ((d * 1) >> 4));
                    }

                    if (rightDamage != 0)
                    {
                        dst[offset + 8 + y * stride] = Clip255(dst[offset + 8 + y * stride] - ((d * 7) >> 4));
                        dst[offset + 9 + y * stride] = Clip255(dst[offset + 9 + y * stride] - ((d * 5) >> 4));
                        dst[offset + 10 + y * stride] = Clip255(dst[offset + 10 + y * stride] - ((d * 3) >> 4));
                        dst[offset + 11 + y * stride] = Clip255(dst[offset + 11 + y * stride] - ((d * 1) >> 4));
                    }
                }
            }
        }
    }

    /// <summary><c>v_block_filter</c> - the same filter across horizontal boundaries.</summary>
    private static void VBlockFilter(ref ErContext s, Span<byte> dst, int dstOffset,
                                     int w, int h, int stride, int isLuma)
    {
        SetMvStrides(ref s, out int mvxStride, out int mvyStride);
        mvxStride >>= isLuma;
        mvyStride *= mvxStride;

        byte[] status = s.ErrorStatusTable!;
        uint[] mbType = s.CurPic.MbType!;
        int mbTypeOffset = s.CurPic.MbTypeOffset;
        short[] mv = s.CurPic.MotionValL0!;
        int mvOffset = s.CurPic.MotionValOffsetL0;

        for (int bY = 0; bY < h - 1; bY++)
        {
            for (int bX = 0; bX < w; bX++)
            {
                int topIndex = (bX >> isLuma) + (bY >> isLuma) * s.MbStride;
                int bottomIndex = (bX >> isLuma) + ((bY + 1) >> isLuma) * s.MbStride;

                int topStatus = status[topIndex];
                int bottomStatus = status[bottomIndex];
                bool topIntra = MbType.IsIntra(mbType[mbTypeOffset + topIndex]);
                bool bottomIntra = MbType.IsIntra(mbType[mbTypeOffset + bottomIndex]);
                int topDamage = topStatus & ErStatus.MbError;
                int bottomDamage = bottomStatus & ErStatus.MbError;
                int offset = dstOffset + bX * 8 + bY * stride * 8;

                int topMv = mvOffset + 2 * (mvyStride * bY + mvxStride * bX);
                int bottomMv = mvOffset + 2 * (mvyStride * (bY + 1) + mvxStride * bX);

                if (topDamage == 0 && bottomDamage == 0)
                    continue;

                if (!topIntra && !bottomIntra &&
                    Math.Abs(mv[topMv] - mv[bottomMv]) +
                    Math.Abs(mv[topMv + 1] + mv[bottomMv + 1]) < 2)
                {
                    continue;
                }

                for (int x = 0; x < 8; x++)
                {
                    int a = dst[offset + x + 7 * stride] - dst[offset + x + 6 * stride];
                    int b = dst[offset + x + 8 * stride] - dst[offset + x + 7 * stride];
                    int c = dst[offset + x + 9 * stride] - dst[offset + x + 8 * stride];

                    int d = Math.Abs(b) - ((Math.Abs(a) + Math.Abs(c) + 1) >> 1);
                    d = Math.Max(d, 0);
                    if (b < 0) d = -d;
                    if (d == 0) continue;

                    if (!(topDamage != 0 && bottomDamage != 0))
                        d = d * 16 / 9;

                    if (topDamage != 0)
                    {
                        dst[offset + x + 7 * stride] = Clip255(dst[offset + x + 7 * stride] + ((d * 7) >> 4));
                        dst[offset + x + 6 * stride] = Clip255(dst[offset + x + 6 * stride] + ((d * 5) >> 4));
                        dst[offset + x + 5 * stride] = Clip255(dst[offset + x + 5 * stride] + ((d * 3) >> 4));
                        dst[offset + x + 4 * stride] = Clip255(dst[offset + x + 4 * stride] + ((d * 1) >> 4));
                    }

                    if (bottomDamage != 0)
                    {
                        dst[offset + x + 8 * stride] = Clip255(dst[offset + x + 8 * stride] - ((d * 7) >> 4));
                        dst[offset + x + 9 * stride] = Clip255(dst[offset + x + 9 * stride] - ((d * 5) >> 4));
                        dst[offset + x + 10 * stride] = Clip255(dst[offset + x + 10 * stride] - ((d * 3) >> 4));
                        dst[offset + x + 11 * stride] = Clip255(dst[offset + x + 11 * stride] - ((d * 1) >> 4));
                    }
                }
            }
        }
    }

    /// <summary><c>ff_crop_tab[MAX_NEG_CROP + x]</c>.</summary>
    private static byte Clip255(int x) => (byte)(x < 0 ? 0 : x > 255 ? 255 : x);

    /// <summary><c>add_blocklist</c>.</summary>
    private static void AddBlocklist(int[] blocklist, ref int blocklistLength, byte[] fixedTable,
                                     int mbX, int mbY, int mbXy)
    {
        if (fixedTable[mbXy] != 0)
            return;

        fixedTable[mbXy] = MvListed;
        blocklist[blocklistLength * 2] = mbX;
        blocklist[blocklistLength * 2 + 1] = mbY;
        blocklistLength++;
    }

    /// <summary>
    /// <c>guess_mv</c> - fills in the motion vector of every macroblock whose own was lost.
    /// </summary>
    /// <remarks>
    /// The algorithm is a flood fill outwards from the macroblocks that survived. Each candidate
    /// collects the vectors of its already-fixed neighbours, adds their mean, their median and
    /// the zero vector, reconstructs the macroblock with each in turn, and keeps the one whose
    /// edges match its neighbours best. The checkerboard test - <c>(mb_x ^ mb_y ^ pass) &amp;
    /// 1</c> - alternates which half of the grid moves on each pass, so a candidate never reads
    /// a neighbour that is being rewritten in the same pass.
    /// </remarks>
    private static void GuessMv(ref MpegEncContext ctx)
    {
        ref ErContext s = ref ctx.Er;

        int mbStride = s.MbStride;
        int mbWidth = s.MbWidth;
        int mbHeight = s.MbHeight;

        if (s.LastPic.Frame is not null)
            mbHeight = Math.Min(mbHeight, (s.LastPic.Height + 15) >> 4);
        if (s.NextPic.Frame is not null)
            mbHeight = Math.Min(mbHeight, (s.NextPic.Height + 15) >> 4);

        int[] blocklist = s.Blocklist!;
        int[] nextBlocklist = s.NextBlocklist!;
        byte[] fixedTable = s.Fixed!;

        SetMvStrides(ref s, out int motStep, out int motStride);

        byte[] status = s.ErrorStatusTable!;
        uint[] curMbType = s.CurPic.MbType!;
        int curMbTypeOffset = s.CurPic.MbTypeOffset;

        int numAvail = 0;

        for (int i = 0; i < mbWidth * mbHeight; i++)
        {
            int mbXy = s.MbIndex2Xy![i];
            int f = 0;
            int error = status[mbXy];

            if (MbType.IsIntra(curMbType[curMbTypeOffset + mbXy]))
                f = MvFrozen;
            if ((error & ErStatus.MvError) == 0)
                f = MvFrozen;

            fixedTable[mbXy] = (byte)f;

            if (f == MvFrozen)
            {
                numAvail++;
            }
            else if (s.LastPic.Frame is not null && s.LastPic.MotionValL0 is not null)
            {
                int mbY0 = mbXy / s.MbStride;
                int mbX0 = mbXy % s.MbStride;
                int motIndex = (mbX0 + mbY0 * motStride) * motStep;

                s.CurPic.MotionValL0![s.CurPic.MotionValOffsetL0 + motIndex * 2] =
                    s.LastPic.MotionValL0[s.LastPic.MotionValOffsetL0 + motIndex * 2];
                s.CurPic.MotionValL0[s.CurPic.MotionValOffsetL0 + motIndex * 2 + 1] =
                    s.LastPic.MotionValL0[s.LastPic.MotionValOffsetL0 + motIndex * 2 + 1];
                s.CurPic.RefIndexL0![4 * mbXy] = s.LastPic.RefIndexL0![4 * mbXy];
            }
        }

        if ((s.ErrorConcealment & ErrorConcealment.GuessMvs) == 0 ||
            numAvail <= Math.Max(mbWidth, mbHeight) / 2)
        {
            for (int mbY = 0; mbY < mbHeight; mbY++)
            {
                for (int mbX = 0; mbX < s.MbWidth; mbX++)
                {
                    int mbXy = mbX + mbY * s.MbStride;
                    int mvDir = s.LastPic.Frame is not null
                        ? MpegVideoConstants.MvDirForward
                        : MpegVideoConstants.MvDirBackward;

                    if (MbType.IsIntra(curMbType[curMbTypeOffset + mbXy]))
                        continue;
                    if ((status[mbXy] & ErStatus.MvError) == 0)
                        continue;

                    ctx.Er.Mv[0] = 0;
                    ctx.Er.Mv[1] = 0;
                    MpegEr.MpegErDecodeMb(ref ctx, 0, mvDir, MpegVideoConstants.MvType16X16,
                                          mbX, mbY, 0, 0);
                }
            }

            return;
        }

        int blocklistLength = 0;

        for (int mbY = 0; mbY < mbHeight; mbY++)
        {
            for (int mbX = 0; mbX < mbWidth; mbX++)
            {
                int mbXy = mbX + mbY * mbStride;

                if (fixedTable[mbXy] != MvFrozen)
                    continue;

                if (mbX > 0) AddBlocklist(blocklist, ref blocklistLength, fixedTable, mbX - 1, mbY, mbXy - 1);
                if (mbY > 0) AddBlocklist(blocklist, ref blocklistLength, fixedTable, mbX, mbY - 1, mbXy - mbStride);
                if (mbX + 1 < mbWidth) AddBlocklist(blocklist, ref blocklistLength, fixedTable, mbX + 1, mbY, mbXy + 1);
                if (mbY + 1 < mbHeight) AddBlocklist(blocklist, ref blocklistLength, fixedTable, mbX, mbY + 1, mbXy + mbStride);
            }
        }

        // FFmpeg declares these inside the loop; they are context fields here so that a damaged
        // frame allocates nothing, and so the method stays free of localloc.
        int[] mvPredictorX = s.MvPredictorX!;
        int[] mvPredictorY = s.MvPredictorY!;
        int[] refs = s.PredictorRef!;

        for (; ; )
        {
            int noneLeft = 1;
            int changed = 1;

            for (int pass = 0; (changed != 0 || pass < 2) && pass < 10; pass++)
            {
                changed = 0;

                for (int blocklistIndex = 0; blocklistIndex < blocklistLength; blocklistIndex++)
                {
                    int mbX = blocklist[blocklistIndex * 2];
                    int mbY = blocklist[blocklistIndex * 2 + 1];
                    int mbXy = mbX + mbY * mbStride;

                    if (((mbX ^ mbY ^ pass) & 1) != 0)
                        continue;

                    int j = 0;
                    if (mbX > 0) j |= fixedTable[mbXy - 1];
                    if (mbX + 1 < mbWidth) j |= fixedTable[mbXy + 1];
                    if (mbY > 0) j |= fixedTable[mbXy - mbStride];
                    if (mbY + 1 < mbHeight) j |= fixedTable[mbXy + mbStride];

                    if ((j & MvChanged) == 0 && pass > 1)
                        continue;

                    noneLeft = 0;
                    int predCount = 0;
                    int motIndex = (mbX + mbY * motStride) * motStep;

                    short[] curMv = s.CurPic.MotionValL0!;
                    int curMvOffset = s.CurPic.MotionValOffsetL0;
                    sbyte[] curRef = s.CurPic.RefIndexL0!;

                    if (mbX > 0 && fixedTable[mbXy - 1] > 1)
                    {
                        mvPredictorX[predCount] = curMv[curMvOffset + (motIndex - motStep) * 2];
                        mvPredictorY[predCount] = curMv[curMvOffset + (motIndex - motStep) * 2 + 1];
                        refs[predCount] = curRef[4 * (mbXy - 1)];
                        predCount++;
                    }

                    if (mbX + 1 < mbWidth && fixedTable[mbXy + 1] > 1)
                    {
                        mvPredictorX[predCount] = curMv[curMvOffset + (motIndex + motStep) * 2];
                        mvPredictorY[predCount] = curMv[curMvOffset + (motIndex + motStep) * 2 + 1];
                        refs[predCount] = curRef[4 * (mbXy + 1)];
                        predCount++;
                    }

                    if (mbY > 0 && fixedTable[mbXy - mbStride] > 1)
                    {
                        mvPredictorX[predCount] = curMv[curMvOffset + (motIndex - motStride * motStep) * 2];
                        mvPredictorY[predCount] = curMv[curMvOffset + (motIndex - motStride * motStep) * 2 + 1];
                        refs[predCount] = curRef[4 * (mbXy - s.MbStride)];
                        predCount++;
                    }

                    if (mbY + 1 < mbHeight && fixedTable[mbXy + mbStride] > 1)
                    {
                        mvPredictorX[predCount] = curMv[curMvOffset + (motIndex + motStride * motStep) * 2];
                        mvPredictorY[predCount] = curMv[curMvOffset + (motIndex + motStride * motStep) * 2 + 1];
                        refs[predCount] = curRef[4 * (mbXy + s.MbStride)];
                        predCount++;
                    }

                    if (predCount == 0)
                        continue;

                    if (predCount > 1)
                    {
                        int sumX = 0, sumY = 0, sumR = 0;
                        bool mixedRefs = false;
                        int k;

                        for (k = 0; k < predCount; k++)
                        {
                            sumX += mvPredictorX[k];
                            sumY += mvPredictorY[k];
                            sumR += refs[k];

                            if (k != 0 && refs[k] != refs[k - 1]) { mixedRefs = true; break; }
                        }

                        // The C reaches skip_mean_and_median by goto, with j left at the loop
                        // counter; the mean therefore divides by however many were summed.
                        if (!mixedRefs)
                        {
                            mvPredictorX[predCount] = sumX / k;
                            mvPredictorY[predCount] = sumY / k;
                            refs[predCount] = sumR / k;

                            int maxX, maxY, maxR, minX, minY, minR;

                            if (predCount >= 3)
                            {
                                minY = minX = minR = 99999;
                                maxY = maxX = maxR = -99999;
                            }
                            else
                            {
                                minX = minY = maxX = maxY = minR = maxR = 0;
                            }

                            for (int m = 0; m < predCount; m++)
                            {
                                maxX = Math.Max(maxX, mvPredictorX[m]);
                                maxY = Math.Max(maxY, mvPredictorY[m]);
                                maxR = Math.Max(maxR, refs[m]);
                                minX = Math.Min(minX, mvPredictorX[m]);
                                minY = Math.Min(minY, mvPredictorY[m]);
                                minR = Math.Min(minR, refs[m]);
                            }

                            mvPredictorX[predCount + 1] = sumX - maxX - minX;
                            mvPredictorY[predCount + 1] = sumY - maxY - minY;
                            refs[predCount + 1] = sumR - maxR - minR;

                            if (predCount == 4)
                            {
                                mvPredictorX[predCount + 1] /= 2;
                                mvPredictorY[predCount + 1] /= 2;
                                refs[predCount + 1] /= 2;
                            }

                            predCount += 2;
                        }
                    }

                    // skip_mean_and_median: the zero vector is always a candidate.
                    mvPredictorX[predCount] = 0;
                    mvPredictorY[predCount] = 0;
                    refs[predCount] = 0;
                    predCount++;

                    int prevX = curMv[curMvOffset + motIndex * 2];
                    int prevY = curMv[curMvOffset + motIndex * 2 + 1];
                    int prevRef = curRef[4 * mbXy];

                    mvPredictorX[predCount] = prevX;
                    mvPredictorY[predCount] = prevY;
                    refs[predCount] = prevRef;
                    predCount++;

                    int bestPred = 0;
                    int bestScore = 256 * 256 * 256 * 64;
                    int linesize = s.CurPic.Linesize[0];
                    byte[] frame = s.CurPic.Frame!;
                    int src = s.CurPic.Data[0] + mbX * 16 + mbY * 16 * linesize;

                    for (int p = 0; p < predCount; p++)
                    {
                        int score = 0;

                        curMv[curMvOffset + motIndex * 2] = (short)mvPredictorX[p];
                        ctx.Er.Mv[0] = mvPredictorX[p];
                        curMv[curMvOffset + motIndex * 2 + 1] = (short)mvPredictorY[p];
                        ctx.Er.Mv[1] = mvPredictorY[p];

                        // A predictor taken from an intra or unavailable neighbour.
                        if (refs[p] < 0)
                            continue;

                        MpegEr.MpegErDecodeMb(ref ctx, refs[p], MpegVideoConstants.MvDirForward,
                                              MpegVideoConstants.MvType16X16, mbX, mbY, 0, 0);

                        if (mbX > 0 && fixedTable[mbXy - 1] > 1)
                            for (int k = 0; k < 16; k++)
                                score += Math.Abs(frame[src + k * linesize - 1] - frame[src + k * linesize]);

                        if (mbX + 1 < mbWidth && fixedTable[mbXy + 1] > 1)
                            for (int k = 0; k < 16; k++)
                                score += Math.Abs(frame[src + k * linesize + 15] - frame[src + k * linesize + 16]);

                        if (mbY > 0 && fixedTable[mbXy - mbStride] > 1)
                            for (int k = 0; k < 16; k++)
                                score += Math.Abs(frame[src + k - linesize] - frame[src + k]);

                        if (mbY + 1 < mbHeight && fixedTable[mbXy + mbStride] > 1)
                            for (int k = 0; k < 16; k++)
                                score += Math.Abs(frame[src + k + linesize * 15] - frame[src + k + linesize * 16]);

                        // <= favours the last predictor, which is the one already in place.
                        if (score <= bestScore)
                        {
                            bestScore = score;
                            bestPred = p;
                        }
                    }

                    ctx.Er.Mv[0] = mvPredictorX[bestPred];
                    ctx.Er.Mv[1] = mvPredictorY[bestPred];

                    for (int a = 0; a < motStep; a++)
                        for (int b = 0; b < motStep; b++)
                        {
                            curMv[curMvOffset + (motIndex + a + b * motStride) * 2] = (short)ctx.Er.Mv[0];
                            curMv[curMvOffset + (motIndex + a + b * motStride) * 2 + 1] = (short)ctx.Er.Mv[1];
                        }

                    MpegEr.MpegErDecodeMb(ref ctx, refs[bestPred], MpegVideoConstants.MvDirForward,
                                          MpegVideoConstants.MvType16X16, mbX, mbY, 0, 0);

                    if (ctx.Er.Mv[0] != prevX || ctx.Er.Mv[1] != prevY)
                    {
                        fixedTable[mbXy] = MvChanged;
                        changed++;
                    }
                    else
                    {
                        fixedTable[mbXy] = MvUnchanged;
                    }
                }
            }

            if (noneLeft != 0)
                return;

            int nextBlocklistLength = 0;

            for (int blocklistIndex = 0; blocklistIndex < blocklistLength; blocklistIndex++)
            {
                int mbX = blocklist[blocklistIndex * 2];
                int mbY = blocklist[blocklistIndex * 2 + 1];
                int mbXy = mbX + mbY * mbStride;

                if ((fixedTable[mbXy] & (MvChanged | MvUnchanged | MvFrozen)) == 0)
                    continue;

                fixedTable[mbXy] = MvFrozen;

                if (mbX > 0) AddBlocklist(nextBlocklist, ref nextBlocklistLength, fixedTable, mbX - 1, mbY, mbXy - 1);
                if (mbY > 0) AddBlocklist(nextBlocklist, ref nextBlocklistLength, fixedTable, mbX, mbY - 1, mbXy - mbStride);
                if (mbX + 1 < mbWidth) AddBlocklist(nextBlocklist, ref nextBlocklistLength, fixedTable, mbX + 1, mbY, mbXy + 1);
                if (mbY + 1 < mbHeight) AddBlocklist(nextBlocklist, ref nextBlocklistLength, fixedTable, mbX, mbY + 1, mbXy + mbStride);
            }

            (blocklistLength, nextBlocklistLength) = (nextBlocklistLength, blocklistLength);
            (blocklist, nextBlocklist) = (nextBlocklist, blocklist);
        }
    }

    /// <summary>
    /// <c>is_intra_more_likely</c> - decides between spatial and temporal concealment for the
    /// macroblocks about which nothing at all is known.
    /// </summary>
    private static bool IsIntraMoreLikely(ref ErContext s)
    {
        if (s.LastPic.Frame is null)
            return true; // No previous frame to copy from, so spatial it is.

        if ((s.ErrorConcealment & ErrorConcealment.FavorInter) != 0)
            return false;

        byte[] status = s.ErrorStatusTable!;
        int undamagedCount = 0;

        for (int i = 0; i < s.MbNum; i++)
        {
            int mbXy = s.MbIndex2Xy![i];
            int error = status[mbXy];

            if (!((error & ErStatus.DcError) != 0 && (error & ErStatus.MvError) != 0))
                undamagedCount++;
        }

        if (undamagedCount < 5)
            return false; // Almost everything is damaged, so copy the previous picture.

        int skipAmount = Math.Max(undamagedCount / 50, 1);
        int isIntraLikely = 0;
        int j = 0;

        uint[] mbType = s.CurPic.MbType!;
        int mbTypeOffset = s.CurPic.MbTypeOffset;

        for (int mbY = 0; mbY < s.MbHeight - 1; mbY++)
        {
            for (int mbX = 0; mbX < s.MbWidth; mbX++)
            {
                int mbXy = mbX + mbY * s.MbStride;
                int error = status[mbXy];

                if ((error & ErStatus.DcError) != 0 && (error & ErStatus.MvError) != 0)
                    continue; // Skip the damaged ones.

                j++;
                if (j % skipAmount != 0)
                    continue;

                if (s.CurPic.PictType == AvPictureType.I)
                {
                    int linesize = s.CurPic.Linesize[0];
                    int mbPtr = s.CurPic.Data[0] + mbX * 16 + mbY * 16 * linesize;
                    int lastMbPtr = s.LastPic.Data[0] + mbX * 16 + mbY * 16 * linesize;

                    // How different this macroblock is from the one below it in the PREVIOUS
                    // picture, against how different the previous picture is from itself one
                    // macroblock down. A still scene makes the first term small.
                    isIntraLikely += MeCmp.PixAbs16(s.LastPic.Frame!, lastMbPtr,
                                                    s.CurPic.Frame!, mbPtr, linesize, 16);
                    isIntraLikely -= MeCmp.PixAbs16(s.LastPic.Frame!, lastMbPtr,
                                                    s.LastPic.Frame!, lastMbPtr + linesize * 16,
                                                    linesize, 16);
                }
                else
                {
                    if (MbType.IsIntra(mbType[mbTypeOffset + mbXy]))
                        isIntraLikely++;
                    else
                        isIntraLikely--;
                }
            }
        }

        return isIntraLikely > 0;
    }

    /// <summary>
    /// <c>ff_er_frame_end</c> - the picture is complete, so repair whatever did not arrive.
    /// </summary>
    public static void ErFrameEnd(ref MpegEncContext ctx)
    {
        ref ErContext s = ref ctx.Er;

        const int threshold = 50;

        // skip_top and skip_bottom are always zero in this port, so the C's check against
        // 3 * mb_width * (skip_top + skip_bottom) reduces to the error_count test before it.
        // lowres and hwaccel likewise do not exist here.
        if (s.ErrorConcealment == ErrorConcealment.None || s.ErrorCount == 0 || !ErSupported(ref s))
            return;

        int linesize0 = s.CurPic.Linesize[0];
        int linesize1 = s.CurPic.Linesize[1];
        int linesize2 = s.CurPic.Linesize[2];
        byte[] status = s.ErrorStatusTable!;

        // An MPEG-2 picture whose height is not a multiple of 32 has a last macroblock row that
        // the encoder never coded. Exactly one row missing, and that row entirely untouched, is
        // that case and not damage.
        if (ctx.CodecId == MpegCodecId.Mpeg2Video &&
            (((ctx.Height + 15) & ~15) & 16) != 0 && s.ErrorCount == 3 * s.MbWidth)
        {
            int mbX;

            for (mbX = 0; mbX < s.MbWidth; mbX++)
            {
                if (status[mbX + (s.MbHeight - 1) * s.MbStride] != 0x7F)
                    break;
            }

            if (mbX == s.MbWidth)
                return; // Ignoring the last missing slice.
        }

        // A reference of a different size cannot be used to conceal from.
        if (s.LastPic.Frame is not null &&
            (s.LastPic.Width != s.CurPic.Width || s.LastPic.Height != s.CurPic.Height))
        {
            s.LastPic = default;
        }

        if (s.NextPic.Frame is not null &&
            (s.NextPic.Width != s.CurPic.Width || s.NextPic.Height != s.CurPic.Height))
        {
            s.NextPic = default;
        }

        // MPEG-1/2 keeps no motion vectors, so ER supplies zeroed ones of its own: the structure
        // of the guess is the same, it simply starts from nothing rather than from the decoder's
        // record. FFmpeg logs "Warning MVs not available" here and does exactly this.
        if (s.CurPic.MotionValL0 is null || s.CurPic.RefIndexL0 is null)
        {
            int size = s.B8Stride * 2 * s.MbHeight;
            int refCount = s.MbStride * s.MbHeight * 4;
            int mvCount = (size + 4) * 2;

            if (s.OwnRefIndexL0 is null || s.OwnRefIndexL0.Length < refCount)
            {
                s.OwnRefIndexL0 = FrameBufferPool<sbyte>.Rent(refCount);
                s.OwnRefIndexL1 = FrameBufferPool<sbyte>.Rent(refCount);
            }

            if (s.OwnMotionValBaseL0 is null || s.OwnMotionValBaseL0.Length < mvCount)
            {
                s.OwnMotionValBaseL0 = FrameBufferPool<short>.Rent(mvCount);
                s.OwnMotionValBaseL1 = FrameBufferPool<short>.Rent(mvCount);
            }

            Array.Clear(s.OwnRefIndexL0, 0, refCount);
            Array.Clear(s.OwnRefIndexL1!, 0, refCount);
            Array.Clear(s.OwnMotionValBaseL0, 0, mvCount);
            Array.Clear(s.OwnMotionValBaseL1!, 0, mvCount);

            s.CurPic.RefIndexL0 = s.OwnRefIndexL0;
            s.CurPic.RefIndexL1 = s.OwnRefIndexL1;
            s.CurPic.MotionValL0 = s.OwnMotionValBaseL0;
            s.CurPic.MotionValL1 = s.OwnMotionValBaseL1;

            // motion_val = motion_val_base + 4, so that the row above the first is addressable.
            s.CurPic.MotionValOffsetL0 = 4 * 2;
            s.CurPic.MotionValOffsetL1 = 4 * 2;
        }

        // Handle overlapping slices.
        for (int errorType = 1; errorType <= 3; errorType++)
        {
            bool endOk = false;

            for (int i = s.MbNum - 1; i >= 0; i--)
            {
                int mbXy = s.MbIndex2Xy![i];
                int error = status[mbXy];

                if ((error & (1 << errorType)) != 0) endOk = true;
                if ((error & (8 << errorType)) != 0) endOk = true;
                if (!endOk) status[mbXy] |= (byte)(1 << errorType);
                if ((error & ErStatus.VpStart) != 0) endOk = false;
            }
        }

        // Handle slices with partitions of different length. MPEG-1/2 never partitions, so this
        // is dead for the only codec on this path today; it is translated because the layer is
        // shared and MPEG-4 will reach it.
        if (s.PartitionedFrame != 0)
        {
            bool endOk = false;

            for (int i = s.MbNum - 1; i >= 0; i--)
            {
                int mbXy = s.MbIndex2Xy![i];
                int error = status[mbXy];

                if ((error & ErStatus.AcEnd) != 0) endOk = false;
                if ((error & ErStatus.MvEnd) != 0 || (error & ErStatus.DcEnd) != 0 ||
                    (error & ErStatus.AcError) != 0)
                {
                    endOk = true;
                }

                if (!endOk) status[mbXy] |= ErStatus.AcError;
                if ((error & ErStatus.VpStart) != 0) endOk = false;
            }
        }

        // The "handle missing slices" block runs only under AV_EF_EXPLODE, which this port does
        // not implement: it exists to turn concealable damage into a hard failure.

        // Backward mark errors.
        int distance = 9999999;

        for (int errorType = 1; errorType <= 3; errorType++)
        {
            for (int i = s.MbNum - 1; i >= 0; i--)
            {
                int mbXy = s.MbIndex2Xy![i];
                int error = status[mbXy];

                if (s.MbskipTable is null || s.MbskipTable[mbXy] == 0)
                    distance++;
                if ((error & (1 << errorType)) != 0)
                    distance = 0;

                if (s.PartitionedFrame != 0)
                {
                    if (distance < ThresholdPart[errorType - 1])
                        status[mbXy] |= (byte)(1 << errorType);
                }
                else
                {
                    if (distance < threshold)
                        status[mbXy] |= (byte)(1 << errorType);
                }

                if ((error & ErStatus.VpStart) != 0)
                    distance = 9999999;
            }
        }

        // Forward mark errors.
        int carried = 0;

        for (int i = 0; i < s.MbNum; i++)
        {
            int mbXy = s.MbIndex2Xy![i];
            int oldError = status[mbXy];

            if ((oldError & ErStatus.VpStart) != 0)
            {
                carried = oldError & ErStatus.MbError;
            }
            else
            {
                carried |= oldError & ErStatus.MbError;
                status[mbXy] |= (byte)carried;
            }
        }

        // The C's "handle not partitioned case" loop reads and writes the same value; it is a
        // no-op kept under #if 1. Translated as nothing, recorded in the checklist.

        uint[] mbTypeTable = s.CurPic.MbType!;
        int mbTypeOffset = s.CurPic.MbTypeOffset;

        bool isIntraLikely = IsIntraMoreLikely(ref s);

        // Set an unknown macroblock type to the most likely one.
        int guessedMbType = isIntraLikely
            ? MbType.Intra
            : MbType.Size16X16 | MbType.ForwardMv;

        for (int i = 0; i < s.MbNum; i++)
        {
            int mbXy = s.MbIndex2Xy![i];
            int error = status[mbXy];

            if (!((error & ErStatus.DcError) != 0 && (error & ErStatus.MvError) != 0))
                continue;

            mbTypeTable[mbTypeOffset + mbXy] = (uint)guessedMbType;
        }

        // With no reference at all, every macroblock has to be concealed spatially.
        if (s.LastPic.Frame is null && s.NextPic.Frame is null)
        {
            for (int i = 0; i < s.MbNum; i++)
            {
                int mbXy = s.MbIndex2Xy![i];

                if (!MbType.IsIntra(mbTypeTable[mbTypeOffset + mbXy]))
                    mbTypeTable[mbTypeOffset + mbXy] = MbType.Intra;
            }
        }

        // Inter blocks whose motion vector survived but whose coefficients did not: redo the
        // prediction and leave the residual out.
        for (int mbY = 0; mbY < s.MbHeight; mbY++)
        {
            for (int mbX = 0; mbX < s.MbWidth; mbX++)
            {
                int mbXy = mbX + mbY * s.MbStride;
                uint mbType = mbTypeTable[mbTypeOffset + mbXy];
                int dir = s.LastPic.Frame is null ? 1 : 0;
                int mvDir = dir != 0 ? MpegVideoConstants.MvDirBackward : MpegVideoConstants.MvDirForward;
                int error = status[mbXy];

                if (MbType.IsIntra(mbType)) continue;
                if ((error & ErStatus.MvError) != 0) continue;
                if ((error & ErStatus.AcError) == 0) continue;

                short[] mv = dir != 0 ? s.CurPic.MotionValL1! : s.CurPic.MotionValL0!;
                int mvOffset = dir != 0 ? s.CurPic.MotionValOffsetL1 : s.CurPic.MotionValOffsetL0;
                int mvType;

                if (MbType.Is8X8(mbType))
                {
                    int mbIndex = mbX * 2 + mbY * 2 * s.B8Stride;
                    mvType = MpegVideoConstants.MvType8X8;

                    for (int j = 0; j < 4; j++)
                    {
                        int at = mvOffset + (mbIndex + (j & 1) + (j >> 1) * s.B8Stride) * 2;
                        ctx.Er.Mv[j * 2] = mv[at];
                        ctx.Er.Mv[j * 2 + 1] = mv[at + 1];
                    }
                }
                else
                {
                    mvType = MpegVideoConstants.MvType16X16;
                    int at = mvOffset + (mbX * 2 + mbY * 2 * s.B8Stride) * 2;
                    ctx.Er.Mv[0] = mv[at];
                    ctx.Er.Mv[1] = mv[at + 1];
                }

                MpegEr.MpegErDecodeMb(ref ctx, 0, mvDir, mvType, mbX, mbY, 0, 0);
            }
        }

        // Guess the motion vectors of everything else.
        if (s.CurPic.PictType == AvPictureType.B)
        {
            for (int mbY = 0; mbY < s.MbHeight; mbY++)
            {
                for (int mbX = 0; mbX < s.MbWidth; mbX++)
                {
                    int xy = mbX * 2 + mbY * 2 * s.B8Stride;
                    int mbXy = mbX + mbY * s.MbStride;
                    uint mbType = mbTypeTable[mbTypeOffset + mbXy];
                    int mvDir = MpegVideoConstants.MvDirForward | MpegVideoConstants.MvDirBackward;
                    int error = status[mbXy];

                    if (MbType.IsIntra(mbType)) continue;
                    if ((error & ErStatus.MvError) == 0) continue;
                    if ((error & ErStatus.AcError) == 0) continue;

                    if (s.LastPic.Frame is null) mvDir &= ~MpegVideoConstants.MvDirForward;
                    if (s.NextPic.Frame is null) mvDir &= ~MpegVideoConstants.MvDirBackward;

                    if (s.PpTime != 0)
                    {
                        // Scale the following picture's vector by where this B picture sits
                        // between its two references, which is what temporal direct mode does.
                        int timePp = s.PpTime;
                        int timePb = s.PbTime;
                        short[] nextMv = s.NextPic.MotionValL0!;
                        int at = s.NextPic.MotionValOffsetL0 + xy * 2;

                        ctx.Er.Mv[0] = nextMv[at] * timePb / timePp;
                        ctx.Er.Mv[1] = nextMv[at + 1] * timePb / timePp;
                        ctx.Er.Mv[8] = nextMv[at] * (timePb - timePp) / timePp;
                        ctx.Er.Mv[9] = nextMv[at + 1] * (timePb - timePp) / timePp;
                    }
                    else
                    {
                        ctx.Er.Mv[0] = 0;
                        ctx.Er.Mv[1] = 0;
                        ctx.Er.Mv[8] = 0;
                        ctx.Er.Mv[9] = 0;
                    }

                    MpegEr.MpegErDecodeMb(ref ctx, 0, mvDir, MpegVideoConstants.MvType16X16,
                                          mbX, mbY, 0, 0);
                }
            }
        }
        else
        {
            GuessMv(ref ctx);
        }

        // Fill the DC tables from the samples now in the picture, so that the intra concealment
        // below has undamaged neighbours to interpolate from.
        byte[] frame = s.CurPic.Frame!;

        for (int mbY = 0; mbY < s.MbHeight; mbY++)
        {
            for (int mbX = 0; mbX < s.MbWidth; mbX++)
            {
                int mbXy = mbX + mbY * s.MbStride;
                uint mbType = mbTypeTable[mbTypeOffset + mbXy];

                if (MbType.IsIntra(mbType) && s.PartitionedFrame != 0)
                    continue;

                int destY = s.CurPic.Data[0] + mbX * 16 + mbY * 16 * linesize0;
                int destCb = s.CurPic.Data[1] + mbX * 8 + mbY * 8 * linesize1;
                int destCr = s.CurPic.Data[2] + mbX * 8 + mbY * 8 * linesize2;

                int dcPtr = mbX * 2 + mbY * 2 * s.B8Stride;

                for (int n = 0; n < 4; n++)
                {
                    int dc = 0;

                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                            dc += frame[destY + x + (n & 1) * 8 + (y + (n >> 1) * 8) * linesize0];

                    s.DcValY![dcPtr + (n & 1) + (n >> 1) * s.B8Stride] = (short)((dc + 4) >> 3);
                }

                if (!s.CurPic.HasChroma)
                    continue;

                int dcu = 0, dcv = 0;

                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        dcu += frame[destCb + x + y * linesize1];
                        dcv += frame[destCr + x + y * linesize2];
                    }

                s.DcValCb![mbX + mbY * s.MbStride] = (short)((dcu + 4) >> 3);
                s.DcValCr![mbX + mbY * s.MbStride] = (short)((dcv + 4) >> 3);
            }
        }

        GuessDc(ref s, s.DcValY!, s.MbWidth * 2, s.MbHeight * 2, s.B8Stride, 1);
        GuessDc(ref s, s.DcValCb!, s.MbWidth, s.MbHeight, s.MbStride, 0);
        GuessDc(ref s, s.DcValCr!, s.MbWidth, s.MbHeight, s.MbStride, 0);

        Filter181(s.DcValY!, s.MbWidth * 2, s.MbHeight * 2, s.B8Stride);

        // Render the intra macroblocks that have no coefficients as flat DC.
        for (int mbY = 0; mbY < s.MbHeight; mbY++)
        {
            for (int mbX = 0; mbX < s.MbWidth; mbX++)
            {
                int mbXy = mbX + mbY * s.MbStride;
                uint mbType = mbTypeTable[mbTypeOffset + mbXy];
                int error = status[mbXy];

                if (MbType.IsInter(mbType)) continue;
                if ((error & ErStatus.AcError) == 0) continue;

                int destY = s.CurPic.Data[0] + mbX * 16 + mbY * 16 * linesize0;
                int destCb = s.CurPic.Data[1] + mbX * 8 + mbY * 8 * linesize1;
                int destCr = s.CurPic.Data[2] + mbX * 8 + mbY * 8 * linesize2;

                PutDc(ref s, frame, destY, destCb, destCr, s.CurPic.HasChroma, mbX, mbY);
            }
        }

        if ((s.ErrorConcealment & ErrorConcealment.Deblock) != 0)
        {
            HBlockFilter(ref s, frame, s.CurPic.Data[0], s.MbWidth * 2, s.MbHeight * 2, linesize0, 1);
            VBlockFilter(ref s, frame, s.CurPic.Data[0], s.MbWidth * 2, s.MbHeight * 2, linesize0, 1);

            if (s.CurPic.HasChroma)
            {
                HBlockFilter(ref s, frame, s.CurPic.Data[1], s.MbWidth, s.MbHeight, linesize1, 0);
                HBlockFilter(ref s, frame, s.CurPic.Data[2], s.MbWidth, s.MbHeight, linesize2, 0);
                VBlockFilter(ref s, frame, s.CurPic.Data[1], s.MbWidth, s.MbHeight, linesize1, 0);
                VBlockFilter(ref s, frame, s.CurPic.Data[2], s.MbWidth, s.MbHeight, linesize2, 0);
            }
        }

        // Clean a few tables.
        for (int i = 0; i < s.MbNum; i++)
        {
            int mbXy = s.MbIndex2Xy![i];
            int error = status[mbXy];

            if (s.MbskipTable is not null && s.CurPic.PictType != AvPictureType.B &&
                (error & (ErStatus.DcError | ErStatus.MvError | ErStatus.AcError)) != 0)
            {
                s.MbskipTable[mbXy] = 0;
            }

            if (s.MbintraTable is not null)
                s.MbintraTable[mbXy] = 1;
        }

        s.CurPic = default;
        s.LastPic = default;
        s.NextPic = default;
    }
}

/// <summary>
/// <c>me_cmp.c</c>, as much of it as error resilience reaches.
/// </summary>
public static class MeCmp
{
    /// <summary>
    /// <c>pix_abs16_c</c> - the sum of absolute differences over a 16-wide block, which is what
    /// <c>mecc.sad[0]</c> resolves to.
    /// </summary>
    public static int PixAbs16(byte[] pix1, int offset1, byte[] pix2, int offset2, int lineSize, int h)
    {
        int s = 0;

        for (int i = 0; i < h; i++)
        {
            for (int x = 0; x < 16; x++)
                s += Math.Abs(pix1[offset1 + x] - pix2[offset2 + x]);

            offset1 += lineSize;
            offset2 += lineSize;
        }

        return s;
    }
}

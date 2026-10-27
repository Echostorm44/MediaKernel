// The MPEG-video side of error resilience - a translation of ffmpeg-master/libavcodec/mpeg_er.c.
//
// error_resilience.c is codec-agnostic: it decides WHICH macroblocks need repairing and WHAT
// motion vector to repair them with. This file is the other half - how to turn that decision
// back into samples for an MPEG-video decoder, and where the buffers it works in come from.

using System;
using MediaKernel.Core.Buffers;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>Error resilience wired to the MPEG-video layer (<c>mpeg_er.c</c>).</summary>
public static class MpegEr
{
    /// <summary>
    /// <c>set_erpic</c> - copies out everything error resilience reads of a picture.
    /// </summary>
    /// <remarks>
    /// The plane pointers come from the picture and not from the work picture, which for a field
    /// is offset one line into the frame and strided double. Error resilience declines field
    /// pictures outright, but the same confusion cost this port a whole field once already in
    /// motion compensation, so the distinction is made explicitly rather than assumed away.
    /// </remarks>
    private static void SetErPic(ref MpegEncContext s, ref ErPicture dst, int slot)
    {
        dst = default;

        if (slot == 0)
            return;

        ref MPVPicture src = ref s.BufferPools.Pool!.Picture(slot);

        dst.Frame = src.Frame;
        dst.Data = src.Data;
        dst.Linesize = src.Linesize;
        dst.Width = s.Width;
        dst.Height = s.Height;
        dst.PictType = src.PictType;
        dst.HasChroma = true;

        dst.MotionValL0 = src.MotionValBaseL0;
        dst.MotionValL1 = src.MotionValBaseL1;
        dst.MotionValOffsetL0 = src.MotionValOffsetL0;
        dst.MotionValOffsetL1 = src.MotionValOffsetL1;
        dst.RefIndexL0 = src.RefIndexL0;
        dst.RefIndexL1 = src.RefIndexL1;

        dst.MbType = src.MbTypeBase;
        dst.MbTypeOffset = src.MbTypeOffset;
        dst.FieldPicture = src.FieldPicture;
    }

    /// <summary>
    /// <c>ff_mpeg_er_frame_start</c> - points error resilience at this picture and its two
    /// references, then marks the whole picture missing.
    /// </summary>
    public static void MpegErFrameStart(ref MpegEncContext s)
    {
        SetErPic(ref s, ref s.Er.CurPic, s.CurPic.Ptr);
        SetErPic(ref s, ref s.Er.NextPic, s.NextPic.Ptr);
        SetErPic(ref s, ref s.Er.LastPic, s.LastPic.Ptr);

        s.Er.QuarterSample = s.QuarterSample;

        ErrorResilience.ErFrameStart(ref s.Er);
    }

    /// <summary>
    /// <c>ff_mpv_er_frame_start_ext</c> - the three fields the caller sets before starting.
    /// </summary>
    public static void MpvErFrameStartExt(ref MpegEncContext s, int partitionedFrame,
                                          ushort ppTime, ushort pbTime)
    {
        s.Er.PartitionedFrame = partitionedFrame;
        s.Er.PpTime = ppTime;
        s.Er.PbTime = pbTime;
        MpegErFrameStart(ref s);
    }

    /// <summary>
    /// <c>mpeg_er_decode_mb</c> - reconstructs one macroblock from a guessed motion vector and
    /// no residual at all.
    /// </summary>
    /// <remarks>
    /// Setting every <c>block_last_index</c> to -1 is what disables the residual: the
    /// reconstruction adds a transformed block only where the index says the block has
    /// coefficients, so a macroblock concealed this way is pure prediction.
    /// </remarks>
    public static void MpegErDecodeMb(ref MpegEncContext s, int reference, int mvDir, int mvType,
                                      int mbX, int mbY, int mbIntra, int mbSkipped)
    {
        // The C asserts !mb_intra: concealment never reconstructs an intra macroblock this way,
        // put_dc does that instead.
        if (mbIntra != 0)
            throw new InvalidOperationException("Error concealment reconstructed an intra macroblock.");

        s.MvDir = mvDir;
        s.MvType = mvType;
        s.MbIntra = mbIntra;
        s.MbSkipped = mbSkipped;
        s.MbX = mbX;
        s.MbY = mbY;
        s.Mcsel = 0;

        for (int i = 0; i < 16; i++)
            s.Mv[i] = s.Er.Mv[i];

        for (int i = 0; i < 12; i++)
            s.BlockLastIndex[i] = -1;

        s.Dest[0] = s.CurPic.Data[0] + mbY * 16 * (int)s.Linesize + mbX * 16;
        s.Dest[1] = s.CurPic.Data[1] + mbY * (16 >> s.ChromaYShift) * (int)s.Uvlinesize +
                    mbX * (16 >> s.ChromaXShift);
        s.Dest[2] = s.CurPic.Data[2] + mbY * (16 >> s.ChromaYShift) * (int)s.Uvlinesize +
                    mbX * (16 >> s.ChromaXShift);

        // `reference` selects a field in FFmpeg's interlaced concealment, which it logs as not
        // fully implemented and then ignores. It is ignored here too, and named so that the
        // parameter is not mistaken for something this port dropped.
        _ = reference;

        MpegVideoDec.MpvReconstructMb(ref s, default);
    }

    /// <summary>
    /// <c>ff_mpeg_er_init</c> - gives error resilience its buffers, and with it the parts of
    /// <c>init_context_frame</c> (<c>mpegvideo.c</c>) that only error resilience reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>mb_index2xy</c>, <c>dc_val</c> and <c>mbintra_table</c> belong to
    /// <c>ff_mpv_common_init</c> in FFmpeg, which this port has not translated as a whole: the
    /// MPEG-1/2 decoder sets its dimensions itself. They are allocated here because this is
    /// where their only reader is, and the checklist records them against their own C file.
    /// </para>
    /// <para>
    /// Called whenever the sequence dimensions change, and a no-op when they have not.
    /// </para>
    /// </remarks>
    public static void MpegErInit(ref MpegEncContext s)
    {
        ref ErContext er = ref s.Er;

        int mbArraySize = s.MbHeight * s.MbStride;

        if (er.MbWidth == s.MbWidth && er.MbHeight == s.MbHeight && er.MbStride == s.MbStride &&
            er.ErrorStatusTable is not null)
        {
            return;
        }

        er.ErrorConcealment = ErrorConcealment.Default;

        er.MbNum = s.MbNum;
        er.MbWidth = s.MbWidth;
        er.MbHeight = s.MbHeight;
        er.MbStride = s.MbStride;
        er.B8Stride = s.B8Stride;

        // mb_index2xy maps a macroblock's raster index to its position in the mb_stride-wide
        // tables. The extra last entry is what ff_er_add_slice indexes when a slice runs to the
        // end of the picture.
        s.MbIndex2Xy = FrameBufferPool<int>.Rent(s.MbNum + 1);

        for (int y = 0; y < s.MbHeight; y++)
            for (int x = 0; x < s.MbWidth; x++)
                s.MbIndex2Xy[x + y * s.MbWidth] = x + y * s.MbStride;

        s.MbIndex2Xy[s.MbHeight * s.MbWidth] = (s.MbHeight - 1) * s.MbStride + s.MbWidth;

        er.MbIndex2Xy = s.MbIndex2Xy;

        // dc_val: one per 8x8 block for luma, one per macroblock for each chroma component.
        // FFmpeg carves all three out of one allocation and starts them at 1024, which is the
        // neutral DC for an 8-bit picture at this scale.
        int ySize = s.B8Stride * (2 * s.MbHeight + 1);
        int cSize = s.MbStride * (s.MbHeight + 1);

        er.DcValY = FrameBufferPool<short>.Rent(ySize);
        er.DcValCb = FrameBufferPool<short>.Rent(cSize);
        er.DcValCr = FrameBufferPool<short>.Rent(cSize);

        er.DcValY.AsSpan().Fill(1024);
        er.DcValCb.AsSpan().Fill(1024);
        er.DcValCr.AsSpan().Fill(1024);

        er.ErrorStatusTable = FrameBufferPool<byte>.Rent(mbArraySize);
        er.ErrorStatusTable.AsSpan().Clear();

        // er_temp_buffer, which guess_mv carves a blocklist, a second blocklist and a byte per
        // macroblock out of.
        er.Blocklist = FrameBufferPool<int>.Rent(mbArraySize * 2);
        er.NextBlocklist = FrameBufferPool<int>.Rent(mbArraySize * 2);
        er.Fixed = FrameBufferPool<byte>.Rent(mbArraySize);

        er.MvPredictorX = new int[8];
        er.MvPredictorY = new int[8];
        er.PredictorRef = new int[8];

        if (s.MbintraTable is null || s.MbintraTable.Length < mbArraySize)
            s.MbintraTable = FrameBufferPool<byte>.Rent(mbArraySize);
        s.MbintraTable.AsSpan(0, mbArraySize).Clear();

        er.MbintraTable = s.MbintraTable;
    }

    /// <summary>
    /// Points error resilience at the macroblock skip table, which <c>ff_mpv_frame_start</c>
    /// may have replaced since <see cref="MpegErInit"/> ran.
    /// </summary>
    public static void MpegErBindTables(ref MpegEncContext s) => s.Er.MbskipTable = s.MbskipTable;
}

// Frame-level decoding for the shared MPEG-video layer - a translation of the parts of
// ffmpeg-master/libavcodec/mpegvideo_dec.c and mpegvideo.c that the MPEG-2 decoder reaches.
//
// Being built up as the decode path needs it; see the port checklist for what is translated.

using System;
using MediaKernel.Core.Buffers;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>Frame-level decoding (<c>mpegvideo_dec.c</c>, <c>mpegvideo.c</c>).</summary>
public static class MpegVideoDec
{
    /// <summary>
    /// <c>ff_init_block_index</c> (<c>mpegvideo.c:472</c>) - positions the per-macroblock
    /// destination offsets at the start of a macroblock row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The destinations are deliberately set ONE macroblock to the left. FFmpeg writes
    /// <c>(s->mb_x - 1U) &lt;&lt; width_of_mb</c>, which for the first macroblock of a row
    /// underflows to -16, and the slice loop then advances them by a macroblock BEFORE calling
    /// reconstruction. Translating the underflow away - clamping it, or reordering the advance -
    /// shifts the whole picture one macroblock sideways.
    /// </para>
    /// <para>
    /// The line size comes from the current picture rather than the context, because a field
    /// picture's stride is not the frame's.
    /// </para>
    /// </remarks>
    public static void InitBlockIndex(ref MpegEncContext s, int bitsPerRawSample = 8)
    {
        int linesize = (int)s.CurPic.Linesize[0];
        int uvlinesize = (int)s.CurPic.Linesize[1];
        int widthOfMb = 4 + (bitsPerRawSample > 8 ? 1 : 0);
        int heightOfMb = 4;

        s.BlockIndex[0] = s.B8Stride * (s.MbY * 2) - 2 + s.MbX * 2;
        s.BlockIndex[1] = s.B8Stride * (s.MbY * 2) - 1 + s.MbX * 2;
        s.BlockIndex[2] = s.B8Stride * (s.MbY * 2 + 1) - 2 + s.MbX * 2;
        s.BlockIndex[3] = s.B8Stride * (s.MbY * 2 + 1) - 1 + s.MbX * 2;
        s.BlockIndex[4] = s.MbStride * (s.MbY + 1) + s.B8Stride * s.MbHeight * 2 + s.MbX - 1;
        s.BlockIndex[5] = s.MbStride * (s.MbY + s.MbHeight + 2) + s.B8Stride * s.MbHeight * 2 + s.MbX - 1;

        // The unsigned underflow at mb_x == 0 is load-bearing; see the remarks.
        int lumaStep = (int)((uint)(s.MbX - 1) << widthOfMb);
        int chromaStep = (int)((uint)(s.MbX - 1) << (widthOfMb - s.ChromaXShift));

        s.Dest[0] = s.CurPic.Data[0] + lumaStep;
        s.Dest[1] = s.CurPic.Data[1] + chromaStep;
        s.Dest[2] = s.CurPic.Data[2] + chromaStep;

        if (s.PictureStructure == 3)   // PICT_FRAME
        {
            s.Dest[0] += s.MbY * linesize << heightOfMb;
            s.Dest[1] += s.MbY * uvlinesize << (heightOfMb - s.ChromaYShift);
            s.Dest[2] += s.MbY * uvlinesize << (heightOfMb - s.ChromaYShift);
        }
        else
        {
            s.Dest[0] += (s.MbY >> 1) * linesize << heightOfMb;
            s.Dest[1] += (s.MbY >> 1) * uvlinesize << (heightOfMb - s.ChromaYShift);
            s.Dest[2] += (s.MbY >> 1) * uvlinesize << (heightOfMb - s.ChromaYShift);
        }
    }

    /// <summary>
    /// <c>ff_set_qscale</c> (<c>mpegvideo.c:505</c>) - sets the quantiser scale and the DC
    /// scales derived from it.
    /// </summary>
    public static void SetQscale(ref MpegEncContext s, int qscale)
    {
        if (qscale < 1) qscale = 1;
        else if (qscale > 31) qscale = 31;

        s.Qscale = qscale;
        s.ChromaQscale = s.ChromaQscaleTable is null ? qscale : s.ChromaQscaleTable[qscale];

        s.YDcScale = s.YDcScaleTable is null ? 8 : s.YDcScaleTable[s.Qscale];
        s.CDcScale = s.CDcScaleTable is null ? 8 : s.CDcScaleTable[s.ChromaQscale];
    }

    /// <summary>
    /// <c>ff_update_block_index</c> (<c>mpegvideo.h:335</c>) - steps the block indices and the
    /// three destinations on by one macroblock.
    /// </summary>
    /// <remarks>
    /// MPEG-1/2 advances only the destinations, inline in its slice loop, because nothing in
    /// it reads <c>block_index</c>. H.263 predicts through those indices, so it needs the
    /// whole thing.
    /// </remarks>
    public static void UpdateBlockIndex(ref MpegEncContext s, int bitsPerRawSample,
                                        int chromaXShift)
    {
        int bytesPerPixel = 1 + (bitsPerRawSample > 8 ? 1 : 0);
        int blockSize = 8 * bytesPerPixel;

        s.BlockIndex[0] += 2;
        s.BlockIndex[1] += 2;
        s.BlockIndex[2] += 2;
        s.BlockIndex[3] += 2;
        s.BlockIndex[4]++;
        s.BlockIndex[5]++;
        s.Dest[0] += 2 * blockSize;
        s.Dest[1] += (2 >> chromaXShift) * blockSize;
        s.Dest[2] += (2 >> chromaXShift) * blockSize;
    }

    /// <summary>
    /// <c>ff_clean_intra_table_entries</c> (<c>mpegvideo.c:447</c>) - resets the DC and AC
    /// prediction entries of the current macroblock, which H.263 and MPEG-4 call when a
    /// macroblock that had been intra is not.
    /// </summary>
    /// <remarks>
    /// The C's comment on the one block it leaves alone is load-bearing: the upper-left luma
    /// block is only ever read by blocks of the same macroblock, so nothing downstream can see
    /// its stale value. The two <c>AV_WN32</c>s each write two DC entries at once.
    /// </remarks>
    public static void CleanIntraTableEntries(ref MpegEncContext s)
    {
        int wrap = s.B8Stride;
        int xy = s.BlockIndex[0];
        int uxy = s.BlockIndex[4];
        int vxy = s.BlockIndex[5];

        short[] dcVal = s.DcValBase!;
        int dc = s.DcValOffset;

        dcVal[dc + xy] = 1024;
        dcVal[dc + xy + 1] = 1024;
        dcVal[dc + xy + wrap] = 1024;
        dcVal[dc + xy + wrap + 1] = 1024;
        dcVal[dc + uxy] = 1024;
        dcVal[dc + vxy] = 1024;

        short[] acVal = s.AcValBase!;
        int ac = s.AcValOffset;

        // ac_val is int16_t[16] per entry, so one entry is 16 shorts and the wrap row clears two.
        acVal.AsSpan(ac + (xy + 1) * 16, 16).Clear();
        acVal.AsSpan(ac + (xy + wrap) * 16, 32).Clear();
        acVal.AsSpan(ac + uxy * 16, 16).Clear();
        acVal.AsSpan(ac + vxy * 16, 16).Clear();
    }

    /// <summary>
    /// <c>alloc_picture_tables</c> plus the frame allocation - gives a picture slot its sample
    /// planes and its per-macroblock tables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DIVERGENCE, recorded: FFmpeg allocates through AVFrame and refcounted table pools. Here
    /// the planes are one rented buffer with a plane offset each, as this project's other
    /// decoders hold a picture, and the tables come from <c>FrameBufferPool&lt;T&gt;</c>.
    /// </para>
    /// <para>
    /// Each plane is padded by <see cref="MpegPicture.EdgeWidth"/> on every side. MPEG-1/2
    /// forbids a motion vector that leaves the frame, so unlike H.263 the padding is not what
    /// makes such a vector safe - it is what keeps the half-pel taps of a legal vector on the
    /// last row or column inside the allocation. The offsets point at the first real sample.
    /// </para>
    /// <para>
    /// A slot reused from the pool keeps its buffers: the sizes are a function of the sequence
    /// header, so a steady-state decode allocates nothing.
    /// </para>
    /// </remarks>
    private static void AllocPictureBuffers(ref MpegEncContext s, ref MPVPicture pic)
    {
        int edge = MpegPicture.EdgeWidth;
        int lumaWidth = s.MbWidth * 16;
        int lumaHeight = s.MbHeight * 16;

        int lumaStride = lumaWidth + 2 * edge;
        int chromaWidth = lumaWidth >> s.ChromaXShift;
        int chromaHeight = lumaHeight >> s.ChromaYShift;
        int chromaStride = chromaWidth + 2 * edge;

        int lumaPlane = lumaStride * (lumaHeight + 2 * edge);
        int chromaPlane = chromaStride * (chromaHeight + 2 * edge);
        int total = lumaPlane + 2 * chromaPlane;

        if (pic.Frame is null || pic.Frame.Length < total)
            pic.Frame = FrameBufferPool<byte>.Rent(total);

        pic.Linesize[0] = lumaStride;
        pic.Linesize[1] = chromaStride;
        pic.Linesize[2] = chromaStride;

        // Offsets of the first real sample, past the edge padding.
        pic.Data[0] = edge * lumaStride + edge;
        pic.Data[1] = lumaPlane + edge * chromaStride + edge;
        pic.Data[2] = lumaPlane + chromaPlane + edge * chromaStride + edge;

        // ff_mpv_init_context_frame's mb_height local, and its comment: "VC-1 can change from
        // being progressive to interlaced on a per-frame basis. We therefore allocate certain
        // buffers so big that they work in both instances." A field picture adds
        // b8_stride * (mb_height & ~1) to every block index, so an odd macroblock height
        // leaves the second field reading past the end of a table sized for the frame.
        int allocMbHeight = s.Msmpeg4Version == MsMpeg4Version.Vc1
            ? (s.MbHeight + 1) & ~1
            : s.MbHeight;

        int mbCount = s.MbStride * (s.MbHeight + 1);

        if (pic.MbTypeBase is null || pic.MbTypeBase.Length < mbCount)
            pic.MbTypeBase = FrameBufferPool<uint>.Rent(mbCount);
        Array.Clear(pic.MbTypeBase, 0, mbCount);
        pic.MbTypeOffset = 0;

        if (pic.QscaleTableBase is null || pic.QscaleTableBase.Length < mbCount)
            pic.QscaleTableBase = FrameBufferPool<sbyte>.Rent(mbCount);
        pic.QscaleTableOffset = 0;

        if (s.CodecId == MpegCodecId.Mpeg4)
        {
            // MPEG-4 is the only codec here whose pictures carry a skip table: a B picture
            // reads the one the following P picture wrote. The C's pool has
            // ZERO_EVERY_TIME on it, so this is cleared on every allocation and not only on
            // the first.
            if (pic.MbskipTable is null || pic.MbskipTable.Length < mbCount + 2)
                pic.MbskipTable = FrameBufferPool<byte>.Rent(mbCount + 2);
            Array.Clear(pic.MbskipTable, 0, mbCount + 2);
        }

        if (s.OutFormat == OutputFormat.H263)
        {
            // FMT_H263 predicts motion vectors from the picture's own field, so each picture
            // carries one. The C's pool has ZERO_EVERY_TIME on it and its comment says why:
            // H.263 with OBMC reads entries the current picture has not written, so the output
            // depends on the buffer's earlier content unless it is cleared.
            int b8ArraySize = s.B8Stride * allocMbHeight * 2;
            int mvSize = 2 * (b8ArraySize + 4);
            int refIndexSize = 4 * mbCount;

            if (pic.MotionValBaseL0 is null || pic.MotionValBaseL0.Length < mvSize)
                pic.MotionValBaseL0 = FrameBufferPool<short>.Rent(mvSize);
            if (pic.MotionValBaseL1 is null || pic.MotionValBaseL1.Length < mvSize)
                pic.MotionValBaseL1 = FrameBufferPool<short>.Rent(mvSize);
            Array.Clear(pic.MotionValBaseL0, 0, mvSize);
            Array.Clear(pic.MotionValBaseL1, 0, mvSize);

            // motion_val = motion_val_base + 4, and the base is int16_t[2], so four entries
            // are eight shorts.
            pic.MotionValOffsetL0 = 4 * 2;
            pic.MotionValOffsetL1 = 4 * 2;

            if (pic.RefIndexL0 is null || pic.RefIndexL0.Length < refIndexSize)
                pic.RefIndexL0 = FrameBufferPool<sbyte>.Rent(refIndexSize);
            if (pic.RefIndexL1 is null || pic.RefIndexL1.Length < refIndexSize)
                pic.RefIndexL1 = FrameBufferPool<sbyte>.Rent(refIndexSize);
            Array.Clear(pic.RefIndexL0, 0, refIndexSize);
            Array.Clear(pic.RefIndexL1, 0, refIndexSize);
        }

        pic.MbWidth = s.MbWidth;
        pic.MbHeight = s.MbHeight;
        pic.MbStride = s.MbStride;
    }

    /// <summary>
    /// <c>alloc_picture</c> - takes a picture slot, gives it buffers, and points
    /// <paramref name="dst"/> at it.
    /// </summary>
    /// <param name="s">The context, which owns the pool.</param>
    /// <param name="dst">The work picture to point at the new slot.</param>
    /// <param name="reference">
    /// Non-zero while a later picture may predict from this one. A B picture, and a picture the
    /// stream says is droppable, is not a reference.
    /// </param>
    private static void AllocPicture(ref MpegEncContext s, ref MPVWorkPicture dst, int reference)
    {
        MpvPicturePool pool = s.BufferPools.Pool ??= new MpvPicturePool();

        int slot = pool.AllocSlot();
        ref MPVPicture pic = ref pool.Picture(slot);

        pic.Reference = reference;
        AllocPictureBuffers(ref s, ref pic);

        // ff_mpv_pic_check_linesize's job: the context keeps the FRAME line sizes, which are not
        // the work picture's when the picture is a field. Motion compensation reads both - the
        // field-doubled stride to walk the prediction, the frame stride to step from one field
        // to the other - so the two must not be conflated.
        s.Linesize = pic.Linesize[0];
        s.Uvlinesize = pic.Linesize[1];

        // ff_mpv_framesize_alloc: the scratch a prediction that leaves the picture is built
        // in. The C's sizes are kept rather than computed from what this codec needs, because
        // what gets written into it is the emulation's 17x17 and two 9x9 blocks at the
        // picture's strides, and a buffer one row short would be overrun invisibly.
        int allocSize = ((int)s.Linesize + 64 + 31) / 32 * 32;
        const int EmuEdgeHeight = 4 * 70;
        int scratchNeeded = allocSize * EmuEdgeHeight + allocSize * 4 * 16 * 2;
        if (s.Sc.ScratchpadBuf is null || s.Sc.Linesize != (int)s.Linesize)
        {
            if (s.Sc.ScratchpadBuf is null || s.Sc.ScratchpadBuf.Length < scratchNeeded)
                s.Sc.ScratchpadBuf = new byte[scratchNeeded];
            s.Sc.EdgeEmuBufferOffset = 0;
            // obmc_scratchpad = scratchpad_buf + 16, and scratchpad_buf follows the edge
            // buffer in the C's two allocations.
            s.Sc.ObmcScratchpadOffset = allocSize * EmuEdgeHeight + 16;
            s.Sc.Linesize = (int)s.Linesize;
        }

        // Not cleared per picture, deliberately: FFmpeg clears it only for a field picture, in
        // mpeg_field_start. ff_mpv_reconstruct_mb writes an entry for every macroblock it
        // reconstructs, so for an undamaged picture the distinction is invisible - but error
        // resilience reads the table for macroblocks that were never reconstructed, and there
        // it must see what FFmpeg sees, which is the previous picture's value.
        int mbCount = s.MbStride * (s.MbHeight + 1);
        if (s.MbskipTable is null || s.MbskipTable.Length < mbCount)
        {
            s.MbskipTable = FrameBufferPool<byte>.Rent(mbCount);
            Array.Clear(s.MbskipTable, 0, mbCount);
        }

        pool.AdoptSlot(ref dst, slot);
    }

    /// <summary>
    /// <c>color_frame</c> - paints a whole picture one shade, used for the frame a stream that
    /// starts on a predicted picture has nothing else to predict from.
    /// </summary>
    private static void ColorFrame(ref MPVPicture pic, int lumaWidth, int lumaHeight,
                                   int chromaXShift, int chromaYShift, byte luma)
    {
        Span<byte> frame = pic.Frame;

        for (int y = 0; y < lumaHeight; y++)
            frame.Slice(pic.Data[0] + y * pic.Linesize[0], lumaWidth).Fill(luma);

        int chromaWidth = (lumaWidth + (1 << chromaXShift) - 1) >> chromaXShift;
        int chromaHeight = (lumaHeight + (1 << chromaYShift) - 1) >> chromaYShift;

        for (int y = 0; y < chromaHeight; y++)
        {
            frame.Slice(pic.Data[1] + y * pic.Linesize[1], chromaWidth).Fill(0x80);
            frame.Slice(pic.Data[2] + y * pic.Linesize[2], chromaWidth).Fill(0x80);
        }
    }

    /// <summary>
    /// <c>alloc_dummy_frame</c> - a stand-in reference picture.
    /// </summary>
    private static void AllocDummyFrame(ref MpegEncContext s, ref MPVWorkPicture dst)
    {
        AllocPicture(ref s, ref dst, 1);
        s.BufferPools.Pool!.Picture(dst.Ptr).Dummy = 1;
    }

    /// <summary>
    /// <c>ff_mpv_alloc_dummy_frames</c> - invents the references a picture needs but the stream
    /// has not supplied.
    /// </summary>
    /// <remarks>
    /// This happens when decoding starts part way into a stream, so the first picture is not an
    /// intra one and has nothing to predict from. FFmpeg paints the dummy mid grey and carries
    /// on; the picture is wrong, but it is wrong in the same way FFmpeg's is, and it is marked
    /// <c>dummy</c> so it is never output.
    /// </remarks>
    public static void MpvAllocDummyFrames(ref MpegEncContext s)
    {
        if (s.LastPic.Ptr == 0 && s.PictType != AvPictureType.I)
        {
            AllocDummyFrame(ref s, ref s.LastPic);
            ColorFrame(ref s.BufferPools.Pool!.Picture(s.LastPic.Ptr),
                       s.MbWidth * 16, s.MbHeight * 16, s.ChromaXShift, s.ChromaYShift, 0x80);
        }

        if (s.NextPic.Ptr == 0 && s.PictType == AvPictureType.B)
            AllocDummyFrame(ref s, ref s.NextPic);
    }

    /// <summary>
    /// <c>ff_mpv_frame_start</c> - called once the picture header is parsed and before any
    /// slice, to take a picture to decode into and to rotate the references.
    /// </summary>
    /// <remarks>
    /// The rotation is the whole of MPEG's reordering and it is three lines: a non-B picture
    /// pushes what was <c>next</c> into <c>last</c> and becomes the new <c>next</c>. So
    /// <c>next</c> is the most recent reference in CODED order, which for a B picture is the one
    /// ahead of it in DISPLAY order - hence backward prediction reading <c>next</c>. A B picture
    /// changes neither, because nothing predicts from it.
    /// </remarks>
    public static void MpvFrameStart(ref MpegEncContext s)
    {
        s.MbSkipped = 0;

        MpvPicturePool pool = s.BufferPools.Pool ??= new MpvPicturePool();
        pool.UnrefPicture(ref s.CurPic);

        AllocPicture(ref s, ref s.CurPic, s.PictType != AvPictureType.B && s.Droppable == 0 ? 1 : 0);

        ref MPVPicture cur = ref pool.Picture(s.CurPic.Ptr);
        cur.TopFieldFirst = s.TopFieldFirst != 0 ? 1 : 0;
        cur.Interlaced = s.ProgressiveFrame == 0 && s.ProgressiveSequence == 0 ? 1 : 0;
        cur.FieldPicture = s.PictureStructure != MpegVideoConstants.PictFrame ? 1 : 0;
        cur.PictType = s.PictType;
        cur.KeyFrame = s.PictType == AvPictureType.I ? 1 : 0;
        cur.Pts = s.CurrentPts;

        if (s.PictType != AvPictureType.B)
        {
            pool.WorkpicFromPic(ref s.LastPic, s.NextPic.Ptr);
            if (s.Droppable == 0)
                pool.WorkpicFromPic(ref s.NextPic, s.CurPic.Ptr);
        }

        MpvAllocDummyFrames(ref s);
    }

    /// <summary>
    /// <c>ff_mpv_frame_end</c> - everything it does is thread progress reporting and an x87
    /// state reset, neither of which has a counterpart here.
    /// </summary>
    /// <remarks>
    /// Kept as a named no-op rather than dropped, so that the call site reads as FFmpeg's does
    /// and so the checklist can say what happened to it.
    /// </remarks>
    public static void MpvFrameEnd(ref MpegEncContext s)
    {
    }

    /// <summary>
    /// <c>ff_mpeg_flush</c> - drops the three reference pictures, so that decoding restarts
    /// from whatever picture arrives next rather than predicting from one on the far side of a
    /// seek.
    /// </summary>
    /// <remarks>
    /// The pool is null until the first picture has been decoded, and flushing before then is
    /// legal: a seek before the first frame is still a seek.
    /// </remarks>
    public static void MpegFlush(ref MpegEncContext s)
    {
        if (s.BufferPools.Pool is { } pool)
        {
            pool.UnrefPicture(ref s.CurPic);
            pool.UnrefPicture(ref s.LastPic);
            pool.UnrefPicture(ref s.NextPic);
        }

        s.MbX = 0;
        s.MbY = 0;
        s.PpTime = 0;
    }

    /// <summary>
    /// <c>add_dct</c> - adds one transformed block onto the prediction, if the bitstream said
    /// the block has any coefficients at all.
    /// </summary>
    private static void AddDct(
        ref MpegEncContext s, Span<short> blocks, int i, Span<byte> dest, int destOffset, int lineSize)
    {
        if (s.BlockLastIndex[i] >= 0)
            IdctDsp.IdctAdd(s.IdctAlgo, dest, destOffset, lineSize, blocks.Slice(i * 64, 64));
    }

    /// <summary>
    /// <c>mpv_reconstruct_mb_internal</c> with <c>is_mpeg12</c> set to
    /// <c>DEFINITELY_MPEG12_H261</c> - turns a parsed macroblock into samples.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two arms differ in one word. An intra macroblock has no prediction, so the transform
    /// result IS the picture and <c>idct_put</c> replaces whatever was there. An inter
    /// macroblock has motion compensation write the prediction first, and the transform result
    /// is a correction that <c>idct_add</c> adds onto it - and only for the blocks the bitstream
    /// says are non-empty, which is what <c>block_last_index</c> records.
    /// </para>
    /// <para>
    /// <c>interlaced_dct</c> splits a macroblock into two fields: the rows of one field are
    /// contiguous in the block but a line apart in the picture, so the stride doubles and the
    /// lower two blocks start one line down rather than eight.
    /// </para>
    /// <para>
    /// No dequantisation here, in either arm. The MPEG-1/2 block readers already did it, which
    /// is why FFmpeg takes the IS_MPEG12_H261 branch that skips the unquantisers entirely.
    /// </para>
    /// </remarks>
    /// <summary><c>put_dct</c> - dequantise an intra block and write it.</summary>
    private static void PutDct(ref MpegEncContext s, Span<short> blocks, int i,
                               Span<byte> frame, int dest, int lineSize, int qscale)
    {
        Span<short> block = blocks.Slice(i * 64, 64);
        Unquantize(ref s, block, i, qscale, intra: true);
        IdctDsp.IdctPut(s.IdctAlgo, frame, dest, lineSize, block);
    }

    /// <summary><c>add_dequant_dct</c> - dequantise an inter block and add it.</summary>
    private static void AddDequantDct(ref MpegEncContext s, Span<short> blocks, int i,
                                      Span<byte> frame, int dest, int lineSize, int qscale)
    {
        if (s.BlockLastIndex[i] < 0)
            return;
        Span<short> block = blocks.Slice(i * 64, 64);
        Unquantize(ref s, block, i, qscale, intra: false);
        IdctDsp.IdctAdd(s.IdctAlgo, frame, dest, lineSize, block);
    }

    /// <summary>The C's <c>dct_unquantize_intra</c> / <c>_inter</c> function pointers.</summary>
    private static void Unquantize(ref MpegEncContext s, Span<short> block, int i, int qscale,
                                   bool intra)
    {
        switch (s.DctUnquantize)
        {
            case UnquantizeKind.H263:
                if (intra)
                    MpegVideoUnquantize.H263Intra(ref s, block, i, qscale);
                else
                    MpegVideoUnquantize.H263Inter(ref s, block, i, qscale);
                break;
            case UnquantizeKind.H263Intra:
                // MPEG-4 with the H.263 quantiser: only the intra arm is this one, and the
                // inter arm is None, which means the block reader already scaled.
                if (intra)
                    MpegVideoUnquantize.H263Intra(ref s, block, i, qscale);
                break;
            case UnquantizeKind.Mpeg2Intra:
            case UnquantizeKind.Mpeg2Inter:
                throw new NotSupportedException(
                    "The MPEG-2 unquantisers are not ported into this path yet; MPEG-4 needs "
                    + "them only when a VOL header sets mpeg_quant.");
            default:
                throw new InvalidOperationException(
                    $"No unquantiser is installed for {s.DctUnquantize}.");
        }
    }

    private static void MpvReconstructMbInternal(ref MpegEncContext s, Span<short> blocks)
    {
        int linesize = (int)s.CurPic.Linesize[0];
        int uvlinesize = (int)s.CurPic.Linesize[1];
        const int blockSize = 8;

        int dctLinesize = linesize << s.InterlacedDct;
        int dctOffset = s.InterlacedDct != 0 ? linesize : linesize * blockSize;

        Span<byte> frame = s.CurPic.Frame;
        int destY = s.Dest[0];
        int destCb = s.Dest[1];
        int destCr = s.Dest[2];

        if (s.MbIntra == 0)
        {
            // Motion handling. A B macroblock predicted both ways puts the forward prediction
            // down and then averages the backward one onto it, which is why the second call
            // passes average: true.
            bool average = false;

            // The rounded put/avg families, except where the stream says otherwise: H.263 and
            // MPEG-4 set no_rounding on some pictures, and a B picture always rounds.
            bool round = s.DctUnquantize == UnquantizeKind.None || s.NoRounding == 0
                       || s.PictType == AvPictureType.B;

            if ((s.MvDir & MpegVideoConstants.MvDirForward) != 0)
            {
                MpegVideoMotion.MpvMotion(ref s, destY, destCb, destCr, 0, s.LastPic,
                                          average: false, round);
                average = true;
            }

            if ((s.MvDir & MpegVideoConstants.MvDirBackward) != 0)
                MpegVideoMotion.MpvMotion(ref s, destY, destCb, destCr, 1, s.NextPic, average, round);

            if (s.DctUnquantize != UnquantizeKind.None)
            {
                // FFmpeg's "is_mpeg12 != DEFINITELY_MPEG12_H261 && s->dct_unquantize_inter"
                // arm: H.263 and its relatives dequantise here rather than at parse time, and
                // chroma uses chroma_qscale, which modified quantisation can make differ.
                AddDequantDct(ref s, blocks, 0, frame, destY, dctLinesize, s.Qscale);
                AddDequantDct(ref s, blocks, 1, frame, destY + blockSize, dctLinesize, s.Qscale);
                AddDequantDct(ref s, blocks, 2, frame, destY + dctOffset, dctLinesize, s.Qscale);
                AddDequantDct(ref s, blocks, 3, frame, destY + dctOffset + blockSize,
                              dctLinesize, s.Qscale);

                AddDequantDct(ref s, blocks, 4, frame, destCb, uvlinesize, s.ChromaQscale);
                AddDequantDct(ref s, blocks, 5, frame, destCr, uvlinesize, s.ChromaQscale);
                return;
            }

            AddDct(ref s, blocks, 0, frame, destY, dctLinesize);
            AddDct(ref s, blocks, 1, frame, destY + blockSize, dctLinesize);
            AddDct(ref s, blocks, 2, frame, destY + dctOffset, dctLinesize);
            AddDct(ref s, blocks, 3, frame, destY + dctOffset + blockSize, dctLinesize);

            if (s.ChromaYShift != 0)
            {
                // 4:2:0: one chroma block per component.
                AddDct(ref s, blocks, 4, frame, destCb, uvlinesize);
                AddDct(ref s, blocks, 5, frame, destCr, uvlinesize);
            }
            else
            {
                int chromaDctLinesize = uvlinesize << s.InterlacedDct;
                int chromaDctOffset = s.InterlacedDct != 0 ? uvlinesize : uvlinesize * blockSize;

                AddDct(ref s, blocks, 4, frame, destCb, chromaDctLinesize);
                AddDct(ref s, blocks, 5, frame, destCr, chromaDctLinesize);
                AddDct(ref s, blocks, 6, frame, destCb + chromaDctOffset, chromaDctLinesize);
                AddDct(ref s, blocks, 7, frame, destCr + chromaDctOffset, chromaDctLinesize);

                if (s.ChromaXShift == 0)
                {
                    AddDct(ref s, blocks, 8, frame, destCb + blockSize, chromaDctLinesize);
                    AddDct(ref s, blocks, 9, frame, destCr + blockSize, chromaDctLinesize);
                    AddDct(ref s, blocks, 10, frame, destCb + blockSize + chromaDctOffset, chromaDctLinesize);
                    AddDct(ref s, blocks, 11, frame, destCr + blockSize + chromaDctOffset, chromaDctLinesize);
                }
            }

            return;
        }

        if (s.DctUnquantize != UnquantizeKind.None)
        {
            // The C's put_dct: dequantise, then idct_put unconditionally - an intra block is
            // written whether or not it has coefficients, because there is no prediction under
            // it.
            PutDct(ref s, blocks, 0, frame, destY, dctLinesize, s.Qscale);
            PutDct(ref s, blocks, 1, frame, destY + blockSize, dctLinesize, s.Qscale);
            PutDct(ref s, blocks, 2, frame, destY + dctOffset, dctLinesize, s.Qscale);
            PutDct(ref s, blocks, 3, frame, destY + dctOffset + blockSize, dctLinesize, s.Qscale);

            PutDct(ref s, blocks, 4, frame, destCb, uvlinesize, s.ChromaQscale);
            PutDct(ref s, blocks, 5, frame, destCr, uvlinesize, s.ChromaQscale);
            return;
        }

        IdctDsp.IdctPut(s.IdctAlgo, frame, destY, dctLinesize, blocks.Slice(0 * 64, 64));
        IdctDsp.IdctPut(s.IdctAlgo, frame, destY + blockSize, dctLinesize, blocks.Slice(1 * 64, 64));
        IdctDsp.IdctPut(s.IdctAlgo, frame, destY + dctOffset, dctLinesize, blocks.Slice(2 * 64, 64));
        IdctDsp.IdctPut(s.IdctAlgo, frame, destY + dctOffset + blockSize, dctLinesize, blocks.Slice(3 * 64, 64));

        if (s.ChromaYShift != 0)
        {
            // 4:2:0: one chroma block per component.
            IdctDsp.IdctPut(s.IdctAlgo, frame, destCb, uvlinesize, blocks.Slice(4 * 64, 64));
            IdctDsp.IdctPut(s.IdctAlgo, frame, destCr, uvlinesize, blocks.Slice(5 * 64, 64));
        }
        else
        {
            // 4:2:2 and 4:4:4: chroma is full height, so it splits by field like luma.
            int chromaDctLinesize = uvlinesize << s.InterlacedDct;
            int chromaDctOffset = s.InterlacedDct != 0 ? uvlinesize : uvlinesize * blockSize;

            IdctDsp.IdctPut(s.IdctAlgo, frame, destCb, chromaDctLinesize, blocks.Slice(4 * 64, 64));
            IdctDsp.IdctPut(s.IdctAlgo, frame, destCr, chromaDctLinesize, blocks.Slice(5 * 64, 64));
            IdctDsp.IdctPut(s.IdctAlgo, frame, destCb + chromaDctOffset, chromaDctLinesize, blocks.Slice(6 * 64, 64));
            IdctDsp.IdctPut(s.IdctAlgo, frame, destCr + chromaDctOffset, chromaDctLinesize, blocks.Slice(7 * 64, 64));

            if (s.ChromaXShift == 0)
            {
                // 4:4:4 carries four more.
                IdctDsp.IdctPut(s.IdctAlgo, frame, destCb + blockSize, chromaDctLinesize, blocks.Slice(8 * 64, 64));
                IdctDsp.IdctPut(s.IdctAlgo, frame, destCr + blockSize, chromaDctLinesize, blocks.Slice(9 * 64, 64));
                IdctDsp.IdctPut(s.IdctAlgo, frame, destCb + blockSize + chromaDctOffset, chromaDctLinesize, blocks.Slice(10 * 64, 64));
                IdctDsp.IdctPut(s.IdctAlgo, frame, destCr + blockSize + chromaDctOffset, chromaDctLinesize, blocks.Slice(11 * 64, 64));
            }
        }
    }

    /// <summary>
    /// <c>ff_mpv_reconstruct_mb</c> - records what the macroblock was, then reconstructs it.
    /// </summary>
    /// <remarks>
    /// The quantiser scale and the skip flag are written for every macroblock whether or not
    /// anything reads them yet: they are what error resilience and the motion vector display
    /// read back, and a picture whose tables are stale is worse than one with none.
    /// </remarks>
    public static void MpvReconstructMb(ref MpegEncContext s, Span<short> blocks)
    {
        int mbXy = s.MbY * s.MbStride + s.MbX;

        if (s.CurPic.QscaleTable is not null)
            s.CurPic.QscaleTable[s.CurPic.QscaleTableOffset + mbXy] = (sbyte)s.Qscale;

        if (s.MbskipTable is not null)
        {
            // A macroblock skipped in this picture and in the last needs no copy at all, which
            // is what the flag is for.
            if (s.MbSkipped != 0)
            {
                s.MbSkipped = 0;
                s.MbskipTable[mbXy] = 1;
            }
            else
            {
                s.MbskipTable[mbXy] = (byte)(s.CurPic.Reference == 0 ? 1 : 0);
            }
        }
        else if (s.MbSkipped != 0)
        {
            s.MbSkipped = 0;
        }

        MpvReconstructMbInternal(ref s, blocks);
    }
}

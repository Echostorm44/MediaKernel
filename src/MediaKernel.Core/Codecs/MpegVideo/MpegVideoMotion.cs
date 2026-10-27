// Motion compensation for the shared MPEG-video layer - a translation of the MPEG-1/2 arms of
// ffmpeg-master/libavcodec/mpegvideo_motion.c.
//
// FFmpeg compiles that file twice from the same source: mpeg_motion_internal and
// mpv_motion_internal are av_always_inline and take an is_mpeg12 flag, so the compiler produces
// one specialisation with every H.263, H.261, MPEG-4 and WMV2 branch folded away. What is here is
// that specialisation, written out. Three consequences, all recorded in the port checklist:
//
//  1. The half-pel function-pointer tables (op_pixels_func (*pix_op)[4]) become a block size and
//     two booleans. MPEG-1/2 only ever selects the rounded put and avg families - the choice in
//     mpv_reconstruct_mb_internal is "put, unless this is the second of two predictions", with
//     the no_rnd family reachable only from codecs that are not MPEG-1/2.
//  2. The qpel tables are absent entirely. MPEG-1/2 has no quarter-sample mode, so qpel_motion,
//     apply_8x8, obmc_motion and put_obmc are unreachable from here and are left todo for the
//     codec that first needs them.
//  3. uint8_t *const *ref_picture becomes the buffer plus three plane offsets, because a plane
//     here is an array plus an offset and the offsets alone do not identify the buffer. Taking
//     the three offsets rather than an MPVWorkPicture is not cosmetic: a field picture’s work
//     picture is a FIELD view, whose luma offset is one line into the frame for a bottom field,
//     and the places where FFmpeg predicts from the picture being decoded use
//     cur_pic.ptr->f->data - the FRAME base - with the field-doubled stride. Passing the work
//     picture there reads the wrong field, which is a whole-field error that only shows up on a
//     stream whose two fields are coded as different picture types.
//
// What is NOT folded away is the out-of-bounds path, and it is worth stating plainly because it
// looks like an omission: when a motion vector reaches outside the frame, MPEG-1/2 does not
// build a replicated block the way H.263 does - FFmpeg logs and returns, leaving the prediction
// as it found it. Motion vectors pointing outside the picture are illegal in MPEG-1/2, so this
// only happens on a damaged stream, and matching FFmpeg means leaving the block alone.

using System;
using MediaKernel.Core.Codecs.Mpeg2;
using MediaKernel.Core.Codecs.Mpeg4Video;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>Motion compensation (<c>mpegvideo_motion.c</c>), MPEG-1/2 arms.</summary>
public static class MpegVideoMotion
{
    /// <summary>
    /// <c>mpeg_motion_internal</c> with <c>is_mpeg12</c> set - applies one motion vector to all
    /// three components.
    /// </summary>
    /// <param name="s">The macroblock context, which supplies the picture geometry.</param>
    /// <param name="destY">Luma destination, an offset into the current picture.</param>
    /// <param name="destCb">Cb destination.</param>
    /// <param name="destCr">Cr destination.</param>
    /// <param name="fieldBased">1 when the two fields of a frame picture are predicted apart.</param>
    /// <param name="bottomField">1 when this call writes the bottom field of such a pair.</param>
    /// <param name="fieldSelect">1 when the prediction reads the reference picture one line down.</param>
    /// <param name="refFrame">The allocation the reference picture lives in.</param>
    /// <param name="refY">Offset of the reference picture's first luma sample.</param>
    /// <param name="refCb">Offset of its first Cb sample.</param>
    /// <param name="refCr">Offset of its first Cr sample.</param>
    /// <param name="average">False for the first prediction, true to average onto it.</param>
    /// <param name="motionX">Horizontal vector, in half samples.</param>
    /// <param name="motionY">Vertical vector, in half samples.</param>
    /// <param name="h">Height of the prediction, 16 for a whole macroblock.</param>
    /// <param name="is16X8">1 for the 16x8 mode, whose halves are a macroblock row apart.</param>
    /// <param name="mbY">The macroblock row the prediction belongs to.</param>
    private static void MpegMotionInternal(
        ref MpegEncContext s, int destY, int destCb, int destCr,
        int fieldBased, int bottomField, int fieldSelect,
        byte[] refFrame, int refY, int refCb, int refCr, bool average, bool round,
        int motionX, int motionY, int h, int is16X8, int mbY)
    {
        int vEdgePos = s.VEdgePos >> fieldBased;
        int linesize = (int)s.CurPic.Linesize[0] << fieldBased;
        int uvlinesize = (int)s.CurPic.Linesize[1] << fieldBased;
        int blockYHalf = fieldBased | is16X8;

        int dxy = ((motionY & 1) << 1) | (motionX & 1);
        int srcX = s.MbX * 16 + (motionX >> 1);
        int srcY = (mbY << (4 - blockYHalf)) + (motionY >> 1);

        int uvdxy, uvsrcX, uvsrcY, mx;

        // H.263 derives the chroma vector from the luma one's bits rather than by dividing,
        // which is a different vector: FFmpeg's FF_BUG_HPEL_CHROMA exists because encoders
        // disagreed about it. Only the non-field arm is reachable here - H.263 has no field
        // pictures - so the workaround branch is not translated.
        if (s.OutFormat == OutputFormat.H263)
        {
            uvdxy = dxy | (motionY & 2) | ((motionX & 2) >> 1);
            uvsrcX = srcX >> 1;
            uvsrcY = srcY >> 1;
        }
        // The chroma vector is the luma vector scaled by the subsampling, and the division is C's
        // truncation towards zero rather than an arithmetic shift: -3 / 2 is -1, not -2.
        else if (s.ChromaYShift != 0)
        {
            mx = motionX / 2;
            int my = motionY / 2;
            uvdxy = ((my & 1) << 1) | (mx & 1);
            uvsrcX = s.MbX * 8 + (mx >> 1);
            uvsrcY = (mbY << (3 - blockYHalf)) + (my >> 1);
        }
        else if (s.ChromaXShift != 0)
        {
            // 4:2:2 - chroma is full height, so the vertical vector is not halved.
            mx = motionX / 2;
            uvdxy = ((motionY & 1) << 1) | (mx & 1);
            uvsrcX = s.MbX * 8 + (mx >> 1);
            uvsrcY = srcY;
        }
        else
        {
            // 4:4:4 - chroma is the same grid as luma.
            uvdxy = dxy;
            uvsrcX = srcX;
            uvsrcY = srcY;
        }

        int ptrY = refY + srcY * linesize + srcX;
        int ptrCb = refCb + uvsrcY * uvlinesize + uvsrcX;
        int ptrCr = refCr + uvsrcY * uvlinesize + uvsrcX;

        // The unsigned compare catches a negative coordinate and an overrun in one test.
        if ((uint)srcX >= (uint)Math.Max(s.HEdgePos - (motionX & 1) - 15, 0) ||
            (uint)srcY >= (uint)Math.Max(vEdgePos - (motionY & 1) - h + 1, 0))
        {
            if (s.OutFormat != OutputFormat.H263)
            {
                // Out of bounds. MPEG-1/2 forbids this, so the stream is damaged; FFmpeg
                // leaves the prediction untouched rather than emulating an edge, and so does
                // this.
                return;
            }

            // H.263 allows a vector that leaves the picture, so the missing samples are built
            // by replicating the edge. The three buffers are laid out exactly as the C lays
            // them out inside one allocation, because their sizes are what the emulation
            // writes and a smaller one would be silently overrun.
            byte[] emu = s.Sc.ScratchpadBuf!;
            int lumaBuf = s.Sc.EdgeEmuBufferOffset;
            int cbBuf = lumaBuf + 18 * (int)s.Linesize;
            int crBuf = cbBuf + 10 * (int)s.Uvlinesize;

            VideoDsp.EmulatedEdgeMc(emu, lumaBuf, refFrame, ptrY,
                                    (int)s.Linesize, (int)s.Linesize,
                                    17, 17, srcX, srcY, s.HEdgePos, s.VEdgePos);
            VideoDsp.EmulatedEdgeMc(emu, cbBuf, refFrame, ptrCb,
                                    (int)s.Uvlinesize, (int)s.Uvlinesize,
                                    9, 9, uvsrcX, uvsrcY, s.HEdgePos >> 1, s.VEdgePos >> 1);
            VideoDsp.EmulatedEdgeMc(emu, crBuf, refFrame, ptrCr,
                                    (int)s.Uvlinesize, (int)s.Uvlinesize,
                                    9, 9, uvsrcX, uvsrcY, s.HEdgePos >> 1, s.VEdgePos >> 1);

            HpelDsp.PixelsTab(dxy, 16, average, round, s.CurPic.Frame!, destY,
                              emu, lumaBuf, (int)s.Linesize, h);
            int emuChromaWidth = 16 >> s.ChromaXShift;
            int emuChromaHeight = h >> s.ChromaYShift;
            HpelDsp.PixelsTab(uvdxy, emuChromaWidth, average, round, s.CurPic.Frame!, destCb,
                              emu, cbBuf, (int)s.Uvlinesize, emuChromaHeight);
            HpelDsp.PixelsTab(uvdxy, emuChromaWidth, average, round, s.CurPic.Frame!, destCr,
                              emu, crBuf, (int)s.Uvlinesize, emuChromaHeight);
            return;
        }

        if (bottomField != 0)
        {
            destY += (int)s.Linesize;
            destCb += (int)s.Uvlinesize;
            destCr += (int)s.Uvlinesize;
        }

        if (fieldSelect != 0)
        {
            ptrY += (int)s.Linesize;
            ptrCb += (int)s.Uvlinesize;
            ptrCr += (int)s.Uvlinesize;
        }

        byte[] dst = s.CurPic.Frame!;
        byte[] src = refFrame;

        HpelDsp.PixelsTab(dxy, 16, average, round, dst, destY, src, ptrY, linesize, h);

        int chromaWidth = 16 >> s.ChromaXShift;
        int chromaHeight = h >> s.ChromaYShift;
        HpelDsp.PixelsTab(uvdxy, chromaWidth, average, round,
                          dst, destCb, src, ptrCb, uvlinesize, chromaHeight);
        HpelDsp.PixelsTab(uvdxy, chromaWidth, average, round,
                          dst, destCr, src, ptrCr, uvlinesize, chromaHeight);
    }

    /// <summary><c>mpeg_motion</c> - one vector over a whole macroblock or a 16x8 half.</summary>
    private static void MpegMotion(
        ref MpegEncContext s, int destY, int destCb, int destCr,
        int fieldSelect, byte[] refFrame, int refY, int refCb, int refCr, bool average,
        bool round, int motionX, int motionY, int h, int is16X8, int mbY)
        => MpegMotionInternal(ref s, destY, destCb, destCr, 0, 0, fieldSelect,
                              refFrame, refY, refCb, refCr, average, round,
                              motionX, motionY, h, is16X8, mbY);

    /// <summary>
    /// <c>mpeg_motion_field</c> - one vector over one field of a frame picture, which is eight
    /// lines of a sixteen-line macroblock at twice the stride.
    /// </summary>
    private static void MpegMotionField(
        ref MpegEncContext s, int destY, int destCb, int destCr,
        int bottomField, int fieldSelect, byte[] refFrame, int refY, int refCb, int refCr,
        bool average, bool round, int motionX, int motionY, int mbY)
        => MpegMotionInternal(ref s, destY, destCb, destCr, 1, bottomField, fieldSelect,
                              refFrame, refY, refCb, refCr, average, round,
                              motionX, motionY, 8, 0, mbY);

    /// <summary>
    /// <c>hpel_motion</c> - one 8x8 prediction at half-pel precision, used by the four-vector
    /// mode.
    /// </summary>
    private static void HpelMotion(
        ref MpegEncContext s, byte[] destFrame, int dest,
        byte[] srcFrame, int src, int srcX, int srcY,
        bool average, bool round, int motionX, int motionY)
    {
        int dxy = 0;

        srcX += motionX >> 1;
        srcY += motionY >> 1;

        // "WARNING: do no forget half pels" - at the right or bottom clamp there is no sample
        // to interpolate towards, so the half-pel bit for that axis is dropped.
        srcX = Math.Clamp(srcX, -16, s.Width);
        if (srcX != s.Width)
            dxy |= motionX & 1;
        srcY = Math.Clamp(srcY, -16, s.Height);
        if (srcY != s.Height)
            dxy |= (motionY & 1) << 1;

        int linesize = (int)s.Linesize;
        src += srcY * linesize + srcX;

        if ((uint)srcX >= (uint)Math.Max(s.HEdgePos - (motionX & 1) - 7, 0) ||
            (uint)srcY >= (uint)Math.Max(s.VEdgePos - (motionY & 1) - 7, 0))
        {
            byte[] emu = s.Sc.ScratchpadBuf!;
            int buf = s.Sc.EdgeEmuBufferOffset;
            VideoDsp.EmulatedEdgeMc(emu, buf, srcFrame, src, linesize, linesize,
                                    9, 9, srcX, srcY, s.HEdgePos, s.VEdgePos);
            HpelDsp.PixelsTab(dxy, 8, average, round, destFrame, dest,
                              emu, buf, linesize, 8);
            return;
        }

        HpelDsp.PixelsTab(dxy, 8, average, round, destFrame, dest,
                          srcFrame, src, linesize, 8);
    }

    /// <summary>
    /// <c>chroma_4mv_motion</c> - the single chroma vector the four luma vectors average to.
    /// </summary>
    /// <remarks>
    /// The rounding is H.263's table rather than an arithmetic one, which is what
    /// <c>ff_h263_round_chroma</c> is; the sum of the four vectors arrives unhalved and the
    /// table absorbs the division.
    /// </remarks>
    private static void Chroma4MvMotion(
        ref MpegEncContext s, int destCb, int destCr,
        byte[] refFrame, int refCb, int refCr, bool average, bool round, int mx, int my)
    {
        mx = Mpeg4Video.H263.H263RoundChroma(mx);
        my = Mpeg4Video.H263.H263RoundChroma(my);

        int dxy = ((my & 1) << 1) | (mx & 1);
        mx >>= 1;
        my >>= 1;

        int srcX = s.MbX * 8 + mx;
        int srcY = s.MbY * 8 + my;
        srcX = Math.Clamp(srcX, -8, s.Width >> 1);
        if (srcX == s.Width >> 1)
            dxy &= ~1;
        srcY = Math.Clamp(srcY, -8, s.Height >> 1);
        if (srcY == s.Height >> 1)
            dxy &= ~2;

        int uvlinesize = (int)s.Uvlinesize;
        int offset = srcY * uvlinesize + srcX;

        bool emu = (uint)srcX >= (uint)Math.Max((s.HEdgePos >> 1) - (dxy & 1) - 7, 0) ||
                   (uint)srcY >= (uint)Math.Max((s.VEdgePos >> 1) - (dxy >> 1) - 7, 0);

        byte[] dst = s.CurPic.Frame!;

        if (!emu)
        {
            HpelDsp.PixelsTab(dxy, 8, average, round, dst, destCb,
                              refFrame, refCb + offset, uvlinesize, 8);
            HpelDsp.PixelsTab(dxy, 8, average, round, dst, destCr,
                              refFrame, refCr + offset, uvlinesize, 8);
            return;
        }

        byte[] buf = s.Sc.ScratchpadBuf!;
        int bufOff = s.Sc.EdgeEmuBufferOffset;

        VideoDsp.EmulatedEdgeMc(buf, bufOff, refFrame, refCb + offset, uvlinesize, uvlinesize,
                                9, 9, srcX, srcY, s.HEdgePos >> 1, s.VEdgePos >> 1);
        HpelDsp.PixelsTab(dxy, 8, average, round, dst, destCb, buf, bufOff, uvlinesize, 8);

        VideoDsp.EmulatedEdgeMc(buf, bufOff, refFrame, refCr + offset, uvlinesize, uvlinesize,
                                9, 9, srcX, srcY, s.HEdgePos >> 1, s.VEdgePos >> 1);
        HpelDsp.PixelsTab(dxy, 8, average, round, dst, destCr, buf, bufOff, uvlinesize, 8);
    }

    /// <summary>
    /// <c>put_obmc</c> - blends five 8x8 predictions into one with the overlapped-block
    /// weights.
    /// </summary>
    /// <remarks>
    /// The weights are a 5-tap window per pixel, written out position by position in the C
    /// because they are not a formula: they taper from the neighbour's vector at the block
    /// edge to the block's own in the middle, and the corners are not the product of the two
    /// edges. Transcribed line for line, OBMC_FILTER4 expanded as the four pixels it covers.
    /// </remarks>
    private static void PutObmc(Span<byte> dst, int dstOff, byte[] src,
                                ReadOnlySpan<int> srcOff, int stride)
    {
        // OBMC_FILTER's five weights at one pixel. The five sources are mid, top, left, right
        // and bottom, in srcOff's order.
        static void F(Span<byte> dst, int at, byte[] src, ReadOnlySpan<int> o, int x,
                      int t, int l, int m, int r, int b)
            => dst[at + x] = (byte)((t * src[o[1] + x] + l * src[o[2] + x] + m * src[o[0] + x]
                                   + r * src[o[3] + x] + b * src[o[4] + x] + 4) >> 3);

        // OBMC_FILTER4: the same weights over a 2x2 group.
        static void F4(Span<byte> dst, int at, byte[] src, ReadOnlySpan<int> o, int x, int stride,
                       int t, int l, int m, int r, int b)
        {
            F(dst, at, src, o, x, t, l, m, r, b);
            F(dst, at, src, o, x + 1, t, l, m, r, b);
            F(dst, at, src, o, x + stride, t, l, m, r, b);
            F(dst, at, src, o, x + 1 + stride, t, l, m, r, b);
        }

        int p = 0;
        F(dst, dstOff, src, srcOff, p, 2, 2, 4, 0, 0);
        F(dst, dstOff, src, srcOff, p + 1, 2, 1, 5, 0, 0);
        F4(dst, dstOff, src, srcOff, p + 2, stride, 2, 1, 5, 0, 0);
        F4(dst, dstOff, src, srcOff, p + 4, stride, 2, 0, 5, 1, 0);
        F(dst, dstOff, src, srcOff, p + 6, 2, 0, 5, 1, 0);
        F(dst, dstOff, src, srcOff, p + 7, 2, 0, 4, 2, 0);
        p += stride;
        F(dst, dstOff, src, srcOff, p, 1, 2, 5, 0, 0);
        F(dst, dstOff, src, srcOff, p + 1, 1, 2, 5, 0, 0);
        F(dst, dstOff, src, srcOff, p + 6, 1, 0, 5, 2, 0);
        F(dst, dstOff, src, srcOff, p + 7, 1, 0, 5, 2, 0);
        p += stride;
        F4(dst, dstOff, src, srcOff, p, stride, 1, 2, 5, 0, 0);
        F4(dst, dstOff, src, srcOff, p + 2, stride, 1, 1, 6, 0, 0);
        F4(dst, dstOff, src, srcOff, p + 4, stride, 1, 0, 6, 1, 0);
        F4(dst, dstOff, src, srcOff, p + 6, stride, 1, 0, 5, 2, 0);
        p += 2 * stride;
        F4(dst, dstOff, src, srcOff, p, stride, 0, 2, 5, 0, 1);
        F4(dst, dstOff, src, srcOff, p + 2, stride, 0, 1, 6, 0, 1);
        F4(dst, dstOff, src, srcOff, p + 4, stride, 0, 0, 6, 1, 1);
        F4(dst, dstOff, src, srcOff, p + 6, stride, 0, 0, 5, 2, 1);
        p += 2 * stride;
        F(dst, dstOff, src, srcOff, p, 0, 2, 5, 0, 1);
        F(dst, dstOff, src, srcOff, p + 1, 0, 2, 5, 0, 1);
        F4(dst, dstOff, src, srcOff, p + 2, stride, 0, 1, 5, 0, 2);
        F4(dst, dstOff, src, srcOff, p + 4, stride, 0, 0, 5, 1, 2);
        F(dst, dstOff, src, srcOff, p + 6, 0, 0, 5, 2, 1);
        F(dst, dstOff, src, srcOff, p + 7, 0, 0, 5, 2, 1);
        p += stride;
        F(dst, dstOff, src, srcOff, p, 0, 2, 4, 0, 2);
        F(dst, dstOff, src, srcOff, p + 1, 0, 1, 5, 0, 2);
        F(dst, dstOff, src, srcOff, p + 6, 0, 0, 5, 1, 2);
        F(dst, dstOff, src, srcOff, p + 7, 0, 0, 4, 2, 2);
    }

    /// <summary><c>obmc_motion</c> - OBMC for one 8x8 luma block.</summary>
    /// <remarks>
    /// A neighbour whose vector equals this block's needs no second prediction, so the C
    /// points at the middle one instead of computing it again - which is a saving and also
    /// the reason the five pointers are an array rather than five locals.
    /// </remarks>
    private static void ObmcMotion(
        ref MpegEncContext s, int dest, byte[] srcFrame, int src, int srcX, int srcY,
        bool round, ReadOnlySpan<short> mv)
    {
        const int Mid = 0;
        Span<int> ptr = stackalloc int[5];
        byte[] pad = s.Sc.ScratchpadBuf!;
        int linesize = (int)s.Linesize;

        for (int i = 0; i < 5; i++)
        {
            if (i != 0 && mv[2 * i] == mv[2 * Mid] && mv[2 * i + 1] == mv[2 * Mid + 1])
            {
                ptr[i] = ptr[Mid];
            }
            else
            {
                ptr[i] = s.Sc.ObmcScratchpadOffset + 8 * (i & 1) + linesize * 8 * (i >> 1);
                HpelMotion(ref s, pad, ptr[i], srcFrame, src, srcX, srcY,
                           average: false, round, mv[2 * i], mv[2 * i + 1]);
            }
        }

        PutObmc(s.CurPic.Frame!, dest, pad, ptr, linesize);
    }

    /// <summary>
    /// <c>apply_obmc</c> - overlapped-block motion compensation, which H.263's advanced
    /// prediction mode turns on.
    /// </summary>
    /// <remarks>
    /// Each 8x8 block is predicted five times - with its own vector and with those of its four
    /// neighbours - and the five are blended. The neighbour cache substitutes this block's own
    /// vector at a picture edge and at an intra neighbour, which is what makes the filter a
    /// no-op there rather than a smear of something unrelated. The bottom row of the cache is
    /// a copy of the middle row, because the macroblock below has not been decoded.
    /// </remarks>
    private static void ApplyObmc(
        ref MpegEncContext s, int destY, int destCb, int destCr,
        byte[] refFrame, int refY, int refCb, int refCr, bool average, bool round)
    {
        // mv_cache[4][4][2], flattened. Row 0 is the macroblock above, rows 1 and 2 this one,
        // row 3 a copy of row 2 because the macroblock below is not decoded yet.
        Span<short> mvCache = stackalloc short[4 * 4 * 2];
        int mbX = s.MbX;
        int mbY = s.MbY;
        int xy = mbX + mbY * s.MbStride;
        int motStride = s.B8Stride;
        int motXy = mbX * 2 + mbY * 2 * motStride;

        short[] mv = s.CurPic.MotionValL0!;
        int mo = s.CurPic.MotionValOffsetL0;
        uint[] mbTypeTab = s.CurPic.MbType!;
        int mt = s.CurPic.MbTypeOffset;

        CacheFromField(mvCache, 1, 1, mv, mo, motXy);
        CacheFromField(mvCache, 1, 2, mv, mo, motXy + 1);
        CacheFromField(mvCache, 2, 1, mv, mo, motXy + motStride);
        CacheFromField(mvCache, 2, 2, mv, mo, motXy + motStride + 1);
        CacheFromField(mvCache, 3, 1, mv, mo, motXy + motStride);
        CacheFromField(mvCache, 3, 2, mv, mo, motXy + motStride + 1);

        if (mbY == 0 || MbType.IsIntra(mbTypeTab[mt + xy - s.MbStride]))
        {
            CacheCopy(mvCache, 0, 1, 1, 1);
            CacheCopy(mvCache, 0, 2, 1, 2);
        }
        else
        {
            CacheFromField(mvCache, 0, 1, mv, mo, motXy - motStride);
            CacheFromField(mvCache, 0, 2, mv, mo, motXy - motStride + 1);
        }

        if (mbX == 0 || MbType.IsIntra(mbTypeTab[mt + xy - 1]))
        {
            CacheCopy(mvCache, 1, 0, 1, 1);
            CacheCopy(mvCache, 2, 0, 2, 1);
        }
        else
        {
            CacheFromField(mvCache, 1, 0, mv, mo, motXy - 1);
            CacheFromField(mvCache, 2, 0, mv, mo, motXy - 1 + motStride);
        }

        if (mbX + 1 >= s.MbWidth || MbType.IsIntra(mbTypeTab[mt + xy + 1]))
        {
            CacheCopy(mvCache, 1, 3, 1, 2);
            CacheCopy(mvCache, 2, 3, 2, 2);
        }
        else
        {
            CacheFromField(mvCache, 1, 3, mv, mo, motXy + 2);
            CacheFromField(mvCache, 2, 3, mv, mo, motXy + 2 + motStride);
        }

        int linesize = (int)s.Linesize;
        int mx = 0, my = 0;
        Span<short> five = stackalloc short[5 * 2];

        for (int i = 0; i < 4; i++)
        {
            int x = (i & 1) + 1;
            int y = (i >> 1) + 1;

            // mid, top, left, right, bottom - the order put_obmc's weights assume.
            CacheTake(five, 0, mvCache, y, x);
            CacheTake(five, 1, mvCache, y - 1, x);
            CacheTake(five, 2, mvCache, y, x - 1);
            CacheTake(five, 3, mvCache, y, x + 1);
            CacheTake(five, 4, mvCache, y + 1, x);

            ObmcMotion(ref s, destY + (i & 1) * 8 + (i >> 1) * 8 * linesize,
                       refFrame, refY,
                       mbX * 16 + (i & 1) * 8, mbY * 16 + (i >> 1) * 8,
                       round, five);

            mx += five[0];
            my += five[1];
        }

        Chroma4MvMotion(ref s, destCb, destCr, refFrame, refCb, refCr, average, round, mx, my);
    }

    /// <summary>One <c>AV_COPY32</c> from the picture's motion field into the cache.</summary>
    private static void CacheFromField(Span<short> cache, int cy, int cx, short[] mv, int mo,
                                       int entry)
    {
        cache[(cy * 4 + cx) * 2 + 0] = mv[mo + entry * 2 + 0];
        cache[(cy * 4 + cx) * 2 + 1] = mv[mo + entry * 2 + 1];
    }

    /// <summary>One <c>AV_COPY32</c> within the cache, for an edge or an intra neighbour.</summary>
    private static void CacheCopy(Span<short> cache, int cy, int cx, int sy, int sx)
    {
        cache[(cy * 4 + cx) * 2 + 0] = cache[(sy * 4 + sx) * 2 + 0];
        cache[(cy * 4 + cx) * 2 + 1] = cache[(sy * 4 + sx) * 2 + 1];
    }

    /// <summary>Takes one of the five vectors obmc_motion is given.</summary>
    private static void CacheTake(Span<short> five, int slot, ReadOnlySpan<short> cache,
                                  int cy, int cx)
    {
        five[slot * 2 + 0] = cache[(cy * 4 + cx) * 2 + 0];
        five[slot * 2 + 1] = cache[(cy * 4 + cx) * 2 + 1];
    }

    /// <summary>
    /// <c>qpel_motion</c> - a whole macroblock predicted at quarter-sample accuracy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chroma vector is not the luma vector divided by four. It is halved once with the
    /// rounding the stream's encoder used, then folded to half-pel with
    /// <c>(mx &gt;&gt; 1) | (mx &amp; 1)</c> - an or, not a shift, so a non-zero remainder
    /// survives as the half-pel bit instead of being discarded.
    /// </para>
    /// <para>
    /// The field-based arm is not translated: interlaced MPEG-4 is not ported, and falling
    /// through to the progressive one would predict from the wrong lines.
    /// </para>
    /// </remarks>
    private static void QpelMotion(
        ref MpegEncContext s, int destY, int destCb, int destCr,
        int fieldBased, int bottomField, int fieldSelect,
        byte[] refFrame, int refY, int refCb, int refCr,
        bool average, bool round, int motionX, int motionY, int h)
    {
        if (fieldBased != 0)
            throw new NotSupportedException(
                "qpel_motion: the field-based arm is interlaced MPEG-4 and is not translated.");

        int dx = motionX & 3;
        int dy = motionY & 3;

        int srcX = s.MbX * 16 + (motionX >> 2);
        int srcY = s.MbY * 16 + (motionY >> 2);

        int vEdgePos = s.VEdgePos;
        int linesize = (int)s.Linesize;
        int uvlinesize = (int)s.Uvlinesize;

        int mx, my;
        if ((s.WorkaroundBugs & Mpeg4VideoDec.FfBugQpelChroma2) != 0)
        {
            ReadOnlySpan<int> rtab = [0, 0, 1, 1, 0, 0, 0, 1];
            mx = (motionX >> 1) + rtab[motionX & 7];
            my = (motionY >> 1) + rtab[motionY & 7];
        }
        else if ((s.WorkaroundBugs & Mpeg4VideoDec.FfBugQpelChroma) != 0)
        {
            mx = (motionX >> 1) | (motionX & 1);
            my = (motionY >> 1) | (motionY & 1);
        }
        else
        {
            mx = motionX / 2;
            my = motionY / 2;
        }
        mx = (mx >> 1) | (mx & 1);
        my = (my >> 1) | (my & 1);

        int uvdxy = (mx & 1) | ((my & 1) << 1);
        mx >>= 1;
        my >>= 1;

        int uvsrcX = s.MbX * 8 + mx;
        int uvsrcY = s.MbY * 8 + my;

        byte[] lumaFrame = refFrame, cbFrame = refFrame, crFrame = refFrame;
        int ptrY = refY + srcY * linesize + srcX;
        int ptrCb = refCb + uvsrcY * uvlinesize + uvsrcX;
        int ptrCr = refCr + uvsrcY * uvlinesize + uvsrcX;

        if ((uint)srcX >= (uint)Math.Max(s.HEdgePos - dx - 15, 0) ||
            (uint)srcY >= (uint)Math.Max(vEdgePos - dy - h + 1, 0))
        {
            byte[] emu = s.Sc.ScratchpadBuf!;
            int lumaBuf = s.Sc.EdgeEmuBufferOffset;
            int cbBuf = lumaBuf + 18 * linesize;
            int crBuf = cbBuf + 10 * uvlinesize;
            if ((s.WorkaroundBugs & Mpeg4VideoDec.FfBugIedge) != 0)
                crBuf -= uvlinesize;

            VideoDsp.EmulatedEdgeMc(emu, lumaBuf, refFrame, ptrY, linesize, linesize,
                                    17, 17, srcX, srcY, s.HEdgePos, s.VEdgePos);
            VideoDsp.EmulatedEdgeMc(emu, cbBuf, refFrame, ptrCb, uvlinesize, uvlinesize,
                                    9, 9, uvsrcX, uvsrcY, s.HEdgePos >> 1, s.VEdgePos >> 1);
            VideoDsp.EmulatedEdgeMc(emu, crBuf, refFrame, ptrCr, uvlinesize, uvlinesize,
                                    9, 9, uvsrcX, uvsrcY, s.HEdgePos >> 1, s.VEdgePos >> 1);

            lumaFrame = cbFrame = crFrame = emu;
            ptrY = lumaBuf;
            ptrCb = cbBuf;
            ptrCr = crBuf;
        }

        QpelTab(average, round, s.CurPic.Frame!, destY, lumaFrame, ptrY, linesize, 16, dx, dy);

        HpelDsp.PixelsTab(uvdxy, 8, average, round, s.CurPic.Frame!, destCr,
                          crFrame, ptrCr, uvlinesize, h >> 1);
        HpelDsp.PixelsTab(uvdxy, 8, average, round, s.CurPic.Frame!, destCb,
                          cbFrame, ptrCb, uvlinesize, h >> 1);
    }

    /// <summary>
    /// <c>qpix_op[size][dxy]</c> - picks the quarter-pel kernel the way ff_mpv_motion picks
    /// the table it passes down: averaging for the second half of a bidirectional prediction,
    /// and the no-rounding variant where the picture header asked for it.
    /// </summary>
    private static void QpelTab(bool average, bool round, Span<byte> dst, int dstOff,
                                ReadOnlySpan<byte> src, int srcOff, int stride, int size,
                                int dx, int dy)
    {
        if (average)
            QpelDsp.Qpel<QpelDsp.Avg>(dst, dstOff, src, srcOff, stride, size, dx, dy);
        else if (round)
            QpelDsp.Qpel<QpelDsp.Put>(dst, dstOff, src, srcOff, stride, size, dx, dy);
        else
            QpelDsp.Qpel<QpelDsp.PutNoRnd>(dst, dstOff, src, srcOff, stride, size, dx, dy);
    }

    /// <summary>
    /// <c>apply_8x8</c> - the four-vector mode: four 8x8 luma predictions and one chroma
    /// prediction from their sum.
    /// </summary>
    /// <remarks>
    /// The two arms differ in more than the interpolator: the quarter-sample one clips the
    /// source position itself and drops the fractional part when the clip bites, and it halves
    /// each vector before summing them for chroma, where the half-pel arm sums them whole.
    /// </remarks>
    private static void Apply8X8(
        ref MpegEncContext s, int destY, int destCb, int destCr, int dir,
        byte[] refFrame, int refY, int refCb, int refCr, bool average, bool round)
    {
        int mbX = s.MbX;
        int mbY = s.MbY;
        int linesize = (int)s.Linesize;
        int mx = 0, my = 0;

        if (s.QuarterSample != 0)
        {
            byte[] dstFrame = s.CurPic.Frame!;

            for (int i = 0; i < 4; i++)
            {
                int motionX = s.Mv[MvIndex(dir, i, 0)];
                int motionY = s.Mv[MvIndex(dir, i, 1)];

                int dx = motionX & 3;
                int dy = motionY & 3;
                int srcX = mbX * 16 + (motionX >> 2) + (i & 1) * 8;
                int srcY = mbY * 16 + (motionY >> 2) + (i >> 1) * 8;

                // Do not forget half pels: at the right or bottom edge the position is
                // clipped onto the last whole sample and the fractional part goes with it.
                srcX = Math.Clamp(srcX, -16, s.Width);
                if (srcX == s.Width)
                    dx = 0;
                srcY = Math.Clamp(srcY, -16, s.Height);
                if (srcY == s.Height)
                    dy = 0;

                byte[] srcFrame = refFrame;
                int ptr = refY + srcY * linesize + srcX;

                if ((uint)srcX >= (uint)Math.Max(s.HEdgePos - (motionX & 3) - 7, 0) ||
                    (uint)srcY >= (uint)Math.Max(s.VEdgePos - (motionY & 3) - 7, 0))
                {
                    byte[] emu = s.Sc.ScratchpadBuf!;
                    int buf = s.Sc.EdgeEmuBufferOffset;
                    VideoDsp.EmulatedEdgeMc(emu, buf, refFrame, ptr, linesize, linesize,
                                            9, 9, srcX, srcY, s.HEdgePos, s.VEdgePos);
                    srcFrame = emu;
                    ptr = buf;
                }

                int dest = destY + (i & 1) * 8 + (i >> 1) * 8 * linesize;
                QpelTab(average, round, dstFrame, dest, srcFrame, ptr, linesize, 8, dx, dy);

                mx += motionX / 2;
                my += motionY / 2;
            }

            Chroma4MvMotion(ref s, destCb, destCr, refFrame, refCb, refCr, average, round,
                            mx, my);
            return;
        }

        for (int i = 0; i < 4; i++)
        {
            HpelMotion(ref s, s.CurPic.Frame!, destY + (i & 1) * 8 + (i >> 1) * 8 * linesize,
                       refFrame, refY,
                       mbX * 16 + (i & 1) * 8,
                       mbY * 16 + (i >> 1) * 8,
                       average, round,
                       s.Mv[MvIndex(dir, i, 0)], s.Mv[MvIndex(dir, i, 1)]);

            mx += s.Mv[MvIndex(dir, i, 0)];
            my += s.Mv[MvIndex(dir, i, 1)];
        }

        Chroma4MvMotion(ref s, destCb, destCr, refFrame, refCb, refCr, average, round, mx, my);
    }

    /// <summary>
    /// <c>mpv_motion_internal</c> with <c>is_mpeg12</c> set - dispatches on
    /// <see cref="MpegEncContext.MvType"/> and applies the macroblock's vectors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The field and dual-prime arms can read the picture being decoded as their reference, which
    /// looks wrong until you remember that in a field picture the first field of the same frame
    /// is already decoded and is the natural opposite-parity reference. That substitution is what
    /// <c>picture_structure != field_select + 1</c> is testing for.
    /// </para>
    /// <para>
    /// Dual prime makes two predictions and averages them, which is why <c>average</c> turns on
    /// after the first.
    /// </para>
    /// </remarks>
    private static void MpvMotionInternal(
        ref MpegEncContext s, int destY, int destCb, int destCr, int dir,
        in MPVWorkPicture refPicture, bool average, bool round)
    {
        int mbY = s.MbY;

        byte[] refFrame = refPicture.Frame!;
        int refY = refPicture.Data[0];
        int refCb = refPicture.Data[1];
        int refCr = refPicture.Data[2];

        // Where FFmpeg writes ref_picture = s->cur_pic.ptr->f->data it means the FRAME base of
        // the picture being decoded, not the field view the macroblock layer writes through.
        // The stride used with it is still the field-doubled one, so frame base plus doubled
        // stride reads the top field and field_select steps to the bottom - which is exactly
        // how a second field predicts from the first.
        ref MPVPicture curFrame = ref s.BufferPools.Pool!.Picture(s.CurPic.Ptr);

        if (s.Obmc != 0 && s.PictType != AvPictureType.B)
        {
            ApplyObmc(ref s, destY, destCb, destCr,
                      refFrame, refY, refCb, refCr, average, round);
            return;
        }

        switch (s.MvType)
        {
            case MpegVideoConstants.MvType16X16:
                if (s.Mcsel != 0)
                {
                    throw new NotSupportedException(
                        "ff_mpeg4_mcsel_motion: global motion compensation is not translated.");
                }
                else if (s.QuarterSample != 0)
                {
                    QpelMotion(ref s, destY, destCb, destCr, 0, 0, 0,
                               refFrame, refY, refCb, refCr, average, round,
                               s.Mv[MvIndex(dir, 0, 0)], s.Mv[MvIndex(dir, 0, 1)], 16);
                }
                else
                {
                    MpegMotion(ref s, destY, destCb, destCr, 0, refFrame, refY, refCb, refCr,
                               average, round,
                               s.Mv[MvIndex(dir, 0, 0)], s.Mv[MvIndex(dir, 0, 1)], 16, 0, mbY);
                }
                break;

            case MpegVideoConstants.MvType8X8:
                Apply8X8(ref s, destY, destCb, destCr, dir,
                         refFrame, refY, refCb, refCr, average, round);
                break;

            case MpegVideoConstants.MvTypeField:
                if (s.PictureStructure == MpegVideoConstants.PictFrame)
                {
                    MpegMotionField(ref s, destY, destCb, destCr,
                                    0, s.FieldSelect[FieldSelectIndex(dir, 0)],
                                    refFrame, refY, refCb, refCr, average, round,
                                    s.Mv[MvIndex(dir, 0, 0)], s.Mv[MvIndex(dir, 0, 1)], mbY);
                    MpegMotionField(ref s, destY, destCb, destCr,
                                    1, s.FieldSelect[FieldSelectIndex(dir, 1)],
                                    refFrame, refY, refCb, refCr, average, round,
                                    s.Mv[MvIndex(dir, 1, 0)], s.Mv[MvIndex(dir, 1, 1)], mbY);
                }
                else
                {
                    if (s.PictureStructure != s.FieldSelect[FieldSelectIndex(dir, 0)] + 1 &&
                        s.PictType != AvPictureType.B && s.FirstField == 0)
                    {
                        refFrame = curFrame.Frame!;
                        refY = curFrame.Data[0];
                        refCb = curFrame.Data[1];
                        refCr = curFrame.Data[2];
                    }

                    MpegMotion(ref s, destY, destCb, destCr,
                               s.FieldSelect[FieldSelectIndex(dir, 0)],
                               refFrame, refY, refCb, refCr, average, round,
                               s.Mv[MvIndex(dir, 0, 0)], s.Mv[MvIndex(dir, 0, 1)], 16, 0, mbY >> 1);
                }
                break;

            case MpegVideoConstants.MvType16X8:
                for (int i = 0; i < 2; i++)
                {
                    byte[] frame2 = refFrame;
                    int y2 = refY, cb2 = refCb, cr2 = refCr;
                    if (s.PictureStructure != s.FieldSelect[FieldSelectIndex(dir, i)] + 1 &&
                        s.PictType != AvPictureType.B && s.FirstField == 0)
                    {
                        frame2 = curFrame.Frame!;
                        y2 = curFrame.Data[0];
                        cb2 = curFrame.Data[1];
                        cr2 = curFrame.Data[2];
                    }

                    MpegMotion(ref s, destY, destCb, destCr,
                               s.FieldSelect[FieldSelectIndex(dir, i)],
                               frame2, y2, cb2, cr2, average, round,
                               s.Mv[MvIndex(dir, i, 0)], s.Mv[MvIndex(dir, i, 1)],
                               8, 1, (mbY & ~1) + i);

                    destY += 16 * (int)s.Linesize;
                    destCb += (16 >> s.ChromaYShift) * (int)s.Uvlinesize;
                    destCr += (16 >> s.ChromaYShift) * (int)s.Uvlinesize;
                }
                break;

            case MpegVideoConstants.MvTypeDmv:
                if (s.PictureStructure == MpegVideoConstants.PictFrame)
                {
                    bool avg = average;
                    for (int i = 0; i < 2; i++)
                    {
                        for (int j = 0; j < 2; j++)
                            MpegMotionField(ref s, destY, destCb, destCr, j, j ^ i,
                                            refFrame, refY, refCb, refCr, avg, round,
                                            s.Mv[MvIndex(dir, 2 * i + j, 0)],
                                            s.Mv[MvIndex(dir, 2 * i + j, 1)], mbY);
                        avg = true;
                    }
                }
                else
                {
                    bool avg = average;
                    for (int i = 0; i < 2; i++)
                    {
                        MpegMotion(ref s, destY, destCb, destCr,
                                   s.PictureStructure != i + 1 ? 1 : 0,
                                   refFrame, refY, refCb, refCr, avg, round,
                                   s.Mv[MvIndex(dir, 2 * i, 0)], s.Mv[MvIndex(dir, 2 * i, 1)],
                                   16, 0, mbY >> 1);

                        // The second prediction is averaged onto the first.
                        avg = true;

                        // In the second field the opposite parity lives in the frame being
                        // decoded, not in the reference.
                        if (s.FirstField == 0)
                        {
                            refFrame = curFrame.Frame!;
                            refY = curFrame.Data[0];
                            refCb = curFrame.Data[1];
                            refCr = curFrame.Data[2];
                        }
                    }
                }
                break;

            default:
                // Unknown input fails loudly: a silent no-op would leave the previous
                // macroblock's samples in place and look like a prediction bug.
                throw new InvalidOperationException($"Unknown MPEG motion vector type {s.MvType}.");
        }
    }

    /// <summary>
    /// <c>ff_mpv_motion</c> - motion compensation for one macroblock in one direction.
    /// </summary>
    /// <remarks>
    /// <c>prefetch_motion</c> is deliberately absent. It issues cache hints for the block four
    /// macroblocks ahead and cannot change a sample; translating it would mean inventing a
    /// prefetch API for no measurable reason on a decoder that is not yet conformant.
    /// </remarks>
    public static void MpvMotion(
        ref MpegEncContext s, int destY, int destCb, int destCr, int dir,
        in MPVWorkPicture refPicture, bool average, bool round)
        => MpvMotionInternal(ref s, destY, destCb, destCr, dir, refPicture, average, round);

    /// <summary><c>mv[i][j][k]</c>, flattened.</summary>
    private static int MvIndex(int i, int j, int k) => i * 8 + j * 2 + k;

    /// <summary><c>field_select[i][j]</c>, flattened.</summary>
    private static int FieldSelectIndex(int i, int j) => i * 2 + j;
}

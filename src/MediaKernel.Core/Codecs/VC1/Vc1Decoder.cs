// Ported from FFmpeg's libavcodec/vc1dec.c.
//
// This is the driver: it allocates the per-picture state the block layer reads, sets the
// defaults a picture header expects to overwrite, and tears it all down again. The picture
// loops and the macroblock decoders live in Vc1Block.cs; the header parsing lives in Vc1.cs.
//
// The arrays here are the ones every VC-1 gate has so far been building by hand, with the
// same interior offsets: FFmpeg allocates 3 * mb_stride for cbp, ttblk, is_intra and luma_mv
// and points at 2 * mb_stride so the interlaced predictors can read two macroblock rows back,
// and it puts one b8 row plus one entry in front of mb_type, blk_mv_type, mv_f and mv_f_next
// so that block_index[] arithmetic is legal at the left and top edges. Those offsets are the
// algorithm, so they are translated before anything that uses them.

using System;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Vc1Video;

/// <summary>
/// One field or slice unit of an Advanced Profile packet, already unescaped - the anonymous
/// struct <c>vc1_decode_frame</c> declares for its <c>slices</c> array.
/// </summary>
/// <remarks>
/// The C's <c>rawbuf</c> and <c>raw_size</c> are left out: they carry the still-escaped bytes
/// to a hardware accelerator, and there is none here.
/// </remarks>
public struct Vc1Slice
{
    /// <summary><c>buf</c> - the unescaped bytes, grown to fit and kept between packets.</summary>
    public byte[]? Buf;

    /// <summary><c>gb</c> - a reader over <see cref="Buf"/>, past this unit's slice header.</summary>
    public GetBitContext Gb;

    /// <summary><c>mby_start</c> - the macroblock row this unit begins at.</summary>
    public int MbyStart;
}

/// <summary>
/// <c>vc1dec.c</c> - the VC-1 and WMV3 driver: allocation, defaults, the packet and slice
/// loop, and teardown.
/// </summary>
public static class Vc1Decoder
{
    /// <summary>
    /// <c>vc1_decode_init_alloc_tables</c> - allocates every per-picture array the block
    /// layer reads and sets the interior offsets into them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FFmpeg rounds the macroblock height up to a multiple of two here, because an
    /// interlaced field picture has half as many macroblock rows as the frame and an odd
    /// frame height would otherwise leave the second field one row short.
    /// </para>
    /// <para>
    /// Diverges from the C in two ways, both forced and both recorded in the function
    /// checklist: a C# array is its own length so the <c>av_malloc</c> failure checks have no
    /// counterpart, and an interior pointer becomes a base array plus an offset. The
    /// <c>sr_rows</c> allocation for the WMV3IMAGE and VC1IMAGE sprite modes is left out
    /// because those codec IDs are out of scope, and so is
    /// <c>ff_intrax8_common_init</c>.
    /// </para>
    /// </remarks>
    public static void Vc1DecodeInitAllocTables(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;
        int mbHeight = (s.MbHeight + 1) & ~1;    // FFALIGN(s->mb_height, 2)

        // Allocate mb bitplanes
        int planeSize = s.MbStride * mbHeight;
        v.MvTypeMbPlane = new byte[planeSize];
        v.DirectMbPlane = new byte[planeSize];
        v.ForwardMbPlane = new byte[planeSize];
        v.FieldtxPlane = new byte[planeSize];
        v.AcpredPlane = new byte[planeSize];
        v.OverFlagsPlane = new byte[planeSize];

        v.NAllocatedBlks = s.MbWidth + 2;
        v.Block = new short[v.NAllocatedBlks * 6 * 64];

        v.CbpBase = new uint[3 * s.MbStride];
        v.CbpOffset = 2 * s.MbStride;
        v.TtblkBase = new int[3 * s.MbStride];
        v.TtblkOffset = 2 * s.MbStride;
        v.IsIntraBase = new byte[3 * s.MbStride];
        v.IsIntraOffset = 2 * s.MbStride;
        // luma_mv is a pair per macroblock, so its offset counts in shorts, not pairs.
        v.LumaMvBase = new short[3 * s.MbStride * 2];
        v.LumaMvOffset = 2 * s.MbStride * 2;

        // allocate block type info in that way so it could be used with s->block_index[]
        int typeSize = s.B8Stride * (mbHeight * 2 + 1) + s.MbStride * (mbHeight + 1) * 2;
        v.MbTypeBase = new byte[typeSize];
        v.MbTypeOffset = s.B8Stride + 1;

        // allocate memory to store block level MV info
        v.BlkMvTypeBase = new byte[s.B8Stride * (mbHeight * 2 + 1)];
        v.BlkMvTypeOffset = s.B8Stride + 1;
        v.MvFBase = new byte[2 * typeSize];
        v.MvFOffset[0] = s.B8Stride + 1;
        v.MvFOffset[1] = v.MvFOffset[0] + typeSize;
        v.MvFNextBase = new byte[2 * typeSize];
        v.MvFNextOffset[0] = s.B8Stride + 1;
        v.MvFNextOffset[1] = v.MvFNextOffset[0] + typeSize;
    }

    /// <summary>
    /// <c>ff_vc1_init_common</c> - the defaults a picture header expects to overwrite, and
    /// the one-time VLC table build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>pq</c> starts at -1 and <c>mvrange</c> at 0 (7.1.1.18, p80) so that a header which
    /// does not carry them is detectable rather than silently zero. The DSP initialisers the C
    /// calls here have no counterpart: this port replaced its function-pointer tables with
    /// direct static calls, which <see cref="Vc1Context"/> records.
    /// </para>
    /// <para>
    /// <c>out_format</c> is not cosmetic. <c>alloc_picture_tables</c> gives a picture its
    /// <c>motion_val</c> and <c>ref_index</c> only for FMT_H263, and every VC-1 predictor
    /// writes <c>motion_val</c>; leaving it at the enum's zero would hand the macroblock layer
    /// a null array on the first real picture.
    /// </para>
    /// </remarks>
    public static void Vc1InitCommon(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;

        // defaults
        v.Pq = unchecked((byte)-1);
        v.Mvrange = 0;                  // 7.1.1.18, p80

        v.Avctx.ChromaSampleLocation = 1;      // AVCHROMA_LOC_LEFT
        s.OutFormat = OutputFormat.H263;

        s.H263Pred = 1;
        s.Msmpeg4Version = MsMpeg4Version.Vc1;

        // VLC tables
        Vc1Dec.Vc1InitStatic();
    }

    /// <summary>
    /// <c>vc1_decode_reset</c> - releases everything
    /// <see cref="Vc1DecodeInitAllocTables"/> allocated.
    /// </summary>
    /// <remarks>
    /// The C frees each array and zeroes <c>s->block_index</c>; here dropping the references
    /// is what frees them, and the zeroing is kept because a stale block index would survive a
    /// reinitialisation and index the new arrays as if they had the old geometry.
    /// </remarks>
    public static void Vc1DecodeReset(Vc1Context v)
    {
        for (int i = 0; i < 6; i++)
            v.S.BlockIndex[i] = 0;
        v.MvTypeMbPlane = null;
        v.DirectMbPlane = null;
        v.ForwardMbPlane = null;
        v.FieldtxPlane = null;
        v.AcpredPlane = null;
        v.OverFlagsPlane = null;
        v.MbTypeBase = null;
        v.MbTypeOffset = 0;
        v.BlkMvTypeBase = null;
        v.BlkMvTypeOffset = 0;
        v.MvFBase = null;
        v.MvFOffset[0] = v.MvFOffset[1] = 0;
        v.MvFNextBase = null;
        v.MvFNextOffset[0] = v.MvFNextOffset[1] = 0;
        v.Block = null;
        v.NAllocatedBlks = 0;
        v.CbpBase = null;
        v.CbpOffset = 0;
        v.TtblkBase = null;
        v.TtblkOffset = 0;
        v.IsIntraBase = null;           // FIXME use v->mb_type[]
        v.IsIntraOffset = 0;
        v.LumaMvBase = null;
        v.LumaMvOffset = 0;
    }

    /// <summary>
    /// <c>ff_vc1_decode_init</c> - everything that depends on the picture size, which is not
    /// known until a sequence header has been read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The C reaches this through <c>ff_mpv_decode_init</c> and <c>ff_mpv_common_init</c>,
    /// neither of which this port has translated whole - the MPEG-video layer here is a set of
    /// functions each decoder calls, not a framework that initialises itself. What those two
    /// do for VC-1 is written out below, in the order the C does it, and the parts that have
    /// no counterpart (the DSP tables, the picture pool, the thread contexts, the pixel
    /// format) are noted where they would have been.
    /// </para>
    /// <para>
    /// <c>vc1_get_format</c> is DIVERGED and not translated: the C asks <c>ff_get_format</c>
    /// to choose between the hardware-accelerated 4:2:0 formats and plain 4:2:0 planar, and
    /// with no hwaccel and no gray mode there is nothing to choose between and no pixel format
    /// field for the answer to land in. The chroma shifts <c>ff_mpv_common_init</c> would
    /// derive from it are written out below.
    /// </para>
    /// <para>
    /// Safe to call again: <c>vc1_decode_frame</c> does exactly that when a stream changes
    /// size, after <see cref="Vc1DecodeReset"/>.
    /// </para>
    /// </remarks>
    /// <returns>0, or a negative error.</returns>
    public static int FfVc1DecodeInit(Vc1Context v)
    {
        ArgumentNullException.ThrowIfNull(v);

        ref MpegEncContext s = ref v.S;
        ref Vc1CodecContext avctx = ref v.Avctx;

        // av_image_check_size, which is av_image_check_size2 with INT64_MAX rather than the
        // context's max_pixels - that limit is only consulted where the caller set one.
        int ret = Vc1CodecContext.AvImageCheckSize2(
            (uint)avctx.Width, (uint)avctx.Height, long.MaxValue);
        if (ret < 0)
            return ret;

        // --- ff_mpv_decode_init -------------------------------------------------------------
        // ff_mpv_common_defaults: the fields a picture header may leave alone.
        // PictureStructure matters most - its zero is PICT_TOP_FIELD, which halves the
        // vertical step between macroblock rows.
        s.ChromaQscaleTable = null;
        s.ProgressiveFrame = 1;
        s.ProgressiveSequence = 1;
        s.PictureStructure = MpegVideoConstants.PictFrame;
        s.SliceContextCount = 1;

        s.Width = avctx.CodedWidth;
        s.Height = avctx.CodedHeight;
        s.CodecId = avctx.CodecId is Vc1CodecId.Wmv3 or Vc1CodecId.Wmv3Image
            ? MpegCodecId.Wmv3
            : MpegCodecId.Vc1;

        // ff_mpv_idct_init, ff_h264chroma_init and the picture pool allocation have no
        // counterpart: the transforms and chroma MC are static calls here, and the pool is
        // created by ff_mpv_frame_start on first use.

        // --- ff_mpv_common_init -------------------------------------------------------------
        // The pixel format is always 4:2:0 planar here, so the shifts
        // av_pix_fmt_get_chroma_sub_sample would return are written out.
        s.ChromaFormat = MpegVideoConstants.Chroma420;
        s.ChromaXShift = 1;
        s.ChromaYShift = 1;

        Vc1InitContextFrame(v);
        s.ContextInitialized = 1;

        Vc1DecodeInitAllocTables(v);
        return 0;
    }

    /// <summary>
    /// <c>ff_mpv_init_context_frame</c>, as VC-1 reaches it - the macroblock grid, the strides
    /// derived from it, and the shared per-picture tables the block layer reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The C's encoder arms, slice-context arithmetic, MPEG-4 direct-mode tables and
    /// refcounted pools are all out of this path; what is left is the geometry,
    /// <c>coded_block</c>, <c>ac_val</c>, <c>dc_val</c>, the skip table and error resilience.
    /// </para>
    /// <para>
    /// <c>mb_height</c> is rounded up to a multiple of two for everything sized per macroblock
    /// row, for the reason the C gives: a VC-1 stream switches between progressive and
    /// interlaced per picture, and a field picture of an odd-height frame adds
    /// <c>b8_stride * (mb_height &amp; ~1)</c> to every block index. The macroblock grid itself
    /// keeps the true height.
    /// </para>
    /// </remarks>
    private static void Vc1InitContextFrame(Vc1Context v)
    {
        ref MpegEncContext s = ref v.S;

        s.MbHeight = (s.Height + 15) / 16;
        int mbHeight = (s.MbHeight + 1) & ~1;       // FFALIGN(s->mb_height, 2), MSMP4_VC1

        s.MbWidth = (s.Width + 15) / 16;
        s.MbStride = s.MbWidth + 1;
        s.B8Stride = s.MbWidth * 2 + 1;

        // The default edge positions; an Advanced Profile picture replaces them in
        // vc1_decode_frame with the coded size, which need not be a multiple of sixteen.
        s.HEdgePos = s.MbWidth * 16;
        s.VEdgePos = s.MbHeight * 16;

        s.MbNum = s.MbWidth * s.MbHeight;

        s.BlockWrap[0] =
        s.BlockWrap[1] =
        s.BlockWrap[2] =
        s.BlockWrap[3] = s.B8Stride;
        s.BlockWrap[4] =
        s.BlockWrap[5] = s.MbStride;

        int ySize = s.B8Stride * (2 * mbHeight + 1);
        int cSize = s.MbStride * (mbHeight + 1);
        int ycSize = ySize + 2 * cSize;

        // msmpeg4_version >= MSMP4_V3, which MSMP4_VC1 is: the coded-block plane
        // vc1_coded_block_pred predicts through.
        s.CodedBlockBase = new byte[ySize];
        s.CodedBlockOffset = s.B8Stride + 1;

        // h263_pred is 1, so both prediction planes are allocated. dc_val starts at 1024, the
        // neutral DC at this scale, because an intra block at the picture edge predicts from
        // entries no macroblock has written.
        s.AcValBase = new short[ycSize * 16];
        s.AcValOffset = (s.B8Stride + 1) * 16;

        s.DcValBase = new short[ycSize];
        s.DcValOffset = s.B8Stride + 1;
        s.DcValBase.AsSpan().Fill(1024);

        // "Note the + 1 is for a quicker MPEG-4 slice_end detection", which VC-1 does not do;
        // the size is the C's because ff_mpv_frame_start reuses whatever is here.
        s.MbskipTable = new byte[mbHeight * s.MbStride + 2];

        MpegEr.MpegErInit(ref s);
        MpegEr.MpegErBindTables(ref s);
    }

    /// <summary>
    /// <c>vc1_decode_init</c> - reads the extradata and sizes everything that follows from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two extradata layouts cannot be told apart from their bytes, which is why this
    /// branches on <see cref="Vc1CodecContext.CodecId"/> as the C does. WMV3 stores a bare
    /// sequence header with a version byte at the end; VC-1 stores marker-delimited units - a
    /// sequence header and an entry point, each escaped - which are scanned for, unescaped
    /// into a scratch buffer and parsed, and both are required.
    /// </para>
    /// <para>
    /// Does not allocate the per-picture tables: the C leaves that to
    /// <see cref="FfVc1DecodeInit"/> once a picture has confirmed the size, and so does this.
    /// </para>
    /// </remarks>
    /// <returns>0, or a negative error.</returns>
    public static int Vc1DecodeInit(Vc1Context v)
    {
        ArgumentNullException.ThrowIfNull(v);

        ref MpegEncContext s = ref v.S;
        ref Vc1CodecContext avctx = ref v.Avctx;

        // save the container output size for WMImage
        v.OutputWidth = avctx.Width;
        v.OutputHeight = avctx.Height;

        byte[]? extradata = avctx.Extradata;
        if (extradata is null || extradata.Length == 0)
            return Vc1.AverrorInvalidData;

        Vc1InitCommon(v);

        int ret;
        if (avctx.CodecId is Vc1CodecId.Wmv3 or Vc1CodecId.Wmv3Image)
        {
            // looks like WMV3 has a sequence header stored in the extradata
            // advanced sequence header may be before the first frame
            // the last byte of the extradata is a version number, 1 for the
            // samples we can decode
            var gb = default(GetBitContext);
            gb.InitGetBits8(extradata, extradata.Length);

            ret = Vc1.Vc1DecodeSequenceHeader(v, ref gb);
            if (ret < 0)
                return ret;

            if (avctx.CodecId == Vc1CodecId.Wmv3Image && v.ResSprite == 0)
                return Vc1.AverrorPatchwelcome;

            // The C logs how many bits are left over, or how far it overran. Nothing reads it.
        }
        else
        {
            // VC1/WVC1/WVP2
            if (extradata.Length < 16)
                return Vc1.AverrorInvalidData;

            int end = extradata.Length;
            byte[] buf2 = GrowScratch(ref v.Buf2, end + Vc1.InputBufferPaddingSize);
            bool seqInitialized = false, epInitialized = false;

            // in WVC1 extradata the first byte is its size, but it can be 0 in mkv
            int start = Vc1Common.FindNextMarker(extradata, 0, end);
            for (int next = start; next < end; start = next)
            {
                next = Vc1Common.FindNextMarker(extradata, start + 4, end);
                int size = next - start - 4;
                if (size <= 0)
                    continue;

                int buf2Size = Vc1Common.Vc1UnescapeBuffer(
                    extradata.AsSpan(start + 4, size), size, buf2);
                var gb = default(GetBitContext);
                gb.InitGetBits8(buf2, buf2Size);

                uint code = (uint)((extradata[start] << 24) | (extradata[start + 1] << 16)
                                 | (extradata[start + 2] << 8) | extradata[start + 3]);
                if (code == Vc1Code.SeqHdr)
                {
                    ret = Vc1.Vc1DecodeSequenceHeader(v, ref gb);
                    if (ret < 0)
                        return ret;
                    seqInitialized = true;
                }
                else if (code == Vc1Code.EntryPoint)
                {
                    ret = Vc1.Vc1DecodeEntryPoint(v, ref gb);
                    if (ret < 0)
                        return ret;
                    epInitialized = true;
                }
            }

            if (!seqInitialized || !epInitialized)
                return Vc1.AverrorInvalidData;      // Incomplete extradata

            v.ResSprite = avctx.CodecId == Vc1CodecId.Vc1Image ? 1 : 0;
        }

        avctx.Profile = v.Profile;
        if (v.Profile == (int)Vc1Profile.Advanced)
            avctx.Level = v.Level;

        // ff_blockdsp_init and ff_h264chroma_init have no counterpart; both are static here.

        avctx.HasBFrames = avctx.MaxBFrames != 0 ? 1 : 0;

        if (v.ColorPrim == 1 || v.ColorPrim == 5 || v.ColorPrim == 6)
            avctx.ColorPrimaries = v.ColorPrim;
        if (v.TransferChar == 1 || v.TransferChar == 7)
            avctx.ColorTrc = v.TransferChar;
        if (v.MatrixCoef == 1 || v.MatrixCoef == 6 || v.MatrixCoef == 7)
            avctx.Colorspace = v.MatrixCoef;

        s.MbWidth = (avctx.CodedWidth + 15) >> 4;
        s.MbHeight = (avctx.CodedHeight + 15) >> 4;

        if (v.Profile == (int)Vc1Profile.Advanced || v.ResFasttx != 0)
        {
            Vc1Dec.Vc1InitTransposedScantables(v);
        }
        else
        {
            // DIVERGENCE, recorded, and it fails loudly rather than quietly: the C's other arm
            // takes the untransposed WMV1 scan and swaps all eight of vc1dsp's inverse
            // transforms for the simple IDCT. This port calls the VC-1 transforms directly
            // instead of through a function-pointer table, so there is no table to swap, and a
            // stream that needs the simple IDCT would otherwise be transformed by the wrong
            // eight functions and come out looking almost right. RES_FASTTX is reserved and is
            // 1 in every conformant stream; the streams that clear it are pre-release WMV3,
            // which also carry sixteen bits the sequence header skips.
            throw new NotSupportedException(
                "VC-1: a Simple or Main Profile stream with RES_FASTTX clear needs the simple "
              + "IDCT in place of the VC-1 inverse transforms, which this port calls directly "
              + "rather than through a replaceable table");
        }

        if (avctx.CodecId is Vc1CodecId.Wmv3Image or Vc1CodecId.Vc1Image)
        {
            // The sprite dimension juggling that follows here in the C only matters to the
            // sprite compositor, which is out of scope - see Vc1Context.
            throw new NotSupportedException(
                "VC-1: the WMV3IMAGE and VC1IMAGE sprite compositors are out of scope");
        }

        return 0;
    }

    /// <summary>
    /// <c>ff_vc1_decode_end</c> - closes the decoder.
    /// </summary>
    /// <remarks>
    /// The C follows <c>vc1_decode_reset</c> with <c>ff_mpv_decode_close</c>, which releases
    /// the shared picture machinery. The pictures here come from a pool the context owns, so
    /// dropping the three work pictures is what that amounts to.
    /// </remarks>
    public static void Vc1DecodeEnd(Vc1Context v)
    {
        ArgumentNullException.ThrowIfNull(v);

        Vc1DecodeReset(v);
        MpegVideoDec.MpegFlush(ref v.S);
    }

    /// <summary>
    /// <c>vc1_decode_frame</c> - one packet, which is one picture or one interlaced frame's
    /// two fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three of the C's arms are not translated, and each is noted at the point it would have
    /// been: the whole <c>avctx-&gt;hwaccel</c> branch, which is a second copy of the slice
    /// loop that hands the escaped bytes to hardware; the sprite compositor, reached only by
    /// the two IMAGE codec ids; and the <c>AV_EF_EXPLODE</c> arms of the slice-header error
    /// paths, which this port has no error-recognition flags to select.
    /// </para>
    /// <para>
    /// The C's <c>goto err</c> and <c>goto end</c> become returns, which is the one
    /// restructuring the porting rules allow: both labels only free the per-packet
    /// allocations, and there are none here.
    /// </para>
    /// <para>
    /// The frame this call produced is left in <see cref="Vc1Context.OutputPic"/>, in place of
    /// the C's <c>av_frame_ref(pict, ...)</c> and <c>*got_frame</c>.
    /// </para>
    /// </remarks>
    /// <returns>The number of bytes consumed, or a negative error.</returns>
    public static int Vc1DecodeFrame(Vc1Context v, byte[] buf, int bufSize)
    {
        ArgumentNullException.ThrowIfNull(v);
        ArgumentNullException.ThrowIfNull(buf);

        ref MpegEncContext s = ref v.S;
        ref Vc1CodecContext avctx = ref v.Avctx;

        v.OutputPic = default;
        v.SecondField = 0;

        int nSlices = 0, nSlices1 = -1;
        int bufStart = 0, bufStartSecondField = -1;
        int ret;

        if ((avctx.Flags & AvCodecFlag.LowDelay) != 0)
            s.LowDelay = 1;

        if (bufSize >= 4 && ReadBe32(buf, bufSize - 4) == Vc1Code.EndOfSeq)
            bufSize -= 4;

        // no supplementary picture
        if (bufSize == 0)
        {
            // special case for last picture
            if (s.LowDelay == 0 && s.NextPic.Ptr != 0)
            {
                v.OutputPic = s.NextPic;
                s.BufferPools.Pool!.UnrefPicture(ref s.NextPic);
            }

            return bufSize;
        }

        // for advanced profile we may need to parse and unescape data
        if (avctx.CodecId is Vc1CodecId.Vc1 or Vc1CodecId.Vc1Image)
        {
            int buf2Size = 0;
            byte[] buf2 = GrowScratch(ref v.Buf2, bufSize + Vc1.InputBufferPaddingSize);

            if (Vc1Common.ISMarker(ReadBe32(buf, 0)))
            {
                // frame starts with marker and needs to be parsed
                int end = bufSize;
                for (int start = 0, next = 0; next < end; start = next)
                {
                    next = Vc1Common.FindNextMarker(buf, start + 4, end);
                    int size = next - start - 4;
                    if (size <= 0) continue;

                    uint code = ReadBe32(buf, start);
                    if (code == Vc1Code.Frame)
                    {
                        bufStart = start;
                        buf2Size = Vc1Common.Vc1UnescapeBuffer(
                            buf.AsSpan(start + 4, size), size, buf2);
                    }
                    else if (code == Vc1Code.Field)
                    {
                        bufStartSecondField = start;
                        ref Vc1Slice slice = ref GrowSlices(v, nSlices, size);
                        int buf2Size3 = Vc1Common.Vc1UnescapeBuffer(
                            buf.AsSpan(start + 4, size), size, slice.Buf!);
                        slice.Gb.InitGetBits8(slice.Buf!, buf2Size3);
                        slice.MbyStart = (avctx.CodedHeight + 31) >> 5;
                        nSlices1 = nSlices - 1;   // index of the last slice of the first field
                        nSlices++;
                    }
                    else if (code == Vc1Code.EntryPoint)
                    {
                        // it should be before frame data
                        buf2Size = Vc1Common.Vc1UnescapeBuffer(
                            buf.AsSpan(start + 4, size), size, buf2);
                        v.Gb.InitGetBits8(buf2, buf2Size);
                        Vc1.Vc1DecodeEntryPoint(v, ref v.Gb);
                    }
                    else if (code == Vc1Code.Slice)
                    {
                        ref Vc1Slice slice = ref GrowSlices(v, nSlices, size);
                        int buf2Size3 = Vc1Common.Vc1UnescapeBuffer(
                            buf.AsSpan(start + 4, size), size, slice.Buf!);
                        slice.Gb.InitGetBits8(slice.Buf!, buf2Size3);
                        slice.MbyStart = (int)slice.Gb.GetBits(9);
                        nSlices++;
                    }
                }
            }
            else if (v.Interlace != 0 && (buf[0] & 0xC0) == 0xC0)
            {
                // WVC1 interlaced stores both fields divided by marker
                int divider = Vc1Common.FindNextMarker(buf, 0, bufSize);
                if (divider == bufSize || ReadBe32(buf, divider) != Vc1Code.Field)
                    return Vc1.AverrorInvalidData;      // Error in WVC1 interlaced frame

                // found field marker, unescape second field
                bufStartSecondField = divider;
                ref Vc1Slice slice = ref GrowSlices(v, nSlices, bufSize);
                int buf2Size3 = Vc1Common.Vc1UnescapeBuffer(
                    buf.AsSpan(divider + 4, bufSize - divider - 4),
                    bufSize - divider - 4, slice.Buf!);
                slice.Gb.InitGetBits8(slice.Buf!, buf2Size3);
                slice.MbyStart = (s.MbHeight + 1) >> 1;
                nSlices1 = nSlices - 1;
                nSlices++;

                buf2Size = Vc1Common.Vc1UnescapeBuffer(buf.AsSpan(0, divider), divider, buf2);
            }
            else
            {
                buf2Size = Vc1Common.Vc1UnescapeBuffer(buf.AsSpan(0, bufSize), bufSize, buf2);
            }

            v.Gb.InitGetBits8(buf2, buf2Size);
        }
        else
        {
            v.Gb.InitGetBits8(buf, bufSize);
        }

        if (v.ResSprite != 0)
        {
            v.NewSprite = v.Gb.GetBits1() == 0 ? 1 : 0;
            v.TwoSprites = (int)v.Gb.GetBits1();

            // res_sprite means a Windows Media Image stream, AV_CODEC_ID_*IMAGE means the
            // sprite compositor - which is out of scope, so the two IMAGE ids throw from
            // Vc1DecodeInit and cannot reach here. The two bits above are still read, because
            // the WMVP and WVP2 streams that carry them are decoded as raw sprites by the
            // plain wmv3 and vc1 decoders, and skipping them would misalign everything after.
        }

        if (s.ContextInitialized != 0 &&
            (s.Width != avctx.CodedWidth || s.Height != avctx.CodedHeight))
        {
            Vc1DecodeReset(v);
        }

        if (s.ContextInitialized == 0)
        {
            ret = FfVc1DecodeInit(v);
            if (ret < 0)
                return ret;

            s.LowDelay = avctx.HasBFrames == 0 || v.ResSprite != 0 ? 1 : 0;

            if (v.Profile == (int)Vc1Profile.Advanced)
            {
                if (avctx.CodedWidth <= 1 || avctx.CodedHeight <= 1)
                    return Vc1.AverrorInvalidData;

                // Advanced Profile codes a size that need not be a multiple of sixteen, and
                // motion compensation must clamp to it rather than to the macroblock grid.
                s.HEdgePos = avctx.CodedWidth;
                s.VEdgePos = avctx.CodedHeight;
            }
        }

        // do parse frame header
        v.PicHeaderFlag = 0;
        v.FirstPicHeaderFlag = 1;
        ret = v.Profile < (int)Vc1Profile.Advanced
            ? Vc1.Vc1ParseFrameHeader(v, ref v.Gb)
            : Vc1.Vc1ParseFrameHeaderAdv(v, ref v.Gb);
        if (ret < 0)
            return ret;
        v.FirstPicHeaderFlag = 0;

        if ((s.MbHeight >> v.FieldMode) == 0)
            return Vc1.AverrorInvalidData;          // image too short

        // skip B-frames if we don't have reference frames
        if (s.LastPic.Ptr == 0 && s.PictType == AvPictureType.B)
            return bufSize;

        if ((avctx.SkipFrame >= AvDiscard.Nonref && s.PictType == AvPictureType.B) ||
            (avctx.SkipFrame >= AvDiscard.Nonkey && s.PictType != AvPictureType.I) ||
            avctx.SkipFrame >= AvDiscard.All)
        {
            return bufSize;
        }

        MpegVideoDec.MpvFrameStart(ref s);
        MpegEr.MpegErBindTables(ref s);

        MpvPicturePool pool = s.BufferPools.Pool!;
        ref MPVPicture cur = ref pool.Picture(s.CurPic.Ptr);
        cur.FieldPicture = v.FieldMode;
        cur.Interlaced = v.Fcm != FrameCodingMode.Progressive ? 1 : 0;
        cur.TopFieldFirst = v.Tff != 0 ? 1 : 0;
        v.LastInterlaced = s.LastPic.Ptr != 0 ? pool.Picture(s.LastPic.Ptr).Interlaced : 0;
        v.NextInterlaced = s.NextPic.Ptr != 0 ? pool.Picture(s.NextPic.Ptr).Interlaced : 0;

        // process pulldown flags.
        // Pulldown flags are only valid when 'broadcast' has been set.
        cur.RepeatPict = v.Rff != 0 ? 1                 // repeat field
                       : v.Rptfrm != 0 ? v.Rptfrm * 2   // repeat frames
                       : 0;

        // The C's avctx->hwaccel branch is a second copy of everything below, feeding the
        // still-escaped bytes to a hardware decoder. There is no hwaccel here.
        int headerRet = 0;

        MpegEr.MpegErFrameStart(ref s);

        v.EndMbX = s.MbWidth;
        if (v.FieldMode != 0)
        {
            // A field picture's rows are every other row of the frame, so the stride the
            // macroblock layer walks is doubled. The picture keeps its frame line sizes in
            // the pool, which is what lets motion compensation step from one field to the
            // other.
            s.CurPic.Linesize[0] <<= 1;
            s.CurPic.Linesize[1] <<= 1;
            s.CurPic.Linesize[2] <<= 1;
            s.Linesize <<= 1;
            s.Uvlinesize <<= 1;
        }
        int mbHeight = s.MbHeight >> v.FieldMode;

        if (mbHeight <= 0)
        {
            throw new InvalidOperationException(
                $"VC-1: a picture of {mbHeight} macroblock rows reached the slice loop, which "
              + "the field-mode check above exists to prevent");
        }

        for (int i = 0; i <= nSlices; i++)
        {
            if (i > 0 && v.Slices[i - 1].MbyStart >= mbHeight)
            {
                if (v.FieldMode <= 0)
                {
                    // Slice starts beyond picture boundary
                    continue;
                }
                v.SecondField = 1;
                if ((s.MbHeight & 1) != 0)
                {
                    throw new InvalidOperationException(
                        "VC-1: a field picture of an odd number of macroblock rows reached "
                      + "the second field, which cannot be addressed");
                }
                v.BlocksOff = s.B8Stride * (s.MbHeight & ~1);
                v.MbOff = s.MbStride * s.MbHeight >> 1;
            }
            else
            {
                v.SecondField = 0;
                v.BlocksOff = 0;
                v.MbOff = 0;
            }

            if (i != 0)
            {
                v.PicHeaderFlag = 0;
                if (v.FieldMode != 0 && i == nSlices1 + 2)
                {
                    headerRet = Vc1.Vc1ParseFrameHeaderAdv(v, ref v.Gb);
                    if (headerRet < 0)
                        continue;           // Field header damaged
                }
                else if (v.Gb.GetBits1() != 0)
                {
                    v.PicHeaderFlag = 1;
                    headerRet = Vc1.Vc1ParseFrameHeaderAdv(v, ref v.Gb);
                    if (headerRet < 0)
                        continue;           // Slice header damaged
                }
            }
            if (headerRet < 0)
                continue;

            s.StartMbY = i == 0 ? 0 : Math.Max(0, v.Slices[i - 1].MbyStart % mbHeight);
            if (v.FieldMode == 0 || v.SecondField != 0)
            {
                s.EndMbY = i == nSlices
                    ? mbHeight
                    : Math.Min(mbHeight, v.Slices[i].MbyStart % mbHeight);
            }
            else
            {
                if (i >= nSlices)
                    continue;               // first field slice count too large

                s.EndMbY = i == nSlices1 + 1
                    ? mbHeight
                    : Math.Min(mbHeight, v.Slices[i].MbyStart % mbHeight);
            }
            if (s.EndMbY <= s.StartMbY)
                continue;                   // end mb y invalid

            if (((s.PictType == AvPictureType.P && v.PFrameSkipped == 0) ||
                 (s.PictType == AvPictureType.B && v.BiType == 0)) &&
                v.CbpcyVlc is null)
            {
                continue;                   // missing cbpcy_vlc
            }

            Vc1Block.Vc1DecodeBlocks(v);

            if (i != nSlices)
                v.Gb = v.Slices[i].Gb;
        }

        if (v.FieldMode != 0)
        {
            v.SecondField = 0;
            s.CurPic.Linesize[0] >>= 1;
            s.CurPic.Linesize[1] >>= 1;
            s.CurPic.Linesize[2] >>= 1;
            s.Linesize >>= 1;
            s.Uvlinesize >>= 1;

            if (s.PictType != AvPictureType.Bi && s.PictType != AvPictureType.B)
            {
                // FFSWAP(v->mv_f_next[i], v->mv_f[i]) for both i. The C swaps two interior
                // pointers per plane; here each pair has the same offsets into its own base
                // array, so swapping the two bases is the same exchange. The next picture's
                // direct mode reads what this one wrote, which is what makes the swap the
                // whole of the handover.
                (v.MvFBase, v.MvFNextBase) = (v.MvFNextBase, v.MvFBase);
            }
        }

        if (s.Er.ErrorOccurred != 0 && s.PictType == AvPictureType.B)
            return Vc1.AverrorInvalidData;

        // A field picture's two halves are concealed together or not at all, and the C skips
        // concealment entirely for them; the two IMAGE ids cannot reach here.
        if (v.FieldMode == 0)
            ErrorResilience.ErFrameEnd(ref s);

        MpegVideoDec.MpvFrameEnd(ref s);

        // Which picture this call outputs - see Vc1Context.OutputPic. A reordered stream
        // outputs the picture before the one just decoded, and the very first outputs nothing.
        if (s.PictType == AvPictureType.B || s.LowDelay != 0)
            v.OutputPic = s.CurPic;
        else if (s.LastPic.Ptr != 0)
            v.OutputPic = s.LastPic;

        avctx.FrameNum++;
        return bufSize;
    }

    /// <summary>
    /// <c>AV_RB32</c> - four big-endian bytes, which is how every VC-1 marker is compared.
    /// </summary>
    private static uint ReadBe32(byte[] buf, int offset) =>
        (uint)((buf[offset] << 24) | (buf[offset + 1] << 16)
             | (buf[offset + 2] << 8) | buf[offset + 3]);

    /// <summary>
    /// The <c>av_fast_realloc</c> of the slice array plus the <c>av_mallocz</c> of one entry's
    /// buffer, as one step that returns the entry.
    /// </summary>
    /// <remarks>
    /// Both grow to the largest packet seen and are then kept, for the reason
    /// <see cref="Vc1Context.Slices"/> gives. An entry's buffer is zeroed to the size asked
    /// for because the C's is, and the unescaper leaves the tail of its buffer untouched.
    /// </remarks>
    private static ref Vc1Slice GrowSlices(Vc1Context v, int index, int size)
    {
        if (index >= v.Slices.Length)
        {
            // Double rather than add one, as av_fast_realloc does, so that a packet of many
            // slices grows the array a handful of times and not once per slice.
            var grown = new Vc1Slice[Math.Max(4, v.Slices.Length * 2)];
            Array.Copy(v.Slices, grown, v.Slices.Length);
            v.Slices = grown;
        }

        ref Vc1Slice slice = ref v.Slices[index];
        GrowScratch(ref slice.Buf, size + Vc1.InputBufferPaddingSize);
        return ref slice;
    }

    /// <summary>
    /// A scratch buffer that grows to fit and is then kept, in place of the C's per-packet
    /// <c>av_mallocz</c> and <c>av_fast_realloc</c>.
    /// </summary>
    /// <remarks>
    /// Packet sizes vary from frame to frame, so renting an exact-length buffer each time
    /// would allocate on nearly every frame, and this project treats a per-frame allocation as
    /// a defect. The first <paramref name="size"/> bytes are zeroed because the C's
    /// allocations are zeroed and the unescaper leaves the tail of its buffer untouched.
    /// </remarks>
    private static byte[] GrowScratch(ref byte[]? buffer, int size)
    {
        if (buffer is null || buffer.Length < size)
            buffer = new byte[size];
        Array.Clear(buffer, 0, size);
        return buffer;
    }

    /// <summary>
    /// <c>get_fp_val</c> - a sprite transform's fixed-point coefficient.
    /// </summary>
    /// <remarks>
    /// Read by the sprite parser alone, which is out of scope, but it is three lines and
    /// translating it costs less than explaining why it is missing.
    /// </remarks>
    public static int GetFpVal(ref GetBitContext gb) =>
        (int)((gb.GetBits(15) - (1 << 14)) * (1 << 4));
}

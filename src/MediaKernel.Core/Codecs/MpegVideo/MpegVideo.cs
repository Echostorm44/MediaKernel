// Shared MPEG-video types - a translation of ffmpeg-master/libavcodec/mpegvideo.h.
//
// This layer is shared: MPEG-1/2, MPEG-4, H.263 and VC-1 all build on it in FFmpeg, which is why
// it lives beside the codecs rather than inside Codecs/MPEG2. Only the parts MPEG-2 reaches are
// translated as the port needs them; see the port checklist for what is still todo.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MpegVideo;

/// <summary>
/// <c>enum OutputFormat</c> (<c>mpegvideo.h:54</c>). Which codec family the shared MPEG-video
/// layer is driving.
/// </summary>
/// <remarks>
/// The values are positional in C and are relied on as such, so they are written out rather than
/// left implicit. Only <see cref="Mpeg1"/> is reachable from the MPEG-1/2 decoder; the rest are
/// here because the enum is one type and truncating it would misnumber nothing today and
/// everything the moment another family is ported.
/// </remarks>
/// <summary>Which family's unquantiser reconstruction runs; see
/// <see cref="MpegEncContext.DctUnquantize"/>.</summary>
public enum UnquantizeKind
{
    /// <summary>None: the block reader dequantised already (MPEG-1/2 and H.261).</summary>
    None = 0,

    /// <summary><c>dct_unquantize_h263_intra</c> and <c>_inter</c>, both arms.</summary>
    H263 = 1,

    /// <summary>
    /// <c>dct_unquantize_h263_intra</c> alone. MPEG-4 is the codec that needs one unquantiser
    /// for intra blocks and another for inter at the same time, which is why the C keeps
    /// three function pointers where every other codec here needs one.
    /// </summary>
    H263Intra = 2,

    /// <summary><c>dct_unquantize_mpeg2_intra</c>.</summary>
    Mpeg2Intra = 3,

    /// <summary><c>dct_unquantize_mpeg2_inter</c>.</summary>
    Mpeg2Inter = 4,
}

/// <summary>
/// Which inverse transform runs - FFmpeg's <c>avctx-&gt;idct_algo</c>, narrowed to the two
/// choices reachable here.
/// </summary>
/// <remarks>
/// <para>
/// An IDCT is specified only to a tolerance, so two conforming implementations disagree by a
/// count here and there and those counts accumulate down a chain of predicted pictures. FFmpeg
/// therefore does not pick one and keep it: a stream that says it was written by Xvid is
/// decoded with Xvid's, and matching ffmpeg on such a stream means doing the same.
/// </para>
/// <para>
/// DIVERGENCE, recorded: the C switches by overwriting the <c>idct_put</c> and <c>idct_add</c>
/// function pointers in <c>IDCTDSPContext</c>. That table was dropped when MPEG-2 was ported
/// (see <c>IDCTDSPContext</c> in the checklist) and the kernels became direct static calls, so
/// the choice is a field read at the call site instead. Unknown values throw.
/// </para>
/// </remarks>
public enum MpegIdct
{
    /// <summary><c>FF_IDCT_SIMPLE</c> - <c>ff_simple_idct_int16_8bit</c>, the default.</summary>
    Simple = 0,

    /// <summary><c>FF_IDCT_XVID</c> - the Walken IDCT in <c>xvididct.c</c>.</summary>
    Xvid = 1,
}

public enum OutputFormat
{
    Mpeg1 = 0,
    H261 = 1,
    H263 = 2,
    Mjpeg = 3,
    SpeedHq = 4,
}

/// <summary>
/// <c>ScanTable</c> (<c>mpegvideo.h:48</c>) - a coefficient scan order with its permuted form.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg's <c>const uint8_t *scantable</c> is a pointer into a static table, so the C# field is a
/// reference to that same table rather than a copy; <c>permutated</c> and <c>raster_end</c> are
/// per-instance and are inline arrays, which give the struct its 64 bytes of storage without the
/// <c>localloc</c> that a <c>stackalloc</c> would emit. A method containing <c>localloc</c> is
/// excluded from tiered compilation and never reaches Tier1, which cost this project dearly in
/// H.264 before it was understood.
/// </para>
/// <para>
/// <c>permutated</c> is the scan order after the IDCT's own coefficient permutation has been
/// folded in, which is why both forms are kept: the parser scatters coefficients through the
/// permuted order and the transform reads them in its own.
/// </para>
/// </remarks>
public struct ScanTable
{
    /// <summary><c>const uint8_t *scantable</c> - the unpermuted scan, shared and not copied.</summary>
    public byte[]? Scantable;

    /// <summary><c>uint8_t permutated[64]</c>.</summary>
    public Permutation64 Permutated;

    /// <summary><c>uint8_t raster_end[64]</c>.</summary>
    public Permutation64 RasterEnd;

    /// <summary>Sixty-four bytes of inline storage, as used by both scan arrays.</summary>
    [InlineArray(64)]
    public struct Permutation64
    {
        private byte element0;
    }
}

/// <summary>
/// <c>enum AVPictureType</c> (libavutil). Mirrored here because <see cref="MpegEncContext"/> has a
/// field of this type; promote it if another codec needs it.
/// </summary>
public enum AvPictureType
{
    None = 0,
    I = 1,
    P = 2,
    B = 3,
    S = 4,
    Si = 5,
    Sp = 6,
    Bi = 7,
}

/// <summary>
/// The anonymous enum typing <c>MpegEncContext.msmpeg4_version</c> (<c>mpegvideo.h:235</c>), named
/// so C# can refer to it.
/// </summary>
public enum MsMpeg4Version
{
    Unused = 0,
    V1 = 1,
    V2 = 2,
    V3 = 3,
    Wmv1 = 4,
    Wmv2 = 5,
    /// <summary>VC-1 (image), WMV3 (image) and MSS2.</summary>
    Vc1 = 6,
}

/// <summary>
/// The two <c>AV_CODEC_ID</c> values this decoder ever carries, with FFmpeg's numbering so the
/// comparisons read the same as the C.
/// </summary>
public static class MpegCodecId
{
    /// <summary><c>AV_CODEC_ID_MPEG1VIDEO</c>.</summary>
    public const int Mpeg1Video = 1;

    /// <summary><c>AV_CODEC_ID_MPEG2VIDEO</c>.</summary>
    public const int Mpeg2Video = 2;

    /// <summary><c>AV_CODEC_ID_H263</c>.</summary>
    public const int H263 = 4;

    /// <summary><c>AV_CODEC_ID_MPEG4</c>.</summary>
    public const int Mpeg4 = 12;

    /// <summary><c>AV_CODEC_ID_VC1</c>.</summary>
    public const int Vc1 = 70;

    /// <summary><c>AV_CODEC_ID_WMV3</c>.</summary>
    public const int Wmv3 = 71;
}

/// <summary>
/// Constants <c>#define</c>d inside the body of <c>MpegEncContext</c>. They read as commentary
/// when the struct is skimmed as a field list, which is how they were missed on a first pass.
/// </summary>
public static class MpegVideoConstants
{
    /// <summary><c>MAX_THREADS</c>.</summary>
    public const int MaxThreads = 32;

    /// <summary><c>MV_DIR_FORWARD</c>.</summary>
    public const int MvDirForward = 1;

    /// <summary><c>MV_DIR_BACKWARD</c>.</summary>
    public const int MvDirBackward = 2;

    /// <summary><c>MV_DIRECT</c> - bidirectional mode where the difference equals the last
    /// P/S/I picture's motion vector (MPEG-4).</summary>
    public const int MvDirect = 4;

    /// <summary><c>MV_TYPE_16X16</c> - one vector for the whole macroblock.</summary>
    public const int MvType16X16 = 0;

    /// <summary><c>MV_TYPE_8X8</c> - four vectors (H.263, MPEG-4 4MV).</summary>
    public const int MvType8X8 = 1;

    /// <summary><c>MV_TYPE_16X8</c> - two vectors, one per 16x8 block.</summary>
    public const int MvType16X8 = 2;

    /// <summary><c>MV_TYPE_FIELD</c> - two vectors, one per field.</summary>
    public const int MvTypeField = 3;

    /// <summary><c>MV_TYPE_DMV</c> - two vectors, MPEG-2's dual prime vectors.</summary>
    public const int MvTypeDmv = 4;

    /// <summary><c>CHROMA_420</c>.</summary>
    public const int Chroma420 = 1;

    /// <summary><c>CHROMA_422</c>.</summary>
    public const int Chroma422 = 2;

    /// <summary><c>CHROMA_444</c>.</summary>
    public const int Chroma444 = 3;

    /// <summary><c>PICT_TOP_FIELD</c> (<c>mpegutils.h</c>).</summary>
    public const int PictTopField = 1;

    /// <summary><c>PICT_BOTTOM_FIELD</c>.</summary>
    public const int PictBottomField = 2;

    /// <summary>
    /// <c>PICT_FRAME</c> - both fields. The value is the bitwise or of the two field codes, which
    /// is why <c>picture_structure == field_select + 1</c> distinguishes parities.
    /// </summary>
    public const int PictFrame = 3;
}

/// <summary>
/// <c>MpegEncContext</c> (<c>mpegvideo.h:67</c>) - the shared MPEG-video context.
/// </summary>
/// <remarks>
/// <para>
/// Mirrored in full, in FFmpeg's declared order, including the fields only MPEG-4, H.263 or
/// MSMPEG-4 read. That was a deliberate choice on 2026-10-16: the alternative was to port only
/// what MPEG-2 touches, which is smaller but means growing the struct back field by field for
/// every later codec, and means the port no longer matches the C it came from. Field coverage is
/// enforced by <c>MpegStructMirrorTests</c>, so a field cannot go missing quietly.
/// </para>
/// <para>
/// Three groups of fields are deliberately absent and recorded in that test: the seven DSP
/// function-pointer tables and the two <c>dct_unquantize</c> pointers, which became direct static
/// calls; libavutil plumbing with no counterpart here (<c>class</c>, <c>avctx</c>,
/// <c>picture_pool</c>, the thread-context union); and <c>er</c>, because error resilience only
/// matters for damaged streams and follows a clean decode.
/// </para>
/// <para>
/// FFmpeg's <c>*_base</c> / bare pointer pairs become an array plus an offset for the same reason
/// as in <see cref="MPVPicture"/>: the bare pointer is an interior pointer, positioned so that
/// indexing one macroblock row above the first stays inside the allocation.
/// </para>
/// </remarks>
public struct MpegEncContext
{
    /// <summary><c>y_dc_scale</c>.</summary>
    public int YDcScale;

    /// <summary><c>c_dc_scale</c>.</summary>
    public int CDcScale;

    /// <summary><c>ac_pred</c> - MPEG-4 / MSMPEG-4 AC prediction.</summary>
    public int AcPred;

    /// <summary><c>block_last_index[12]</c> - index of the last non-zero coefficient per block.</summary>
    public Int12 BlockLastIndex;

    /// <summary><c>h263_aic</c> - H.263 Advanced Intra Coding.</summary>
    public int H263Aic;

    /// <summary><c>inter_scantable</c>.</summary>
    public ScanTable InterScantable;

    /// <summary><c>intra_scantable</c>.</summary>
    public ScanTable IntraScantable;

    /// <summary><c>width</c> - must be a multiple of 16.</summary>
    public int Width;

    /// <summary><c>height</c> - must be a multiple of 16.</summary>
    public int Height;

    /// <summary><c>out_format</c>.</summary>
    public OutputFormat OutFormat;

    /// <summary><c>h263_pred</c> - use MPEG-4/H.263 AC/DC prediction.</summary>
    public int H263Pred;

    /// <summary>
    /// <c>codec_id</c>. Only the two values in <see cref="MpegCodecId"/> occur here, and only
    /// error resilience reads it - one of its repairs applies to MPEG-2 alone.
    /// </summary>
    public int CodecId;

    /// <summary><c>encoding</c> - non-zero when encoding. Always zero here.</summary>
    public int Encoding;

    /// <summary><c>workaround_bugs</c> - flags for encoder bugs that cannot be detected.</summary>
    public int WorkaroundBugs;

    /// <summary><c>codec_tag</c>.</summary>
    public int CodecTag;

    /// <summary><c>context_initialized</c>.</summary>
    public int ContextInitialized;

    /// <summary><c>mb_width</c>.</summary>
    public int MbWidth;

    /// <summary><c>mb_height</c>.</summary>
    public int MbHeight;

    /// <summary>
    /// <c>mb_stride</c> - <c>mb_width + 1</c>, so that the left and top neighbours of the first
    /// macroblock in a row are addressable without a special case.
    /// </summary>
    public int MbStride;

    /// <summary><c>b8_stride</c> - <c>2 * mb_width + 1</c>, the same trick for 8x8 block arrays.</summary>
    public int B8Stride;

    /// <summary><c>h_edge_pos</c> - horizontal position of the right edge, for pixel replication.</summary>
    public int HEdgePos;

    /// <summary><c>v_edge_pos</c> - vertical position of the bottom edge.</summary>
    public int VEdgePos;

    /// <summary><c>mb_num</c>.</summary>
    public int MbNum;

    /// <summary><c>linesize</c> - may differ from width.</summary>
    public nint Linesize;

    /// <summary><c>uvlinesize</c> - chroma line size, may differ from width.</summary>
    public nint Uvlinesize;

    /// <summary><c>buffer_pools</c>.</summary>
    public BufferPoolContext BufferPools;

    /// <summary><c>start_mb_y</c> - first macroblock row this thread owns.</summary>
    public int StartMbY;

    /// <summary><c>end_mb_y</c> - one past the last macroblock row this thread owns.</summary>
    public int EndMbY;

    /// <summary><c>slice_context_count</c>.</summary>
    public int SliceContextCount;

    /// <summary><c>last_pic</c> - a view of the previous picture.</summary>
    public MPVWorkPicture LastPic;

    /// <summary><c>next_pic</c> - a view of the next picture.</summary>
    public MPVWorkPicture NextPic;

    /// <summary><c>cur_pic</c> - a view of the current picture.</summary>
    public MPVWorkPicture CurPic;

    /// <summary><c>dc_val_base</c> - the allocation behind <see cref="DcValOffset"/>.</summary>
    public short[]? DcValBase;

    /// <summary><c>y_dc_scale_table</c> - qscale to luma DC scale.</summary>
    public byte[]? YDcScaleTable;

    /// <summary><c>c_dc_scale_table</c> - qscale to chroma DC scale.</summary>
    public byte[]? CDcScaleTable;

    /// <summary><c>chroma_qscale_table</c> - qscale to chroma qscale (H.263).</summary>
    public byte[]? ChromaQscaleTable;

    /// <summary><c>coded_block_base</c> - the allocation behind <see cref="CodedBlockOffset"/>.</summary>
    public byte[]? CodedBlockBase;

    /// <summary><c>coded_block</c> - coded block pattern prediction (MSMPEG-4 v3, WMV1).</summary>
    public int CodedBlockOffset;

    /// <summary><c>ac_val_base</c> - the allocation behind <see cref="AcValOffset"/>, 16 shorts per entry.</summary>
    public short[]? AcValBase;

    /// <summary><c>dc_val</c> - H.263 AIC / MPEG-4 DC prediction, as an offset into <see cref="DcValBase"/>.</summary>
    public int DcValOffset;

    /// <summary>
    /// <c>ac_val</c> - H.263 AIC / MPEG-4 AC prediction, as an offset into
    /// <see cref="AcValBase"/>. The C's pointer is <c>int16_t (*)[16]</c> and steps whole
    /// entries; this offset is in <b>shorts</b>, so whatever sets it must multiply the entry
    /// index by 16, and a reader indexes <c>AcValOffset + entry * 16 + k</c>.
    /// </summary>
    public int AcValOffset;

    /// <summary><c>mb_skipped</c> - set only while decoding.</summary>
    public int MbSkipped;

    /// <summary><c>mbskip_table</c>.</summary>
    public byte[]? MbskipTable;

    /// <summary><c>mbintra_table</c>.</summary>
    public byte[]? MbintraTable;

    /// <summary><c>cbp_table</c>.</summary>
    public byte[]? CbpTable;

    /// <summary><c>pred_dir_table</c>.</summary>
    public byte[]? PredDirTable;

    /// <summary><c>sc</c>.</summary>
    public ScratchpadContext Sc;

    /// <summary><c>qscale</c>.</summary>
    public int Qscale;

    /// <summary><c>chroma_qscale</c>.</summary>
    public int ChromaQscale;

    /// <summary>
    /// Which unquantiser reconstruction runs, standing in for the C's
    /// <c>dct_unquantize_intra</c> and <c>dct_unquantize_inter</c> function pointers.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE, recorded: two function pointers become one enum, because the pair is always
    /// set together to the intra and inter halves of one family and a codec never mixes them.
    /// <see cref="UnquantizeKind.None"/> is FFmpeg's <c>DEFINITELY_MPEG12_H261</c> arm of
    /// <c>mpv_reconstruct_mb_internal</c>, which skips the unquantisers because the MPEG-1/2
    /// block readers already dequantised.
    /// </remarks>
    public UnquantizeKind DctUnquantize;

    /// <summary>
    /// <c>avctx-&gt;idct_algo</c>, as far as it is reachable here: which inverse transform the
    /// reconstruction calls. See <see cref="MpegIdct"/> for why this is a choice at all.
    /// </summary>
    public MpegIdct IdctAlgo;

    /// <summary><c>pict_type</c>.</summary>
    public AvPictureType PictType;

    /// <summary>
    /// The presentation timestamp of the access unit currently being decoded, which
    /// <c>MpvFrameStart</c> stamps onto the picture it allocates.
    /// </summary>
    public long CurrentPts;

    /// <summary><c>droppable</c>.</summary>
    public int Droppable;

    /// <summary><c>p_field_mv_table_base</c> - the allocation behind the field MV tables.</summary>
    public short[]? PFieldMvTableBase;

    /// <summary>
    /// <c>p_field_mv_table[2][2]</c> - as offsets into <see cref="PFieldMvTableBase"/>. Encoding
    /// only, kept because the mirror is in full.
    /// </summary>
    public Int4 PFieldMvTableOffset;

    /// <summary><c>mv_dir</c> - see MvDir* on <see cref="MpegVideoConstants"/>.</summary>
    public int MvDir;

    /// <summary><c>mv_type</c> - see MvType* on <see cref="MpegVideoConstants"/>.</summary>
    public int MvType;

    /// <summary>
    /// <c>mv[2][4][2]</c> - motion vectors for a macroblock. First index 0 forward, 1 backward;
    /// second depends on <see cref="MvType"/>; third 0 is x and 1 is y.
    /// </summary>
    public Int16Flat Mv;

    /// <summary><c>field_select[2][2]</c>.</summary>
    public Int4 FieldSelect;

    /// <summary><c>last_mv[2][2][2]</c> - used for MV prediction in MPEG-1 and MPEG-4 B-pictures.</summary>
    public Int8 LastMv;

    /// <summary><c>direct_scale_mv[2][64]</c> - precomputed to avoid division in MPEG-4 direct mode.</summary>
    public Short128 DirectScaleMv;

    /// <summary><c>no_rounding</c> - apply no rounding to motion compensation (MPEG-4, MSMPEG-4).</summary>
    public int NoRounding;

    /// <summary><c>mb_x</c>.</summary>
    public int MbX;

    /// <summary><c>mb_y</c>.</summary>
    public int MbY;

    /// <summary><c>mb_intra</c>.</summary>
    public int MbIntra;

    /// <summary><c>block_index[6]</c> - index of the current macroblock in the block arrays.</summary>
    public Int6 BlockIndex;

    /// <summary><c>block_wrap[6]</c>.</summary>
    public Int6 BlockWrap;

    /// <summary><c>dest[3]</c> - as offsets into the current picture's allocation.</summary>
    public MPVPicture.PlaneOffsets Dest;

    /// <summary><c>mb_index2xy</c> - macroblock index to <c>mb_x + mb_y * mb_stride</c>.</summary>
    public int[]? MbIndex2Xy;

    /// <summary><c>intra_matrix[64]</c> - the quantiser matrix carried in the bitstream.</summary>
    public Ushort64 IntraMatrix;

    /// <summary><c>chroma_intra_matrix[64]</c>.</summary>
    public Ushort64 ChromaIntraMatrix;

    /// <summary><c>inter_matrix[64]</c>.</summary>
    public Ushort64 InterMatrix;

    /// <summary><c>chroma_inter_matrix[64]</c>.</summary>
    public Ushort64 ChromaInterMatrix;

    /// <summary><c>resync_mb_x</c> - x position of the last resync marker.</summary>
    public int ResyncMbX;

    /// <summary><c>resync_mb_y</c> - y position of the last resync marker.</summary>
    public int ResyncMbY;

    /// <summary><c>mtype</c> - H.261.</summary>
    public int Mtype;

    /// <summary><c>obmc</c> - H.263 overlapped block motion compensation.</summary>
    public int Obmc;

    /// <summary><c>h263_aic_dir</c> - AIC direction: 0 left, 1 top.</summary>
    public int H263AicDir;

    /// <summary><c>studio_profile</c> - MPEG-4.</summary>
    public int StudioProfile;

    /// <summary><c>last_time_base</c>.</summary>
    public int LastTimeBase;

    /// <summary><c>time_base</c> - time in seconds of the last I, P or S picture.</summary>
    public int TimeBase;

    /// <summary><c>time</c> - time of the current picture.</summary>
    public long Time;

    /// <summary><c>last_non_b_time</c>.</summary>
    public long LastNonBTime;

    /// <summary><c>pp_time</c> - distance between the last two P/S/I pictures.</summary>
    public ushort PpTime;

    /// <summary><c>pb_time</c> - distance between the last B and P/S/I picture.</summary>
    public ushort PbTime;

    /// <summary><c>pp_field_time</c>.</summary>
    public ushort PpFieldTime;

    /// <summary><c>pb_field_time</c>.</summary>
    public ushort PbFieldTime;

    /// <summary><c>mcsel</c>.</summary>
    public int Mcsel;

    /// <summary><c>quarter_sample</c> - 1 for quarter-pel, 0 for half-pel. MPEG-2 is half-pel.</summary>
    public int QuarterSample;

    /// <summary><c>low_delay</c> - no reordering needed, stream has no B-pictures.</summary>
    public int LowDelay;

    /// <summary><c>first_slice_line</c>.</summary>
    public int FirstSliceLine;

    /// <summary><c>msmpeg4_version</c>.</summary>
    public MsMpeg4Version Msmpeg4Version;

    /// <summary><c>inter_intra_pred</c>.</summary>
    public int InterIntraPred;

    /// <summary><c>mspel</c>.</summary>
    public int Mspel;

    /// <summary><c>progressive_sequence</c>.</summary>
    public int ProgressiveSequence;

    /// <summary><c>mpeg_f_code[2][2]</c>.</summary>
    public Int4 MpegFCode;

    /// <summary><c>picture_structure</c> - values from <c>mpegutils.h</c>.</summary>
    public int PictureStructure;

    /// <summary><c>intra_dc_precision</c> - indexes <c>Mpeg12DcScaleTable</c>.</summary>
    public int IntraDcPrecision;

    /// <summary><c>frame_pred_frame_dct</c>.</summary>
    public int FramePredFrameDct;

    /// <summary><c>top_field_first</c>.</summary>
    public int TopFieldFirst;

    /// <summary><c>concealment_motion_vectors</c>.</summary>
    public int ConcealmentMotionVectors;

    /// <summary><c>q_scale_type</c> - selects the non-linear quantiser scale.</summary>
    public int QScaleType;

    /// <summary><c>intra_vlc_format</c>.</summary>
    public int IntraVlcFormat;

    /// <summary><c>alternate_scan</c> - selects the alternate coefficient scan.</summary>
    public int AlternateScan;

    /// <summary><c>repeat_first_field</c>.</summary>
    public int RepeatFirstField;

    /// <summary><c>chroma_420_type</c>.</summary>
    public int Chroma420Type;

    /// <summary><c>chroma_format</c> - see Chroma4xx on <see cref="MpegVideoConstants"/>.</summary>
    public int ChromaFormat;

    /// <summary><c>chroma_x_shift</c> - derived from the pixel format.</summary>
    public int ChromaXShift;

    /// <summary><c>chroma_y_shift</c>.</summary>
    public int ChromaYShift;

    /// <summary><c>progressive_frame</c>.</summary>
    public int ProgressiveFrame;

    /// <summary><c>full_pel[2]</c> - MPEG-1 full-pel motion vectors.</summary>
    public Int2 FullPel;

    /// <summary><c>interlaced_dct</c>.</summary>
    public int InterlacedDct;

    /// <summary><c>first_field</c> - 1 for the first field of a field picture.</summary>
    public int FirstField;

    /// <summary><c>context_reinit</c> - set when a size change needs reinitialisation.</summary>
    public int ContextReinit;

    /// <summary><c>slice_ctx_size</c>.</summary>
    public uint SliceCtxSize;

    /// <summary><c>er</c> - error resilience state, the last field of the C structure too.</summary>
    public ErContext Er;

    // ----- inline storage for the fixed-size arrays above -----

    /// <summary>Two ints.</summary>
    [InlineArray(2)] public struct Int2 { private int element0; }

    /// <summary>Four ints - a <c>[2][2]</c> array, row-major.</summary>
    [InlineArray(4)] public struct Int4 { private int element0; }

    /// <summary>Six ints.</summary>
    [InlineArray(6)] public struct Int6 { private int element0; }

    /// <summary>Eight ints - a <c>[2][2][2]</c> array, row-major.</summary>
    [InlineArray(8)] public struct Int8 { private int element0; }

    /// <summary>Twelve ints.</summary>
    [InlineArray(12)] public struct Int12 { private int element0; }

    /// <summary>Sixteen ints - <c>mv[2][4][2]</c>, row-major.</summary>
    [InlineArray(16)] public struct Int16Flat { private int element0; }

    /// <summary>Sixty-four ushorts - a quantiser matrix.</summary>
    [InlineArray(64)] public struct Ushort64 { private ushort element0; }

    /// <summary>A hundred and twenty-eight shorts - <c>direct_scale_mv[2][64]</c>, row-major.</summary>
    [InlineArray(128)] public struct Short128 { private short element0; }
}

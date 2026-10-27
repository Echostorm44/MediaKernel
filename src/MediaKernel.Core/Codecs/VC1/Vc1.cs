// VC-1's decoder context and its header parsing - a translation of ffmpeg-master/libavcodec's
// vc1.h (the VC1Context struct) and vc1.c.
//
// The struct comes first and whole, in FFmpeg's declared order, because in this codec family the
// context IS the algorithm: the bitplanes, the intensity-compensation lookup tables and the
// per-block caches are read by name from a dozen files, and a context grown field by field as
// each one is ported stops matching the C it came from. Field coverage is checked by
// Vc1StructMirrorTests, so a field cannot go missing quietly.
//
// vc1.c is translated in full - the bitplane decoder and the syntax elements beside it, the
// sequence and entry point headers, the intensity-compensation lookup tables and both picture
// header parsers. See the port checklist's vc1 section for the rest of the codec.

using System;
using MediaKernel.Core.Codecs.Mpeg2;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Vc1Video;

/// <summary><c>enum FrameCodingMode</c> (vc1.h) - how an Advanced Profile picture is coded.</summary>
public enum FrameCodingMode
{
    /// <summary><c>PROGRESSIVE</c> - progressive frame.</summary>
    Progressive = 0,

    /// <summary><c>ILACE_FRAME</c> - interlaced frame.</summary>
    IlaceFrame = 1,

    /// <summary><c>ILACE_FIELD</c> - interlaced field.</summary>
    IlaceField = 2,
}

/// <summary><c>enum TransformTypes</c> (vc1.h).</summary>
public enum TransformType
{
    /// <summary><c>TT_8X8</c>.</summary>
    Tt8X8 = 0,

    /// <summary><c>TT_8X4_BOTTOM</c>.</summary>
    Tt8X4Bottom = 1,

    /// <summary><c>TT_8X4_TOP</c>.</summary>
    Tt8X4Top = 2,

    /// <summary><c>TT_8X4</c> - both halves.</summary>
    Tt8X4 = 3,

    /// <summary><c>TT_4X8_RIGHT</c>.</summary>
    Tt4X8Right = 4,

    /// <summary><c>TT_4X8_LEFT</c>.</summary>
    Tt4X8Left = 5,

    /// <summary><c>TT_4X8</c> - both halves.</summary>
    Tt4X8 = 6,

    /// <summary><c>TT_4X4</c>.</summary>
    Tt4X4 = 7,
}

/// <summary>The <c>AVDISCARD_*</c> values this decoder compares against.</summary>
public static class AvDiscard
{
    /// <summary><c>AVDISCARD_NONE</c> - discard nothing.</summary>
    public const int None = -16;

    /// <summary><c>AVDISCARD_DEFAULT</c> - discard useless packets, e.g. 0-sized ones.</summary>
    public const int Default = 0;

    /// <summary><c>AVDISCARD_NONREF</c>.</summary>
    public const int Nonref = 8;

    /// <summary><c>AVDISCARD_BIDIR</c>.</summary>
    public const int Bidir = 16;

    /// <summary><c>AVDISCARD_NONINTRA</c>.</summary>
    public const int Nonintra = 24;

    /// <summary><c>AVDISCARD_NONKEY</c>.</summary>
    public const int Nonkey = 32;

    /// <summary><c>AVDISCARD_ALL</c> - discard all.</summary>
    public const int All = 48;
}

/// <summary>The <c>AV_CODEC_FLAG_*</c> bits this decoder reads out of <c>flags</c>.</summary>
public static class AvCodecFlag
{
    /// <summary><c>AV_CODEC_FLAG_LOW_DELAY</c> - the caller wants no reorder delay.</summary>
    public const int LowDelay = 1 << 19;
}

/// <summary>
/// The <c>AV_CODEC_ID_*</c> values <c>vc1dec.c</c> registers four decoders for.
/// </summary>
/// <remarks>
/// <c>vc1_decode_init</c> branches on it because the two extradata layouts cannot be told
/// apart from the bytes alone: WMV3 stores a bare sequence header, while VC-1 stores
/// marker-delimited units and carries an entry point as well. The two sprite ids are named
/// because the code that reads this field tests for them, and a test against an unnamed value
/// would hide what it is about; the sprite paths themselves are out of scope.
/// </remarks>
public enum Vc1CodecId
{
    /// <summary><c>AV_CODEC_ID_VC1</c> - also WVC1, WMVA and WVP2.</summary>
    Vc1 = 0,

    /// <summary><c>AV_CODEC_ID_WMV3</c> - also WMVP, whose sprites it hands back raw.</summary>
    Wmv3 = 1,

    /// <summary><c>AV_CODEC_ID_WMV3IMAGE</c> - the WMVP sprite compositor, out of scope.</summary>
    Wmv3Image = 2,

    /// <summary><c>AV_CODEC_ID_VC1IMAGE</c> - the WVP2 sprite compositor, out of scope.</summary>
    Vc1Image = 3,
}

/// <summary>
/// The <c>AVCodecContext</c> fields VC-1's headers read and write, as
/// <see cref="Mpeg4Video.H263CodecContext"/> does for MPEG-4.
/// </summary>
/// <remarks>
/// There is no AVCodecContext in this port, and a decoder that needs a couple of dozen of its
/// three hundred fields should not pull in a stand-in for all of them. The two <c>utils.c</c>
/// functions the headers call through - <c>ff_set_dimensions</c> and <c>ff_set_sar</c>, which
/// exist precisely to validate before writing those fields - are methods here for the same
/// reason, and are recorded in the registry as such.
/// </remarks>
public struct Vc1CodecContext
{
    /// <summary><c>width</c> - the dimensions after <c>lowres</c> scaling.</summary>
    public int Width;

    /// <summary><c>height</c>.</summary>
    public int Height;

    /// <summary><c>coded_width</c> - the dimensions as the bitstream codes them.</summary>
    public int CodedWidth;

    /// <summary><c>coded_height</c>.</summary>
    public int CodedHeight;

    /// <summary><c>lowres</c> - always 0 here; this port has no lowres decoding.</summary>
    public int Lowres;

    /// <summary>
    /// <c>codec_id</c> - which of <c>vc1dec.c</c>'s four decoders this context belongs to.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="Vc1CodecId.Vc1"/>, so a caller that forgets to set it gets the
    /// marker-delimited extradata path and a loud failure rather than a WMV3 sequence header
    /// read as if it were escaped units.
    /// </remarks>
    public Vc1CodecId CodecId;

    /// <summary>
    /// <c>extradata</c> - the out-of-band sequence header, whose layout
    /// <see cref="CodecId"/> selects. There is no separate size: the array is its own length.
    /// </summary>
    public byte[]? Extradata;

    /// <summary><c>profile</c> - a <see cref="Vc1Profile"/> value, from the sequence header.</summary>
    public int Profile;

    /// <summary><c>level</c> - Advanced Profile only; left alone for Simple and Main.</summary>
    public int Level;

    /// <summary><c>has_b_frames</c> - the reorder delay, which for VC-1 is one picture or none.</summary>
    public int HasBFrames;

    /// <summary><c>chroma_sample_location</c>; AVCHROMA_LOC_LEFT for every VC-1 stream.</summary>
    public int ChromaSampleLocation;

    /// <summary><c>color_primaries</c>.</summary>
    public int ColorPrimaries;

    /// <summary><c>color_trc</c>.</summary>
    public int ColorTrc;

    /// <summary><c>colorspace</c>.</summary>
    public int Colorspace;

    /// <summary><c>flags</c>, of which only AV_CODEC_FLAG_LOW_DELAY is read here.</summary>
    public int Flags;

    /// <summary><c>skip_frame</c> - an <see cref="AvDiscard"/> value.</summary>
    public int SkipFrame;

    /// <summary><c>frame_num</c>.</summary>
    public long FrameNum;

    /// <summary><c>max_pixels</c> - <see cref="long.MaxValue"/> unless a caller lowers it.</summary>
    public long MaxPixels;

    /// <summary><c>sample_aspect_ratio</c>.</summary>
    public AvRational SampleAspectRatio;

    /// <summary><c>framerate</c>.</summary>
    public AvRational Framerate;

    /// <summary><c>max_b_frames</c>.</summary>
    public int MaxBFrames;

    /// <summary><c>skip_loop_filter</c> - an <see cref="AvDiscard"/> value.</summary>
    public int SkipLoopFilter;

    /// <summary>A context with the defaults an unconfigured AVCodecContext has.</summary>
    public static Vc1CodecContext Default() => new()
    {
        MaxPixels = long.MaxValue,
        SampleAspectRatio = new AvRational(0, 1),
        Framerate = new AvRational(0, 1),
        SkipLoopFilter = AvDiscard.Default,
        SkipFrame = AvDiscard.Default,
    };

    /// <summary>
    /// <c>ff_set_dimensions</c> (utils.c) - validate a coded size and store it, zeroing both
    /// sizes when it is not usable.
    /// </summary>
    /// <returns>0, or <see cref="Vc1.AverrorEinval"/> for a size that cannot be decoded.</returns>
    public int SetDimensions(int width, int height)
    {
        int ret = AvImageCheckSize2((uint)width, (uint)height, MaxPixels);

        if (ret < 0)
            width = height = 0;

        CodedWidth = width;
        CodedHeight = height;
        Width = -((-width) >> Lowres);    // AV_CEIL_RSHIFT
        Height = -((-height) >> Lowres);

        return ret;
    }

    /// <summary><c>ff_set_sar</c> (utils.c) - store an aspect ratio, or 0:1 if it is invalid.</summary>
    public int SetSar(AvRational sar)
    {
        int ret = AvImageCheckSar((uint)Width, (uint)Height, sar);

        if (ret < 0)
        {
            SampleAspectRatio = new AvRational(0, 1);
            return ret;
        }

        SampleAspectRatio = sar;
        return 0;
    }

    /// <summary>
    /// <c>av_image_check_size2</c> (imgutils.c), for AV_PIX_FMT_NONE - the only pixel format
    /// these two call sites pass, for which <c>av_image_get_linesize</c> returns a value that is
    /// not positive and the 8-bits-per-pixel fallback below is taken.
    /// </summary>
    public static int AvImageCheckSize2(uint w, uint h, long maxPixels)
    {
        long stride = 8L * w;
        stride += 128 * 8;

        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue ||
            stride >= int.MaxValue || stride * (h + 128L) >= int.MaxValue)
        {
            return Vc1.AverrorEinval;
        }

        if (maxPixels < long.MaxValue && w * (long)h > maxPixels)
            return Vc1.AverrorEinval;

        return 0;
    }

    /// <summary><c>av_image_check_sar</c> (imgutils.c).</summary>
    public static int AvImageCheckSar(uint w, uint h, AvRational sar)
    {
        if (sar.Den <= 0 || sar.Num < 0)
            return Vc1.AverrorEinval;

        if (sar.Num == 0 || sar.Num == sar.Den)
            return 0;

        // av_rescale_rnd with AV_ROUND_ZERO, which truncates; every value here is non-negative.
        long scaledDim = sar.Num < sar.Den
            ? (long)w * sar.Num / sar.Den
            : (long)h * sar.Den / sar.Num;

        return scaledDim > 0 ? 0 : Vc1.AverrorEinval;
    }
}

/// <summary>
/// <c>VC1Context</c> (vc1.h) - everything one VC-1 or WMV3 decoder holds, wrapping the shared
/// <see cref="MpegEncContext"/> as <c>v-&gt;s</c>.
/// </summary>
/// <remarks>
/// <para>
/// A class rather than a struct, for the same reason <c>H263DecContext</c> is: it carries an
/// <see cref="MpegEncContext"/> and six kilobytes of lookup tables, and there is exactly one per
/// decoder. Fields are in the order the C declares them.
/// </para>
/// <para>
/// FFmpeg's <c>*_base</c> / bare pointer pairs are an array plus an offset here, as they are in
/// <see cref="MpegEncContext"/>: the bare pointer is an interior pointer positioned so that
/// indexing one macroblock to the left of the first stays inside the allocation.
/// </para>
/// <para>
/// Five members of the C struct are deliberately absent, and each is recorded in the function
/// registry and asserted by <c>Vc1StructMirrorTests</c>:
/// </para>
/// <list type="bullet">
/// <item><c>h264chroma</c> and <c>vc1dsp</c> are function-pointer tables, which this port
/// replaced with direct static calls on 2026-10-16 - the same decision <see cref="MpegEncContext"/>
/// records for its seven DSP tables.</item>
/// <item><c>x8</c> is the IntraX8 decoder, used only by WMV3's Complex Profile and WMV2, neither
/// of which is in scope.</item>
/// <item><c>sprite_output_frame</c> and <c>sr_rows</c> belong to the WMVP/WVP2 sprite modes,
/// which are out of scope and whose every entry point throws.</item>
/// </list>
/// </remarks>
public class Vc1Context
{
    /// <summary><c>s</c> - the shared MPEG-video context.</summary>
    public MpegEncContext S;

    /// <summary><c>gb</c>.</summary>
    public GetBitContext Gb;

    /// <summary>
    /// <c>s.avctx</c> - the AVCodecContext fields the headers touch. Not a field of the C
    /// struct, which reaches them through <c>v-&gt;s.avctx</c>; <see cref="MpegEncContext"/>
    /// has no such pointer in this port, so it lives here.
    /// </summary>
    public Vc1CodecContext Avctx = Vc1CodecContext.Default();

    // --- Simple/Main Profile sequence header ------------------------------------------------

    /// <summary><c>res_sprite</c> - reserved, sprite mode.</summary>
    public int ResSprite;

    /// <summary><c>res_y411</c> - reserved, old interlaced mode.</summary>
    public int ResY411;

    /// <summary><c>res_x8</c> - reserved.</summary>
    public int ResX8;

    /// <summary><c>multires</c> - frame-level RESPIC syntax element present.</summary>
    public int Multires;

    /// <summary><c>res_fasttx</c> - reserved, always 1.</summary>
    public int ResFasttx;

    /// <summary><c>res_transtab</c> - reserved, always 0.</summary>
    public int ResTranstab;

    /// <summary><c>rangered</c> - RANGEREDFRM present at frame level.</summary>
    public int Rangered;

    /// <summary><c>res_rtm_flag</c> - reserved, set to 1.</summary>
    public int ResRtmFlag;

    /// <summary><c>reserved</c>.</summary>
    public int Reserved;

    // --- Advanced Profile -------------------------------------------------------------------

    /// <summary><c>level</c> - 3 bits, provided by the TS layer for Advanced/Simple.</summary>
    public int Level;

    /// <summary><c>chromaformat</c> - 2 bits; only 2 (4:2:0) is defined.</summary>
    public int Chromaformat;

    /// <summary><c>postprocflag</c> - per-frame processing suggestion flag present.</summary>
    public int Postprocflag;

    /// <summary><c>broadcast</c> - TFF/RFF present.</summary>
    public int Broadcast;

    /// <summary><c>interlace</c> - progressive/interlaced (RPTFTM syntax element).</summary>
    public int Interlace;

    /// <summary><c>tfcntrflag</c> - TFCNTR present.</summary>
    public int Tfcntrflag;

    /// <summary><c>panscanflag</c> - NUMPANSCANWIN and the window corners present.</summary>
    public int Panscanflag;

    /// <summary><c>refdist_flag</c> - REFDIST present in II/IP/PI/PP field picture headers.</summary>
    public int RefdistFlag;

    /// <summary><c>extended_dmv</c> - additional extended dmv range at P/B frame level.</summary>
    public int ExtendedDmv;

    /// <summary><c>color_prim</c> - 8 bits, chroma coordinates of the colour primaries.</summary>
    public int ColorPrim;

    /// <summary><c>transfer_char</c> - 8 bits, opto-electronic transfer characteristics.</summary>
    public int TransferChar;

    /// <summary><c>matrix_coef</c> - 8 bits, primaries to YCbCr transform matrix.</summary>
    public int MatrixCoef;

    /// <summary><c>hrd_param_flag</c> - Hypothetical Reference Decoder parameters present.</summary>
    public int HrdParamFlag;

    /// <summary><c>psf</c> - Progressive Segmented Frame.</summary>
    public int Psf;

    // --- Sequence header data for all profiles ----------------------------------------------

    /// <summary><c>profile</c> - 2 bits; see <see cref="Vc1Profile"/>.</summary>
    public int Profile;

    /// <summary><c>frmrtq_postproc</c> - 3 bits.</summary>
    public int FrmrtqPostproc;

    /// <summary><c>bitrtq_postproc</c> - 5 bits, quantised framerate-based postprocessing strength.</summary>
    public int BitrtqPostproc;

    /// <summary><c>loop_filter</c>.</summary>
    public int LoopFilter;

    /// <summary><c>max_coded_width</c>.</summary>
    public int MaxCodedWidth;

    /// <summary><c>max_coded_height</c>.</summary>
    public int MaxCodedHeight;

    /// <summary><c>fastuvmc</c> - rounding of qpel vectors to hpel; not in Simple.</summary>
    public int Fastuvmc;

    /// <summary><c>extended_mv</c> - extended MV in P/B; not in Simple.</summary>
    public int ExtendedMv;

    /// <summary><c>dquant</c> - 2 bits, how qscale varies with macroblocks; not in Simple.</summary>
    public int Dquant;

    /// <summary><c>vstransform</c> - variable-size transform type and info.</summary>
    public int Vstransform;

    /// <summary><c>overlap</c> - overlapped transforms in use.</summary>
    public int Overlap;

    /// <summary><c>max_b_frames</c>.</summary>
    public int MaxBFrames;

    /// <summary><c>quantizer_mode</c> - 2 bits, see QUANT_*.</summary>
    public int QuantizerMode;

    /// <summary><c>finterpflag</c> - INTERPFRM present.</summary>
    public int Finterpflag;

    // --- Frame decoding info for all profiles -----------------------------------------------

    /// <summary><c>mv_mode</c> - MV coding mode.</summary>
    public byte MvMode;

    /// <summary><c>mv_mode2</c> - secondary MV coding mode (B frames).</summary>
    public byte MvMode2;

    /// <summary><c>k_x</c> - bits per MV, from the MV range.</summary>
    public int KX;

    /// <summary><c>k_y</c> - bits per MV, from the MV range.</summary>
    public int KY;

    /// <summary><c>range_x</c>.</summary>
    public int RangeX;

    /// <summary><c>range_y</c>.</summary>
    public int RangeY;

    /// <summary><c>pq</c> - current frame quantiser scale.</summary>
    public byte Pq;

    /// <summary><c>altpq</c> - alternate frame quantiser scale.</summary>
    public byte Altpq;

    /// <summary><c>zz_8x8[4][64]</c> - zigzag tables for TT_8X8, permuted for the IDCT; stride 64.</summary>
    public readonly byte[] Zz8x8 = new byte[4 * 64];

    /// <summary><c>left_blk_sh</c> - 3 or 0, the position of the left block in blk[].</summary>
    public int LeftBlkSh;

    /// <summary><c>top_blk_sh</c> - 3 or 0, the position of the top block in blk[].</summary>
    public int TopBlkSh;

    /// <summary><c>zz_8x4</c> - zigzag scan for the TT_8X4 coding mode.</summary>
    public byte[]? Zz8x4;

    /// <summary><c>zz_4x8</c> - zigzag scan for the TT_4X8 coding mode.</summary>
    public byte[]? Zz4x8;

    /// <summary><c>dquantfrm</c>.</summary>
    public byte Dquantfrm;

    /// <summary><c>dqprofile</c>.</summary>
    public byte Dqprofile;

    /// <summary><c>dqsbedge</c>.</summary>
    public byte Dqsbedge;

    /// <summary><c>dqbilevel</c>.</summary>
    public byte Dqbilevel;

    /// <summary><c>dc_table_index</c>.</summary>
    public int DcTableIndex;

    /// <summary><c>c_ac_table_index</c> - chroma index from the ACFRM element.</summary>
    public int CAcTableIndex;

    /// <summary><c>y_ac_table_index</c> - luma index from the AC2FRM element.</summary>
    public int YAcTableIndex;

    /// <summary><c>esc3_level_length</c>.</summary>
    public int Esc3LevelLength;

    /// <summary><c>esc3_run_length</c>.</summary>
    public int Esc3RunLength;

    /// <summary><c>ttfrm</c> - transform type info present at frame level.</summary>
    public int Ttfrm;

    /// <summary><c>ttmbf</c> - transform type flag.</summary>
    public byte Ttmbf;

    /// <summary><c>ttblk_base</c> - transform type at block level.</summary>
    public int[]? TtblkBase;

    /// <summary><c>ttblk</c> - the interior pointer into <see cref="TtblkBase"/>.</summary>
    public int TtblkOffset;

    /// <summary><c>codingset</c> - table set from 11.8 for luma blocks.</summary>
    public int Codingset;

    /// <summary><c>codingset2</c> - table set from 11.8 for chroma blocks.</summary>
    public int Codingset2;

    /// <summary><c>pqindex</c> - raw pqindex, used in coding set selection.</summary>
    public int Pqindex;

    /// <summary><c>a_avail</c>.</summary>
    public int AAvail;

    /// <summary><c>c_avail</c>.</summary>
    public int CAvail;

    /// <summary><c>mb_type_base</c>.</summary>
    public byte[]? MbTypeBase;

    /// <summary><c>mb_type</c> - the interior pointer into <see cref="MbTypeBase"/>.</summary>
    public int MbTypeOffset;

    // --- Luma compensation parameters -------------------------------------------------------

    /// <summary><c>lumscale</c>.</summary>
    public byte Lumscale;

    /// <summary><c>lumshift</c>.</summary>
    public byte Lumshift;

    /// <summary><c>bfraction</c> - relative position between anchors, for scaling MVs.</summary>
    public short Bfraction;

    /// <summary><c>halfpq</c> - uniform quantiser over the image, and qp+.5.</summary>
    public byte Halfpq;

    /// <summary><c>respic</c> - frame-level flag for resized images.</summary>
    public byte Respic;

    /// <summary><c>buffer_fullness</c> - HRD info.</summary>
    public int BufferFullness;

    /// <summary><c>mvrange</c> - extended MV range flag.</summary>
    public byte Mvrange;

    /// <summary><c>pquantizer</c> - uniform (over the sequence) quantiser in use.</summary>
    public byte Pquantizer;

    /// <summary><c>cbpcy_vlc</c> - the CBPCY table this picture reads.</summary>
    public VLCElem[]? CbpcyVlc;

    /// <summary><c>tt_index</c> - index into the transform type tables, to decode TTMB.</summary>
    public int TtIndex;

    /// <summary><c>mv_table_index</c>.</summary>
    public int MvTableIndex;

    /// <summary><c>mv_type_mb_plane</c> - bitplane for mv_type == 4MV.</summary>
    public byte[]? MvTypeMbPlane;

    /// <summary><c>direct_mb_plane</c> - bitplane for "direct" macroblocks.</summary>
    public byte[]? DirectMbPlane;

    /// <summary><c>forward_mb_plane</c> - bitplane for "forward" macroblocks.</summary>
    public byte[]? ForwardMbPlane;

    /// <summary><c>mv_type_is_raw</c>.</summary>
    public int MvTypeIsRaw;

    /// <summary><c>dmb_is_raw</c>.</summary>
    public int DmbIsRaw;

    /// <summary><c>fmb_is_raw</c>.</summary>
    public int FmbIsRaw;

    /// <summary><c>skip_is_raw</c>.</summary>
    public int SkipIsRaw;

    /// <summary><c>last_luty[2][256]</c> - intensity compensation lookup, stride 256.</summary>
    public readonly byte[] LastLuty = new byte[2 * 256];

    /// <summary><c>last_lutuv[2][256]</c>, stride 256.</summary>
    public readonly byte[] LastLutuv = new byte[2 * 256];

    /// <summary><c>aux_luty[2][256]</c>, stride 256.</summary>
    public readonly byte[] AuxLuty = new byte[2 * 256];

    /// <summary><c>aux_lutuv[2][256]</c>, stride 256.</summary>
    public readonly byte[] AuxLutuv = new byte[2 * 256];

    /// <summary><c>next_luty[2][256]</c>, stride 256.</summary>
    public readonly byte[] NextLuty = new byte[2 * 256];

    /// <summary><c>next_lutuv[2][256]</c>, stride 256.</summary>
    public readonly byte[] NextLutuv = new byte[2 * 256];

    /// <summary><c>curr_luty</c> - whichever of the three luma tables this picture uses.</summary>
    public byte[]? CurrLuty;

    /// <summary><c>curr_lutuv</c> - whichever of the three chroma tables this picture uses.</summary>
    public byte[]? CurrLutuv;

    /// <summary><c>last_use_ic</c>.</summary>
    public int LastUseIc;

    /// <summary>
    /// <c>curr_use_ic</c>. DIVERGENCE: the C holds a pointer to one of the three
    /// <c>*_use_ic</c> ints and writes through it, which has no C# equivalent; this names which
    /// one with <see cref="UseIcNone"/>, <see cref="UseIcLast"/>, <see cref="UseIcNext"/> or
    /// <see cref="UseIcAux"/>, and <see cref="CurrUseIc"/> reads and writes it.
    /// </summary>
    public int CurrUseIcSelect;

    /// <summary><c>next_use_ic</c>.</summary>
    public int NextUseIc;

    /// <summary><c>aux_use_ic</c>.</summary>
    public int AuxUseIc;

    /// <summary><c>curr_use_ic</c> is a null pointer.</summary>
    public const int UseIcNone = 0;

    /// <summary><c>curr_use_ic</c> points at <c>last_use_ic</c>.</summary>
    public const int UseIcLast = 1;

    /// <summary><c>curr_use_ic</c> points at <c>next_use_ic</c>.</summary>
    public const int UseIcNext = 2;

    /// <summary><c>curr_use_ic</c> points at <c>aux_use_ic</c>.</summary>
    public const int UseIcAux = 3;

    /// <summary><c>*v-&gt;curr_use_ic</c> - the int <c>curr_use_ic</c> points at.</summary>
    public int CurrUseIc
    {
        get => CurrUseIcSelect switch
        {
            UseIcLast => LastUseIc,
            UseIcNext => NextUseIc,
            UseIcAux => AuxUseIc,
            _ => throw new System.InvalidOperationException(
                "curr_use_ic was read while it pointed at nothing."),
        };
        set
        {
            switch (CurrUseIcSelect)
            {
                case UseIcLast: LastUseIc = value; break;
                case UseIcNext: NextUseIc = value; break;
                case UseIcAux: AuxUseIc = value; break;
                default:
                    throw new System.InvalidOperationException(
                        "curr_use_ic was written while it pointed at nothing.");
            }
        }
    }

    /// <summary><c>last_interlaced</c> - whether last_pic is interlaced.</summary>
    public int LastInterlaced;

    /// <summary><c>next_interlaced</c> - whether next_pic is interlaced.</summary>
    public int NextInterlaced;

    /// <summary><c>rnd</c> - rounding control.</summary>
    public int Rnd;

    /// <summary><c>cbptab</c>.</summary>
    public int Cbptab;

    // --- Frame decoding info for Simple/Main profiles only ----------------------------------

    /// <summary><c>rangeredfrm</c> - out_sample = CLIP((in_sample - 128) * 2 + 128).</summary>
    public byte Rangeredfrm;

    /// <summary><c>interpfrm</c>.</summary>
    public byte Interpfrm;

    // --- Frame decoding info for Advanced Profile -------------------------------------------

    /// <summary><c>fcm</c>.</summary>
    public FrameCodingMode Fcm;

    /// <summary><c>numpanscanwin</c>.</summary>
    public byte Numpanscanwin;

    /// <summary><c>tfcntr</c>.</summary>
    public byte Tfcntr;

    /// <summary><c>rptfrm</c>.</summary>
    public byte Rptfrm;

    /// <summary><c>tff</c> - top field first.</summary>
    public byte Tff;

    /// <summary><c>rff</c> - repeat first field.</summary>
    public byte Rff;

    /// <summary><c>uvsamp</c>.</summary>
    public byte Uvsamp;

    /// <summary><c>postproc</c>.</summary>
    public byte Postproc;

    /// <summary><c>hrd_num_leaky_buckets</c>.</summary>
    public int HrdNumLeakyBuckets;

    /// <summary><c>bit_rate_exponent</c>.</summary>
    public byte BitRateExponent;

    /// <summary><c>buffer_size_exponent</c>.</summary>
    public byte BufferSizeExponent;

    /// <summary><c>acpred_plane</c> - AC prediction flags bitplane.</summary>
    public byte[]? AcpredPlane;

    /// <summary><c>acpred_is_raw</c>.</summary>
    public int AcpredIsRaw;

    /// <summary><c>over_flags_plane</c> - overflags bitplane.</summary>
    public byte[]? OverFlagsPlane;

    /// <summary><c>overflg_is_raw</c>.</summary>
    public int OverflgIsRaw;

    /// <summary><c>condover</c>.</summary>
    public byte Condover;

    /// <summary><c>range_mapy_flag</c>.</summary>
    public byte RangeMapyFlag;

    /// <summary><c>range_mapuv_flag</c>.</summary>
    public byte RangeMapuvFlag;

    /// <summary><c>range_mapy</c>.</summary>
    public byte RangeMapy;

    /// <summary><c>range_mapuv</c>.</summary>
    public byte RangeMapuv;

    // --- Frame decoding info for interlaced pictures ----------------------------------------

    /// <summary><c>dmvrange</c> - extended differential MV range flag.</summary>
    public byte Dmvrange;

    /// <summary><c>fourmvswitch</c>.</summary>
    public int Fourmvswitch;

    /// <summary><c>intcomp</c>.</summary>
    public int Intcomp;

    /// <summary><c>lumscale2</c> - for an interlaced field P picture.</summary>
    public byte Lumscale2;

    /// <summary><c>lumshift2</c>.</summary>
    public byte Lumshift2;

    /// <summary><c>mbmode_vlc</c>.</summary>
    public VLCElem[]? MbmodeVlc;

    /// <summary><c>imv_vlc</c>.</summary>
    public VLCElem[]? ImvVlc;

    /// <summary><c>twomvbp_vlc</c>.</summary>
    public VLCElem[]? TwomvbpVlc;

    /// <summary><c>fourmvbp_vlc</c>.</summary>
    public VLCElem[]? FourmvbpVlc;

    /// <summary><c>twomvbp</c>.</summary>
    public byte Twomvbp;

    /// <summary><c>fourmvbp</c>.</summary>
    public byte Fourmvbp;

    /// <summary><c>fieldtx_plane</c>.</summary>
    public byte[]? FieldtxPlane;

    /// <summary><c>fieldtx_is_raw</c>.</summary>
    public int FieldtxIsRaw;

    /// <summary><c>zzi_8x8[64]</c>.</summary>
    public readonly byte[] Zzi8x8 = new byte[64];

    /// <summary><c>blk_mv_type_base</c> - 0 frame MV, 1 field MV (interlaced frame).</summary>
    public byte[]? BlkMvTypeBase;

    /// <summary><c>blk_mv_type</c> - the interior pointer into <see cref="BlkMvTypeBase"/>.</summary>
    public int BlkMvTypeOffset;

    /// <summary><c>mv_f_base</c> - 0 MV from the same field, 1 from the opposite field.</summary>
    public byte[]? MvFBase;

    /// <summary><c>mv_f[2]</c> - the two interior pointers into <see cref="MvFBase"/>.</summary>
    public readonly int[] MvFOffset = new int[2];

    /// <summary><c>mv_f_next_base</c>.</summary>
    public byte[]? MvFNextBase;

    /// <summary><c>mv_f_next[2]</c> - the two interior pointers into <see cref="MvFNextBase"/>.</summary>
    public readonly int[] MvFNextOffset = new int[2];

    /// <summary><c>field_mode</c> - 1 for interlaced field pictures.</summary>
    public int FieldMode;

    /// <summary><c>fptype</c>.</summary>
    public int Fptype;

    /// <summary><c>second_field</c>.</summary>
    public int SecondField;

    /// <summary><c>refdist</c> - distance of this picture from its reference.</summary>
    public int Refdist;

    /// <summary><c>numref</c> - past field pictures used as reference; 0 means one, 1 means two.</summary>
    public int Numref;

    /// <summary>
    /// <c>reffield</c> - with one reference, which of the previous frame's two fields to use.
    /// </summary>
    public int Reffield;

    /// <summary><c>intcompfield</c> - 0 both fields, 1 bottom, 2 top.</summary>
    public int Intcompfield;

    /// <summary><c>cur_field_type</c> - 0 top, 1 bottom.</summary>
    public int CurFieldType;

    /// <summary><c>ref_field_type[2]</c> - forward and backward reference field type.</summary>
    public readonly int[] RefFieldType = new int[2];

    /// <summary><c>blocks_off</c>.</summary>
    public int BlocksOff;

    /// <summary><c>mb_off</c>.</summary>
    public int MbOff;

    /// <summary><c>bmvtype</c>.</summary>
    public int Bmvtype;

    /// <summary><c>frfd</c> - forward reference frame distance.</summary>
    public int Frfd;

    /// <summary><c>brfd</c> - backward reference frame distance.</summary>
    public int Brfd;

    /// <summary><c>first_pic_header_flag</c>.</summary>
    public int FirstPicHeaderFlag;

    /// <summary><c>pic_header_flag</c>.</summary>
    public int PicHeaderFlag;

    /// <summary><c>mbmodetab</c>.</summary>
    public int Mbmodetab;

    /// <summary><c>icbptab</c>.</summary>
    public int Icbptab;

    /// <summary><c>imvtab</c>.</summary>
    public int Imvtab;

    /// <summary><c>twomvbptab</c>.</summary>
    public int Twomvbptab;

    /// <summary><c>fourmvbptab</c>.</summary>
    public int Fourmvbptab;

    // --- Frame decoding info for sprite modes -----------------------------------------------

    /// <summary><c>new_sprite</c>.</summary>
    public int NewSprite;

    /// <summary><c>two_sprites</c>.</summary>
    public int TwoSprites;

    /// <summary><c>output_width</c> - the container's output size, saved for WMImage.</summary>
    public int OutputWidth;

    /// <summary><c>output_height</c>.</summary>
    public int OutputHeight;

    /// <summary><c>sprite_width</c>.</summary>
    public int SpriteWidth;

    /// <summary><c>sprite_height</c>.</summary>
    public int SpriteHeight;

    /// <summary><c>p_frame_skipped</c>.</summary>
    public int PFrameSkipped;

    /// <summary><c>bi_type</c>.</summary>
    public int BiType;

    /// <summary><c>x8_type</c>.</summary>
    public int X8Type;

    /// <summary>
    /// <c>block</c> - <c>n_allocated_blks</c> groups of six 64-coefficient blocks, flattened
    /// with a stride of 6 * 64.
    /// </summary>
    public short[]? Block;

    /// <summary><c>n_allocated_blks</c>.</summary>
    public int NAllocatedBlks;

    /// <summary><c>cur_blk_idx</c>.</summary>
    public int CurBlkIdx;

    /// <summary><c>left_blk_idx</c>.</summary>
    public int LeftBlkIdx;

    /// <summary><c>topleft_blk_idx</c>.</summary>
    public int TopleftBlkIdx;

    /// <summary><c>top_blk_idx</c>.</summary>
    public int TopBlkIdx;

    /// <summary><c>cbp_base</c>.</summary>
    public uint[]? CbpBase;

    /// <summary><c>cbp</c> - the interior pointer into <see cref="CbpBase"/>.</summary>
    public int CbpOffset;

    /// <summary><c>is_intra_base</c>.</summary>
    public byte[]? IsIntraBase;

    /// <summary><c>is_intra</c> - the interior pointer into <see cref="IsIntraBase"/>.</summary>
    public int IsIntraOffset;

    /// <summary><c>luma_mv_base</c> - pairs, flattened with a stride of 2.</summary>
    public short[]? LumaMvBase;

    /// <summary><c>luma_mv</c> - the interior pointer into <see cref="LumaMvBase"/>, in pairs.</summary>
    public int LumaMvOffset;

    /// <summary>
    /// <c>bfraction_lut_index</c> - index of the BFRACTION value, Table 40, reproduced into
    /// <see cref="Vc1Data.Vc1BfractionLut"/>.
    /// </summary>
    public byte BfractionLutIndex;

    /// <summary><c>broken_link</c> - the BROKEN_LINK syntax element.</summary>
    public byte BrokenLink;

    /// <summary><c>closed_entry</c> - the CLOSED_ENTRY syntax element.</summary>
    public byte ClosedEntry;

    /// <summary><c>end_mb_x</c> - horizontal macroblock limit, used only by mss2.</summary>
    public int EndMbX;

    /// <summary><c>parse_only</c> - the context belongs to the parser.</summary>
    public int ParseOnly;

    /// <summary><c>resync_marker</c> - could this stream contain resync markers.</summary>
    public int ResyncMarker;

    /// <summary><c>blocks[6][64]</c> - the scratch block, flattened with a stride of 64.</summary>
    public readonly short[] Blocks = new short[6 * 64];

    /// <summary>
    /// <c>buf2</c> - where the first field, or a whole progressive frame, is unescaped to.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE, recorded: the C allocates this per packet with <c>av_mallocz</c> and frees
    /// it before returning. Packet sizes differ from frame to frame, so renting one per frame
    /// would allocate on nearly every frame, and a per-frame allocation is a defect here. It
    /// grows to the largest packet seen and is then kept.
    /// </remarks>
    public byte[]? Buf2;

    /// <summary>
    /// <c>slices</c> - the field and slice units of one Advanced Profile packet, each already
    /// unescaped and with a bit reader positioned at its first macroblock row.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE, recorded: the C grows this with <c>av_fast_realloc</c> per packet and frees
    /// every entry's buffer before returning; here both the array and each entry's buffer grow
    /// to the largest packet seen and are kept, for the reason <see cref="Buf2"/> gives. The
    /// C's <c>rawbuf</c> and <c>raw_size</c> are not carried: they exist only to hand the
    /// escaped bytes to a hardware accelerator, and there is none here.
    /// </remarks>
    public Vc1Slice[] Slices = [];

    /// <summary>
    /// The picture <c>vc1_decode_frame</c> output, which is not always the one it decoded.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE, recorded: the C hands the frame back with <c>av_frame_ref(pict, ...)</c>
    /// into the caller's AVFrame and reports it through <c>*got_frame</c>. There is no
    /// AVFrame here, so the choice the C makes between <c>cur_pic</c>, <c>last_pic</c> and
    /// <c>next_pic</c> is recorded as a work picture instead, exactly as
    /// <c>H263DecContext.OutputPic</c> does. A zero <c>Ptr</c> means this call produced
    /// nothing.
    /// </remarks>
    public MPVWorkPicture OutputPic;
}

/// <summary>Bitplane coding modes (<c>enum Imode</c>, vc1.h).</summary>
public enum Imode
{
    /// <summary><c>IMODE_RAW</c> - the bits are read in the macroblock layer instead.</summary>
    Raw = 0,

    /// <summary><c>IMODE_NORM2</c>.</summary>
    Norm2 = 1,

    /// <summary><c>IMODE_DIFF2</c>.</summary>
    Diff2 = 2,

    /// <summary><c>IMODE_NORM6</c>.</summary>
    Norm6 = 3,

    /// <summary><c>IMODE_DIFF6</c>.</summary>
    Diff6 = 4,

    /// <summary><c>IMODE_ROWSKIP</c>.</summary>
    Rowskip = 5,

    /// <summary><c>IMODE_COLSKIP</c>.</summary>
    Colskip = 6,
}

/// <summary><c>enum QuantMode</c> (vc1.h) - the sequence's quantiser mode.</summary>
public enum QuantMode
{
    /// <summary><c>QUANT_FRAME_IMPLICIT</c> - implicitly specified at frame level.</summary>
    FrameImplicit = 0,

    /// <summary><c>QUANT_FRAME_EXPLICIT</c> - explicitly specified at frame level.</summary>
    FrameExplicit = 1,

    /// <summary><c>QUANT_NON_UNIFORM</c> - non-uniform quant used for all frames.</summary>
    NonUniform = 2,

    /// <summary><c>QUANT_UNIFORM</c> - uniform quant used for all frames.</summary>
    Uniform = 3,
}

/// <summary><c>enum MVModes</c> (vc1.h) - a P or B picture's motion vector mode.</summary>
public enum MvModes
{
    /// <summary><c>MV_PMODE_1MV_HPEL_BILIN</c> - one half-pel vector, bilinear.</summary>
    OneMvHpelBilin = 0,

    /// <summary><c>MV_PMODE_1MV</c>.</summary>
    OneMv = 1,

    /// <summary><c>MV_PMODE_1MV_HPEL</c>.</summary>
    OneMvHpel = 2,

    /// <summary><c>MV_PMODE_MIXED_MV</c>.</summary>
    MixedMv = 3,

    /// <summary><c>MV_PMODE_INTENSITY_COMP</c>.</summary>
    IntensityComp = 4,
}

/// <summary><c>enum MBModesIntfr</c> (vc1.h) - MBMODE for an interlaced frame P picture.</summary>
public enum MbModesIntfr
{
    /// <summary><c>MV_PMODE_INTFR_1MV</c>.</summary>
    OneMv = 0,

    /// <summary><c>MV_PMODE_INTFR_2MV_FIELD</c>.</summary>
    TwoMvField = 1,

    /// <summary><c>MV_PMODE_INTFR_2MV</c>.</summary>
    TwoMv = 2,

    /// <summary><c>MV_PMODE_INTFR_4MV_FIELD</c>.</summary>
    FourMvField = 3,

    /// <summary><c>MV_PMODE_INTFR_4MV</c>.</summary>
    FourMv = 4,

    /// <summary><c>MV_PMODE_INTFR_INTRA</c>.</summary>
    Intra = 5,
}

/// <summary>
/// <c>enum CodingSet</c> - which AC coefficient table a block is read through. The picture
/// loops choose one for intra blocks and one for inter blocks from the two table indices in
/// the picture header and the quantiser index.
/// </summary>
public enum CodingSet
{
    /// <summary><c>CS_HIGH_MOT_INTRA</c>.</summary>
    HighMotIntra = 0,

    /// <summary><c>CS_HIGH_MOT_INTER</c>.</summary>
    HighMotInter = 1,

    /// <summary><c>CS_LOW_MOT_INTRA</c>.</summary>
    LowMotIntra = 2,

    /// <summary><c>CS_LOW_MOT_INTER</c>.</summary>
    LowMotInter = 3,

    /// <summary><c>CS_MID_RATE_INTRA</c>.</summary>
    MidRateIntra = 4,

    /// <summary><c>CS_MID_RATE_INTER</c>.</summary>
    MidRateInter = 5,

    /// <summary><c>CS_HIGH_RATE_INTRA</c>.</summary>
    HighRateIntra = 6,

    /// <summary><c>CS_HIGH_RATE_INTER</c>.</summary>
    HighRateInter = 7,
}

/// <summary><c>enum COTypes</c> (vc1.h) - the Advanced Profile overlap conditions.</summary>
// Named after the C enum rather than after its CONDOVER_ prefix, because VC1Context has a
// field called condover and a type of that name beside it would shadow confusingly.
public enum CoTypes
{
    /// <summary><c>CONDOVER_NONE</c>.</summary>
    None = 0,

    /// <summary><c>CONDOVER_ALL</c>.</summary>
    All = 1,

    /// <summary><c>CONDOVER_SELECT</c> - a bitplane says which macroblocks overlap.</summary>
    Select = 2,
}

/// <summary>The quantiser profiles a frame-level DQUANT can select (<c>enum DQProfile</c>).</summary>
public enum DqProfile
{
    /// <summary><c>DQPROFILE_FOUR_EDGES</c>.</summary>
    FourEdges = 0,

    /// <summary><c>DQPROFILE_DOUBLE_EDGES</c>.</summary>
    DoubleEdges = 1,

    /// <summary><c>DQPROFILE_SINGLE_EDGE</c>.</summary>
    SingleEdge = 2,

    /// <summary><c>DQPROFILE_ALL_MBS</c>.</summary>
    AllMbs = 3,
}

/// <summary>The shared VC-1 header and bitplane parsing (<c>vc1.c</c>).</summary>
public static class Vc1
{
    /// <summary><c>AVERROR_INVALIDDATA</c> - <c>-MKTAG('I', 'N', 'D', 'A')</c>.</summary>
    public const int AverrorInvalidData = -1094995529;

    /// <summary>
    /// <c>AVERROR(EINVAL)</c>. Not the same value as <see cref="AverrorInvalidData"/>, and the
    /// two are not interchangeable: it is what <c>av_image_check_size2</c> and
    /// <c>av_image_check_sar</c> return, and a sequence header hands it back to its caller
    /// unchanged.
    /// </summary>
    public const int AverrorEinval = -22;

    /// <summary><c>AVERROR_PATCHWELCOME</c> - <c>-MKTAG(0xF8, 'P', 'A', 'W')</c>.</summary>
    public const int AverrorPatchwelcome = -1463898360;

    /// <summary>
    /// <c>AV_INPUT_BUFFER_PADDING_SIZE</c> - the slack every buffer a bit reader is pointed at
    /// carries, so that a reader which runs past the end reads zeroes rather than whatever
    /// follows.
    /// </summary>
    public const int InputBufferPaddingSize = 64;

    /// <summary>
    /// <c>decode_rowskip</c> - one flag per row; a cleared flag means the whole row is zero.
    /// </summary>
    public static void DecodeRowskip(
        Span<byte> plane, int planeOffset, int width, int height, int stride, ref GetBitContext gb)
    {
        for (int y = 0; y < height; y++)
        {
            if (gb.GetBits1() == 0) // rowskip
                plane.Slice(planeOffset, width).Clear();
            else
                for (int x = 0; x < width; x++)
                    plane[planeOffset + x] = (byte)gb.GetBits1();
            planeOffset += stride;
        }
    }

    /// <summary>
    /// <c>decode_colskip</c> - one flag per column; a cleared flag means the whole column is zero.
    /// </summary>
    public static void DecodeColskip(
        Span<byte> plane, int planeOffset, int width, int height, int stride, ref GetBitContext gb)
    {
        for (int x = 0; x < width; x++)
        {
            if (gb.GetBits1() == 0) // colskip
                for (int y = 0; y < height; y++)
                    plane[planeOffset + y * stride] = 0;
            else
                for (int y = 0; y < height; y++)
                    plane[planeOffset + y * stride] = (byte)gb.GetBits1();
            planeOffset++;
        }
    }

    /// <summary>
    /// <c>bitplane_decoding</c> - reads one bitplane into <paramref name="data"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <c>(imode &lt;&lt; 1) + invert</c>, or -1 when a NORM-6 code falls in one of that
    /// book's holes. <paramref name="rawFlag"/> comes back set when the plane is raw, in which
    /// case nothing was written and <c>invert</c> is the return value.
    /// </para>
    /// <para>
    /// The plane is <c>mb_width</c> wide and <c>mb_height &gt;&gt; field_mode</c> tall but is
    /// stored at <c>mb_stride</c>, so the NORM-2 arm - which treats the plane as one long line -
    /// has to step over the gap at the end of every row, and does it with a running
    /// <c>offset</c> rather than by indexing.
    /// </para>
    /// </remarks>
    public static int BitplaneDecoding(Span<byte> data, out int rawFlag, Vc1Context v)
    {
        ref GetBitContext gb = ref v.Gb;

        int width = v.S.MbWidth;
        int height = v.S.MbHeight >> v.FieldMode;
        int stride = v.S.MbStride;
        byte invert = (byte)gb.GetBits1();
        int imode = gb.GetVlc2(Vc1Data.Vc1ImodeVlc, Vc1Data.ImodeVlcBits, 1);

        int planep = 0;
        rawFlag = 0;
        switch ((Imode)imode)
        {
            case Imode.Raw:
                // Data is actually read in the MB layer (same for all tests == "raw").
                rawFlag = 1; // invert ignored
                return invert;

            case Imode.Diff2:
            case Imode.Norm2:
            {
                int y, offset;
                if (((height * width) & 1) != 0)
                {
                    data[planep++] = (byte)gb.GetBits1();
                    y = offset = 1;
                    if (offset == width)
                    {
                        offset = 0;
                        planep += stride - width;
                    }
                }
                else
                {
                    y = offset = 0;
                }

                // Decode the bitplane as one long line.
                for (; y < height * width; y += 2)
                {
                    int code = gb.GetVlc2(Vc1Data.Vc1Norm2Vlc, Vc1Data.Norm2VlcBits, 1);
                    data[planep++] = (byte)(code & 1);
                    offset++;
                    if (offset == width)
                    {
                        offset = 0;
                        planep += stride - width;
                    }
                    data[planep++] = (byte)(code >> 1);
                    offset++;
                    if (offset == width)
                    {
                        offset = 0;
                        planep += stride - width;
                    }
                }
                break;
            }

            case Imode.Diff6:
            case Imode.Norm6:
                if (height % 3 == 0 && width % 3 != 0)
                {
                    // 2x3 decoding.
                    for (int y = 0; y < height; y += 3)
                    {
                        for (int x = width & 1; x < width; x += 2)
                        {
                            int code = gb.GetVlc2(Vc1Data.Vc1Norm6Vlc, Vc1Data.Norm6VlcBits, 2);
                            if (code < 0)
                                return -1;  // invalid NORM-6 VLC
                            data[planep + x + 0] = (byte)((code >> 0) & 1);
                            data[planep + x + 1] = (byte)((code >> 1) & 1);
                            data[planep + x + 0 + stride] = (byte)((code >> 2) & 1);
                            data[planep + x + 1 + stride] = (byte)((code >> 3) & 1);
                            data[planep + x + 0 + stride * 2] = (byte)((code >> 4) & 1);
                            data[planep + x + 1 + stride * 2] = (byte)((code >> 5) & 1);
                        }
                        planep += stride * 3;
                    }
                    if ((width & 1) != 0)
                        DecodeColskip(data, 0, 1, height, stride, ref gb);
                }
                else
                {
                    // 3x2 decoding.
                    planep += (height & 1) * stride;
                    for (int y = height & 1; y < height; y += 2)
                    {
                        for (int x = width % 3; x < width; x += 3)
                        {
                            int code = gb.GetVlc2(Vc1Data.Vc1Norm6Vlc, Vc1Data.Norm6VlcBits, 2);
                            if (code < 0)
                                return -1;  // invalid NORM-6 VLC
                            data[planep + x + 0] = (byte)((code >> 0) & 1);
                            data[planep + x + 1] = (byte)((code >> 1) & 1);
                            data[planep + x + 2] = (byte)((code >> 2) & 1);
                            data[planep + x + 0 + stride] = (byte)((code >> 3) & 1);
                            data[planep + x + 1 + stride] = (byte)((code >> 4) & 1);
                            data[planep + x + 2 + stride] = (byte)((code >> 5) & 1);
                        }
                        planep += stride * 2;
                    }
                    int rest = width % 3;
                    if (rest != 0)
                        DecodeColskip(data, 0, rest, height, stride, ref gb);
                    if ((height & 1) != 0)
                        DecodeRowskip(data, rest, width - rest, 1, stride, ref gb);
                }
                break;

            case Imode.Rowskip:
                DecodeRowskip(data, 0, width, height, stride, ref gb);
                break;

            case Imode.Colskip:
                DecodeColskip(data, 0, width, height, stride, ref gb);
                break;

            default:
                break;
        }

        // Applying the diff operator.
        if (imode == (int)Imode.Diff2 || imode == (int)Imode.Diff6)
        {
            planep = 0;
            data[0] ^= invert;
            for (int x = 1; x < width; x++)
                data[x] ^= data[x - 1];
            for (int y = 1; y < height; y++)
            {
                planep += stride;
                data[planep] ^= data[planep - stride];
                for (int x = 1; x < width; x++)
                {
                    if (data[planep + x - 1] != data[planep + x - stride])
                        data[planep + x] ^= invert;
                    else
                        data[planep + x] ^= data[planep + x - 1];
                }
            }
        }
        else if (invert != 0)
        {
            // FFmpeg's own "FIXME stride": this inverts the padding between rows as well.
            for (int x = 0; x < stride * height; x++)
                data[x] = (byte)(data[x] != 0 ? 0 : 1);
        }

        return (imode << 1) + invert;
    }

    /// <summary>
    /// <c>vop_dquant_decoding</c> - the frame-level quantiser variation, 8.1.1.11.
    /// </summary>
    public static int VopDquantDecoding(Vc1Context v)
    {
        ref GetBitContext gb = ref v.Gb;

        // Variable size.
        if (v.Dquant != 2)
        {
            v.Dquantfrm = (byte)gb.GetBits1();
            if (v.Dquantfrm == 0)
                return 0;

            v.Dqprofile = (byte)gb.GetBits(2);
            switch ((DqProfile)v.Dqprofile)
            {
                case DqProfile.SingleEdge:
                case DqProfile.DoubleEdges:
                    v.Dqsbedge = (byte)gb.GetBits(2);
                    break;
                case DqProfile.AllMbs:
                    v.Dqbilevel = (byte)gb.GetBits1();
                    if (v.Dqbilevel == 0)
                    {
                        v.Halfpq = 0;
                        return 0;
                    }
                    break;
                default:
                    break;  // Forbidden?
            }
        }

        int pqdiff = (int)gb.GetBits(3);
        if (pqdiff == 7)
            v.Altpq = (byte)gb.GetBits(5);
        else
            v.Altpq = (byte)(v.Pq + pqdiff + 1);

        return 0;
    }

    /// <summary>
    /// <c>read_bfraction</c> - BFRACTION, Table 40, as an index into
    /// <see cref="Vc1Data.Vc1BfractionLut"/>.
    /// </summary>
    /// <returns>0, or <see cref="AverrorInvalidData"/> for the invalid index 21.</returns>
    public static int ReadBfraction(Vc1Context v, ref GetBitContext gb)
    {
        int bfractionLutIndex = (int)gb.GetBits(3);

        if (bfractionLutIndex == 7)
            bfractionLutIndex = 7 + (int)gb.GetBits(4);

        if (bfractionLutIndex == 21)
            return AverrorInvalidData;  // bfraction invalid

        v.BfractionLutIndex = (byte)bfractionLutIndex;
        v.Bfraction = Vc1Data.Vc1BfractionLut[v.BfractionLutIndex];
        return 0;
    }

    /// <summary>
    /// <c>ff_vc1_decode_sequence_header</c> - the Simple and Main Profile sequence header, and
    /// the gate into the Advanced Profile one. See Figure 7-8, p16-17.
    /// </summary>
    /// <remarks>
    /// The <c>avctx</c> parameter of the C is <c>v-&gt;s.avctx</c> at both call sites, and is
    /// <see cref="Vc1Context.Avctx"/> here.
    /// </remarks>
    /// <returns>0, or negative for a header this decoder cannot use.</returns>
    public static int Vc1DecodeSequenceHeader(Vc1Context v, ref GetBitContext gb)
    {
        v.Profile = (int)gb.GetBits(2);
        if (v.Profile == (int)Vc1Profile.Complex)
        {
            // WMV3 Complex Profile is not fully supported.
        }

        if (v.Profile == (int)Vc1Profile.Advanced)
        {
            v.Zz8x4 = Vc1Data.Vc1AdvProgressive8x4Zz;
            v.Zz4x8 = Vc1Data.Vc1AdvProgressive4x8Zz;
            return DecodeSequenceHeaderAdv(v, ref gb);
        }
        else
        {
            v.Chromaformat = 1;
            v.Zz8x4 = Wmv2Data.Wmv2ScantableA;
            v.Zz4x8 = Wmv2Data.Wmv2ScantableB;
            v.ResY411 = (int)gb.GetBits1();
            v.ResSprite = (int)gb.GetBits1();
            if (v.ResY411 != 0)
                return -1;  // old interlaced mode is not supported
        }

        // (fps-2)/4 (->30)
        v.FrmrtqPostproc = (int)gb.GetBits(3);   // common
        // (bitrate-32kbps)/64kbps
        v.BitrtqPostproc = (int)gb.GetBits(5);   // common
        v.LoopFilter = (int)gb.GetBits1();       // common
        // LOOPFILTER shall not be enabled in Simple Profile; ffmpeg logs and continues.
        if (v.Avctx.SkipLoopFilter >= AvDiscard.All)
            v.LoopFilter = 0;

        v.ResX8 = (int)gb.GetBits1();            // reserved
        v.Multires = (int)gb.GetBits1();
        v.ResFasttx = (int)gb.GetBits1();

        v.Fastuvmc = (int)gb.GetBits1();         // common
        if (v.Profile == 0 && v.Fastuvmc == 0)
            return -1;  // FASTUVMC unavailable in Simple Profile
        v.ExtendedMv = (int)gb.GetBits1();       // common
        if (v.Profile == 0 && v.ExtendedMv != 0)
            return -1;  // extended MVs unavailable in Simple Profile
        v.Dquant = (int)gb.GetBits(2);           // common
        v.Vstransform = (int)gb.GetBits1();      // common

        v.ResTranstab = (int)gb.GetBits1();
        if (v.ResTranstab != 0)
            return -1;  // 1 for reserved RES_TRANSTAB is forbidden

        v.Overlap = (int)gb.GetBits1();          // common

        v.ResyncMarker = (int)gb.GetBits1();
        v.Rangered = (int)gb.GetBits1();
        // RANGERED should be set to 0 in Simple Profile; ffmpeg logs and continues.

        v.MaxBFrames = v.Avctx.MaxBFrames = (int)gb.GetBits(3);  // common
        v.QuantizerMode = (int)gb.GetBits(2);    // common

        v.Finterpflag = (int)gb.GetBits1();      // common

        if (v.ResSprite != 0)
        {
            int w = (int)gb.GetBits(11);
            int h = (int)gb.GetBits(11);
            int ret = v.Avctx.SetDimensions(w, h);
            if (ret < 0)
                return ret;  // failed to set dimensions
            gb.SkipBits(5);  // frame rate
            v.ResX8 = (int)gb.GetBits1();
            if (gb.GetBits1() != 0)  // something to do with DC VLC selection
                return -1;  // unsupported sprite feature
            gb.SkipBits(3);  // slice code
            v.ResRtmFlag = 0;
        }
        else
        {
            v.ResRtmFlag = (int)gb.GetBits1();   // reserved
        }
        // TODO: figure out what they mean (always 0x402F)
        if (v.ResFasttx == 0)
            gb.SkipBits(16);
        return 0;
    }

    /// <summary>
    /// <c>decode_sequence_header_adv</c> - the rest of an Advanced Profile sequence header.
    /// </summary>
    public static int DecodeSequenceHeaderAdv(Vc1Context v, ref GetBitContext gb)
    {
        v.ResRtmFlag = 1;
        v.Level = (int)gb.GetBits(3);
        // A LEVEL of 5 or more is reserved; ffmpeg logs and continues.
        v.Chromaformat = (int)gb.GetBits(2);
        if (v.Chromaformat != 1)
            return -1;  // only 4:2:0 chroma format supported

        // (fps-2)/4 (->30)
        v.FrmrtqPostproc = (int)gb.GetBits(3);   // common
        // (bitrate-32kbps)/64kbps
        v.BitrtqPostproc = (int)gb.GetBits(5);   // common
        v.Postprocflag = (int)gb.GetBits1();     // common

        v.MaxCodedWidth = ((int)gb.GetBits(12) + 1) << 1;
        v.MaxCodedHeight = ((int)gb.GetBits(12) + 1) << 1;
        v.Broadcast = (int)gb.GetBits1();
        v.Interlace = (int)gb.GetBits1();
        v.Tfcntrflag = (int)gb.GetBits1();
        v.Finterpflag = (int)gb.GetBits1();
        gb.SkipBits1();  // reserved

        v.Psf = (int)gb.GetBits1();
        if (v.Psf != 0)  // PsF, 6.1.13
            return -1;  // Progressive Segmented Frame mode: not supported (yet)
        v.MaxBFrames = v.Avctx.MaxBFrames = 7;
        if (gb.GetBits1() != 0)  // Display Info - decoding is not affected by it
        {
            int w, h, ar = 0;
            w = (int)gb.GetBits(14) + 1;
            h = (int)gb.GetBits(14) + 1;
            if (gb.GetBits1() != 0)
                ar = (int)gb.GetBits(4);
            if (ar != 0 && ar < 14)
            {
                v.Avctx.SampleAspectRatio = new AvRational(
                    Vc1Data.Vc1PixelAspect[ar * 2], Vc1Data.Vc1PixelAspect[ar * 2 + 1]);
            }
            else if (ar == 15)
            {
                w = (int)gb.GetBits(8) + 1;
                h = (int)gb.GetBits(8) + 1;
                v.Avctx.SampleAspectRatio = new AvRational(w, h);
            }
            else
            {
                if (v.Avctx.Width > v.MaxCodedWidth || v.Avctx.Height > v.MaxCodedHeight)
                {
                    // avpriv_request_sample("Huge resolution"): the aspect ratio is left alone.
                }
                else
                {
                    AvRationalMath.Reduce(out int num, out int den,
                        (long)v.Avctx.Height * w, (long)v.Avctx.Width * h, 1 << 30);
                    v.Avctx.SampleAspectRatio = new AvRational(num, den);
                }
            }
            v.Avctx.SetSar(v.Avctx.SampleAspectRatio);

            if (gb.GetBits1() != 0)  // framerate stuff
            {
                if (gb.GetBits1() != 0)
                {
                    v.Avctx.Framerate = new AvRational((int)gb.GetBits(16) + 1, 32);
                }
                else
                {
                    int nr = (int)gb.GetBits(8);
                    int dr = (int)gb.GetBits(4);
                    if (nr > 0 && nr < 8 && dr > 0 && dr < 3)
                    {
                        v.Avctx.Framerate = new AvRational(
                            Vc1Data.Vc1FpsNr[nr - 1] * 1000, Vc1Data.Vc1FpsDr[dr - 1]);
                    }
                }
            }

            if (gb.GetBits1() != 0)
            {
                v.ColorPrim = (int)gb.GetBits(8);
                v.TransferChar = (int)gb.GetBits(8);
                v.MatrixCoef = (int)gb.GetBits(8);
            }
        }

        v.HrdParamFlag = (int)gb.GetBits1();
        if (v.HrdParamFlag != 0)
        {
            v.HrdNumLeakyBuckets = (int)gb.GetBits(5);
            gb.SkipBits(4);  // bitrate exponent
            gb.SkipBits(4);  // buffer size exponent
            for (int i = 0; i < v.HrdNumLeakyBuckets; i++)
            {
                gb.SkipBits(16);  // hrd_rate[n]
                gb.SkipBits(16);  // hrd_buffer[n]
            }
        }
        return 0;
    }

    /// <summary>
    /// <c>ff_vc1_decode_entry_point</c> - the Advanced Profile entry point header, which may
    /// re-state most of the sequence header's coding flags and the coded size.
    /// </summary>
    public static int Vc1DecodeEntryPoint(Vc1Context v, ref GetBitContext gb)
    {
        int w, h;
        int ret;

        v.BrokenLink = (byte)gb.GetBits1();
        v.ClosedEntry = (byte)gb.GetBits1();
        v.Panscanflag = (int)gb.GetBits1();
        v.RefdistFlag = (int)gb.GetBits1();
        v.LoopFilter = (int)gb.GetBits1();
        if (v.Avctx.SkipLoopFilter >= AvDiscard.All)
            v.LoopFilter = 0;
        v.Fastuvmc = (int)gb.GetBits1();
        v.ExtendedMv = (int)gb.GetBits1();
        v.Dquant = (int)gb.GetBits(2);
        v.Vstransform = (int)gb.GetBits1();
        v.Overlap = (int)gb.GetBits1();
        v.QuantizerMode = (int)gb.GetBits(2);

        if (v.HrdParamFlag != 0)
        {
            for (int i = 0; i < v.HrdNumLeakyBuckets; i++)
                gb.SkipBits(8);  // hrd_full[n]
        }

        if (gb.GetBits1() != 0)
        {
            w = ((int)gb.GetBits(12) + 1) << 1;
            h = ((int)gb.GetBits(12) + 1) << 1;
        }
        else
        {
            w = v.MaxCodedWidth;
            h = v.MaxCodedHeight;
        }
        if ((ret = v.Avctx.SetDimensions(w, h)) < 0)
            return ret;  // failed to set dimensions

        if (v.ExtendedMv != 0)
            v.ExtendedDmv = (int)gb.GetBits1();
        if ((v.RangeMapyFlag = (byte)gb.GetBits1()) != 0)
        {
            // Luma scaling is not supported, expect wrong picture.
            v.RangeMapy = (byte)gb.GetBits(3);
        }
        if ((v.RangeMapuvFlag = (byte)gb.GetBits1()) != 0)
        {
            // Chroma scaling is not supported, expect wrong picture.
            v.RangeMapuv = (byte)gb.GetBits(3);
        }

        return 0;
    }

    /// <summary>
    /// <c>INIT_LUT</c> - fill one pair of intensity compensation lookup tables.
    /// </summary>
    /// <remarks>
    /// A macro in the C because <c>luty</c> and <c>lutuv</c> are a row of a two-dimensional
    /// array at some call sites and a whole array at others; a method taking two spans covers
    /// both. <paramref name="chain"/> composes the new scale onto whatever the tables already
    /// hold, which is how a second intensity compensation on the same picture is applied.
    /// </remarks>
    public static void InitLut(
        int lumscale, int lumshift, Span<byte> luty, Span<byte> lutuv, int chain)
    {
        int scale, shift;
        if (lumscale == 0)
        {
            scale = -64;
            shift = (255 - lumshift * 2) * 64;
            if (lumshift > 31)
                shift += 128 << 6;
        }
        else
        {
            scale = lumscale + 32;
            if (lumshift > 31)
                shift = (lumshift - 64) * 64;
            else
                shift = lumshift << 6;
        }
        for (int i = 0; i < 256; i++)
        {
            int iy = chain != 0 ? luty[i] : i;
            int iu = chain != 0 ? lutuv[i] : i;
            luty[i] = AvClipUint8((scale * iy + shift + 32) >> 6);
            lutuv[i] = AvClipUint8((scale * (iu - 128) + 128 * 64 + 32) >> 6);
        }
    }

    /// <summary>
    /// <c>rotate_luts</c> - choose which of the three intensity compensation table sets this
    /// picture writes into, and reset it to the identity.
    /// </summary>
    /// <remarks>
    /// A B or BI picture uses the auxiliary set and leaves the other two alone. Any other
    /// picture type pushes the stream along: what was the last picture's set becomes the next
    /// one's and vice versa, so that the tables a P picture builds are the ones the picture
    /// after it reads as "last". The C does the swap with three <c>ROTATE</c> expansions over
    /// pointers; here the contents are swapped and <c>curr_*</c> is pointed at <c>next_*</c>,
    /// which is what <c>C = N</c> does after the memcpys.
    /// </remarks>
    public static void RotateLuts(Vc1Context v)
    {
        if (v.S.PictType == AvPictureType.Bi || v.S.PictType == AvPictureType.B)
        {
            v.CurrUseIcSelect = Vc1Context.UseIcAux;
            v.CurrLuty = v.AuxLuty;
            v.CurrLutuv = v.AuxLutuv;
        }
        else
        {
            // ROTATE(int tmp, &v->last_use_ic, &v->next_use_ic, v->curr_use_ic)
            (v.LastUseIc, v.NextUseIc) = (v.NextUseIc, v.LastUseIc);
            v.CurrUseIcSelect = Vc1Context.UseIcNext;

            // ROTATE(uint8_t tmp[2][256], v->last_luty, v->next_luty, v->curr_luty)
            Swap(v.LastLuty, v.NextLuty);
            v.CurrLuty = v.NextLuty;

            // ROTATE(uint8_t tmp[2][256], v->last_lutuv, v->next_lutuv, v->curr_lutuv)
            Swap(v.LastLutuv, v.NextLutuv);
            v.CurrLutuv = v.NextLutuv;
        }

        InitLut(32, 0, v.CurrLuty.AsSpan(0, 256), v.CurrLutuv.AsSpan(0, 256), 0);
        InitLut(32, 0, v.CurrLuty.AsSpan(256, 256), v.CurrLutuv.AsSpan(256, 256), 0);
        v.CurrUseIc = 0;
    }

    /// <summary>
    /// <c>ROTATE</c>'s two memcpys through a stack temporary, over a whole table.
    /// </summary>
    private static void Swap(Span<byte> a, Span<byte> b)
    {
        for (int i = 0; i < a.Length; i++)
            (a[i], b[i]) = (b[i], a[i]);
    }

    /// <summary><c>av_clip_uint8</c>.</summary>
    private static byte AvClipUint8(int a) => (byte)(a < 0 ? 0 : a > 255 ? 255 : a);

    /// <summary>
    /// <c>ff_vc1_parse_frame_header</c> - the Simple and Main Profile picture header.
    /// </summary>
    /// <returns>0, or negative for a header this decoder cannot use.</returns>
    public static int Vc1ParseFrameHeader(Vc1Context v, ref GetBitContext gb)
    {
        int pqindex, lowquant, status;

        v.FieldMode = 0;
        v.Fcm = FrameCodingMode.Progressive;
        if (v.Finterpflag != 0)
            v.Interpfrm = (byte)gb.GetBits1();
        // The C has an MSS2 arm here that reads RESPIC/RANGERED/MULTIRES out of the two bits
        // this profile calls framecnt. MSS2 is a separate decoder that is not in scope, and
        // this port has no codec_id to test, so only the framecnt arm is translated.
        gb.SkipBits(2);  // framecnt unused
        v.Rangeredfrm = 0;
        if (v.Rangered != 0)
            v.Rangeredfrm = (byte)gb.GetBits1();
        if (gb.GetBits1() != 0)
        {
            v.S.PictType = AvPictureType.P;
        }
        else
        {
            if (v.Avctx.MaxBFrames != 0 && gb.GetBits1() == 0)
                v.S.PictType = AvPictureType.B;
            else
                v.S.PictType = AvPictureType.I;
        }

        v.BiType = 0;
        if (v.S.PictType == AvPictureType.B)
        {
            if (ReadBfraction(v, ref gb) < 0)
                return AverrorInvalidData;
            if (v.Bfraction == 0)
                v.S.PictType = AvPictureType.Bi;
        }
        if (v.S.PictType == AvPictureType.I || v.S.PictType == AvPictureType.Bi)
            gb.SkipBits(7);  // skip buffer fullness

        if (v.ParseOnly != 0)
            return 0;

        // Calculate RND.
        if (v.S.PictType == AvPictureType.I || v.S.PictType == AvPictureType.Bi)
            v.Rnd = 1;
        if (v.S.PictType == AvPictureType.P)
            v.Rnd ^= 1;

        if (gb.GetBitsLeft() < 5)
            return AverrorInvalidData;

        // Quantizer stuff.
        pqindex = (int)gb.GetBits(5);
        if (pqindex == 0)
            return -1;
        v.Pq = v.QuantizerMode == (int)QuantMode.FrameImplicit
            ? Vc1Data.Vc1PquantTable[pqindex]
            : Vc1Data.Vc1PquantTable[32 + pqindex];
        v.Pqindex = pqindex;
        v.Halfpq = pqindex < 9 ? (byte)gb.GetBits1() : (byte)0;
        v.Pquantizer = (QuantMode)v.QuantizerMode switch
        {
            QuantMode.FrameImplicit => (byte)(pqindex < 9 ? 1 : 0),
            QuantMode.NonUniform => 0,
            QuantMode.FrameExplicit => (byte)gb.GetBits1(),
            _ => 1,
        };
        v.Dquantfrm = 0;
        if (v.ExtendedMv == 1)
            v.Mvrange = (byte)gb.GetUnary(0, 3);
        v.KX = v.Mvrange + 9 + (v.Mvrange >> 1);  // k_x can be 9 10 12 13
        v.KY = v.Mvrange + 8;                     // k_y can be 8 9 10 11
        v.RangeX = 1 << (v.KX - 1);
        v.RangeY = 1 << (v.KY - 1);
        if (v.Multires != 0 && v.S.PictType != AvPictureType.B)
            v.Respic = (byte)gb.GetBits(2);

        if (v.ResX8 != 0 &&
            (v.S.PictType == AvPictureType.I || v.S.PictType == AvPictureType.Bi))
            v.X8Type = (int)gb.GetBits1();
        else
            v.X8Type = 0;

        if (v.FirstPicHeaderFlag != 0)
            RotateLuts(v);

        switch (v.S.PictType)
        {
            case AvPictureType.P:
                v.TtIndex = (v.Pq > 4 ? 1 : 0) + (v.Pq > 12 ? 1 : 0);

                lowquant = v.Pq > 12 ? 0 : 1;
                v.MvMode = Vc1Data.Vc1MvPmodeTable[lowquant * 5 + gb.GetUnary(1, 4)];
                if (v.MvMode == (byte)MvModes.IntensityComp)
                {
                    v.MvMode2 = Vc1Data.Vc1MvPmodeTable2[lowquant * 4 + gb.GetUnary(1, 3)];
                    v.Lumscale = (byte)gb.GetBits(6);
                    v.Lumshift = (byte)gb.GetBits(6);
                    v.LastUseIc = 1;
                    // Fill lookup tables for intensity compensation.
                    InitLut(v.Lumscale, v.Lumshift,
                            v.LastLuty.AsSpan(0, 256), v.LastLutuv.AsSpan(0, 256), 1);
                    InitLut(v.Lumscale, v.Lumshift,
                            v.LastLuty.AsSpan(256, 256), v.LastLutuv.AsSpan(256, 256), 1);
                }
                if (v.MvMode == (byte)MvModes.IntensityComp)
                {
                    v.S.QuarterSample = v.MvMode2 != (byte)MvModes.OneMvHpel &&
                                        v.MvMode2 != (byte)MvModes.OneMvHpelBilin ? 1 : 0;
                    v.S.Mspel = v.MvMode2 != (byte)MvModes.OneMvHpelBilin ? 1 : 0;
                }
                else
                {
                    v.S.QuarterSample = v.MvMode != (byte)MvModes.OneMvHpel &&
                                        v.MvMode != (byte)MvModes.OneMvHpelBilin ? 1 : 0;
                    v.S.Mspel = v.MvMode != (byte)MvModes.OneMvHpelBilin ? 1 : 0;
                }

                if ((v.MvMode == (byte)MvModes.IntensityComp &&
                     v.MvMode2 == (byte)MvModes.MixedMv) ||
                    v.MvMode == (byte)MvModes.MixedMv)
                {
                    status = BitplaneDecoding(v.MvTypeMbPlane, out v.MvTypeIsRaw, v);
                    if (status < 0)
                        return -1;
                }
                else
                {
                    v.MvTypeIsRaw = 0;
                    v.MvTypeMbPlane.AsSpan(0, v.S.MbStride * v.S.MbHeight).Clear();
                }
                status = BitplaneDecoding(v.S.MbskipTable, out v.SkipIsRaw, v);
                if (status < 0)
                    return -1;

                if (gb.GetBitsLeft() < 4)
                    return AverrorInvalidData;

                // Hopefully this is correct for P-frames.
                v.MvTableIndex = (int)gb.GetBits(2);  // but using ff_vc1_ tables
                v.Cbptab = (int)gb.GetBits(2);
                v.CbpcyVlc = Vc1Data.Vc1CbpcyPVlc[v.Cbptab];

                if (v.Dquant != 0)
                    VopDquantDecoding(v);

                ReadTransformType(v, ref gb);
                break;

            case AvPictureType.B:
                v.TtIndex = (v.Pq > 4 ? 1 : 0) + (v.Pq > 12 ? 1 : 0);

                v.MvMode = gb.GetBits1() != 0
                    ? (byte)MvModes.OneMv : (byte)MvModes.OneMvHpelBilin;
                v.S.QuarterSample = v.MvMode == (byte)MvModes.OneMv ? 1 : 0;
                v.S.Mspel = v.S.QuarterSample;

                status = BitplaneDecoding(v.DirectMbPlane, out v.DmbIsRaw, v);
                if (status < 0)
                    return -1;
                status = BitplaneDecoding(v.S.MbskipTable, out v.SkipIsRaw, v);
                if (status < 0)
                    return -1;

                v.MvTableIndex = (int)gb.GetBits(2);
                v.Cbptab = (int)gb.GetBits(2);
                v.CbpcyVlc = Vc1Data.Vc1CbpcyPVlc[v.Cbptab];

                if (v.Dquant != 0)
                    VopDquantDecoding(v);

                ReadTransformType(v, ref gb);
                break;

            default:
                break;
        }

        if (v.X8Type == 0)
        {
            // AC Syntax.
            v.CAcTableIndex = gb.Decode012();
            if (v.S.PictType == AvPictureType.I || v.S.PictType == AvPictureType.Bi)
                v.YAcTableIndex = gb.Decode012();
            // DC Syntax.
            v.DcTableIndex = (int)gb.GetBits1();
        }

        if (v.S.PictType == AvPictureType.Bi)
        {
            v.S.PictType = AvPictureType.B;
            v.BiType = 1;
        }
        return 0;
    }

    /// <summary>
    /// <c>ff_vc1_parse_frame_header_adv</c> - the Advanced Profile picture header, which also
    /// covers the interlaced frame and interlaced field coding modes.
    /// </summary>
    public static int Vc1ParseFrameHeaderAdv(Vc1Context v, ref GetBitContext gb)
    {
        // The C declares pqindex, lowquant and status here too; all three belong to the
        // parse_common_info half, which is its own method.
        int fieldMode;

        v.Numref = 0;
        v.PFrameSkipped = 0;
        if (v.SecondField != 0)
        {
            if (v.Fcm != FrameCodingMode.IlaceField || v.FieldMode != 1)
                return -1;
            if ((v.Fptype & 4) != 0)
                v.S.PictType = (v.Fptype & 1) != 0 ? AvPictureType.Bi : AvPictureType.B;
            else
                v.S.PictType = (v.Fptype & 1) != 0 ? AvPictureType.P : AvPictureType.I;
            // The C also writes this picture type into s.cur_pic.ptr->f, which is the AVFrame
            // the decoder will output. There is no AVFrame here and no picture allocated at
            // header-parse time; the driver copies v.S.PictType into the output picture.
            if (v.PicHeaderFlag == 0)
                return ParseCommonInfo(v, ref gb);
        }

        fieldMode = 0;
        FrameCodingMode fcm;
        if (v.Interlace != 0)
        {
            fcm = (FrameCodingMode)gb.Decode012();
            if (fcm != FrameCodingMode.Progressive && fcm == FrameCodingMode.IlaceField)
                fieldMode = 1;
        }
        else
        {
            fcm = FrameCodingMode.Progressive;
        }
        if (v.FirstPicHeaderFlag == 0 && v.FieldMode != fieldMode)
            return AverrorInvalidData;
        v.FieldMode = fieldMode;
        v.Fcm = fcm;

        if (v.S.MbHeight != (v.S.Height + 15 >> 4) &&
            v.S.MbHeight != ((v.S.Height + 15 >> 4) + 1 & ~1))
        {
            throw new InvalidOperationException(
                $"mb_height {v.S.MbHeight} matches neither of the two shapes a VC-1 picture of "
                + $"height {v.S.Height} can have (av_assert0 in ff_vc1_parse_frame_header_adv)");
        }
        if (v.FieldMode != 0)
        {
            v.S.MbHeight = (v.S.Height + 15 >> 4) + 1 & ~1;  // FFALIGN(..., 2)
            v.Fptype = (int)gb.GetBits(3);
            if ((v.Fptype & 4) != 0)  // B-picture
                v.S.PictType = (v.Fptype & 2) != 0 ? AvPictureType.Bi : AvPictureType.B;
            else
                v.S.PictType = (v.Fptype & 2) != 0 ? AvPictureType.P : AvPictureType.I;
        }
        else
        {
            v.S.MbHeight = v.S.Height + 15 >> 4;
            switch (gb.GetUnary(0, 4))
            {
                case 0:
                    v.S.PictType = AvPictureType.P;
                    break;
                case 1:
                    v.S.PictType = AvPictureType.B;
                    break;
                case 2:
                    v.S.PictType = AvPictureType.I;
                    break;
                case 3:
                    v.S.PictType = AvPictureType.Bi;
                    break;
                case 4:
                    v.S.PictType = AvPictureType.P;  // skipped pic
                    v.PFrameSkipped = 1;
                    break;
                default:
                    break;
            }
        }
        if (v.Tfcntrflag != 0)
            gb.SkipBits(8);
        if (v.Broadcast != 0)
        {
            if (v.Interlace == 0 || v.Psf != 0)
            {
                v.Rptfrm = (byte)gb.GetBits(2);
            }
            else
            {
                v.Tff = (byte)gb.GetBits1();
                v.Rff = (byte)gb.GetBits1();
            }
        }
        else
        {
            v.Tff = 1;
        }
        if (v.Panscanflag != 0)
        {
            // avpriv_report_missing_feature("Pan-scan"): the C reads no bits for it either, and
            // its own comment is "//...".
        }
        if (v.PFrameSkipped != 0)
            return 0;
        v.Rnd = (int)gb.GetBits1();
        if (v.Interlace != 0)
            v.Uvsamp = (byte)gb.GetBits1();
        if (v.FieldMode != 0)
        {
            if (v.RefdistFlag == 0)
            {
                v.Refdist = 0;
            }
            else if (v.S.PictType != AvPictureType.B && v.S.PictType != AvPictureType.Bi)
            {
                v.Refdist = (int)gb.GetBits(2);
                if (v.Refdist == 3)
                    v.Refdist += gb.GetUnary(0, 14);
                if (v.Refdist > 16)
                    return AverrorInvalidData;
            }
            if (v.S.PictType == AvPictureType.B || v.S.PictType == AvPictureType.Bi)
            {
                if (ReadBfraction(v, ref gb) < 0)
                    return AverrorInvalidData;
                v.Frfd = (v.Bfraction * v.Refdist) >> 8;
                v.Brfd = v.Refdist - v.Frfd - 1;
                if (v.Brfd < 0)
                    v.Brfd = 0;
            }
            return ParseCommonInfo(v, ref gb);
        }
        if (v.Fcm == FrameCodingMode.Progressive)
        {
            if (v.Finterpflag != 0)
                v.Interpfrm = (byte)gb.GetBits1();
            if (v.S.PictType == AvPictureType.B)
            {
                if (ReadBfraction(v, ref gb) < 0)
                    return AverrorInvalidData;
                if (v.Bfraction == 0)
                    v.S.PictType = AvPictureType.Bi;  // XXX: should not happen here
            }
        }

        return ParseCommonInfo(v, ref gb);
    }

    /// <summary>
    /// The <c>parse_common_info</c> label in <c>ff_vc1_parse_frame_header_adv</c>, which three
    /// paths reach - two of them by <c>goto</c>.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE: a method rather than a label. The three jumps are all forward and none of
    /// them re-enters the code above, so the label is the tail of the function and extracting
    /// it is a restructuring the porting rules allow. Everything it reads is in
    /// <paramref name="v"/>.
    /// </remarks>
    private static int ParseCommonInfo(Vc1Context v, ref GetBitContext gb)
    {
        int pqindex, lowquant, status;

        if (v.FieldMode != 0)
            v.CurFieldType = (v.Tff ^ v.SecondField) == 0 ? 1 : 0;
        pqindex = (int)gb.GetBits(5);
        if (pqindex == 0)
            return -1;
        v.Pq = v.QuantizerMode == (int)QuantMode.FrameImplicit
            ? Vc1Data.Vc1PquantTable[pqindex]
            : Vc1Data.Vc1PquantTable[32 + pqindex];
        v.Pqindex = pqindex;
        v.Halfpq = pqindex < 9 ? (byte)gb.GetBits1() : (byte)0;
        v.Pquantizer = (QuantMode)v.QuantizerMode switch
        {
            QuantMode.FrameImplicit => (byte)(pqindex < 9 ? 1 : 0),
            QuantMode.NonUniform => 0,
            QuantMode.FrameExplicit => (byte)gb.GetBits1(),
            _ => 1,
        };
        v.Dquantfrm = 0;
        if (v.Postprocflag != 0)
            v.Postproc = (byte)gb.GetBits(2);

        if (v.ParseOnly != 0)
            return 0;

        if (v.FirstPicHeaderFlag != 0)
            RotateLuts(v);

        switch (v.S.PictType)
        {
            case AvPictureType.I:
            case AvPictureType.Bi:
                if (v.Fcm == FrameCodingMode.IlaceFrame)  // interlace frame picture
                {
                    status = BitplaneDecoding(v.FieldtxPlane, out v.FieldtxIsRaw, v);
                    if (status < 0)
                        return -1;
                }
                else
                {
                    v.FieldtxIsRaw = 0;
                }
                status = BitplaneDecoding(v.AcpredPlane, out v.AcpredIsRaw, v);
                if (status < 0)
                    return -1;
                v.Condover = (byte)CoTypes.None;
                if (v.Overlap != 0 && v.Pq <= 8)
                {
                    v.Condover = (byte)gb.Decode012();
                    if (v.Condover == (byte)CoTypes.Select)
                    {
                        status = BitplaneDecoding(v.OverFlagsPlane, out v.OverflgIsRaw, v);
                        if (status < 0)
                            return -1;
                    }
                }
                break;

            case AvPictureType.P:
                if (v.FieldMode != 0)
                {
                    v.Numref = (int)gb.GetBits1();
                    if (v.Numref == 0)
                    {
                        v.Reffield = (int)gb.GetBits1();
                        v.RefFieldType[0] = v.Reffield ^ (v.CurFieldType != 0 ? 0 : 1);
                    }
                }
                v.Mvrange = v.ExtendedMv != 0 ? (byte)gb.GetUnary(0, 3) : (byte)0;
                if (v.Interlace != 0)
                {
                    v.Dmvrange = v.ExtendedDmv != 0 ? (byte)gb.GetUnary(0, 3) : (byte)0;
                    if (v.Fcm == FrameCodingMode.IlaceFrame)  // interlaced frame picture
                    {
                        v.Fourmvswitch = (int)gb.GetBits1();
                        v.Intcomp = (int)gb.GetBits1();
                        if (v.Intcomp != 0)
                        {
                            v.Lumscale = (byte)gb.GetBits(6);
                            v.Lumshift = (byte)gb.GetBits(6);
                            InitLut(v.Lumscale, v.Lumshift,
                                    v.LastLuty.AsSpan(0, 256), v.LastLutuv.AsSpan(0, 256), 1);
                            InitLut(v.Lumscale, v.Lumshift,
                                    v.LastLuty.AsSpan(256, 256), v.LastLutuv.AsSpan(256, 256), 1);
                            v.LastUseIc = 1;
                        }
                        status = BitplaneDecoding(v.S.MbskipTable, out v.SkipIsRaw, v);
                        if (status < 0)
                            return -1;
                        v.Mbmodetab = (int)gb.GetBits(2);
                        v.MbmodeVlc = v.Fourmvswitch != 0
                            ? Vc1Data.Vc1Intfr4mvMbmodeVlc[v.Mbmodetab]
                            : Vc1Data.Vc1IntfrNon4mvMbmodeVlc[v.Mbmodetab];
                        v.Imvtab = (int)gb.GetBits(2);
                        v.ImvVlc = Vc1Data.Vc11refMvdataVlc[v.Imvtab];
                        // Interlaced p-picture cbpcy range is [1, 63].
                        v.Icbptab = (int)gb.GetBits(3);
                        v.CbpcyVlc = Vc1Data.Vc1IcbpcyVlc[v.Icbptab];
                        v.Twomvbptab = (int)gb.GetBits(2);
                        v.TwomvbpVlc = Vc1Data.Vc12mvBlockPatternVlc[v.Twomvbptab];
                        if (v.Fourmvswitch != 0)
                        {
                            v.Fourmvbptab = (int)gb.GetBits(2);
                            v.FourmvbpVlc = Vc1Data.Vc14mvBlockPatternVlc[v.Fourmvbptab];
                        }
                    }
                }
                v.KX = v.Mvrange + 9 + (v.Mvrange >> 1);  // k_x can be 9 10 12 13
                v.KY = v.Mvrange + 8;                     // k_y can be 8 9 10 11
                v.RangeX = 1 << (v.KX - 1);
                v.RangeY = 1 << (v.KY - 1);

                v.TtIndex = (v.Pq > 4 ? 1 : 0) + (v.Pq > 12 ? 1 : 0);
                if (v.Fcm != FrameCodingMode.IlaceFrame)
                {
                    int mvmode = gb.GetUnary(1, 4);
                    lowquant = v.Pq > 12 ? 0 : 1;
                    v.MvMode = Vc1Data.Vc1MvPmodeTable[lowquant * 5 + mvmode];
                    if (v.MvMode == (byte)MvModes.IntensityComp)
                    {
                        int mvmode2 = gb.GetUnary(1, 3);
                        v.MvMode2 = Vc1Data.Vc1MvPmodeTable2[lowquant * 4 + mvmode2];
                        v.Intcompfield = v.FieldMode != 0 ? gb.Decode210() ^ 3 : 3;

                        v.Lumscale2 = v.Lumscale = 32;
                        v.Lumshift2 = v.Lumshift = 0;
                        if ((v.Intcompfield & 1) != 0)
                        {
                            v.Lumscale = (byte)gb.GetBits(6);
                            v.Lumshift = (byte)gb.GetBits(6);
                        }
                        if ((v.Intcompfield & 2) != 0 && v.FieldMode != 0)
                        {
                            v.Lumscale2 = (byte)gb.GetBits(6);
                            v.Lumshift2 = (byte)gb.GetBits(6);
                        }
                        else if (v.FieldMode == 0)
                        {
                            v.Lumscale2 = v.Lumscale;
                            v.Lumshift2 = v.Lumshift;
                        }
                        if (v.FieldMode != 0 && v.SecondField != 0)
                        {
                            int other = v.CurFieldType ^ 1;
                            if (v.CurFieldType != 0)
                            {
                                InitLut(v.Lumscale, v.Lumshift,
                                        v.CurrLuty.AsSpan(other * 256, 256),
                                        v.CurrLutuv.AsSpan(other * 256, 256), 0);
                                InitLut(v.Lumscale2, v.Lumshift2,
                                        v.LastLuty.AsSpan(v.CurFieldType * 256, 256),
                                        v.LastLutuv.AsSpan(v.CurFieldType * 256, 256), 1);
                            }
                            else
                            {
                                InitLut(v.Lumscale2, v.Lumshift2,
                                        v.CurrLuty.AsSpan(other * 256, 256),
                                        v.CurrLutuv.AsSpan(other * 256, 256), 0);
                                InitLut(v.Lumscale, v.Lumshift,
                                        v.LastLuty.AsSpan(v.CurFieldType * 256, 256),
                                        v.LastLutuv.AsSpan(v.CurFieldType * 256, 256), 1);
                            }
                            v.NextUseIc = 1;
                            v.CurrUseIc = 1;
                        }
                        else
                        {
                            InitLut(v.Lumscale, v.Lumshift,
                                    v.LastLuty.AsSpan(0, 256), v.LastLutuv.AsSpan(0, 256), 1);
                            InitLut(v.Lumscale2, v.Lumshift2,
                                    v.LastLuty.AsSpan(256, 256), v.LastLutuv.AsSpan(256, 256), 1);
                        }
                        v.LastUseIc = 1;
                    }
                    if (v.MvMode == (byte)MvModes.IntensityComp)
                    {
                        v.S.QuarterSample = v.MvMode2 != (byte)MvModes.OneMvHpel &&
                                            v.MvMode2 != (byte)MvModes.OneMvHpelBilin ? 1 : 0;
                        v.S.Mspel = v.MvMode2 != (byte)MvModes.OneMvHpelBilin ? 1 : 0;
                    }
                    else
                    {
                        v.S.QuarterSample = v.MvMode != (byte)MvModes.OneMvHpel &&
                                            v.MvMode != (byte)MvModes.OneMvHpelBilin ? 1 : 0;
                        v.S.Mspel = v.MvMode != (byte)MvModes.OneMvHpelBilin ? 1 : 0;
                    }
                }
                if (v.Fcm == FrameCodingMode.Progressive)  // progressive
                {
                    if ((v.MvMode == (byte)MvModes.IntensityComp &&
                         v.MvMode2 == (byte)MvModes.MixedMv) ||
                        v.MvMode == (byte)MvModes.MixedMv)
                    {
                        status = BitplaneDecoding(v.MvTypeMbPlane, out v.MvTypeIsRaw, v);
                        if (status < 0)
                            return -1;
                    }
                    else
                    {
                        v.MvTypeIsRaw = 0;
                        v.MvTypeMbPlane.AsSpan(0, v.S.MbStride * v.S.MbHeight).Clear();
                    }
                    status = BitplaneDecoding(v.S.MbskipTable, out v.SkipIsRaw, v);
                    if (status < 0)
                        return -1;

                    // Hopefully this is correct for P-frames.
                    v.MvTableIndex = (int)gb.GetBits(2);  // but using ff_vc1_ tables
                    v.Cbptab = (int)gb.GetBits(2);
                    v.CbpcyVlc = Vc1Data.Vc1CbpcyPVlc[v.Cbptab];
                }
                else if (v.Fcm == FrameCodingMode.IlaceFrame)  // frame interlaced
                {
                    v.S.QuarterSample = 1;
                    v.S.Mspel = 1;
                }
                else
                {   // field interlaced
                    v.Mbmodetab = (int)gb.GetBits(3);
                    v.Imvtab = (int)gb.GetBits(2 + v.Numref);
                    v.ImvVlc = v.Numref == 0
                        ? Vc1Data.Vc11refMvdataVlc[v.Imvtab]
                        : Vc1Data.Vc12refMvdataVlc[v.Imvtab];
                    v.Icbptab = (int)gb.GetBits(3);
                    v.CbpcyVlc = Vc1Data.Vc1IcbpcyVlc[v.Icbptab];
                    if ((v.MvMode == (byte)MvModes.IntensityComp &&
                         v.MvMode2 == (byte)MvModes.MixedMv) ||
                        v.MvMode == (byte)MvModes.MixedMv)
                    {
                        v.Fourmvbptab = (int)gb.GetBits(2);
                        v.FourmvbpVlc = Vc1Data.Vc14mvBlockPatternVlc[v.Fourmvbptab];
                        v.MbmodeVlc = Vc1Data.Vc1IfMmvMbmodeVlc[v.Mbmodetab];
                    }
                    else
                    {
                        v.MbmodeVlc = Vc1Data.Vc1If1mvMbmodeVlc[v.Mbmodetab];
                    }
                }
                if (v.Dquant != 0)
                    VopDquantDecoding(v);

                ReadTransformType(v, ref gb);
                break;

            case AvPictureType.B:
                if (v.Fcm == FrameCodingMode.IlaceFrame)
                {
                    if (ReadBfraction(v, ref gb) < 0)
                        return AverrorInvalidData;
                    if (v.Bfraction == 0)
                        return -1;
                }
                v.Mvrange = v.ExtendedMv != 0 ? (byte)gb.GetUnary(0, 3) : (byte)0;
                v.KX = v.Mvrange + 9 + (v.Mvrange >> 1);  // k_x can be 9 10 12 13
                v.KY = v.Mvrange + 8;                     // k_y can be 8 9 10 11
                v.RangeX = 1 << (v.KX - 1);
                v.RangeY = 1 << (v.KY - 1);

                v.TtIndex = (v.Pq > 4 ? 1 : 0) + (v.Pq > 12 ? 1 : 0);

                if (v.FieldMode != 0)
                {
                    if (v.ExtendedDmv != 0)
                        v.Dmvrange = (byte)gb.GetUnary(0, 3);
                    int mvmode = gb.GetUnary(1, 3);
                    lowquant = v.Pq > 12 ? 0 : 1;
                    v.MvMode = Vc1Data.Vc1MvPmodeTable2[lowquant * 4 + mvmode];
                    v.S.QuarterSample = v.MvMode == (byte)MvModes.OneMv ||
                                        v.MvMode == (byte)MvModes.MixedMv ? 1 : 0;
                    v.S.Mspel = v.MvMode != (byte)MvModes.OneMvHpelBilin ? 1 : 0;
                    status = BitplaneDecoding(v.ForwardMbPlane, out v.FmbIsRaw, v);
                    if (status < 0)
                        return -1;
                    v.Mbmodetab = (int)gb.GetBits(3);
                    v.MbmodeVlc = v.MvMode == (byte)MvModes.MixedMv
                        ? Vc1Data.Vc1IfMmvMbmodeVlc[v.Mbmodetab]
                        : Vc1Data.Vc1If1mvMbmodeVlc[v.Mbmodetab];
                    v.Imvtab = (int)gb.GetBits(3);
                    v.ImvVlc = Vc1Data.Vc12refMvdataVlc[v.Imvtab];
                    v.Icbptab = (int)gb.GetBits(3);
                    v.CbpcyVlc = Vc1Data.Vc1IcbpcyVlc[v.Icbptab];
                    if (v.MvMode == (byte)MvModes.MixedMv)
                    {
                        v.Fourmvbptab = (int)gb.GetBits(2);
                        v.FourmvbpVlc = Vc1Data.Vc14mvBlockPatternVlc[v.Fourmvbptab];
                    }
                    v.Numref = 1;  // interlaced field B pictures are always 2-ref
                }
                else if (v.Fcm == FrameCodingMode.IlaceFrame)
                {
                    if (v.ExtendedDmv != 0)
                        v.Dmvrange = (byte)gb.GetUnary(0, 3);
                    // intcomp - present but shall always be 0; ffmpeg warns and continues.
                    gb.SkipBits1();
                    v.Intcomp = 0;
                    v.MvMode = (byte)MvModes.OneMv;
                    v.Fourmvswitch = 0;
                    v.S.QuarterSample = 1;
                    v.S.Mspel = 1;
                    status = BitplaneDecoding(v.DirectMbPlane, out v.DmbIsRaw, v);
                    if (status < 0)
                        return -1;
                    status = BitplaneDecoding(v.S.MbskipTable, out v.SkipIsRaw, v);
                    if (status < 0)
                        return -1;
                    v.Mbmodetab = (int)gb.GetBits(2);
                    v.MbmodeVlc = Vc1Data.Vc1IntfrNon4mvMbmodeVlc[v.Mbmodetab];
                    v.Imvtab = (int)gb.GetBits(2);
                    v.ImvVlc = Vc1Data.Vc11refMvdataVlc[v.Imvtab];
                    // Interlaced p/b-picture cbpcy range is [1, 63].
                    v.Icbptab = (int)gb.GetBits(3);
                    v.CbpcyVlc = Vc1Data.Vc1IcbpcyVlc[v.Icbptab];
                    v.Twomvbptab = (int)gb.GetBits(2);
                    v.TwomvbpVlc = Vc1Data.Vc12mvBlockPatternVlc[v.Twomvbptab];
                    v.Fourmvbptab = (int)gb.GetBits(2);
                    v.FourmvbpVlc = Vc1Data.Vc14mvBlockPatternVlc[v.Fourmvbptab];
                }
                else
                {
                    v.MvMode = gb.GetBits1() != 0
                        ? (byte)MvModes.OneMv : (byte)MvModes.OneMvHpelBilin;
                    v.S.QuarterSample = v.MvMode == (byte)MvModes.OneMv ? 1 : 0;
                    v.S.Mspel = v.S.QuarterSample;
                    status = BitplaneDecoding(v.DirectMbPlane, out v.DmbIsRaw, v);
                    if (status < 0)
                        return -1;
                    status = BitplaneDecoding(v.S.MbskipTable, out v.SkipIsRaw, v);
                    if (status < 0)
                        return -1;
                    v.MvTableIndex = (int)gb.GetBits(2);
                    v.Cbptab = (int)gb.GetBits(2);
                    v.CbpcyVlc = Vc1Data.Vc1CbpcyPVlc[v.Cbptab];
                }

                if (v.Dquant != 0)
                    VopDquantDecoding(v);

                ReadTransformType(v, ref gb);
                break;

            default:
                break;
        }

        // AC Syntax.
        v.CAcTableIndex = gb.Decode012();
        if (v.S.PictType == AvPictureType.I || v.S.PictType == AvPictureType.Bi)
        {
            v.YAcTableIndex = gb.Decode012();
        }
        else if (v.Fcm != FrameCodingMode.Progressive && v.S.QuarterSample == 0)
        {
            v.RangeX <<= 1;
            v.RangeY <<= 1;
        }

        // DC Syntax.
        v.DcTableIndex = (int)gb.GetBits1();
        if ((v.S.PictType == AvPictureType.I || v.S.PictType == AvPictureType.Bi) &&
            v.Dquant != 0)
        {
            VopDquantDecoding(v);
        }

        v.BiType = v.S.PictType == AvPictureType.Bi ? 1 : 0;
        if (v.BiType != 0)
            v.S.PictType = AvPictureType.B;

        return 0;
    }

    /// <summary>
    /// The TTMBF/TTFRM pair, which both picture headers read identically at four places.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE: the C repeats these nine lines four times, once per picture type per parser,
    /// with its own "FIXME Is that so ?" on the TTFRM of two of them. One method, because four
    /// copies of nine lines is where a transcription error hides.
    /// </remarks>
    private static void ReadTransformType(Vc1Context v, ref GetBitContext gb)
    {
        if (v.Vstransform != 0)
        {
            v.Ttmbf = (byte)gb.GetBits1();
            if (v.Ttmbf != 0)
                v.Ttfrm = Vc1Data.Vc1TtfrmToTt[gb.GetBits(2)];
            else
                v.Ttfrm = 0;  // FIXME Is that so ?
        }
        else
        {
            v.Ttmbf = 1;
            v.Ttfrm = (int)TransformType.Tt8X8;
        }
    }
}

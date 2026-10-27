// VC-1's VLC storage and table widths - the hand-written half of a translation of
// ffmpeg-master/libavcodec/vc1data.c. The data tables are generated into Vc1Data.Tables.cs by
// scripts/gen_vc1_data.py; this file holds what the C has besides tables, which is the storage
// vc1_init_static fills and the widths it fills it at.
//
// The widths are #defines in vc1data.h and vc1.h rather than entries of either file's data, so
// they live here with the storage they size.

using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Vc1Video;

public static partial class Vc1Data
{
    /// <summary><c>VC1_IMODE_VLC_BITS</c> (vc1data.h).</summary>
    public const int ImodeVlcBits = 4;

    /// <summary><c>VC1_NORM2_VLC_BITS</c>.</summary>
    public const int Norm2VlcBits = 3;

    /// <summary><c>VC1_NORM6_VLC_BITS</c>.</summary>
    public const int Norm6VlcBits = 9;

    /// <summary><c>VC1_TTMB_VLC_BITS</c>.</summary>
    public const int TtmbVlcBits = 9;

    /// <summary><c>VC1_MV_DIFF_VLC_BITS</c>.</summary>
    public const int MvDiffVlcBits = 9;

    /// <summary><c>VC1_CBPCY_P_VLC_BITS</c>.</summary>
    public const int CbpcyPVlcBits = 9;

    /// <summary><c>VC1_ICBPCY_VLC_BITS</c>.</summary>
    public const int IcbpcyVlcBits = 9;

    /// <summary><c>VC1_4MV_BLOCK_PATTERN_VLC_BITS</c>.</summary>
    public const int FourMvBlockPatternVlcBits = 6;

    /// <summary><c>VC1_2MV_BLOCK_PATTERN_VLC_BITS</c>.</summary>
    public const int TwoMvBlockPatternVlcBits = 3;

    /// <summary><c>VC1_TTBLK_VLC_BITS</c>.</summary>
    public const int TtblkVlcBits = 5;

    /// <summary><c>VC1_SUBBLKPAT_VLC_BITS</c>.</summary>
    public const int SubblkpatVlcBits = 6;

    /// <summary><c>VC1_INTFR_4MV_MBMODE_VLC_BITS</c>.</summary>
    public const int Intfr4MvMbmodeVlcBits = 9;

    /// <summary><c>VC1_INTFR_NON4MV_MBMODE_VLC_BITS</c>.</summary>
    public const int IntfrNon4MvMbmodeVlcBits = 6;

    /// <summary><c>VC1_IF_MMV_MBMODE_VLC_BITS</c>.</summary>
    public const int IfMmvMbmodeVlcBits = 5;

    /// <summary><c>VC1_IF_1MV_MBMODE_VLC_BITS</c>.</summary>
    public const int If1MvMbmodeVlcBits = 5;

    /// <summary><c>VC1_1REF_MVDATA_VLC_BITS</c>.</summary>
    public const int OneRefMvdataVlcBits = 9;

    /// <summary><c>VC1_2REF_MVDATA_VLC_BITS</c>.</summary>
    public const int TwoRefMvdataVlcBits = 9;

    /// <summary><c>AC_VLC_BITS</c> (vc1.h).</summary>
    public const int AcVlcBits = 9;

    /// <summary><c>AC_MODES</c> (vc1data.h) - how many AC coding sets there are.</summary>
    public const int AcModes = 8;

    /// <summary><c>ff_vc1_imode_vlc</c>.</summary>
    public static readonly VLCElem[] Vc1ImodeVlc = new VLCElem[1 << ImodeVlcBits];

    /// <summary><c>ff_vc1_norm2_vlc</c>.</summary>
    public static readonly VLCElem[] Vc1Norm2Vlc = new VLCElem[1 << Norm2VlcBits];

    /// <summary>
    /// <c>ff_vc1_norm6_vlc</c>. The 556 is the C's, and is the one table size in VC-1 that is
    /// written down there: the rest come out of the pool and are sized by the builder.
    /// </summary>
    public static readonly VLCElem[] Vc1Norm6Vlc = new VLCElem[556];

    /// <summary><c>ff_vc1_ttmb_vlc</c>.</summary>
    public static readonly VLCElem[][] Vc1TtmbVlc = new VLCElem[3][];

    /// <summary><c>ff_vc1_mv_diff_vlc</c>.</summary>
    public static readonly VLCElem[][] Vc1MvDiffVlc = new VLCElem[4][];

    /// <summary><c>ff_vc1_cbpcy_p_vlc</c>.</summary>
    public static readonly VLCElem[][] Vc1CbpcyPVlc = new VLCElem[4][];

    /// <summary><c>ff_vc1_icbpcy_vlc</c>.</summary>
    public static readonly VLCElem[][] Vc1IcbpcyVlc = new VLCElem[8][];

    /// <summary><c>ff_vc1_4mv_block_pattern_vlc</c>.</summary>
    public static readonly VLCElem[][] Vc14mvBlockPatternVlc = new VLCElem[4][];

    /// <summary><c>ff_vc1_2mv_block_pattern_vlc</c>.</summary>
    public static readonly VLCElem[][] Vc12mvBlockPatternVlc = new VLCElem[4][];

    /// <summary><c>ff_vc1_ttblk_vlc</c>.</summary>
    public static readonly VLCElem[][] Vc1TtblkVlc = new VLCElem[3][];

    /// <summary><c>ff_vc1_subblkpat_vlc</c>.</summary>
    public static readonly VLCElem[][] Vc1SubblkpatVlc = new VLCElem[3][];

    /// <summary><c>ff_vc1_intfr_4mv_mbmode_vlc</c>.</summary>
    public static readonly VLCElem[][] Vc1Intfr4mvMbmodeVlc = new VLCElem[4][];

    /// <summary><c>ff_vc1_intfr_non4mv_mbmode_vlc</c>.</summary>
    public static readonly VLCElem[][] Vc1IntfrNon4mvMbmodeVlc = new VLCElem[4][];

    /// <summary><c>ff_vc1_if_mmv_mbmode_vlc</c>.</summary>
    public static readonly VLCElem[][] Vc1IfMmvMbmodeVlc = new VLCElem[8][];

    /// <summary><c>ff_vc1_if_1mv_mbmode_vlc</c>.</summary>
    public static readonly VLCElem[][] Vc1If1mvMbmodeVlc = new VLCElem[8][];

    /// <summary><c>ff_vc1_1ref_mvdata_vlc</c>.</summary>
    public static readonly VLCElem[][] Vc11refMvdataVlc = new VLCElem[4][];

    /// <summary><c>ff_vc1_2ref_mvdata_vlc</c>.</summary>
    public static readonly VLCElem[][] Vc12refMvdataVlc = new VLCElem[8][];

    /// <summary><c>ff_vc1_ac_coeff_table</c>.</summary>
    public static readonly VLCElem[][] Vc1AcCoeffTable = new VLCElem[8][];
}

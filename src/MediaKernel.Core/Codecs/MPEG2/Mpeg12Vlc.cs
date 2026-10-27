// MPEG-1/2 VLC constants - a translation of ffmpeg-master/libavcodec/mpeg12vlc.h.
//
// The VLC storage arrays the header declares (ff_dc_lum_vlc and the rest) are defined in
// mpeg12.c and are built at runtime, so they are translated with that file; these are the
// compile-time bit widths and the run/level element count, which the tables and the readers
// both need.

namespace MediaKernel.Core.Codecs.Mpeg2;

/// <summary>MPEG-1/2 VLC bit widths (<c>mpeg12vlc.h</c>).</summary>
/// <remarks>
/// Each width is the number of bits a VLC reader peeks for its first-level lookup. They are
/// FFmpeg's values and are not free to change: the generated tables are sized by them, so a
/// different width here and the same tables there is a silent mis-decode rather than a build
/// error.
/// </remarks>
public static class Mpeg12Vlc
{
    /// <summary><c>DC_VLC_BITS</c>.</summary>
    public const int DcVlcBits = 9;

    /// <summary><c>MV_VLC_BITS</c>.</summary>
    public const int MvVlcBits = 8;

    /// <summary><c>TEX_VLC_BITS</c>.</summary>
    public const int TexVlcBits = 9;

    /// <summary><c>MBINCR_VLC_BITS</c>.</summary>
    public const int MbIncrVlcBits = 9;

    /// <summary><c>MB_PAT_VLC_BITS</c>.</summary>
    public const int MbPatVlcBits = 9;

    /// <summary><c>MB_PTYPE_VLC_BITS</c>.</summary>
    public const int MbPTypeVlcBits = 6;

    /// <summary><c>MB_BTYPE_VLC_BITS</c>.</summary>
    public const int MbBTypeVlcBits = 6;

    /// <summary>
    /// <c>MPEG12_RL_NB_ELEMS</c> - the number of run/level pairs in the MPEG-1/2 coefficient
    /// tables, which is why <c>ff_mpeg12_level</c> and <c>ff_mpeg12_run</c> are 111 entries and
    /// the two VLC tables are 113 rows (the extra two being escape and end-of-block).
    /// </summary>
    public const int RunLevelElements = 111;
}

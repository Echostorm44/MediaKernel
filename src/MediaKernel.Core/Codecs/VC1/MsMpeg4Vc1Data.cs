// The VLC tables MSMPEG-4 and VC-1 share, and the builder that fills them - the hand-written
// half of a translation of ffmpeg-master/libavcodec/msmpeg4_vc1_data.c. The data tables are
// generated into MsMpeg4Vc1Data.Tables.cs by scripts/gen_vc1_data.py.

using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Vc1Video;

public static partial class MsMpeg4Vc1Data
{
    /// <summary><c>MSMP4_MB_INTRA_VLC_BITS</c> (msmpeg4_vc1_data.h).</summary>
    public const int MbIntraVlcBits = 9;

    /// <summary><c>MSMP4_DC_VLC_BITS</c>.</summary>
    public const int DcVlcBits = 9;

    /// <summary><c>ff_msmp4_mb_i_vlc</c>.</summary>
    public static readonly VLCElem[] Msmp4MbIVlc = new VLCElem[536];

    /// <summary>
    /// <c>ff_msmp4_dc_vlc</c> - flattened from [2][2], stride 2: the first index is the DC table
    /// the picture header selects, the second is luma then chroma.
    /// </summary>
    public static readonly VLCElem[][] Msmp4DcVlc = new VLCElem[2 * 2][];

    private static readonly object initLock = new();
    private static bool initialised;

    /// <summary>
    /// <c>msmp4_vc1_vlcs_init</c> and <c>ff_msmp4_vc1_vlcs_init_once</c> - the four DC books and
    /// the intra coded-block-pattern book, built once.
    /// </summary>
    /// <remarks>
    /// The C separates the two: <c>ff_msmp4_vc1_vlcs_init_once</c> is the <c>ff_thread_once</c>
    /// wrapper and <c>msmp4_vc1_vlcs_init</c> the body. A lock and a flag do both jobs here, as
    /// the other static initialisers in this port already do, so the pair is one method.
    /// </remarks>
    public static void Msmp4Vc1VlcsInitOnce()
    {
        lock (initLock)
        {
            if (initialised)
                return;

            // &ff_msmp4_dc_tables[i][j][0][1] with a wrap of 8 and a size of 4 is the length
            // column of a [120][2] block of uint32_t; the row stride is 240 entries.
            uint[] dc = Msmp4DcTables;
            for (int i = 0; i < 2; i++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int bas = (i * 2 + j) * 120 * 2;
                    Msmp4DcVlc[i * 2 + j] = Vlc.InitTablesSparse(
                        DcVlcBits, 120,
                        k => (int)dc[bas + 2 * k + 1], k => dc[bas + 2 * k]);
                }
            }

            ushort[] mbI = Msmp4MbITable;
            Vlc.InitTableSparse(Msmp4MbIVlc, MbIntraVlcBits, 64,
                                k => mbI[2 * k + 1], k => mbI[2 * k]);

            initialised = true;
        }
    }
}

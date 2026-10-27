// DTS Coherent Acoustics - port of ffmpeg-master/libavcodec/dcahuff.c.
//
// All 150-odd DTS VLC tables come out of one flat array of (length, symbol) pairs, consumed in
// order. The order is the whole interface: ff_dca_vlc_src_tables holds nothing to say where one
// table ends and the next begins, so the sequence of DCA_INIT_VLC calls below IS the table
// layout, and a table built with the wrong code count silently shifts every table after it.

using System;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Audio.Dts;

/// <summary><c>dcahuff.c</c> - the DTS VLC tables, built once.</summary>
internal static class DcaHuff
{
    public const int TmodeVlcBits = 3;
    public const int ScalesVlcBits = 9;
    public const int TnlGrpVlcBits = 9;
    public const int TnlScfVlcBits = 9;
    public const int DampVlcBits = 6;
    public const int DphVlcBits = 6;
    public const int FstRsdVlcBits = 9;
    public const int RsdApprxVlcBits = 5;
    public const int RsdAmpVlcBits = 9;
    public const int AvgG3VlcBits = 9;
    public const int StGridVlcBits = 9;
    public const int GridVlcBits = 9;
    public const int RsdVlcBits = 6;

    /// <summary><c>ff_dca_vlc_quant_index[DCA_CODE_BOOKS][7]</c>.</summary>
    public static VLCElem[][][] QuantIndex { get; } = new VLCElem[DcaLimits.CodeBooks][][];

    /// <summary>
    /// The first-level width each quant index table was built with,
    /// <c>bitalloc_maxbits[book][group]</c>; <see cref="GetBitContext.GetVlc2"/> needs it again
    /// at decode time.
    /// </summary>
    public static byte[][] QuantIndexBits { get; } = new byte[DcaLimits.CodeBooks][];

    /// <summary><c>ff_dca_vlc_bit_allocation[5]</c>.</summary>
    public static VLCElem[][] BitAllocation { get; } = new VLCElem[DcaLimits.Bitalloc12Count][];

    /// <summary>The first-level width of each <see cref="BitAllocation"/> table.</summary>
    public static byte[] BitAllocationBits { get; } = new byte[DcaLimits.Bitalloc12Count];

    /// <summary><c>ff_dca_vlc_scale_factor[5]</c>.</summary>
    public static VLCElem[][] ScaleFactor { get; } = new VLCElem[5][];

    /// <summary><c>ff_dca_vlc_transition_mode[4]</c>.</summary>
    public static VLCElem[][] TransitionMode { get; } = new VLCElem[4][];

    // ---- The LBR tables ------------------------------------------------------------------
    //
    // These sixteen are built with VLC_INIT_LE, because dca_lbr.c begins
    // `#define BITSTREAM_READER_LE`. They are laid out bit-reversed and must be read with
    // GetBitContextLe and nothing else; a BE reader over them decodes the short codes of a
    // well-formed stream often enough to look plausible. See Vlc.InitFromLengths.

    /// <summary><c>ff_dca_vlc_tnl_grp[5]</c>.</summary>
    public static VLCElem[][] TnlGrp { get; } = new VLCElem[5][];

    /// <summary><c>ff_dca_vlc_tnl_scf</c>.</summary>
    public static VLCElem[] TnlScf { get; private set; } = [];

    /// <summary><c>ff_dca_vlc_damp</c>.</summary>
    public static VLCElem[] Damp { get; private set; } = [];

    /// <summary><c>ff_dca_vlc_dph</c>.</summary>
    public static VLCElem[] Dph { get; private set; } = [];

    /// <summary><c>ff_dca_vlc_fst_rsd_amp</c>.</summary>
    public static VLCElem[] FstRsdAmp { get; private set; } = [];

    /// <summary><c>ff_dca_vlc_rsd_apprx</c>.</summary>
    public static VLCElem[] RsdApprx { get; private set; } = [];

    /// <summary><c>ff_dca_vlc_rsd_amp</c>.</summary>
    public static VLCElem[] RsdAmp { get; private set; } = [];

    /// <summary><c>ff_dca_vlc_avg_g3</c>.</summary>
    public static VLCElem[] AvgG3 { get; private set; } = [];

    /// <summary><c>ff_dca_vlc_st_grid</c>.</summary>
    public static VLCElem[] StGrid { get; private set; } = [];

    /// <summary><c>ff_dca_vlc_grid_2</c>.</summary>
    public static VLCElem[] Grid2 { get; private set; } = [];

    /// <summary><c>ff_dca_vlc_grid_3</c>.</summary>
    public static VLCElem[] Grid3 { get; private set; } = [];

    /// <summary><c>ff_dca_vlc_rsd</c>.</summary>
    public static VLCElem[] Rsd { get; private set; } = [];

    /// <summary><c>tnl_grp_sizes</c> - the code count of each tnl_grp table.</summary>
    public static ReadOnlySpan<int> TnlGrpSizes => [37, 34, 31, 28, 23];

    /// <summary>
    /// How many source pairs the LBR tables consume, after <see cref="CoreSrcTablePairs"/>.
    /// </summary>
    public const int LbrSrcTablePairs =
        37 + 34 + 31 + 28 + 23      // tnl_grp
        + 20 + 7 + 9 + 24 + 6 + 33 + 18 + 22 + 20 + 13 + 9;

    /// <summary>
    /// How many (length, symbol) pairs of <c>ff_dca_vlc_src_tables</c> the core's tables consume.
    /// </summary>
    /// <remarks>
    /// Recorded as a constant and asserted, not just computed, because it is the one number that
    /// says the core read its own share of the array and left the LBR tables where LBR expects
    /// them. From the C's own loops: the quant index books take
    /// <c>sum over books of group_size[i] * bitalloc_sizes[i]</c>, then 5 x 12 bit allocation,
    /// 5 x 129 scale factor and 4 x 4 transition mode.
    /// </remarks>
    public const int CoreSrcTablePairs = 1988 + 5 * 12 + 5 * 129 + 4 * 4;

    private static readonly object InitLock = new();
    private static bool initialised;

    /// <summary>
    /// <c>ff_dca_init_vlcs</c> - build every DTS core VLC table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DIVERGENCE, recorded: FFmpeg carves all of these out of one <c>VLCElem[30218]</c> whose
    /// size is in its source, advancing an offset by each table's size. Nothing here has that
    /// array - <see cref="Vlc.InitFromLengths"/> grows each table instead - so the tables are
    /// separate arrays and the offset is only into the source pair list. The consumption order
    /// is unchanged, which is the part that matters.
    /// </para>
    /// <para>
    /// The LBR tables ARE built here, after the core's and in the C's order, because they share
    /// the one source pair array and reading them out of order would start every table after
    /// the first at the wrong offset. They are the ones built with <c>VLC_INIT_LE</c>.
    /// <see cref="CoreSrcTablePairs"/> is asserted between the two groups so that a change to
    /// the core's tables cannot silently shift LBR's.
    /// </para>
    /// </remarks>
    public static void InitVlcs()
    {
        if (initialised)
            return;

        lock (InitLock)
        {
            if (initialised)
                return;

            ReadOnlySpan<byte> src = DcaData.VlcSrcTables;
            int offset = 0;

            // One VLC per (book, group). The group count and the code count per book are table
            // driven; bitalloc_maxbits gives the first-level width.
            for (int i = 0; i < DcaLimits.CodeBooks; i++)
            {
                int groups = DcaData.QuantIndexGroupSize[i];
                int codes = DcaData.BitallocSizes[i];
                int symbolOffset = DcaData.BitallocOffsets[i];

                QuantIndex[i] = new VLCElem[groups][];
                QuantIndexBits[i] = new byte[groups];

                for (int j = 0; j < groups; j++)
                {
                    byte bits = DcaData.BitallocMaxbits[i * DcaData.BitallocMaxbitsStride + j];
                    QuantIndexBits[i][j] = bits;
                    QuantIndex[i][j] = Build(src, offset, bits, codes, symbolOffset);
                    offset += codes;
                }
            }

            for (int i = 0; i < DcaLimits.Bitalloc12Count; i++)
            {
                byte bits = DcaData.Bitalloc12VlcBits[i];
                BitAllocationBits[i] = bits;
                BitAllocation[i] = Build(src, offset, bits, 12, 1);
                offset += 12;
            }

            for (int i = 0; i < ScaleFactor.Length; i++)
            {
                ScaleFactor[i] = Build(src, offset, ScalesVlcBits, 129, -64);
                offset += 129;
            }

            for (int i = 0; i < TransitionMode.Length; i++)
            {
                TransitionMode[i] = Build(src, offset, TmodeVlcBits, 4, 0);
                offset += 4;
            }

            if (offset != CoreSrcTablePairs)
                throw new InvalidOperationException(
                    $"DTS core VLC build consumed {offset} source pairs, expected "
                  + $"{CoreSrcTablePairs}. Every LBR table after them would start at the wrong "
                  + "offset and decode garbage that looks like audio.");

            // The LBR tables, in the C's order. The symbol offset is -1 for all of them except
            // ff_dca_vlc_rsd, which is 0 - LBR_INIT_VLC's last argument.
            for (int i = 0; i < TnlGrp.Length; i++)
            {
                TnlGrp[i] = BuildLe(src, offset, TnlGrpVlcBits, TnlGrpSizes[i], -1);
                offset += TnlGrpSizes[i];
            }

            TnlScf = BuildLe(src, offset, TnlScfVlcBits, 20, -1);
            offset += 20;
            Damp = BuildLe(src, offset, DampVlcBits, 7, -1);
            offset += 7;
            Dph = BuildLe(src, offset, DphVlcBits, 9, -1);
            offset += 9;
            FstRsdAmp = BuildLe(src, offset, FstRsdVlcBits, 24, -1);
            offset += 24;
            RsdApprx = BuildLe(src, offset, RsdApprxVlcBits, 6, -1);
            offset += 6;
            RsdAmp = BuildLe(src, offset, RsdAmpVlcBits, 33, -1);
            offset += 33;
            AvgG3 = BuildLe(src, offset, AvgG3VlcBits, 18, -1);
            offset += 18;
            StGrid = BuildLe(src, offset, StGridVlcBits, 22, -1);
            offset += 22;
            Grid2 = BuildLe(src, offset, GridVlcBits, 20, -1);
            offset += 20;
            Grid3 = BuildLe(src, offset, GridVlcBits, 13, -1);
            offset += 13;
            Rsd = BuildLe(src, offset, RsdVlcBits, 9, 0);
            offset += 9;

            if (offset != CoreSrcTablePairs + LbrSrcTablePairs)
                throw new InvalidOperationException(
                    $"DTS VLC build consumed {offset} source pairs, expected "
                  + $"{CoreSrcTablePairs + LbrSrcTablePairs}.");

            initialised = true;
        }

        static VLCElem[] Build(ReadOnlySpan<byte> src, int offset, int nbBits, int nbCodes,
                               int symbolOffset)
        {
            // Copied out because a span cannot be captured by the accessors the builder takes.
            byte[] pairs = src.Slice(offset * DcaData.VlcSrcTablesStride,
                                     nbCodes * DcaData.VlcSrcTablesStride).ToArray();
            // The order is the C's and is easy to get backwards: FFmpeg passes
            // &src_table[0][1] as the LENGTHS and &src_table[0][0] as the symbols. Reading the
            // pair the other way round builds 62 tables that are all incomplete prefix codes by
            // exactly one code, every one of which still decodes - into noise. The lengths are
            // int8_t, not uint8_t, because a negative length means "advance the code as if an
            // entry of this length were here, but emit nothing".
            return Vlc.InitFromLengths(nbBits, nbCodes,
                                       i => (sbyte)pairs[i * DcaData.VlcSrcTablesStride + 1],
                                       i => pairs[i * DcaData.VlcSrcTablesStride],
                                       symbolOffset);
        }

        static VLCElem[] BuildLe(ReadOnlySpan<byte> src, int offset, int nbBits, int nbCodes,
                                 int symbolOffset)
        {
            byte[] pairs = src.Slice(offset * DcaData.VlcSrcTablesStride,
                                     nbCodes * DcaData.VlcSrcTablesStride).ToArray();
            return Vlc.InitFromLengths(nbBits, nbCodes,
                                       i => (sbyte)pairs[i * DcaData.VlcSrcTablesStride + 1],
                                       i => pairs[i * DcaData.VlcSrcTablesStride],
                                       symbolOffset, littleEndian: true);
        }
    }
}

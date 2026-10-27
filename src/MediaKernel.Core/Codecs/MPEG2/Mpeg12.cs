// MPEG-1/2 shared decoder pieces - a translation of ffmpeg-master/libavcodec/mpeg12.c.
//
// mpeg12.c holds what MPEG-1 and MPEG-2 decoding have in common: the VLC storage, the one-time
// table construction that fills it, the run-length VLC initialiser, and the intra block reader.

using System;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Mpeg2;

/// <summary>Shared MPEG-1/2 decoding tables and helpers (<c>mpeg12.c</c>).</summary>
public static class Mpeg12
{
    /// <summary><c>MAX_LEVEL</c> from <c>rl.h</c>.</summary>
    public const int MaxLevel = 64;

    // VLC storage, sized exactly as FFmpeg declares it in mpeg12.c. The sizes are not arbitrary:
    // the builder hands out subtables from the tail of each array, so one that is too small
    // throws during construction and one that is too large silently wastes nothing but says the
    // declared size was not carried across faithfully.

    /// <summary><c>ff_mv_vlc[266]</c>.</summary>
    public static readonly VLCElem[] MvVlc = new VLCElem[266];

    /// <summary><c>ff_dc_lum_vlc[512]</c>.</summary>
    public static readonly VLCElem[] DcLumVlc = new VLCElem[512];

    /// <summary><c>ff_dc_chroma_vlc[514]</c>.</summary>
    public static readonly VLCElem[] DcChromaVlc = new VLCElem[514];

    /// <summary><c>ff_mbincr_vlc[538]</c>.</summary>
    public static readonly VLCElem[] MbIncrVlc = new VLCElem[538];

    /// <summary><c>ff_mb_ptype_vlc[64]</c>.</summary>
    public static readonly VLCElem[] MbPTypeVlc = new VLCElem[64];

    /// <summary><c>ff_mb_btype_vlc[64]</c>.</summary>
    public static readonly VLCElem[] MbBTypeVlc = new VLCElem[64];

    /// <summary><c>ff_mb_pat_vlc[512]</c>.</summary>
    public static readonly VLCElem[] MbPatVlc = new VLCElem[512];

    /// <summary><c>ff_mpeg1_rl_vlc[680]</c>.</summary>
    public static readonly VLCElem[] Mpeg1RlVlc = new VLCElem[680];

    /// <summary><c>ff_mpeg2_rl_vlc[674]</c>.</summary>
    public static readonly VLCElem[] Mpeg2RlVlc = new VLCElem[674];

    private static readonly object InitLock = new();
    private static bool initialised;

    /// <summary>
    /// <c>ff_mpeg12_init_vlcs</c> - builds every VLC table above, once.
    /// </summary>
    /// <remarks>
    /// FFmpeg guards this with <c>ff_thread_once</c>; the equivalent here is a lock and a flag,
    /// since the tables are process-wide and several decoder instances may start at once. The
    /// inner builder is <c>mpeg12_init_vlcs</c>, which is the static function this wraps.
    /// </remarks>
    public static void Mpeg12InitVlcsOnce()
    {
        if (System.Threading.Volatile.Read(ref initialised)) return;
        lock (InitLock)
        {
            if (initialised) return;
            Mpeg12InitVlcs();
            System.Threading.Volatile.Write(ref initialised, true);
        }
    }

    /// <summary><c>mpeg12_init_vlcs</c> - the table construction itself.</summary>
    private static void Mpeg12InitVlcs()
    {
        // The DC tables come as separate length and code arrays.
        byte[] dcLumBits = Mpeg12Data.Mpeg12VlcDcLumBits.ToArray();
        ushort[] dcLumCode = Mpeg12Data.Mpeg12VlcDcLumCode.ToArray();
        Vlc.InitTableSparse(MvVlcSpan(DcLumVlc), Mpeg12Vlc.DcVlcBits, 12,
                            i => dcLumBits[i], i => dcLumCode[i]);

        byte[] dcChromaBits = Mpeg12Data.Mpeg12VlcDcChromaBits.ToArray();
        ushort[] dcChromaCode = Mpeg12Data.Mpeg12VlcDcChromaCode.ToArray();
        Vlc.InitTableSparse(MvVlcSpan(DcChromaVlc), Mpeg12Vlc.DcVlcBits, 12,
                            i => dcChromaBits[i], i => dcChromaCode[i]);

        // The remaining tables are [n][2] arrays of {code, length}, flattened here, so the
        // length is at i * 2 + 1 and the code at i * 2 - FFmpeg's &table[0][1] and &table[0][0].
        byte[] mv = Mpeg12Data.Mpeg12MbMotionVectorTable.ToArray();
        Vlc.InitTableSparse(MvVlcSpan(MvVlc), Mpeg12Vlc.MvVlcBits, 17,
                            i => mv[i * 2 + 1], i => mv[i * 2]);

        byte[] mbIncr = Mpeg12Data.Mpeg12MbAddrIncrTable.ToArray();
        Vlc.InitTableSparse(MvVlcSpan(MbIncrVlc), Mpeg12Vlc.MbIncrVlcBits, 36,
                            i => mbIncr[i * 2 + 1], i => mbIncr[i * 2]);

        byte[] mbPat = Mpeg12Data.Mpeg12MbPatTable.ToArray();
        Vlc.InitTableSparse(MvVlcSpan(MbPatVlc), Mpeg12Vlc.MbPatVlcBits, 64,
                            i => mbPat[i * 2 + 1], i => mbPat[i * 2]);

        // The two macroblock type tables are sparse: the symbol is not the index but a macroblock
        // type word, so it is supplied explicitly.
        Vlc.InitTableSparse(MvVlcSpan(MbPTypeVlc), Mpeg12Vlc.MbPTypeVlcBits, 7,
                            i => TableMbPtype[i * 2 + 1], i => TableMbPtype[i * 2],
                            i => Ptype2MbType[i]);

        Vlc.InitTableSparse(MvVlcSpan(MbBTypeVlc), Mpeg12Vlc.MbBTypeVlcBits, 11,
                            i => TableMbBtype[i * 2 + 1], i => TableMbBtype[i * 2],
                            i => Btype2MbType[i]);

        InitTwoDimensionalVlcRunLevel(Mpeg12Data.Mpeg1VlcTable, Mpeg1RlVlc,
                                      Mpeg12Data.Mpeg12Run, Mpeg12Data.Mpeg12Level,
                                      Mpeg12Vlc.RunLevelElements);
        InitTwoDimensionalVlcRunLevel(Mpeg12Data.Mpeg2VlcTable, Mpeg2RlVlc,
                                      Mpeg12Data.Mpeg12Run, Mpeg12Data.Mpeg12Level,
                                      Mpeg12Vlc.RunLevelElements);
    }

    private static Span<VLCElem> MvVlcSpan(VLCElem[] table) => table.AsSpan();

    /// <summary>
    /// <c>ff_init_2d_vlc_rl</c> - builds a run-length VLC, whose entries carry a decoded run and
    /// level rather than a symbol to look up afterwards.
    /// </summary>
    /// <remarks>
    /// Two sentinel symbols sit past the real codes: index <paramref name="n"/> is escape and
    /// <c>n + 1</c> is end-of-block, which is why the coefficient tables hold 113 rows for 111
    /// run/level pairs. An illegal code is marked with run 65 and level <see cref="MaxLevel"/> so
    /// the coefficient loop can reject it without a second test.
    /// </remarks>
    public static void InitTwoDimensionalVlcRunLevel(
        ReadOnlySpan<ushort> tableVlc,
        Span<VLCElem> rlVlc,
        ReadOnlySpan<sbyte> tableRun,
        ReadOnlySpan<sbyte> tableLevel,
        int n)
    {
        // Copied out because a lambda cannot close over a span.
        ushort[] rows = tableVlc.ToArray();
        sbyte[] runs = tableRun.ToArray();
        sbyte[] levels = tableLevel.ToArray();

        Vlc.InitTableSparse(rlVlc, Mpeg12Vlc.TexVlcBits, n + 2,
                            i => rows[i * 2 + 1],
                            i => rows[i * 2]);

        for (int i = 0; i < rlVlc.Length; i++)
        {
            // Read both fields before writing any: the run-length fields overlay them.
            int idx = rlVlc[i].Sym;
            int len = rlVlc[i].Len;
            int level, run;

            if (len == 0)
            {
                // Illegal code.
                run = 65;
                level = MaxLevel;
            }
            else if (len < 0)
            {
                // A subtable follows; keep its base so the second lookup can find it.
                run = 0;
                level = idx;
            }
            else if (idx == n)
            {
                // Escape.
                run = 65;
                level = 0;
            }
            else if (idx == n + 1)
            {
                // End of block.
                run = 0;
                level = 127;
            }
            else
            {
                run = runs[idx] + 1;
                level = levels[idx];
            }

            rlVlc[i].Level = (short)level;
            rlVlc[i].Len8 = (sbyte)len;
            rlVlc[i].Run = (byte)run;
        }
    }

    /// <summary><c>MAX_INDEX</c> - the last coefficient position in a block.</summary>
    private const int MaxIndex = 63;

    /// <summary>
    /// <c>decode_dc</c> (<c>mpeg12dec.h:30</c>) - the DC differential, which is coded as a size
    /// followed by that many bits.
    /// </summary>
    public static int DecodeDc(ref GetBitContext gb, int component)
    {
        int code = component == 0
            ? gb.GetVlc2(DcLumVlc, Mpeg12Vlc.DcVlcBits, 2)
            : gb.GetVlc2(DcChromaVlc, Mpeg12Vlc.DcVlcBits, 2);

        // A zero size means a zero differential; anything else is that many biased bits, which
        // is what get_xbits decodes.
        return code == 0 ? 0 : gb.GetXBits(code);
    }

    /// <summary>
    /// <c>ff_mpeg1_decode_block_intra</c> - reads one intra block of an MPEG-1 picture,
    /// dequantising as it goes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MPEG-1 detects end-of-block by looking at the next two bits rather than decoding a
    /// symbol: the EOB code is <c>10</c>, and FFmpeg writes that as a signed comparison against
    /// 0xBFFFFFFF on the 32-bit cache, which is the same test. Those two bits are consumed at
    /// the end.
    /// </para>
    /// <para>
    /// Dequantisation happens here rather than in a later pass, which is why MPEG-1 intra blocks
    /// never go through MpegVideoUnquantize. The oddification is the same
    /// <c>(level - 1) | 1</c>.
    /// </para>
    /// </remarks>
    /// <returns>True when the block was read; false when the coefficient index ran past 63,
    /// which means the bitstream is corrupt.</returns>
    public static bool Mpeg1DecodeBlockIntra(
        ref GetBitContext gb, ReadOnlySpan<ushort> quantMatrix, ReadOnlySpan<byte> scantable,
        Span<int> lastDc, Span<short> block, int index, int qscale)
    {
        int component = index <= 3 ? 0 : index - 4 + 1;

        int diff = DecodeDc(ref gb, component);
        int dc = lastDc[component] + diff;
        lastDc[component] = dc;

        block[0] = (short)(dc * quantMatrix[0]);

        int i = 0;
        bool overflowed = false;

        // The two-bit end-of-block code, tested before the first coefficient.
        if (gb.ShowBits(2) != 2)
        {
            while (true)
            {
                gb.GetRlVlc(Mpeg1RlVlc, Mpeg12Vlc.TexVlcBits, 2, out int level, out int run);
                int j;

                if (level != 0)
                {
                    i += run;
                    if (i > MaxIndex) { overflowed = true; break; }

                    j = scantable[i];
                    level = (level * qscale * quantMatrix[j]) >> 4;
                    level = (level - 1) | 1;

                    // The sign is the next bit: 0 positive, 1 negative, applied without a branch
                    // exactly as the C does.
                    int sign = -(int)gb.ShowBits(1);
                    level = (level ^ sign) - sign;
                    gb.SkipBits(1);
                }
                else
                {
                    // Escape: a six-bit run and an eight-bit level, with two extensions.
                    run = (int)gb.ShowBits(6) + 1;
                    gb.SkipBits(6);
                    level = gb.GetSBits(8);

                    if (level == -128)
                        level = (int)gb.GetBits(8) - 256;
                    else if (level == 0)
                        level = (int)gb.GetBits(8);

                    i += run;
                    if (i > MaxIndex) { overflowed = true; break; }

                    j = scantable[i];
                    if (level < 0)
                    {
                        level = -level;
                        level = (level * qscale * quantMatrix[j]) >> 4;
                        level = (level - 1) | 1;
                        level = -level;
                    }
                    else
                    {
                        level = (level * qscale * quantMatrix[j]) >> 4;
                        level = (level - 1) | 1;
                    }
                }

                block[j] = (short)level;

                if (gb.ShowBits(2) == 2) break;
            }
        }

        // Consume the end-of-block code. The C reaches this even when the index overflowed, so
        // the reader is left in the same place either way.
        gb.SkipBits(2);
        return !overflowed;
    }

    // ----- the two sparse macroblock-type tables, from mpeg12.c -----

    /// <summary>
    /// <c>table_mb_ptype[7][2]</c>, flattened to {code, length} pairs.
    /// </summary>
    private static readonly ushort[] TableMbPtype =
    [
        3, 5,   // MB_INTRA
        1, 2,   // MB_PAT
        1, 3,   // MB_FOR
        1, 1,   // MB_FOR | MB_PAT
        1, 6,   // MB_QUANT | MB_INTRA
        1, 5,   // MB_QUANT | MB_PAT
        2, 5,   // MB_QUANT | MB_FOR | MB_PAT
    ];

    /// <summary>
    /// <c>ptype2mb_type[7]</c> - the macroblock type each P-picture code decodes to.
    /// </summary>
    /// <remarks>
    /// The second and sixth entries carry <c>MB_TYPE_ZERO_MV</c>, which mpeg12dec.h defines as
    /// <c>MB_TYPE_CODEC_SPECIFIC</c>: a coded-pattern macroblock with no transmitted motion
    /// vector, which predicts from the co-located block rather than from nothing.
    /// </remarks>
    private static readonly int[] Ptype2MbType =
    [
        MbType.Intra,
        MbType.ForwardMv | MbType.Cbp | MbType.ZeroMv | MbType.Size16X16,
        MbType.ForwardMv,
        MbType.ForwardMv | MbType.Cbp,
        MbType.Quant | MbType.Intra,
        MbType.Quant | MbType.ForwardMv | MbType.Cbp | MbType.ZeroMv | MbType.Size16X16,
        MbType.Quant | MbType.ForwardMv | MbType.Cbp,
    ];

    /// <summary><c>table_mb_btype[11][2]</c>, flattened to {code, length} pairs.</summary>
    private static readonly ushort[] TableMbBtype =
    [
        3, 5,   // MB_INTRA
        2, 3,   // MB_BACK
        3, 3,   // MB_BACK | MB_PAT
        2, 4,   // MB_FOR
        3, 4,   // MB_FOR | MB_PAT
        2, 2,   // MB_FOR | MB_BACK
        3, 2,   // MB_FOR | MB_BACK | MB_PAT
        1, 6,   // MB_QUANT | MB_INTRA
        2, 6,   // MB_QUANT | MB_BACK | MB_PAT
        3, 6,   // MB_QUANT | MB_FOR | MB_PAT
        2, 5,   // MB_QUANT | MB_FOR | MB_BACK | MB_PAT
    ];

    /// <summary><c>btype2mb_type[11]</c> - the macroblock type each B-picture code decodes to.</summary>
    private static readonly int[] Btype2MbType =
    [
        MbType.Intra,
        MbType.BackwardMv,
        MbType.BackwardMv | MbType.Cbp,
        MbType.ForwardMv,
        MbType.ForwardMv | MbType.Cbp,
        MbType.BidirMv,
        MbType.BidirMv | MbType.Cbp,
        MbType.Quant | MbType.Intra,
        MbType.Quant | MbType.BackwardMv | MbType.Cbp,
        MbType.Quant | MbType.ForwardMv | MbType.Cbp,
        MbType.Quant | MbType.BidirMv | MbType.Cbp,
    ];
}

/// <summary>
/// Macroblock type flags (<c>mpegutils.h</c>), as much of them as the MPEG-video decoders use.
/// </summary>
/// <remarks>
/// These are shared across FFmpeg's video decoders and several are marked in the C as H.264-only;
/// the ones here are those the MPEG-1/2 path sets or tests. The values are bit positions and are
/// stored in the per-picture <c>mb_type</c> table, so they are not free to renumber.
/// </remarks>
public static class MbType
{
    /// <summary><c>MB_TYPE_INTRA4x4</c>, and <c>MB_TYPE_INTRA</c> - the default when a codec has
    /// only one intra type, which is the case for MPEG-1/2.</summary>
    public const int Intra = 1 << 0;

    /// <summary><c>MB_TYPE_INTRA16x16</c> - H.264 only, present because <c>IS_INTRA</c>
    /// tests it.</summary>
    public const int Intra16X16 = 1 << 1;

    /// <summary><c>MB_TYPE_INTRA_PCM</c> - likewise.</summary>
    public const int IntraPcm = 1 << 2;

    /// <summary><c>MB_TYPE_16x16</c>.</summary>
    public const int Size16X16 = 1 << 3;

    /// <summary><c>MB_TYPE_16x8</c>.</summary>
    public const int Size16X8 = 1 << 4;

    /// <summary><c>MB_TYPE_8x16</c>.</summary>
    public const int Size8X16 = 1 << 5;

    /// <summary><c>MB_TYPE_8x8</c>.</summary>
    public const int Size8X8 = 1 << 6;

    /// <summary><c>MB_TYPE_INTERLACED</c>.</summary>
    public const int Interlaced = 1 << 7;

    /// <summary><c>MB_TYPE_DIRECT2</c> - H.263 and MPEG-4 B-frame direct mode.</summary>
    public const int Direct2 = 1 << 8;

    /// <summary>
    /// <c>MB_TYPE_CODEC_SPECIFIC</c>, which mpeg12dec.h names <c>MB_TYPE_ZERO_MV</c>: a
    /// macroblock with a coded pattern but no transmitted motion vector.
    /// </summary>
    public const int ZeroMv = 1 << 9;

    /// <summary><c>MB_TYPE_CBP</c> - the macroblock has a coded block pattern.</summary>
    public const int Cbp = 1 << 10;

    /// <summary><c>MB_TYPE_QUANT</c> - the macroblock carries a quantiser scale change.</summary>
    public const int Quant = 1 << 11;

    /// <summary><c>MB_TYPE_FORWARD_MV</c>.</summary>
    public const int ForwardMv = 1 << 12;

    /// <summary><c>MB_TYPE_BACKWARD_MV</c>.</summary>
    public const int BackwardMv = 1 << 13;

    /// <summary><c>MB_TYPE_BIDIR_MV</c>.</summary>
    public const int BidirMv = ForwardMv | BackwardMv;

    /// <summary><c>MB_TYPE_SKIP</c>.</summary>
    public const int Skip = 1 << 17;

    /// <summary><c>MB_TYPE_ACPRED</c> - H.263 Advanced Intra Coding used AC prediction.</summary>
    public const int Acpred = 1 << 18;

    /// <summary><c>IS_INTRA(a)</c> - any of the three intra bits.</summary>
    public static bool IsIntra(uint mbType) => (mbType & (Intra | Intra16X16 | IntraPcm)) != 0;

    /// <summary><c>IS_INTER(a)</c> - any of the four partition sizes.</summary>
    public static bool IsInter(uint mbType)
        => (mbType & (Size16X16 | Size16X8 | Size8X16 | Size8X8)) != 0;

    /// <summary><c>IS_8X8(a)</c>.</summary>
    public static bool Is8X8(uint mbType) => (mbType & Size8X8) != 0;

    /// <summary><c>IS_SKIP(a)</c> - what H.263's loop filter reads to decide a macroblock
    /// contributes no filter strength.</summary>
    public static bool IsSkip(uint mbType) => (mbType & Skip) != 0;
}

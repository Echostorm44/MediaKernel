// The VC-1 / WMV3 decoder - a translation of ffmpeg-master/libavcodec/vc1dec.c.
//
// Being built up a piece at a time; see the port checklist's vc1 section for what is
// translated and what is still todo. This file currently holds the one-time table construction.

using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Vc1Video;

/// <summary>The VC-1 and WMV3 decoder (<c>vc1dec.c</c>).</summary>
public static class Vc1Dec
{
    private static readonly object initLock = new();
    private static bool initialised;

    /// <summary><c>vc1_init_static</c> - every VLC table the decoder reads, built once.</summary>
    /// <remarks>
    /// <para>
    /// The C builds twenty-five of these families out of one <c>static VLCElem
    /// vlc_table[32372]</c>, handed out in order by a <c>VLCInitState</c> cursor, so no family's
    /// table size appears anywhere in FFmpeg's source. <see cref="Vlc.InitTablesSparse"/> sizes
    /// each one from the builder instead - the same lengths, one array each; the divergence is
    /// recorded there.
    /// </para>
    /// <para>
    /// The interlaced families (<c>intfr</c>, <c>if_mmv</c>, <c>if_1mv</c>, <c>1ref</c>,
    /// <c>2ref</c>, <c>icbpcy</c>) are built here exactly as the C builds them, although the
    /// interlaced decoding paths that read them are out of scope and not yet ported. Building
    /// them is what lets the table gate compare all eighty-five tables against FFmpeg rather
    /// than the subset one profile happens to reach.
    /// </para>
    /// </remarks>
    public static void Vc1InitStatic()
    {
        lock (initLock)
        {
            if (initialised)
                return;

            byte[] norm2Bits = Vc1VlcData.Vc1Norm2Bits;
            byte[] norm2Codes = Vc1VlcData.Vc1Norm2Codes;
            Vlc.InitTableSparse(Vc1Data.Vc1Norm2Vlc, Vc1Data.Norm2VlcBits, 4,
                                i => norm2Bits[i], i => norm2Codes[i]);

            byte[] norm6Bits = Vc1VlcData.Vc1Norm6Bits;
            ushort[] norm6Codes = Vc1VlcData.Vc1Norm6Codes;
            Vlc.InitTableSparse(Vc1Data.Vc1Norm6Vlc, Vc1Data.Norm6VlcBits, 64,
                                i => norm6Bits[i], i => norm6Codes[i]);

            byte[] imodeBits = Vc1VlcData.Vc1ImodeBits;
            byte[] imodeCodes = Vc1VlcData.Vc1ImodeCodes;
            Vlc.InitTableSparse(Vc1Data.Vc1ImodeVlc, Vc1Data.ImodeVlcBits, 7,
                                i => imodeBits[i], i => imodeCodes[i]);

            byte[] ttmbBits = Vc1VlcData.Vc1TtmbBits;
            ushort[] ttmbCodes = Vc1VlcData.Vc1TtmbCodes;
            byte[] ttblkBits = Vc1VlcData.Vc1TtblkBits;
            byte[] ttblkCodes = Vc1VlcData.Vc1TtblkCodes;
            byte[] subblkpatBits = Vc1VlcData.Vc1SubblkpatBits;
            byte[] subblkpatCodes = Vc1VlcData.Vc1SubblkpatCodes;
            for (int i = 0; i < 3; i++)
            {
                int ttmb = i * 16, ttblk = i * 8, subblkpat = i * 15;
                Vc1Data.Vc1TtmbVlc[i] = Vlc.InitTablesSparse(
                    Vc1Data.TtmbVlcBits, 16,
                    k => ttmbBits[ttmb + k], k => ttmbCodes[ttmb + k]);
                Vc1Data.Vc1TtblkVlc[i] = Vlc.InitTablesSparse(
                    Vc1Data.TtblkVlcBits, 8,
                    k => ttblkBits[ttblk + k], k => ttblkCodes[ttblk + k]);
                Vc1Data.Vc1SubblkpatVlc[i] = Vlc.InitTablesSparse(
                    Vc1Data.SubblkpatVlcBits, 15,
                    k => subblkpatBits[subblkpat + k], k => subblkpatCodes[subblkpat + k]);
            }

            byte[] fourMvBits = Vc1VlcData.Vc14mvBlockPatternBits;
            byte[] fourMvCodes = Vc1VlcData.Vc14mvBlockPatternCodes;
            byte[] cbpcyBits = Vc1VlcData.Vc1CbpcyPBits;
            ushort[] cbpcyCodes = Vc1VlcData.Vc1CbpcyPCodes;
            byte[] mvDiffBits = Vc1VlcData.Vc1MvDiffBits;
            ushort[] mvDiffCodes = Vc1VlcData.Vc1MvDiffCodes;
            byte[] intfr4Bits = Vc1VlcData.Vc1Intfr4mvMbmodeBits;
            ushort[] intfr4Codes = Vc1VlcData.Vc1Intfr4mvMbmodeCodes;
            byte[] intfrNon4Bits = Vc1VlcData.Vc1IntfrNon4mvMbmodeBits;
            byte[] intfrNon4Codes = Vc1VlcData.Vc1IntfrNon4mvMbmodeCodes;
            byte[] oneRefBits = Vc1VlcData.Vc11refMvdataBits;
            uint[] oneRefCodes = Vc1VlcData.Vc11refMvdataCodes;
            byte[] twoMvBits = Vc1VlcData.Vc12mvBlockPatternBits;
            byte[] twoMvCodes = Vc1VlcData.Vc12mvBlockPatternCodes;
            for (int i = 0; i < 4; i++)
            {
                int fourMv = i * 16, cbpcy = i * 64, mvDiff = i * 73;
                int intfr4 = i * 15, intfrNon4 = i * 9, oneRef = i * 72, twoMv = i * 4;

                Vc1Data.Vc14mvBlockPatternVlc[i] = Vlc.InitTablesSparse(
                    Vc1Data.FourMvBlockPatternVlcBits, 16,
                    k => fourMvBits[fourMv + k], k => fourMvCodes[fourMv + k]);
                Vc1Data.Vc1CbpcyPVlc[i] = Vlc.InitTablesSparse(
                    Vc1Data.CbpcyPVlcBits, 64,
                    k => cbpcyBits[cbpcy + k], k => cbpcyCodes[cbpcy + k]);
                Vc1Data.Vc1MvDiffVlc[i] = Vlc.InitTablesSparse(
                    Vc1Data.MvDiffVlcBits, 73,
                    k => mvDiffBits[mvDiff + k], k => mvDiffCodes[mvDiff + k]);
                // 4MV MBMODE, interlaced frame P picture.
                Vc1Data.Vc1Intfr4mvMbmodeVlc[i] = Vlc.InitTablesSparse(
                    Vc1Data.Intfr4MvMbmodeVlcBits, 15,
                    k => intfr4Bits[intfr4 + k], k => intfr4Codes[intfr4 + k]);
                // NON-4MV MBMODE for the same.
                Vc1Data.Vc1IntfrNon4mvMbmodeVlc[i] = Vlc.InitTablesSparse(
                    Vc1Data.IntfrNon4MvMbmodeVlcBits, 9,
                    k => intfrNon4Bits[intfrNon4 + k], k => intfrNon4Codes[intfrNon4 + k]);
                // Interlaced MVDATA, one reference.
                Vc1Data.Vc11refMvdataVlc[i] = Vlc.InitTablesSparse(
                    Vc1Data.OneRefMvdataVlcBits, 72,
                    k => oneRefBits[oneRef + k], k => oneRefCodes[oneRef + k]);
                // 2MV block pattern.
                Vc1Data.Vc12mvBlockPatternVlc[i] = Vlc.InitTablesSparse(
                    Vc1Data.TwoMvBlockPatternVlcBits, 4,
                    k => twoMvBits[twoMv + k], k => twoMvCodes[twoMv + k]);
            }

            uint[] acTables = Vc1VlcData.Vc1AcTables;
            int[] acSizes = Vc1VlcData.Vc1AcSizes;
            byte[] twoRefBits = Vc1VlcData.Vc12refMvdataBits;
            uint[] twoRefCodes = Vc1VlcData.Vc12refMvdataCodes;
            byte[] icbpcyBits = Vc1VlcData.Vc1IcbpcyPBits;
            ushort[] icbpcyCodes = Vc1VlcData.Vc1IcbpcyPCodes;
            byte[] ifMmvBits = Vc1VlcData.Vc1IfMmvMbmodeBits;
            byte[] ifMmvCodes = Vc1VlcData.Vc1IfMmvMbmodeCodes;
            byte[] if1MvBits = Vc1VlcData.Vc1If1mvMbmodeBits;
            byte[] if1MvCodes = Vc1VlcData.Vc1If1mvMbmodeCodes;
            for (int i = 0; i < 8; i++)
            {
                // &vc1_ac_tables[i][0][0] with a wrap of 8 and a size of 4: a [186][2] block of
                // uint32_t per coding set, code first and length second.
                int ac = i * 186 * 2;
                int twoRef = i * 126, icbpcy = i * 63, ifMmv = i * 8, if1Mv = i * 6;

                Vc1Data.Vc1AcCoeffTable[i] = Vlc.InitTablesSparse(
                    Vc1Data.AcVlcBits, acSizes[i],
                    k => (int)acTables[ac + 2 * k + 1], k => acTables[ac + 2 * k]);
                // Interlaced MVDATA, two references.
                Vc1Data.Vc12refMvdataVlc[i] = Vlc.InitTablesSparse(
                    Vc1Data.TwoRefMvdataVlcBits, 126,
                    k => twoRefBits[twoRef + k], k => twoRefCodes[twoRef + k]);
                // Interlaced CBPCY (Table 124 - Table 131).
                Vc1Data.Vc1IcbpcyVlc[i] = Vlc.InitTablesSparse(
                    Vc1Data.IcbpcyVlcBits, 63,
                    k => icbpcyBits[icbpcy + k], k => icbpcyCodes[icbpcy + k]);
                // Interlaced field picture MBMODE.
                Vc1Data.Vc1IfMmvMbmodeVlc[i] = Vlc.InitTablesSparse(
                    Vc1Data.IfMmvMbmodeVlcBits, 8,
                    k => ifMmvBits[ifMmv + k], k => ifMmvCodes[ifMmv + k]);
                Vc1Data.Vc1If1mvMbmodeVlc[i] = Vlc.InitTablesSparse(
                    Vc1Data.If1MvMbmodeVlcBits, 6,
                    k => if1MvBits[if1Mv + k], k => if1MvCodes[if1Mv + k]);
            }

            MsMpeg4Vc1Data.Msmp4Vc1VlcsInitOnce();

            initialised = true;
        }
    }

    /// <summary>
    /// <c>ff_vc1_init_transposed_scantables</c> - the scan tables an advanced profile picture
    /// uses, which are the ordinary ones with row and column swapped.
    /// </summary>
    /// <remarks>
    /// The transposition is why <c>left_blk_sh</c> and <c>top_blk_sh</c> swap with it: a
    /// block's row of AC predictors is a column in the transposed layout, so the shift that
    /// steps along each is the other one.
    /// </remarks>
    public static void Vc1InitTransposedScantables(Vc1Context v)
    {
        static byte Transpose(byte x) => (byte)((x >> 3) | ((x & 7) << 3));

        for (int i = 0; i < 64; i++)
        {
            v.Zz8x8[0 * 64 + i] = Transpose(MsMpeg4Vc1Data.Wmv1Scantable[0 * 64 + i]);
            v.Zz8x8[1 * 64 + i] = Transpose(MsMpeg4Vc1Data.Wmv1Scantable[1 * 64 + i]);
            v.Zz8x8[2 * 64 + i] = Transpose(MsMpeg4Vc1Data.Wmv1Scantable[2 * 64 + i]);
            v.Zz8x8[3 * 64 + i] = Transpose(MsMpeg4Vc1Data.Wmv1Scantable[3 * 64 + i]);
            v.Zzi8x8[i] = Transpose(Vc1Data.Vc1AdvInterlaced8x8Zz[i]);
        }
        v.LeftBlkSh = 0;
        v.TopBlkSh = 3;
    }
}

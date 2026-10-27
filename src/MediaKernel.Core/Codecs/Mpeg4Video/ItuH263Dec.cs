// FFmpeg's ituh263dec.c - the ITU H.263 bitstream decoder. Being built up in gated stages;
// see the port checklist for what is translated.

using System;
using MediaKernel.Core.Codecs.Mpeg2;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Mpeg4Video;

/// <summary>The ITU H.263 bitstream decoder (<c>ituh263dec.c</c>).</summary>
public static class ItuH263Dec
{
    /// <summary><c>H263_MV_VLC_BITS</c> (h263dec.h).</summary>
    public const int H263MvVlcBits = 9;

    /// <summary><c>INTRA_MCBPC_VLC_BITS</c> (h263dec.h).</summary>
    public const int IntraMcbpcVlcBits = 6;

    /// <summary><c>INTER_MCBPC_VLC_BITS</c> (h263dec.h).</summary>
    public const int InterMcbpcVlcBits = 7;

    /// <summary><c>CBPY_VLC_BITS</c> (h263dec.h).</summary>
    public const int CbpyVlcBits = 6;

    /// <summary><c>TEX_VLC_BITS</c> (h263dec.h).</summary>
    public const int TexVlcBits = 9;

    /// <summary><c>H263_MBTYPE_B_VLC_BITS</c>.</summary>
    public const int H263MbtypeBVlcBits = 6;

    /// <summary><c>CBPC_B_VLC_BITS</c>.</summary>
    public const int CbpcBVlcBits = 3;

    /// <summary>
    /// <c>h263_mb_type_b_map</c> - the macroblock type each B-frame MBTYPE code stands for.
    /// Entry 12 is the stuffing code and maps to nothing.
    /// </summary>
    public static ReadOnlySpan<short> H263MbTypeBMap =>
    [
        MbType.Direct2 | MbType.BidirMv,
        MbType.Direct2 | MbType.BidirMv    | MbType.Cbp,
        MbType.Direct2 | MbType.BidirMv    | MbType.Cbp | MbType.Quant,
                         MbType.ForwardMv                             | MbType.Size16X16,
                         MbType.ForwardMv  | MbType.Cbp               | MbType.Size16X16,
                         MbType.ForwardMv  | MbType.Cbp | MbType.Quant | MbType.Size16X16,
                         MbType.BackwardMv                            | MbType.Size16X16,
                         MbType.BackwardMv | MbType.Cbp               | MbType.Size16X16,
                         MbType.BackwardMv | MbType.Cbp | MbType.Quant | MbType.Size16X16,
                         MbType.BidirMv                               | MbType.Size16X16,
                         MbType.BidirMv    | MbType.Cbp               | MbType.Size16X16,
                         MbType.BidirMv    | MbType.Cbp | MbType.Quant | MbType.Size16X16,
        0, // stuffing
        MbType.Intra                       | MbType.Cbp,
        MbType.Intra                       | MbType.Cbp | MbType.Quant,
    ];

    /// <summary><c>ff_h263_intra_MCBPC_vlc[72]</c>.</summary>
    public static readonly VLCElem[] H263IntraMcbpcVlc = new VLCElem[72];

    /// <summary><c>ff_h263_inter_MCBPC_vlc[198]</c>.</summary>
    public static readonly VLCElem[] H263InterMcbpcVlc = new VLCElem[198];

    /// <summary><c>ff_h263_cbpy_vlc[64]</c>.</summary>
    public static readonly VLCElem[] H263CbpyVlc = new VLCElem[64];

    /// <summary><c>ff_h263_mv_vlc[538]</c>.</summary>
    public static readonly VLCElem[] H263MvVlc = new VLCElem[538];

    /// <summary><c>h263_mbtype_b_vlc[80]</c>.</summary>
    public static readonly VLCElem[] H263MbtypeBVlc = new VLCElem[80];

    /// <summary><c>cbpc_b_vlc[8]</c>.</summary>
    public static readonly VLCElem[] CbpcBVlc = new VLCElem[8];

    /// <summary><c>ff_h263_rl_inter</c> with its decoding tables for all 32 quantisers.</summary>
    public static RlTable H263RlInter { get; private set; } = null!;

    /// <summary><c>ff_rl_intra_aic</c>, quantiser 0 only, as the C's INIT_FIRST_VLC_RL.</summary>
    public static RlTable RlIntraAic { get; private set; } = null!;

    private static readonly object initLock = new();
    private static bool initialised;

    /// <summary>
    /// <c>ff_h263_decode_init_vlc</c> and the <c>h263_decode_init_vlc</c> it runs once - one
    /// member here, because the only thing the outer function adds is an AVOnce.
    /// </summary>
    public static void H263DecodeInitVlc()
    {
        // A lock rather than Lazy, because what is being initialised is six separate static
        // tables rather than one value.
        lock (initLock)
        {
            if (initialised)
                return;

            byte[] intraBits = H263Data.H263IntraMCBPCBits;
            byte[] intraCode = H263Data.H263IntraMCBPCCode;
            Vlc.InitTableSparse(H263IntraMcbpcVlc, IntraMcbpcVlcBits, 9,
                                i => intraBits[i], i => intraCode[i]);

            byte[] interBits = H263Data.H263InterMCBPCBits;
            byte[] interCode = H263Data.H263InterMCBPCCode;
            Vlc.InitTableSparse(H263InterMcbpcVlc, InterMcbpcVlcBits, 28,
                                i => interBits[i], i => interCode[i]);

            byte[] cbpy = H263Data.H263CbpyTab;
            Vlc.InitTableSparse(H263CbpyVlc, CbpyVlcBits, 16,
                                i => cbpy[2 * i + 1], i => cbpy[2 * i]);

            byte[] mv = H263Data.Mvtab;
            Vlc.InitTableSparse(H263MvVlc, H263MvVlcBits, 33,
                                i => mv[2 * i + 1], i => mv[2 * i]);

            H263RlInter = H263.H263InitRlInter();
            for (int q = 0; q < 32; q++)
                H263RlInter.RlVlc[q] = new VLCElem[554];
            Rl.InitVlc(H263RlInter, 554);

            RlIntraAic = H263Data.RlIntraAic();
            Rl.Init(RlIntraAic);
            RlIntraAic.RlVlc[0] = new VLCElem[554];
            Rl.InitVlc(RlIntraAic, 554);

            byte[] mbtypeB = H263Data.H263MbtypeBTab;
            Vlc.InitTableSparse(H263MbtypeBVlc, H263MbtypeBVlcBits, 15,
                                i => mbtypeB[2 * i + 1], i => mbtypeB[2 * i],
                                i => H263MbTypeBMap[i]);

            byte[] cbpcB = H263Data.CbpcBTab;
            Vlc.InitTableSparse(CbpcBVlc, CbpcBVlcBits, 4,
                                i => cbpcB[2 * i + 1], i => cbpcB[2 * i]);

            initialised = true;
        }
    }

    /// <summary><c>sign_extend</c> (mathops.h).</summary>
    private static int SignExtend(int val, int bits)
    {
        int shift = 8 * sizeof(int) - bits;
        return (val * (1 << shift)) >> shift;
    }

    /// <summary><c>ff_h263_decode_mba</c> - the macroblock address of a structured slice.</summary>
    public static int H263DecodeMba(H263DecContext h)
    {
        int i;
        for (i = 0; i < 6; i++)
            if (h.C.MbNum - 1 <= H263Data.MbaMax[i])
                break;
        int mbPos = (int)h.Gb.GetBits(H263Data.MbaLength[i]);
        h.C.MbX = mbPos % h.C.MbWidth;
        h.C.MbY = mbPos / h.C.MbWidth;

        return mbPos;
    }

    /// <summary>
    /// <c>h263_decode_gob_header</c> - the group-of-blocks or slice header.
    /// </summary>
    /// <returns>Less than zero if an error occurred.</returns>
    private static int H263DecodeGobHeader(H263DecContext h)
    {
        // Check for GOB Start Code
        uint val = h.Gb.ShowBits(16);
        if (val != 0)
            return -1;

        // We have a GBSC probably with GSTUFF
        h.Gb.SkipBits(16);              // drop the zeros
        int left = h.Gb.GetBitsLeft();
        left = Math.Min(left, 32);
        // We must check the bits left or we might end in an infinite loop (or segfault).
        for (; left > 13; left--)
        {
            if (h.Gb.GetBits1() != 0)
                break;                  // seek the '1' bit
        }
        if (left <= 13)
            return -1;

        if (h.H263SliceStructured != 0)
        {
            if (!h.Gb.CheckMarker())    // before MBA
                return -1;

            H263DecodeMba(h);

            if (h.C.MbNum > 1583)
                if (!h.Gb.CheckMarker())    // after MBA
                    return -1;

            h.C.Qscale = (int)h.Gb.GetBits(5);   // SQUANT
            if (!h.Gb.CheckMarker())    // after SQUANT
                return -1;
            h.Gb.SkipBits(2);           // GFID
        }
        else
        {
            uint gobNumber = h.Gb.GetBits(5);   // GN
            h.C.MbX = 0;
            h.C.MbY = h.GobIndex * (int)gobNumber;
            h.Gb.SkipBits(2);           // GFID
            h.C.Qscale = (int)h.Gb.GetBits(5);  // GQUANT
        }

        if (h.C.MbY >= h.C.MbHeight)
            return -1;

        if (h.C.Qscale == 0)
            return -1;

        return 0;
    }

    /// <summary>
    /// <c>ff_h263_resync</c> - finds the next group-of-blocks header.
    /// </summary>
    /// <returns>The bit position of the resync marker, or less than zero if none was found.</returns>
    public static int H263Resync(H263DecContext h)
    {
        int pos, ret;

        if (h.C.CodecId != MpegCodecId.H263 && h.C.CodecId != MpegCodecId.Mpeg4)
            throw new NotSupportedException(
                $"ff_h263_resync: codec {h.C.CodecId} is not translated here.");

        if (h.C.StudioProfile != 0)
            throw new NotSupportedException(
                "ff_h263_resync: the Studio Profile slice search is not ported.");

        if (h.C.CodecId == MpegCodecId.Mpeg4)
        {
            h.Gb.SkipBits1();
            h.Gb.AlignGetBits();
        }

        if (h.Gb.ShowBits(16) == 0)
        {
            pos = h.Gb.GetBitsCount();
            ret = ResyncHeader(h);
            if (ret >= 0)
                return pos;
        }

        // OK, it's not where it is supposed to be ...
        h.Gb = h.LastResyncGb;
        h.Gb.AlignGetBits();
        int left = h.Gb.GetBitsLeft();

        for (; left > 16 + 1 + 5 + 5; left -= 8)
        {
            if (h.Gb.ShowBits(16) == 0)
            {
                GetBitContext bak = h.Gb;

                pos = h.Gb.GetBitsCount();
                ret = ResyncHeader(h);
                if (ret >= 0)
                    return pos;

                h.Gb = bak;
            }
            h.Gb.SkipBits(8);
        }

        return -1;
    }

    /// <summary>
    /// The header <c>ff_h263_resync</c> reads once it has found a candidate position: a
    /// group-of-blocks header for H.263, a video packet header for MPEG-4.
    /// </summary>
    private static int ResyncHeader(H263DecContext h) =>
        h.C.CodecId == MpegCodecId.Mpeg4
            ? Mpeg4VideoDec.Mpeg4DecodeVideoPacketHeader((Mpeg4DecContext)h)
            : H263DecodeGobHeader(h);

    /// <summary>
    /// <c>ff_h263_decode_motion</c> - one motion-vector component.
    /// </summary>
    /// <returns>The component, or 0xffff on an invalid code.</returns>
    public static int H263DecodeMotion(H263DecContext h, int pred, int fCode)
    {
        int code = h.Gb.GetVlc2(H263MvVlc, H263MvVlcBits, 2);

        if (code == 0)
            return pred;
        if (code < 0)
            return 0xffff;

        int sign = (int)h.Gb.GetBits1();
        int shift = fCode - 1;
        int val = code;
        if (shift != 0)
        {
            val = (val - 1) << shift;
            val |= (int)h.Gb.GetBits(shift);
            val++;
        }
        if (sign != 0)
            val = -val;
        val += pred;

        // modulo decoding
        if (h.H263LongVectors == 0)
        {
            val = SignExtend(val, 5 + fCode);
        }
        else
        {
            // The horrible H.263 long vector mode.
            if (pred < -31 && val < -63)
                val += 64;
            if (pred > 32 && val > 63)
                val -= 64;
        }
        return val;
    }

    /// <summary><c>h263p_decode_umotion</c> - the RVLC of H.263+ unrestricted motion.</summary>
    private static int H263pDecodeUmotion(H263DecContext h, int pred)
    {
        if (h.Gb.GetBits1() != 0)       // motion difference = 0
            return pred;

        int code = 2 + (int)h.Gb.GetBits1();

        while (h.Gb.GetBits1() != 0)
        {
            code <<= 1;
            code += (int)h.Gb.GetBits1();
            if (code >= 32768)
                throw new NotSupportedException("H.263+ UMV: huge DMV.");
        }
        int sign = code & 1;
        code >>= 1;

        return sign != 0 ? pred - code : pred + code;
    }

    /// <summary>
    /// <c>preview_obmc</c> - reads the next macroblock's motion vectors so overlapped-block
    /// motion compensation can use them, then rewinds the reader.
    /// </summary>
    /// <remarks>The C's own comment calls this an ugly hack; it is translated as it stands.</remarks>
    private static void PreviewObmc(H263DecContext h)
    {
        GetBitContext gb = h.Gb;

        // cbpc starts at zero only to satisfy definite assignment: the skip path breaks out
        // of the do-while before it is read, as the C's goto does.
        int cbpc = 0;
        int predX, predY, mx, my;
        int xy = h.C.MbX + 1 + h.C.MbY * h.C.MbStride;
        int stride = h.C.B8Stride * 2;

        for (int i = 0; i < 4; i++)
            h.C.BlockIndex[i] += 2;
        for (int i = 4; i < 6; i++)
            h.C.BlockIndex[i] += 1;
        h.C.MbX++;

        short[] mv = h.C.CurPic.MotionValL0!;
        int mo = h.C.CurPic.MotionValOffsetL0;
        uint[] mbType = h.C.CurPic.MbType!;
        int mt = h.C.CurPic.MbTypeOffset;
        bool skipped = false;

        do
        {
            if (h.Gb.GetBits1() != 0)
            {
                // skip mb
                int motVal = mo + h.C.BlockIndex[0] * 2;
                mv[motVal + 0] = mv[motVal + 2] =
                mv[motVal + 0 + stride] = mv[motVal + 2 + stride] = 0;
                mv[motVal + 1] = mv[motVal + 3] =
                mv[motVal + 1 + stride] = mv[motVal + 3 + stride] = 0;

                mbType[mt + xy] = MbType.Skip | MbType.Size16X16 | MbType.ForwardMv;
                skipped = true;
                break;
            }
            cbpc = h.Gb.GetVlc2(H263InterMcbpcVlc, InterMcbpcVlcBits, 2);
        }
        while (cbpc == 20);

        if (!skipped)
        {
            if ((cbpc & 4) != 0)
            {
                mbType[mt + xy] = MbType.Intra;
            }
            else
            {
                h.Gb.GetVlc2(H263CbpyVlc, CbpyVlcBits, 1);
                if ((cbpc & 8) != 0)
                    h.Gb.SkipBits(h.ModifiedQuant != 0 ? (h.Gb.GetBits1() != 0 ? 1 : 5) : 2);

                if ((cbpc & 16) == 0)
                {
                    mbType[mt + xy] = MbType.Size16X16 | MbType.ForwardMv;
                    // 16x16 motion prediction
                    int motVal = H263.H263PredMotion(ref h.C, 0, 0, out predX, out predY);
                    mx = h.Umvplus != 0 ? H263pDecodeUmotion(h, predX)
                                        : H263DecodeMotion(h, predX, 1);
                    my = h.Umvplus != 0 ? H263pDecodeUmotion(h, predY)
                                        : H263DecodeMotion(h, predY, 1);

                    mv[motVal + 0] = mv[motVal + 2] =
                    mv[motVal + 0 + stride] = mv[motVal + 2 + stride] = (short)mx;
                    mv[motVal + 1] = mv[motVal + 3] =
                    mv[motVal + 1 + stride] = mv[motVal + 3 + stride] = (short)my;
                }
                else
                {
                    mbType[mt + xy] = MbType.Size8X8 | MbType.ForwardMv;
                    for (int i = 0; i < 4; i++)
                    {
                        int motVal = H263.H263PredMotion(ref h.C, i, 0, out predX, out predY);
                        mx = h.Umvplus != 0 ? H263pDecodeUmotion(h, predX)
                                            : H263DecodeMotion(h, predX, 1);
                        my = h.Umvplus != 0 ? H263pDecodeUmotion(h, predY)
                                            : H263DecodeMotion(h, predY, 1);
                        if (h.Umvplus != 0 && mx - predX == 1 && my - predY == 1)
                            h.Gb.SkipBits1();   // bit stuffing to prevent a PSC
                        mv[motVal + 0] = (short)mx;
                        mv[motVal + 1] = (short)my;
                    }
                }
            }
        }

        for (int i = 0; i < 4; i++)
            h.C.BlockIndex[i] -= 2;
        for (int i = 4; i < 6; i++)
            h.C.BlockIndex[i] -= 1;
        h.C.MbX--;

        h.Gb = gb;
    }

    /// <summary><c>h263_decode_dquant</c> - the quantiser change.</summary>
    private static void H263DecodeDquant(H263DecContext h)
    {
        ReadOnlySpan<sbyte> quantTab = [-1, -2, 1, 2];
        int qscale;

        if (h.ModifiedQuant != 0)
        {
            if (h.Gb.GetBits1() != 0)
                qscale = H263Data.ModifiedQuantTab[(int)h.Gb.GetBits1() * 32 + h.C.Qscale];
            else
                qscale = (int)h.Gb.GetBits(5);
        }
        else
        {
            qscale = h.C.Qscale + quantTab[(int)h.Gb.GetBits(2)];
        }
        MpegVideoDec.SetQscale(ref h.C, qscale);
    }

    /// <summary>
    /// <c>h263_pred_acdc</c> - Advanced Intra Coding's DC and AC prediction.
    /// </summary>
    private static void H263PredAcdc(ref MpegEncContext s, Span<short> block, int n)
    {
        int wrap, scale;
        int xy = s.BlockIndex[n];
        short[] dcValBase = s.DcValBase!;
        int dcVal = s.DcValOffset + xy;
        short[] acValBase = s.AcValBase!;
        int acVal = s.AcValOffset + xy * 16;

        // find prediction
        if (n < 4)
        {
            wrap = s.B8Stride;
            scale = s.YDcScale;
        }
        else
        {
            wrap = s.MbStride;
            scale = s.CDcScale;
        }

        //  B C
        //  A X
        int a = dcValBase[dcVal - 1];
        int c = dcValBase[dcVal - wrap];

        // No prediction outside a GOB boundary.
        if (s.FirstSliceLine != 0 && n != 3)
        {
            if (n != 2) c = 1024;
            if (n != 1 && s.MbX == s.ResyncMbX) a = 1024;
        }

        int predDc;
        if (s.AcPred != 0)
        {
            predDc = 1024;
            if (s.H263AicDir != 0)
            {
                // left prediction
                if (a != 1024)
                {
                    int acVal2 = acVal - 16;
                    for (int i = 1; i < 8; i++)
                        block[i << 3] += acValBase[acVal2 + i];
                    predDc = a;
                }
            }
            else
            {
                // top prediction
                if (c != 1024)
                {
                    int acVal2 = acVal - 16 * wrap;
                    for (int i = 1; i < 8; i++)
                        block[i] += acValBase[acVal2 + i + 8];
                    predDc = c;
                }
            }
        }
        else
        {
            // just DC prediction
            if (a != 1024 && c != 1024)
                predDc = (a + c) >> 1;
            else if (a != 1024)
                predDc = a;
            else
                predDc = c;
        }

        // we assume pred is positive
        block[0] = (short)(block[0] * scale + predDc);

        if (block[0] < 0)
            block[0] = 0;
        else
            block[0] |= 1;

        // Update AC/DC tables.
        dcValBase[dcVal] = block[0];

        // left copy
        for (int i = 1; i < 8; i++)
            acValBase[acVal + i] = block[i << 3];
        // top copy
        for (int i = 1; i < 8; i++)
            acValBase[acVal + 8 + i] = block[i];
    }

    /// <summary><c>h263_decode_block</c> - one 8x8 block's coefficients.</summary>
    private static int H263DecodeBlock(H263DecContext h, Span<short> block, int n, int coded)
    {
        RlTable rl = H263RlInter;
        GetBitContext gb = h.Gb;
        int i;

        // The port's IDCT uses the identity permutation, so the permutated scan is the scan.
        ReadOnlySpan<byte> scanTable = h.C.IntraScantable.Permutated;

        bool notCoded = false;

        if (h.C.H263Aic != 0 && h.C.MbIntra != 0)
        {
            i = 0;
            if (coded == 0)
            {
                notCoded = true;
            }
            else
            {
                rl = RlIntraAic;
                if (h.C.AcPred != 0)
                {
                    scanTable = h.C.H263AicDir != 0
                        ? h.PermutatedIntraVScantable        // left
                        : h.PermutatedIntraHScantable;       // top
                }
            }
        }
        else if (h.C.MbIntra != 0)
        {
            // DC coefficient
            int dc = (int)h.Gb.GetBits(8);
            if ((dc & 0x7F) == 0)
                return -1;
            if (dc == 255)
                dc = 128;
            block[0] = (short)dc;
            i = 1;
        }
        else
        {
            i = 0;
        }

        if (!notCoded)
        {
            if (coded == 0)
            {
                h.C.BlockLastIndex[n] = i - 1;
                return 0;
            }

            while (true)   // the C's "retry" label
            {
                bool retry = false;
                i--;       // offset by -1 to allow direct indexing of scan_table
                while (true)
                {
                    h.Gb.GetRlVlc(rl.RlVlc[0], TexVlcBits, 2, out int level, out int run);
                    if (run == 66)
                    {
                        if (level != 0)
                            return -1;

                        // escape
                        run = (int)h.Gb.ShowBits(7) + 1;
                        h.Gb.SkipBits(7);
                        level = (sbyte)h.Gb.GetBits(8);
                        if (level == -128)
                        {
                            int lo = (int)h.Gb.GetBits(5);
                            int hi = h.Gb.GetSBits(6);
                            level = lo | (hi * (1 << 5));
                        }
                    }
                    else
                    {
                        if (h.Gb.GetBits1() != 0)
                            level = -level;
                    }
                    i += run;
                    if (i >= 64)
                    {
                        // Redo the update without the last flag, reverting the -1 offset.
                        i = i - run + ((run - 1) & 63) + 1;
                        if (i < 64)
                        {
                            // only the last marker, no overrun
                            block[scanTable[i]] = (short)level;
                            break;
                        }
                        if (h.AltInterVlc != 0 && ReferenceEquals(rl, H263RlInter)
                            && h.C.MbIntra == 0)
                        {
                            // Looks like a hack but no, it is the way it is supposed to work.
                            rl = RlIntraAic;
                            i = 0;
                            h.Gb = gb;
                            block.Clear();
                            retry = true;
                            break;
                        }
                        return -1;
                    }
                    block[scanTable[i]] = (short)level;
                }

                if (!retry)
                    break;
            }
        }

        if (h.C.MbIntra != 0 && h.C.H263Aic != 0)
            H263PredAcdc(ref h.C, block, n);

        h.C.BlockLastIndex[n] = i;
        return 0;
    }

    /// <summary><c>h263_skip_b_part</c> - reads and discards a PB-frame's B part.</summary>
    private static int H263SkipBPart(H263DecContext h, int cbp)
    {
        Span<short> dblock = stackalloc short[64];
        Span<int> bli = stackalloc int[6];

        // h->c.mb_intra has to be zero to decode the B part of a PB-frame correctly, but the
        // real value has to be restored for the OBMC condition later.
        int mbi = h.C.MbIntra;
        for (int i = 0; i < 6; i++) bli[i] = h.C.BlockLastIndex[i];
        h.C.MbIntra = 0;
        for (int i = 0; i < 6; i++)
        {
            dblock.Clear();
            if (H263DecodeBlock(h, dblock, i, cbp & 32) < 0)
                return -1;
            cbp += cbp;
        }
        h.C.MbIntra = mbi;
        for (int i = 0; i < 6; i++) h.C.BlockLastIndex[i] = bli[i];
        return 0;
    }

    /// <summary><c>h263_get_modb</c> - a PB-frame's MODB field.</summary>
    private static int H263GetModb(ref GetBitContext gb, int pbFrame, out int cbpb)
    {
        int c, mv = 1;

        if (pbFrame < 3)
        {
            // H.263 Annex G and i263 PB-frame
            c = (int)gb.GetBits1();
            if (pbFrame == 2 && c != 0)
                mv = gb.GetBits1() != 0 ? 0 : 1;
        }
        else
        {
            // H.263 Annex M improved PB-frame
            mv = GetUnary(ref gb, 0, 4) + 1;
            c = mv & 1;
            mv = (mv & 2) != 0 ? 1 : 0;
        }
        cbpb = c != 0 ? (int)gb.GetBits(6) : 0;
        return mv;
    }

    /// <summary><c>get_unary</c> (unary.h) - counts bits until a stop bit or a length limit.</summary>
    private static int GetUnary(ref GetBitContext gb, int stop, int len)
    {
        int i;
        for (i = 0; i < len && gb.GetBits1() != stop; i++) { }
        return i;
    }

    /// <summary>
    /// <c>ff_h263_decode_mb</c> - one macroblock's header, motion vectors and coefficients.
    /// </summary>
    public static H263SliceResult H263DecodeMb(H263DecContext h)
    {
        // cbpc starts at zero only to satisfy definite assignment; see PreviewObmc.
        int cbpc = 0, cbpy, cbp = 0, predX, predY, mx, my;
        int dquant = 0;
        int xy = h.C.MbX + h.C.MbY * h.C.MbStride;
        int cbpb = 0, pbMvCount = 0;
        bool intra = false;
        bool skipToEnd = false;

        uint[] mbTypeTab = h.C.CurPic.MbType!;
        int mt = h.C.CurPic.MbTypeOffset;

        if (h.C.PictType == AvPictureType.P)
        {
            do
            {
                if (h.Gb.GetBits1() != 0)
                {
                    // skip mb
                    h.C.MbIntra = 0;
                    for (int i = 0; i < 6; i++)
                        h.C.BlockLastIndex[i] = -1;
                    h.C.MvDir = MpegVideoConstants.MvDirForward;
                    h.C.MvType = MpegVideoConstants.MvType16X16;
                    mbTypeTab[mt + xy] = MbType.Skip | MbType.Size16X16 | MbType.ForwardMv;
                    h.C.Mv[MvIndex(0, 0, 0)] = 0;
                    h.C.Mv[MvIndex(0, 0, 1)] = 0;
                    h.C.MbSkipped = (h.C.Obmc | h.LoopFilter) != 0 ? 0 : 1;
                    skipToEnd = true;
                    break;
                }
                cbpc = h.Gb.GetVlc2(H263InterMcbpcVlc, InterMcbpcVlcBits, 2);
                if (cbpc < 0)
                    return H263SliceResult.Error;
            }
            while (cbpc == 20);

            if (!skipToEnd)
            {
                h.Block.AsSpan().Clear();

                dquant = cbpc & 8;
                h.C.MbIntra = (cbpc & 4) != 0 ? 1 : 0;
                if (h.C.MbIntra != 0)
                {
                    intra = true;
                }
                else
                {
                    if (h.PbFrame != 0 && h.Gb.GetBits1() != 0)
                        pbMvCount = H263GetModb(ref h.Gb, h.PbFrame, out cbpb);
                    cbpy = h.Gb.GetVlc2(H263CbpyVlc, CbpyVlcBits, 1);

                    if (cbpy < 0)
                        return H263SliceResult.Error;

                    if (h.AltInterVlc == 0 || (cbpc & 3) != 3)
                        cbpy ^= 0xF;

                    cbp = (cbpc & 3) | (cbpy << 2);
                    if (dquant != 0)
                        H263DecodeDquant(h);

                    h.C.MvDir = MpegVideoConstants.MvDirForward;
                    if ((cbpc & 16) == 0)
                    {
                        mbTypeTab[mt + xy] = MbType.Size16X16 | MbType.ForwardMv;
                        // 16x16 motion prediction
                        h.C.MvType = MpegVideoConstants.MvType16X16;
                        H263.H263PredMotion(ref h.C, 0, 0, out predX, out predY);
                        mx = h.Umvplus != 0 ? H263pDecodeUmotion(h, predX)
                                            : H263DecodeMotion(h, predX, 1);
                        if (mx >= 0xffff)
                            return H263SliceResult.Error;

                        my = h.Umvplus != 0 ? H263pDecodeUmotion(h, predY)
                                            : H263DecodeMotion(h, predY, 1);
                        if (my >= 0xffff)
                            return H263SliceResult.Error;
                        h.C.Mv[MvIndex(0, 0, 0)] = mx;
                        h.C.Mv[MvIndex(0, 0, 1)] = my;

                        if (h.Umvplus != 0 && mx - predX == 1 && my - predY == 1)
                            h.Gb.SkipBits1();   // bit stuffing to prevent a PSC
                    }
                    else
                    {
                        mbTypeTab[mt + xy] = MbType.Size8X8 | MbType.ForwardMv;
                        h.C.MvType = MpegVideoConstants.MvType8X8;
                        short[] mv = h.C.CurPic.MotionValL0!;
                        for (int i = 0; i < 4; i++)
                        {
                            int motVal = H263.H263PredMotion(ref h.C, i, 0, out predX, out predY);
                            mx = h.Umvplus != 0 ? H263pDecodeUmotion(h, predX)
                                                : H263DecodeMotion(h, predX, 1);
                            if (mx >= 0xffff)
                                return H263SliceResult.Error;

                            my = h.Umvplus != 0 ? H263pDecodeUmotion(h, predY)
                                                : H263DecodeMotion(h, predY, 1);
                            if (my >= 0xffff)
                                return H263SliceResult.Error;
                            h.C.Mv[MvIndex(0, i, 0)] = mx;
                            h.C.Mv[MvIndex(0, i, 1)] = my;
                            if (h.Umvplus != 0 && mx - predX == 1 && my - predY == 1)
                                h.Gb.SkipBits1();
                            mv[motVal + 0] = (short)mx;
                            mv[motVal + 1] = (short)my;
                        }
                    }
                }
            }
        }
        else if (h.C.PictType == AvPictureType.B)
        {
            throw new NotSupportedException(
                "ff_h263_decode_mb: the B-frame path is not translated yet.");
        }
        else
        {
            // I-frame
            do
            {
                cbpc = h.Gb.GetVlc2(H263IntraMcbpcVlc, IntraMcbpcVlcBits, 2);
                if (cbpc < 0)
                    return H263SliceResult.Error;
            }
            while (cbpc == 8);

            h.Block.AsSpan().Clear();

            dquant = cbpc & 4;
            h.C.MbIntra = 1;
            intra = true;
        }

        if (intra)
        {
            // The C's "intra:" label, which the P path jumps to as well. Every path that
            // arrives here has already set dquant - 8's bit from an inter MCBPC, 4's from an
            // intra one - which is why it is declared at the top rather than per branch.
            mbTypeTab[mt + xy] = MbType.Intra;
            if (h.C.H263Aic != 0)
            {
                h.C.AcPred = (int)h.Gb.GetBits1();
                if (h.C.AcPred != 0)
                {
                    mbTypeTab[mt + xy] = MbType.Intra | MbType.Acpred;
                    h.C.H263AicDir = (int)h.Gb.GetBits1();
                }
            }
            else
            {
                h.C.AcPred = 0;
            }

            if (h.PbFrame != 0 && h.Gb.GetBits1() != 0)
                pbMvCount = H263GetModb(ref h.Gb, h.PbFrame, out cbpb);
            cbpy = h.Gb.GetVlc2(H263CbpyVlc, CbpyVlcBits, 1);
            if (cbpy < 0)
                return H263SliceResult.Error;
            cbp = (cbpc & 3) | (cbpy << 2);
            if (dquant != 0)
                H263DecodeDquant(h);

            pbMvCount += h.PbFrame != 0 ? 1 : 0;
        }

        if (!skipToEnd)
        {
            while (pbMvCount-- != 0)
            {
                H263DecodeMotion(h, 0, 1);
                H263DecodeMotion(h, 0, 1);
            }

            // decode each block
            for (int i = 0; i < 6; i++)
            {
                if (H263DecodeBlock(h, h.Block.AsSpan(i * 64, 64), i, cbp & 32) < 0)
                    return H263SliceResult.Error;
                cbp += cbp;
            }

            if (h.PbFrame != 0 && H263SkipBPart(h, cbpb) < 0)
                return H263SliceResult.Error;
            if (h.C.Obmc != 0 && h.C.MbIntra == 0)
            {
                if (h.C.PictType == AvPictureType.P &&
                    h.C.MbX + 1 < h.C.MbWidth && h.MbNumLeft != 1)
                    PreviewObmc(h);
            }
        }

        if (h.Gb.GetBitsLeft() < 0)
            return H263SliceResult.Error;

        // per-MB end of slice check
        {
            int v = (int)h.Gb.ShowBits(16);

            if (h.Gb.GetBitsLeft() < 16)
                v >>= 16 - h.Gb.GetBitsLeft();

            if (v == 0)
                return H263SliceResult.End;
        }

        return H263SliceResult.Ok;
    }

    /// <summary><c>mv[i][j][k]</c> on the shared context.</summary>
    private static int MvIndex(int i, int j, int k) => i * 8 + j * 2 + k;

    /// <summary>
    /// <c>skip_1stop_8data_bits</c> (get_bits.h) - skips the trailing PEI bytes, each
    /// introduced by a one bit.
    /// </summary>
    private static int Skip1Stop8DataBits(ref GetBitContext gb)
    {
        if (gb.GetBitsLeft() <= 0)
            return -1;

        while (gb.GetBits1() != 0)
        {
            gb.SkipBits(8);
            if (gb.GetBitsLeft() <= 0)
                return -1;
        }

        return 0;
    }

    /// <summary>
    /// <c>ff_h263_decode_picture_header</c>. Most is hardcoded in the C too; its own comment
    /// says it should be extended to handle all H.263 streams.
    /// </summary>
    /// <returns>Zero on success, less than zero on a damaged header.</returns>
    public static int H263DecodePictureHeader(H263DecContext h)
    {
        int width, height, i;

        h.Gb.AlignGetBits();

        uint startcode = h.Gb.GetBits(22 - 8);

        for (i = h.Gb.GetBitsLeft(); i > 24; i -= 8)
        {
            startcode = ((startcode << 8) | h.Gb.GetBits(8)) & 0x003FFFFF;

            if (startcode == 0x20)
                break;
        }

        if (startcode != 0x20)
            return -1;

        // temporal reference
        i = (int)h.Gb.GetBits(8);     // picture timestamp

        i -= (i - (h.PictureNumber & 0xFF) + 128) & ~0xFF;

        h.PictureNumber = (h.PictureNumber & ~0xFF) + i;

        // PTYPE starts here
        if (!h.Gb.CheckMarker())
            return -1;
        if (h.Gb.GetBits1() != 0)
            return -1;      // H.263 id

        h.Gb.SkipBits1();   // split screen off
        h.Gb.SkipBits1();   // camera off
        h.Gb.SkipBits1();   // freeze picture release off

        int format = (int)h.Gb.GetBits(3);
        //  0    forbidden
        //  1    sub-QCIF
        //  2    QCIF
        //  7    extended PTYPE (PLUSPTYPE)

        if (format != 7 && format != 6)
        {
            // H.263v1
            width = H263Data.H263Format[format * 2 + 0];
            height = H263Data.H263Format[format * 2 + 1];
            if (width == 0)
                return -1;

            h.C.PictType = h.Gb.GetBits1() != 0 ? AvPictureType.P : AvPictureType.I;

            h.H263LongVectors = (int)h.Gb.GetBits1();

            if (h.Gb.GetBits1() != 0)
                return -1;      // SAC: off

            h.C.Obmc = (int)h.Gb.GetBits1();        // advanced prediction mode

            h.PbFrame = (int)h.Gb.GetBits1();
            h.C.ChromaQscale = h.C.Qscale = (int)h.Gb.GetBits(5);
            h.Gb.SkipBits1();   // continuous presence multipoint mode: off

            h.C.Width = width;
            h.C.Height = height;
            h.Avctx.SampleAspectRatio = new AvRational(12, 11);
            h.Avctx.Framerate = new AvRational(30000, 1001);
        }
        else
        {
            throw new NotSupportedException(
                "ff_h263_decode_picture_header: the H.263+ PLUSPTYPE path is not translated "
                + "yet.");
        }

        if (h.C.Width <= 0 || h.C.Height <= 0)
            return -1;

        if (h.C.Width * h.C.Height / 256 / 8 > h.Gb.GetBitsLeft())
            return -1;

        h.C.MbWidth = (h.C.Width + 15) / 16;
        h.C.MbHeight = (h.C.Height + 15) / 16;
        h.C.MbNum = h.C.MbWidth * h.C.MbHeight;

        h.GobIndex = H263GobHeight(h.C.Height);

        if (h.PbFrame != 0)
        {
            h.Gb.SkipBits(3);   // temporal reference for B pictures
            if (h.CustomPcf != 0)
                h.Gb.SkipBits(2);
            h.Gb.SkipBits(2);   // quantisation information for B pictures
        }

        if (h.C.PictType != AvPictureType.B)
        {
            h.C.Time = h.PictureNumber;
            // pp_time is a uint16_t in the C and the subtraction truncates into it.
            h.C.PpTime = (ushort)(h.C.Time - h.C.LastNonBTime);
            h.C.LastNonBTime = h.C.Time;
        }
        else
        {
            throw new NotSupportedException(
                "ff_h263_decode_picture_header: B pictures need ff_mpeg4_init_direct_mv, "
                + "which is not translated yet.");
        }

        // PEI
        if (Skip1Stop8DataBits(ref h.Gb) < 0)
            return -1;

        if (h.H263SliceStructured != 0)
        {
            if (!h.Gb.CheckMarker())    // SEPB1
                return -1;

            H263DecodeMba(h);

            if (!h.Gb.CheckMarker())    // SEPB2
                return -1;
        }

        if (h.C.PictType == AvPictureType.B)
            h.C.LowDelay = 0;

        if (h.C.H263Aic != 0)
        {
            h.C.YDcScaleTable =
            h.C.CDcScaleTable = H263Data.AicDcScaleTable;
        }
        else
        {
            h.C.YDcScaleTable =
            h.C.CDcScaleTable = MpegVideoData.Mpeg1DcScaleTable;
        }

        return 0;
    }

    /// <summary><c>H263_GOB_HEIGHT</c> (h263.h) - GOB height in macroblock rows.</summary>
    private static int H263GobHeight(int h) => h <= 400 ? 1 : h <= 800 ? 2 : 4;
}

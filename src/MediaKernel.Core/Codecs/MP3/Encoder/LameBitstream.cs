// Translated from lame-3.100/libmp3lame/bitstream.c - the frame writer.
//
// From the encoder's side the stream is a series of main_data() blocks with headers and side
// information inserted at the right places to keep the framing (Figure A.7 of the IS). The
// header for a frame is built into a ring of MAX_HEADER_BUF slots, each stamped with the bit
// position it belongs at, and putbits2 emits it when the running bit count reaches that stamp.
// That indirection is what lets main data for one frame start inside the previous one, which
// is the bit reservoir.
//
// THE VERSION STRING IS PART OF THE OUTPUT. drain_into_ancillary writes "LAME" and then
// get_lame_short_version() into the padding, so the bytes a frame ends with depend on which
// LAME this is. For 3.100 the patch level is 0 and the release branch is taken, so the string
// is "3.100" - five bytes that have to match, and one more reason the reference version was
// pinned by reading an encoded file's tag rather than guessed.
//
// do_gain_analysis's body is entirely inside #ifdef DECODE_ON_THE_FLY, which needs the mpglib
// decoder and gain_analysis.c; both are out of scope. It is recorded as diverged and the
// function returns its argument, which is what the C does when that symbol is undefined.
//
// WHERE THE C LIMPS, THIS THROWS. format_bitstream prints "This is a fatal error" and then
// carries on with a corrected ResvSize; encodeSideInfo2 prints that the header ring overflowed
// and carries on writing over a header it has not emitted. Both mean the bitstream is already
// wrong. Under this project's fail-loudly rule they throw, which is a recorded divergence: if
// neither ever fires the output is identical, and if one does the bit-exact gate needs to say
// so rather than produce a plausible wrong frame.

using System;

namespace MediaKernel.Core.Codecs.MP3.Encoder;

/// <summary><c>bitstream.c</c>.</summary>
internal static class LameBitstream
{
    /// <summary><c>MAX_LENGTH</c> - the widest field putbits2 will take, less two.</summary>
    private const int MaxLength = 32;

    /// <summary><c>LAME_MAXALBUMART</c> - 128 KB.</summary>
    private const int LameMaxAlbumArt = 128 * 1024;

    /// <summary><c>BUFFER_SIZE</c> - <c>LAME_MAXMP3BUFFER</c>.</summary>
    public const int BufferSize = 16384 + LameMaxAlbumArt;

    /// <summary><c>CRC16_POLYNOMIAL</c>.</summary>
    private const int Crc16Polynomial = 0x8005;

    /// <summary>
    /// <c>get_lame_short_version()</c> for this source tree - see the file header.
    /// </summary>
    /// <remarks>
    /// <c>version.h</c> gives major 3, minor 100, patch 0 and the release branch, so the
    /// <c>#else</c> arm applies and the string is major.minor with no patch level.
    /// </remarks>
    public const string ShortVersion = "3.100";

    /// <summary><c>MDB_DEFAULT</c>, <c>MDB_STRICT_ISO</c>, <c>MDB_MAXIMUM</c> (<c>lame.h</c>).</summary>
    public const int MdbDefault = 0, MdbStrictIso = 1, MdbMaximum = 2;

    /// <summary>
    /// <c>crc16_lookup[256]</c> of VbrTag.c, built from its stated polynomial rather than
    /// transcribed; the gate compares it against the C's literal table.
    /// </summary>
    private static readonly ushort[] crc16Lookup = BuildCrc16Lookup();

    private static ushort[] BuildCrc16Lookup()
    {
        // x^16 + x^15 + x^2 + 1, reflected, which is the 0xA001 form.
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            uint crc = (uint)i;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001u : crc >> 1;
            table[i] = (ushort)crc;
        }
        return table;
    }

    /// <summary><c>CRC_update_lookup</c> (VbrTag.c).</summary>
    private static ushort CrcUpdateLookup(byte value, ushort crc)
        => (ushort)(crc16Lookup[(crc ^ value) & 0xff] ^ (crc >> 8));

    /// <summary><c>UpdateMusicCRC</c> (VbrTag.c).</summary>
    /// <remarks>
    /// Only the Xing/LAME tag ever reads <c>nMusicCRC</c>, and libmp3lame.c turns that tag off,
    /// so this cannot reach the output. It is translated anyway, because <c>copy_buffer</c>
    /// calls it unconditionally and leaving a call out is how a port drifts.
    /// </remarks>
    private static void UpdateMusicCrc(ref ushort crc, ReadOnlySpan<byte> buffer, int size)
    {
        for (int i = 0; i < size; ++i)
            crc = CrcUpdateLookup(buffer[i], crc);
    }

    /// <summary>
    /// <c>calcFrameLength</c> - the frame length in bits, including the padding slot.
    /// </summary>
    /// <remarks>
    /// <c>(version + 1) * 72000</c> is 144000 for MPEG-1 and 72000 for MPEG-2 and 2.5, which
    /// is the 1152 or 576 samples per frame divided by 8 and multiplied by 1000 for the kbps.
    /// The division truncates, as it does in the C, and that truncation is the frame-size
    /// pattern a 128 kbps 44.1 kHz stream repeats.
    /// </remarks>
    private static int CalcFrameLength(ref SessionConfig cfg, int kbps, int pad)
        => 8 * ((cfg.Version + 1) * 72000 * kbps / cfg.SamplerateOut + pad);

    /// <summary><c>getframebits</c> - the current frame's length in bits.</summary>
    public static int GetFrameBits(LameInternalFlags gfc)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncResult eov = ref gfc.OvEnc;
        int bitRate;

        if (eov.BitrateIndex != 0)
            bitRate = LameTables.bitrate_table[cfg.Version][eov.BitrateIndex];
        else
            bitRate = cfg.AvgBitrate;

        // the main encoding routine toggles padding on and off; one layer III slot is 8 bits
        return CalcFrameLength(ref cfg, bitRate, eov.Padding);
    }

    /// <summary><c>get_max_frame_buffer_size_by_constraint</c>.</summary>
    public static int GetMaxFrameBufferSizeByConstraint(ref SessionConfig cfg, int constraint)
    {
        int maxmp3buf = 0;
        if (cfg.AvgBitrate > 320)
        {
            // in free format the buffer is constant
            if (constraint == MdbStrictIso)
                maxmp3buf = CalcFrameLength(ref cfg, cfg.AvgBitrate, 0);
            else
                maxmp3buf = 7680 * (cfg.Version + 1);   // 7680 bits per granule at most
        }
        else
        {
            int maxKbps;
            if (cfg.SamplerateOut < 16000)
                maxKbps = LameTables.bitrate_table[cfg.Version][8];  // allow 64 kbps (MPEG-2.5)
            else
                maxKbps = LameTables.bitrate_table[cfg.Version][14];

            switch (constraint)
            {
                default:
                case MdbDefault:
                    // Bouvigne's laxer reading of the ISO document instead of 8 * 960: every
                    // decoder should have room for a 320 kbps 32 kHz frame.
                    maxmp3buf = 8 * 1440;
                    break;
                case MdbStrictIso:
                    maxmp3buf = CalcFrameLength(ref cfg, maxKbps, 0);
                    break;
                case MdbMaximum:
                    maxmp3buf = 7680 * (cfg.Version + 1);
                    break;
            }
        }
        return maxmp3buf;
    }

    /// <summary><c>putheader_bits</c> - emit the header whose turn it is.</summary>
    private static void PutheaderBits(LameInternalFlags gfc)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncStateVar esv = ref gfc.SvEnc;
        ref BitStreamStruc bs = ref gfc.Bs;

        for (int i = 0; i < cfg.SideinfoLen; i++)
            bs.Buf![bs.BufByteIdx + i] = esv.Header![esv.WPtr].Buf[i];

        bs.BufByteIdx += cfg.SideinfoLen;
        bs.Totbit += cfg.SideinfoLen * 8;
        esv.WPtr = (esv.WPtr + 1) & (LameEncStateConstants.MaxHeaderBuf - 1);
    }

    /// <summary><c>putbits2</c> - write j bits, emitting a frame header if one falls due.</summary>
    private static void PutBits2(LameInternalFlags gfc, int val, int j)
    {
        ref EncStateVar esv = ref gfc.SvEnc;
        ref BitStreamStruc bs = ref gfc.Bs;

        while (j > 0)
        {
            int k;
            if (bs.BufBitIdx == 0)
            {
                bs.BufBitIdx = 8;
                bs.BufByteIdx++;
                if (esv.Header![esv.WPtr].WriteTiming == bs.Totbit)
                    PutheaderBits(gfc);
                bs.Buf![bs.BufByteIdx] = 0;
            }

            k = Math.Min(j, bs.BufBitIdx);
            j -= k;

            bs.BufBitIdx -= k;

            bs.Buf![bs.BufByteIdx] = (byte)(bs.Buf[bs.BufByteIdx] | ((val >> j) << bs.BufBitIdx));
            bs.Totbit += k;
        }
    }

    /// <summary><c>putbits_noheaders</c> - write j bits without emitting any frame header.</summary>
    private static void PutBitsNoheaders(LameInternalFlags gfc, int val, int j)
    {
        ref BitStreamStruc bs = ref gfc.Bs;

        while (j > 0)
        {
            int k;
            if (bs.BufBitIdx == 0)
            {
                bs.BufBitIdx = 8;
                bs.BufByteIdx++;
                bs.Buf![bs.BufByteIdx] = 0;
            }

            k = Math.Min(j, bs.BufBitIdx);
            j -= k;

            bs.BufBitIdx -= k;

            bs.Buf![bs.BufByteIdx] = (byte)(bs.Buf[bs.BufByteIdx] | ((val >> j) << bs.BufBitIdx));
            bs.Totbit += k;
        }
    }

    /// <summary>
    /// <c>drain_into_ancillary</c> - stuffing, because some combinations of bitrate, sample
    /// rate and channel count cannot fill a frame from main data alone.
    /// </summary>
    /// <remarks>
    /// The stuffing is not arbitrary: "LAME" and then the short version string, and only then
    /// alternating <c>ancillary_flag</c> bits. Those bytes are part of the output.
    /// </remarks>
    private static void DrainIntoAncillary(LameInternalFlags gfc, int remainingBits)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncStateVar esv = ref gfc.SvEnc;

        if (remainingBits >= 8) { PutBits2(gfc, 0x4c, 8); remainingBits -= 8; }  // 'L'
        if (remainingBits >= 8) { PutBits2(gfc, 0x41, 8); remainingBits -= 8; }  // 'A'
        if (remainingBits >= 8) { PutBits2(gfc, 0x4d, 8); remainingBits -= 8; }  // 'M'
        if (remainingBits >= 8) { PutBits2(gfc, 0x45, 8); remainingBits -= 8; }  // 'E'

        if (remainingBits >= 32)
        {
            string version = ShortVersion;
            for (int i = 0; i < version.Length && remainingBits >= 8; ++i)
            {
                remainingBits -= 8;
                PutBits2(gfc, version[i], 8);
            }
        }

        for (; remainingBits >= 1; remainingBits -= 1)
        {
            PutBits2(gfc, esv.AncillaryFlag, 1);
            esv.AncillaryFlag ^= cfg.DisableReservoir == 0 ? 1 : 0;
        }
    }

    /// <summary><c>writeheader</c> - write N bits into the header being built.</summary>
    private static void WriteHeader(LameInternalFlags gfc, int val, int j)
    {
        ref EncStateVar esv = ref gfc.SvEnc;
        int ptr = esv.Header![esv.HPtr].Ptr;

        while (j > 0)
        {
            int k = Math.Min(j, 8 - (ptr & 7));
            j -= k;
            esv.Header[esv.HPtr].Buf[ptr >> 3] =
                (byte)(esv.Header[esv.HPtr].Buf[ptr >> 3] | ((val >> j) << (8 - (ptr & 7) - k)));
            ptr += k;
        }
        esv.Header[esv.HPtr].Ptr = ptr;
    }

    /// <summary><c>CRC_update</c> - the bitwise CRC-16 of the header.</summary>
    private static int CrcUpdate(int value, int crc)
    {
        value <<= 8;
        for (int i = 0; i < 8; i++)
        {
            value <<= 1;
            crc <<= 1;

            if (((crc ^ value) & 0x10000) != 0)
                crc ^= Crc16Polynomial;
        }
        return crc;
    }

    /// <summary><c>CRC_writeheader</c> - fill in bytes 4 and 5 with the header's CRC.</summary>
    private static void CrcWriteHeader(LameInternalFlags gfc, int slot)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref HeaderSlot header = ref gfc.SvEnc.Header![slot];
        int crc = 0xffff;   // (jo) init crc16 for error_protection

        crc = CrcUpdate(header.Buf[2], crc);
        crc = CrcUpdate(header.Buf[3], crc);
        for (int i = 6; i < cfg.SideinfoLen; i++)
            crc = CrcUpdate(header.Buf[i], crc);

        header.Buf[4] = (byte)(crc >> 8);
        header.Buf[5] = (byte)(crc & 255);
    }

    /// <summary><c>encodeSideInfo2</c> - build this frame's header and side information.</summary>
    /// <remarks>
    /// The <c>table_select == 14</c> rewrites are the C's own guard against the unused Huffman
    /// row, which <c>choose_table</c> can return; it maps it to 16 on the way out so that the
    /// side info names a table a decoder has. <c>Huffmancode</c> is not given the same
    /// treatment, which is the NULL dereference recorded against <c>mp3enc-takehiro</c>.
    /// </remarks>
    private static void EncodeSideInfo2(LameInternalFlags gfc, int bitsPerFrame)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncResult eov = ref gfc.OvEnc;
        ref EncStateVar esv = ref gfc.SvEnc;
        ref IiiSideInfo l3Side = ref gfc.L3Side;
        int gr, ch;

        esv.Header![esv.HPtr].Ptr = 0;
        for (int i = 0; i < cfg.SideinfoLen; i++) esv.Header[esv.HPtr].Buf[i] = 0;

        if (cfg.SamplerateOut < 16000)
            WriteHeader(gfc, 0xffe, 12);
        else
            WriteHeader(gfc, 0xfff, 12);
        WriteHeader(gfc, cfg.Version, 1);
        WriteHeader(gfc, 4 - 3, 2);
        WriteHeader(gfc, cfg.ErrorProtection == 0 ? 1 : 0, 1);
        WriteHeader(gfc, eov.BitrateIndex, 4);
        WriteHeader(gfc, cfg.SamplerateIndex, 2);
        WriteHeader(gfc, eov.Padding, 1);
        WriteHeader(gfc, cfg.Extension, 1);
        WriteHeader(gfc, (int)cfg.Mode, 2);
        WriteHeader(gfc, eov.ModeExt, 2);
        WriteHeader(gfc, cfg.Copyright, 1);
        WriteHeader(gfc, cfg.Original, 1);
        WriteHeader(gfc, cfg.Emphasis, 2);
        if (cfg.ErrorProtection != 0)
            WriteHeader(gfc, 0, 16);    // dummy

        if (cfg.Version == 1)
        {
            // MPEG-1
            WriteHeader(gfc, l3Side.MainDataBegin, 9);

            if (cfg.ChannelsOut == 2)
                WriteHeader(gfc, l3Side.PrivateBits, 3);
            else
                WriteHeader(gfc, l3Side.PrivateBits, 5);

            for (ch = 0; ch < cfg.ChannelsOut; ch++)
                for (int band = 0; band < 4; band++)
                    WriteHeader(gfc, l3Side.Scfsi[ch * 4 + band], 1);

            for (gr = 0; gr < 2; gr++)
            {
                for (ch = 0; ch < cfg.ChannelsOut; ch++)
                {
                    ref GrInfo gi = ref l3Side.Tt[gr * 2 + ch];
                    WriteHeader(gfc, gi.Part23Length + gi.Part2Length, 12);
                    WriteHeader(gfc, gi.BigValues / 2, 9);
                    WriteHeader(gfc, gi.GlobalGain, 8);
                    WriteHeader(gfc, gi.ScalefacCompress, 4);

                    if (gi.BlockType != LameBlockType.Norm)
                    {
                        WriteHeader(gfc, 1, 1);     // window_switching_flag
                        WriteHeader(gfc, gi.BlockType, 2);
                        WriteHeader(gfc, gi.MixedBlockFlag, 1);

                        if (gi.TableSelect[0] == 14) gi.TableSelect[0] = 16;
                        WriteHeader(gfc, gi.TableSelect[0], 5);
                        if (gi.TableSelect[1] == 14) gi.TableSelect[1] = 16;
                        WriteHeader(gfc, gi.TableSelect[1], 5);

                        WriteHeader(gfc, gi.SubblockGain[0], 3);
                        WriteHeader(gfc, gi.SubblockGain[1], 3);
                        WriteHeader(gfc, gi.SubblockGain[2], 3);
                    }
                    else
                    {
                        WriteHeader(gfc, 0, 1);     // window_switching_flag
                        if (gi.TableSelect[0] == 14) gi.TableSelect[0] = 16;
                        WriteHeader(gfc, gi.TableSelect[0], 5);
                        if (gi.TableSelect[1] == 14) gi.TableSelect[1] = 16;
                        WriteHeader(gfc, gi.TableSelect[1], 5);
                        if (gi.TableSelect[2] == 14) gi.TableSelect[2] = 16;
                        WriteHeader(gfc, gi.TableSelect[2], 5);

                        WriteHeader(gfc, gi.Region0Count, 4);
                        WriteHeader(gfc, gi.Region1Count, 3);
                    }
                    WriteHeader(gfc, gi.Preflag, 1);
                    WriteHeader(gfc, gi.ScalefacScale, 1);
                    WriteHeader(gfc, gi.Count1TableSelect, 1);
                }
            }
        }
        else
        {
            // MPEG-2
            WriteHeader(gfc, l3Side.MainDataBegin, 8);
            WriteHeader(gfc, l3Side.PrivateBits, cfg.ChannelsOut);

            gr = 0;
            for (ch = 0; ch < cfg.ChannelsOut; ch++)
            {
                ref GrInfo gi = ref l3Side.Tt[gr * 2 + ch];
                WriteHeader(gfc, gi.Part23Length + gi.Part2Length, 12);
                WriteHeader(gfc, gi.BigValues / 2, 9);
                WriteHeader(gfc, gi.GlobalGain, 8);
                WriteHeader(gfc, gi.ScalefacCompress, 9);

                if (gi.BlockType != LameBlockType.Norm)
                {
                    WriteHeader(gfc, 1, 1);     // window_switching_flag
                    WriteHeader(gfc, gi.BlockType, 2);
                    WriteHeader(gfc, gi.MixedBlockFlag, 1);

                    if (gi.TableSelect[0] == 14) gi.TableSelect[0] = 16;
                    WriteHeader(gfc, gi.TableSelect[0], 5);
                    if (gi.TableSelect[1] == 14) gi.TableSelect[1] = 16;
                    WriteHeader(gfc, gi.TableSelect[1], 5);

                    WriteHeader(gfc, gi.SubblockGain[0], 3);
                    WriteHeader(gfc, gi.SubblockGain[1], 3);
                    WriteHeader(gfc, gi.SubblockGain[2], 3);
                }
                else
                {
                    WriteHeader(gfc, 0, 1);     // window_switching_flag
                    if (gi.TableSelect[0] == 14) gi.TableSelect[0] = 16;
                    WriteHeader(gfc, gi.TableSelect[0], 5);
                    if (gi.TableSelect[1] == 14) gi.TableSelect[1] = 16;
                    WriteHeader(gfc, gi.TableSelect[1], 5);
                    if (gi.TableSelect[2] == 14) gi.TableSelect[2] = 16;
                    WriteHeader(gfc, gi.TableSelect[2], 5);

                    WriteHeader(gfc, gi.Region0Count, 4);
                    WriteHeader(gfc, gi.Region1Count, 3);
                }

                WriteHeader(gfc, gi.ScalefacScale, 1);
                WriteHeader(gfc, gi.Count1TableSelect, 1);
            }
        }

        if (cfg.ErrorProtection != 0)
            CrcWriteHeader(gfc, esv.HPtr);

        {
            int old = esv.HPtr;

            esv.HPtr = (old + 1) & (LameEncStateConstants.MaxHeaderBuf - 1);
            esv.Header[esv.HPtr].WriteTiming =
                esv.Header[old].WriteTiming + bitsPerFrame;

            if (esv.HPtr == esv.WPtr)
            {
                // The C prints "MAX_HEADER_BUF too small" and carries on over a header it has
                // not emitted yet; see the file header for why this throws instead.
                throw new InvalidOperationException(
                    "MP3 encoder: the frame header ring overflowed, so a header that has not "
                  + "been written to the bitstream is about to be overwritten.");
            }
        }
    }

    /// <summary><c>huffman_coder_count1</c> - the count1 region's quadruples.</summary>
    private static int HuffmanCoderCount1(LameInternalFlags gfc, in GrInfo gi)
    {
        var h = LameTables.ht[gi.Count1TableSelect + 32];
        int bits = 0;
        int ix = gi.BigValues;
        int xr = gi.BigValues;

        for (int i = (gi.Count1 - gi.BigValues) / 4; i > 0; --i)
        {
            int huffbits = 0;
            int p = 0, v;

            v = gi.L3Enc[ix + 0];
            if (v != 0)
            {
                p += 8;
                if (gi.Xr[xr + 0] < 0.0f) huffbits++;
            }

            v = gi.L3Enc[ix + 1];
            if (v != 0)
            {
                p += 4;
                huffbits *= 2;
                if (gi.Xr[xr + 1] < 0.0f) huffbits++;
            }

            v = gi.L3Enc[ix + 2];
            if (v != 0)
            {
                p += 2;
                huffbits *= 2;
                if (gi.Xr[xr + 2] < 0.0f) huffbits++;
            }

            v = gi.L3Enc[ix + 3];
            if (v != 0)
            {
                p++;
                huffbits *= 2;
                if (gi.Xr[xr + 3] < 0.0f) huffbits++;
            }

            ix += 4;
            xr += 4;
            PutBits2(gfc, huffbits + h.Table![p], h.Hlen![p]);
            bits += h.Hlen[p];
        }
        return bits;
    }

    /// <summary>
    /// <c>Huffmancode</c> - the pseudocode of page 98 of the IS.
    /// </summary>
    /// <remarks>
    /// <c>cbits</c> is an <c>int16_t</c> that goes negative - it counts the sign bits, which
    /// are written as part of the code word's length - and <c>xbits</c> a <c>uint16_t</c> that
    /// then subtracts it. Both stay small enough that <c>int</c> holds them without the C's
    /// truncation ever mattering: <c>xbits</c> is at most twice <c>linbits</c>, which is 26.
    /// </remarks>
    private static int Huffmancode(
        LameInternalFlags gfc, int tableindex, int start, int end, in GrInfo gi)
    {
        var h = LameTables.ht[tableindex];
        uint linbits = h.Xlen;
        int bits = 0;

        if (tableindex == 0) return bits;

        for (int i = start; i < end; i += 2)
        {
            int cbits = 0;
            int xbits = 0;
            uint xlen = h.Xlen;
            uint ext = 0;
            uint x1 = (uint)gi.L3Enc[i];
            uint x2 = (uint)gi.L3Enc[i + 1];

            if (x1 != 0u)
            {
                if (gi.Xr[i] < 0.0f) ext++;
                cbits--;
            }

            if (tableindex > 15)
            {
                // use ESC words
                if (x1 >= 15u)
                {
                    uint linbitsX1 = x1 - 15u;
                    ext |= linbitsX1 << 1;
                    xbits = (int)linbits;
                    x1 = 15u;
                }

                if (x2 >= 15u)
                {
                    uint linbitsX2 = x2 - 15u;
                    ext <<= (int)linbits;
                    ext |= linbitsX2;
                    xbits += (int)linbits;
                    x2 = 15u;
                }
                xlen = 16;
            }

            if (x2 != 0u)
            {
                ext <<= 1;
                if (gi.Xr[i + 1] < 0.0f) ext++;
                cbits--;
            }

            x1 = x1 * xlen + x2;
            xbits -= cbits;
            cbits += h.Hlen![x1];

            PutBits2(gfc, h.Table![x1], cbits);
            PutBits2(gfc, (int)ext, xbits);
            bits += cbits + xbits;
        }
        return bits;
    }

    /// <summary><c>ShortHuffmancodebits</c> - short blocks have no region2.</summary>
    private static int ShortHuffmancodebits(LameInternalFlags gfc, in GrInfo gi)
    {
        int region1Start = 3 * gfc.ScalefacBand.S[3];
        if (region1Start > gi.BigValues) region1Start = gi.BigValues;

        int bits = Huffmancode(gfc, gi.TableSelect[0], 0, region1Start, gi);
        bits += Huffmancode(gfc, gi.TableSelect[1], region1Start, gi.BigValues, gi);
        return bits;
    }

    /// <summary><c>LongHuffmancodebits</c>.</summary>
    private static int LongHuffmancodebits(LameInternalFlags gfc, in GrInfo gi)
    {
        int i;
        int bigvalues, bits;
        int region1Start, region2Start;

        bigvalues = gi.BigValues;

        i = gi.Region0Count + 1;
        region1Start = gfc.ScalefacBand.L[i];
        i += gi.Region1Count + 1;
        region2Start = gfc.ScalefacBand.L[i];

        if (region1Start > bigvalues) region1Start = bigvalues;
        if (region2Start > bigvalues) region2Start = bigvalues;

        bits = Huffmancode(gfc, gi.TableSelect[0], 0, region1Start, gi);
        bits += Huffmancode(gfc, gi.TableSelect[1], region1Start, region2Start, gi);
        bits += Huffmancode(gfc, gi.TableSelect[2], region2Start, bigvalues, gi);
        return bits;
    }

    /// <summary><c>writeMainData</c> - the scalefactors and the Huffman data for every granule.</summary>
    private static int WriteMainData(LameInternalFlags gfc)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref IiiSideInfo l3Side = ref gfc.L3Side;
        int gr, ch, sfb, dataBits, totBits = 0;

        if (cfg.Version == 1)
        {
            // MPEG-1
            for (gr = 0; gr < 2; gr++)
            {
                for (ch = 0; ch < cfg.ChannelsOut; ch++)
                {
                    ref GrInfo gi = ref l3Side.Tt[gr * 2 + ch];
                    int slen1 = LameTakehiro.Slen1Tab[gi.ScalefacCompress];
                    int slen2 = LameTakehiro.Slen2Tab[gi.ScalefacCompress];
                    dataBits = 0;

                    for (sfb = 0; sfb < gi.Sfbdivide; sfb++)
                    {
                        if (gi.Scalefac[sfb] == -1) continue;   // scfsi is used
                        PutBits2(gfc, gi.Scalefac[sfb], slen1);
                        dataBits += slen1;
                    }
                    for (; sfb < gi.Sfbmax; sfb++)
                    {
                        if (gi.Scalefac[sfb] == -1) continue;   // scfsi is used
                        PutBits2(gfc, gi.Scalefac[sfb], slen2);
                        dataBits += slen2;
                    }

                    if (gi.BlockType == LameBlockType.Short)
                        dataBits += ShortHuffmancodebits(gfc, gi);
                    else
                        dataBits += LongHuffmancodebits(gfc, gi);

                    dataBits += HuffmanCoderCount1(gfc, gi);
                    totBits += dataBits;
                }
            }
        }
        else
        {
            // MPEG-2
            gr = 0;
            for (ch = 0; ch < cfg.ChannelsOut; ch++)
            {
                ref GrInfo gi = ref l3Side.Tt[gr * 2 + ch];
                int i, sfbPartition, scaleBits = 0;
                int[] partitionTable = LameQuantizePvt.NrOfSfbBlock
                    [gi.SfbPartitionTableRow / 3][gi.SfbPartitionTableRow % 3];
                dataBits = 0;

                sfb = 0;
                sfbPartition = 0;

                if (gi.BlockType == LameBlockType.Short)
                {
                    for (; sfbPartition < 4; sfbPartition++)
                    {
                        int sfbs = partitionTable[sfbPartition] / 3;
                        int slen = gi.Slen[sfbPartition];
                        for (i = 0; i < sfbs; i++, sfb++)
                        {
                            PutBits2(gfc, Math.Max(gi.Scalefac[sfb * 3 + 0], 0), slen);
                            PutBits2(gfc, Math.Max(gi.Scalefac[sfb * 3 + 1], 0), slen);
                            PutBits2(gfc, Math.Max(gi.Scalefac[sfb * 3 + 2], 0), slen);
                            scaleBits += 3 * slen;
                        }
                    }
                    dataBits += ShortHuffmancodebits(gfc, gi);
                }
                else
                {
                    for (; sfbPartition < 4; sfbPartition++)
                    {
                        int sfbs = partitionTable[sfbPartition];
                        int slen = gi.Slen[sfbPartition];
                        for (i = 0; i < sfbs; i++, sfb++)
                        {
                            PutBits2(gfc, Math.Max(gi.Scalefac[sfb], 0), slen);
                            scaleBits += slen;
                        }
                    }
                    dataBits += LongHuffmancodebits(gfc, gi);
                }
                dataBits += HuffmanCoderCount1(gfc, gi);
                totBits += scaleBits + dataBits;
            }
        }
        return totBits;
    }

    /// <summary>
    /// <c>compute_flushbits</c> - the bits needed to flush every buffered frame.
    /// </summary>
    /// <remarks>
    /// Call this only between frames, once <c>format_bitstream</c> has added all headers and
    /// data. It should equal the reservoir size, which is what <c>format_bitstream</c> checks.
    /// </remarks>
    public static int ComputeFlushbits(LameInternalFlags gfc, out int totalBytesOutput)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncStateVar esv = ref gfc.SvEnc;
        int flushbits, remainingHeaders;
        int bitsPerFrame;
        int lastPtr, firstPtr;

        firstPtr = esv.WPtr;        // the first header to add to the bitstream
        lastPtr = esv.HPtr - 1;     // the last header to add
        if (lastPtr == -1) lastPtr = LameEncStateConstants.MaxHeaderBuf - 1;

        flushbits = esv.Header![lastPtr].WriteTiming - gfc.Bs.Totbit;
        totalBytesOutput = flushbits;

        if (flushbits >= 0)
        {
            // some headers have not been written yet; take their size off
            remainingHeaders = 1 + lastPtr - firstPtr;
            if (lastPtr < firstPtr)
                remainingHeaders = 1 + lastPtr - firstPtr + LameEncStateConstants.MaxHeaderBuf;
            flushbits -= remainingHeaders * 8 * cfg.SideinfoLen;
        }

        // Finish the last frame. These bits are not needed to decode it, but some decoders
        // ignore a final frame that is short.
        bitsPerFrame = GetFrameBits(gfc);
        flushbits += bitsPerFrame;
        totalBytesOutput += bitsPerFrame;

        if (totalBytesOutput % 8 != 0)
            totalBytesOutput = 1 + totalBytesOutput / 8;
        else
            totalBytesOutput /= 8;
        totalBytesOutput += gfc.Bs.BufByteIdx + 1;

        if (flushbits < 0)
        {
            // The C prints "strange error flushing buffer" and returns the negative value,
            // which flush_bitstream then treats as "nothing to do".
            return flushbits;
        }
        return flushbits;
    }

    /// <summary><c>flush_bitstream</c>.</summary>
    public static void FlushBitstream(LameInternalFlags gfc)
    {
        ref EncStateVar esv = ref gfc.SvEnc;
        ref IiiSideInfo l3Side = ref gfc.L3Side;
        int flushbits;

        if ((flushbits = ComputeFlushbits(gfc, out _)) < 0) return;
        DrainIntoAncillary(gfc, flushbits);

        // Every frame has been padded out with ancillary data, which is the same as filling
        // the reservoir with it.
        esv.ResvSize = 0;
        l3Side.MainDataBegin = 0;
    }

    /// <summary><c>add_dummy_byte</c>.</summary>
    public static void AddDummyByte(LameInternalFlags gfc, byte val, uint n)
    {
        ref EncStateVar esv = ref gfc.SvEnc;

        while (n-- > 0u)
        {
            PutBitsNoheaders(gfc, val, 8);

            for (int i = 0; i < LameEncStateConstants.MaxHeaderBuf; ++i)
                esv.Header![i].WriteTiming += 8;
        }
    }

    /// <summary>
    /// <c>format_bitstream</c> - write one quantised frame into the bit buffer.
    /// </summary>
    public static int FormatBitstream(LameInternalFlags gfc)
    {
        ref SessionConfig cfg = ref gfc.Cfg;
        ref EncStateVar esv = ref gfc.SvEnc;
        ref IiiSideInfo l3Side = ref gfc.L3Side;
        int bits;
        int bitsPerFrame;

        bitsPerFrame = GetFrameBits(gfc);
        DrainIntoAncillary(gfc, l3Side.ResvDrainPre);

        EncodeSideInfo2(gfc, bitsPerFrame);
        bits = 8 * cfg.SideinfoLen;
        bits += WriteMainData(gfc);
        DrainIntoAncillary(gfc, l3Side.ResvDrainPost);
        bits += l3Side.ResvDrainPost;

        l3Side.MainDataBegin += (bitsPerFrame - bits) / 8;

        // Does the bit count we think we have match what we actually wrote?
        if (ComputeFlushbits(gfc, out _) != esv.ResvSize)
        {
            throw new InvalidOperationException(
                "MP3 encoder: internal buffer inconsistency, flushbits does not match "
              + $"ResvSize ({ComputeFlushbits(gfc, out _)} against {esv.ResvSize}).");
        }

        if (l3Side.MainDataBegin * 8 != esv.ResvSize)
        {
            // The C calls this a fatal error, prints a list of likely causes, sets ResvSize
            // from main_data_begin and carries on. See the file header for why this throws.
            throw new InvalidOperationException(
                "MP3 encoder: bit reservoir error. main_data_begin is "
              + $"{8 * l3Side.MainDataBegin} bits and ResvSize is {esv.ResvSize}; drain pre "
              + $"{l3Side.ResvDrainPre}, drain post {l3Side.ResvDrainPost}, header and side "
              + $"info {8 * cfg.SideinfoLen}, total {bits} bits (remainder {bits % 8}), "
              + $"bits per frame {bitsPerFrame}.");
        }

        if (gfc.Bs.Totbit > 1000000000)
        {
            // Reset the counter so totbit cannot overflow - eight hours at 128 kbps.
            for (int i = 0; i < LameEncStateConstants.MaxHeaderBuf; ++i)
                esv.Header![i].WriteTiming -= gfc.Bs.Totbit;
            gfc.Bs.Totbit = 0;
        }

        return 0;
    }

    /// <summary>
    /// <c>do_gain_analysis</c> - the whole body is inside <c>#ifdef DECODE_ON_THE_FLY</c>,
    /// which needs mpglib and gain_analysis.c; both are out of scope.
    /// </summary>
    private static int DoGainAnalysis(LameInternalFlags gfc, ReadOnlySpan<byte> buffer, int minimum)
    {
        _ = gfc;
        _ = buffer;
        return minimum;
    }

    /// <summary><c>do_copy_buffer</c>.</summary>
    private static int DoCopyBuffer(LameInternalFlags gfc, Span<byte> buffer, int size)
    {
        ref BitStreamStruc bs = ref gfc.Bs;
        int minimum = bs.BufByteIdx + 1;
        if (minimum <= 0) return 0;
        if (minimum > size) return -1;      // the buffer is too small

        bs.Buf.AsSpan(0, minimum).CopyTo(buffer);
        bs.BufByteIdx = -1;
        bs.BufBitIdx = 0;
        return minimum;
    }

    /// <summary>
    /// <c>copy_buffer</c> - move the bit buffer into the caller's.
    /// </summary>
    /// <param name="mp3data">0 for an ID3 or VBR tag, 1 for real frame data.</param>
    public static int CopyBuffer(LameInternalFlags gfc, Span<byte> buffer, int size, int mp3data)
    {
        int minimum = DoCopyBuffer(gfc, buffer, size);
        if (minimum > 0 && mp3data != 0)
        {
            UpdateMusicCrc(ref gfc.NMusicCrc, buffer, minimum);

            // the byte count the Xing/LAME header would record for seeking
            gfc.VbrSeekTable.NBytesWritten += (ulong)minimum;

            return DoGainAnalysis(gfc, buffer, minimum);
        }
        return minimum;
    }

    /// <summary><c>init_bit_stream_w</c>.</summary>
    public static void InitBitStreamW(LameInternalFlags gfc)
    {
        ref EncStateVar esv = ref gfc.SvEnc;

        esv.HPtr = esv.WPtr = 0;
        esv.Header![esv.HPtr].WriteTiming = 0;

        gfc.Bs.Buf = new byte[BufferSize];
        gfc.Bs.BufSize = BufferSize;
        gfc.Bs.BufByteIdx = -1;
        gfc.Bs.BufBitIdx = 0;
        gfc.Bs.Totbit = 0;
    }
}

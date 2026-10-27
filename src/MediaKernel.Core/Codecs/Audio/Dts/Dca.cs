// DTS Coherent Acoustics - port of ffmpeg-master/libavcodec/dca.c.
//
// Two jobs: normalise the four ways a DTS core frame can be laid out in a file into the one the
// decoder reads, and parse the 18-byte core frame header.

using System;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Audio.Dts;

/// <summary><c>dca.c</c>.</summary>
internal static class Dca
{
    /// <summary>
    /// <c>avpriv_dca_convert_bitstream</c> - rewrite a frame into big-endian 16-bit form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DTS is carried four ways and all four occur in the wild: 16-bit big-endian (the one the
    /// decoder wants), 16-bit little-endian, and two 14-bit forms where each 16-bit word carries
    /// only 14 payload bits. The 14-bit forms exist because DTS was originally laid into a CD
    /// audio track, where some bits were not available. Nothing downstream knows about any of
    /// this: <see cref="ParseCoreFrameHeader"/> and the core decoder only ever see
    /// <see cref="DcaSyncwords.CoreBe"/>.
    /// </para>
    /// <para>
    /// DIVERGENCE, recorded: FFmpeg repacks the 14-bit forms with <c>PutBitContext</c>. This tree
    /// has no general bit writer, and importing one for a fourteen-bits-at-a-time copy would be
    /// more code than the copy. The accumulator below is what <c>put_bits</c> does for this one
    /// case, with the same most-significant-bit-first order and the same final flush of a partial
    /// byte.
    /// </para>
    /// </remarks>
    /// <returns>Bytes written to <paramref name="dst"/>, or -1 for an unrecognised layout.</returns>
    public static int ConvertBitstream(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int srcSize = src.Length;
        if (srcSize > dst.Length)
            srcSize = dst.Length;
        if (srcSize < 4)
            return -1;

        uint mrk = (uint)(src[0] << 24 | src[1] << 16 | src[2] << 8 | src[3]);
        switch (mrk)
        {
            case DcaSyncwords.CoreBe:
            case DcaSyncwords.Substream:
                src[..srcSize].CopyTo(dst);
                return srcSize;

            case DcaSyncwords.CoreLe:
                // Byte-swap each 16-bit word. The last word of an odd-length frame reads one
                // byte past srcSize in the C too; here the odd tail byte is moved alone.
                for (int i = 0; i < (srcSize + 1) >> 1; i++)
                {
                    int at = i * 2;
                    byte lo = src[at];
                    byte hi = at + 1 < srcSize ? src[at + 1] : (byte)0;
                    dst[at] = hi;
                    if (at + 1 < dst.Length)
                        dst[at + 1] = lo;
                }
                return srcSize;

            case DcaSyncwords.Core14BBe:
            case DcaSyncwords.Core14BLe:
            {
                bool bigEndian = mrk == DcaSyncwords.Core14BBe;
                ulong accumulator = 0;
                int held = 0;
                int written = 0;

                for (int i = 0; i < (srcSize + 1) >> 1; i++)
                {
                    int at = i * 2;
                    byte a = src[at];
                    byte b = at + 1 < srcSize ? src[at + 1] : (byte)0;
                    int word = bigEndian ? (a << 8) | b : (b << 8) | a;

                    accumulator = (accumulator << 14) | (uint)(word & 0x3FFF);
                    held += 14;

                    while (held >= 8)
                    {
                        if (written >= dst.Length)
                            return written;
                        held -= 8;
                        dst[written++] = (byte)(accumulator >> held);
                    }
                }

                // flush_put_bits: a partial byte is left-aligned and zero-filled.
                if (held > 0 && written < dst.Length)
                    dst[written++] = (byte)(accumulator << (8 - held));

                return written;
            }

            default:
                return -1;
        }
    }

    /// <summary>
    /// <c>ff_dca_parse_core_frame_header</c> - parse and validate the core frame header.
    /// </summary>
    /// <remarks>
    /// Every one of the nine ways this can fail is a real constraint of the format rather than a
    /// sanity check, and each returns its own <see cref="DcaParseError"/> so a stream that is
    /// refused says which rule it broke. The reader must hold at least the first 120 bits; bits
    /// past the end of the buffer read as zero, which fails the sync word check.
    /// </remarks>
    public static DcaParseError ParseCoreFrameHeader(ref DcaCoreFrameHeader h, ref GetBitContext gb)
    {
        if (gb.GetBits(32) != DcaSyncwords.CoreBe)
            return DcaParseError.SyncWord;

        h.NormalFrame = (byte)gb.GetBits1();
        h.DeficitSamples = (byte)(gb.GetBits(5) + 1);
        if (h.DeficitSamples != DcaLimits.PcmBlockSamples)
            return DcaParseError.DeficitSamples;

        h.CrcPresent = (byte)gb.GetBits1();
        h.Npcmblocks = (byte)(gb.GetBits(7) + 1);
        if ((h.Npcmblocks & (DcaLimits.SubbandSamples - 1)) != 0)
            return DcaParseError.PcmBlocks;

        h.FrameSize = (ushort)(gb.GetBits(14) + 1);
        if (h.FrameSize < 96)
            return DcaParseError.FrameSize;

        h.AudioMode = (byte)gb.GetBits(6);
        if (h.AudioMode >= (int)DcaCoreAudioMode.Count)
            return DcaParseError.AudioMode;

        h.SrCode = (byte)gb.GetBits(4);
        if (DcaData.SampleRates[h.SrCode] == 0)
            return DcaParseError.SampleRate;

        h.BrCode = (byte)gb.GetBits(5);
        if (gb.GetBits1() != 0)
            return DcaParseError.ReservedBit;

        h.DrcPresent = (byte)gb.GetBits1();
        h.TsPresent = (byte)gb.GetBits1();
        h.AuxPresent = (byte)gb.GetBits1();
        h.HdcdMaster = (byte)gb.GetBits1();
        h.ExtAudioType = (byte)gb.GetBits(3);
        h.ExtAudioPresent = (byte)gb.GetBits1();
        h.SyncSsf = (byte)gb.GetBits1();
        h.LfePresent = (byte)gb.GetBits(2);
        if (h.LfePresent == DcaCoreLfeFlag.Invalid)
            return DcaParseError.LfeFlag;

        h.PredictorHistory = (byte)gb.GetBits1();
        if (h.CrcPresent != 0)
            gb.SkipBits(16);
        h.FilterPerfect = (byte)gb.GetBits1();
        h.EncoderRev = (byte)gb.GetBits(4);
        h.CopyHist = (byte)gb.GetBits(2);
        h.PcmrCode = (byte)gb.GetBits(3);
        if (DcaData.BitsPerSample[h.PcmrCode] == 0)
            return DcaParseError.PcmRes;

        h.SumdiffFront = (byte)gb.GetBits1();
        h.SumdiffSurround = (byte)gb.GetBits1();
        h.DnCode = (byte)gb.GetBits(4);
        return DcaParseError.None;
    }

    /// <summary>
    /// <c>avpriv_dca_parse_core_frame_header</c> - the same, from a buffer rather than a reader.
    /// </summary>
    public static DcaParseError ParseCoreFrameHeaderPublic(ref DcaCoreFrameHeader h, byte[] buf, int size)
    {
        var gb = default(GetBitContext);
        gb.InitGetBits8(buf, size);
        return ParseCoreFrameHeader(ref h, ref gb);
    }

    /// <summary>
    /// <c>ff_dca_count_chs_for_mask</c> (<c>dca.h</c>) - how many individual channels a
    /// DCASpeakerPair mask stands for.
    /// </summary>
    /// <remarks>
    /// The bits in <c>0xae66</c> name speaker *pairs* rather than single speakers, so each one
    /// present counts twice. Shifting them up by 16 and taking one popcount over the result is
    /// how the C does it, and it is cheaper than a second popcount.
    /// </remarks>
    public static int CountChsForMask(int mask)
        => System.Numerics.BitOperations.PopCount(
               (uint)((mask & 0xffff) | ((mask & 0xae66) << 16)));
}

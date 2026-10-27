// Translation of libavcodec/vorbis_parser.c - works out how many samples a Vorbis packet
// decodes to without decoding it.
//
// A container that stores Vorbis in Ogg needs this: an Ogg page carries only the granule
// position of the last packet that finishes on it, so the timestamp of every other packet has
// to be worked backwards from the durations of the packets around it. The duration is decided
// by the block size of the packet's mode and of the one before it, both of which are readable
// from the first byte of the packet once the Setup header has been read.

using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Vorbis;

/// <summary>Which special packet <see cref="VorbisPacketParser.ParseFrame"/> was given.</summary>
[Flags]
public enum VorbisPacketFlags
{
    None = 0,
    Header = 1,
    Comment = 2,
    Setup = 4,
}

/// <summary>
/// Reports the duration in samples of each Vorbis packet. A port of
/// <c>AVVorbisParseContext</c>; the method names are FFmpeg's with the <c>av_vorbis_parse_</c>
/// prefix dropped.
/// </summary>
public sealed class VorbisPacketParser
{
    private readonly int[] blocksize = new int[2];
    private readonly int[] modeBlocksize = new int[64];
    private int modeCount;
    private int modeMask;
    private int prevMask;
    private int previousBlocksize;

    /// <summary>Whether both headers parsed. A parser without them reports every duration as 0.</summary>
    public bool ValidExtradata { get; private set; }

    /// <summary>
    /// Reads the identification and setup headers. Returns false if either is malformed, which
    /// leaves the parser reporting zero durations rather than wrong ones.
    /// </summary>
    public bool Initialize(ReadOnlySpan<byte> identHeader, ReadOnlySpan<byte> setupHeader)
    {
        ValidExtradata = false;

        if (!ParseIdHeader(identHeader) || !ParseSetupHeader(setupHeader))
            return false;

        ValidExtradata = true;
        previousBlocksize = blocksize[modeBlocksize[0]];
        return true;
    }

    /// <summary>parse_id_header.</summary>
    private bool ParseIdHeader(ReadOnlySpan<byte> buf)
    {
        // Id header should be 30 bytes
        if (buf.Length < 30)
            return false;

        if (buf[0] != 1 || !buf[1..7].SequenceEqual("vorbis"u8))
            return false;

        // Framing bit
        if ((buf[29] & 0x1) == 0)
            return false;

        blocksize[0] = 1 << (buf[28] & 0xF);
        blocksize[1] = 1 << (buf[28] >> 4);
        return true;
    }

    /// <summary>
    /// parse_setup_header. The mode list sits at the very end of the Setup header behind a long
    /// run of variable-sized fields, so FFmpeg reverses the buffer and searches backwards from
    /// the framing bit for a plausible mode count rather than parsing the whole header. This is
    /// that search, byte for byte; it can in principle match a false positive, and FFmpeg says so.
    /// </summary>
    private bool ParseSetupHeader(ReadOnlySpan<byte> buf)
    {
        // Avoid overread
        if (buf.Length < 7)
            return false;

        if (buf[0] != 5 || !buf[1..7].SequenceEqual("vorbis"u8))
            return false;

        // Reverse bytes so we can easily read backwards with GetBits
        var reversed = new byte[buf.Length];
        for (int i = 0; i < buf.Length; i++)
            reversed[i] = buf[buf.Length - 1 - i];

        var gb = new GetBitContext();
        gb.InitGetBits8(reversed, reversed.Length);

        int gotFramingBit = 0;
        while (gb.GetBitsLeft() > 97)
        {
            if (gb.GetBits1() != 0)
            {
                gotFramingBit = gb.GetBitsCount();
                break;
            }
        }
        if (gotFramingBit == 0)
            return false;

        int count = 0;
        bool gotModeHeader = false;
        int lastModeCount = 0;

        while (gb.GetBitsLeft() >= 97)
        {
            if (gb.GetBits(8) > 63 || gb.GetBits(16) != 0 || gb.GetBits(16) != 0)
                break;
            gb.SkipBits(1);
            count++;
            if (count > 64)
                break;
            var lookahead = gb;
            if (lookahead.GetBits(6) + 1 == count)
            {
                gotModeHeader = true;
                lastModeCount = count;
            }
        }
        if (!gotModeHeader)
            return false;

        // The mode count is limited to 63 so the previous block flag is in the first byte.
        if (lastModeCount > 63)
            return false;

        modeCount = lastModeCount;

        // The number of bits needed to code the mode, turned into a mask over the first byte.
        modeMask = ((1 << (Log2(modeCount - 1) + 1)) - 1) << 1;
        prevMask = (modeMask | 0x1) + 1;

        gb = new GetBitContext();
        gb.InitGetBits8(reversed, reversed.Length);
        gb.SkipBits(gotFramingBit);
        for (int i = modeCount - 1; i >= 0; i--)
        {
            gb.SkipBits(40);
            modeBlocksize[i] = (int)gb.GetBits1();
        }

        return true;
    }

    /// <summary>av_log2.</summary>
    private static int Log2(int value) => value <= 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)value);

    /// <summary>
    /// av_vorbis_parse_frame_flags. Returns the packet's duration in samples, or -1 for a packet
    /// that cannot be one. A header, comment or setup packet has no duration and returns 0 with
    /// the matching flag set.
    /// </summary>
    public int ParseFrame(ReadOnlySpan<byte> buf, out VorbisPacketFlags flags)
    {
        flags = VorbisPacketFlags.None;

        if (!ValidExtradata || buf.Length == 0)
            return 0;

        int previous = previousBlocksize;

        if ((buf[0] & 1) != 0)
        {
            flags = buf[0] switch
            {
                1 => VorbisPacketFlags.Header,
                3 => VorbisPacketFlags.Comment,
                5 => VorbisPacketFlags.Setup,
                // An unknown type is ignored, exactly as FFmpeg ignores it.
                _ => VorbisPacketFlags.None,
            };

            // Special packets have no duration.
            return 0;
        }

        int mode = modeCount == 1 ? 0 : (buf[0] & modeMask) >> 1;
        if (mode >= modeCount)
            return -1;

        if (modeBlocksize[mode] != 0)
        {
            int flag = (buf[0] & prevMask) != 0 ? 1 : 0;
            previous = blocksize[flag];
        }

        int currentBlocksize = blocksize[modeBlocksize[mode]];
        previousBlocksize = currentBlocksize;
        return (previous + currentBlocksize) >> 2;
    }

    /// <summary>av_vorbis_parse_reset.</summary>
    public void Reset()
    {
        if (ValidExtradata)
            previousBlocksize = blocksize[0];
    }
}

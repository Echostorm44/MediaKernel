// MediaKernel - Maxis XA demuxer.
//
// A translation of ffmpeg-master/libavformat/xa.c. The format is a 24-byte header and then
// nothing but blocks of EA ADPCM, one byte of coefficients and shift per channel followed by
// fourteen bytes of nibbles, so the demuxer's whole job is the header and a fixed packet size.

using System;
using System.Buffers.Binary;
using System.IO;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Maxis XA: the audio format of SimCity 2000 and its contemporaries.
/// </summary>
public sealed class XaDec
{
    /// <summary>The C's AVPROBE_SCORE_EXTENSION, which is what xa_probe returns.</summary>
    public const int ProbeScoreExtension = 50;

    private static readonly uint Xa00Tag = MkTag('X', 'A', (char)0, (char)0);
    private static readonly uint Xai0Tag = MkTag('X', 'A', 'I', (char)0);
    private static readonly uint Xaj0Tag = MkTag('X', 'A', 'J', (char)0);

    private readonly Stream stream;

    // ── MaxisXADemuxContext ─────────────────────────────────────────────
    private uint outSize;
    private uint sentBytes;

    public XaDec(Stream input) => stream = input;

    /// <summary>Always <c>adpcm_ea_maxis_xa</c>: the format holds nothing else.</summary>
    public string CodecId => "adpcm_ea_maxis_xa";

    public int SampleRate { get; private set; }

    public int ChannelCount { get; private set; }

    public long BitRate { get; private set; }

    /// <summary>Decoded bytes the header says the file holds, before compression.</summary>
    public uint OutSize => outSize;

    /// <summary>
    /// Bytes in one packet: a one-byte header and fourteen bytes of samples per channel. The
    /// ADPCM decoder needs it as its block alignment, and the file does not state it - the
    /// header's own block-align field is one of the three the C skips.
    /// </summary>
    public int BlockAlign => 15 * ChannelCount;

    /// <summary>xa.c's <c>xa_probe</c>.</summary>
    public static int Probe(ReadOnlySpan<byte> p)
    {
        if (p.Length < 24)
            return 0;
        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(p);
        if (tag != Xa00Tag && tag != Xai0Tag && tag != Xaj0Tag)
            return 0;

        int channels = BinaryPrimitives.ReadUInt16LittleEndian(p[10..]);
        int srate = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[12..]);
        int bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(p[22..]);
        if (channels == 0 || channels > 8 || srate == 0 || srate > 192000 ||
            bitsPerSample < 4 || bitsPerSample > 32)
            return 0;
        return ProbeScoreExtension;
    }

    /// <summary>xa.c's <c>xa_read_header</c>.</summary>
    public void ReadHeader()
    {
        /*Set up the XA Audio Decoder*/
        Skip(4);                /* Skip the XA ID */
        outSize = Rl32();
        Skip(2);                /* Skip the tag */
        ChannelCount = Rl16();
        SampleRate = (int)Rl32();
        Skip(4);                /* Skip average byte rate */
        Skip(2);                /* Skip block align */
        Skip(2);                /* Skip bits-per-sample */

        if (ChannelCount == 0 || SampleRate == 0)
            throw new InvalidDataException("XA: the header states no channels or no sample rate.");

        BitRate = Math.Clamp(15L * ChannelCount * 8 * SampleRate / 28, 0, int.MaxValue);
    }

    /// <summary>
    /// xa.c's <c>xa_read_packet</c>. Returns false at end of stream, which here is the
    /// decoded size the header declared rather than the end of the file.
    /// </summary>
    public bool ReadPacket(Span<byte> buffer, out int size, out int duration)
    {
        size = 0;
        duration = 0;

        if (sentBytes >= outSize)
            return false;
        /* 1 byte header and 14 bytes worth of samples * number channels per block */
        int packetSize = 15 * ChannelCount;

        int read = Read(buffer[..packetSize]);
        if (read <= 0)
            return false;

        sentBytes += (uint)packetSize;
        size = read;
        duration = 28;
        return true;
    }

    private static uint MkTag(char a, char b, char c, char d)
        => ((uint)d << 24) | ((uint)c << 16) | ((uint)b << 8) | a;

    private int Read(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = stream.Read(destination[total..]);
            if (n <= 0) break;
            total += n;
        }
        if (total < destination.Length)
            destination[total..].Clear();
        return total;
    }

    private int Rl16()
    {
        Span<byte> b = stackalloc byte[2];
        Read(b);
        return BinaryPrimitives.ReadUInt16LittleEndian(b);
    }

    private uint Rl32()
    {
        Span<byte> b = stackalloc byte[4];
        Read(b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    private void Skip(long count) => stream.Position += count;
}

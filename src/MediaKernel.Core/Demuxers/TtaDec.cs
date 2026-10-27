// MediaKernel - TTA (True Audio) demuxer.
//
// A translation of ffmpeg-master/libavformat/tta.c. A TTA file is a 22-byte header, a seek
// table of one 32-bit compressed size per frame, and then the frames: the demuxer's whole job
// is that table, because a TTA frame does not state its own length.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// True Audio. Frames are a fixed 256/245 of a second each, except the last.
/// </summary>
public sealed class TtaDec
{
    /// <summary>The C's AVPROBE_SCORE_EXTENSION + 30.</summary>
    public const int ProbeScore = 80;

    private const int HeaderSize = 22;

    private readonly Stream stream;

    // ── TTAContext ──────────────────────────────────────────────────────
    private int totalFrames, currentFrame;
    private int frameSize;
    private int lastFrameSize;

    // av_add_index_entry: position, timestamp and the compressed size, which for this format
    // is the only place a frame's length is written down.
    private readonly List<(long Pos, long Timestamp, int Size)> indexEntries = [];

    public TtaDec(Stream input) => stream = input;

    /// <summary>Always <c>tta</c>.</summary>
    public string CodecId => "tta";

    public int SampleRate { get; private set; }

    public int ChannelCount { get; private set; }

    /// <summary>Bits per coded sample - 8, 16 or 24.</summary>
    public int BitsPerCodedSample { get; private set; }

    /// <summary>Total sample frames, from the header.</summary>
    public uint NbSamples { get; private set; }

    /// <summary>The 22-byte TTA1 header, which is also the decoder's extradata.</summary>
    public byte[] Extradata { get; private set; } = [];

    /// <summary>Frames in the file, which is the length of the seek table.</summary>
    public int TotalFrames => totalFrames;

    /// <summary>tta.c's <c>tta_probe</c>.</summary>
    public static int Probe(ReadOnlySpan<byte> p)
    {
        if (p.Length < 14)
            return 0;
        if (BinaryPrimitives.ReadUInt32LittleEndian(p) == MkTag('T', 'T', 'A', '1') &&
            (BinaryPrimitives.ReadUInt16LittleEndian(p[4..]) == 1 ||
             BinaryPrimitives.ReadUInt16LittleEndian(p[4..]) == 2) &&
            BinaryPrimitives.ReadUInt16LittleEndian(p[6..]) > 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(p[8..]) > 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(p[10..]) > 0)
            return ProbeScore;
        return 0;
    }

    /// <summary>tta.c's <c>tta_read_header</c>.</summary>
    /// <remarks>
    /// DIVERGENCE: ff_id3v1_read and ff_ape_parse_tag are not called. Both read metadata that
    /// nothing downstream here carries, and the APE call in the C reads and then seeks back,
    /// so skipping it changes no position. The ID3v1 tag lives in the last 128 bytes of the
    /// file and is not in the way either.
    /// <para>
    /// Both CRCs are checked unconditionally. The C checks them only under AV_EF_CRCCHECK,
    /// which ffmpeg does not set by default; a file whose seek table does not checksum has a
    /// frame table that cannot be trusted, and every frame length comes out of it.
    /// </para>
    /// </remarks>
    public void ReadHeader()
    {
        byte[] header = new byte[HeaderSize];
        ReadInto(header);

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != MkTag('T', 'T', 'A', '1'))
            throw new InvalidDataException("TTA: no TTA1 signature.");

        int channels = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
        int sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(10));
        if (sampleRate <= 0 || sampleRate > 1000000)
            throw new InvalidDataException($"TTA: nonsense samplerate {sampleRate}.");

        uint nbSamples = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14));
        if (nbSamples == 0)
            throw new InvalidDataException("TTA: invalid number of samples.");

        uint crc = Crc32IeeeLe(uint.MaxValue, header.AsSpan(0, 18)) ^ uint.MaxValue;
        if (crc != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18)))
            throw new InvalidDataException("TTA: header CRC error.");

        frameSize = sampleRate * 256 / 245;
        lastFrameSize = (int)(nbSamples % (uint)frameSize);
        if (lastFrameSize == 0)
            lastFrameSize = frameSize;
        totalFrames = (int)(nbSamples / (uint)frameSize) + (lastFrameSize < frameSize ? 1 : 0);
        currentFrame = 0;

        if (totalFrames >= (int.MaxValue - 4) / sizeof(uint) || totalFrames <= 0)
            throw new InvalidDataException($"TTA: totalframes {totalFrames} invalid.");

        SampleRate = sampleRate;
        ChannelCount = channels;
        BitsPerCodedSample = bps;
        NbSamples = nbSamples;
        Extradata = header;

        long framepos = stream.Position;
        framepos += 4L * totalFrames + 4;

        // ffio_init_checksum over the seek table, which is the same CRC run over the bytes as
        // they are read rather than over a buffer.
        var table = new byte[4 * totalFrames];
        ReadInto(table);
        for (int i = 0; i < totalFrames; i++)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(4 * i));
            indexEntries.Add((framepos, i * (long)frameSize, (int)size));
            framepos += size;
        }

        uint tableCrc = Crc32IeeeLe(uint.MaxValue, table) ^ uint.MaxValue;
        Span<byte> stored = stackalloc byte[4];
        ReadInto(stored);
        if (tableCrc != BinaryPrimitives.ReadUInt32LittleEndian(stored))
            throw new InvalidDataException("TTA: seek table CRC error.");
    }

    /// <summary>
    /// tta.c's <c>tta_read_packet</c>. Returns false at end of stream.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE: av_get_packet allocates per packet; the caller owns the buffer here.
    /// </remarks>
    public bool ReadPacket(ref byte[] buffer, out int size, out long dts, out int duration)
    {
        size = 0;
        dts = 0;
        duration = 0;

        // FIXME!
        if (currentFrame >= totalFrames)
            return false;

        int wanted = indexEntries[currentFrame].Size;
        if (buffer.Length < wanted)
            buffer = new byte[wanted];

        int read = Read(buffer.AsSpan(0, wanted));
        dts = indexEntries[currentFrame++].Timestamp;
        duration = currentFrame == totalFrames ? lastFrameSize : frameSize;
        size = read;
        return read > 0;
    }

    /// <summary>tta.c's <c>tta_read_seek</c>. Returns false where the C returns -1.</summary>
    public bool ReadSeek(long timestamp)
    {
        int index = IndexSearchTimestamp(timestamp);
        if (index < 0)
            return false;
        stream.Position = indexEntries[index].Pos;
        currentFrame = index;
        return true;
    }

    /// <summary>av_index_search_timestamp with no flags: the last entry at or before it.</summary>
    private int IndexSearchTimestamp(long timestamp)
    {
        int lo = 0, hi = indexEntries.Count - 1;
        if (hi < 0 || indexEntries[0].Timestamp > timestamp)
            return -1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (indexEntries[mid].Timestamp <= timestamp) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private static uint MkTag(char a, char b, char c, char d)
        => ((uint)d << 24) | ((uint)c << 16) | ((uint)b << 8) | a;

    /// <summary>
    /// <c>av_crc(av_crc_get_table(AV_CRC_32_IEEE_LE), ...)</c> - the reflected CRC-32 of
    /// polynomial 0xEDB88320, which is also what <c>ff_crcEDB88320_update</c> runs over the
    /// seek table.
    /// </summary>
    private static uint Crc32IeeeLe(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = Crc32Table[(byte)(crc ^ b)] ^ (crc >> 8);
        return crc;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

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

    private void ReadInto(Span<byte> destination)
    {
        if (Read(destination) != destination.Length)
            throw new EndOfStreamException("TTA: the file ends inside its header or seek table.");
    }
}

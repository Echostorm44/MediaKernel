// MediaKernel - Core Audio Format (CAF) demuxer.
//
// A translation of ffmpeg-master/libavformat/cafdec.c, with the tag table from caf.c and the
// two helpers cafdec.c reaches into other files for: ff_mov_get_lpcm_codec_id (isom.h), which
// is ff_get_pcm_codec_id (utils.c) with CAF's flag convention, and ff_mp4_read_descr_len
// (isom.c), which is how a variable packet table stores its sizes.
//
// The file this replaces was written from the format documentation rather than from ffmpeg,
// and its tests were built out of buffers it wrote itself, so nothing in it had ever been
// compared with ffmpeg's reading of a real CAF file.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// One packet as <see cref="CafDec.ReadPacket"/> reports it: everything about it except the
/// bytes, which the caller's buffer holds.
/// </summary>
/// <remarks>
/// <see cref="Priming"/> and <see cref="Remainder"/> are the <c>AV_PKT_DATA_SKIP_SAMPLES</c>
/// side data the C attaches - the encoder delay at the front of the first packet and the
/// padding at the end of the last. Nothing in this pipeline carries per-packet side data, so
/// they are reported here and trimmed by whoever consumes the decoded samples.
/// </remarks>
public readonly struct CafPacketInfo(int size, long frames, long pts, uint priming, uint remainder)
{
    /// <summary>Bytes of the caller's buffer this packet fills.</summary>
    public readonly int Size = size;

    /// <summary>Sample frames the packet decodes to - the C's <c>pkt-&gt;duration</c>.</summary>
    public readonly long Frames = frames;

    /// <summary>Presentation timestamp in sample frames, negative while priming is outstanding.</summary>
    public readonly long Pts = pts;

    /// <summary>Encoder delay to discard from the front of this packet.</summary>
    public readonly uint Priming = priming;

    /// <summary>Padding to discard from the end of this packet.</summary>
    public readonly uint Remainder = remainder;
}

/// <summary>
/// Apple's Core Audio Format: a 'desc' chunk saying what the samples are, a 'data' chunk of
/// samples, and optional chunks for the magic cookie ('kuki'), the packet table ('pakt') and
/// metadata ('info').
/// </summary>
public sealed class CafDec
{
    /// <summary>The C's AVPROBE_SCORE_MAX.</summary>
    public const int ProbeScoreMax = 100;

    private const int CafMaxPktSize = 4096;

    private const int AlacPreamble = 12;
    private const int AlacHeader = 36;
    private const int AlacNewKuki = 24;

    private const int FlacStreaminfoSize = 34;
    private const int FlacMetadataTypeStreaminfo = 0;

    private readonly Stream stream;

    // ── CafContext ──────────────────────────────────────────────────────
    private int bytesPerPacket;         // bytes in a packet, or 0 if variable
    private int framesPerPacket;        // frames in a packet, or 0 if variable
    private long numBytes;              // total number of bytes in stream
    private long numPackets;            // packet amount
    private long packetCnt;             // packet counter
    private long frameCnt;              // frame counter
    private long dataStart;             // data start position, in bytes
    private long dataSize;              // raw data size, in bytes
    private uint remainder;             // frames to discard from the last packet

    // ── The AVStream fields this demuxer sets ───────────────────────────
    // av_add_index_entry builds an AVIndexEntry array on the stream; the only two fields this
    // demuxer ever reads back are the position and the timestamp, so that is what is kept.
    private readonly List<(long Pos, long Timestamp)> indexEntries = [];

    private bool eof;

    public CafDec(Stream input) => stream = input;

    /// <summary>The codec name as ffmpeg knows it: <c>pcm_s16be</c>, <c>aac</c>, <c>alac</c>.</summary>
    public string CodecId { get; private set; } = "";

    /// <summary>The 'desc' chunk's four-character format id, for diagnostics.</summary>
    public uint CodecTag { get; private set; }

    public int SampleRate { get; private set; }

    public int ChannelCount { get; private set; }

    /// <summary>Bits per CODED sample - what the 'desc' chunk calls mBitsPerChannel.</summary>
    public int BitsPerCodedSample { get; private set; }

    /// <summary>Bytes in one packet, or zero when packets vary - the C's block_align.</summary>
    public int BlockAlign { get; private set; }

    /// <summary>Sample frames in one packet, or zero when it is one - the C's frame_size.</summary>
    public int FrameSize { get; private set; }

    public long BitRate { get; private set; }

    /// <summary>Total sample frames including priming and remainder, as the packet table says.</summary>
    public long NbFrames { get; private set; }

    /// <summary>Playable sample frames: the packet table's total less priming and remainder.</summary>
    public long StreamDuration { get; private set; }

    /// <summary>Encoder delay in sample frames, from the packet table.</summary>
    public int InitialPadding { get; private set; }

    /// <summary>Padding in sample frames at the end of the last packet.</summary>
    public uint TrailingPadding => remainder;

    /// <summary>The magic cookie as the decoder wants it, or null when the file has none.</summary>
    public byte[]? Extradata { get; private set; }

    /// <summary>The 'info' chunk's key/value pairs.</summary>
    public Dictionary<string, string> Metadata { get; } = [];

    // ── probe ───────────────────────────────────────────────────────────

    /// <summary>
    /// cafdec.c's <c>probe</c>. Returns <see cref="ProbeScoreMax"/> or zero - the file must
    /// open with 'caff', version 1, and a 'desc' chunk of exactly 32 bytes.
    /// </summary>
    public static int Probe(ReadOnlySpan<byte> p)
    {
        if (p.Length < 20)
            return 0;
        if (BinaryPrimitives.ReadUInt32BigEndian(p) != MkBeTag('c', 'a', 'f', 'f'))
            return 0;
        if (BinaryPrimitives.ReadUInt16BigEndian(p[4..]) != 1)
            return 0;
        if (BinaryPrimitives.ReadUInt32BigEndian(p[8..]) != MkBeTag('d', 'e', 's', 'c'))
            return 0;
        if (BinaryPrimitives.ReadInt64BigEndian(p[12..]) != 32)
            return 0;
        return ProbeScoreMax;
    }

    // ── header ──────────────────────────────────────────────────────────

    /// <summary>cafdec.c's <c>read_desc_chunk</c>.</summary>
    private void ReadDescChunk()
    {
        /* parse format description */
        SampleRate = (int)Math.Clamp(BitConverter.Int64BitsToDouble(Rb64()), 0, int.MaxValue);
        CodecTag = Rl32();
        int flags = (int)Rb32();
        bytesPerPacket = (int)Rb32();
        BlockAlign = bytesPerPacket;
        framesPerPacket = (int)Rb32();
        FrameSize = framesPerPacket != 1 ? framesPerPacket : 0;
        ChannelCount = (int)Rb32();
        BitsPerCodedSample = (int)Rb32();

        if (bytesPerPacket < 0 || framesPerPacket < 0 || ChannelCount < 0)
            throw new InvalidDataException("CAF: the desc chunk has a negative packet size, "
                                         + "frame count or channel count.");

        /* calculate bit rate for constant size packets */
        BitRate = framesPerPacket > 0 && bytesPerPacket > 0
            ? (long)SampleRate * bytesPerPacket * 8 / framesPerPacket
            : 0;

        /* determine codec */
        // The C leaves codec_id at AV_CODEC_ID_NONE for a tag it does not know and lets the
        // stream through without a decoder. Here an unreadable file has to say so at the point
        // the format is decided, not produce silence further down.
        CodecId = CodecTag == MkTag('l', 'p', 'c', 'm')
            ? MovGetLpcmCodecId(BitsPerCodedSample, (flags ^ 0x2) | 0x4)
              ?? throw new NotSupportedException(
                     $"CAF: lpcm with {BitsPerCodedSample} bits and flags 0x{flags:X} is not a "
                   + "format pcm.c decodes.")
            : CodecGetId(CodecTag)
              ?? throw new NotSupportedException(
                     $"CAF: format '{FourCc(CodecTag)}' is not a codec ffmpeg's CAF tag table "
                   + "knows.");
    }

    /// <summary>cafdec.c's <c>read_kuki_chunk</c>.</summary>
    private void ReadKukiChunk(long size)
    {
        if (size < 0 || size > int.MaxValue - 64)
            throw new InvalidDataException($"CAF: a kuki chunk of {size} bytes is not readable.");

        if (CodecId == "aac")
        {
            // The C calls ff_mov_read_esds, which walks the esds descriptors and keeps the
            // DecoderSpecificInfo as extradata. The AAC adapter here already accepts a whole
            // esds box and extracts the AudioSpecificConfig itself - it has to, because that is
            // what Mp4Demuxer hands it - so the cookie is passed through whole rather than
            // parsed twice.
            Extradata = ReadBytes((int)size);
        }
        else if (CodecId == "alac")
        {
            if (size < AlacNewKuki)
            {
                Skip(size);
                throw new InvalidDataException("CAF: the ALAC magic cookie is too short.");
            }

            byte[] preamble = ReadBytes(AlacPreamble);
            var extradata = new byte[AlacHeader];

            /* For the old style cookie, we skip 12 bytes, then read 36 bytes.
             * The new style cookie only contains the last 24 bytes of what was
             * 36 bytes in the old style cookie, so we fabricate the first 12 bytes
             * in that case to maintain compatibility. */
            if (preamble.AsSpan(4, 8).SequenceEqual("frmaalac"u8))
            {
                if (size < AlacPreamble + AlacHeader)
                    throw new InvalidDataException("CAF: the old-style ALAC magic cookie is "
                                                 + "too short for its 36-byte header.");
                ReadInto(extradata.AsSpan(0, AlacHeader));
                Skip(size - AlacPreamble - AlacHeader);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(extradata, 36);
                "alac"u8.CopyTo(extradata.AsSpan(4));
                BinaryPrimitives.WriteUInt32BigEndian(extradata.AsSpan(8), 0);
                preamble.CopyTo(extradata.AsSpan(12));
                ReadInto(extradata.AsSpan(24, AlacNewKuki - 12));
                Skip(size - AlacNewKuki);
            }

            Extradata = extradata;
        }
        else if (CodecId == "flac")
        {
            /* The magic cookie format for FLAC consists mostly of an mp4 dfLa atom. */
            if (size < 16 + FlacStreaminfoSize)
                throw new InvalidDataException("CAF: the FLAC magic cookie is too short.");
            /* Check cookie version. */
            if (R8() != 0)
                throw new InvalidDataException("CAF: unknown FLAC magic cookie version.");
            Rb24(); /* Flags */
            /* read dfLa fourcc */
            byte[] buf = ReadBytes(4);
            if (!buf.AsSpan().SequenceEqual("dfLa"u8))
                throw new InvalidDataException("CAF: the FLAC magic cookie is not a dfLa atom.");
            /* Check dfLa version. */
            if (R8() != 0)
                throw new InvalidDataException("CAF: unknown dfLa version.");
            Rb24(); /* Flags */
            buf = ReadBytes(4);
            FlacParseBlockHeader(buf, out _, out int type, out int flacMetadataSize);
            if (type != FlacMetadataTypeStreaminfo || flacMetadataSize != FlacStreaminfoSize)
                throw new InvalidDataException("CAF: STREAMINFO must be the first FLAC metadata block.");
            Extradata = ReadBytes(FlacStreaminfoSize);
        }
        else if (CodecId == "opus")
        {
            // The data layout for Opus is currently unknown, so we generate
            // extradata using known sane values. Multichannel streams are not supported.
            if (ChannelCount > 2)
                throw new NotSupportedException("CAF: multichannel Opus in CAF - ffmpeg itself "
                                              + "asks for a sample of this.");

            var extradata = new byte[19];
            BinaryPrimitives.WriteUInt32BigEndian(extradata, MkBeTag('O', 'p', 'u', 's'));
            BinaryPrimitives.WriteUInt32BigEndian(extradata.AsSpan(4), MkBeTag('H', 'e', 'a', 'd'));
            extradata[8] = 1; /* OpusHead version */
            extradata[9] = (byte)ChannelCount;
            BinaryPrimitives.WriteUInt16LittleEndian(extradata.AsSpan(10), (ushort)InitialPadding);
            BinaryPrimitives.WriteUInt32LittleEndian(extradata.AsSpan(12), (uint)SampleRate);
            BinaryPrimitives.WriteUInt16LittleEndian(extradata.AsSpan(16), 0);
            extradata[18] = 0;
            Extradata = extradata;

            Skip(size);
        }
        else
        {
            Extradata = ReadBytes((int)size);
        }
    }

    /// <summary>cafdec.c's <c>read_pakt_chunk</c>.</summary>
    private void ReadPaktChunk(long size)
    {
        long pos = 0;
        long ccount = Tell();

        long packets = Rb64();
        if (packets < 0 || int.MaxValue / 16 < packets)
            throw new InvalidDataException($"CAF: a packet table of {packets} packets is not readable.");

        NbFrames = Rb64();          /* valid frames */
        uint priming = Rb32();      /* priming frames */
        remainder = Rb32();         /* remainder frames */

        frameCnt = -(long)priming;
        InitialPadding = (int)priming;
        NbFrames += priming;
        NbFrames += remainder;

        if (CodecId == "opus" && Extradata is { Length: >= 12 })
            BinaryPrimitives.WriteUInt16LittleEndian(Extradata.AsSpan(10), (ushort)InitialPadding);

        if (bytesPerPacket > 0 && framesPerPacket > 0)
        {
            if (packets == 0)
            {
                if (dataSize < 0)
                    throw new InvalidDataException("CAF: a packet table with no packets needs a "
                                                 + "data chunk of known size.");
                packets = dataSize / bytesPerPacket;
            }
            StreamDuration = framesPerPacket * packets - priming;
            pos = (long)bytesPerPacket * packets;
        }
        else
        {
            StreamDuration = frameCnt;
            for (long i = 0; i < packets; i++)
            {
                if (eof)
                    throw new InvalidDataException("CAF: the packet table ends before its "
                                                 + "declared packet count.");
                indexEntries.Add((pos, StreamDuration));
                pos += bytesPerPacket != 0 ? bytesPerPacket : Mp4ReadDescrLen();
                StreamDuration += framesPerPacket != 0 ? framesPerPacket : Mp4ReadDescrLen();
            }
        }
        StreamDuration -= remainder;
        if (StreamDuration < 0)
            throw new InvalidDataException("CAF: the packet table describes a negative duration.");

        if (Tell() - ccount > size || size > long.MaxValue - ccount)
            throw new InvalidDataException("CAF: error reading packet table.");
        Seek(ccount + size);

        numPackets = packets;
        numBytes = pos;
    }

    /// <summary>cafdec.c's <c>read_info_chunk</c>.</summary>
    private void ReadInfoChunk(long size)
    {
        uint nbEntries = Rb32();

        if (3L * nbEntries > size)
            return;

        for (uint i = 0; i < nbEntries && !eof; i++)
        {
            string key = GetStr(32);
            string value = GetStr(1024);
            if (key.Length == 0)
                continue;
            Metadata.TryAdd(key, value);
        }
    }

    /// <summary>cafdec.c's <c>read_header</c>.</summary>
    public void ReadHeader()
    {
        ReadHeaderChunks();

        // A constant bitrate file - PCM, and anything else with a fixed packet size - carries
        // no packet table at all, so nothing above sets a length. The data chunk and the
        // description chunk are enough between them, and are what cafdec.c uses in the same
        // case: "num_packets = caf->data_size / caf->bytes_per_packet" and then
        // "st->duration = caf->frames_per_packet * num_packets".
        if (StreamDuration == 0 && bytesPerPacket > 0 && framesPerPacket > 0 && dataSize > 0)
            StreamDuration = dataSize / bytesPerPacket * framesPerPacket;
    }

    private void ReadHeaderChunks()
    {
        Skip(8); /* magic, version, file flags */

        /* audio description chunk */
        if (Rb32() != MkBeTag('d', 'e', 's', 'c'))
            throw new InvalidDataException("CAF: desc chunk not present.");
        long size = Rb64();
        if (size != 32)
            throw new InvalidDataException($"CAF: the desc chunk is {size} bytes, not 32.");

        ReadDescChunk();

        /* parse each chunk */
        bool foundData = false;
        bool breakToFoundData = false;
        while (!eof)
        {
            /* stop at data chunk if seeking is not supported or
               data chunk size is unknown */
            if (foundData && dataSize < 0)
                break;

            uint tag = Rb32();
            size = Rb64();
            long pos = Tell();
            if (eof)
                break;

            if (tag == MkBeTag('d', 'a', 't', 'a'))
            {
                Skip(4); /* edit count */
                dataStart = Tell();
                dataSize = size < 0 ? -1 : size - 4;
                if (dataStart < 0 || dataSize > long.MaxValue - dataStart)
                    throw new InvalidDataException("CAF: the data chunk starts past the end of the file.");

                if (dataSize > 0)
                    Skip(dataSize);
                foundData = true;
            }
            else if (tag == MkBeTag('c', 'h', 'a', 'n'))
            {
                // ff_mov_read_chan, which turns the channel layout chunk into an
                // AVChannelLayout. Nothing downstream here carries a layout - a track has a
                // channel COUNT, which the desc chunk already gave - so the chunk is skipped
                // below with every other one whose contents this port does not use.
            }
            else if (tag == MkBeTag('k', 'u', 'k', 'i'))
            {
                ReadKukiChunk(size);
            }
            else if (tag == MkBeTag('p', 'a', 'k', 't'))
            {
                ReadPaktChunk(size);
            }
            else if (tag == MkBeTag('i', 'n', 'f', 'o'))
            {
                ReadInfoChunk(size);
            }
            else
            {
                // The C logs an unknown chunk and falls through into the 'free' case, which is
                // the only one that can leave the loop early.
                if (size < 0 && foundData)
                {
                    breakToFoundData = true;
                    break;
                }
                if (size < 0)
                    throw new InvalidDataException(
                        $"CAF: chunk '{FourCc(tag)}' declares a negative size.");
            }

            if (size > 0)
            {
                if (pos > long.MaxValue - size)
                    throw new InvalidDataException("CAF: a chunk ends past the end of the file.");
                Seek(pos + size);
            }
        }

        if (!foundData && !breakToFoundData)
            throw new InvalidDataException("CAF: no data chunk.");

        // found_data:
        if (bytesPerPacket > 0 && framesPerPacket > 0)
        {
            if (dataSize > 0 && dataSize / bytesPerPacket < long.MaxValue / framesPerPacket)
                NbFrames = dataSize / bytesPerPacket * framesPerPacket;
        }
        else if (indexEntries.Count > 0 && StreamDuration > 0)
        {
            if (SampleRate != 0 && dataSize / StreamDuration > long.MaxValue / SampleRate / 8)
                throw new InvalidDataException("CAF: overflow during bit rate calculation.");
            BitRate = SampleRate * 8L * (dataSize / StreamDuration);
        }
        else
        {
            throw new InvalidDataException("CAF: missing packet table. It is required when "
                                         + "block size or frame size are variable.");
        }

        /* position the stream at the start of data */
        if (dataSize >= 0)
            Seek(dataStart);
    }

    // ── packets ─────────────────────────────────────────────────────────

    /// <summary>
    /// cafdec.c's <c>read_packet</c>. Returns false at end of stream.
    /// </summary>
    /// <remarks>
    /// The C's av_get_packet allocates a buffer for every packet. Here the caller owns the
    /// buffer and it is grown only when a packet does not fit, because a decode loop in this
    /// project may not allocate per frame.
    /// </remarks>
    public bool ReadPacket(ref byte[] buffer, out CafPacketInfo info)
    {
        info = default;

        if (eof)
            return false;

        long left = CafMaxPktSize;

        /* don't read past end of data chunk */
        if (dataSize > 0)
        {
            left = dataStart + dataSize - Tell();
            if (left == 0)
                return false;
            if (left < 0)
                throw new InvalidDataException("CAF: read past the end of the data chunk.");
        }

        long pktFrames = framesPerPacket;
        long pktSize = bytesPerPacket;
        uint priming = 0;
        uint packetRemainder = 0;

        if (pktSize > 0 && pktFrames == 1)
        {
            pktSize = CafMaxPktSize / pktSize * pktSize;
            pktSize = Math.Min(pktSize, left);
            pktFrames = pktSize / bytesPerPacket;
        }
        else if (indexEntries.Count > 0)
        {
            if (packetCnt < indexEntries.Count - 1)
            {
                pktSize = indexEntries[(int)packetCnt + 1].Pos - indexEntries[(int)packetCnt].Pos;
                pktFrames = indexEntries[(int)packetCnt + 1].Timestamp - indexEntries[(int)packetCnt].Timestamp;
            }
            else if (packetCnt == indexEntries.Count - 1)
            {
                pktSize = numBytes - indexEntries[(int)packetCnt].Pos;
                pktFrames = StreamDuration - indexEntries[(int)packetCnt].Timestamp;
                packetRemainder = remainder;
            }
            else
            {
                return false;
            }
        }
        else if (packetCnt + 1 == numPackets)
        {
            pktFrames -= remainder;
            packetRemainder = remainder;
        }

        if (pktSize == 0 || pktFrames == 0 || pktSize > left)
            throw new InvalidDataException(
                $"CAF: packet {packetCnt} is {pktSize} bytes and {pktFrames} frames with "
              + $"{left} bytes of data left.");

        if (buffer.Length < pktSize)
            buffer = new byte[pktSize];
        int res = Read(buffer.AsSpan(0, (int)pktSize));
        if (res <= 0)
            return false;

        if (packetCnt == 0)
            priming = (uint)InitialPadding;

        info = new CafPacketInfo(res, pktFrames, frameCnt, priming, packetRemainder);

        packetCnt++;
        frameCnt += pktFrames;

        return true;
    }

    /// <summary>cafdec.c's <c>read_seek</c>. Returns false when the file cannot be seeked.</summary>
    public bool ReadSeek(long timestamp)
    {
        timestamp = Math.Max(timestamp, 0);

        long pos, newPacketCnt, newFrameCnt;

        if (framesPerPacket > 0 && bytesPerPacket > 0)
        {
            /* calculate new byte position based on target frame position */
            pos = bytesPerPacket * (timestamp / framesPerPacket);
            if (dataSize > 0)
                pos = Math.Min(pos, dataSize);
            newPacketCnt = pos / bytesPerPacket;
            newFrameCnt = framesPerPacket * newPacketCnt - InitialPadding;
        }
        else if (indexEntries.Count > 0)
        {
            newPacketCnt = IndexSearchTimestamp(timestamp);
            newFrameCnt = indexEntries[(int)newPacketCnt].Timestamp;
            pos = indexEntries[(int)newPacketCnt].Pos;
        }
        else
        {
            return false;
        }

        Seek(pos + dataStart);
        eof = false;

        packetCnt = newPacketCnt;
        frameCnt = newFrameCnt;

        return true;
    }

    /// <summary>
    /// av_index_search_timestamp with no flags: the last entry at or before the target, which
    /// for a packet table is the packet the target frame falls inside.
    /// </summary>
    private long IndexSearchTimestamp(long timestamp)
    {
        int lo = 0, hi = indexEntries.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (indexEntries[mid].Timestamp <= timestamp) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    // ── helpers the C gets from other files ─────────────────────────────

    /// <summary>
    /// isom.h's <c>ff_mov_get_lpcm_codec_id</c> over utils.c's <c>ff_get_pcm_codec_id</c>.
    /// CAF's lpcm flags are 0x1 float, 0x2 big-endian, 0x4 signed, and the caller has already
    /// inverted the endianness bit, because a CAF 'desc' chunk sets it for LITTLE-endian.
    /// </summary>
    internal static string? MovGetLpcmCodecId(int bps, int flags)
        => GetPcmCodecId(bps, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0 ? -1 : 0);

    /// <summary>utils.c's <c>ff_get_pcm_codec_id</c>.</summary>
    internal static string? GetPcmCodecId(int bps, bool flt, bool be, int sflags)
    {
        if (bps <= 0 || bps > 64)
            return null;

        if (flt)
        {
            return bps switch
            {
                32 => be ? "pcm_f32be" : "pcm_f32le",
                64 => be ? "pcm_f64be" : "pcm_f64le",
                _ => null,
            };
        }

        bps += 7;
        bps >>= 3;
        if ((sflags & (1 << (bps - 1))) != 0)
        {
            return bps switch
            {
                1 => "pcm_s8",
                2 => be ? "pcm_s16be" : "pcm_s16le",
                3 => be ? "pcm_s24be" : "pcm_s24le",
                4 => be ? "pcm_s32be" : "pcm_s32le",
                8 => be ? "pcm_s64be" : "pcm_s64le",
                _ => null,
            };
        }

        return bps switch
        {
            1 => "pcm_u8",
            2 => be ? "pcm_u16be" : "pcm_u16le",
            3 => be ? "pcm_u24be" : "pcm_u24le",
            4 => be ? "pcm_u32be" : "pcm_u32le",
            _ => null,
        };
    }

    /// <summary>
    /// caf.c's <c>ff_codec_caf_tags</c>, less the lpcm rows: those exist so that the muxer can
    /// find a tag for a PCM codec, and the demuxer reaches them through
    /// <see cref="MovGetLpcmCodecId"/> instead.
    /// </summary>
    internal static string? CodecGetId(uint tag)
        => CafTags.TryGetValue(tag, out string? id) ? id : null;

    private static readonly Dictionary<uint, string> CafTags = new()
    {
        [MkTag('a', 'a', 'c', ' ')] = "aac",
        [MkTag('a', 'a', 'c', 'l')] = "aac",
        [MkTag('a', 'c', '-', '3')] = "ac3",
        [MkTag('i', 'm', 'a', '4')] = "adpcm_ima_qt",
        [MkTag('m', 's', (char)0, (char)17)] = "adpcm_ima_wav",
        [MkTag('m', 's', (char)0, (char)2)] = "adpcm_ms",
        [MkTag('a', 'l', 'a', 'c')] = "alac",
        [MkTag('s', 'a', 'm', 'r')] = "amr_nb",
        [MkTag('a', 'g', 's', 'm')] = "gsm",
        [MkTag('m', 's', (char)0, '1')] = "gsm_ms",
        [MkTag('i', 'l', 'b', 'c')] = "ilbc",
        [MkTag('f', 'l', 'a', 'c')] = "flac",
        [MkTag('M', 'A', 'C', '3')] = "mace3",
        [MkTag('M', 'A', 'C', '6')] = "mace6",
        [MkTag('.', 'm', 'p', '1')] = "mp1",
        [MkTag('.', 'm', 'p', '2')] = "mp2",
        [MkTag('.', 'm', 'p', '3')] = "mp3",
        [MkTag('m', 's', (char)0, 'U')] = "mp3",
        [MkTag('o', 'p', 'u', 's')] = "opus",
        [MkTag('a', 'l', 'a', 'w')] = "pcm_alaw",
        [MkTag('u', 'l', 'a', 'w')] = "pcm_mulaw",
        [MkTag('Q', 'c', 'l', 'p')] = "qcelp",
        [MkTag('Q', 'D', 'M', '2')] = "qdm2",
        [MkTag('Q', 'D', 'M', 'C')] = "qdmc",
    };

    /// <summary>isom.c's <c>ff_mp4_read_descr_len</c>.</summary>
    private int Mp4ReadDescrLen()
    {
        int len = 0;
        int count = 4;
        while (count-- > 0)
        {
            int c = R8();
            len = (len << 7) | (c & 0x7f);
            if ((c & 0x80) == 0)
                break;
        }
        return len;
    }

    /// <summary>libavcodec/flac.h's <c>flac_parse_block_header</c>.</summary>
    private static void FlacParseBlockHeader(ReadOnlySpan<byte> block, out bool last, out int type, out int size)
    {
        last = (block[0] & 0x80) != 0;
        type = block[0] & 0x7F;
        size = (block[1] << 16) | (block[2] << 8) | block[3];
    }

    private static uint MkBeTag(char a, char b, char c, char d)
        => ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;

    /// <summary>The C's MKTAG, which is little-endian, as read by avio_rl32.</summary>
    private static uint MkTag(char a, char b, char c, char d)
        => ((uint)d << 24) | ((uint)c << 16) | ((uint)b << 8) | a;

    private static string FourCc(uint tag) => string.Create(4, tag, static (span, t) =>
    {
        for (int i = 0; i < 4; i++)
        {
            // The tag was read with avio_rl32, so its first byte in the file is the low one.
            char ch = (char)((t >> (8 * i)) & 0xFF);
            span[i] = char.IsAsciiLetterOrDigit(ch) || ch is ' ' or '-' or '.' ? ch : '?';
        }
    });

    // ── the AVIOContext calls, over a Stream ────────────────────────────
    //
    // avio returns zero and raises eof_reached on a short read rather than failing, and
    // read_header's loop is written around exactly that, so these mirror it.

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
        {
            eof = true;
            destination[total..].Clear();
        }
        return total;
    }

    private void ReadInto(Span<byte> destination)
    {
        if (Read(destination) != destination.Length)
            throw new EndOfStreamException("CAF: the file ends inside a chunk it declared.");
    }

    private byte[] ReadBytes(int count)
    {
        var buffer = new byte[count];
        ReadInto(buffer);
        return buffer;
    }

    private byte R8()
    {
        Span<byte> b = stackalloc byte[1];
        Read(b);
        return b[0];
    }

    private uint Rb24()
    {
        Span<byte> b = stackalloc byte[3];
        Read(b);
        return ((uint)b[0] << 16) | ((uint)b[1] << 8) | b[2];
    }

    private uint Rb32()
    {
        Span<byte> b = stackalloc byte[4];
        Read(b);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    private uint Rl32()
    {
        Span<byte> b = stackalloc byte[4];
        Read(b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    private long Rb64()
    {
        Span<byte> b = stackalloc byte[8];
        Read(b);
        return BinaryPrimitives.ReadInt64BigEndian(b);
    }

    /// <summary>avio_get_str: a NUL-terminated string, truncated to the caller's buffer size.</summary>
    private string GetStr(int maxLength)
    {
        var sb = new StringBuilder();
        while (!eof)
        {
            byte c = R8();
            if (c == 0) break;
            if (sb.Length < maxLength - 1) sb.Append((char)c);
        }
        return sb.ToString();
    }

    private long Tell() => stream.Position;

    private void Seek(long position)
    {
        stream.Position = position;
        eof = position >= stream.Length;
    }

    private void Skip(long count) => Seek(stream.Position + count);
}

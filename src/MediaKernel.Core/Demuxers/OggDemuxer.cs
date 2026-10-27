// OGG container demuxer for Vorbis, Opus, Theora, and FLAC streams
// Reference: RFC 3533 (OGG), VLC's modules/demux/ogg.c

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

using MediaKernel.Core.Codecs.Opus;
using MediaKernel.Core.Codecs.Vorbis;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// OGG codec types detected from stream headers.
/// </summary>
public enum OggCodecType
{
    Unknown,
    Vorbis,
    Opus,
    Theora,
    Flac,
    Speex,
    Vp8,
    Daala,
    Skeleton
}

/// <summary>
/// Represents an OGG logical stream.
/// </summary>
public sealed class OggStream
{
    public uint SerialNumber { get; init; }
    public OggCodecType CodecType { get; init; }
    public int StreamIndex { get; init; }

    /// <summary>
    /// Every header packet of this stream, end to end. Kept because that is the shape the
    /// Vorbis FATE harness has always read it in.
    /// </summary>
    public byte[] CodecPrivateData { get; set; } = [];

    /// <summary>
    /// The header packets with their boundaries intact - three for Vorbis, two for Opus. A
    /// Vorbis decoder needs them separately, and nothing but the demuxer knows where one ends.
    /// </summary>
    public List<byte[]> HeaderPackets { get; } = new();

    /// <summary>
    /// Samples the decoder is to discard from the front of the stream, from the Opus
    /// identification header. Zero for every other codec.
    /// </summary>
    public int PreSkip { get; init; }
    
    // Vorbis/Opus specific
    public uint SampleRate { get; init; }
    public byte Channels { get; init; }
    
    // Theora/VP8 specific
    public ushort Width { get; init; }
    public ushort Height { get; init; }
    public uint FrameRateNumerator { get; init; }
    public uint FrameRateDenominator { get; init; }
    
    // Timing
    public long GranulePosition { get; set; }
    public long TotalSamples { get; set; }
}

/// <summary>
/// One packet, with the timing that had to be worked out across a page rather than read from it.
/// </summary>
public readonly struct OggPacket
{
    /// <summary>Index into <see cref="OggDemuxer.Streams"/>.</summary>
    public int StreamIndex { get; init; }

    public byte[] Data { get; init; }

    /// <summary>Presentation time in the codec's own sample clock.</summary>
    public long PresentationSamples { get; init; }

    /// <summary>How many samples this packet decodes to.</summary>
    public int DurationSamples { get; init; }

    /// <summary>
    /// Samples to drop from the end of the decoded frame, which is only ever non-zero on the
    /// last packet of a stream. FFmpeg calls it end_trimming and carries it as packet side
    /// data; it is how a file whose audio ends in the middle of a frame says so, and it is what
    /// makes gapless playback gapless.
    /// </summary>
    public int EndTrimSamples { get; init; }
}

/// <summary>
/// Represents an OGG page header.
/// </summary>
public readonly struct OggPageHeader
{
    public readonly byte Version;
    public readonly byte HeaderType;
    public readonly long GranulePosition;
    public readonly uint SerialNumber;
    public readonly uint PageSequenceNumber;
    public readonly uint Checksum;
    public readonly byte SegmentCount;

    public OggPageHeader(byte version, byte headerType, long granulePosition,
        uint serialNumber, uint pageSequenceNumber, uint checksum, byte segmentCount)
    {
        Version = version;
        HeaderType = headerType;
        GranulePosition = granulePosition;
        SerialNumber = serialNumber;
        PageSequenceNumber = pageSequenceNumber;
        Checksum = checksum;
        SegmentCount = segmentCount;
    }

    public bool IsContinued => (HeaderType & 0x01) != 0;
    public bool IsBeginningOfStream => (HeaderType & 0x02) != 0;
    public bool IsEndOfStream => (HeaderType & 0x04) != 0;
}

/// <summary>
/// OGG container demuxer supporting Vorbis, Opus, Theora, and FLAC.
/// </summary>
public sealed class OggDemuxer : IDisposable
{
    private readonly Stream inputStream;
    private readonly byte[] readBuffer = new byte[65536];
    private readonly List<OggStream> streams = new();
    private readonly Dictionary<uint, OggStream> streamsBySerial = new();
    private readonly Dictionary<uint, List<byte>> pendingPackets = new();

    // Queue of complete packets waiting to be returned by ReadPacket
    private readonly Queue<OggPacket> packetQueue = new();

    /// <summary>
    /// Per-stream timing. An Ogg page carries one granule position, which belongs to the last
    /// packet that finishes on it, so every other packet's timestamp has to be worked out from
    /// the durations around it. This holds what that takes.
    /// </summary>
    private sealed class StreamTiming
    {
        public VorbisPacketParser? Vorbis;
        public bool IsOpus;
        public int PreSkip;

        /// <summary>
        /// FFmpeg's <c>os-&gt;lastpts</c>: the presentation time the next packet is known to
        /// have, or null for its AV_NOPTS_VALUE. A page end sets it from the granule position
        /// and reading a packet clears it, which is what makes a packet in the middle of a page
        /// derive its own time instead.
        /// </summary>
        public long? PendingPts;
    }

    private readonly Dictionary<uint, StreamTiming> timings = new();

    /// <summary>Scratch for one page's worth of packets; reused so a page costs no allocation.</summary>
    private readonly List<(byte[] Data, int Duration)> pagePackets = new();

    private bool isInitialized;
    private bool isDisposed;
    private long currentPosition;

    // The page that ended the header-reading loop. Initialize used to rewind the stream to
    // the start of it and read it again.
    //
    // NOT THE BUG, and the first version of this comment said it was. A live HTTP connection
    // cannot seek, so the rewind looked like the reason an Ogg station decoded zero samples -
    // but the pipeline hands this demuxer a buffered wrapper whose CanSeek is TRUE even for a
    // socket, so the rewind had always worked. The fault was the short read below. Perturbing
    // this back to the rewind leaves `live-stream` green, which is how the mistake was caught.
    //
    // It is kept because oggdec.c never seeks backwards over a page it has already read, and
    // because not depending on that buffer staying seekable is worth having for free. It is a
    // faithfulness change and nothing measures it.
    private OggPageHeader pendingHeader;
    private byte[]? pendingPageData;

    // OGG sync pattern
    private static readonly byte[] OggSyncPattern = "OggS"u8.ToArray();

    public OggDemuxer(Stream stream)
    {
        inputStream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>Gets all discovered streams.</summary>
    public IReadOnlyList<OggStream> Streams => streams;

    /// <summary>
    /// Initializes the demuxer by parsing OGG headers.
    /// </summary>
    public bool Initialize()
    {
        if (isInitialized)
            return true;

        if (!inputStream.CanRead)
            return false;

        inputStream.Position = 0;

        // Read beginning-of-stream pages to discover all streams
        while (TryReadPage(out var header, out var pageData))
        {
            if (!header.IsBeginningOfStream)
            {
                // Done with BOS pages — this is a header page for a stream we already know.
                // Process it, then move to the header-reading loop.
                ProcessHeaderPage(header, pageData);
                break;
            }

            // Parse BOS page to identify codec
            var stream = IdentifyCodec(header, pageData);
            if (stream != null)
            {
                streams.Add(stream);
                streamsBySerial[stream.SerialNumber] = stream;
                pendingPackets[stream.SerialNumber] = new List<byte>();
            }
        }

        // Continue reading header pages until we get data pages
        // (Vorbis has 3 header pages, Opus has 2, etc.)
        while (TryReadPage(out var header, out var pageData))
        {
            if (header.GranulePosition != 0 && header.GranulePosition != -1)
            {
                // A data page: the headers are over. Keep it for the first ReadNextPacket
                // rather than seeking back and reading it again - see the note on
                // pendingPageData for what this does and does not fix.
                pendingHeader = header;
                pendingPageData = pageData;
                break;
            }

            ProcessHeaderPage(header, pageData);
        }

        isInitialized = streams.Count > 0;
        if (isInitialized)
            MeasureDuration();

        return isInitialized;
    }

    /// <summary>
    /// The length of the file in the first stream's sample clock, or zero if it could not be
    /// read. Ogg states no duration anywhere: the only record of it is the granule position on
    /// the last page, which is what FFmpeg's ogg_get_length reads too.
    /// </summary>
    public long DurationSamples { get; private set; }

    /// <summary>The same length in milliseconds, or zero if unknown.</summary>
    public double DurationMilliseconds => streams.Count > 0 && SampleClock(streams[0]) > 0
        ? DurationSamples * 1000.0 / SampleClock(streams[0])
        : 0;

    /// <summary>
    /// The clock a stream's timestamps are counted in. Opus is always timed at 48 kHz whatever
    /// rate its header names, because that is the rate it decodes at.
    /// </summary>
    public static int SampleClock(OggStream stream) =>
        stream.CodecType == OggCodecType.Opus ? 48000 : (int)stream.SampleRate;

    /// <summary>
    /// Reads the granule position of the last page in the file, then puts the stream back.
    /// </summary>
    private void MeasureDuration()
    {
        if (!inputStream.CanSeek || inputStream.Length <= 0)
            return;

        long resume = inputStream.Position;
        try
        {
            // A page is at most 65307 bytes, so the last one begins inside the final window.
            const int Window = 128 * 1024;
            int length = (int)Math.Min(Window, inputStream.Length);
            inputStream.Position = inputStream.Length - length;

            var tail = new byte[length];
            inputStream.ReadExactly(tail, 0, length);

            for (int i = length - 27; i >= 0; i--)
            {
                if (tail[i] != 'O' || tail[i + 1] != 'g' || tail[i + 2] != 'g' || tail[i + 3] != 'S')
                    continue;

                long granule = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(i + 6));
                if (granule < 0)
                    continue;

                DurationSamples = granule - (streams.Count > 0 ? streams[0].PreSkip : 0);
                return;
            }
        }
        finally
        {
            inputStream.Position = resume;
        }
    }

    /// <summary>
    /// Reads the next packet from any stream.
    /// </summary>
    /// <param name="streamIndex">Output stream index.</param>
    /// <param name="packet">Output packet data.</param>
    /// <param name="timestamp">Granule position timestamp.</param>
    /// <returns>True if a packet was read.</returns>
    public bool ReadPacket(out int streamIndex, out byte[] packet, out long timestamp)
    {
        bool got = ReadNextPacket(out var next);
        streamIndex = got ? next.StreamIndex : -1;
        packet = got ? next.Data : [];
        timestamp = got ? next.PresentationSamples : 0;
        return got;
    }

    /// <summary>
    /// Reads the next packet from any stream, with its presentation time and duration in the
    /// codec's own sample clock.
    /// </summary>
    public bool ReadNextPacket(out OggPacket packet)
    {
        packet = default;

        if (!isInitialized)
            return false;

        if (packetQueue.Count > 0)
        {
            packet = packetQueue.Dequeue();
            return true;
        }

        // Read pages until we have at least one complete packet
        while (TryReadPage(out var header, out var pageData))
        {
            if (!streamsBySerial.TryGetValue(header.SerialNumber, out var stream))
                continue;

            SplitPageIntoPackets(header, pageData);
            if (pagePackets.Count == 0)
                continue;

            QueuePagePackets(header, stream);
            stream.GranulePosition = header.GranulePosition;

            if (packetQueue.Count > 0)
            {
                packet = packetQueue.Dequeue();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Seeks to approximate position by granule.
    /// </summary>
    public bool SeekToGranule(uint serialNumber, long targetGranule)
    {
        if (!isInitialized || !inputStream.CanSeek)
            return false;

        // Binary search for target granule
        long fileStart = 0;
        long fileEnd = inputStream.Length;

        while (fileEnd - fileStart > 65536)
        {
            long mid = (fileStart + fileEnd) / 2;
            inputStream.Position = mid;

            // Find next page sync
            if (!SyncToNextPage())
            {
                fileEnd = mid;
                continue;
            }

            if (!TryReadPage(out var header, out _))
            {
                fileEnd = mid;
                continue;
            }

            if (header.SerialNumber == serialNumber)
            {
                if (header.GranulePosition < targetGranule)
                    fileStart = mid;
                else
                    fileEnd = mid;
            }
            else
            {
                // Different stream, try again
                fileStart = mid;
            }
        }

        // Seek to found position
        inputStream.Position = fileStart;
        SyncToNextPage();

        return true;
    }

    private bool TryReadPage(out OggPageHeader header, out byte[] pageData)
    {
        if (pendingPageData != null)
        {
            header = pendingHeader;
            pageData = pendingPageData;
            pendingPageData = null;
            return true;
        }

        header = default;
        pageData = [];
        currentPosition = inputStream.CanSeek ? inputStream.Position : currentPosition;

        // ReadAtLeast and not Read, here and below. THIS was why an Ogg station decoded
        // nothing: a stream returns a short read whenever it likes, and treating one as end of
        // stream ends the demux at an arbitrary page. moog_small.ogg over a socket produced
        // ZERO samples while reporting its Vorbis track correctly. The same mistake was in the
        // MPEG audio demuxer, where it cut a live stream at its first metadata boundary; that
        // one carries a comment saying so and this file did not.
        //
        // Of the three reads changed here, only the page BODY is measured: perturbing the
        // 27-byte header read or the segment table back to Read leaves `live-stream` green,
        // because neither came back short on these streams. They are the same defect in the
        // same function and are fixed together rather than left as two thirds of a fix.
        if (inputStream.ReadAtLeast(readBuffer.AsSpan(0, 27), 27, throwOnEndOfStream: false) != 27)
            return false;

        // Verify sync pattern "OggS"
        if (readBuffer[0] != 'O' || readBuffer[1] != 'g' || 
            readBuffer[2] != 'g' || readBuffer[3] != 'S')
        {
            return false;
        }

        byte version = readBuffer[4];
        byte headerType = readBuffer[5];
        long granulePosition = BinaryPrimitives.ReadInt64LittleEndian(readBuffer.AsSpan(6));
        uint serialNumber = BinaryPrimitives.ReadUInt32LittleEndian(readBuffer.AsSpan(14));
        uint pageSequence = BinaryPrimitives.ReadUInt32LittleEndian(readBuffer.AsSpan(18));
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(readBuffer.AsSpan(22));
        byte segmentCount = readBuffer[26];

        if (version != 0)
            return false;

        header = new OggPageHeader(version, headerType, granulePosition,
            serialNumber, pageSequence, checksum, segmentCount);

        // Read segment table
        if (segmentCount > 0)
        {
            if (inputStream.ReadAtLeast(readBuffer.AsSpan(0, segmentCount), segmentCount,
                                        throwOnEndOfStream: false) != segmentCount)
                return false;
        }

        // Calculate total page data size
        int pageSize = 0;
        for (int i = 0; i < segmentCount; i++)
            pageSize += readBuffer[i];

        // Store segment table
        byte[] segmentTable = new byte[segmentCount];
        Buffer.BlockCopy(readBuffer, 0, segmentTable, 0, segmentCount);

        // Read page data
        pageData = new byte[pageSize + segmentCount];
        Buffer.BlockCopy(segmentTable, 0, pageData, 0, segmentCount);
        
        if (pageSize > 0)
        {
            int read = inputStream.ReadAtLeast(pageData.AsSpan(segmentCount, pageSize), pageSize,
                                               throwOnEndOfStream: false);
            if (read != pageSize)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Fills <see cref="pagePackets"/> with the packets that finish on this page, joining a
    /// packet that was continued from the page before.
    /// </summary>
    /// <remarks>
    /// A segment of 255 bytes means the packet carries on into the next segment, so a packet
    /// ends at the first segment shorter than 255. A page that is not flagged as continued
    /// cannot finish a packet left hanging, so anything pending is a torn packet and goes.
    /// </remarks>
    private void SplitPageIntoPackets(in OggPageHeader header, byte[] pageData)
    {
        pagePackets.Clear();

        uint serial = header.SerialNumber;
        if (!pendingPackets.TryGetValue(serial, out var pending))
        {
            pending = new List<byte>();
            pendingPackets[serial] = pending;
        }

        if (!header.IsContinued && pending.Count > 0)
            pending.Clear();

        var segmentTable = pageData.AsSpan(0, header.SegmentCount);
        int dataOffset = header.SegmentCount;

        for (int i = 0; i < header.SegmentCount; i++)
        {
            byte segSize = segmentTable[i];

            if (segSize > 0 && dataOffset + segSize <= pageData.Length)
                pending.AddRange(pageData.AsSpan(dataOffset, segSize));

            dataOffset += segSize;

            if (segSize < 255 && pending.Count > 0)
            {
                pagePackets.Add((pending.ToArray(), 0));
                pending.Clear();
            }
        }
    }

    /// <summary>
    /// Works out how long each of this page's packets is and where the first of them starts,
    /// then queues them.
    /// </summary>
    /// <remarks>
    /// This is FFmpeg's scheme from <c>oggparsevorbis.c</c> and <c>oggparseopus.c</c>: the first
    /// data page's granule position is the end of its last packet, so subtracting the durations
    /// of every packet on it gives the start of its first - which is how the encoder delay is
    /// recovered. After that each packet simply follows the one before.
    /// </remarks>
    private void QueuePagePackets(in OggPageHeader header, OggStream stream)
    {
        var timing = GetTiming(stream);
        long previousPts = 0;
        int previousDuration = 0;

        for (int i = 0; i < pagePackets.Count; i++)
        {
            // A packet whose time is not already known works it out from the end of the page:
            // the granule position is where the page's last packet finishes, so subtracting the
            // durations of this packet and every one after it gives where this one starts. This
            // is the "first packet handling" block of FFmpeg's vorbis_packet and opus_packet,
            // which runs for every packet that does not directly follow a page boundary.
            if (timing.PendingPts is null && header.GranulePosition >= 0 && !header.IsEndOfStream)
            {
                ResetParser(timing);
                long remaining = 0;
                for (int j = i; j < pagePackets.Count; j++)
                    remaining += PacketDuration(timing, pagePackets[j].Data);

                timing.PendingPts = header.GranulePosition - remaining - timing.PreSkip;
                ResetParser(timing);
            }

            int duration = PacketDuration(timing, pagePackets[i].Data);
            long pts = timing.PendingPts ?? previousPts + previousDuration;
            timing.PendingPts = null;

            // The last packet of the last page is cut short to land exactly on the granule
            // position, which is how an encoder says the file ends mid-packet. FFmpeg calls
            // the difference end_trimming and only ever shortens.
            int endTrim = 0;
            if (header.IsEndOfStream && i == pagePackets.Count - 1 && header.GranulePosition >= 0)
            {
                long target = header.GranulePosition - timing.PreSkip - pts;
                if (target > 0 && target < duration)
                {
                    endTrim = duration - (int)target;
                    duration = (int)target;
                }
            }

            packetQueue.Enqueue(new OggPacket
            {
                StreamIndex = stream.StreamIndex,
                Data = pagePackets[i].Data,
                PresentationSamples = pts,
                DurationSamples = duration,
                EndTrimSamples = endTrim,
            });

            previousPts = pts;
            previousDuration = duration;
        }

        // The page states where its last packet ends, so that is where the next one starts.
        if (header.GranulePosition >= 0)
            timing.PendingPts = header.GranulePosition - timing.PreSkip;
    }

    private static void ResetParser(StreamTiming timing) => timing.Vorbis?.Reset();

    private StreamTiming GetTiming(OggStream stream)
    {
        if (timings.TryGetValue(stream.SerialNumber, out var timing))
            return timing;

        timing = new StreamTiming { IsOpus = stream.CodecType == OggCodecType.Opus, PreSkip = stream.PreSkip };

        if (stream.CodecType == OggCodecType.Vorbis && stream.HeaderPackets.Count >= 3)
        {
            var parser = new VorbisPacketParser();
            if (parser.Initialize(stream.HeaderPackets[0], stream.HeaderPackets[2]))
                timing.Vorbis = parser;
        }

        timings[stream.SerialNumber] = timing;
        return timing;
    }

    /// <summary>
    /// The duration of one packet in the codec's own sample clock, or zero for a codec whose
    /// packets this demuxer cannot measure.
    /// </summary>
    private static int PacketDuration(StreamTiming timing, ReadOnlySpan<byte> packet)
    {
        if (timing.Vorbis != null)
        {
            // Vorbis block sizes chain: a packet's duration depends on the size of the block
            // before it, so this reads the parser's running state and must not reset it. The
            // page-start derivation does the resetting, which is why most packets end up
            // measured from the short block size and the first packet of a page does not.
            int duration = timing.Vorbis.ParseFrame(packet, out _);
            return duration > 0 ? duration : 0;
        }

        if (timing.IsOpus)
        {
            if (!OpusPacketParser.TryParsePacket(packet, out var info))
                return 0;
            return OpusPacketParser.GetPacketDuration(info);
        }

        return 0;
    }

    private bool SyncToNextPage()
    {
        Span<byte> syncBuffer = stackalloc byte[4];
        int bytesRead = 0;

        while (bytesRead < 65536)
        {
            if (inputStream.Read(syncBuffer) != 4)
                return false;

            if (syncBuffer[0] == 'O' && syncBuffer[1] == 'g' &&
                syncBuffer[2] == 'g' && syncBuffer[3] == 'S')
            {
                inputStream.Position -= 4;
                return true;
            }

            // Shift and continue
            inputStream.Position -= 3;
            bytesRead++;
        }

        return false;
    }

    private OggStream? IdentifyCodec(OggPageHeader header, byte[] pageData)
    {
        if (pageData.Length < 8)
            return null;

        int dataOffset = header.SegmentCount;
        var data = pageData.AsSpan(dataOffset);

        OggCodecType codecType = OggCodecType.Unknown;
        uint sampleRate = 0;
        byte channels = 0;
        ushort width = 0, height = 0;
        uint fpsNum = 0, fpsDen = 1;
        int preSkip = 0;
        byte[] codecPrivate = [];

        // Check Vorbis: "\x01vorbis"
        if (data.Length >= 7 && data[0] == 0x01 &&
            data[1] == 'v' && data[2] == 'o' && data[3] == 'r' &&
            data[4] == 'b' && data[5] == 'i' && data[6] == 's')
        {
            codecType = OggCodecType.Vorbis;
            if (data.Length >= 30)
            {
                channels = data[11];
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
            }
            codecPrivate = data.ToArray();
        }
        // Check Opus: "OpusHead"
        else if (data.Length >= 8 &&
            data[0] == 'O' && data[1] == 'p' && data[2] == 'u' && data[3] == 's' &&
            data[4] == 'H' && data[5] == 'e' && data[6] == 'a' && data[7] == 'd')
        {
            codecType = OggCodecType.Opus;
            if (data.Length >= 19)
            {
                channels = data[9];
                // The samples the decoder discards from the front of the stream; the timestamps
                // are shifted by it so the first sample the listener hears is at zero.
                preSkip = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
            }
            codecPrivate = data.ToArray();
        }
        // Check Theora: "\x80theora"
        else if (data.Length >= 7 && data[0] == 0x80 &&
            data[1] == 't' && data[2] == 'h' && data[3] == 'e' &&
            data[4] == 'o' && data[5] == 'r' && data[6] == 'a')
        {
            codecType = OggCodecType.Theora;
            if (data.Length >= 42)
            {
                width = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(data[14..]) << 4);
                height = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(data[16..]) << 4);
                fpsNum = BinaryPrimitives.ReadUInt32BigEndian(data[22..]);
                fpsDen = BinaryPrimitives.ReadUInt32BigEndian(data[26..]);
            }
            codecPrivate = data.ToArray();
        }
        // Check FLAC: "\x7FFLAC"
        else if (data.Length >= 5 && data[0] == 0x7F &&
            data[1] == 'F' && data[2] == 'L' && data[3] == 'A' && data[4] == 'C')
        {
            codecType = OggCodecType.Flac;
            if (data.Length >= 51)
            {
                // FLAC in OGG has mapping header + STREAMINFO
                int streamInfoOffset = 13; // Mapping header is 13 bytes
                if (streamInfoOffset + 34 <= data.Length)
                {
                    var streamInfo = data.Slice(streamInfoOffset);
                    sampleRate = (uint)((streamInfo[10] << 12) | (streamInfo[11] << 4) | (streamInfo[12] >> 4));
                    channels = (byte)(((streamInfo[12] >> 1) & 0x07) + 1);
                }
            }
            codecPrivate = data.ToArray();
        }
        // Check Speex: "Speex   "
        else if (data.Length >= 8 &&
            data[0] == 'S' && data[1] == 'p' && data[2] == 'e' && data[3] == 'e' &&
            data[4] == 'x' && data[5] == ' ' && data[6] == ' ' && data[7] == ' ')
        {
            codecType = OggCodecType.Speex;
            if (data.Length >= 80)
            {
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(data[36..]);
                channels = (byte)BinaryPrimitives.ReadUInt32LittleEndian(data[48..]);
            }
            codecPrivate = data.ToArray();
        }
        else
        {
            return null;
        }

        var identified = new OggStream
        {
            SerialNumber = header.SerialNumber,
            CodecType = codecType,
            StreamIndex = streams.Count,
            CodecPrivateData = codecPrivate,
            SampleRate = sampleRate,
            Channels = channels,
            Width = width,
            Height = height,
            FrameRateNumerator = fpsNum,
            FrameRateDenominator = fpsDen,
            PreSkip = preSkip,
        };

        // A beginning-of-stream page carries exactly one packet, the identification header.
        identified.HeaderPackets.Add(codecPrivate);
        return identified;
    }

    private bool ProcessHeaderPage(OggPageHeader header, byte[] pageData)
    {
        if (!streamsBySerial.TryGetValue(header.SerialNumber, out var stream))
            return false;

        // A header page is still a page: its packets have boundaries, and a Vorbis decoder
        // cannot be initialised from the three headers run together. Splitting here is what
        // lets the pipeline hand a decoder the same extradata a Matroska file would.
        SplitPageIntoPackets(header, pageData);

        foreach (var (data, _) in pagePackets)
        {
            stream.HeaderPackets.Add(data);

            var combined = new byte[stream.CodecPrivateData.Length + data.Length];
            stream.CodecPrivateData.CopyTo(combined, 0);
            data.CopyTo(combined, stream.CodecPrivateData.Length);
            stream.CodecPrivateData = combined;
        }

        return true;
    }

    public void Dispose()
    {
        if (isDisposed)
            return;
        isDisposed = true;
    }
}

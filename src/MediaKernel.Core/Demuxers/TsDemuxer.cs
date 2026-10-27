using System;
using System.Buffers;
using System.Buffers.Binary;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// High-performance MPEG Transport Stream (TS) demuxer.
/// Parses PAT/PMT program tables and extracts PES packets.
/// </summary>
/// <remarks>
/// MPEG-TS uses fixed 188-byte packets with 4-byte headers.
/// Supports standard broadcast and HLS/DASH segment formats.
/// </remarks>
public sealed class TsDemuxer : IDisposable
{
    private readonly Stream inputStream;
    private readonly byte[] packetBuffer;

    /// <summary>
    /// A second packet-sized buffer, for the timestamp probes a seek runs. It is deliberately
    /// not <see cref="packetBuffer"/>: a probe reads packets from all over the file between two
    /// ordinary reads, and sharing the buffer would hand the assembler a packet from wherever
    /// the search last looked.
    /// </summary>
    private readonly byte[] probeBuffer;
    private readonly Dictionary<ushort, TsPidInfo> pidMap = new();
    private readonly Dictionary<ushort, TsProgram> programs = new();
    private readonly List<TsTrack> tracks = new();
    private readonly Dictionary<ushort, PesAssembler> pesAssemblers = new();

    private ushort transportStreamId;
    private bool patParsed;
    private bool isInitialized;
    private bool isDisposed;

    /// <summary>The length of the file, which is what a seek searches within.</summary>
    private readonly long fileSize;

    /// <summary>
    /// Where the 188-byte packet grid begins. A transport stream need not start at a sync byte -
    /// a capture can begin mid-packet - and every position a seek computes has to land on this
    /// grid. <c>ts->pos47_full</c>, which <c>mpegts_get_dts</c> takes modulo the packet size.
    /// </summary>
    private long firstPacketPosition;

    /// <summary>
    /// <c>AVFormatContext.start_time</c> on the 90 kHz clock, or <see cref="NoTimestamp"/>
    /// before <see cref="ProbeTimings"/> has run.
    /// </summary>
    private long startTimestamp = NoTimestamp;

    /// <summary>The last decode timestamp in the file, 90 kHz, or <see cref="NoTimestamp"/>.</summary>
    private long lastTimestamp = NoTimestamp;

    /// <summary>No timestamp: <c>AV_NOPTS_VALUE</c>.</summary>
    private const long NoTimestamp = FormatSeek.NoTimestamp;

    /// <summary>
    /// How many packets <see cref="ProbeTimings"/> will read looking for each track's first
    /// timestamp. <c>avformat_find_stream_info</c> is bounded the same way (<c>probesize</c>);
    /// a stream whose second track only appears minutes in would otherwise be read whole
    /// before the first frame was decoded.
    /// </summary>
    private const int MaxTimingProbePackets = 20_000;

    public const int PacketSize = 188;
    public const int SyncByte = 0x47;
    private const int MaxSyncSearchBytes = 1024;

    public TsDemuxer(Stream stream)
    {
        inputStream = stream ?? throw new ArgumentNullException(nameof(stream));
        packetBuffer = ArrayPool<byte>.Shared.Rent(PacketSize);
        probeBuffer = ArrayPool<byte>.Shared.Rent(PacketSize);
        fileSize = stream.CanSeek ? stream.Length : 0;
    }

    /// <summary>Gets all discovered tracks after initialization.</summary>
    public IReadOnlyList<TsTrack> Tracks => tracks;

    /// <summary>Gets all discovered programs.</summary>
    public IReadOnlyDictionary<ushort, TsProgram> Programs => programs;

    /// <summary>Gets the transport stream ID.</summary>
    public ushort TransportStreamId => transportStreamId;

    /// <summary>
    /// <c>AVFormatContext.start_time</c>: the 90 kHz timestamp the earliest track begins at, or
    /// -1 if no track stated one. A transport stream commonly starts well away from zero -
    /// ffmpeg's own muxer offsets the first timestamp by 1.4 s - and <c>-ss</c> counts from
    /// here, not from zero.
    /// </summary>
    public long StartTimestamp => startTimestamp == NoTimestamp ? -1 : startTimestamp;

    /// <summary>
    /// The length of the stream, from the first timestamp to the last, or 0 if neither could be
    /// read. <c>update_stream_timings</c>: <c>duration = end_time - start_time</c>.
    /// </summary>
    public long DurationMicroseconds
        => startTimestamp == NoTimestamp || lastTimestamp == NoTimestamp || lastTimestamp <= startTimestamp
            ? 0
            : ((lastTimestamp - startTimestamp) * 1_000_000) / 90_000;

    /// <summary>
    /// Initializes the demuxer by finding PAT/PMT and building track list.
    /// </summary>
    public bool Initialize()
    {
        if (isInitialized)
            return true;

        // Sync to first TS packet
        if (!SyncToPacket())
            return false;

        // SyncToPacket leaves the stream ON the sync byte, so this is where the packet grid
        // begins - every position a seek computes is aligned to it.
        firstPacketPosition = inputStream.Position;

        // Parse packets until we have PAT and at least one PMT
        int packetsRead = 0;
        const int maxInitPackets = 1000;

        while (packetsRead < maxInitPackets)
        {
            if (!ReadPacket(out var packet))
                break;

            ProcessPacket(packet);
            packetsRead++;

            // Stop once we have tracks from PMT
            if (tracks.Count > 0)
                break;
        }

        isInitialized = tracks.Count > 0;

        if (isInitialized && inputStream.CanSeek && fileSize > 0)
            ProbeTimings();

        return isInitialized;
    }

    /// <summary>
    /// Reads the first timestamp of each track and the last one in the file, which is what
    /// <c>avformat_find_stream_info</c> and <c>estimate_timings_from_pts</c> do before the
    /// first frame is handed out.
    /// </summary>
    /// <remarks>
    /// Without this a transport stream had no start time and no duration at all. Both are
    /// visible: <c>-ss</c> is counted from the start of the stream, so on a file whose first
    /// timestamp is 1.433 s - which is what ffmpeg's own muxer writes - every seek was 1.433 s
    /// early, and a duration of zero is what the CLI's progress bar divides by.
    /// <para>
    /// It reads from the start of the packet grid and then puts the stream back exactly where
    /// <see cref="Initialize"/> left it, so what <see cref="ReadNextPacket"/> goes on to hand
    /// out is byte for byte what it handed out before this existed.
    /// </para>
    /// </remarks>
    private void ProbeTimings()
    {
        long resume = inputStream.Position;

        inputStream.Position = firstPacketPosition;

        int remaining = MaxTimingProbePackets;
        int wanted = tracks.Count;

        while (remaining-- > 0 && wanted > 0)
        {
            if (!ReadPacket(out var packet))
                break;

            if (!packet.PayloadUnitStart || !packet.PayloadPresent)
                continue;

            ushort pid = packet.Pid;
            var track = tracks.FirstOrDefault(t => t.Pid == pid);
            if (track == null || track.FirstPresentationTimestamp >= 0)
                continue;

            if (!PesAssembler.TryParsePesTimestamps(packet.GetPayload(), out long pts, out _))
                continue;

            track.FirstPresentationTimestamp = pts;
            wanted--;
        }

        // ffmpeg takes the minimum over the tracks that have one.
        foreach (var track in tracks)
        {
            if (track.FirstPresentationTimestamp >= 0 &&
                (startTimestamp == NoTimestamp || track.FirstPresentationTimestamp < startTimestamp))
            {
                startTimestamp = track.FirstPresentationTimestamp;
            }
        }

        int defaultPid = DefaultPid();
        if (defaultPid >= 0 &&
            FormatSeek.FindLastTimestamp(defaultPid, fileSize, MpegTsReadDts, out long last, out _))
        {
            lastTimestamp = last;
        }

        inputStream.Position = resume;
    }

    /// <summary>
    /// Reads the next PES packet from the stream.
    /// </summary>
    public bool ReadNextPacket(out TsPacketInfo packet)
    {
        packet = default;

        if (!isInitialized)
            throw new InvalidOperationException("Demuxer not initialized. Call Initialize() first.");

        while (true)
        {
            if (!ReadPacket(out var tsPacket))
                return FlushPending(out packet);

            // Check if this PID has a complete PES packet
            if (ProcessPacketForPes(tsPacket, out packet))
                return true;
        }
    }

    /// <summary>
    /// Hands out the PES packets still being assembled when the stream ends. Nothing follows
    /// them to mark their end, so without this the last packet of every track is dropped - a
    /// whole picture at the end of the file, which is a silent short count rather than an error.
    /// </summary>
    private bool FlushPending(out TsPacketInfo packet)
    {
        packet = default;

        foreach (var track in tracks)
        {
            if (!pesAssemblers.TryGetValue(track.Pid, out var assembler)) continue;
            if (!assembler.TryFlush(out var completed)) continue;

            packet = new TsPacketInfo
            {
                Pid = track.Pid,
                TrackIndex = tracks.IndexOf(track),
                PresentationTimeTicks = completed.Pts >= 0 ? PtsToTicks(completed.Pts) : -1,
                DecodeTimeTicks = completed.Dts >= 0 ? PtsToTicks(completed.Dts) : -1,
                IsKeyframe = false,
                PayloadData = completed.Data,
            };
            return true;
        }

        return false;
    }

    /// <summary>
    /// Seeks to the last packet at or before a time, so that decoding from there reaches the
    /// time asked for.
    /// </summary>
    /// <remarks>
    /// <c>mpegts_read_seek</c>, which is <c>ff_seek_frame_binary</c> over
    /// <see cref="MpegTsReadDts"/>: a transport stream has no index, so the position is found
    /// by reading timestamps out of the file and narrowing in on the target.
    /// <para>
    /// WHAT THIS REPLACED. A byte position computed from a bitrate that was not measured but
    /// hardcoded - <c>GetEstimatedBitrate</c> returned 20 Mbit/s with a comment saying so. The
    /// only transport stream in this tree runs at 1.47 Mbit/s, so the estimate was 13.6 times
    /// too large and <c>-ss 20</c> landed 273 seconds into the file: past the target, so the
    /// trim could never find its window, and the seek reported success.
    /// </para>
    /// </remarks>
    public bool Seek(double timestampMs)
    {
        if (!inputStream.CanSeek || fileSize <= 0)
            return false;

        int pid = DefaultPid();
        if (pid < 0)
            return false;

        // The timestamps in the file are 90 kHz, as every MPEG system timestamp is.
        long targetTimestamp = (long)Math.Round(timestampMs * 90.0);

        long position = FormatSeek.SeekFrameBinary(pid, targetTimestamp, fileSize, MpegTsReadDts);
        if (position < 0)
            return false;

        // Only once the search has succeeded: a failed seek leaves the demuxer where it was,
        // and throwing away the half-assembled packets would lose a picture for nothing.
        foreach (var assembler in pesAssemblers.Values)
            assembler.Reset();

        inputStream.Position = position;
        return true;
    }

    /// <summary>
    /// <c>av_find_default_stream_index</c>: the stream a seek is aimed at. Video when there is
    /// one, because that is where the keyframes are. The PID is the identity here, as the
    /// stream index is in the C.
    /// </summary>
    private int DefaultPid()
    {
        int fallback = -1;

        foreach (var track in tracks)
        {
            if (track.TrackType == TsTrackType.Video)
                return track.Pid;
            if (fallback < 0)
                fallback = track.Pid;
        }

        return fallback;
    }

    /// <summary>
    /// <c>mpegts_get_dts</c>: the decode timestamp of the first PES packet of one PID at or
    /// after a byte position, and where that packet starts.
    /// </summary>
    /// <remarks>
    /// The C reads whole frames through <c>av_read_frame</c> and takes the dts of the first one
    /// belonging to the stream, whose <c>pkt-&gt;pos</c> is <c>pes-&gt;ts_packet_pos</c> - the
    /// position of the TS packet that carried the PES header. This reads that header straight
    /// out of that packet instead of reassembling a payload the search would only discard,
    /// which is the same divergence the program stream's probe records
    /// (<c>ParsePesPacket(discardData: true)</c>): the same timestamp at the same position,
    /// without the copying or the state.
    /// </remarks>
    private long MpegTsReadDts(int pid, ref long position, long positionLimit)
    {
        if (!inputStream.CanSeek || position < 0 || position >= fileSize)
            return NoTimestamp;

        // "pos = ((*ppos + raw_packet_size - 1 - pos47) / raw_packet_size) * raw_packet_size
        //        + pos47" - round the requested position up onto the packet grid.
        long pos47 = firstPacketPosition % PacketSize;
        long pos = ((position + PacketSize - 1 - pos47) / PacketSize) * PacketSize + pos47;

        while (pos < positionLimit && pos + PacketSize <= fileSize)
        {
            inputStream.Position = pos;

            if (inputStream.Read(probeBuffer, 0, PacketSize) != PacketSize)
                return NoTimestamp;

            if (probeBuffer[0] != SyncByte)
            {
                // "if (buf[0] != SYNC_BYTE) { if (mpegts_resync(...) < 0) return
                // AV_NOPTS_VALUE; pos = avio_tell(s->pb); continue; }"
                inputStream.Position = pos;
                if (!SyncToPacket())
                    return NoTimestamp;
                pos = inputStream.Position;
                continue;
            }

            var packet = TsPacket.Parse(probeBuffer.AsSpan(0, PacketSize));

            if (packet.Pid == pid && packet.PayloadUnitStart && packet.PayloadPresent &&
                PesAssembler.TryParsePesTimestamps(packet.GetPayload(), out long pts, out long dts))
            {
                // "*pts = *dts = AV_NOPTS_VALUE; ... if (PTS) *dts = *pts = get_pts()" - a
                // packet with only a presentation timestamp decodes at that time too.
                long timestamp = dts >= 0 ? dts : pts;
                if (timestamp >= 0)
                {
                    position = pos;
                    return timestamp;
                }
            }

            pos += PacketSize;
        }

        return NoTimestamp;
    }

    private bool SyncToPacket()
    {
        int bytesSearched = 0;
        int b;

        while (bytesSearched < MaxSyncSearchBytes && (b = inputStream.ReadByte()) >= 0)
        {
            if (b == SyncByte)
            {
                // Verify this is a valid sync by checking next packet
                long pos = inputStream.Position;
                inputStream.Position = pos + PacketSize - 1;
                int nextByte = inputStream.ReadByte();
                inputStream.Position = pos - 1; // Rewind to sync byte

                if (nextByte == SyncByte || nextByte < 0)
                    return true;
            }
            bytesSearched++;
        }

        return false;
    }

    private bool ReadPacket(out TsPacket packet)
    {
        packet = default;

        int bytesRead = inputStream.Read(packetBuffer, 0, PacketSize);
        if (bytesRead < PacketSize)
            return false;

        if (packetBuffer[0] != SyncByte)
        {
            // Lost sync, try to resync
            if (!SyncToPacket())
                return false;
            bytesRead = inputStream.Read(packetBuffer, 0, PacketSize);
            if (bytesRead < PacketSize)
                return false;
        }

        // The span, not the array: ArrayPool.Rent(188) hands back a 256-byte array, and a
        // TsPacket over the whole of it reported 68 bytes of stale buffer as payload on
        // every single packet - which is how an assembled video stream came out a third
        // larger than the one ffmpeg extracts from the same file.
        packet = TsPacket.Parse(packetBuffer.AsSpan(0, PacketSize));
        return true;
    }

    private void ProcessPacket(TsPacket packet)
    {
        ushort pid = packet.Pid;

        // Track PID statistics
        if (!pidMap.TryGetValue(pid, out var pidInfo))
        {
            pidInfo = new TsPidInfo(pid);
            pidMap[pid] = pidInfo;
        }
        pidInfo.PacketCount++;

        // Handle special PIDs
        switch (pid)
        {
            case TsReservedPid.Pat:
                ParsePat(packet);
                break;

            case TsReservedPid.NullPacket:
                // Ignore null packets
                break;

            default:
                // Check if this is a PMT PID
                if (programs.Values.Any(p => p.PmtPid == pid && !p.PmtParsed))
                {
                    ParsePmt(packet);
                }
                break;
        }
    }

    private bool ProcessPacketForPes(TsPacket packet, out TsPacketInfo pesPacket)
    {
        pesPacket = default;
        ushort pid = packet.Pid;

        // Only process PIDs that are elementary streams
        var track = tracks.FirstOrDefault(t => t.Pid == pid);
        if (track == null)
            return false;

        // Get or create PES assembler for this PID
        if (!pesAssemblers.TryGetValue(pid, out var assembler))
        {
            assembler = new PesAssembler(pid);
            pesAssemblers[pid] = assembler;
        }

        // Add packet data to assembler
        if (assembler.AddPacket(packet, out var completedPes))
        {
            pesPacket = new TsPacketInfo
            {
                Pid = pid,
                TrackIndex = tracks.IndexOf(track),
                PresentationTimeTicks = completedPes.Pts >= 0 
                    ? PtsToTicks(completedPes.Pts) 
                    : -1,
                DecodeTimeTicks = completedPes.Dts >= 0 
                    ? PtsToTicks(completedPes.Dts) 
                    : -1,
                IsKeyframe = packet.RandomAccessIndicator,
                PayloadData = completedPes.Data
            };
            return true;
        }

        return false;
    }

    private void ParsePat(TsPacket packet)
    {
        if (patParsed)
            return;

        var payload = packet.GetPayload();
        if (payload.Length < 8)
            return;

        // Skip pointer field if present
        int offset = 0;
        if (packet.PayloadUnitStart)
        {
            offset = payload[0] + 1;
            if (offset >= payload.Length)
                return;
        }

        var section = payload[offset..];
        if (section.Length < 8)
            return;

        byte tableId = section[0];
        if (tableId != 0x00) // PAT table ID
            return;

        int sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        transportStreamId = BinaryPrimitives.ReadUInt16BigEndian(section.Slice(3, 2));

        // Skip version/current_next/section_number/last_section_number
        int programOffset = 8;
        int programEnd = Math.Min(3 + sectionLength - 4, section.Length); // -4 for CRC

        while (programOffset + 4 <= programEnd)
        {
            ushort programNumber = BinaryPrimitives.ReadUInt16BigEndian(section.Slice(programOffset, 2));
            ushort pmtPid = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(section.Slice(programOffset + 2, 2)) & 0x1FFF);

            if (programNumber != 0) // Skip NIT reference
            {
                if (!programs.ContainsKey(programNumber))
                {
                    programs[programNumber] = new TsProgram(programNumber, pmtPid);
                }
            }

            programOffset += 4;
        }

        patParsed = true;
    }

    private void ParsePmt(TsPacket packet)
    {
        var payload = packet.GetPayload();
        if (payload.Length < 12)
            return;

        // Skip pointer field if present
        int offset = 0;
        if (packet.PayloadUnitStart)
        {
            offset = payload[0] + 1;
            if (offset >= payload.Length)
                return;
        }

        var section = payload[offset..];
        if (section.Length < 12)
            return;

        byte tableId = section[0];
        if (tableId != 0x02) // PMT table ID
            return;

        int sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        ushort programNumber = BinaryPrimitives.ReadUInt16BigEndian(section.Slice(3, 2));

        if (!programs.TryGetValue(programNumber, out var program))
            return;

        ushort pcrPid = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(section.Slice(8, 2)) & 0x1FFF);
        int programInfoLength = ((section[10] & 0x0F) << 8) | section[11];

        program.PcrPid = pcrPid;

        // Skip program descriptors
        int esOffset = 12 + programInfoLength;
        int sectionEnd = Math.Min(3 + sectionLength - 4, section.Length);

        while (esOffset + 5 <= sectionEnd)
        {
            byte streamType = section[esOffset];
            ushort esPid = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(section.Slice(esOffset + 1, 2)) & 0x1FFF);
            int esInfoLength = ((section[esOffset + 3] & 0x0F) << 8) | section[esOffset + 4];

            // Parse ES descriptors for language, etc.
            string? language = null;
            uint registration = 0;
            if (esInfoLength > 0 && esOffset + 5 + esInfoLength <= section.Length)
            {
                ReadOnlySpan<byte> esDescriptors = section.Slice(esOffset + 5, esInfoLength);
                language = ParseEsDescriptors(esDescriptors);
                registration = ParseRegistrationDescriptor(esDescriptors);
            }

            // Stream type 0x06 is "PES private data" and says nothing about the payload; the
            // registration descriptor does. 'BSSD' is SMPTE 302M, which is how linear PCM
            // rides in a transport stream. Without this the track was typed Unknown and
            // dropped, so a 302M stream decoded to nothing at all.
            TsTrackType trackType = GetTrackType(streamType);
            string codecName = GetCodecName(streamType);
            if (streamType == TsStreamType.PrivateData && registration == BssdRegistration)
            {
                trackType = TsTrackType.Audio;
                codecName = "s302m";
            }

            // Create track for this elementary stream
            var track = new TsTrack
            {
                Pid = esPid,
                StreamType = streamType,
                ProgramNumber = programNumber,
                TrackType = trackType,
                CodecName = codecName,
                Language = language
            };

            // Avoid duplicates
            if (!tracks.Any(t => t.Pid == esPid))
            {
                tracks.Add(track);
            }

            esOffset += 5 + esInfoLength;
        }

        program.PmtParsed = true;
    }

    /// <summary>'BSSD', the registration descriptor value SMPTE 302M carries.</summary>
    private const uint BssdRegistration = 0x42535344;

    /// <summary>
    /// The format identifier of an ES registration descriptor (tag 0x05), or 0 if there is
    /// none. It is the only thing that says what a stream type 0x06 payload actually is.
    /// </summary>
    private static uint ParseRegistrationDescriptor(ReadOnlySpan<byte> descriptors)
    {
        int offset = 0;
        while (offset + 2 <= descriptors.Length)
        {
            byte tag = descriptors[offset];
            byte length = descriptors[offset + 1];

            if (offset + 2 + length > descriptors.Length)
                break;

            if (tag == 0x05 && length >= 4)
                return BinaryPrimitives.ReadUInt32BigEndian(descriptors.Slice(offset + 2, 4));

            offset += 2 + length;
        }

        return 0;
    }

    private string? ParseEsDescriptors(ReadOnlySpan<byte> descriptors)
    {
        int offset = 0;
        while (offset + 2 <= descriptors.Length)
        {
            byte tag = descriptors[offset];
            byte length = descriptors[offset + 1];

            if (offset + 2 + length > descriptors.Length)
                break;

            // ISO 639 language descriptor
            if (tag == 0x0A && length >= 3)
            {
                return System.Text.Encoding.ASCII.GetString(descriptors.Slice(offset + 2, 3));
            }

            offset += 2 + length;
        }

        return null;
    }

    private static TsTrackType GetTrackType(byte streamType)
    {
        return streamType switch
        {
            TsStreamType.Mpeg1Video or TsStreamType.Mpeg2Video or 
            TsStreamType.Mpeg4Video or TsStreamType.H264 or 
            TsStreamType.H265 or TsStreamType.Vp9 => TsTrackType.Video,

            TsStreamType.Mpeg1Audio or TsStreamType.Mpeg2Audio or 
            TsStreamType.AacAdts or TsStreamType.AacLatm or 
            TsStreamType.Ac3 or TsStreamType.Eac3 or 
            TsStreamType.Dts or TsStreamType.DtsHd or 
            TsStreamType.TrueHd or TsStreamType.Lpcm => TsTrackType.Audio,

            TsStreamType.DvbSubtitle or TsStreamType.Teletext => TsTrackType.Subtitle,

            _ => TsTrackType.Unknown
        };
    }

    private static string GetCodecName(byte streamType)
    {
        return streamType switch
        {
            TsStreamType.Mpeg1Video => "mpeg1",
            TsStreamType.Mpeg2Video => "mpeg2",
            TsStreamType.Mpeg4Video => "mpeg4",
            TsStreamType.H264 => "h264",
            TsStreamType.H265 => "hevc",
            TsStreamType.Vp9 => "vp9",
            TsStreamType.Mpeg1Audio or TsStreamType.Mpeg2Audio => "mp3",
            TsStreamType.AacAdts or TsStreamType.AacLatm => "aac",
            TsStreamType.Ac3 => "ac3",
            TsStreamType.Eac3 => "eac3",
            TsStreamType.Dts or TsStreamType.DtsHd => "dts",
            TsStreamType.TrueHd => "truehd",
            TsStreamType.Lpcm => "lpcm",
            TsStreamType.DvbSubtitle => "dvbsub",
            TsStreamType.Teletext => "teletext",
            _ => "unknown"
        };
    }

    private static long PtsToTicks(long pts)
    {
        // PTS is in 90kHz clock units
        return pts * TimeSpan.TicksPerSecond / 90000;
    }

    public void Dispose()
    {
        if (isDisposed)
            return;
        isDisposed = true;
        ArrayPool<byte>.Shared.Return(packetBuffer);
        ArrayPool<byte>.Shared.Return(probeBuffer);
    }
}

/// <summary>
/// Parsed TS packet header and data.
/// </summary>
public readonly ref struct TsPacket
{
    public readonly ushort Pid;
    public readonly byte ContinuityCounter;
    public readonly bool PayloadUnitStart;
    public readonly bool AdaptationFieldPresent;
    public readonly bool PayloadPresent;
    public readonly bool RandomAccessIndicator;
    public readonly int AdaptationFieldLength;
    private readonly ReadOnlySpan<byte> rawData;

    private TsPacket(ReadOnlySpan<byte> data, ushort pid, byte cc, bool pusi, 
        bool hasAf, bool hasPayload, bool rai, int afLen)
    {
        rawData = data;
        Pid = pid;
        ContinuityCounter = cc;
        PayloadUnitStart = pusi;
        AdaptationFieldPresent = hasAf;
        PayloadPresent = hasPayload;
        RandomAccessIndicator = rai;
        AdaptationFieldLength = afLen;
    }

    public static TsPacket Parse(ReadOnlySpan<byte> data)
    {
        ushort pid = (ushort)(((data[1] & 0x1F) << 8) | data[2]);
        byte cc = (byte)(data[3] & 0x0F);
        bool pusi = (data[1] & 0x40) != 0;
        byte adaptationControl = (byte)((data[3] >> 4) & 0x03);
        bool hasAf = (adaptationControl & 0x02) != 0;
        bool hasPayload = (adaptationControl & 0x01) != 0;

        int afLen = 0;
        bool rai = false;
        if (hasAf && data.Length > 4)
        {
            afLen = data[4];
            if (afLen > 0 && data.Length > 5)
            {
                rai = (data[5] & 0x40) != 0;
            }
        }

        return new TsPacket(data, pid, cc, pusi, hasAf, hasPayload, rai, afLen);
    }

    public ReadOnlySpan<byte> GetPayload()
    {
        int offset = 4;
        if (AdaptationFieldPresent)
            offset += 1 + AdaptationFieldLength;

        if (offset >= rawData.Length)
            return ReadOnlySpan<byte>.Empty;

        return rawData[offset..];
    }
}

/// <summary>
/// PID statistics and metadata.
/// </summary>
public class TsPidInfo
{
    public ushort Pid { get; }
    public int PacketCount { get; set; }

    public TsPidInfo(ushort pid)
    {
        Pid = pid;
    }
}

/// <summary>
/// Program information from PAT/PMT.
/// </summary>
public class TsProgram
{
    public ushort ProgramNumber { get; }
    public ushort PmtPid { get; }
    public ushort PcrPid { get; set; }
    public bool PmtParsed { get; set; }

    public TsProgram(ushort programNumber, ushort pmtPid)
    {
        ProgramNumber = programNumber;
        PmtPid = pmtPid;
    }
}

/// <summary>
/// Track information discovered from PMT.
/// </summary>
public class TsTrack
{
    public ushort Pid { get; set; }
    public byte StreamType { get; set; }
    public ushort ProgramNumber { get; set; }
    public TsTrackType TrackType { get; set; }
    public string CodecName { get; set; } = "unknown";
    public string? Language { get; set; }

    /// <summary>
    /// The first presentation timestamp this track states, on the 90 kHz clock, or -1 if the
    /// timing probe did not find one. <c>AVStream.start_time</c>.
    /// </summary>
    public long FirstPresentationTimestamp { get; set; } = -1;
}

/// <summary>
/// Completed PES packet info for output.
/// </summary>
public struct TsPacketInfo
{
    public ushort Pid;
    public int TrackIndex;
    public long PresentationTimeTicks;
    public long DecodeTimeTicks;
    public bool IsKeyframe;
    public byte[] PayloadData;
}

/// <summary>
/// Assembles PES packets from TS packet fragments.
/// </summary>
internal class PesAssembler
{
    private readonly ushort pid;

    /// <summary>
    /// The elementary payload of the PES packet being assembled, without the PES header. A
    /// plain array rather than a List&lt;byte&gt;: this grows by one TS payload at a time, and a
    /// List took a fresh array from every one of them.
    /// </summary>
    private byte[] buffer = new byte[8192];
    private int length;

    private long pts = -1;
    private long dts = -1;

    /// <summary>
    /// How many payload bytes this PES says it carries, or zero for an unbounded one. This is
    /// NOT PES_packet_length: that field counts the three flag bytes and the optional header as
    /// well, so comparing it against the payload alone can never be satisfied, and the payload
    /// is never trimmed to it - which is how trailing adaptation field stuffing used to reach
    /// the decoder and how whole packets used to be dropped as "incomplete".
    /// </summary>
    private int expectedPayloadLength;

    private bool hasStarted;

    public PesAssembler(ushort pid)
    {
        this.pid = pid;
    }

    public bool AddPacket(TsPacket packet, out CompletedPes completed)
    {
        completed = default;

        if (!packet.PayloadPresent)
            return false;

        var payload = packet.GetPayload();
        if (payload.IsEmpty)
            return false;

        if (packet.PayloadUnitStart)
        {
            // Whatever was being assembled ends here, whether or not it reached the length it
            // declared. A short PES is the best the stream offers and still decodes; dropping
            // it would drop a picture silently, which is the failure this demuxer had.
            bool hasComplete = hasStarted && length > 0;
            if (hasComplete)
                completed = new CompletedPes(buffer[..length], pts, dts);

            Reset();

            if (payload.Length >= 9 &&
                payload[0] == 0x00 && payload[1] == 0x00 && payload[2] == 0x01)
            {
                ParsePesHeader(payload);
            }

            return hasComplete;
        }

        // Continue previous PES packet
        if (hasStarted)
        {
            Append(payload);

            if (expectedPayloadLength > 0 && length >= expectedPayloadLength)
            {
                completed = new CompletedPes(buffer[..expectedPayloadLength], pts, dts);
                Reset();
                return true;
            }
        }

        return false;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (length + data.Length > buffer.Length)
        {
            int capacity = buffer.Length;
            while (capacity < length + data.Length) capacity *= 2;
            Array.Resize(ref buffer, capacity);
        }

        data.CopyTo(buffer.AsSpan(length));
        length += data.Length;
    }

    private void ParsePesHeader(ReadOnlySpan<byte> data)
    {
        hasStarted = true;

        int pesPacketLength = (data[4] << 8) | data[5];
        byte streamId = data[3];

        // Check if this stream has PTS/DTS
        if (streamId >= 0xE0 || (streamId >= 0xC0 && streamId <= 0xDF) || streamId == 0xBD)
        {
            byte flags = data[7];
            int headerDataLength = data[8];
            int offset = 9;

            // PTS present
            if ((flags & 0x80) != 0 && offset + 5 <= data.Length)
            {
                pts = ParseTimestamp(data.Slice(offset, 5));
                offset += 5;
            }

            // DTS present
            if ((flags & 0x40) != 0 && offset + 5 <= data.Length)
            {
                dts = ParseTimestamp(data.Slice(offset, 5));
            }
            else
            {
                dts = pts;
            }

            // PES_packet_length counts everything after itself: the three flag and length bytes,
            // the optional header, then the payload. Zero means unbounded, which video uses when
            // a picture will not fit in 65535 bytes.
            expectedPayloadLength = pesPacketLength == 0
                ? 0 : Math.Max(0, pesPacketLength - 3 - headerDataLength);

            int payloadStart = 9 + headerDataLength;
            if (payloadStart < data.Length)
                Append(data[payloadStart..]);
        }
        else
        {
            // No optional header on these, so the declared length is the payload length.
            expectedPayloadLength = pesPacketLength;
            Append(data[6..]);
        }

        // A PES small enough to sit in the packet that starts it is already complete, and
        // nothing after it will add to it.
        if (expectedPayloadLength > 0 && length > expectedPayloadLength)
            length = expectedPayloadLength;
    }

    /// <summary>
    /// The presentation and decode timestamps a TS payload's PES header states, without
    /// assembling the packet. Used by the seek's timestamp probe, which wants the header of a
    /// packet whose payload it is going to throw away.
    /// </summary>
    /// <returns>
    /// False when this payload does not begin a PES packet, or begins one that carries no
    /// timestamp - which is the common case for every TS packet but the first of each PES.
    /// </returns>
    public static bool TryParsePesTimestamps(ReadOnlySpan<byte> data, out long pts, out long dts)
    {
        pts = -1;
        dts = -1;

        if (data.Length < 14 || data[0] != 0x00 || data[1] != 0x00 || data[2] != 0x01)
            return false;

        byte streamId = data[3];
        if (streamId < 0xC0 && streamId != 0xBD)
            return false;

        byte flags = data[7];
        if ((flags & 0x80) == 0)
            return false;

        pts = ParseTimestamp(data.Slice(9, 5));
        dts = (flags & 0x40) != 0 && data.Length >= 19 ? ParseTimestamp(data.Slice(14, 5)) : pts;
        return true;
    }

    private static long ParseTimestamp(ReadOnlySpan<byte> data)
    {
        // PTS/DTS are 33-bit values split across 5 bytes
        long ts = ((long)(data[0] & 0x0E) << 29) |
                  ((long)data[1] << 22) |
                  ((long)(data[2] & 0xFE) << 14) |
                  ((long)data[3] << 7) |
                  ((long)(data[4] & 0xFE) >> 1);
        return ts;
    }

    /// <summary>
    /// Hands out whatever is assembled, once, and leaves the assembler empty. Used at the end
    /// of the stream, where no payload unit start follows to close the last PES packet.
    /// </summary>
    public bool TryFlush(out CompletedPes completed)
    {
        completed = default;
        if (!hasStarted || length == 0)
            return false;

        completed = new CompletedPes(buffer[..length], pts, dts);
        Reset();
        return true;
    }

    public void Reset()
    {
        length = 0;
        pts = -1;
        dts = -1;
        expectedPayloadLength = 0;
        hasStarted = false;
    }
}

/// <summary>
/// Completed PES packet with data and timestamps.
/// </summary>
internal readonly struct CompletedPes
{
    public readonly byte[] Data;
    public readonly long Pts;
    public readonly long Dts;

    public CompletedPes(byte[] data, long pts, long dts)
    {
        Data = data;
        Pts = pts;
        Dts = dts;
    }
}

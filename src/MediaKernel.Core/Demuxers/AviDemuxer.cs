using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// AVI container demuxer for legacy media files.
/// Parses RIFF/AVI structure and extracts audio/video chunks.
/// </summary>
public sealed class AviDemuxer : IDisposable
{
    private readonly Stream inputStream;
    private readonly byte[] readBuffer;
    private readonly List<AviTrack> tracks = new();

    /// <summary>
    /// <c>amv_file_format</c> - the RIFF form type was <c>AMV </c> rather than <c>AVI </c>.
    /// </summary>
    /// <remarks>
    /// An AMV file is RIFF with AVI's shape and four deliberate differences, which is why
    /// avidec.c reads it with the same demuxer and a flag rather than a demuxer of its own:
    /// the main header is tagged <c>amvh</c>, the stream headers do not state their type,
    /// the video stream's format chunk is not a BITMAPINFOHEADER (the picture size comes
    /// from the main header instead), and the audio is IMA ADPCM whatever the format chunk
    /// claims.
    /// </remarks>
    private bool isAmvFileFormat;

    /// <summary><c>avih_width</c> - the main header's, which only AMV reads.</summary>
    private int avihWidth;

    /// <summary><c>avih_height</c>.</summary>
    private int avihHeight;

    /// <summary><c>riff_end</c> - where the RIFF header says the file ends.</summary>
    private long declaredRiffEnd;

    /// <summary><c>io_fsize</c> - where it actually ends.</summary>
    private long fileLength;
    private readonly List<AviIndexEntry> index = new();
    private readonly SeekIndex seekIndex = new();

    private long movieDataOffset;
    private long movieDataSize;
    private long idx1Offset;

    // The chunk ReadNextPacket is still handing out packets from. A chunk is not a packet:
    // avi_read_packet cuts raw PCM and small-sample ADPCM into pieces, because one AVI audio
    // chunk can hold a second of PCM and a decoder wants frames.
    private int pendingTrack = -1;
    private long pendingPosition;
    private int pendingRemaining;
    private bool pendingKeyframe;

    /// <summary>
    /// True once the 'idx1' index was found not to describe this file and the reader fell back
    /// to walking the chunks.
    /// </summary>
    /// <remarks>
    /// The fallback keeps a damaged file playable, which is what avi_sync's resynchroniser is
    /// for. It also hides a wrong offset base completely - the packets come out right either
    /// way - so it is reported rather than left silent, and the avi gate asserts it stayed
    /// false. A heuristic that quietly repairs a bug is a bug nobody finds.
    /// </remarks>
    public bool IndexAbandoned { get; private set; }
    private uint idx1Size;
    private uint totalFrames;
    private uint microsecondsPerFrame;
    private bool isInitialized;
    private bool isDisposed;
    private int currentIndexPosition;
    private bool hasKeyframeIndex;

    private const int DefaultBufferSize = 64 * 1024;
    private const uint AviifKeyframe = 0x00000010; // AVIIF_KEYFRAME flag in idx1 entries

    public AviDemuxer(Stream stream)
    {
        inputStream = stream ?? throw new ArgumentNullException(nameof(stream));
        readBuffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
    }

    /// <summary>Gets all discovered tracks.</summary>
    public IReadOnlyList<AviTrack> Tracks => tracks;

    /// <summary>Gets the total frame count.</summary>
    public uint TotalFrames => totalFrames;

    /// <summary>Gets the frame duration in microseconds.</summary>
    public uint MicrosecondsPerFrame => microsecondsPerFrame;

    /// <summary>Gets the duration in milliseconds.</summary>
    /// <remarks>
    /// Each stream states its own length in its <c>strh</c> - a sample count and the rate and
    /// scale that turn it into seconds - and the file's duration is the longest of them. That
    /// is what <c>avi_read_header</c> uses (<c>st->duration = st->nb_frames</c>, in the
    /// stream's own time base); the main header's frame count and frame interval are only the
    /// fallback for a stream that states neither. On <c>Video-xvid-mp3.avi</c> the main header
    /// gives 463 seconds for a file ffprobe measures at 4758.
    /// <para>
    /// A truncated file is rescaled by how much of it is actually there, as the C does when
    /// <c>riff_end</c> is past the end of the file: a partial download states the length of
    /// the whole, and a player that believes it draws a seek bar four times too long.
    /// </para>
    /// </remarks>
    public double DurationMilliseconds
    {
        get
        {
            double longest = 0;

            foreach (var track in tracks)
            {
                if (track.Rate == 0 || track.SampleCount == 0)
                    continue;

                double seconds = track.SampleCount * (double)track.Scale / track.Rate;
                if (seconds > longest)
                    longest = seconds;
            }

            if (longest <= 0)
                longest = totalFrames * microsecondsPerFrame / 1_000_000.0;

            if (declaredRiffEnd > 0 && fileLength > 0 && declaredRiffEnd > fileLength)
                longest = longest * fileLength / declaredRiffEnd;

            return longest * 1000.0;
        }
    }

    /// <summary>Gets whether a keyframe index is available for seeking.</summary>
    public bool HasKeyframeIndex => hasKeyframeIndex;

    /// <summary>
    /// Initializes the demuxer by parsing the AVI header.
    /// </summary>
    public bool Initialize()
    {
        if (isInitialized)
            return true;

        // Read RIFF header
        Span<byte> header = stackalloc byte[12];
        if (inputStream.Read(header) < 12)
            return false;

        uint riffFourCC = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));

        // avi->riff_end against avi->io_fsize: what the file says it holds, and what it does.
        declaredRiffEnd = fileSize + 8L;
        fileLength = inputStream.CanSeek ? inputStream.Length : 0;
        uint aviFourCC = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));

        // 'AMV ' is the form type of an AMV file: the format cheap MP3/MP4 players used,
        // which is RIFF with AVI's shape and a handful of deliberate differences. avidec.c
        // reads it with the same demuxer and a flag, and so does this.
        if (riffFourCC != AviFourCC.Riff
            || (aviFourCC != AviFourCC.Avi && aviFourCC != AviFourCC.Amv))
            return false;

        // Parse chunks
        while (TryReadChunkHeader(out var chunkId, out var chunkSize))
        {
            switch (chunkId)
            {
                case AviFourCC.List:
                    uint listType = ReadFourCC();
                    ParseList(listType, chunkSize >= 4 ? chunkSize - 4 : 0);
                    break;

                case AviFourCC.Idx1:
                    // Remember idx1 location for later parsing
                    idx1Offset = inputStream.Position;
                    idx1Size = chunkSize;
                    SkipBytes(chunkSize);
                    break;

                default:
                    SkipBytes(chunkSize);
                    break;
            }

            // Align to word boundary
            if ((chunkSize & 1) != 0)
                SkipBytes(1);
        }

        // Parse idx1 index if found
        if (idx1Offset > 0 && idx1Size > 0)
        {
            ParseIdx1Index();
        }

        // Seek back to movi data for reading
        if (movieDataOffset > 0)
        {
            inputStream.Position = movieDataOffset;
        }

        isInitialized = tracks.Count > 0;
        return isInitialized;
    }

    /// <summary>
    /// Reads the next chunk from the AVI file.
    /// </summary>
    public bool ReadNextPacket(out AviPacket packet)
    {
        packet = default;

        if (!isInitialized)
            throw new InvalidOperationException("Demuxer not initialized.");

        // Still inside a chunk that has not been handed out in full.
        if (pendingRemaining > 0)
            return CutPacketFromPendingChunk(out packet);

        bool gotChunk = index.Count > 0
            ? ReadPacketFromIndex(out var chunk)
            : ReadNextChunkPacket(out chunk);

        if (!gotChunk)
            return false;

        pendingTrack = chunk.TrackIndex;
        pendingPosition = chunk.PayloadPosition;
        pendingRemaining = chunk.PayloadSize;
        pendingKeyframe = chunk.IsKeyframe;

        return CutPacketFromPendingChunk(out packet);
    }

    /// <summary>
    /// avidec.c's <c>avi_read_packet</c>, the part that decides how much of a chunk one
    /// packet is. The comments are the C's own.
    /// </summary>
    /// <remarks>
    /// <c>ast->sample_size</c> is dwSampleSize from the stream header, zero for video. A
    /// compressed audio format leaves it 0 or 1 and its chunk is one packet; raw PCM sets it
    /// to the bytes per frame, and a chunk holding a second of PCM becomes a packet per 1024
    /// frames. Nothing here had this, so our packets were whole chunks and ffprobe's list was
    /// twice as long as ours for a PCM track.
    /// </remarks>
    private bool CutPacketFromPendingChunk(out AviPacket packet)
    {
        var track = tracks[pendingTrack];
        int sampleSize = track.SampleSize;

        int size;
        if (sampleSize <= 1)
        {
            // "minorityreport.AVI block_align=1024 sample_size=1 IMA-ADPCM"
            size = int.MaxValue;
        }
        else if (sampleSize < 32)
        {
            // "arbitrary multiplier to avoid tiny packets for raw PCM data"
            size = 1024 * sampleSize;
        }
        else
        {
            size = sampleSize;
        }

        if (size > pendingRemaining)
            size = pendingRemaining;

        // "XXX: How to handle B-frames in AVI?" - the C's comment, and the answer is that it
        // does not: AVI stores no presentation timestamps, so this is the decode timestamp and
        // a reordered stream's pictures are presented in the order the decoder hands them back.
        long dts = track.FrameOffset;
        if (sampleSize != 0)
            dts /= sampleSize;

        packet = new AviPacket
        {
            TrackIndex = pendingTrack,
            IsKeyframe = pendingKeyframe,
            PayloadPosition = pendingPosition,
            PayloadSize = size,
            DecodeTimestamp = dts,
            DecodeTimestampTicks = TimestampToTicks(track, dts),
        };

        track.FrameOffset += GetDuration(track, size);

        pendingPosition += size;
        pendingRemaining -= size;
        return true;
    }

    /// <summary>
    /// <c>get_duration</c> - how much of the stream's own time one packet of this many bytes
    /// is. A video frame is one tick whatever its size; raw PCM is counted in bytes, which is
    /// why its timestamp is divided by the sample size; and a compressed format that declares
    /// a block align is counted in blocks, rounding up.
    /// </summary>
    private static long GetDuration(AviTrack track, int length)
    {
        if (track.SampleSize != 0)
            return length;
        if (track.DshowBlockAlign != 0)
            return (length + (long)track.DshowBlockAlign - 1) / track.DshowBlockAlign;
        return 1;
    }

    /// <summary>
    /// A timestamp in the stream's own units as .NET ticks. The stream's time base is
    /// <c>dwScale/dwRate</c> from its header - <c>avpriv_set_pts_info(st, 64, ast-&gt;scale,
    /// ast-&gt;rate)</c> - and is per stream, not per file: the audio and video tracks of one
    /// AVI routinely count in different units.
    /// </summary>
    private static long TimestampToTicks(AviTrack track, long timestamp)
    {
        if (track.Rate == 0)
            return 0;

        // Int128 because the intermediate overflows: an audio stream counts bytes, and a
        // byte count times a scale times ten million passes long.MaxValue on a long file.
        return (long)((Int128)timestamp * track.Scale * TimeSpan.TicksPerSecond / track.Rate);
    }

    /// <summary>
    /// Seeks to the nearest keyframe before the specified timestamp.
    /// </summary>
    public bool Seek(double timestampMs)
    {
        if (index.Count == 0)
            return false;

        // Find target frame
        int targetFrame = (int)(timestampMs * 1000 / microsecondsPerFrame);

        // Find preceding keyframe
        for (int i = Math.Min(targetFrame, index.Count - 1); i >= 0; i--)
        {
            if (index[i].IsKeyframe)
            {
                currentIndexPosition = i;
                pendingRemaining = 0;
                return true;
            }
        }

        currentIndexPosition = 0;
        pendingRemaining = 0;
        return true;
    }

    /// <summary>
    /// Gets the seek index for keyframe-based seeking.
    /// Only populated if the AVI file contains an idx1 chunk.
    /// </summary>
    /// <param name="trackIndex">Track index to get seek points for (default 0 = first video track).</param>
    /// <returns>SeekIndex with keyframe positions.</returns>
    public SeekIndex GetSeekIndex(int trackIndex = 0)
    {
        return seekIndex;
    }

    /// <summary>
    /// Parses the idx1 chunk to build a keyframe index.
    /// </summary>
    private void ParseIdx1Index()
    {
        if (movieDataOffset <= 0)
            return;

        long savedPosition = inputStream.Position;
        inputStream.Position = idx1Offset;

        // Each idx1 entry is 16 bytes: FourCC (4) + Flags (4) + Offset (4) + Size (4)
        int entryCount = (int)(idx1Size / 16);
        Span<byte> entryBuffer = stackalloc byte[16];

        // Determine offset base - idx1 offsets can be relative to movi content or file start
        // We need to check the first few entries to figure this out
        long offsetBase = DetermineIdx1OffsetBase(entryCount, entryBuffer);

        // Reset and read all entries
        inputStream.Position = idx1Offset;

        // Track frame counters per stream for timestamp calculation
        var frameCounters = new int[tracks.Count];
        int videoTrackIndex = -1;
        
        // Find first video track
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].TrackType == AviTrackType.Video)
            {
                videoTrackIndex = i;
                break;
            }
        }

        for (int i = 0; i < entryCount; i++)
        {
            if (inputStream.Read(entryBuffer) < 16)
                break;

            uint chunkId = BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer[..4]);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer.Slice(4, 4));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer.Slice(8, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer.Slice(12, 4));

            bool isKeyframe = (flags & AviifKeyframe) != 0;
            long absoluteOffset = offsetBase + offset;

            // Get stream index from chunk ID (e.g., "00dc" -> stream 0)
            int streamIndex = GetTrackIndexFromChunkId(chunkId);
            if (streamIndex < 0 || streamIndex >= tracks.Count)
                continue;

            // Add to internal index
            index.Add(new AviIndexEntry
            {
                ChunkId = chunkId,
                IsKeyframe = isKeyframe,
                Offset = absoluteOffset,
                Size = (int)size,
                TrackIndex = streamIndex,
                FrameNumber = frameCounters[streamIndex]
            });

            // Add keyframes from video track to seek index
            if (streamIndex == videoTrackIndex && isKeyframe)
            {
                // Calculate timestamp based on frame number (convert from microseconds to ticks)
                long timestampMicroseconds = (long)frameCounters[streamIndex] * microsecondsPerFrame;
                long timestampTicks = timestampMicroseconds * 10; // 1 tick = 100 nanoseconds, 1 microsecond = 10 ticks
                
                seekIndex.Add(new SeekPoint(
                    timestampTicks,
                    absoluteOffset,
                    streamIndex,
                    frameCounters[streamIndex]
                ));
            }

            frameCounters[streamIndex]++;
        }

        hasKeyframeIndex = index.Count > 0 && seekIndex.Count > 0;
        inputStream.Position = savedPosition;
    }

    /// <summary>
    /// Determines whether idx1 offsets are relative to movi content start or file start.
    /// Some AVI files use file-relative offsets (typically offset > movi position),
    /// while others use movi-relative offsets (offset < movi position).
    /// </summary>
    /// <summary>
    /// avidec.c's <c>avi_read_idx1</c>, the part that decides what an 'idx1' offset is
    /// relative to. Two conventions exist and the chunk is the same either way, so the C does
    /// not guess: it finds the first real chunk in the 'movi' list and derives the offset
    /// from it, which is right for both.
    /// </summary>
    /// <remarks>
    /// What was here guessed, and guessed wrong in both directions. It returned
    /// <c>movieDataOffset</c> - the byte after the 'movi' four-character code - where the
    /// convention is relative to the code itself, four bytes earlier, and then
    /// <see cref="ReadPacketFromIndex"/> treated the result as the payload position when an
    /// index offset points at the chunk header. The two errors left every payload position
    /// four bytes into the chunk header, on its size field. PCM did not care, nothing else in
    /// an AVI ever reached a decoder, and so no test had ever seen it.
    /// </remarks>
    private long DetermineIdx1OffsetBase(int entryCount, Span<byte> entryBuffer)
    {
        if (entryCount == 0)
            return 0;

        // "avio_seek(pb, avi->movi_list + 4, SEEK_SET); if (avi_sync(s, 1) == 0)
        //  first_packet_pos = avio_tell(pb) - 8;" - movi_list is the position of the 'movi'
        // code, so movi_list + 4 is where the first chunk header starts.
        long moviList = movieDataOffset - 4;
        long firstPacketPos = FindFirstMoviChunk(moviList + 4);
        if (firstPacketPos <= 0)
            return 0;

        long savedPos = inputStream.Position;
        uint firstOffset = 0;
        bool found = false;

        for (int i = 0; i < entryCount; i++)
        {
            if (inputStream.Read(entryBuffer) < 16)
                break;

            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer[..4]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer.Slice(12, 4));

            // The C skips an entry whose stream index is out of range and an 'xxpc' palette
            // change before it looks at the first packet, so this does too.
            if (GetTrackIndexFromChunkId(tag) < 0)
                continue;
            if (((tag >> 16) & 0xFF) == 'p' && ((tag >> 24) & 0xFF) == 'c')
                continue;

            firstOffset = BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer.Slice(8, 4));
            found = true;
            break;
        }

        inputStream.Position = savedPos;
        if (!found)
            return 0;

        // "if (avi->movi_list + 4 != pos || pos + 500 > first_packet_pos)
        //      data_offset = first_packet_pos - pos;"
        if (moviList + 4 != firstOffset || firstOffset + 500 > firstPacketPos)
            return firstPacketPos - firstOffset;

        return 0;
    }

    /// <summary>
    /// avidec.c's <c>avi_sync</c>, reduced to the one thing <c>avi_read_idx1</c> wants from
    /// it: the position of the first chunk header in the 'movi' list that belongs to a track.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE: avi_sync resynchronises on damaged input, walking forward a byte at a time
    /// past garbage and handling 'LIST'/'RIFF'/'JUNK' and the ix.. indexes. This reads the
    /// chunk headers in order and stops at the first one whose code names a track, which is
    /// what a well-formed file gives either way. A file that needs the resynchroniser falls
    /// back to the sequential reader, which does not use the index at all.
    /// </remarks>
    private long FindFirstMoviChunk(long from)
    {
        long savedPos = inputStream.Position;
        long result = -1;
        inputStream.Position = from;
        long limit = movieDataOffset + movieDataSize;

        Span<byte> header = stackalloc byte[8];
        while (inputStream.Position + 8 <= limit)
        {
            long at = inputStream.Position;
            if (inputStream.Read(header) < 8)
                break;

            uint chunkId = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));

            if (GetTrackIndexFromChunkId(chunkId) >= 0)
            {
                result = at;
                break;
            }

            // A 'LIST' inside 'movi' - 'rec ' interleaving - has its four-character type next,
            // and its children follow, so only the type is stepped over.
            if (chunkId == AviFourCC.List)
            {
                SkipBytes(4);
                continue;
            }

            SkipBytes(chunkSize + (chunkSize & 1));
        }

        inputStream.Position = savedPos;
        return result;
    }

    private void ParseList(uint listType, uint size)
    {
        // A LIST whose size is zero runs to the end of the file. That is not damage: every
        // LIST in an AMV file is written with a zero size, and so is its RIFF header, and
        // avidec.c copes because its header walk is FLAT - a LIST's size is used only to
        // compute an end marker and the tags inside it are read by the same loop. This
        // demuxer nests by size instead, so the zero has to be turned into a real length
        // here or the walk desynchronises and finds one stream instead of two.
        long remaining = inputStream.Length - inputStream.Position;
        if (size == 0 || size > remaining)
            size = (uint)Math.Max(remaining, 0);

        long endPosition = inputStream.Position + size;

        switch (listType)
        {
            case AviFourCC.Hdrl:
                ParseHeaderList(endPosition);
                break;

            case AviFourCC.Strl:
                ParseStreamList(endPosition);
                break;

            case AviFourCC.Movi:
                movieDataOffset = inputStream.Position;
                movieDataSize = size;
                SkipBytes(size);
                break;

            default:
                SkipBytes(size);
                break;
        }
    }

    private void ParseHeaderList(long endPosition)
    {
        while (inputStream.Position < endPosition)
        {
            if (!TryReadChunkHeader(out var chunkId, out var chunkSize))
                break;

            switch (chunkId)
            {
                case AviFourCC.Amvh:
                    // The C falls THROUGH from 'amvh' into 'avih': the header has the same
                    // layout, it is only the tag that differs.
                    isAmvFileFormat = true;
                    ParseMainHeader(chunkSize);
                    break;

                case AviFourCC.Avih:
                    ParseMainHeader(chunkSize);
                    break;

                case AviFourCC.List:
                    uint listType = ReadFourCC();
                    ParseList(listType, chunkSize >= 4 ? chunkSize - 4 : 0);
                    if (listType == AviFourCC.Movi)
                        return;     // the C's `goto end_of_header`
                    break;

                default:
                    SkipBytes(chunkSize);
                    break;
            }

            if ((chunkSize & 1) != 0)
                SkipBytes(1);
        }
    }

    private void ParseMainHeader(uint size)
    {
        Span<byte> buffer = stackalloc byte[56];
        int bytesRead = inputStream.Read(buffer[..(int)Math.Min(size, 56)]);

        if (bytesRead >= 24)
        {
            microsecondsPerFrame = BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]);
            // Skip max_bytes_per_sec, padding_granularity, flags
            totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(16, 4));
        }

        // dwWidth and dwHeight. An ordinary AVI's are advisory and avidec.c ignores them -
        // the BITMAPINFOHEADER in each stream's format chunk is what counts - but an AMV
        // video stream has no BITMAPINFOHEADER, so these are the only picture size there is.
        if (bytesRead >= 40)
        {
            avihWidth = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(32, 4));
            avihHeight = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(36, 4));
        }

        if (size > bytesRead)
            SkipBytes(size - bytesRead);
    }

    private void ParseStreamList(long endPosition)
    {
        var track = new AviTrack { TrackIndex = tracks.Count };

        while (inputStream.Position < endPosition)
        {
            if (!TryReadChunkHeader(out var chunkId, out var chunkSize))
                break;

            switch (chunkId)
            {
                case AviFourCC.Strh:
                    ParseStreamHeader(chunkSize, track);
                    break;

                case AviFourCC.Strf:
                    ParseStreamFormat(chunkSize, track);
                    break;

                case AviFourCC.List:
                    // A 'strl' contains no lists, so this one is the NEXT 'strl' or the
                    // 'movi' - a sibling, reached because this list's declared size was
                    // zero. Give it back to the caller rather than consuming it.
                    inputStream.Position -= 8;
                    endPosition = inputStream.Position;
                    break;

                default:
                    SkipBytes(chunkSize);
                    break;
            }

            if ((chunkSize & 1) != 0)
                SkipBytes(1);
        }

        // avi_read_header creates a stream for every 'strl' it reads a 'strh' out of, and
        // nothing about the handler decides that. This used to test FourCC, which is the
        // handler from the stream header - and which is 0 for a normal audio stream, because
        // an audio stream's codec is in its 'strf' as a WAVE format tag. So every AVI audio
        // track in existence was silently dropped here, and no test had ever looked.
        if (track.HasStreamHeader)
            tracks.Add(track);
    }

    private void ParseStreamHeader(uint size, AviTrack track)
    {
        Span<byte> buffer = stackalloc byte[56];
        int bytesRead = inputStream.Read(buffer[..(int)Math.Min(size, 56)]);

        if (bytesRead >= 48)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]);

            // An AMV file's stream headers do not state their type: stream 0 is the video
            // and stream 1 is the audio, by position. Reading the declared type instead
            // gives both streams whatever happens to be in those four bytes.
            if (isAmvFileFormat)
                type = tracks.Count == 0 ? AviFourCC.Vids : AviFourCC.Auds;

            // fccHandler. avidec.c keeps it as ast->handler and uses it for three special
            // cases, never as the codec: the codec comes from the 'strf' chunk. For video it
            // is usually the same four-character code the BITMAPINFOHEADER carries, which is
            // why reading it as the codec looked right for years.
            track.Handler = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4, 4));
            track.HasStreamHeader = true;

            track.TrackType = type switch
            {
                AviFourCC.Vids => AviTrackType.Video,
                AviFourCC.Auds => AviTrackType.Audio,
                AviFourCC.Txts => AviTrackType.Subtitle,
                _ => AviTrackType.Unknown
            };

            uint scale = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(20, 4));
            uint rate = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(24, 4));

            // "scale/rate is %u/%u which is invalid. (This file has been generated by broken
            // software.)" - and the fallback is the avih's microseconds per frame if it has
            // one, 25 fps otherwise. The pair is the stream's time base, which is the only
            // thing that turns a frame counter into a time.
            if (scale == 0 || rate == 0)
            {
                if (microsecondsPerFrame != 0)
                {
                    rate = 1_000_000;
                    scale = microsecondsPerFrame;
                }
                else
                {
                    rate = 25;
                    scale = 1;
                }
            }

            track.Scale = scale;
            track.Rate = rate;
            track.SampleRate = (double)rate / scale;

            // dwStart - ast->cum_len, the timestamp the stream's first packet carries. "crazy
            // start time, iam scared, giving up" is the C's own check, at an hour.
            long cumulativeLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(28, 4));
            if (cumulativeLength > 3600L * rate / scale)
                cumulativeLength = 0;

            track.SampleCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(32, 4));

            // dwSampleSize, which is how much of a chunk one packet is. "case
            // MKTAG('v','i','d','s'): ast->sample_size = 0;" - a video chunk is always one
            // packet, whatever the header claims.
            int sampleSize = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(44, 4));

            // "ast->cum_len *= FFMAX(1, ast->sample_size)" comes before the switch that zeroes
            // sample_size for video, so the start time of a stream that declares one is in
            // bytes and is converted here, with the raw value - not the zeroed one.
            track.CumulativeLength = cumulativeLength * Math.Max(1, sampleSize);

            if (sampleSize < 0)
            {
                // "Invalid sample_size %d at stream %d setting it to 0"
                sampleSize = 0;
            }
            track.SampleSize = track.TrackType == AviTrackType.Video ? 0 : sampleSize;

            // ast->frame_offset = ast->cum_len. For video it counts frames, for audio with a
            // sample size it counts bytes, and avi_read_packet divides.
            track.FrameOffset = track.CumulativeLength;
        }

        if (size > bytesRead)
            SkipBytes(size - bytesRead);
    }

    private void ParseStreamFormat(uint size, AviTrack track)
    {
        if (isAmvFileFormat && track.TrackType == AviTrackType.Video)
        {
            // An AMV video stream's format chunk is not a BITMAPINFOHEADER, so there is
            // nothing to read out of it: the size is the main header's and the codec is
            // fixed. avidec.c does exactly this - assign, then avio_skip(pb, size).
            track.Width = avihWidth;
            track.Height = avihHeight;
            track.FourCC = AviFourCC.Amvf;
            SkipBytes(size);
            return;
        }

        if (track.TrackType == AviTrackType.Video)
        {
            Span<byte> buffer = stackalloc byte[40];
            int bytesRead = inputStream.Read(buffer[..(int)Math.Min(size, 40)]);

            int signedHeight = 0;

            if (bytesRead >= 40)
            {
                track.Width = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));
                signedHeight = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(8, 4));
                track.Height = Math.Abs(signedHeight);
                track.BitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(14, 2));
                // biCompression, which avidec.c assigns to codec_tag and looks up in
                // ff_codec_bmp_tags.
                track.FourCC = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(16, 4));
            }

            if (size > bytesRead)
            {
                // Read codec extra data
                int extraSize = (int)size - bytesRead;
                track.CodecPrivate = new byte[extraSize];
                inputStream.ReadExactly(track.CodecPrivate);
            }

            // avidec.c APPENDS "BottomUp" to the extradata itself when biCompression is
            // zero and biHeight is positive, and only then does it take the absolute value
            // of the height. That is how an uncompressed BI_RGB AVI - whose rows really are
            // stored bottom row first - reaches rawdec.c's flip, and the sign test is the
            // whole of it: a NEGATIVE biHeight means top-down, so it gets no trailer. Read
            // in the wrong order, as this did until raw video was ported, the sign is gone
            // before anything can look at it and every uncompressed AVI decodes upside
            // down.
            if (track.FourCC == 0 && signedHeight > 0)
            {
                byte[] existing = track.CodecPrivate ?? [];
                var withTrailer = new byte[existing.Length + 9];
                existing.CopyTo(withTrailer, 0);
                "BottomUp"u8.CopyTo(withTrailer.AsSpan(existing.Length, 8));
                withTrailer[^1] = 0;
                track.CodecPrivate = withTrailer;
            }
        }
        else if (track.TrackType == AviTrackType.Audio)
        {
            // avidec.c hands the whole chunk to ff_get_wav_header, which is the only thing
            // that knows what shape it is: WAVEFORMAT, PCMWAVEFORMAT and WAVEFORMATEX are
            // each the previous one with fields added, and WAVEFORMATEXTENSIBLE puts the real
            // codec in a GUID. Reading sixteen bytes and switching on the tag, which is what
            // this did, cannot see any of that.
            var wav = Riff.GetWavHeader(inputStream, (int)size, bigEndian: false);

            track.AudioFormatTag = (ushort)wav.CodecTag;
            track.AudioCodecName = wav.CodecName;
            track.AudioChannels = wav.Channels;
            track.AudioSampleRate = (uint)wav.SampleRate;
            track.AudioBytesPerSecond = (uint)(wav.BitRate / 8);
            track.AudioBlockAlign = (ushort)wav.BlockAlign;
            track.AudioBitsPerSample = wav.BitsPerCodedSample;
            track.CodecPrivate = wav.ExtraData;

            // "st->codecpar->codec_id = AV_CODEC_ID_ADPCM_IMA_AMV" - whatever the format
            // chunk claimed, and avidec.c also clears dshow_block_align for it. Named
            // honestly here even though no decoder for it exists yet: a track labelled with
            // the codec the format chunk happens to name would decode to noise, where one
            // labelled adpcm_ima_amv simply finds no decoder and says so.
            if (isAmvFileFormat)
            {
                track.AudioCodecName = "adpcm_ima_amv";
                track.DshowBlockAlign = 0;
            }

            // "block align variable used to emulate bugs in the faulty files that are out
            // there" - the C's own comment on the field. It is what turns a packet's byte
            // length into a duration for a compressed format that declares no sample size.
            track.DshowBlockAlign = wav.BlockAlign;

            // "sample size (%d) != block align (%d)" - the WAVEFORMAT wins, because it is the
            // one the decoder was configured from.
            if (track.SampleSize != 0 && wav.BlockAlign != 0
                && track.SampleSize != wav.BlockAlign)
            {
                track.SampleSize = wav.BlockAlign;
            }

            // "AVI files with Xan DPCM audio (wrongly) declare PCM audio in the header but
            // have Axan as stream_code_tag."
            if (track.Handler == AviFourCC.Axan)
            {
                track.AudioCodecName = "xan_dpcm";
                track.AudioFormatTag = 0;
                track.DshowBlockAlign = 0;
            }

            // "overriding invalid dshow_block_align of %d" - a block align of four or less on
            // a format whose frames are far larger than that is a lie, and dividing by it
            // would multiply every timestamp.
            if (track.DshowBlockAlign is > 0 and <= 4
                && track.AudioCodecName is "aac" or "ftr" or "flac" or "mp2")
            {
                track.DshowBlockAlign = 0;
            }

            // "overriding sample_size" - a block align equal to the codec's frame length in
            // SAMPLES is not a byte count, so a sample size copied from it is not one either.
            if ((track.AudioCodecName == "aac" && track.DshowBlockAlign == 1024 && track.SampleSize == 1024)
                || (track.AudioCodecName == "aac" && track.DshowBlockAlign == 4096 && track.SampleSize == 4096)
                || (track.AudioCodecName == "mp3" && track.DshowBlockAlign == 1152 && track.SampleSize == 1152))
            {
                track.SampleSize = 0;
            }

            // "2-aligned (fix for Stargate SG-1 - 3x18 - Shades of Grey.avi)" - the C's own
            // comment. GetWavHeader consumed exactly `size`, so only the pad is left.
            if ((size & 1) != 0)
                SkipBytes(1);

            // "AVI files with Xan DPCM audio (wrongly) declare PCM audio in the header but
            // have Axan as stream_code_tag."
            if (track.Handler == AviFourCC.Axan)
            {
                track.AudioCodecName = "xan_dpcm";
                track.AudioFormatTag = 0;
            }
        }
        else
        {
            SkipBytes(size);
        }
    }

    private bool ReadPacketFromIndex(out AviPacket packet)
    {
        packet = default;

        if (currentIndexPosition >= index.Count)
            return false;

        var entry = index[currentIndexPosition++];

        int trackIndex = GetTrackIndexFromChunkId(entry.ChunkId);
        if (trackIndex < 0)
            return ReadPacketFromIndex(out packet);

        // An 'idx1' offset points at the chunk header, not at the payload - which is why
        // avi_read_packet seeks there and then reads the header. The header's own size is the
        // one used, as avi_sync uses it, because the index's copy can disagree with it.
        inputStream.Position = entry.Offset;
        if (!TryReadChunkHeader(out uint chunkId, out uint chunkSize))
            return false;

        if (chunkId != entry.ChunkId)
        {
            // The index does not describe this file. Rather than hand out payloads from the
            // middle of chunk headers, which is what the old arithmetic did silently, the
            // index is abandoned and the rest of the file is read by walking the chunks.
            IndexAbandoned = true;
            index.Clear();
            currentIndexPosition = 0;
            pendingRemaining = 0;
            inputStream.Position = movieDataOffset;
            return ReadNextChunkPacket(out packet);
        }

        // "(st->discard >= AVDISCARD_DEFAULT && size == 0)" - with the default discard
        // setting, which is what a plain decode uses, avi_sync skips a zero-size chunk
        // outright rather than emitting an empty packet. An AVI drop frame is one of these,
        // and this file has 29 of them.
        if (chunkSize == 0)
        {
            // avi_sync advances frame_offset by the skipped chunk's duration before it skips:
            // a drop frame occupies its slot in time even though it carries no bytes. Leaving
            // it out makes every timestamp after the first drop frame one frame early, and
            // this file has 29 of them.
            tracks[trackIndex].FrameOffset += GetDuration(tracks[trackIndex], 0);
            return ReadPacketFromIndex(out packet);
        }

        packet = new AviPacket
        {
            TrackIndex = trackIndex,
            IsKeyframe = entry.IsKeyframe,
            PayloadPosition = inputStream.Position,
            PayloadSize = (int)chunkSize
        };

        return true;
    }

    private bool ReadNextChunkPacket(out AviPacket packet)
    {
        packet = default;

        while (TryReadChunkHeader(out var chunkId, out var chunkSize))
        {
            int trackIndex = GetTrackIndexFromChunkId(chunkId);

            if (trackIndex >= 0 && chunkSize > 0)
            {
                packet = new AviPacket
                {
                    TrackIndex = trackIndex,
                    IsKeyframe = true, // Without index, assume keyframe
                    PayloadPosition = inputStream.Position,
                    PayloadSize = (int)chunkSize
                };

                SkipBytes(chunkSize);
                SkipChunkPadding(chunkSize);

                return true;
            }

            // As in the index path: a chunk that is skipped rather than emitted still
            // advances the stream's clock, which is what avi_sync does before its skip.
            if (trackIndex >= 0)
                tracks[trackIndex].FrameOffset += GetDuration(tracks[trackIndex], (int)chunkSize);

            SkipBytes(chunkSize);
            SkipChunkPadding(chunkSize);
        }

        return false;
    }

    /// <summary>
    /// The pad byte that follows an odd-sized chunk - except in an AMV file, which has none.
    /// </summary>
    /// <remarks>
    /// DIVERGENCE, and a narrow one. avidec.c never assumes alignment in the packet path at
    /// all: <c>avi_sync</c> scans forward byte by byte for something that looks like a chunk
    /// id, so a file that pads and a file that does not both work without the demuxer
    /// knowing which it is. This demuxer walks by size, which is simpler and is conformant
    /// on AVI, so the one format that does not pad gets a test rather than a byte scanner.
    /// AMV chunks really are unaligned: in the FATE sample the first is 3383 bytes at 0x144
    /// and the next begins at 0xE7B, with no pad - so padding anyway lands one byte inside
    /// the next chunk id and every packet after the first is lost.
    /// </remarks>
    private void SkipChunkPadding(uint chunkSize)
    {
        if (!isAmvFileFormat && (chunkSize & 1) != 0)
            SkipBytes(1);
    }

    private int GetTrackIndexFromChunkId(uint chunkId)
    {
        // Chunk IDs are like "00dc", "01wb" where first two chars are stream index
        byte digit1 = (byte)(chunkId & 0xFF);
        byte digit2 = (byte)((chunkId >> 8) & 0xFF);

        if (digit1 >= '0' && digit1 <= '9' && digit2 >= '0' && digit2 <= '9')
        {
            int streamIndex = (digit1 - '0') * 10 + (digit2 - '0');
            if (streamIndex < tracks.Count)
                return streamIndex;
        }

        return -1;
    }

    private bool TryReadChunkHeader(out uint chunkId, out uint chunkSize)
    {
        chunkId = 0;
        chunkSize = 0;

        Span<byte> buffer = stackalloc byte[8];
        if (inputStream.Read(buffer) < 8)
            return false;

        chunkId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]);
        chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4, 4));
        return true;
    }

    private uint ReadFourCC()
    {
        Span<byte> buffer = stackalloc byte[4];
        inputStream.Read(buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private void SkipBytes(long count)
    {
        if (count <= 0)
            return;

        if (inputStream.CanSeek)
        {
            inputStream.Position += count;
        }
        else
        {
            long remaining = count;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, readBuffer.Length);
                int read = inputStream.Read(readBuffer, 0, toRead);
                if (read == 0) break;
                remaining -= read;
            }
        }
    }

    public void Dispose()
    {
        if (isDisposed)
            return;
        isDisposed = true;
        ArrayPool<byte>.Shared.Return(readBuffer);
    }
}

/// <summary>
/// AVI FourCC codes.
/// </summary>
public static class AviFourCC
{
    public const uint Riff = 0x46464952; // 'RIFF'
    public const uint Avi = 0x20495641;  // 'AVI '
    public const uint Amv = 0x20564D41;  // 'AMV ' - the RIFF form type of an AMV file
    public const uint Amvh = 0x68766D61; // 'amvh' - its main header, where AVI has 'avih'
    public const uint Amvf = 0x46564D41; // 'AMVF' - the AMV video codec tag
    public const uint List = 0x5453494C; // 'LIST'
    public const uint Hdrl = 0x6C726468; // 'hdrl'
    public const uint Avih = 0x68697661; // 'avih'
    public const uint Strl = 0x6C727473; // 'strl'
    public const uint Strh = 0x68727473; // 'strh'
    public const uint Strf = 0x66727473; // 'strf'
    public const uint Movi = 0x69766F6D; // 'movi'
    public const uint Idx1 = 0x31786469; // 'idx1'
    public const uint Vids = 0x73646976; // 'vids'
    public const uint Auds = 0x73647561; // 'auds'
    public const uint Axan = 0x6E617841; // 'Axan' (Xan DPCM mislabelled as PCM)
    public const uint Txts = 0x73747874; // 'txts'

    public static string ToString(uint fourCC)
    {
        Span<char> chars = stackalloc char[4];
        chars[0] = (char)(fourCC & 0xFF);
        chars[1] = (char)((fourCC >> 8) & 0xFF);
        chars[2] = (char)((fourCC >> 16) & 0xFF);
        chars[3] = (char)((fourCC >> 24) & 0xFF);
        return new string(chars);
    }
}

/// <summary>
/// AVI track type.
/// </summary>
public enum AviTrackType
{
    Unknown,
    Video,
    Audio,
    Subtitle
}

/// <summary>
/// AVI track information.
/// </summary>
public class AviTrack
{
    public int TrackIndex { get; set; }
    public AviTrackType TrackType { get; set; }
    public uint FourCC { get; set; }
    public double SampleRate { get; set; }

    /// <summary><c>ast->scale</c> - dwScale, the numerator of the stream's time base.</summary>
    public uint Scale { get; set; }

    /// <summary><c>ast->rate</c> - dwRate, the denominator of the stream's time base.</summary>
    public uint Rate { get; set; }

    /// <summary>
    /// <c>ast->cum_len</c> - dwStart, multiplied by the sample size for a stream that declares
    /// one, which is where this stream's timestamps begin.
    /// </summary>
    public long CumulativeLength { get; set; }

    /// <summary>
    /// <c>ast->frame_offset</c> - the running frame counter for video, byte counter for audio
    /// with a sample size. It is the timestamp of the next packet this stream yields.
    /// </summary>
    public long FrameOffset { get; set; }

    /// <summary>
    /// <c>ast->dshow_block_align</c> - "block align variable used to emulate bugs in the faulty
    /// files that are out there", and the divisor that turns a compressed packet's length into
    /// a duration when the stream declares no sample size.
    /// </summary>
    public int DshowBlockAlign { get; set; }
    public uint SampleCount { get; set; }

    // Video
    public int Width { get; set; }
    public int Height { get; set; }
    public int BitsPerPixel { get; set; }

    // Audio
    /// <summary>
    /// fccHandler from the stream header. avidec.c keeps this as <c>ast->handler</c> and uses
    /// it only for the three cases that need it - DV, Xvid's codec tag and Xan DPCM - never as
    /// the codec.
    /// </summary>
    public uint Handler { get; set; }

    /// <summary>
    /// True once a 'strh' has been read. avi_read_header creates a stream for every 'strl'
    /// that has one, whatever its handler says.
    /// </summary>
    public bool HasStreamHeader { get; set; }

    /// <summary>
    /// dwSampleSize from the stream header - <c>ast->sample_size</c> - which decides how much
    /// of a chunk one packet is. Zero for video, and for a compressed audio format whose
    /// frames are not a fixed size.
    /// </summary>
    public int SampleSize { get; set; }

    public ushort AudioFormatTag { get; set; }

    /// <summary>
    /// The codec ff_get_wav_header resolved from this track's WAVEFORMAT, or null when the
    /// format tag is in no table. A WAVEFORMATEXTENSIBLE's real codec is in a GUID, so the
    /// format tag alone cannot be asked for it.
    /// </summary>
    public string? AudioCodecName { get; set; }
    public int AudioChannels { get; set; }
    public uint AudioSampleRate { get; set; }
    public uint AudioBytesPerSecond { get; set; }
    public ushort AudioBlockAlign { get; set; }
    public int AudioBitsPerSample { get; set; }

    public byte[]? CodecPrivate { get; set; }

    /// <summary>
    /// The codec FFmpeg names, which is what the registry is keyed by and what ffprobe
    /// prints. A codec with no decoder here still gets its real name, so the track is listed
    /// and skipped rather than decoded as something it is not.
    /// </summary>
    /// <remarks>
    /// avidec.c's 'strf' arms: video is <c>ff_codec_bmp_tags</c>, then the mov video tags,
    /// then the unofficial ones; audio is whatever <c>ff_get_wav_header</c> resolved. The mov
    /// pass is the one divergence - this tree's mov tags are a switch inside
    /// <see cref="Mp4Track"/> rather than a table, so a mov tag in an AVI is not looked up.
    /// avidec.c logs a warning when it hits one, which says how rare it is.
    /// </remarks>
    public string GetCodecName()
    {
        if (TrackType == AviTrackType.Video)
        {
            string? id = RiffTags.CodecGetId(RiffTags.CodecBmpTags, FourCC)
                      ?? RiffTags.CodecGetId(RiffTags.CodecBmpTagsUnofficial, FourCC);

            // "if (st->codecpar->codec_id == AV_CODEC_ID_MPEG4 && ast->handler ==
            // MKTAG('X','V','I','D')) st->codecpar->codec_tag = MKTAG('X','V','I','D');"
            // - the C rewrites the tag, not the codec, so the name is "mpeg4" either way.

            // A four-character code in no table keeps itself, lower-cased the way the old
            // code did, so the track is named something traceable rather than "unknown".
            return id ?? AviFourCC.ToString(FourCC).ToLowerInvariant().Trim('\0');
        }

        if (TrackType == AviTrackType.Audio)
        {
            return AudioCodecName ?? $"0x{AudioFormatTag:X4}";
        }

        return "unknown";
    }
}

/// <summary>
/// AVI index entry.
/// </summary>
public struct AviIndexEntry
{
    public uint ChunkId;
    public bool IsKeyframe;
    public long Offset;
    public int Size;
    public int TrackIndex;
    public int FrameNumber;
}

/// <summary>
/// AVI packet for output.
/// </summary>
public struct AviPacket
{
    public int TrackIndex;
    public bool IsKeyframe;
    public long PayloadPosition;
    public int PayloadSize;

    /// <summary>
    /// <c>pkt->dts</c>, in the stream's own time base. AVI carries no presentation timestamps
    /// at all, so there is no pts to go with it.
    /// </summary>
    public long DecodeTimestamp;

    /// <summary><see cref="DecodeTimestamp"/> in .NET ticks.</summary>
    public long DecodeTimestampTicks;
}

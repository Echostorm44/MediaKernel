using System.Buffers;
using System.Buffers.Binary;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// High-performance Matroska (MKV/WebM) demuxer.
/// Parses container structure and extracts media packets for decoders.
/// </summary>
/// <remarks>
/// This is a stream-forward parser that minimizes seeking and memory allocation.
/// Uses Span&lt;T&gt; for zero-copy parsing of clusters and blocks.
/// </remarks>
public sealed class MatroskaDemuxer : IDisposable
{
    private readonly Stream inputStream;
    private readonly byte[] readBuffer;
    private readonly List<MatroskaTrack> tracks = new();

    /// <summary>
    /// Track numbers dropped because they use a compression algorithm that is not implemented.
    /// </summary>
    /// <remarks>
    /// Kept rather than forgotten so <see cref="UnsupportedCompressionTracks"/> can say why a
    /// track the file advertises is not in <see cref="Tracks"/>.
    /// </remarks>
    private readonly List<int> unsupportedCompressionTracks = new();
    private readonly List<SeekEntry> seekIndex = new();
    private readonly Queue<MatroskaPacket> pendingPackets = new();

    /// <summary>
    /// Where the SeekHead says each top-level element lives, as an absolute file position.
    /// <c>MatroskaDemuxContext.seekhead</c>.
    /// </summary>
    private readonly Dictionary<uint, long> seekHeadPositions = new();

    /// <summary>Whether the Cues the SeekHead points at have been fetched already.</summary>
    private bool cuesParsed;

    // Where restored frames are built for a header-stripped track. One buffer per demuxer, laid
    // out so that every packet from a single block gets its own non-overlapping window: a laced
    // block produces several packets at once and they are drained before the next block is read,
    // so they all have to stay valid together. Sized up front for the whole block, because
    // growing it part way through would leave the windows already handed out pointing at the
    // old array.
    private byte[] restoreBuffer = new byte[8192];
    private int restoreUsed;
    private readonly ChapterList chapters = new();
    
    // Dynamic seek index - built on-the-fly from keyframes during playback
    private readonly List<SeekEntry> dynamicSeekIndex = new();
    private readonly HashSet<long> dynamicIndexTimestamps = new(); // Prevents duplicates
    
    private long segmentStartPosition;
    private long segmentDataSize;
    private ulong timestampScale = 1_000_000; // Default: 1ms
    private double durationMs;
    private string? title;
    private string? muxingApp;
    private string? writingApp;
    private long currentClusterTimestamp;
    private long currentClusterPosition; // For dynamic index building
    private bool isInitialized;
    private bool isDisposed;
    private bool buildDynamicIndex = true; // Enable dynamic index by default

    private const int DefaultBufferSize = 64 * 1024; // 64KB read buffer

    public MatroskaDemuxer(Stream stream)
    {
        inputStream = stream ?? throw new ArgumentNullException(nameof(stream));
        readBuffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
    }

    /// <summary>Gets all discovered tracks after initialization.</summary>
    public IReadOnlyList<MatroskaTrack> Tracks => tracks;

    /// <summary>
    /// Track numbers present in the file but dropped for using unimplemented compression.
    /// </summary>
    public IReadOnlyList<int> UnsupportedCompressionTracks => unsupportedCompressionTracks;

    /// <summary>Gets the seek index (cue points) for random access.</summary>
    public IReadOnlyList<SeekEntry> SeekIndex => seekIndex;
    
    /// <summary>Gets the dynamically built seek index from keyframes encountered during playback.</summary>
    public IReadOnlyList<SeekEntry> DynamicSeekIndex => dynamicSeekIndex;
    
    /// <summary>
    /// Gets or sets whether to build a dynamic seek index from keyframes during playback.
    /// Useful for files without Cues elements.
    /// </summary>
    public bool BuildDynamicIndex
    {
        get => buildDynamicIndex;
        set => buildDynamicIndex = value;
    }
    
    /// <summary>
    /// Gets a SeekIndex for use with SeekCoordinator.
    /// Returns the cue-based index if available, otherwise the dynamic index.
    /// </summary>
    public MediaKernel.Core.Demuxers.SeekIndex GetSeekIndex()
    {
        // Prefer cue-based index if available
        if (seekIndex.Count > 0)
        {
            return MediaKernel.Core.Demuxers.SeekIndex.FromLegacyEntries(seekIndex);
        }
        
        // Fall back to dynamic index
        return MediaKernel.Core.Demuxers.SeekIndex.FromLegacyEntries(dynamicSeekIndex);
    }

    /// <summary>Gets the timestamp scale in nanoseconds per unit.</summary>
    public ulong TimestampScale => timestampScale;

    /// <summary>Gets the total duration in milliseconds, if known.</summary>
    public double DurationMilliseconds => durationMs;

    /// <summary>Gets the title from segment info.</summary>
    public string? Title => title;

    /// <summary>Gets the chapter list for navigation.</summary>
    public ChapterList Chapters => chapters;

    /// <summary>
    /// Initializes the demuxer by parsing the EBML header, segment info, and tracks.
    /// Must be called before reading packets.
    /// </summary>
    public bool Initialize()
    {
        if (isInitialized)
            return true;

        if (!ParseEbmlHeader())
            return false;

        if (!FindAndParseSegment())
            return false;

        isInitialized = true;
        return tracks.Count > 0;
    }

    /// <summary>
    /// Reads the next media packet from the current position.
    /// </summary>
    /// <param name="packet">The parsed packet info (does not include payload data).</param>
    /// <returns>True if a packet was read, false if end of stream.</returns>
    public bool ReadNextPacket(out MatroskaPacket packet)
    {
        packet = default;

        if (!isInitialized)
            throw new InvalidOperationException("Demuxer not initialized. Call Initialize() first.");

        // Return any pending packets from laced blocks first
        if (pendingPackets.TryDequeue(out packet))
            return true;

        while (true)
        {
            if (!TryReadElementHeader(out var header))
                return false;

            switch (header.ElementId)
            {
                case MatroskaElementId.Cluster:
                    // Track cluster position for dynamic index building
                    currentClusterPosition = inputStream.Position - header.HeaderSize;
                    continue;

                case MatroskaElementId.Timestamp:
                    currentClusterTimestamp = (long)ReadUnsignedInteger((int)header.DataSize);
                    continue;

                case MatroskaElementId.SimpleBlock:
                    if (ParseSimpleBlock(header.DataSize, out packet))
                    {
                        AddToDynamicIndexIfKeyframe(packet);
                        return true;
                    }
                    continue;

                case MatroskaElementId.BlockGroup:
                    if (ParseBlockGroup(header.DataSize, out packet))
                    {
                        AddToDynamicIndexIfKeyframe(packet);
                        return true;
                    }
                    continue;

                case MatroskaElementId.Cues:
                    ParseCues(header.DataSize);
                    continue;

                default:
                    // Skip unknown elements
                    SkipBytes((long)header.DataSize);
                    continue;
            }
        }
    }
    
    /// <summary>
    /// Adds a keyframe to the dynamic seek index if indexing is enabled.
    /// Only tracks video keyframes to keep the index size reasonable.
    /// </summary>
    private void AddToDynamicIndexIfKeyframe(in MatroskaPacket packet)
    {
        if (!buildDynamicIndex || !packet.IsKeyframe)
            return;

        // The dynamic index is a fallback for a file with no cues - Seek uses it only when
        // seekIndex is empty. Building it anyway cost two List appends and a linear Contains
        // scan on every video keyframe of a cued file, which is both the only remaining
        // allocation in this demuxer's read path and a scan that grows with the file.
        if (seekIndex.Count > 0)
            return;
        
        // Only index video tracks (track type 1)
        var trackNumber = packet.TrackNumber;
        MatroskaTrack? track = null;
        foreach (var t in tracks)
        {
            if (t.TrackNumber == trackNumber)
            {
                track = t;
                break;
            }
        }
        
        if (track == null || track.TrackType != MatroskaTrackType.Video)
            return;
        
        // Avoid duplicate entries for the same timestamp
        if (dynamicIndexTimestamps.Contains(packet.TimestampTicks))
            return;
        
        dynamicIndexTimestamps.Add(packet.TimestampTicks);
        dynamicSeekIndex.Add(new SeekEntry(packet.TimestampTicks, currentClusterPosition));
    }

    /// <summary>
    /// Seeks to the nearest keyframe before the specified timestamp.
    /// Uses cue-based index if available, otherwise dynamic index.
    /// </summary>
    /// <param name="timestampMs">Target timestamp in milliseconds.</param>
    /// <returns>True if seek succeeded.</returns>
    public bool Seek(double timestampMs)
    {
        // The cues normally sit at the end of the file behind a SeekHead pointer, so the index
        // is empty until they are fetched. This is the moment they are needed.
        if (seekIndex.Count == 0)
        {
            ParseCuesFromSeekHead();
        }

        // Use cue index if available, otherwise try dynamic index
        var indexToUse = seekIndex.Count > 0 ? seekIndex : dynamicSeekIndex;
        
        if (indexToUse.Count == 0)
            return false;

        // Clear any pending packets from laced blocks
        pendingPackets.Clear();

        long targetTicks = (long)(timestampMs * TimeSpan.TicksPerMillisecond);
        
        // Binary search for nearest keyframe
        int low = 0;
        int high = indexToUse.Count - 1;
        long bestPosition = segmentStartPosition;

        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            if (indexToUse[mid].TimestampTicks <= targetTicks)
            {
                bestPosition = indexToUse[mid].FilePosition;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        inputStream.Position = bestPosition;
        return true;
    }

    private bool ParseEbmlHeader()
    {
        if (!TryReadElementHeader(out var header))
            return false;

        if (header.ElementId != MatroskaElementId.EbmlHeader)
            return false;

        // Read and validate EBML header
        long headerEnd = inputStream.Position + (long)header.DataSize;
        string? docType = null;

        while (inputStream.Position < headerEnd)
        {
            if (!TryReadElementHeader(out var child))
                break;

            switch (child.ElementId)
            {
                case MatroskaElementId.DocType:
                    docType = ReadString((int)child.DataSize);
                    break;
                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }

        // Validate DocType
        return docType == "matroska" || docType == "webm";
    }

    private bool FindAndParseSegment()
    {
        if (!TryReadElementHeader(out var header))
            return false;

        if (header.ElementId != MatroskaElementId.Segment)
            return false;

        segmentStartPosition = inputStream.Position;
        segmentDataSize = (long)header.DataSize;

        // Parse segment children until we have tracks
        long segmentEnd = header.IsUnknownSize 
            ? long.MaxValue 
            : segmentStartPosition + segmentDataSize;

        while (inputStream.Position < segmentEnd)
        {
            if (!TryReadElementHeader(out var child))
                break;

            switch (child.ElementId)
            {
                case MatroskaElementId.SeekHead:
                    ParseSeekHead(child.DataSize);
                    break;

                case MatroskaElementId.SegmentInfo:
                    ParseSegmentInfo(child.DataSize);
                    break;

                case MatroskaElementId.Tracks:
                    ParseTracks(child.DataSize);
                    // Once we have tracks, we're ready to demux
                    return true;

                case MatroskaElementId.Cues:
                    ParseCues(child.DataSize);
                    break;

                case MatroskaElementId.Chapters:
                    ParseChapters(child.DataSize);
                    break;

                case MatroskaElementId.Cluster:
                    // Reached first cluster - tracks should already be parsed
                    // Rewind so ReadNextPacket can process it
                    inputStream.Position -= child.HeaderSize;
                    return tracks.Count > 0;

                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }

        return tracks.Count > 0;
    }

    private void ParseSeekHead(ulong size)
    {
        long endPosition = inputStream.Position + (long)size;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            if (child.ElementId == MatroskaElementId.Seek)
            {
                ParseSeekEntry(child.DataSize);
            }
            else
            {
                SkipBytes((long)child.DataSize);
            }
        }
    }

    private void ParseSeekEntry(ulong size)
    {
        long endPosition = inputStream.Position + (long)size;
        uint seekId = 0;
        ulong seekPosition = 0;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            switch (child.ElementId)
            {
                case MatroskaElementId.SeekId:
                    seekId = (uint)ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.SeekPosition:
                    seekPosition = ReadUnsignedInteger((int)child.DataSize);
                    break;
                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }

        // "pos = seekheads[i].pos + matroska->segment_start" - the position a SeekHead states
        // is relative to the start of the segment, not to the start of the file.
        if (seekId != 0 && seekPosition > 0)
        {
            seekHeadPositions[seekId] = segmentStartPosition + (long)seekPosition;
        }
    }

    /// <summary>
    /// <c>matroska_parse_cues</c>: reads the Cues element the SeekHead points at, once, when
    /// the index is first needed.
    /// </summary>
    /// <remarks>
    /// WHAT THIS FIXES. Every muxer - mkvmerge, ffmpeg - writes the Cues at the END of the
    /// file and puts a pointer to them in the SeekHead at the front. This demuxer read those
    /// pointers and discarded them ("Store for later random access" was the whole of it), and
    /// stopped scanning the segment as soon as it had the Tracks element, so the Cues were
    /// never reached: <see cref="Seek"/> found an empty index, returned false, and
    /// <c>DecodePipeline</c> went on to decode the file from the beginning and throw the
    /// result away. Seeking ten minutes into a 44-minute film took <b>4 minutes 36 seconds</b>
    /// and discarded 15,003 decoded frames, which from the outside looks exactly like a seek
    /// that does nothing.
    /// <para>
    /// Deferred rather than done at open time, as <c>matroska_execute_seekhead</c> defers it
    /// ("defer cues parsing until we actually need cue data"): a file that is played straight
    /// through never pays for the jump to the end and back.
    /// </para>
    /// </remarks>
    private void ParseCuesFromSeekHead()
    {
        if (cuesParsed)
            return;

        cuesParsed = true;

        if (!seekHeadPositions.TryGetValue(MatroskaElementId.Cues, out long position))
            return;

        if (inputStream is null || !inputStream.CanSeek
            || position <= 0 || position >= inputStream.Length)
        {
            return;
        }

        // "int64_t before_pos = avio_tell(...)" ... "matroska_reset_status(..., before_pos)":
        // the probe puts the stream back where it found it.
        long restore = inputStream.Position;

        try
        {
            inputStream.Position = position;

            if (TryReadElementHeader(out var header) && header.ElementId == MatroskaElementId.Cues)
            {
                ParseCues(header.DataSize);
            }
        }
        finally
        {
            inputStream.Position = restore;
        }
    }

    private void ParseSegmentInfo(ulong size)
    {
        long endPosition = inputStream.Position + (long)size;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            switch (child.ElementId)
            {
                case MatroskaElementId.TimestampScale:
                    timestampScale = ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.Duration:
                    double rawDuration = ReadFloat((int)child.DataSize);
                    durationMs = rawDuration * timestampScale / 1_000_000.0;
                    break;
                case MatroskaElementId.Title:
                    title = ReadString((int)child.DataSize);
                    break;
                case MatroskaElementId.MuxingApp:
                    muxingApp = ReadString((int)child.DataSize);
                    break;
                case MatroskaElementId.WritingApp:
                    writingApp = ReadString((int)child.DataSize);
                    break;
                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }
    }

    private void ParseTracks(ulong size)
    {
        long endPosition = inputStream.Position + (long)size;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            if (child.ElementId == MatroskaElementId.TrackEntry)
            {
                var track = ParseTrackEntry(child.DataSize);
                if (track != null)
                {
                    // A track compressed with anything other than header stripping carries
                    // compressed data where a decoder expects a frame. zlib, bzlib and lzo1x are
                    // not implemented, and feeding one of them to a decoder would produce noise
                    // that looks like a decoder bug, so the track is dropped and the file plays
                    // without it rather than with it wrong.
                    if (track.HasUnsupportedCompression)
                        unsupportedCompressionTracks.Add(track.TrackNumber);
                    else
                        tracks.Add(track);
                }
            }
            else
            {
                SkipBytes((long)child.DataSize);
            }
        }
    }

    private MatroskaTrack? ParseTrackEntry(ulong size)
    {
        long endPosition = inputStream.Position + (long)size;
        var track = new MatroskaTrack();

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            switch (child.ElementId)
            {
                case MatroskaElementId.TrackNumber:
                    track.TrackNumber = (int)ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.TrackUid:
                    track.TrackUid = ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.TrackType:
                    track.TrackType = (MatroskaTrackType)ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.FlagEnabled:
                    track.IsEnabled = ReadUnsignedInteger((int)child.DataSize) != 0;
                    break;
                case MatroskaElementId.FlagDefault:
                    track.IsDefault = ReadUnsignedInteger((int)child.DataSize) != 0;
                    break;
                case MatroskaElementId.DefaultDuration:
                    track.DefaultDurationNs = ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.Name:
                    track.Name = ReadString((int)child.DataSize);
                    break;
                case MatroskaElementId.Language:
                    track.Language = ReadString((int)child.DataSize);
                    break;
                case MatroskaElementId.CodecId:
                    track.CodecId = ReadString((int)child.DataSize);
                    break;
                case MatroskaElementId.CodecPrivate:
                    track.CodecPrivate = ReadBytes((int)child.DataSize);
                    break;
                case MatroskaElementId.Video:
                    ParseVideoSettings(child.DataSize, track);
                    break;
                case MatroskaElementId.Audio:
                    ParseAudioSettings(child.DataSize, track);
                    break;
                case MatroskaElementId.ContentEncodings:
                    ParseContentEncodings(child.DataSize, track);
                    break;
                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }

        return track.TrackNumber > 0 ? track : null;
    }

    /// <summary>
    /// Parse ContentEncodings, which is where header stripping is declared.
    /// </summary>
    /// <remarks>
    /// Only the compression entry is of interest, and only algorithm 3 (header stripping) is
    /// supported; the settings blob is the prefix to restore. Several encodings may be declared
    /// and are meant to be applied in ContentEncodingOrder, but every file in the wild that uses
    /// this declares exactly one, so a second compression entry is treated as unsupported rather
    /// than silently ignored.
    /// </remarks>
    private void ParseContentEncodings(ulong size, MatroskaTrack track)
    {
        long endPosition = inputStream.Position + (long)size;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            if (child.ElementId == MatroskaElementId.ContentEncoding)
                ParseContentEncoding(child.DataSize, track);
            else
                SkipBytes((long)child.DataSize);
        }
    }

    private void ParseContentEncoding(ulong size, MatroskaTrack track)
    {
        long endPosition = inputStream.Position + (long)size;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            if (child.ElementId == MatroskaElementId.ContentCompression)
                ParseContentCompression(child.DataSize, track);
            else
                SkipBytes((long)child.DataSize);
        }
    }

    private void ParseContentCompression(ulong size, MatroskaTrack track)
    {
        long endPosition = inputStream.Position + (long)size;

        // ContentCompAlgo defaults to 0 (zlib) when absent, per the specification - so a
        // compression element with no algorithm is zlib and not header stripping.
        long algorithm = 0;
        byte[]? settings = null;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            switch (child.ElementId)
            {
                case MatroskaElementId.ContentCompAlgo:
                    algorithm = (long)ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.ContentCompSettings:
                    settings = ReadBytes((int)child.DataSize);
                    break;
                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }

        const long HeaderStripping = 3;
        if (algorithm == HeaderStripping)
        {
            // A header-stripping entry with no settings strips nothing, which is legal and
            // means the frames are already whole.
            if (track.HeaderStripBytes is not null)
                track.HasUnsupportedCompression = true;
            else if (settings is { Length: > 0 })
                track.HeaderStripBytes = settings;
        }
        else
        {
            track.HasUnsupportedCompression = true;
        }
    }

    private void ParseVideoSettings(ulong size, MatroskaTrack track)
    {
        long endPosition = inputStream.Position + (long)size;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            switch (child.ElementId)
            {
                case MatroskaElementId.PixelWidth:
                    track.VideoWidth = (int)ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.PixelHeight:
                    track.VideoHeight = (int)ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.DisplayWidth:
                    track.DisplayWidth = (int)ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.DisplayHeight:
                    track.DisplayHeight = (int)ReadUnsignedInteger((int)child.DataSize);
                    break;
                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }
    }

    private void ParseAudioSettings(ulong size, MatroskaTrack track)
    {
        long endPosition = inputStream.Position + (long)size;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            switch (child.ElementId)
            {
                case MatroskaElementId.SamplingFrequency:
                    track.AudioSampleRate = ReadFloat((int)child.DataSize);
                    break;
                case MatroskaElementId.Channels:
                    track.AudioChannels = (int)ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.BitDepth:
                    track.AudioBitDepth = (int)ReadUnsignedInteger((int)child.DataSize);
                    break;
                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }
    }

    private void ParseCues(ulong size)
    {
        long endPosition = inputStream.Position + (long)size;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            if (child.ElementId == MatroskaElementId.CuePoint)
            {
                ParseCuePoint(child.DataSize);
            }
            else
            {
                SkipBytes((long)child.DataSize);
            }
        }
    }

    private void ParseCuePoint(ulong size)
    {
        long endPosition = inputStream.Position + (long)size;
        long cueTime = 0;
        long clusterPosition = 0;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            switch (child.ElementId)
            {
                case MatroskaElementId.CueTime:
                    cueTime = (long)ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.CueTrackPositions:
                    clusterPosition = ParseCueTrackPositions(child.DataSize);
                    break;
                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }

        if (clusterPosition > 0)
        {
            // Convert timestamp to ticks (timestampScale is in nanoseconds, 100ns per tick)
            long timestampTicks = (long)((ulong)cueTime * timestampScale / 100);
            long filePosition = segmentStartPosition + clusterPosition;
            seekIndex.Add(new SeekEntry(timestampTicks, filePosition));
        }
    }

    private long ParseCueTrackPositions(ulong size)
    {
        long endPosition = inputStream.Position + (long)size;
        long clusterPosition = 0;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            if (child.ElementId == MatroskaElementId.CueClusterPosition)
            {
                clusterPosition = (long)ReadUnsignedInteger((int)child.DataSize);
            }
            else
            {
                SkipBytes((long)child.DataSize);
            }
        }

        return clusterPosition;
    }

    private void ParseChapters(ulong size)
    {
        long endPosition = inputStream.Position + (long)size;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            if (child.ElementId == MatroskaElementId.EditionEntry)
            {
                var edition = ParseEditionEntry(child.DataSize);
                if (edition != null)
                    chapters.AddEdition(edition);
            }
            else
            {
                SkipBytes((long)child.DataSize);
            }
        }
    }

    private ChapterEdition? ParseEditionEntry(ulong size)
    {
        long endPosition = inputStream.Position + (long)size;
        ulong editionUid = 0;
        bool isHidden = false;
        bool isDefault = false;
        bool isOrdered = false;
        var chapterList = new List<Chapter>();

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            switch (child.ElementId)
            {
                case MatroskaElementId.EditionUid:
                    editionUid = ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.EditionFlagHidden:
                    isHidden = ReadUnsignedInteger((int)child.DataSize) != 0;
                    break;
                case MatroskaElementId.EditionFlagDefault:
                    isDefault = ReadUnsignedInteger((int)child.DataSize) != 0;
                    break;
                case MatroskaElementId.EditionFlagOrdered:
                    isOrdered = ReadUnsignedInteger((int)child.DataSize) != 0;
                    break;
                case MatroskaElementId.ChapterAtom:
                    var chapter = ParseChapterAtom(child.DataSize);
                    if (chapter != null)
                        chapterList.Add(chapter);
                    break;
                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }

        if (chapterList.Count == 0)
            return null;

        return new ChapterEdition
        {
            Id = editionUid,
            IsHidden = isHidden,
            IsDefault = isDefault,
            IsOrdered = isOrdered,
            Chapters = chapterList
        };
    }

    private Chapter? ParseChapterAtom(ulong size)
    {
        long endPosition = inputStream.Position + (long)size;
        ulong chapterUid = 0;
        ulong timeStartNs = 0;
        ulong timeEndNs = 0;
        bool isHidden = false;
        bool isEnabled = true;
        string chapterTitle = string.Empty;
        string chapterLanguage = "und";
        var subChapters = new List<Chapter>();

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            switch (child.ElementId)
            {
                case MatroskaElementId.ChapterUid:
                    chapterUid = ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.ChapterTimeStart:
                    timeStartNs = ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.ChapterTimeEnd:
                    timeEndNs = ReadUnsignedInteger((int)child.DataSize);
                    break;
                case MatroskaElementId.ChapterFlagHidden:
                    isHidden = ReadUnsignedInteger((int)child.DataSize) != 0;
                    break;
                case MatroskaElementId.ChapterFlagEnabled:
                    isEnabled = ReadUnsignedInteger((int)child.DataSize) != 0;
                    break;
                case MatroskaElementId.ChapterDisplay:
                    ParseChapterDisplay(child.DataSize, ref chapterTitle, ref chapterLanguage);
                    break;
                case MatroskaElementId.ChapterAtom:
                    // Nested chapter
                    var subChapter = ParseChapterAtom(child.DataSize);
                    if (subChapter != null)
                        subChapters.Add(subChapter);
                    break;
                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }

        // Convert nanoseconds to TimeSpan ticks (100ns each)
        long startTicks = (long)(timeStartNs / 100);
        long endTicks = (long)(timeEndNs / 100);

        return new Chapter
        {
            Id = chapterUid,
            StartTime = new TimeSpan(startTicks),
            EndTime = new TimeSpan(endTicks),
            Title = chapterTitle,
            Language = chapterLanguage,
            IsHidden = isHidden,
            IsEnabled = isEnabled,
            SubChapters = subChapters
        };
    }

    private void ParseChapterDisplay(ulong size, ref string title, ref string language)
    {
        long endPosition = inputStream.Position + (long)size;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            switch (child.ElementId)
            {
                case MatroskaElementId.ChapString:
                    title = ReadString((int)child.DataSize);
                    break;
                case MatroskaElementId.ChapLanguage:
                    language = ReadString((int)child.DataSize);
                    break;
                case MatroskaElementId.ChapLanguageBcp47:
                    // Prefer BCP-47 if present
                    language = ReadString((int)child.DataSize);
                    break;
                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }
    }

    private bool ParseSimpleBlock(ulong size, out MatroskaPacket packet)
    {
        packet = default;
        if (size < 4)
        {
            SkipBytes((long)size);
            return false;
        }

        // Read entire block into buffer for lacing support
        int blockSize = (int)size;
        byte[] blockData;
        bool rentedBuffer = false;
        
        if (blockSize <= readBuffer.Length)
        {
            blockData = readBuffer;
        }
        else
        {
            blockData = ArrayPool<byte>.Shared.Rent(blockSize);
            rentedBuffer = true;
        }

        try
        {
            int bytesRead = inputStream.Read(blockData, 0, blockSize);
            if (bytesRead < blockSize)
                return false;

            var blockSpan = blockData.AsSpan(0, blockSize);

            // Parse track number (VINT)
            if (!EbmlParser.TryReadVint(blockSpan, out ulong trackNumber, out int trackNumLen))
                return false;

            // Read timestamp (signed 16-bit, big-endian)
            short relativeTimestamp = BinaryPrimitives.ReadInt16BigEndian(blockSpan.Slice(trackNumLen, 2));

            // Read flags
            byte flags = blockSpan[trackNumLen + 2];
            bool isKeyframe = (flags & 0x80) != 0;
            int lacingType = (flags >> 1) & 0x03;

            int headerSize = trackNumLen + 3;
            int payloadStart = headerSize;

            // Calculate absolute timestamp
            long absoluteTimestamp = currentClusterTimestamp + relativeTimestamp;
            long timestampTicks = (long)((ulong)absoluteTimestamp * timestampScale / 100);

            if (lacingType == 0)
            {
                // No lacing - single frame
                long currentPosition = inputStream.Position - blockSize + payloadStart;
                byte[]? strip = HeaderStripFor((int)trackNumber);
                if (strip is not null)
                    ResetRestoreBuffer(strip.Length + blockSpan.Length - payloadStart);
                packet = new MatroskaPacket
                {
                    TrackNumber = (int)trackNumber,
                    TimestampTicks = timestampTicks,
                    IsKeyframe = isKeyframe,
                    PayloadPosition = currentPosition,
                    PayloadSize = blockSize - payloadStart,
                    // With header stripping the frame a decoder wants is the prefix followed by
                    // the stored bytes, which is nowhere in the file as one run - so it is built.
                    Data = strip is null
                        ? default
                        : RestoreStrippedHeader(strip, blockSpan[payloadStart..])
                };
                return true;
            }

            // Handle laced blocks
            return ParseLacedBlock(blockSpan, payloadStart, (int)trackNumber, timestampTicks,
                                   isKeyframe, lacingType, inputStream.Position - blockSize,
                                   out packet);
        }
        finally
        {
            if (rentedBuffer)
                ArrayPool<byte>.Shared.Return(blockData);
        }
    }

    /// <summary>
    /// Split a laced block into one packet per frame.
    /// </summary>
    /// <param name="blockFilePosition">
    /// Where this block's bytes start in the file. Laced frames are contiguous there, so each
    /// one's position is this plus its offset within the block - which is what lets a laced
    /// frame be read at all. Before this argument existed every laced packet carried
    /// PayloadPosition = -1 and no payload, so nothing downstream could reach the bytes: in a
    /// typical .mkv that is every audio packet in the file, because audio is what lacing is
    /// for.
    /// </param>
    /// <summary>
    /// The bytes header stripping removed from this track's frames, or null.
    /// </summary>
    private byte[]? HeaderStripFor(int trackNumber)
    {
        foreach (var track in tracks)
            if (track.TrackNumber == trackNumber)
                return track.HeaderStripBytes;
        return null;
    }

    /// <summary>
    /// Makes room in <see cref="restoreBuffer"/> for every restored frame one block can produce,
    /// and starts filling it from the beginning again.
    /// </summary>
    /// <remarks>
    /// Called once per block, before any window is handed out, because a later grow would
    /// reallocate the array out from under the windows already given to this block's packets.
    /// </remarks>
    private void ResetRestoreBuffer(int capacity)
    {
        if (restoreBuffer.Length < capacity)
            restoreBuffer = new byte[Math.Max(capacity, restoreBuffer.Length * 2)];
        restoreUsed = 0;
    }

    /// <summary>
    /// Put a stripped prefix back on a frame, producing the frame a decoder expects.
    /// </summary>
    /// <remarks>
    /// The result is a window onto <see cref="restoreBuffer"/>, which is the lifetime
    /// <see cref="MatroskaPacket.Data"/> documents: valid until the next block is read. Building
    /// a fresh array here cost about 1.2 KB of garbage per frame on every header-stripped track,
    /// which in practice is every mkvmerge-produced audio track.
    /// </remarks>
    private ReadOnlyMemory<byte> RestoreStrippedHeader(byte[] prefix, ReadOnlySpan<byte> stored)
    {
        int at = restoreUsed;
        int length = prefix.Length + stored.Length;

        if (at + length > restoreBuffer.Length)
            throw new InvalidOperationException(
                $"Matroska header stripping: this block needs {at + length} bytes of restore "
              + $"space and {restoreBuffer.Length} were reserved. ResetRestoreBuffer was called "
              + "with too small a capacity for the block.");

        prefix.CopyTo(restoreBuffer, at);
        stored.CopyTo(restoreBuffer.AsSpan(at + prefix.Length));
        restoreUsed = at + length;
        return new ReadOnlyMemory<byte>(restoreBuffer, at, length);
    }

    private bool ParseLacedBlock(ReadOnlySpan<byte> block, int payloadStart, int trackNumber,
        long timestampTicks, bool isKeyframe, int lacingType, long blockFilePosition,
        out MatroskaPacket firstPacket)
    {
        firstPacket = default;

        // Read number of frames (stored as frameCount - 1)
        if (payloadStart >= block.Length)
            return false;

        int frameCount = block[payloadStart] + 1;
        int dataStart = payloadStart + 1;

        // Get frame sizes based on lacing type
        Span<int> frameSizes = stackalloc int[frameCount];
        int totalSizesLength = 0;

        switch (lacingType)
        {
            case 1: // Xiph lacing
                if (!ParseXiphLacing(block[dataStart..], frameCount, frameSizes, out totalSizesLength))
                    return false;
                break;

            case 2: // Fixed-size lacing
                int remainingData = block.Length - dataStart;
                int frameSize = remainingData / frameCount;
                for (int i = 0; i < frameCount; i++)
                    frameSizes[i] = frameSize;
                totalSizesLength = 0;
                break;

            case 3: // EBML lacing
                if (!ParseEbmlLacing(block[dataStart..], frameCount, frameSizes, out totalSizesLength))
                    return false;
                break;

            default:
                return false;
        }

        dataStart += totalSizesLength;

        // Get default frame duration for this track (if known) for timestamp calculation
        long frameDurationTicks = GetDefaultFrameDurationTicks(trackNumber);

        // One packet per laced frame. The frames sit back to back from dataStart, so each
        // one's file position is the block's plus its offset - currentOffset was already
        // being tracked here and then thrown away.
        long currentOffset = dataStart;
        byte[]? strip = HeaderStripFor(trackNumber);

        // Every laced frame in this block gets its own window, and they must all stay valid
        // until the queue drains, so the whole block's worth is reserved in one go.
        if (strip is not null)
            ResetRestoreBuffer(block.Length + (frameCount * strip.Length));

        for (int i = 0; i < frameCount; i++)
        {
            // Guard before slicing: a corrupt lacing header can claim more bytes than the block
            // holds, and the sizes come straight off the wire.
            if (currentOffset + frameSizes[i] > block.Length || frameSizes[i] < 0)
                return false;

            var lacedPacket = new MatroskaPacket
            {
                TrackNumber = trackNumber,
                TimestampTicks = timestampTicks + (i * frameDurationTicks),
                IsKeyframe = isKeyframe && i == 0, // Only first frame can be keyframe
                PayloadPosition = blockFilePosition + currentOffset,
                PayloadSize = frameSizes[i],
                Data = strip is null
                    ? default
                    : RestoreStrippedHeader(strip,
                          block.Slice((int)currentOffset, frameSizes[i]))
            };

            if (i == 0)
                firstPacket = lacedPacket;
            else
                pendingPackets.Enqueue(lacedPacket);

            currentOffset += frameSizes[i];
        }

        return true;
    }

    private static bool ParseXiphLacing(ReadOnlySpan<byte> data, int frameCount, Span<int> frameSizes, out int bytesConsumed)
    {
        bytesConsumed = 0;
        int totalSize = 0;
        int pos = 0;

        // Parse n-1 frame sizes (last frame size is remainder)
        for (int i = 0; i < frameCount - 1; i++)
        {
            int frameSize = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                frameSize += b;
                if (b < 255)
                    break;
            }
            frameSizes[i] = frameSize;
            totalSize += frameSize;
        }

        bytesConsumed = pos;

        // Last frame size is remainder of data
        int remainingData = data.Length - pos;
        frameSizes[frameCount - 1] = remainingData - totalSize;
        
        return true;
    }

    private static bool ParseEbmlLacing(ReadOnlySpan<byte> data, int frameCount, Span<int> frameSizes, out int bytesConsumed)
    {
        bytesConsumed = 0;
        int pos = 0;

        // First frame size is unsigned VINT
        if (!EbmlParser.TryReadVint(data[pos..], out ulong firstSize, out int firstLen))
            return false;
        
        frameSizes[0] = (int)firstSize;
        pos += firstLen;

        // Subsequent sizes are signed differences from previous size
        long previousSize = (long)firstSize;
        for (int i = 1; i < frameCount - 1; i++)
        {
            if (!EbmlParser.TryReadVint(data[pos..], out ulong signedDiff, out int diffLen))
                return false;

            // Convert to signed: subtract the midpoint for the VINT width
            long midpoint = GetEbmlLacingMidpoint(diffLen);
            long diff = (long)signedDiff - midpoint;
            long currentSize = previousSize + diff;
            
            frameSizes[i] = (int)currentSize;
            previousSize = currentSize;
            pos += diffLen;
        }

        bytesConsumed = pos;

        // Calculate last frame size as remainder
        int totalKnownSize = 0;
        for (int i = 0; i < frameCount - 1; i++)
            totalKnownSize += frameSizes[i];
        
        frameSizes[frameCount - 1] = data.Length - pos - totalKnownSize;
        
        return true;
    }

    private static long GetEbmlLacingMidpoint(int vintLength)
    {
        // Midpoint for signed EBML lacing VINTs
        return vintLength switch
        {
            1 => 0x3F,
            2 => 0x1FFF,
            3 => 0x0FFFFF,
            4 => 0x07FFFFFF,
            _ => 0x3F
        };
    }

    private long GetDefaultFrameDurationTicks(int trackNumber)
    {
        var track = tracks.FirstOrDefault(t => t.TrackNumber == trackNumber);
        if (track?.DefaultDurationNs > 0)
        {
            // Convert nanoseconds to ticks (100ns per tick)
            return (long)(track.DefaultDurationNs / 100);
        }
        
        // Default to ~20ms for audio (typical for Opus/Vorbis)
        return TimeSpan.FromMilliseconds(20).Ticks;
    }

    private bool ParseBlockGroup(ulong size, out MatroskaPacket packet)
    {
        packet = default;
        long endPosition = inputStream.Position + (long)size;
        bool foundBlock = false;
        
        // BlockGroup additional data
        long blockDuration = 0;
        long referenceBlockDelta = 0;
        bool hasReference = false;
        long discardPadding = 0;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadElementHeader(out var child))
                break;

            switch (child.ElementId)
            {
                case MatroskaElementId.Block:
                    // Block is parsed like SimpleBlock but keyframe comes from ReferenceBlock
                    foundBlock = ParseBlockWithinBlockGroup(child.DataSize, out packet);
                    break;
                    
                case MatroskaElementId.BlockDuration:
                    blockDuration = (long)ReadUnsignedInteger((int)child.DataSize);
                    break;
                    
                case MatroskaElementId.ReferenceBlock:
                    referenceBlockDelta = ReadSignedInteger((int)child.DataSize);
                    hasReference = true;
                    break;
                    
                case MatroskaElementId.DiscardPadding:
                    discardPadding = ReadSignedInteger((int)child.DataSize);
                    break;
                    
                case MatroskaElementId.BlockAdditions:
                    // Skip block additions for now - used for codec-specific data
                    SkipBytes((long)child.DataSize);
                    break;
                    
                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }
        
        if (foundBlock)
        {
            // Apply BlockGroup metadata to the packet
            if (blockDuration > 0)
            {
                // Convert from Matroska timestamp units to ticks
                packet.DurationTicks = (long)((ulong)blockDuration * timestampScale / 100);
            }
            
            if (hasReference)
            {
                packet.ReferenceBlockDelta = (long)(referenceBlockDelta * (long)timestampScale / 100);
                
                // If there's a reference, this is not a keyframe
                // A positive reference indicates a B-frame that can be discarded
                packet.IsKeyframe = false;
                if (referenceBlockDelta > 0)
                {
                    packet.IsDiscardable = true;
                }
            }
            
            if (discardPadding != 0)
            {
                packet.DiscardPaddingNs = discardPadding;
            }
        }

        return foundBlock;
    }
    
    /// <summary>
    /// Parses a Block element within BlockGroup - similar to SimpleBlock but keyframe is from ReferenceBlock.
    /// </summary>
    private bool ParseBlockWithinBlockGroup(ulong size, out MatroskaPacket packet)
    {
        packet = default;
        if (size < 4)
        {
            SkipBytes((long)size);
            return false;
        }

        // Read entire block into buffer for lacing support
        int blockSize = (int)size;
        byte[] blockData;
        bool rentedBuffer = false;
        
        if (blockSize <= readBuffer.Length)
        {
            blockData = readBuffer;
        }
        else
        {
            blockData = ArrayPool<byte>.Shared.Rent(blockSize);
            rentedBuffer = true;
        }

        try
        {
            int bytesRead = inputStream.Read(blockData, 0, blockSize);
            if (bytesRead < blockSize)
                return false;

            var blockSpan = blockData.AsSpan(0, blockSize);

            // Parse track number (VINT)
            if (!EbmlParser.TryReadVint(blockSpan, out ulong trackNumber, out int trackNumLen))
                return false;

            // Read timestamp (signed 16-bit, big-endian)
            short relativeTimestamp = BinaryPrimitives.ReadInt16BigEndian(blockSpan.Slice(trackNumLen, 2));

            // Read flags - for Block within BlockGroup, bit 0x80 is NOT the keyframe flag
            // Keyframe is determined by absence of ReferenceBlock elements
            byte flags = blockSpan[trackNumLen + 2];
            int lacingType = (flags >> 1) & 0x03;
            
            // Default to keyframe - will be overridden by BlockGroup if ReferenceBlock present
            bool isKeyframe = true;

            int headerSize = trackNumLen + 3;
            int payloadStart = headerSize;

            // Calculate absolute timestamp
            long absoluteTimestamp = currentClusterTimestamp + relativeTimestamp;
            long timestampTicks = (long)((ulong)absoluteTimestamp * timestampScale / 100);

            if (lacingType == 0)
            {
                // No lacing - single frame
                long currentPosition = inputStream.Position - blockSize + payloadStart;
                byte[]? strip = HeaderStripFor((int)trackNumber);
                if (strip is not null)
                    ResetRestoreBuffer(strip.Length + blockSpan.Length - payloadStart);
                packet = new MatroskaPacket
                {
                    TrackNumber = (int)trackNumber,
                    TimestampTicks = timestampTicks,
                    IsKeyframe = isKeyframe,
                    PayloadPosition = currentPosition,
                    PayloadSize = blockSize - payloadStart,
                    // With header stripping the frame a decoder wants is the prefix followed by
                    // the stored bytes, which is nowhere in the file as one run - so it is built.
                    Data = strip is null
                        ? default
                        : RestoreStrippedHeader(strip, blockSpan[payloadStart..])
                };
                return true;
            }

            // Handle laced blocks
            return ParseLacedBlock(blockSpan, payloadStart, (int)trackNumber, timestampTicks,
                                   isKeyframe, lacingType, inputStream.Position - blockSize,
                                   out packet);
        }
        finally
        {
            if (rentedBuffer)
                ArrayPool<byte>.Shared.Return(blockData);
        }
    }
    
    /// <summary>
    /// Reads a signed integer from EBML format.
    /// </summary>
    private long ReadSignedInteger(int length)
    {
        Span<byte> buffer = stackalloc byte[8];
        int bytesRead = inputStream.Read(buffer[..length]);
        
        if (bytesRead == 0)
            return 0;
        
        // Sign extend the value
        long value = 0;
        bool isNegative = (buffer[0] & 0x80) != 0;
        
        for (int i = 0; i < bytesRead; i++)
        {
            value = (value << 8) | buffer[i];
        }
        
        // Sign extend if necessary
        if (isNegative && bytesRead < 8)
        {
            long mask = -1L << (bytesRead * 8);
            value |= mask;
        }
        
        return value;
    }

    private bool TryReadElementHeader(out EbmlElementHeader header)
    {
        header = default;

        // Read enough bytes for max header size (4 byte ID + 8 byte size = 12)
        Span<byte> buffer = stackalloc byte[12];
        int bytesRead = inputStream.Read(buffer);
        if (bytesRead < 2)
            return false;

        if (!EbmlElementHeader.TryParse(buffer[..bytesRead], out header))
            return false;

        // Seek back to just after the header
        inputStream.Position -= bytesRead - header.HeaderSize;
        return true;
    }

    private ulong ReadUnsignedInteger(int length)
    {
        Span<byte> buffer = stackalloc byte[8];
        int bytesRead = inputStream.Read(buffer[..length]);
        return EbmlParser.ReadUnsignedInteger(buffer[..bytesRead]);
    }

    private double ReadFloat(int length)
    {
        Span<byte> buffer = stackalloc byte[8];
        int bytesRead = inputStream.Read(buffer[..length]);
        return EbmlParser.ReadFloat(buffer[..bytesRead]);
    }

    private string ReadString(int length)
    {
        if (length > readBuffer.Length)
        {
            var largeBuffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                int bytesRead = inputStream.Read(largeBuffer, 0, length);
                return EbmlParser.ReadString(largeBuffer.AsSpan(0, bytesRead));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(largeBuffer);
            }
        }

        int read = inputStream.Read(readBuffer, 0, length);
        return EbmlParser.ReadString(readBuffer.AsSpan(0, read));
    }

    private byte[] ReadBytes(int length)
    {
        var result = new byte[length];
        inputStream.ReadExactly(result);
        return result;
    }

    private void SkipBytes(long count)
    {
        if (inputStream.CanSeek)
        {
            inputStream.Position += count;
        }
        else
        {
            // For non-seekable streams, read and discard
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
/// Represents a track discovered in the Matroska file.
/// </summary>
public sealed class MatroskaTrack
{
    public int TrackNumber { get; set; }
    public ulong TrackUid { get; set; }
    public MatroskaTrackType TrackType { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsDefault { get; set; } = true;
    public string? Name { get; set; }
    public string? Language { get; set; } = "und";
    public string? CodecId { get; set; }
    public byte[]? CodecPrivate { get; set; }
    public ulong DefaultDurationNs { get; set; }

    // Video properties
    public int VideoWidth { get; set; }
    public int VideoHeight { get; set; }
    public int DisplayWidth { get; set; }
    public int DisplayHeight { get; set; }

    // Audio properties
    public double AudioSampleRate { get; set; }
    public int AudioChannels { get; set; }
    public int AudioBitDepth { get; set; }

    /// <summary>
    /// The bytes Matroska header stripping removed from the front of every frame on this track,
    /// or null when the track uses no compression.
    /// </summary>
    /// <remarks>
    /// ContentCompAlgo 3 is "header stripping": the muxer noticed that every frame begins with
    /// the same bytes, stored them once here, and cut them from each frame. A demuxer that
    /// ignores it hands every frame to the decoder with its first few bytes missing, which for
    /// an audio codec means the sync word - mkvmerge strips the 4-byte DTS sync word and the
    /// AC-3 one by default, so this is the normal case rather than an exotic one.
    /// </remarks>
    public byte[]? HeaderStripBytes { get; set; }

    /// <summary>
    /// True when this track uses a compression algorithm that is NOT header stripping.
    /// </summary>
    /// <remarks>
    /// zlib, bzlib and lzo1x are the others. None is implemented, and a frame from such a track
    /// is compressed data rather than a frame, so the demuxer refuses it instead of passing it
    /// on to be decoded as noise.
    /// </remarks>
    public bool HasUnsupportedCompression { get; set; }

    /// <summary>Gets the four-character code for this codec.</summary>
    public string GetCodecFourCC()
    {
        return CodecId switch
        {
            // matroska.c's three MPEG-4 Part 2 rows. V_MPEG4/MS/V3 is deliberately absent:
            // it is AV_CODEC_ID_MSMPEG4V3, a different decoder, and naming it mpeg4 would hand
            // its bitstream to a parser that cannot read it.
            "V_MPEG4/ISO/ASP" or "V_MPEG4/ISO/AP" or "V_MPEG4/ISO/SP" => "mpeg4",
            "V_MPEG4/ISO/AVC" => "h264",
            "V_MPEGH/ISO/HEVC" => "hevc",
            "V_VP8" => "vp8",
            "V_VP9" => "vp9",
            "V_AV1" => "av1",
            "A_AAC" => "aac",
            "A_AAC/MPEG4/LC" => "aac",
            "A_OPUS" => "opus",
            "A_VORBIS" => "vorbis",
            "A_AC3" => "ac3",
            "A_DTS" => "dts",
            // DTS-HD Master Audio carries a core substream, so it plays as its core.
            // A_DTS/EXPRESS is deliberately NOT here: DTS Express is LBR only, with no
            // core to fall back to, so claiming a decoder for it would produce silence.
            "A_DTS/LOSSLESS" => "dts",
            "A_EAC3" => "eac3",
            "A_FLAC" => "flac",
            "A_MPEG/L3" => "mp3",

            // Raw PCM. matroska.c lists one row per width against the same codec id string,
            // and matroskadec.c then picks the width from BitDepth - the string alone does not
            // say it. A depth the C has no row for keeps the 16-bit reading, which is what its
            // switch does by falling through.
            "A_PCM/INT/LIT" => AudioBitDepth switch
            {
                8 => "pcm_u8",
                24 => "pcm_s24le",
                32 => "pcm_s32le",
                _ => "pcm_s16le",
            },
            "A_PCM/INT/BIG" => AudioBitDepth switch
            {
                8 => "pcm_u8",
                24 => "pcm_s24be",
                32 => "pcm_s32be",
                _ => "pcm_s16be",
            },
            "A_PCM/FLOAT/IEEE" => AudioBitDepth == 64 ? "pcm_f64le" : "pcm_f32le",
            // matroska.c maps these to AV_CODEC_ID_SUBRIP and AV_CODEC_ID_WEBVTT, whose
            // descriptor names - the ones ffprobe prints - are "subrip" and "webvtt".
            // "srt" and "vtt" are file extensions, and name no codec anywhere.
            "S_TEXT/UTF8" => "subrip",
            "S_TEXT/ASS" => "ass",
            "S_TEXT/WEBVTT" => "webvtt",
            _ => CodecId ?? "unknown"
        };
    }
}

/// <summary>
/// Represents a media packet parsed from a Matroska block.
/// </summary>
public struct MatroskaPacket
{
    /// <summary>Track number this packet belongs to.</summary>
    public int TrackNumber;
    
    /// <summary>Presentation timestamp in ticks.</summary>
    public long TimestampTicks;
    
    /// <summary>True if this is a keyframe (I-frame).</summary>
    public bool IsKeyframe;
    
    /// <summary>True if this is a discardable frame (used for B-frames).</summary>
    public bool IsDiscardable;
    
    /// <summary>Reference to previous frame in timecode delta (0 = no reference).</summary>
    public long ReferenceBlockDelta;
    
    /// <summary>Explicit duration in ticks (0 = use default).</summary>
    public long DurationTicks;
    
    /// <summary>Discard padding in nanoseconds (for audio codecs like Opus).</summary>
    public long DiscardPaddingNs;
    
    /// <summary>File position where payload data starts.</summary>
    public long PayloadPosition;
    
    /// <summary>Size of payload data in bytes.</summary>
    public long PayloadSize;

    /// <summary>
    /// The frame itself, when the demuxer had to build it rather than point at it.
    /// </summary>
    /// <remarks>
    /// Null for the ordinary case, where <see cref="PayloadPosition"/> and
    /// <see cref="PayloadSize"/> address the bytes in the file and nothing needs copying. It is
    /// set when the track uses Matroska header stripping, because the frame handed to a decoder
    /// is then the stripped prefix followed by the stored bytes and exists nowhere in the file
    /// as a contiguous run.
    /// <para>
    /// When it is set, <see cref="PayloadPosition"/> and <see cref="PayloadSize"/> still describe
    /// the STORED bytes and stay consistent with each other. They are deliberately not stretched
    /// to the restored length: a position and size cannot express a frame that is partly a
    /// constant prefix, and widening the size to cover the prefix makes every consumer that reads
    /// by position pull in bytes belonging to the next element - which is exactly how this broke
    /// H.264 playback for one build. A consumer that wants the frame a decoder can use reads this
    /// field and falls back to the pair.
    /// </para>
    /// <para>
    /// <b>Lifetime:</b> when set, this is a window onto the demuxer's own restore buffer,
    /// valid until the next block is read - which, for the several packets a laced block
    /// produces, is after the last of them has been handed out. A caller that keeps a frame
    /// copies it.
    /// </para>
    /// </remarks>
    public ReadOnlyMemory<byte> Data;
}

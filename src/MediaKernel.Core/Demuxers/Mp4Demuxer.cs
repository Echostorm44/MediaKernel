using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// High-performance MP4/MOV/M4A/M4V demuxer.
/// Parses ISOBMFF container structure and extracts media samples.
/// </summary>
/// <remarks>
/// Supports both traditional MP4 (moov before mdat) and fragmented MP4 (moof+mdat pairs).
/// Uses zero-copy Span parsing for sample table navigation.
/// </remarks>
public sealed class Mp4Demuxer : IDisposable
{
    private readonly Stream inputStream;
    private readonly byte[] readBuffer;
    private readonly List<Mp4Track> tracks = new();
    private readonly List<MovieFragment> fragments = new();
    private int currentTrackFragmentIndex;
    private readonly ChapterList chapters = new();
    
    private long movieBoxPosition;
    private long mediaDataPosition;
    private long mediaDataSize;
    private uint timescale;
    private ulong duration;
    private bool isFragmented;
    private bool isInitialized;
    private bool isDisposed;
    
    // Fragment playback state
    private int currentFragmentIndex;
    private int currentFragmentRunIndex;
    private int currentFragmentSampleIndex;

    private const int DefaultBufferSize = 64 * 1024;

    public Mp4Demuxer(Stream stream)
    {
        inputStream = stream ?? throw new ArgumentNullException(nameof(stream));
        readBuffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
    }

    /// <summary>Gets all discovered tracks after initialization.</summary>
    public IReadOnlyList<Mp4Track> Tracks => tracks;

    /// <summary>Gets the movie timescale (units per second).</summary>
    public uint Timescale => timescale;

    /// <summary>Gets the total duration in timescale units.</summary>
    public ulong Duration => duration;

    /// <summary>Gets the duration in milliseconds.</summary>
    public double DurationMilliseconds => timescale > 0 ? duration * 1000.0 / timescale : 0;

    /// <summary>Gets whether this is a fragmented MP4 file.</summary>
    public bool IsFragmented => isFragmented;
    
    /// <summary>Gets all movie fragments (moof boxes) for fragmented MP4.</summary>
    public IReadOnlyList<MovieFragment> Fragments => fragments;
    
    /// <summary>Gets the chapter list for navigation.</summary>
    public ChapterList Chapters => chapters;

    /// <summary>
    /// Gets a SeekIndex for the specified track for use with SeekCoordinator.
    /// The index is built from the sync sample (stss) entries.
    /// </summary>
    /// <param name="trackIndex">The track index (0-based).</param>
    public SeekIndex? GetSeekIndex(int trackIndex = 0)
    {
        if (trackIndex < 0 || trackIndex >= tracks.Count)
            return null;
        
        var track = tracks[trackIndex];
        if (track.SyncSamples == null || track.SyncSamples.Count == 0)
            return null;
        
        var seekIndex = new SeekIndex();

        // Built from the rebuilt index when there is one, so a seek point names a timestamp
        // some packet really carries and never names a sample the edit list discards.
        if (track.IndexEntries is { } index)
        {
            for (int i = 0; i < index.Length; i++)
            {
                if (!index[i].IsKeyframe || index[i].Discard)
                    continue;

                seekIndex.Add(new SeekPoint(
                    TimescaleToTicks(index[i].Timestamp, track.Timescale),
                    index[i].Position, trackIndex, i));
            }

            return seekIndex;
        }

        foreach (int sampleIndex in track.SyncSamples.OrderBy(s => s))
        {
            long timestampTicks = GetSampleTimestampTicks(track, sampleIndex);
            long filePosition = GetSampleFileOffset(track, sampleIndex);

            seekIndex.Add(new SeekPoint(timestampTicks, filePosition, trackIndex, sampleIndex));
        }

        return seekIndex;
    }

    /// <summary>How many packets the track has left to hand out, index rebuilt or not.</summary>
    private static int PendingSampleCount(Mp4Track track)
        => track.IndexEntries?.Length ?? track.SampleCount;

    private long GetSampleTimestampTicks(Mp4Track track, int sampleIndex)
        => TimescaleToTicks(GetSampleTime(track, sampleIndex) - track.DtsShift, track.Timescale);
    
    /// <summary>The file offset of one sample.</summary>
    /// <remarks>
    /// Read from the table <see cref="BuildSampleIndex"/> already built. This used to walk the
    /// chunk table and return the offset of the sample's whole chunk, which is the same thing only
    /// when a chunk holds a single sample - so in a file with several samples per chunk every seek
    /// point landed at the start of a chunk rather than on its frame.
    /// </remarks>
    private static long GetSampleFileOffset(Mp4Track track, int sampleIndex)
        => track.SampleOffsets != null && sampleIndex >= 0 && sampleIndex < track.SampleOffsets.Length
            ? track.SampleOffsets[sampleIndex]
            : 0;

    /// <summary>
    /// Initializes the demuxer by parsing the file structure.
    /// </summary>
    public bool Initialize()
    {
        if (isInitialized)
            return true;

        // Find and parse moov box
        while (TryReadBoxHeader(out var header))
        {
            switch (header.BoxType)
            {
                case Mp4BoxType.FileType:
                    ParseFileType(header);
                    break;

                case Mp4BoxType.Movie:
                    movieBoxPosition = inputStream.Position - header.HeaderSize;
                    ParseMovieBox(header);
                    break;

                case Mp4BoxType.MediaData:
                    mediaDataPosition = inputStream.Position;
                    mediaDataSize = (long)header.DataSize;
                    SkipBytes((long)header.DataSize);
                    break;

                case Mp4BoxType.MovieFragment:
                    isFragmented = true;
                    long moofPosition = inputStream.Position - header.HeaderSize;
                    ParseMovieFragment(header, moofPosition);
                    break;

                default:
                    SkipBytes((long)header.DataSize);
                    break;
            }

            // Stop after finding moov (for traditional MP4, not fMP4)
            if (tracks.Count > 0 && !isFragmented)
                break;
        }

        // Build sample indices for each track
        foreach (var track in tracks)
        {
            BuildSampleIndex(track);
            MovFixIndex(track);
        }

        isInitialized = true;
        return tracks.Count > 0;
    }

    /// <summary>
    /// Reads the next sample packet using round-robin track selection.
    /// </summary>
    public bool ReadNextPacket(out Mp4Packet packet)
    {
        packet = default;

        if (!isInitialized || tracks.Count == 0)
            return false;

        // A fragmented file's samples are in its moof boxes, not in the sample tables below,
        // which are empty. ReadNextFragmentSample reads them and was called by nothing at all:
        // every caller goes through ReadNextPacket, so every fragmented MP4 - Smooth
        // Streaming, DASH, any .ism - came back with no samples and no error.
        if (isFragmented)
            return ReadNextFragmentSample(out packet);

        // Find track with earliest pending sample
        Mp4Track? bestTrack = null;
        long earliestTime = long.MaxValue;

        foreach (var track in tracks)
        {
            if (track.CurrentSampleIndex >= PendingSampleCount(track))
                continue;

            var sampleTime = track.IndexEntries is { } index
                ? index[track.CurrentSampleIndex].Timestamp
                : GetSampleTime(track, track.CurrentSampleIndex) - track.DtsShift;
            var normalizedTime = NormalizeToMovieTimescale(sampleTime, track.Timescale);
            
            if (normalizedTime < earliestTime)
            {
                earliestTime = normalizedTime;
                bestTrack = track;
            }
        }

        if (bestTrack == null)
            return false;

        return ReadTrackSample(bestTrack, out packet);
    }

    /// <summary>
    /// Reads the next sample from a specific track.
    /// </summary>
    public bool ReadTrackSample(Mp4Track track, out Mp4Packet packet)
    {
        packet = default;

        // A track with an edit list reads from the index that edit list produced, which is
        // what ffmpeg does for every track: mov_build_index writes an index and mov_fix_index
        // replaces it. Here the index only exists where it differs from the tables.
        if (track.IndexEntries is { } index)
        {
            if (track.CurrentSampleIndex >= index.Length)
                return false;

            ref readonly Mp4IndexEntry entry = ref index[track.CurrentSampleIndex];

            packet = new Mp4Packet
            {
                TrackIndex = tracks.IndexOf(track),
                DecodeTimeTicks = TimescaleToTicks(entry.Timestamp, track.Timescale),

                // `pkt->pts = pkt->dts + sc->dts_shift + tts_data[...].offset` - mov.c:11281.
                // The shift comes back on here because the index timestamps carry it taken off.
                PresentationTimeTicks = TimescaleToTicks(
                    entry.Timestamp + track.DtsShift + entry.CompositionOffset, track.Timescale),
                IsKeyframe = entry.IsKeyframe,
                IsDiscard = entry.Discard,
                PayloadPosition = entry.Position,
                PayloadSize = entry.Size,
            };

            track.CurrentSampleIndex++;
            return true;
        }

        if (track.CurrentSampleIndex >= track.SampleCount)
            return false;

        int sampleIndex = track.CurrentSampleIndex;
        var sampleInfo = GetSampleInfo(track, sampleIndex);

        // `current_dts -= sc->dts_shift` - mov.c:4775, mov_build_index. A track whose
        // composition offsets go negative has its whole decode timeline moved back by the
        // largest of them, so that no presentation time lands before its own decode time; the
        // shift then comes back on to form the pts, as mov_read_packet does. Nothing here
        // subtracted it, so every dts of such a file was one shift high - 1001 of them on
        // h264/thezerotheorem-cut.mp4, whose pts were right all along.
        long decodeTime = GetSampleTime(track, sampleIndex) - track.DtsShift;
        long presentationTime = decodeTime + track.DtsShift
                              + GetCompositionOffset(track, sampleIndex);

        packet = new Mp4Packet
        {
            TrackIndex = tracks.IndexOf(track),
            DecodeTimeTicks = TimescaleToTicks(decodeTime, track.Timescale),
            PresentationTimeTicks = TimescaleToTicks(presentationTime, track.Timescale),
            IsKeyframe = IsSyncSample(track, sampleIndex),
            PayloadPosition = sampleInfo.Offset,
            PayloadSize = sampleInfo.Size
        };

        track.CurrentSampleIndex++;
        return true;
    }
    
    /// <summary>
    /// Reads the next sample from a fragmented MP4.
    /// Returns samples across all fragments in file order: fragment, track fragment,
    /// run, sample.
    /// </summary>
    /// <remarks>
    /// The four cursors have to be four cursors. This used to iterate the track
    /// fragments with a foreach while keeping the run and sample indices in fields, so
    /// every call restarted at the first track fragment of the moof: once one was
    /// exhausted the same call carried on into the next and returned a sample from it,
    /// and the call after that went back to the first with the second one's sample
    /// index. A file with audio and video interleaved therefore re-read part of one
    /// track fragment, skipped part of the next, and computed offsets from the wrong
    /// run - 92 samples out of 120 for fate-suite/isom/vc1-wmapro.ism, with no error
    /// reported anywhere.
    /// </remarks>
    public bool ReadNextFragmentSample(out Mp4Packet packet)
    {
        packet = default;
        
        if (!isFragmented || fragments.Count == 0)
            return false;
        
        // Find the next sample across all fragments
        while (currentFragmentIndex < fragments.Count)
        {
            var fragment = fragments[currentFragmentIndex];
            
            while (currentTrackFragmentIndex < fragment.TrackFragments.Count)
            {
                var trackFragment = fragment.TrackFragments[currentTrackFragmentIndex];
                
                while (currentFragmentRunIndex < trackFragment.Runs.Count)
                {
                    var run = trackFragment.Runs[currentFragmentRunIndex];
                    
                    if (currentFragmentSampleIndex < run.Samples.Count)
                    {
                        var sample = run.Samples[currentFragmentSampleIndex];
                        var header = trackFragment.Header;
                        
                        // Find the track for this fragment
                        int trackIndex = tracks.FindIndex(t => t.TrackId == header.TrackId);
                        if (trackIndex < 0)
                            trackIndex = 0;
                        
                        var track = trackIndex < tracks.Count ? tracks[trackIndex] : null;
                        uint trackTimescale = track?.Timescale ?? timescale;
                        
                        // Decode time is the track fragment's base plus every sample
                        // before this one - in the earlier runs of this track fragment
                        // as well as in this one, which a run-local loop alone misses.
                        ulong decodeTime = trackFragment.BaseMediaDecodeTime;
                        for (int r = 0; r < currentFragmentRunIndex; r++)
                        {
                            var earlier = trackFragment.Runs[r];
                            for (int i = 0; i < earlier.Samples.Count; i++)
                            {
                                decodeTime += earlier.HasSampleDuration
                                    ? earlier.Samples[i].Duration
                                    : header.DefaultSampleDuration;
                            }
                        }
                        for (int i = 0; i < currentFragmentSampleIndex; i++)
                        {
                            decodeTime += run.HasSampleDuration
                                ? run.Samples[i].Duration
                                : header.DefaultSampleDuration;
                        }
                        
                        // Get sample size
                        uint sampleSize = run.HasSampleSize
                            ? sample.Size
                            : header.DefaultSampleSize;
                        
                        // Calculate file offset
                        // ISO 14496-12 8.8.7: with base_data_offset present it is the base;
                        // otherwise the base is the first byte of the enclosing moof, which
                        // is also exactly what default-base-is-moof asks for. The fallback
                        // had been mediaDataPosition - the position of the LAST mdat the file
                        // walk saw - so every fragment but the final one was given offsets
                        // from the end of the file, and the reads either ran past it or
                        // returned another fragment's bytes.
                        long baseOffset = header.HasBaseDataOffset
                            ? header.BaseDataOffset
                            : fragment.FilePosition;
                        
                        long sampleOffset = baseOffset + run.DataOffset;
                        for (int i = 0; i < currentFragmentSampleIndex; i++)
                        {
                            sampleOffset += run.HasSampleSize
                                ? run.Samples[i].Size
                                : header.DefaultSampleSize;
                        }
                        
                        // Check if keyframe
                        uint flags = run.HasSampleFlags
                            ? sample.Flags
                            : (currentFragmentSampleIndex == 0 && run.HasFirstSampleFlags
                                ? run.FirstSampleFlags
                                : header.DefaultSampleFlags);
                        bool isKeyframe = (flags & 0x00010000) == 0; // sample_is_non_sync_sample
                        
                        packet = new Mp4Packet
                        {
                            TrackIndex = trackIndex,
                            DecodeTimeTicks = (long)(decodeTime * TimeSpan.TicksPerSecond / trackTimescale),
                            PresentationTimeTicks = (long)((decodeTime + (ulong)sample.CompositionTimeOffset) * (ulong)TimeSpan.TicksPerSecond / trackTimescale),
                            IsKeyframe = isKeyframe,
                            PayloadPosition = sampleOffset,
                            PayloadSize = (int)sampleSize
                        };
                        
                        currentFragmentSampleIndex++;
                        return true;
                    }
                    
                    currentFragmentRunIndex++;
                    currentFragmentSampleIndex = 0;
                }
                
                currentTrackFragmentIndex++;
                currentFragmentRunIndex = 0;
            }
            
            currentFragmentIndex++;
            currentTrackFragmentIndex = 0;
        }
        
        return false;
    }
    
    /// <summary>
    /// Resets fragment playback position to the beginning.
    /// </summary>
    public void ResetFragmentPosition()
    {
        currentTrackFragmentIndex = 0;
        currentFragmentIndex = 0;
        currentFragmentRunIndex = 0;
        currentFragmentSampleIndex = 0;
    }

    /// <summary>
    /// Seeks to the nearest sync sample before the specified timestamp.
    /// </summary>
    public bool Seek(double timestampMs)
    {
        foreach (var track in tracks)
        {
            // Rounded, not truncated. The caller asks in milliseconds and the tables are in the
            // track's own timescale, and a request derived from a timestamp this demuxer itself
            // reported comes back a fraction low - 3436.7666 ms for the keyframe at 309,309 of
            // 90,000, which truncates to 309,308. The index search then answers with the
            // keyframe BEFORE the one asked for, because 309,308 really is before it.
            long trackTime = (long)Math.Round(timestampMs * track.Timescale / 1000.0);

            // A track with an edit list is searched over the index that edit list produced,
            // the way mov_seek_stream does. The caller asks in presentation time; the index
            // holds decode time, and `timestamp -= (sc->min_corrected_pts + sc->dts_shift)`
            // is how the C crosses between them.
            if (track.IndexEntries is { Length: > 0 } index)
            {
                long searchTime = trackTime - (track.MinCorrectedPts + track.DtsShift);

                int entry = IndexSearchTimestamp(index, index.Length, searchTime,
                                                 backward: true, any: false);

                // "if (sample < 0 && nb_index_entries && timestamp < index_entries[0].timestamp)
                //  sample = 0;"
                if (entry < 0)
                    entry = 0;

                track.CurrentSampleIndex = entry;

                // A seek into the middle of the priming has already passed part of it, so only
                // what is left is skipped.
                track.SkipSamples = MovGetSkipSamples(track, entry);
                continue;
            }

            int sampleIndex = FindSampleAtTime(track, trackTime);
            track.CurrentSampleIndex = FindPrecedingSyncSample(track, sampleIndex);
        }

        return true;
    }

    private void ParseFileType(Mp4BoxHeader header)
    {
        // Read brand and version (8 bytes minimum)
        Span<byte> buffer = stackalloc byte[8];
        if (inputStream.Read(buffer) < 8)
            return;

        // Major brand (4 bytes)
        // Minor version (4 bytes)
        // Compatible brands follow...
        
        long remaining = (long)header.DataSize - 8;
        if (remaining > 0)
            SkipBytes(remaining);
    }

    private void ParseMovieBox(Mp4BoxHeader header)
    {
        long endPosition = inputStream.Position + (long)header.DataSize;
        int trackCount = 0;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadBoxHeader(out var child))
            {
                break;
            }
            
            string boxName = Mp4BoxType.FourCCToString(child.BoxType);

            switch (child.BoxType)
            {
                case Mp4BoxType.MovieHeader:
                    ParseMovieHeader(child);
                    break;

                case Mp4BoxType.Track:
                    trackCount++;
                    var track = ParseTrack(child);
                    if (track != null)
                        tracks.Add(track);
                    break;

                case Mp4BoxType.MovieExtends:
                    // mvex says the samples are in moof fragments and the sample tables in
                    // this moov are empty. It has to be read HERE and not when the first moof
                    // turns up, because Initialize stops walking the file as soon as it has
                    // tracks and does not yet know the file is fragmented - so a flag set at
                    // the first moof is a flag set after the loop that would have used it.
                    // That is why every fragmented MP4 - Smooth Streaming, DASH, any .ism -
                    // produced zero samples: the tracks, codecs and extradata all came out
                    // right and there was nothing to decode.
                    isFragmented = true;
                    SkipBytes((long)child.DataSize);
                    break;

                case Mp4BoxType.UserData:
                    ParseUserDataBox(child);
                    break;

                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }
    }

    private void ParseMovieHeader(Mp4BoxHeader header)
    {
        // Limit read to actual box data size
        int toRead = (int)Math.Min(32, header.DataSize);
        Span<byte> buffer = stackalloc byte[32];
        int bytesRead = inputStream.Read(buffer.Slice(0, toRead));
        if (bytesRead < 4)
            return;

        byte version = buffer[0];
        int offset = 4; // Skip version + flags

        if (version == 1)
        {
            // 64-bit times - need 28 bytes minimum
            if (bytesRead >= 28)
            {
                // creation_time (8) + modification_time (8) + timescale (4) + duration (8)
                timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + 16, 4));
                duration = BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(offset + 20, 8));
            }
        }
        else
        {
            // 32-bit times
            // creation_time (4) + modification_time (4) + timescale (4) + duration (4)
            timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + 8, 4));
            duration = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + 12, 4));
        }

        // Skip rest of mvhd
        long remaining = (long)header.DataSize - bytesRead;
        if (remaining > 0)
            SkipBytes(remaining);
    }

    private void ParseUserDataBox(Mp4BoxHeader header)
    {
        long endPosition = inputStream.Position + (long)header.DataSize;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadBoxHeader(out var child))
                break;

            if (child.BoxType == Mp4BoxType.ChapterList)
            {
                ParseChapterList(child);
            }
            else
            {
                SkipBytes((long)child.DataSize);
            }
        }
    }

    private void ParseChapterList(Mp4BoxHeader header)
    {
        // Nero chapter list format:
        // 1 byte version (usually 1)
        // 3 bytes flags (usually 0)
        // 1 byte reserved (0)
        // 4 bytes chapter count (for version 1) or
        // For older version: 1 byte chapter count
        
        if (header.DataSize < 6)
        {
            SkipBytes((long)header.DataSize);
            return;
        }

        long startPosition = inputStream.Position;
        long endPosition = startPosition + (long)header.DataSize;
        
        Span<byte> headerBytes = stackalloc byte[9];
        int headerRead = inputStream.Read(headerBytes);
        if (headerRead < 5)
            return;

        byte version = headerBytes[0];
        // Skip flags (3 bytes) and reserved (1 byte)
        int chapterCount;
        int dataOffset;

        if (version >= 1 && headerRead >= 9)
        {
            // Version 1: 4-byte chapter count at offset 5
            chapterCount = BinaryPrimitives.ReadInt32BigEndian(headerBytes.Slice(5, 4));
            dataOffset = 9;
        }
        else
        {
            // Older format: 1-byte chapter count at offset 4
            chapterCount = headerBytes[4];
            dataOffset = 5;
        }

        if (chapterCount <= 0 || chapterCount > 256)
        {
            SkipBytes(endPosition - inputStream.Position);
            return;
        }

        var chapterList = new List<Chapter>();
        
        // Read chapter entries
        for (int i = 0; i < chapterCount && inputStream.Position < endPosition; i++)
        {
            // Each entry: 8 bytes timestamp (100ns units) + 1 byte name length + name
            Span<byte> entryHeader = stackalloc byte[9];
            if (inputStream.Read(entryHeader) < 9)
                break;

            // Timestamp is in 100-nanosecond units
            long timestampTicks = BinaryPrimitives.ReadInt64BigEndian(entryHeader.Slice(0, 8));
            int nameLength = entryHeader[8];

            string title = string.Empty;
            if (nameLength > 0)
            {
                byte[] nameBuffer = new byte[nameLength];
                int nameRead = inputStream.Read(nameBuffer, 0, nameLength);
                if (nameRead == nameLength)
                {
                    title = System.Text.Encoding.UTF8.GetString(nameBuffer);
                }
            }

            chapterList.Add(new Chapter
            {
                Id = (ulong)(i + 1),
                StartTime = new TimeSpan(timestampTicks),
                Title = title,
                IsEnabled = true
            });
        }

        // Skip any remaining data
        long remaining = endPosition - inputStream.Position;
        if (remaining > 0)
            SkipBytes(remaining);

        // Create edition with parsed chapters
        if (chapterList.Count > 0)
        {
            chapters.AddEdition(new ChapterEdition
            {
                Id = 1,
                IsDefault = true,
                Chapters = chapterList
            });
        }
    }

    private Mp4Track? ParseTrack(Mp4BoxHeader header)
    {
        var track = new Mp4Track();
        long endPosition = inputStream.Position + (long)header.DataSize;

        while (inputStream.Position < endPosition)
        {
            long beforeRead = inputStream.Position;
            if (!TryReadBoxHeader(out var child))
            {
                break;
            }
            
            string boxName = Mp4BoxType.FourCCToString(child.BoxType);

            switch (child.BoxType)
            {
                case Mp4BoxType.TrackHeader:
                    ParseTrackHeader(child, track);
                    break;

                case Mp4BoxType.Media:
                    ParseMedia(child, track);
                    break;

                case Mp4BoxType.Edit:
                    ParseEdit(child, track);
                    break;

                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }

        return track.TrackId > 0 ? track : null;
    }

    private void ParseTrackHeader(Mp4BoxHeader header, Mp4Track track)
    {
        // Limit read to actual box data size
        int toRead = (int)Math.Min(92, header.DataSize);
        Span<byte> buffer = stackalloc byte[92];
        int bytesRead = inputStream.Read(buffer.Slice(0, toRead));
        if (bytesRead < 4)
            return;

        byte version = buffer[0];
        int offset = 4;

        if (version == 1)
        {
            // 64-bit: creation_time (8) + modification_time (8) + track_ID (4) + reserved (4) + duration (8)
            track.TrackId = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + 16, 4));
            track.Duration = BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(offset + 24, 8));
            offset += 32;
        }
        else
        {
            // 32-bit: creation_time (4) + modification_time (4) + track_ID (4) + reserved (4) + duration (4)
            track.TrackId = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + 8, 4));
            track.Duration = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + 16, 4));
            offset += 20;
        }

        // Skip reserved (8 bytes) + layer (2) + alternate_group (2) + volume (2) + reserved (2) + matrix (36)
        offset += 52;

        if (bytesRead >= offset + 8)
        {
            // width and height are 16.16 fixed point
            uint widthFixed = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset, 4));
            uint heightFixed = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + 4, 4));
            track.Width = (int)(widthFixed >> 16);
            track.Height = (int)(heightFixed >> 16);
        }

        long remaining = (long)header.DataSize - bytesRead;
        if (remaining > 0)
            SkipBytes(remaining);
    }

    private void ParseMedia(Mp4BoxHeader header, Mp4Track track)
    {
        long endPosition = inputStream.Position + (long)header.DataSize;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadBoxHeader(out var child))
            {
                break;
            }
            
            string boxName = Mp4BoxType.FourCCToString(child.BoxType);

            switch (child.BoxType)
            {
                case Mp4BoxType.MediaHeader:
                    ParseMediaHeader(child, track);
                    break;

                case Mp4BoxType.Handler:
                    ParseHandler(child, track);
                    break;

                case Mp4BoxType.MediaInfo:
                    ParseMediaInfo(child, track);
                    break;

                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }
    }

    private void ParseMediaHeader(Mp4BoxHeader header, Mp4Track track)
    {
        // Limit read to the actual box data size
        int toRead = (int)Math.Min(32, header.DataSize);
        Span<byte> buffer = stackalloc byte[32];
        int bytesRead = inputStream.Read(buffer.Slice(0, toRead));
        if (bytesRead < 4)
            return;

        byte version = buffer[0];
        int offset = 4;

        if (version == 1)
        {
            // 64-bit times
            track.Timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + 16, 4));
            track.MediaDuration = BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(offset + 20, 8));
        }
        else
        {
            // 32-bit times
            track.Timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + 8, 4));
            track.MediaDuration = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + 12, 4));
        }

        long remaining = (long)header.DataSize - bytesRead;
        if (remaining > 0)
            SkipBytes(remaining);
    }

    private void ParseHandler(Mp4BoxHeader header, Mp4Track track)
    {
        // Limit read to actual box data size
        int toRead = (int)Math.Min(24, header.DataSize);
        Span<byte> buffer = stackalloc byte[24];
        int bytesRead = inputStream.Read(buffer.Slice(0, toRead));
        if (bytesRead < 12)
        {
            SkipBytes((long)header.DataSize - bytesRead);
            return;
        }

        // FullBox: version (1) + flags (3) + pre_defined (4) = 8 bytes
        // Then handler_type (4)
        uint handlerType = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4));
        
        // Debug: log the handler type to stderr
        char c1 = (char)((handlerType >> 24) & 0xFF);
        char c2 = (char)((handlerType >> 16) & 0xFF);
        char c3 = (char)((handlerType >> 8) & 0xFF);
        char c4 = (char)(handlerType & 0xFF);

        track.TrackType = handlerType switch
        {
            Mp4HandlerType.Video => Mp4TrackType.Video,
            Mp4HandlerType.Audio => Mp4TrackType.Audio,
            Mp4HandlerType.Text or Mp4HandlerType.Subtitle => Mp4TrackType.Subtitle,
            Mp4HandlerType.Hint => Mp4TrackType.Hint,
            _ => Mp4TrackType.Unknown
        };
        

        long remaining = (long)header.DataSize - bytesRead;
        if (remaining > 0)
            SkipBytes(remaining);
    }

    private void ParseMediaInfo(Mp4BoxHeader header, Mp4Track track)
    {
        long endPosition = inputStream.Position + (long)header.DataSize;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadBoxHeader(out var child))
                break;
                
            string boxName = Mp4BoxType.FourCCToString(child.BoxType);

            if (child.BoxType == Mp4BoxType.SampleTable)
            {
                long beforeStbl = inputStream.Position;
                ParseSampleTable(child, track);
            }
            else
            {
                SkipBytes((long)child.DataSize);
            }
        }
    }

    private void ParseSampleTable(Mp4BoxHeader header, Mp4Track track)
    {
        long endPosition = inputStream.Position + (long)header.DataSize;

        while (inputStream.Position < endPosition)
        {
            long beforeBox = inputStream.Position;
            if (!TryReadBoxHeader(out var child))
                break;
                
            string boxName = Mp4BoxType.FourCCToString(child.BoxType);

            switch (child.BoxType)
            {
                case Mp4BoxType.SampleDescription:
                    ParseSampleDescription(child, track);
                    break;

                case Mp4BoxType.TimeToSample:
                    ParseTimeToSample(child, track);
                    break;

                case Mp4BoxType.CompositionOffset:
                    ParseCompositionOffset(child, track);
                    break;

                case Mp4BoxType.SampleToChunk:
                    ParseSampleToChunk(child, track);
                    break;

                case Mp4BoxType.SampleSize:
                    ParseSampleSize(child, track);
                    break;

                case Mp4BoxType.ChunkOffset:
                    ParseChunkOffset(child, track, is64Bit: false);
                    break;

                case Mp4BoxType.ChunkOffset64:
                    ParseChunkOffset(child, track, is64Bit: true);
                    break;

                case Mp4BoxType.SyncSample:
                    ParseSyncSample(child, track);
                    break;

                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }
    }

    private void ParseSampleDescription(Mp4BoxHeader header, Mp4Track track)
    {
        // Where this box ends, taken BEFORE anything is read. The sample entry parsers are
        // allowed to read as much or as little of their entry as they need, and this is
        // what puts the walk back on the next box afterwards.
        //
        // It used to subtract the entry's declared length from a running remainder and skip
        // that instead, which is only correct if the entry parser consumed its entry
        // exactly. A classic QuickTime video sample entry is not a fixed 86 bytes - the one
        // in catfight-cvid-pal8-partial.mov is 2142, because the palette lives inside it -
        // so the skip landed in the middle of the sample table, every box after it was read
        // as garbage, and the file's SECOND TRACK was never seen at all. The container
        // naming sweep is what caught it.
        long boxEnd = inputStream.Position + (long)header.DataSize;

        Span<byte> buffer = stackalloc byte[8];
        if (inputStream.Read(buffer) < 8)
        {
            inputStream.Position = boxEnd;
            return;
        }

        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));

        // The stsd box's own version byte, which mov_parse_stsd_audio needs: whether a
        // version-1 sound description's four extra fields are present depends on it.
        track.StsdVersion = buffer[0];
        if (entryCount > 0 && boxEnd - inputStream.Position > 8
            && TryReadBoxHeader(out var entryHeader))
        {
            // Store codec FourCC
            track.CodecFourCC = entryHeader.BoxType;

            // Parse codec-specific data
            ParseCodecEntry(entryHeader, track);
        }

        inputStream.Position = boxEnd;
    }

    private void ParseCodecEntry(Mp4BoxHeader header, Mp4Track track)
    {
        if (track.TrackType == Mp4TrackType.Video)
        {
            ParseVideoSampleEntry(header, track);
        }
        else if (track.TrackType == Mp4TrackType.Audio)
        {
            ParseAudioSampleEntry(header, track);
        }
        else
        {
            SkipBytes((long)header.DataSize);
        }
    }

    private void ParseVideoSampleEntry(Mp4BoxHeader header, Mp4Track track)
    {
        Span<byte> buffer = stackalloc byte[78];
        int bytesRead = inputStream.Read(buffer);
        if (bytesRead < 78)
        {
            SkipBytes((long)header.DataSize - bytesRead);
            return;
        }

        // VisualSampleEntry layout:
        // 6 bytes: reserved
        // 2 bytes: data_reference_index
        // 2 bytes: pre_defined
        // 2 bytes: reserved
        // 12 bytes: pre_defined (3 x 32-bit)
        // 2 bytes: width (offset 24)
        // 2 bytes: height (offset 26)
        track.Width = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(24, 2));
        track.Height = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(26, 2));
        // Skip horizresolution (4) + vertresolution (4) + reserved (4) + frame_count (2) + compressorname (32)
        // Skip horizresolution (4) + vertresolution (4) + reserved (4) + frame_count (2) + compressorname (32)

        long remaining = (long)header.DataSize - bytesRead;

        // The palette of a paletted QuickTime track, which lives HERE and nowhere else.
        // ff_get_qtpalette measures from the entry's box start; this buffer is the entry's
        // BODY, so its 82 is 74 here.
        int depthAndFlags = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(74, 2));
        int colorTableId = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(76, 2));
        int paletteDepth = depthAndFlags & 0x1F;
        bool greyscale = (depthAndFlags & 0x20) != 0;

        track.BitsPerCodedSample = depthAndFlags;

        if (paletteDepth is 1 or 2 or 4 or 8
            && !(greyscale && track.CodecFourCC == Mp4BoxType.Cvid))
        {
            if (colorTableId != 0)
            {
                // A non-zero id means -1: the default Macintosh table for this depth, or a
                // computed greyscale ramp when the greyscale bit is set above one bit.
                track.Palette = new uint[256];
                if (greyscale && paletteDepth > 1)
                {
                    int colorCount = 1 << paletteDepth;
                    int colorIndex = 255;
                    int colorDec = 256 / (colorCount - 1);
                    for (int i = 0; i < colorCount; i++)
                    {
                        uint v = (uint)colorIndex;
                        track.Palette[i] = 0xFF000000u | (v << 16) | (v << 8) | v;
                        colorIndex -= colorDec;
                        if (colorIndex < 0) colorIndex = 0;
                    }
                }
                else
                {
                    ReadOnlySpan<byte> table = QtPalette.ForDepth(paletteDepth);
                    int colorCount = 1 << paletteDepth;
                    for (int i = 0; i < colorCount && i * 3 + 2 < table.Length; i++)
                    {
                        track.Palette[i] = 0xFF000000u
                                         | ((uint)table[i * 3 + 0] << 16)
                                         | ((uint)table[i * 3 + 1] << 8)
                                         | table[i * 3 + 2];
                    }
                }

                track.BitsPerCodedSample = paletteDepth;
            }
            else if (remaining >= 8)
            {
                // The table is inline, right after the 78 bytes already read: a start
                // index, two flag bytes, an end index, and then four 16-bit values per
                // colour of which only the top byte of each of the last three is used.
                Span<byte> head = stackalloc byte[8];
                if (inputStream.Read(head) == 8)
                {
                    remaining -= 8;
                    uint colorStart = BinaryPrimitives.ReadUInt32BigEndian(head);
                    uint colorEnd = BinaryPrimitives.ReadUInt16BigEndian(head.Slice(6, 2));

                    if (colorStart <= 255 && colorEnd <= 255)
                    {
                        track.Palette = new uint[256];
                        Span<byte> colour = stackalloc byte[8];
                        for (uint i = colorStart; i <= colorEnd && remaining >= 8; i++)
                        {
                            if (inputStream.Read(colour) != 8)
                                break;
                            remaining -= 8;
                            track.Palette[i] = 0xFF000000u
                                             | ((uint)colour[2] << 16)
                                             | ((uint)colour[4] << 8)
                                             | colour[6];
                        }

                        track.BitsPerCodedSample = paletteDepth;
                    }
                }
            }
        }

        // Look for codec config boxes (avcC, hvcC, etc.)
        while (remaining > 8)
        {
            if (!TryReadBoxHeader(out var child))
                break;

            if (child.BoxType == Mp4BoxType.AvcC || 
                child.BoxType == Mp4BoxType.HvcC || 
                child.BoxType == Mp4BoxType.VpcC ||
                child.BoxType == Mp4BoxType.Av1C)
            {
                track.CodecPrivate = new byte[(int)child.DataSize];
                inputStream.ReadExactly(track.CodecPrivate);
            }
            else if (child.BoxType == Mp4BoxType.Dvc1 && child.DataSize >= 7)
            {
                // mov_read_dvc1: one profile_level byte whose top nibble must be 0xC, six
                // reserved bytes, and then the VC-1 extradata - a marker-delimited sequence
                // header and entry point, which is the only place a VC-1 track in MP4 carries
                // them. Without it the decoder has nothing to open with.
                Span<byte> head = stackalloc byte[7];
                inputStream.ReadExactly(head);
                if ((head[0] & 0xF0) == 0xC0)
                {
                    track.CodecPrivate = new byte[(int)child.DataSize - 7];
                    inputStream.ReadExactly(track.CodecPrivate);
                }
                else
                {
                    SkipBytes((long)child.DataSize - 7);
                }
            }
            else
            {
                SkipBytes((long)child.DataSize);
            }

            remaining -= child.HeaderSize + (long)child.DataSize;
        }

        if (remaining > 0)
            SkipBytes(remaining);
    }

    private void ParseAudioSampleEntry(Mp4BoxHeader header, Mp4Track track)
    {
        Span<byte> buffer = stackalloc byte[28];
        int bytesRead = inputStream.Read(buffer);
        if (bytesRead < 28)
        {
            SkipBytes((long)header.DataSize - bytesRead);
            return;
        }

        // Skip reserved (6) + data_ref_index (2); then the sound description's own version
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(8, 2));
        // Skip revision (2) + vendor (4)
        track.AudioChannels = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(16, 2));
        track.AudioBitsPerSample = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(18, 2));
        // Skip compression_id (2) + packet_size (2)
        uint sampleRateFixed = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(24, 4));
        track.AudioSampleRate = sampleRateFixed >> 16; // 16.16 fixed point

        // A VERSION 1 OR 2 SOUND DESCRIPTION HAS MORE FIELDS BEFORE ITS CHILD BOXES - four
        // more for version 1, nine for version 2 - and reading past them is not optional:
        // the esds that follows is simply not where a version 0 entry would put it. Skipping
        // this is why h264/thezerotheorem-cut.mp4 reached the AAC decoder with ZERO bytes of
        // AudioSpecificConfig and produced no audio, while its video track decoded.
        //
        // mov_parse_stsd_audio reads them when the file is not isom, or its compatible_brands
        // name "qt  ", or - the clause that covers this file - the stsd box's version is 0
        // and the entry's is not. The first two need the brand list, which this demuxer does
        // not keep; the third is what every file in practice is, because a stsd version other
        // than 0 is vanishingly rare and a version 0 sound description has no extra fields to
        // read either way.
        if (track.StsdVersion == 0 && version > 0)
        {
            if (version == 1)
            {
                Span<byte> v1 = stackalloc byte[16];
                if (inputStream.Read(v1) == 16)
                {
                    track.AudioSamplesPerFrame = BinaryPrimitives.ReadUInt32BigEndian(v1);
                    // bytes per packet (4) is skipped, as in the C
                    track.AudioBytesPerFrame =
                        BinaryPrimitives.ReadUInt32BigEndian(v1.Slice(8, 4));
                    // bytes per sample (4) is skipped
                    bytesRead += 16;
                }
            }
            else if (version == 2)
            {
                // sizeof struct (4), sample rate as a double (8), channels (4),
                // always 0x7F000000 (4), bits (4), lpcm flags (4), bytes per frame (4),
                // samples per frame (4).
                Span<byte> v2 = stackalloc byte[36];
                if (inputStream.Read(v2) == 36)
                {
                    double rate = BitConverter.Int64BitsToDouble(
                        BinaryPrimitives.ReadInt64BigEndian(v2.Slice(4, 8)));
                    if (rate > 0) track.AudioSampleRate = (uint)rate;
                    track.AudioChannels =
                        (int)BinaryPrimitives.ReadUInt32BigEndian(v2.Slice(12, 4));
                    track.AudioBitsPerSample =
                        (int)BinaryPrimitives.ReadUInt32BigEndian(v2.Slice(20, 4));
                    track.PcmFormatFlags = (int)BinaryPrimitives.ReadUInt32BigEndian(v2.Slice(24, 4));
                    track.AudioBytesPerFrame =
                        BinaryPrimitives.ReadUInt32BigEndian(v2.Slice(28, 4));
                    track.AudioSamplesPerFrame =
                        BinaryPrimitives.ReadUInt32BigEndian(v2.Slice(32, 4));
                    bytesRead += 36;
                }
            }
        }

        long remaining = (long)header.DataSize - bytesRead;

        // Look for esds or other codec config boxes
        while (remaining > 8)
        {
            if (!TryReadBoxHeader(out var child))
                break;

            if (child.BoxType == Mp4BoxType.Esds)
            {
                track.CodecPrivate = new byte[(int)child.DataSize];
                inputStream.ReadExactly(track.CodecPrivate);
            }
            else if (child.BoxType == Mp4BoxType.Wave)
            {
                // mov_read_wave: a QuickTime 'wave' box wrapping the real configuration.
                // Its children are the same ones that can appear directly, so they are read
                // by the same loop one level down rather than by a second copy of it.
                ParseWaveBox(track, (long)child.DataSize);
            }
            else if (child.BoxType == Mp4BoxType.DOpus ||
                     child.BoxType == Mp4BoxType.DfLa ||
                     child.BoxType == Mp4BoxType.Dac3 ||
                     child.BoxType == Mp4BoxType.Dec3 ||
                     // An 'alac' sample entry nests a second 'alac' box holding the 36-byte
                     // ALACSpecificConfig. Without it there is nothing to decode from: ALAC
                     // has no in-band configuration, so the track was reaching the pipeline
                     // with no codec private data and producing no audio at all.
                     child.BoxType == Mp4BoxType.Alac)
            {
                track.CodecPrivate = new byte[(int)child.DataSize];
                inputStream.ReadExactly(track.CodecPrivate);
            }
            else if (child.BoxType == Mp4BoxType.PcmC)
            {
                ReadPcmConfiguration(track, (long)child.DataSize);
            }
            else if (child.BoxType == Mp4BoxType.Enda)
            {
                // mov.c's mov_read_enda: one 16-bit field, 1 meaning little-endian. In
                // QuickTime this usually sits inside a 'wave' box of a version-1 sound
                // description; only a direct child is read here, and a file that hides it in
                // a 'wave' keeps the byte order its four-character code implies.
                if (child.DataSize >= 2)
                {
                    Span<byte> enda = stackalloc byte[2];
                    inputStream.ReadExactly(enda);
                    track.PcmLittleEndian = BinaryPrimitives.ReadUInt16BigEndian(enda) == 1;
                    SkipBytes((long)child.DataSize - 2);
                }
                else
                {
                    SkipBytes((long)child.DataSize);
                }
            }
            else
            {
                SkipBytes((long)child.DataSize);
            }

            remaining -= child.HeaderSize + (long)child.DataSize;
        }

        if (remaining > 0)
            SkipBytes(remaining);
    }

    /// <summary>
    /// <c>mov_read_wave</c>: the children of a QuickTime <c>wave</c> box. Only the ones that
    /// carry configuration are read; everything else is skipped, which is what the C does
    /// with the 'frma' and terminator boxes a 'wave' also holds.
    /// </summary>
    private void ParseWaveBox(Mp4Track track, long size)
    {
        long end = inputStream.Position + size;

        while (end - inputStream.Position > 8 && TryReadBoxHeader(out var child))
        {
            long childEnd = inputStream.Position + (long)child.DataSize;

            if (child.BoxType == Mp4BoxType.Esds || child.BoxType == Mp4BoxType.Alac)
            {
                track.CodecPrivate = new byte[(int)child.DataSize];
                inputStream.ReadExactly(track.CodecPrivate);
            }
            else if (child.BoxType == Mp4BoxType.Enda && child.DataSize >= 2)
            {
                Span<byte> enda = stackalloc byte[2];
                inputStream.ReadExactly(enda);
                track.PcmLittleEndian = BinaryPrimitives.ReadUInt16BigEndian(enda) == 1;
            }

            inputStream.Position = childEnd;
        }

        inputStream.Position = end;
    }

    /// <summary>
    /// mov.c's <c>mov_read_pcmc</c>: the ISO 23003-5 configuration box, which is the only
    /// place an 'ipcm' or 'fpcm' sample entry states its width and its byte order.
    /// </summary>
    private void ReadPcmConfiguration(Mp4Track track, long size)
    {
        if (size < 6)
            throw new InvalidDataException("MP4: empty pcmC box.");

        Span<byte> buffer = stackalloc byte[6];
        inputStream.ReadExactly(buffer);

        int version = buffer[0];
        int flags = (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
        if (version != 0 || flags != 0)
            throw new InvalidDataException(
                $"MP4: unsupported pcmC box with version {version}, flags {flags:X}.");

        track.PcmFormatFlags = buffer[4];
        track.PcmSampleSize = buffer[5];
        // "indicates little-endian format. If not present, big-endian format is used"
        track.PcmLittleEndian = (track.PcmFormatFlags & 1) != 0;

        SkipBytes(size - 6);
    }

    private void ParseTimeToSample(Mp4BoxHeader header, Mp4Track track)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (inputStream.Read(buffer) < 8)
        {
            SkipBytes((long)header.DataSize);
            return;
        }

        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
        track.TimeToSampleEntries = new List<(uint SampleCount, uint SampleDelta)>((int)entryCount);

        for (uint i = 0; i < entryCount; i++)
        {
            if (inputStream.Read(buffer) < 8)
                break;
            uint sampleCount = BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]);
            uint sampleDelta = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
            track.TimeToSampleEntries.Add((sampleCount, sampleDelta));
        }
    }

    private void ParseCompositionOffset(Mp4BoxHeader header, Mp4Track track)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (inputStream.Read(buffer) < 8)
        {
            SkipBytes((long)header.DataSize);
            return;
        }

        byte version = buffer[0];
        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
        track.CompositionOffsetEntries = new List<(uint SampleCount, int SampleOffset)>((int)entryCount);

        for (uint i = 0; i < entryCount; i++)
        {
            if (inputStream.Read(buffer) < 8)
                break;
            uint sampleCount = BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]);
            int sampleOffset = version == 1
                ? BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(4, 4))
                : (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
            track.CompositionOffsetEntries.Add((sampleCount, sampleOffset));

            // `if (i+2<entries) mov_update_dts_shift(sc, duration, c->fc);` - mov.c:3787. The
            // last two entries are left out of the shift deliberately; the comment the C does
            // not have is that a trailing negative offset belongs to the end of the file, where
            // shifting the whole timeline for it would cost more than it saves.
            if (i + 2 < entryCount)
                MovUpdateDtsShift(track, sampleOffset);
        }
    }

    /// <summary>
    /// <c>mov_update_dts_shift</c>: how far the whole decode timeline has to move so that a
    /// negative composition offset still lands at a non-negative presentation time.
    /// </summary>
    private static void MovUpdateDtsShift(Mp4Track track, int duration)
    {
        if (duration < 0)
        {
            // int.MinValue has no positive counterpart, so the C nudges it by one rather than
            // negating it into itself.
            if (duration == int.MinValue)
                duration++;
            track.DtsShift = Math.Max(track.DtsShift, -duration);
        }
    }

    private void ParseSampleToChunk(Mp4BoxHeader header, Mp4Track track)
    {
        // Limit initial read to min of 8 (header) and dataSize
        int toRead = (int)Math.Min(8, header.DataSize);
        Span<byte> buffer = stackalloc byte[12];
        int bytesRead = inputStream.Read(buffer.Slice(0, toRead));
        if (bytesRead < 8)
        {
            SkipBytes((long)header.DataSize - bytesRead);
            return;
        }

        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
        track.SampleToChunkEntries = new List<(uint FirstChunk, uint SamplesPerChunk, uint SampleDescriptionIndex)>((int)entryCount);
        
        long bytesConsumed = bytesRead;
        long bytesRemaining = (long)header.DataSize - bytesConsumed;

        for (uint i = 0; i < entryCount && bytesRemaining >= 12; i++)
        {
            if (inputStream.Read(buffer) < 12)
                break;
            bytesConsumed += 12;
            bytesRemaining -= 12;
            uint firstChunk = BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]);
            uint samplesPerChunk = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
            uint sampleDescIdx = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4));
            track.SampleToChunkEntries.Add((firstChunk, samplesPerChunk, sampleDescIdx));
        }
        
        // Skip any remaining bytes in the box
        if (bytesRemaining > 0)
            SkipBytes(bytesRemaining);
    }

    private void ParseSampleSize(Mp4BoxHeader header, Mp4Track track)
    {
        Span<byte> buffer = stackalloc byte[12];
        if (inputStream.Read(buffer) < 12)
        {
            SkipBytes((long)header.DataSize);
            return;
        }

        uint sampleSize = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
        uint sampleCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4));

        track.SampleCount = (int)sampleCount;

        if (sampleSize != 0)
        {
            // All samples have the same size
            track.FixedSampleSize = sampleSize;
        }
        else
        {
            // Variable sample sizes
            track.SampleSizes = new uint[sampleCount];
            byte[] sizeBuffer = ArrayPool<byte>.Shared.Rent((int)sampleCount * 4);
            try
            {
                int bytesRead = inputStream.Read(sizeBuffer, 0, (int)sampleCount * 4);
                for (int i = 0; i < sampleCount && i * 4 + 4 <= bytesRead; i++)
                {
                    track.SampleSizes[i] = BinaryPrimitives.ReadUInt32BigEndian(sizeBuffer.AsSpan(i * 4, 4));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sizeBuffer);
            }
        }
    }

    private void ParseChunkOffset(Mp4BoxHeader header, Mp4Track track, bool is64Bit)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (inputStream.Read(buffer) < 8)
        {
            SkipBytes((long)header.DataSize);
            return;
        }

        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
        track.ChunkOffsets = new long[entryCount];

        int entrySize = is64Bit ? 8 : 4;
        byte[] offsetBuffer = ArrayPool<byte>.Shared.Rent((int)entryCount * entrySize);
        try
        {
            int bytesRead = inputStream.Read(offsetBuffer, 0, (int)entryCount * entrySize);
            for (int i = 0; i < entryCount && i * entrySize + entrySize <= bytesRead; i++)
            {
                track.ChunkOffsets[i] = is64Bit
                    ? (long)BinaryPrimitives.ReadUInt64BigEndian(offsetBuffer.AsSpan(i * 8, 8))
                    : BinaryPrimitives.ReadUInt32BigEndian(offsetBuffer.AsSpan(i * 4, 4));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(offsetBuffer);
        }
    }

    private void ParseSyncSample(Mp4BoxHeader header, Mp4Track track)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (inputStream.Read(buffer) < 8)
        {
            SkipBytes((long)header.DataSize);
            return;
        }

        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));

        // `if (!entries) { sc->keyframe_absent = 1; return 0; }` - mov.c:3434. An stss that
        // names no samples is the file saying it does not name keyframes, which is NOT the same
        // as the box being missing, and the two have opposite meanings for an audio track. We
        // built an empty set for it and asked that set, so no sample of the two FATE xHE-AAC
        // streams was a keyframe and nothing could seek in them. See IsSyncSample.
        if (entryCount == 0)
        {
            track.KeyframeAbsent = true;
            return;
        }

        var sampleNumbers = new int[entryCount];
        int read = 0;

        for (; read < entryCount; read++)
        {
            if (inputStream.Read(buffer[..4]) < 4)
                break;
            sampleNumbers[read] = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]);
        }

        // `key_off = (sc->keyframe_count && sc->keyframes[0] > 0)` - mov.c:4773. The numbers
        // are one-based by the standard, and the C only takes the one off when the file agrees.
        int keyOff = read > 0 && sampleNumbers[0] > 0 ? 1 : 0;

        track.SyncSamples = new HashSet<int>(read);
        for (int i = 0; i < read; i++)
            track.SyncSamples.Add(sampleNumbers[i] - keyOff);
    }

    private void ParseEdit(Mp4BoxHeader header, Mp4Track track)
    {
        long endPosition = inputStream.Position + (long)header.DataSize;

        while (inputStream.Position < endPosition)
        {
            if (!TryReadBoxHeader(out var child))
                break;

            if (child.BoxType == Mp4BoxType.EditList)
                ParseEditList(child, track);
            else
                SkipBytes((long)child.DataSize);
        }
    }

    /// <summary>
    /// <c>elst</c>: the edit list - which parts of the track's media the timeline presents, and
    /// in what order. Translated from <c>mov_read_elst</c>.
    /// </summary>
    /// <remarks>
    /// An encoder that needs priming samples writes them into the file and sets the first edit's
    /// <c>media_time</c> to where the real audio starts, which is how gapless playback works.
    /// Ignoring it does not corrupt anything, it just makes the output start too early - by 2220
    /// samples on one of the two FATE xHE-AAC streams and 2323 on the other, and by 79,232 on an
    /// audio-only file cut out of <c>Video-H264-AAC.mp4</c> with <c>-c copy</c>.
    ///
    /// <para>Every entry is read, because <see cref="MovFixIndex"/> walks all of them: an empty
    /// edit (<c>media_time -1</c>) is a gap to insert before the media rather than media to
    /// drop, and a file can hold several of either.</para>
    /// </remarks>
    private void ParseEditList(Mp4BoxHeader header, Mp4Track track)
    {
        Span<byte> buffer = stackalloc byte[20];
        if (header.DataSize < 8 || inputStream.Read(buffer[..8]) < 8)
        {
            SkipBytes((long)header.DataSize - Math.Min(8, (long)header.DataSize));
            return;
        }

        byte version = buffer[0];
        long remaining = (long)header.DataSize - 8;
        int editCount = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
        int entrySize = version == 1 ? 20 : 12;

        // "Invalid edit list entry_count" - the count the box declares is believed only as far
        // as the box's own size, which is what the C does below FF_COMPLIANCE_STRICT.
        if (remaining != (long)editCount * entrySize)
            editCount = (int)(remaining / entrySize);

        if (editCount <= 0)
        {
            SkipBytes(remaining);
            return;
        }

        var entries = new List<Mp4EditListEntry>(editCount);

        for (int i = 0; i < editCount && remaining >= entrySize; i++)
        {
            if (inputStream.Read(buffer[..entrySize]) < entrySize)
                break;
            remaining -= entrySize;

            var entry = version == 1
                ? new Mp4EditListEntry
                  {
                      Duration = BinaryPrimitives.ReadInt64BigEndian(buffer.Slice(0, 8)),
                      MediaTime = BinaryPrimitives.ReadInt64BigEndian(buffer.Slice(8, 8)),
                      Rate = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(16, 4)) / 65536.0,
                  }
                : new Mp4EditListEntry
                  {
                      Duration = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(0, 4)),
                      MediaTime = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(4, 4)),
                      Rate = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(8, 4)) / 65536.0,
                  };

            // The C refuses a negative duration outright; a negative media time other than the
            // -1 that marks an empty edit is refused only in strict mode, so it is kept here
            // and the walk treats it as media before the start of the track.
            if (entry.Duration < 0)
                break;

            entries.Add(entry);
        }

        if (entries.Count > 0)
        {
            track.EditListEntries = entries;

            // Kept for the one reading that predates the index rebuild - the first edit's
            // media time, which for audio is the priming sample count.
            if (entries[0].MediaTime > 0)
                track.EditListStartSkip = (ulong)entries[0].MediaTime;
        }

        if (remaining > 0)
            SkipBytes(remaining);
    }

    /// <summary>
    /// <c>mov_fix_index</c>: rebuilds a track's sample index so that it holds the samples the
    /// edit list selects, carrying the timestamps the edit list gives them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT THIS COST, PART ONE - THE VIDEO TIMELINE. <c>Video-H264-AAC.mp4</c> has an edit list
    /// of one entry with <c>media_time 6006</c> on its video track and <c>0</c> on its audio
    /// track - an encoder trimming the reorder delay, which is the ordinary case for H.264 in
    /// MP4. <see cref="ParseEditList"/> stored that media time and nothing read it, so every
    /// video timestamp this demuxer produced was two frame intervals too high, pts and dts
    /// alike. Nothing noticed, because nothing selected a frame BY TIMESTAMP: FrameCRC
    /// conformance compares frames in order, playback drives a clock off whatever it is given,
    /// and a <c>-t</c> window measures its duration from the first frame it keeps - so a uniform
    /// shift is invisible to all of them. The accurate-seek trim does select by timestamp, and
    /// with the shift in place <c>-ss 10</c> kept the frame at 9.943 s.
    /// </para>
    /// <para>
    /// WHAT THIS COST, PART TWO - THE AUDIO PRIMING. An encoder that needs priming samples
    /// writes them into the file and sets the first edit's <c>media_time</c> to where the real
    /// audio starts; that is how gapless playback works, and it is the ordinary case for AAC. We
    /// handed all of it to the caller. On an audio-only file cut from the same source with
    /// <c>ffmpeg -ss 5 -t 3 -map 0:a:0 -c copy</c>, whose edit list reads
    /// <c>media_time 79232</c>, ffmpeg decodes 144,000 samples and we decoded 223,232 - the
    /// difference being exactly the edit list's media time, 1.651 s of encoder warm-up played
    /// as if it were music.
    /// </para>
    /// <para>
    /// The first of those was once fixed on its own, by taking the smallest presentation time
    /// over the first 64 samples and subtracting it. That is the last six lines of this C
    /// function and nothing else, and it could not do the second: dropping priming samples is
    /// not a shift of the timeline but a rebuild of the index, which is what the rest of the
    /// function is for.
    /// </para>
    /// <para>
    /// DIVERGENCE, and there are four.
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// The C keeps composition offsets in a run-length table (<c>tts_data</c>) that this
    /// function rebuilds as it walks, carrying a (table index, sample within run) cursor pair
    /// through <c>find_prev_closest_index</c> and back. We store the offset on the index entry
    /// instead, so the cursor pair collapses into the entry index and the rebuild disappears.
    /// Four bytes per sample buys the whole of that bookkeeping.
    /// </description></item>
    /// <item><description>
    /// <c>msc->index_ranges</c> is not built. It exists so that <c>mov_read_packet</c> can walk
    /// the original sample numbers of a reordered index to find each sample's data; our entries
    /// carry their own file offset and size, so there is nothing left to look up.
    /// </description></item>
    /// <item><description>
    /// <c>st->duration</c> is not shortened to the edit list's total. Nothing here reads a
    /// per-track duration; the container's is reported instead.
    /// </description></item>
    /// <item><description>
    /// The entries are built into a <see cref="List{T}"/> and frozen into an array, rather than
    /// the C's doubling <c>av_fast_realloc</c>. Same growth, written once.
    /// </description></item>
    /// </list>
    /// <para>
    /// A track with no edit list keeps the index it always had - the sample tables, read
    /// directly - exactly as the C returns early when <c>!msc->elst_data</c>.
    /// </para>
    /// </remarks>
    private void MovFixIndex(Mp4Track track)
    {
        var editList = track.EditListEntries;
        int oldCount = track.SampleCount;

        // `if (!msc->elst_data || msc->elst_count <= 0 || nb_old <= 0) return;`
        if (editList is null || editList.Count == 0 || oldCount <= 0)
            return;

        Mp4IndexEntry[] oldIndex = BuildPreEditIndex(track, oldCount);
        var entries = new List<Mp4IndexEntry>(oldCount);

        bool isAudio = track.TrackType == Mp4TrackType.Audio;

        // mov.c excludes Vorbis from every skip_samples path: a Vorbis stream in MP4 carries
        // its own priming in the codec's blocksize rules and would have it taken off twice.
        bool skipSamplesApplies = isAudio && track.GetCodecName() != "vorbis";

        long editListDuration = 0;
        long editListDtsCounter = 0;
        long editListDtsEntryEnd = 0;
        long emptyEditsSumDuration = 0;
        long minCorrectedPts = -1;
        long skipSamples = 0;
        int firstNonZeroAudioEdit = -1;
        bool foundNonEmptyEdit = false;
        var frameDurationBuffer = new List<long>();

        // "If the dts_shift is positive (in case of negative ctts values in mov), then negate
        // the DTS by dts_shift"
        if (track.DtsShift > 0)
            editListDtsEntryEnd -= track.DtsShift;

        for (int editListIndex = 0;
             GetEditListEntry(track, editListIndex, out long editListMediaTime, out editListDuration);
             editListIndex++)
        {
            editListDtsCounter = editListDtsEntryEnd;
            editListDtsEntryEnd += editListDuration;

            if (!foundNonEmptyEdit && editListMediaTime == -1)
            {
                emptyEditsSumDuration += editListDuration;
                continue;
            }
            foundNonEmptyEdit = true;

            // "If we encounter a non-negative edit list reset the skip_samples/start_pad fields
            // and set them according to the edit list below." Only the first non-empty edit of
            // an audio track contributes priming; a later one is a cut, not a warm-up.
            if (isAudio)
            {
                firstNonZeroAudioEdit = firstNonZeroAudioEdit < 0 ? 1 : 0;
                if (firstNonZeroAudioEdit > 0)
                    skipSamples = 0;
            }

            long searchTimestamp = editListMediaTime;

            // "Audio decoders like AAC need a decoder delay samples previous to the current
            // sample, to correctly decode this frame. Hence for audio we seek to a frame 1 sec.
            // before the edit_list_media_time to cover the decoder delay."
            if (isAudio)
                searchTimestamp = Math.Max(searchTimestamp - track.Timescale, oldIndex[0].Timestamp);

            int index = FindPrevClosestIndex(track, oldIndex, oldCount, searchTimestamp, any: false);
            if (index < 0)
            {
                index = FindPrevClosestIndex(track, oldIndex, oldCount, searchTimestamp, any: true);
                if (index < 0)
                    index = 0;
            }

            bool editListStartEncountered = false;
            bool foundKeyframeAfterEdit = false;

            for (; index < oldCount; index++)
            {
                ref readonly Mp4IndexEntry current = ref oldIndex[index];

                long frameDuration = index + 1 < oldCount
                    ? oldIndex[index + 1].Timestamp - current.Timestamp
                    : editListDuration;

                bool discard = false;

                // "frames (pts) before or after edit list"
                long currentCompositionOffset = current.CompositionOffset;
                long currentCts = current.Timestamp + track.DtsShift + currentCompositionOffset;

                if (currentCts < editListMediaTime ||
                    currentCts >= editListDuration + editListMediaTime)
                {
                    if (skipSamplesApplies && currentCts < editListMediaTime &&
                        currentCts + frameDuration > editListMediaTime && firstNonZeroAudioEdit > 0)
                    {
                        // The frame the edit starts inside: keep it, and skip the samples of it
                        // that precede the edit.
                        long packetSkipSamples = editListMediaTime - currentCts;
                        skipSamples += packetSkipSamples;

                        // "Shift the index entry timestamp by packet_skip_samples to be correct."
                        editListDtsCounter -= packetSkipSamples;

                        if (!editListStartEncountered)
                        {
                            editListStartEncountered = true;
                            FixIndexEntryTimestamps(entries, editListDtsCounter, frameDurationBuffer);
                        }
                    }
                    else
                    {
                        discard = true;

                        if (!editListStartEncountered)
                        {
                            frameDurationBuffer.Add(frameDuration);

                            // A wholly discarded frame before the edit is still decoded - an
                            // audio decoder needs it for its own state - so its samples are
                            // counted into the skip as well.
                            if (skipSamplesApplies && firstNonZeroAudioEdit > 0)
                                skipSamples += frameDuration;
                        }
                    }
                }
                else
                {
                    minCorrectedPts = minCorrectedPts < 0
                        ? editListDtsCounter + currentCompositionOffset + track.DtsShift
                        : Math.Min(minCorrectedPts,
                                   editListDtsCounter + currentCompositionOffset + track.DtsShift);

                    if (!editListStartEncountered)
                    {
                        editListStartEncountered = true;

                        // "Make timestamps strictly monotonically increasing by rewriting
                        // timestamps for discarded packets."
                        FixIndexEntryTimestamps(entries, editListDtsCounter, frameDurationBuffer);
                    }
                }

                entries.Add(new Mp4IndexEntry
                {
                    Position = current.Position,
                    Timestamp = editListDtsCounter,
                    Size = current.Size,
                    CompositionOffset = current.CompositionOffset,
                    IsKeyframe = current.IsKeyframe,
                    Discard = discard,
                });

                // "Only start incrementing DTS in frame_duration amounts, when we encounter a
                // frame in edit list."
                if (editListStartEncountered)
                    editListDtsCounter += frameDuration;

                // "Break when found first key frame after edit entry completion"
                if (currentCts + frameDuration >= editListDuration + editListMediaTime &&
                    (current.IsKeyframe || isAudio))
                {
                    if (track.CompositionOffsetEntries is { Count: > 0 })
                    {
                        // "If we have CTTS and this is the first keyframe after edit elist, wait
                        // for one more, because there might be trailing B-frames after this
                        // I-frame that do belong to the edit."
                        if (!isAudio && !foundKeyframeAfterEdit)
                        {
                            foundKeyframeAfterEdit = true;
                            continue;
                        }
                    }
                    break;
                }
            }

            // `num_discarded_begin` is this list's own length here. The C resets the count at
            // the top of each edit while leaving the buffer allocated, and its next realloc
            // truncates it to the new count - so the buffer does not survive an edit either.
            frameDurationBuffer.Clear();
        }

        // "If there are empty edits, then msc->min_corrected_pts might be positive
        // intentionally. So we subtract the sum duration of empty edits here."
        minCorrectedPts -= emptyEditsSumDuration;

        // "If the minimum pts turns out to be greater than zero after fixing the index, then we
        // subtract the dts by that amount to make the first pts zero."  mov.c:4529-4536
        if (track.TrackType == Mp4TrackType.Video && minCorrectedPts > 0)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                entry.Timestamp -= minCorrectedPts;
                entries[i] = entry;
            }
        }

        track.MinCorrectedPts = minCorrectedPts;
        track.StartTime = emptyEditsSumDuration;
        track.SkipSamples = skipSamples;
        track.StartPad = skipSamples;
        track.IndexEntries = entries.ToArray();
        track.CurrentSampleIndex = 0;
    }

    /// <summary>
    /// The index <c>mov_build_index</c> leaves behind, which is the input <c>mov_fix_index</c>
    /// reorders: one entry per sample, in decode order, timestamped from <c>stts</c>.
    /// </summary>
    /// <remarks>
    /// Built in one pass over the run-length tables rather than by asking for each sample in
    /// turn. <see cref="GetSampleTime"/> and <see cref="GetCompositionOffset"/> walk their
    /// tables from the start on every call, and this file's <c>ctts</c> has 150,931 entries, so
    /// a per-sample loop over them would be quadratic.
    /// </remarks>
    private Mp4IndexEntry[] BuildPreEditIndex(Mp4Track track, int sampleCount)
    {
        var index = new Mp4IndexEntry[sampleCount];

        // `current_dts -= sc->dts_shift` - mov_build_index starts the decode clock below zero
        // when the composition offsets go negative, and mov_fix_index adds the shift back when
        // it works out a presentation time.
        long time = -track.DtsShift;
        int sample = 0;

        if (track.TimeToSampleEntries != null)
        {
            foreach (var (runLength, sampleDelta) in track.TimeToSampleEntries)
            {
                for (uint i = 0; i < runLength && sample < sampleCount; i++)
                {
                    index[sample++].Timestamp = time;
                    time += sampleDelta;
                }
            }
        }

        // An stts that runs short leaves the rest of the track at the last time it named, which
        // is what GetSampleTime returns for a sample past the end of the table.
        for (; sample < sampleCount; sample++)
            index[sample].Timestamp = time;

        sample = 0;
        if (track.CompositionOffsetEntries != null)
        {
            foreach (var (runLength, sampleOffset) in track.CompositionOffsetEntries)
            {
                for (uint i = 0; i < runLength && sample < sampleCount; i++)
                    index[sample++].CompositionOffset = sampleOffset;
            }
        }

        for (int i = 0; i < sampleCount; i++)
        {
            var (offset, size) = GetSampleInfo(track, i);
            index[i].Position = offset;
            index[i].Size = size;
            index[i].IsKeyframe = IsSyncSample(track, i);
        }

        return index;
    }

    /// <summary>
    /// <c>get_edit_list_entry</c>: one edit, with its duration converted from the movie
    /// timescale the box states it in to the track's own.
    /// </summary>
    private bool GetEditListEntry(Mp4Track track, int editListIndex,
                                  out long editListMediaTime, out long editListDuration)
    {
        editListMediaTime = 0;
        editListDuration = 0;

        if (track.EditListEntries is null || editListIndex >= track.EditListEntries.Count)
            return false;

        var entry = track.EditListEntries[editListIndex];
        editListMediaTime = entry.MediaTime;

        // "Support for mvhd.timescale = 0 with editlists" is an avpriv_request_sample in the C,
        // which stops the walk.
        if (timescale == 0)
            return false;

        editListDuration = AvRescale(entry.Duration, track.Timescale, timescale);

        // `if (*edit_list_duration + (uint64_t)*edit_list_media_time > INT64_MAX)` - an
        // overflow guard whose arithmetic is UNSIGNED, and the cast is load-bearing. An empty
        // edit's media time is -1, which as a uint64 is every bit set, so the sum wraps back
        // to just under the duration and the guard does not fire. Written as signed C# it
        // fires on every empty edit, which zeroes the gap the edit exists to describe: on
        // h264-1080p-high.mp4, whose edit list is an empty 512-unit edit followed by the
        // media, that put every timestamp in the file 512 units low.
        if ((ulong)editListDuration + (ulong)editListMediaTime > long.MaxValue)
            editListDuration = 0;

        return true;
    }

    /// <summary>
    /// <c>av_rescale</c> with the default rounding: to nearest, halves away from zero. The
    /// product is formed in 128 bits because a long duration times a 90 kHz timescale overflows
    /// 64 well before any file does.
    /// </summary>
    private static long AvRescale(long value, long multiplier, long divisor)
    {
        if (divisor == 0)
            return 0;

        Int128 product = (Int128)value * multiplier;
        Int128 half = divisor / 2;
        return (long)((product >= 0 ? product + half : product - half) / divisor);
    }

    /// <summary>
    /// <c>find_prev_closest_index</c>: the entry at or before a presentation time that a decode
    /// can start from - a keyframe, unless <paramref name="any"/> says any entry will do.
    /// </summary>
    private int FindPrevClosestIndex(Mp4Track track, Mp4IndexEntry[] oldIndex, int count,
                                     long timestampPts, bool any)
    {
        // "If dts_shift > 0, then all the index timestamps will have to be offset by at least
        // dts_shift amount to obtain PTS. Hence we decrement the searched timestamp_pts by
        // dts_shift to find the closest index element."
        if (track.DtsShift > 0)
            timestampPts -= track.DtsShift;

        int index = IndexSearchTimestamp(oldIndex, count, timestampPts, backward: true, any);

        // "Keep going backwards in the index entries until the timestamp is the same."
        if (index >= 0)
        {
            for (int i = index; i > 0 && oldIndex[i].Timestamp == oldIndex[i - 1].Timestamp; i--)
            {
                if (any || oldIndex[i - 1].IsKeyframe)
                    index = i - 1;
            }
        }

        // "If we have CTTS then refine the search, by searching backwards over PTS computed by
        // adding corresponding CTTS durations to index timestamps."
        if (track.CompositionOffsetEntries is { Count: > 0 } && index >= 0)
        {
            while (index >= 0 &&
                   !(oldIndex[index].Timestamp + oldIndex[index].CompositionOffset <= timestampPts
                     && oldIndex[index].IsKeyframe))
            {
                index--;
            }
        }

        return index;
    }

    /// <summary>
    /// <c>ff_index_search_timestamp</c>: a binary search over index entries that steps past
    /// discarded ones and, unless asked otherwise, lands on a keyframe.
    /// </summary>
    private static int IndexSearchTimestamp(Mp4IndexEntry[] entries, int entryCount,
                                            long wantedTimestamp, bool backward, bool any)
    {
        int a = -1;
        int b = entryCount;

        // "Optimize appending index entries at the end."
        if (b > 0 && entries[b - 1].Timestamp < wantedTimestamp)
            a = b - 1;

        while (b - a > 1)
        {
            int m = (a + b) >> 1;

            // "Search for the next non-discarded packet."
            while (entries[m].Discard && m < b && m < entryCount - 1)
            {
                m++;
                if (m == b && entries[m].Timestamp >= wantedTimestamp)
                {
                    m = b - 1;
                    break;
                }
            }

            long timestamp = entries[m].Timestamp;
            if (timestamp >= wantedTimestamp)
                b = m;
            if (timestamp <= wantedTimestamp)
                a = m;
        }

        int found = backward ? a : b;

        if (!any)
        {
            while (found >= 0 && found < entryCount && !entries[found].IsKeyframe)
                found += backward ? -1 : 1;
        }

        return found == entryCount ? -1 : found;
    }

    /// <summary>
    /// <c>fix_index_entry_timestamps</c>: walks back over the entries discarded before the edit
    /// started and spaces their timestamps out behind the first kept one, so the index stays
    /// strictly increasing.
    /// </summary>
    /// <remarks>
    /// They are discarded, so nothing presents them; they are in the index because the decoder
    /// needs them, and a seek that binary-searches past a run of equal timestamps would not
    /// find its way back to the right one.
    /// </remarks>
    private static void FixIndexEntryTimestamps(List<Mp4IndexEntry> entries, long endTimestamp,
                                                List<long> frameDurationBuffer)
    {
        int endIndex = entries.Count;

        for (int i = 0; i < frameDurationBuffer.Count; i++)
        {
            endTimestamp -= frameDurationBuffer[frameDurationBuffer.Count - 1 - i];

            var entry = entries[endIndex - 1 - i];
            entry.Timestamp = endTimestamp;
            entries[endIndex - 1 - i] = entry;
        }

        frameDurationBuffer.Clear();
    }

    /// <summary>
    /// <c>mov_get_skip_samples</c>: how much of the priming is still ahead of a given entry, so
    /// that a seek into the middle of it skips only what it has not already passed.
    /// </summary>
    private static long MovGetSkipSamples(Mp4Track track, int entryIndex)
    {
        if (track.TrackType != Mp4TrackType.Audio || track.IndexEntries is not { Length: > 0 } index)
            return 0;

        long firstTimestamp = index[0].Timestamp;
        long timestamp = index[Math.Clamp(entryIndex, 0, index.Length - 1)].Timestamp;

        // "compute skip samples according to stream start_pad, seek ts and first ts" - the
        // track's timescale IS the sample rate for audio, so the rescale is the identity and
        // the subtraction is already in samples.
        long off = track.Timescale == track.AudioSampleRate || track.AudioSampleRate == 0
            ? timestamp - firstTimestamp
            : AvRescale(timestamp - firstTimestamp, track.AudioSampleRate, track.Timescale);

        return Math.Max(track.StartPad - off, 0);
    }

    private void BuildSampleIndex(Mp4Track track)
    {
        if (track.ChunkOffsets == null || track.SampleToChunkEntries == null)
            return;

        int totalSamples = track.SampleCount;
        track.SampleOffsets = new long[totalSamples];

        int sampleIndex = 0;
        int chunkIndex = 0;
        int stscIndex = 0;

        while (sampleIndex < totalSamples && chunkIndex < track.ChunkOffsets.Length)
        {
            // An stsc entry applies from its first_chunk up to the chunk before the next entry's
            // first_chunk. Those numbers are one-based, so the chunk being filled here is
            // chunkIndex + 1, and the entry changes when that reaches the next entry's
            // first_chunk - not one chunk earlier. Advancing early gave the first chunk the wrong
            // samples-per-chunk and put every sample after it at the wrong file offset. A while
            // loop rather than an if, since an entry can cover no chunks at all.
            while (stscIndex + 1 < track.SampleToChunkEntries.Count &&
                   chunkIndex + 1 >= track.SampleToChunkEntries[stscIndex + 1].FirstChunk)
            {
                stscIndex++;
            }

            uint samplesPerChunk = track.SampleToChunkEntries[stscIndex].SamplesPerChunk;

            // Calculate sample offsets within chunk
            long offset = track.ChunkOffsets[chunkIndex];
            for (int i = 0; i < samplesPerChunk && sampleIndex < totalSamples; i++)
            {
                track.SampleOffsets[sampleIndex] = offset;
                offset += GetSampleSize(track, sampleIndex);
                sampleIndex++;
            }

            chunkIndex++;
        }
    }

    private uint GetSampleSize(Mp4Track track, int sampleIndex)
    {
        if (track.FixedSampleSize > 0)
            return track.FixedSampleSize;
        return track.SampleSizes != null && sampleIndex < track.SampleSizes.Length
            ? track.SampleSizes[sampleIndex]
            : 0;
    }

    private (long Offset, int Size) GetSampleInfo(Mp4Track track, int sampleIndex)
    {
        long offset = track.SampleOffsets != null && sampleIndex < track.SampleOffsets.Length
            ? track.SampleOffsets[sampleIndex]
            : 0;
        int size = (int)GetSampleSize(track, sampleIndex);
        return (offset, size);
    }

    private long GetSampleTime(Mp4Track track, int sampleIndex)
    {
        if (track.TimeToSampleEntries == null)
            return 0;

        long time = 0;
        int currentSample = 0;

        foreach (var (sampleCount, sampleDelta) in track.TimeToSampleEntries)
        {
            if (currentSample + sampleCount > sampleIndex)
            {
                time += (sampleIndex - currentSample) * sampleDelta;
                break;
            }
            time += sampleCount * sampleDelta;
            currentSample += (int)sampleCount;
        }

        return time;
    }

    private int GetCompositionOffset(Mp4Track track, int sampleIndex)
    {
        if (track.CompositionOffsetEntries == null)
            return 0;

        int currentSample = 0;
        foreach (var (sampleCount, sampleOffset) in track.CompositionOffsetEntries)
        {
            if (currentSample + sampleCount > sampleIndex)
            {
                return sampleOffset;
            }
            currentSample += (int)sampleCount;
        }

        return 0;
    }

    /// <summary>
    /// <c>mov_build_index</c>'s keyframe decision, for the boxes this demuxer reads.
    /// </summary>
    /// <remarks>
    /// <c>stps</c> (partial sync samples) and <c>sdtp</c>'s RAP groups also name keyframes in
    /// the C; neither box is parsed here, so a file that names its keyframes only that way is
    /// read as if it named none.
    /// </remarks>
    private static bool IsSyncSample(Mp4Track track, int sampleIndex)
    {
        if (track.SyncSamples is { Count: > 0 } syncSamples)
            return syncSamples.Contains(sampleIndex);

        // `if (sc->keyframe_absent && !sc->stps_count && !rap_group_present &&
        //     (codec_type == AVMEDIA_TYPE_AUDIO || (i==0 && j==0))) keyframe = 1;`
        // An stss with no entries: every audio sample is a keyframe, and a video track gets
        // its first sample and nothing else.
        if (track.KeyframeAbsent)
            return track.TrackType == Mp4TrackType.Audio || sampleIndex == 0;

        // No stss box at all - `!sc->keyframe_absent && !sc->keyframe_count` is true for every
        // sample, so every sample is a keyframe.
        return true;
    }

    private int FindSampleAtTime(Mp4Track track, long targetTime)
    {
        if (track.TimeToSampleEntries == null)
            return 0;

        long time = 0;
        int sampleIndex = 0;

        foreach (var (sampleCount, sampleDelta) in track.TimeToSampleEntries)
        {
            long entryDuration = sampleCount * sampleDelta;
            if (time + entryDuration > targetTime)
            {
                sampleIndex += (int)((targetTime - time) / sampleDelta);
                break;
            }
            time += entryDuration;
            sampleIndex += (int)sampleCount;
        }

        return Math.Min(sampleIndex, track.SampleCount - 1);
    }

    private int FindPrecedingSyncSample(Mp4Track track, int sampleIndex)
    {
        if (track.SyncSamples == null)
            return sampleIndex;

        int bestSync = 0;
        foreach (int sync in track.SyncSamples.OrderBy(s => s))
        {
            if (sync > sampleIndex)
                break;
            bestSync = sync;
        }

        return bestSync;
    }

    private long NormalizeToMovieTimescale(long time, uint trackTimescale)
    {
        if (trackTimescale == timescale || trackTimescale == 0)
            return time;
        return time * timescale / trackTimescale;
    }

    private static long TimescaleToTicks(long time, uint trackTimescale)
    {
        if (trackTimescale == 0)
            return 0;
        return time * TimeSpan.TicksPerSecond / trackTimescale;
    }

    private bool TryReadBoxHeader(out Mp4BoxHeader header)
    {
        header = default;

        Span<byte> buffer = stackalloc byte[16];
        int bytesRead = inputStream.Read(buffer[..8]);
        if (bytesRead < 8)
            return false;

        uint size = BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]);
        uint boxType = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));

        ulong dataSize;
        int headerSize = 8;

        if (size == 1)
        {
            // 64-bit extended size
            bytesRead = inputStream.Read(buffer.Slice(8, 8));
            if (bytesRead < 8)
                return false;
            ulong extendedSize = BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(8, 8));
            dataSize = extendedSize - 16;
            headerSize = 16;
        }
        else if (size == 0)
        {
            // Size extends to end of file
            dataSize = ulong.MaxValue;
        }
        else
        {
            dataSize = size - 8;
        }

        header = new Mp4BoxHeader(boxType, dataSize, headerSize);
        return true;
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
    
    #region Fragmented MP4 Parsing
    
    /// <summary>
    /// Parses a Movie Fragment (moof) box.
    /// </summary>
    private void ParseMovieFragment(Mp4BoxHeader moofHeader, long moofPosition)
    {
        long endPosition = inputStream.Position + (long)moofHeader.DataSize;
        var fragment = new MovieFragment { FilePosition = moofPosition };
        
        while (inputStream.Position < endPosition)
        {
            if (!TryReadBoxHeader(out var child))
                break;
            
            switch (child.BoxType)
            {
                case Mp4BoxType.MovieFragmentHeader:
                    ParseMovieFragmentHeader(child, fragment);
                    break;
                    
                case Mp4BoxType.TrackFragment:
                    var trackFragment = ParseTrackFragment(child);
                    if (trackFragment != null)
                        fragment.TrackFragments.Add(trackFragment);
                    break;
                    
                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }
        
        fragments.Add(fragment);
    }
    
    /// <summary>
    /// Parses Movie Fragment Header (mfhd) - contains sequence number.
    /// </summary>
    private void ParseMovieFragmentHeader(Mp4BoxHeader header, MovieFragment fragment)
    {
        Span<byte> buffer = stackalloc byte[8];
        int bytesRead = inputStream.Read(buffer[..8]);
        if (bytesRead < 8)
            return;
        
        // Skip version (1) and flags (3)
        fragment.SequenceNumber = BinaryPrimitives.ReadUInt32BigEndian(buffer[4..]);
    }
    
    /// <summary>
    /// Parses Track Fragment (traf) box.
    /// </summary>
    private TrackFragment? ParseTrackFragment(Mp4BoxHeader trafHeader)
    {
        long endPosition = inputStream.Position + (long)trafHeader.DataSize;
        var trackFragment = new TrackFragment();
        
        while (inputStream.Position < endPosition)
        {
            if (!TryReadBoxHeader(out var child))
                break;
            
            switch (child.BoxType)
            {
                case Mp4BoxType.TrackFragmentHeader:
                    ParseTrackFragmentHeader(child, trackFragment.Header);
                    break;
                    
                case Mp4BoxType.TrackFragmentDecodeTime:
                    trackFragment.BaseMediaDecodeTime = ParseTrackFragmentDecodeTime(child);
                    break;
                    
                case Mp4BoxType.TrackRun:
                    var run = ParseTrackRun(child);
                    if (run != null)
                        trackFragment.Runs.Add(run);
                    break;
                    
                default:
                    SkipBytes((long)child.DataSize);
                    break;
            }
        }
        
        return trackFragment;
    }
    
    /// <summary>
    /// Parses Track Fragment Header (tfhd).
    /// </summary>
    private void ParseTrackFragmentHeader(Mp4BoxHeader header, TrackFragmentHeader tfhd)
    {
        // Minimum 8 bytes: version (1) + flags (3) + track_id (4)
        Span<byte> versionFlags = stackalloc byte[4];
        if (inputStream.Read(versionFlags) < 4)
            return;
        
        uint flags = (uint)((versionFlags[1] << 16) | (versionFlags[2] << 8) | versionFlags[3]);
        
        Span<byte> trackIdBuf = stackalloc byte[4];
        if (inputStream.Read(trackIdBuf) < 4)
            return;
        tfhd.TrackId = BinaryPrimitives.ReadUInt32BigEndian(trackIdBuf);
        
        // Parse optional fields based on flags
        tfhd.HasBaseDataOffset = (flags & 0x000001) != 0;
        tfhd.HasSampleDescriptionIndex = (flags & 0x000002) != 0;
        tfhd.HasDefaultSampleDuration = (flags & 0x000008) != 0;
        tfhd.HasDefaultSampleSize = (flags & 0x000010) != 0;
        tfhd.HasDefaultSampleFlags = (flags & 0x000020) != 0;
        tfhd.DurationIsEmpty = (flags & 0x010000) != 0;
        tfhd.DefaultBaseIsMoof = (flags & 0x020000) != 0;
        
        Span<byte> buf = stackalloc byte[8];
        
        if (tfhd.HasBaseDataOffset)
        {
            if (inputStream.Read(buf[..8]) >= 8)
                tfhd.BaseDataOffset = BinaryPrimitives.ReadInt64BigEndian(buf);
        }
        
        if (tfhd.HasSampleDescriptionIndex)
        {
            if (inputStream.Read(buf[..4]) >= 4)
                tfhd.SampleDescriptionIndex = BinaryPrimitives.ReadUInt32BigEndian(buf);
        }
        
        if (tfhd.HasDefaultSampleDuration)
        {
            if (inputStream.Read(buf[..4]) >= 4)
                tfhd.DefaultSampleDuration = BinaryPrimitives.ReadUInt32BigEndian(buf);
        }
        
        if (tfhd.HasDefaultSampleSize)
        {
            if (inputStream.Read(buf[..4]) >= 4)
                tfhd.DefaultSampleSize = BinaryPrimitives.ReadUInt32BigEndian(buf);
        }
        
        if (tfhd.HasDefaultSampleFlags)
        {
            if (inputStream.Read(buf[..4]) >= 4)
                tfhd.DefaultSampleFlags = BinaryPrimitives.ReadUInt32BigEndian(buf);
        }
    }
    
    /// <summary>
    /// Parses Track Fragment Decode Time (tfdt).
    /// </summary>
    private ulong ParseTrackFragmentDecodeTime(Mp4BoxHeader header)
    {
        Span<byte> versionFlags = stackalloc byte[4];
        if (inputStream.Read(versionFlags) < 4)
            return 0;
        
        byte version = versionFlags[0];
        
        if (version == 1)
        {
            Span<byte> buf = stackalloc byte[8];
            if (inputStream.Read(buf) >= 8)
                return BinaryPrimitives.ReadUInt64BigEndian(buf);
        }
        else
        {
            Span<byte> buf = stackalloc byte[4];
            if (inputStream.Read(buf) >= 4)
                return BinaryPrimitives.ReadUInt32BigEndian(buf);
        }
        
        return 0;
    }
    
    /// <summary>
    /// Parses Track Run (trun) - contains per-sample information.
    /// </summary>
    private TrackFragmentRun? ParseTrackRun(Mp4BoxHeader header)
    {
        Span<byte> versionFlags = stackalloc byte[4];
        if (inputStream.Read(versionFlags) < 4)
            return null;
        
        byte version = versionFlags[0];
        uint flags = (uint)((versionFlags[1] << 16) | (versionFlags[2] << 8) | versionFlags[3]);
        
        var run = new TrackFragmentRun
        {
            HasDataOffset = (flags & 0x000001) != 0,
            HasFirstSampleFlags = (flags & 0x000004) != 0,
            HasSampleDuration = (flags & 0x000100) != 0,
            HasSampleSize = (flags & 0x000200) != 0,
            HasSampleFlags = (flags & 0x000400) != 0,
            HasSampleCompositionTimeOffset = (flags & 0x000800) != 0
        };
        
        Span<byte> buf = stackalloc byte[4];
        
        // Sample count (required)
        if (inputStream.Read(buf) < 4)
            return null;
        run.SampleCount = BinaryPrimitives.ReadUInt32BigEndian(buf);
        
        // Data offset (optional)
        if (run.HasDataOffset)
        {
            if (inputStream.Read(buf) >= 4)
                run.DataOffset = BinaryPrimitives.ReadInt32BigEndian(buf);
        }
        
        // First sample flags (optional)
        if (run.HasFirstSampleFlags)
        {
            if (inputStream.Read(buf) >= 4)
                run.FirstSampleFlags = BinaryPrimitives.ReadUInt32BigEndian(buf);
        }
        
        // Per-sample data
        for (uint i = 0; i < run.SampleCount; i++)
        {
            var sample = new TrackRunSample();
            
            if (run.HasSampleDuration)
            {
                if (inputStream.Read(buf) >= 4)
                    sample.Duration = BinaryPrimitives.ReadUInt32BigEndian(buf);
            }
            
            if (run.HasSampleSize)
            {
                if (inputStream.Read(buf) >= 4)
                    sample.Size = BinaryPrimitives.ReadUInt32BigEndian(buf);
            }
            
            if (run.HasSampleFlags)
            {
                if (inputStream.Read(buf) >= 4)
                    sample.Flags = BinaryPrimitives.ReadUInt32BigEndian(buf);
            }
            else if (i == 0 && run.HasFirstSampleFlags)
            {
                sample.Flags = run.FirstSampleFlags;
            }
            
            if (run.HasSampleCompositionTimeOffset)
            {
                if (inputStream.Read(buf) >= 4)
                {
                    // Version 0: unsigned, Version 1: signed
                    sample.CompositionTimeOffset = version == 0
                        ? (int)BinaryPrimitives.ReadUInt32BigEndian(buf)
                        : BinaryPrimitives.ReadInt32BigEndian(buf);
                }
            }
            
            run.Samples.Add(sample);
        }
        
        return run;
    }
    
    #endregion

    public void Dispose()
    {
        if (isDisposed)
            return;
        isDisposed = true;
        ArrayPool<byte>.Shared.Return(readBuffer);
    }
}

/// <summary>
/// Parsed MP4 box header.
/// </summary>
public readonly struct Mp4BoxHeader
{
    public readonly uint BoxType;
    public readonly ulong DataSize;
    public readonly int HeaderSize;

    public Mp4BoxHeader(uint boxType, ulong dataSize, int headerSize)
    {
        BoxType = boxType;
        DataSize = dataSize;
        HeaderSize = headerSize;
    }
}

/// <summary>
/// Represents a track discovered in the MP4 file.
/// </summary>
public sealed class Mp4Track
{
    public uint TrackId { get; set; }
    public Mp4TrackType TrackType { get; set; }
    public uint Timescale { get; set; }
    public ulong Duration { get; set; }
    public ulong MediaDuration { get; set; }

    /// <summary>
    /// How much media the edit list says to discard from the start, in this track's timescale -
    /// so for audio, a number of samples. Zero when there is no edit list or it starts at zero.
    /// </summary>
    public ulong EditListStartSkip { get; set; }

    /// <summary>
    /// <c>sc-&gt;elst_data</c>: every entry of the track's edit list, or null when it has none.
    /// </summary>
    public List<Mp4EditListEntry>? EditListEntries { get; set; }

    /// <summary>
    /// The track's sample index after <see cref="Mp4Demuxer.MovFixIndex"/> has rebuilt it to
    /// the edit list's timeline, or null when the track has no edit list and the sample tables
    /// are read directly.
    /// </summary>
    public Mp4IndexEntry[]? IndexEntries { get; set; }

    /// <summary>
    /// <c>sc-&gt;dts_shift</c>: how far the decode timeline moves to keep presentation times
    /// non-negative when the composition offsets go negative.
    /// </summary>
    public int DtsShift { get; set; }

    /// <summary>
    /// <c>sc-&gt;min_corrected_pts</c>: the smallest presentation time the edit list keeps, in
    /// this track's timescale. Subtracted from a video track's index timestamps, and taken back
    /// off a requested timestamp when seeking.
    /// </summary>
    public long MinCorrectedPts { get; set; }

    /// <summary>
    /// <c>st-&gt;start_time</c>: the total duration of the empty edits before the media starts.
    /// </summary>
    public long StartTime { get; set; }

    /// <summary>
    /// <c>sti-&gt;skip_samples</c>: how many samples of decoded audio the edit list says to
    /// throw away at the start of the track - an encoder's priming.
    /// </summary>
    public long SkipSamples { get; set; }

    /// <summary>
    /// <c>sc-&gt;start_pad</c>: the same figure as <see cref="SkipSamples"/>, kept because a
    /// seek recomputes the skip from it rather than from the running count.
    /// </summary>
    public long StartPad { get; set; }

    // Video properties
    public int Width { get; set; }
    public int Height { get; set; }
    
    // Audio properties
    /// <summary>
    /// <c>codecpar-&gt;bits_per_coded_sample</c> of a video track - the sample description's
    /// depth field. Masked to its low five bits when the track turns out to be paletted,
    /// which is what <c>mov_parse_stsd_video</c> does after <c>ff_get_qtpalette</c> says so.
    /// </summary>
    public int BitsPerCodedSample { get; set; }

    /// <summary>
    /// <c>sc-&gt;palette</c> - 256 BGRA entries, or null when the track is not paletted.
    /// A paletted QuickTime track carries its palette in the video sample description and
    /// nowhere else, so a demuxer that does not read it leaves the decoder with indices and
    /// no colours.
    /// </summary>
    public uint[]? Palette { get; set; }

    public int AudioChannels { get; set; }
    public int AudioBitsPerSample { get; set; }
    public uint AudioSampleRate { get; set; }
    
    // Codec info
    public uint CodecFourCC { get; set; }

    /// <summary>The 'pcmC' box's format flags; bit 0 means little-endian.</summary>
    public int PcmFormatFlags { get; set; }

    /// <summary>
    /// The <c>stsd</c> box's own version byte. <c>mov_parse_stsd_audio</c> needs it: a
    /// version-1 sound description's four extra fields are only present when this is 0.
    /// </summary>
    public int StsdVersion { get; set; }

    /// <summary>
    /// <c>samples_per_frame</c> from a version 1 or 2 sound description, or 0.
    /// </summary>
    public uint AudioSamplesPerFrame { get; set; }

    /// <summary>
    /// <c>bytes_per_frame</c> from a version 1 or 2 sound description, or 0.
    /// </summary>
    public uint AudioBytesPerFrame { get; set; }

    /// <summary>Bits per sample from the 'pcmC' box, zero when the track has none.</summary>
    public int PcmSampleSize { get; set; }

    /// <summary>
    /// True when a 'pcmC' or 'enda' box said the samples are little-endian. An ISO PCM sample
    /// entry is big-endian unless one of those says otherwise.
    /// </summary>
    public bool PcmLittleEndian { get; set; }
    public byte[]? CodecPrivate { get; set; }
    
    // Sample tables
    public int SampleCount { get; set; }
    public uint FixedSampleSize { get; set; }
    public uint[]? SampleSizes { get; set; }
    public long[]? ChunkOffsets { get; set; }
    public long[]? SampleOffsets { get; set; }
    public List<(uint SampleCount, uint SampleDelta)>? TimeToSampleEntries { get; set; }
    public List<(uint SampleCount, int SampleOffset)>? CompositionOffsetEntries { get; set; }
    public List<(uint FirstChunk, uint SamplesPerChunk, uint SampleDescriptionIndex)>? SampleToChunkEntries { get; set; }
    public HashSet<int>? SyncSamples { get; set; }

    /// <summary>
    /// <c>sc-&gt;keyframe_absent</c>: the track has an <c>stss</c> box that names no samples,
    /// which is the file declining to name keyframes rather than saying there are none.
    /// </summary>
    public bool KeyframeAbsent { get; set; }
    
    // Playback state
    public int CurrentSampleIndex { get; set; }

    public string GetCodecName()
    {
        // 'raw ' is in BOTH of isom_tags.c's tables - AV_CODEC_ID_RAWVIDEO in
        // ff_codec_movvideo_tags and AV_CODEC_ID_PCM_U8 in ff_codec_movaudio_tags - and
        // mov.c picks the table by the TRACK's handler, not by the tag. Without this test an
        // uncompressed QuickTime video track is named pcm_u8 and finds the PCM decoder,
        // which is the worst of the three outcomes: not a refusal, not a picture, but a
        // track that opens and hands out noise as audio.
        if (TrackType == Mp4TrackType.Video && CodecFourCC == Mp4BoxType.RawPcm)
            return "rawvideo";

        return CodecFourCC switch
        {
            Mp4BoxType.Avc1 or Mp4BoxType.Avc2 or Mp4BoxType.Avc3 or Mp4BoxType.Avc4 => "h264",
            Mp4BoxType.Hvc1 or Mp4BoxType.Hev1 => "hevc",
            Mp4BoxType.Vp08 => "vp8",
            Mp4BoxType.Vp09 => "vp9",
            Mp4BoxType.Av01 => "av1",
            Mp4BoxType.Mp4v => "mpeg4",

            // The MJPEG family, from isom_tags.c. Five tags are one codec, and the other two
            // names are different bitstreams that happen to live next door: 'mjpb' is
            // Motion-JPEG format B, which has its own decoder in ffmpeg, and the Media 100
            // tags a fourth. Naming them correctly is worth doing even with no decoder behind
            // them - a track named something ffprobe does not name is a track that may find
            // the WRONG decoder, which is worse than finding none.
            Mp4BoxType.Jpeg or Mp4BoxType.Mjpa or Mp4BoxType.Avdj or Mp4BoxType.Avrn
                or Mp4BoxType.Dmb1 => "mjpeg",
            Mp4BoxType.Mjpb => "mjpegb",
            Mp4BoxType.Cvid => "cinepak",
            Mp4BoxType.M100_601N or Mp4BoxType.M100_601P or Mp4BoxType.M100_dtnt
                or Mp4BoxType.M100_dtNT or Mp4BoxType.M100_dtpa or Mp4BoxType.M100_dtPA
                => "media100",
            Mp4BoxType.Mp4a => "aac",
            Mp4BoxType.Ac3 => "ac3",
            Mp4BoxType.Ec3 => "eac3",
            Mp4BoxType.Opus => "opus",
            Mp4BoxType.Flac => "flac",
            Mp4BoxType.Alac => "alac",

            // Raw PCM. The two ISO sample entries say nothing on their own - the width and the
            // byte order are in the 'pcmC' box - and the QuickTime ones carry the width in the
            // tag, with 'enda' able to flip the byte order. A width neither states is a file
            // this cannot read, and it says so rather than naming a codec that does not exist.
            Mp4BoxType.Ipcm => PcmSampleSize switch
            {
                16 => PcmLittleEndian ? "pcm_s16le" : "pcm_s16be",
                24 => PcmLittleEndian ? "pcm_s24le" : "pcm_s24be",
                32 => PcmLittleEndian ? "pcm_s32le" : "pcm_s32be",
                _ => throw new NotSupportedException(
                    $"MP4: 'ipcm' with pcm_sample_size {PcmSampleSize}."),
            },
            Mp4BoxType.Fpcm => PcmSampleSize switch
            {
                32 => PcmLittleEndian ? "pcm_f32le" : "pcm_f32be",
                64 => PcmLittleEndian ? "pcm_f64le" : "pcm_f64be",
                _ => throw new NotSupportedException(
                    $"MP4: 'fpcm' with pcm_sample_size {PcmSampleSize}."),
            },
            Mp4BoxType.Sowt => AudioBitsPerSample == 8 ? "pcm_s8" : "pcm_s16le",
            Mp4BoxType.Twos => AudioBitsPerSample == 8 ? "pcm_s8" : "pcm_s16be",
            Mp4BoxType.RawPcm or Mp4BoxType.NonePcm => "pcm_u8",
            Mp4BoxType.In24 => PcmLittleEndian ? "pcm_s24le" : "pcm_s24be",
            Mp4BoxType.In32 => PcmLittleEndian ? "pcm_s32le" : "pcm_s32be",
            Mp4BoxType.Fl32 => PcmLittleEndian ? "pcm_f32le" : "pcm_f32be",
            Mp4BoxType.Fl64 => PcmLittleEndian ? "pcm_f64le" : "pcm_f64be",
            Mp4BoxType.Alaw => "pcm_alaw",
            Mp4BoxType.Ulaw => "pcm_mulaw",

            _ => Mp4BoxType.FourCCToString(CodecFourCC)
        };
    }
}

/// <summary>
/// Represents a sample packet from an MP4 file.
/// </summary>
public struct Mp4Packet
{
    public int TrackIndex;
    public long DecodeTimeTicks;
    public long PresentationTimeTicks;
    public bool IsKeyframe;
    public long PayloadPosition;
    public int PayloadSize;

    /// <summary>
    /// <c>AV_PKT_FLAG_DISCARD</c>: the edit list does not present this sample. It is handed out
    /// anyway because the samples that are presented may need it to decode, and the caller
    /// drops the frame it produces.
    /// </summary>
    public bool IsDiscard;
}

/// <summary>
/// One entry of an <c>elst</c> box: <c>MOVElst</c>.
/// </summary>
public struct Mp4EditListEntry
{
    /// <summary>segment_duration, in the MOVIE timescale.</summary>
    public long Duration;

    /// <summary>
    /// media_time, in the TRACK's timescale - where in the media this edit starts.
    /// <c>-1</c> marks an empty edit, which presents nothing for its duration.
    /// </summary>
    public long MediaTime;

    /// <summary>media_rate, from the 16.16 fixed-point field.</summary>
    public double Rate;
}

/// <summary>
/// One sample of a track whose index the edit list rebuilt: <c>AVIndexEntry</c>, plus the
/// composition offset that the C keeps in a parallel run-length table.
/// </summary>
/// <remarks>
/// <c>AVIndexEntry.min_distance</c> is not carried: it exists so a binary search can bound how
/// far a keyframe may be from a position, and the searches here work on an index that holds
/// every sample, where that bound is never consulted.
/// </remarks>
public struct Mp4IndexEntry
{
    /// <summary>The sample's first byte in the file.</summary>
    public long Position;

    /// <summary>Decode timestamp on the edit list's timeline, in the track's timescale.</summary>
    public long Timestamp;

    public int Size;

    /// <summary><c>ctts</c>: presentation time is <see cref="Timestamp"/> plus this.</summary>
    public int CompositionOffset;

    public bool IsKeyframe;

    /// <summary><c>AVINDEX_DISCARD_FRAME</c>: decoded, but not presented.</summary>
    public bool Discard;
}

/// <summary>
/// Track Fragment Header (tfhd) - default values for samples in a fragment.
/// </summary>
public sealed class TrackFragmentHeader
{
    public uint TrackId { get; set; }
    public long BaseDataOffset { get; set; }
    public uint SampleDescriptionIndex { get; set; }
    public uint DefaultSampleDuration { get; set; }
    public uint DefaultSampleSize { get; set; }
    public uint DefaultSampleFlags { get; set; }
    
    // Flags indicating which fields are present
    public bool HasBaseDataOffset { get; set; }
    public bool HasSampleDescriptionIndex { get; set; }
    public bool HasDefaultSampleDuration { get; set; }
    public bool HasDefaultSampleSize { get; set; }
    public bool HasDefaultSampleFlags { get; set; }
    public bool DurationIsEmpty { get; set; }
    public bool DefaultBaseIsMoof { get; set; }
}

/// <summary>
/// Track Run (trun) - sample information for a run of samples.
/// </summary>
public sealed class TrackFragmentRun
{
    public uint SampleCount { get; set; }
    public int DataOffset { get; set; }
    public uint FirstSampleFlags { get; set; }
    public List<TrackRunSample> Samples { get; } = [];
    
    // Flags indicating which fields are present
    public bool HasDataOffset { get; set; }
    public bool HasFirstSampleFlags { get; set; }
    public bool HasSampleDuration { get; set; }
    public bool HasSampleSize { get; set; }
    public bool HasSampleFlags { get; set; }
    public bool HasSampleCompositionTimeOffset { get; set; }
}

/// <summary>
/// Individual sample data within a Track Run.
/// </summary>
public struct TrackRunSample
{
    public uint Duration;
    public uint Size;
    public uint Flags;
    public int CompositionTimeOffset;
    
    /// <summary>
    /// Returns true if this sample is a sync sample (keyframe).
    /// Sample flags bit 16-17: sample_is_non_sync_sample (1 = not sync)
    /// </summary>
    public readonly bool IsSyncSample => (Flags & 0x00010000) == 0;
    
    /// <summary>
    /// Returns true if this sample depends on other samples.
    /// Sample flags bit 24-25: sample_depends_on (2 = depends on others, not sync)
    /// </summary>
    public readonly bool DependsOnOthers => ((Flags >> 24) & 0x03) == 1;
}

/// <summary>
/// Track Fragment (traf) - container for fragment data for one track.
/// </summary>
public sealed class TrackFragment
{
    public TrackFragmentHeader Header { get; set; } = new();
    public ulong BaseMediaDecodeTime { get; set; }
    public List<TrackFragmentRun> Runs { get; } = [];
}

/// <summary>
/// Movie Fragment (moof) - container for one or more track fragments.
/// </summary>
public sealed class MovieFragment
{
    public uint SequenceNumber { get; set; }
    public long FilePosition { get; set; }
    public long DataOffset { get; set; }
    public List<TrackFragment> TrackFragments { get; } = [];
}

// MediaKernel - MPEG Program Stream Demuxer
// Demuxes MPEG PS (Program Stream) files - the container used for DVDs, SVCDs, VCDs

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Track information for MPEG PS stream.
/// </summary>
public sealed class MpegPsTrack
{
    public int StreamId { get; init; }
    public int SubStreamId { get; init; }
    public TrackType Type { get; init; }
    public CodecType Codec { get; init; }
    public bool IsConfigured { get; set; }
    public long FirstPresentationTimestamp { get; set; } = -1;
    public long LastPresentationTimestamp { get; set; } = -1;
    
    // Audio properties
    public int AudioChannels { get; set; }
    public int AudioSampleRate { get; set; }
    public int AudioBitsPerSample { get; set; }
    
    // Video properties  
    public int VideoWidth { get; set; }
    public int VideoHeight { get; set; }
    public double FrameRate { get; set; }
}

/// <summary>
/// Demuxer for MPEG Program Stream (PS) files.
/// Used for DVDs (.vob), video CDs, and general MPEG PS containers.
/// </summary>
public sealed class MpegPsDemuxer : IDisposable
{
    // Start codes
    private const uint PackStartCode = 0x000001BA;
    private const uint SystemHeaderStartCode = 0x000001BB;
    private const uint ProgramStreamMapCode = 0x000001BC;
    private const uint PrivateStream1Code = 0x000001BD;
    private const uint PaddingStreamCode = 0x000001BE;
    private const uint PrivateStream2Code = 0x000001BF;
    private const uint EndCode = 0x000001B9;
    
    // Stream ID ranges
    private const byte AudioStreamIdBase = 0xC0; // 0xC0-0xDF
    private const byte VideoStreamIdBase = 0xE0; // 0xE0-0xEF
    
    // Private stream 1 substream IDs (DVD/DVD-Audio)
    private const byte Ac3SubstreamBase = 0x80;     // 0x80-0x87 (AC-3)
    private const byte DtsSubstreamBase = 0x88;     // 0x88-0x8F (DTS)
    private const byte SddsSubstreamBase = 0x90;    // 0x90-0x97 (SDDS - rare)
    private const byte LpcmSubstreamBase = 0xA0;    // 0xA0-0xA7 (DVD LPCM)
    private const byte MlpSubstreamBase = 0xA0;     // 0xA0-0xA7 (DVD-Audio MLP/TrueHD)
    private const byte TrueHdSubstreamBase = 0xA1;  // 0xA1 typically for TrueHD
    private const byte DtsHdSubstreamBase = 0x88;   // 0x88-0x8F (DTS-HD in 0x8A+)
    private const byte EAc3SubstreamBase = 0xC0;    // 0xC0-0xC7 (E-AC-3/DD+)
    private const byte SubtitleBase = 0x20;         // 0x20-0x3F (DVD subtitles)
    private const byte CvdSubtitleBase = 0x70;      // 0x70-0x73 (CVD subtitles)
    private const byte SvcdSubtitleBase = 0x00;     // 0x00-0x03 (SVCD OGT subtitles)
    
    private Stream? inputStream;
    private bool ownsStream;
    private bool isInitialized;
    private bool endOfStream;
    private bool isDvdAudio;
    
    private readonly Dictionary<int, MpegPsTrack> tracks = new();
    private int muxRate;
    private long systemClockReference;
    private long durationMicroseconds;
    private long fileSize;
    
    /// <summary>
    /// All discovered tracks.
    /// </summary>
    public IReadOnlyDictionary<int, MpegPsTrack> Tracks => tracks;
    
    /// <summary>
    /// Number of tracks discovered.
    /// </summary>
    public int TrackCount => tracks.Count;
    
    /// <summary>
    /// Multiplex rate in bytes per second.
    /// </summary>
    public int MuxRate => muxRate;
    
    /// <summary>
    /// Current system clock reference (90kHz ticks).
    /// </summary>
    public long SystemClockReference => systemClockReference;
    
    /// <summary>
    /// Estimated duration in microseconds.
    /// </summary>
    public long DurationMicroseconds => durationMicroseconds;
    
    /// <summary>
    /// Whether the stream has ended.
    /// </summary>
    public bool EndOfStream => endOfStream;
    
    /// <summary>
    /// Whether this appears to be a DVD-Audio stream (AOB file with MLP/TrueHD).
    /// </summary>
    public bool IsDvdAudio => isDvdAudio;
    
    /// <summary>
    /// Initialize the demuxer from a file path.
    /// </summary>
    public bool Initialize(string filePath)
    {
        if (isInitialized)
            return false;
        
        if (!File.Exists(filePath))
            return false;
        
        // Detect DVD-Audio by file extension
        isDvdAudio = filePath.EndsWith(".aob", StringComparison.OrdinalIgnoreCase);
        
        try
        {
            inputStream = File.OpenRead(filePath);
            ownsStream = true;
            fileSize = inputStream.Length;
            return Initialize();
        }
        catch
        {
            Dispose();
            return false;
        }
    }
    
    /// <summary>
    /// Initialize the demuxer from a stream.
    /// </summary>
    public bool Initialize(Stream stream, bool takeOwnership = false)
    {
        if (isInitialized)
            return false;
        
        inputStream = stream;
        ownsStream = takeOwnership;
        fileSize = stream.CanSeek ? stream.Length : 0;
        
        return Initialize();
    }
    
    private bool Initialize()
    {
        if (inputStream == null)
            return false;
        
        // Verify PS sync
        Span<byte> header = stackalloc byte[4];
        if (inputStream.Read(header) != 4)
            return false;
        
        uint startCode = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (startCode != PackStartCode)
            return false;
        
        inputStream.Position = 0;
        
        // Scan for tracks by reading first few packets
        ScanForTracks();
        
        // Estimate duration if seekable
        if (inputStream.CanSeek && fileSize > 0)
        {
            EstimateDuration();
        }
        
        inputStream.Position = 0;
        isInitialized = true;
        return true;
    }
    
    /// <summary>Whether any video track is still missing its picture size.</summary>
    private bool NeedsGeometry()
    {
        foreach (var track in tracks.Values)
        {
            if (track.Type == TrackType.Video && track.VideoWidth == 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Fills a video track's size and frame rate from the sequence header in its own data.
    /// </summary>
    /// <remarks>
    /// MPEG-1 and MPEG-2 only. H.264 and HEVC state theirs in a sequence parameter set, which
    /// needs that codec's own parser; a track of those keeps a size of zero rather than a
    /// guess, which is what it had before and is at least honest.
    /// </remarks>
    private void ReadVideoGeometry(MpegPsPacket packet)
    {
        if (!tracks.TryGetValue(packet.TrackId, out var track)
            || track.Type != TrackType.Video
            || track.VideoWidth != 0
            || track.Codec is not (CodecType.Mpeg1Video or CodecType.Mpeg2Video))
        {
            return;
        }

        ReadOnlySpan<byte> data = packet.Data;

        for (int i = 0; i + 8 <= data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1 || data[i + 3] != 0xB3)
                continue;

            if (!Codecs.Mpeg2.Mpeg2HeaderParser.TryParseSequenceHeader(
                    data[(i + 4)..], out var header, out _))
            {
                continue;
            }

            track.VideoWidth = header.Width;
            track.VideoHeight = header.Height;

            if (header.FrameRateDenominator > 0)
                track.FrameRate = header.FrameRateNumerator / (double)header.FrameRateDenominator;

            return;
        }
    }

    private void ScanForTracks()
    {
        if (inputStream == null)
            return;
        
        long startPos = inputStream.Position;
        int packetsScanned = 0;
        const int maxPacketsToScan = 500;
        
        while (packetsScanned < maxPacketsToScan && inputStream.Position < inputStream.Length)
        {
            if (!SyncToNextPacket(out uint startCode))
                break;
            
            // Every arm here is mpeg.c's: a pack header and a system header are stepped
            // over, an elementary stream becomes a track, and anything else - padding,
            // private_stream_2, a program stream map - is a `goto redo` there, which is a
            // skip and carry on, NOT a stop. This used to break out of the loop, which
            // mattered the moment the whitelist stopped treating padding as a stream:
            // this file's padding comes first, so the scan ended before it saw a track.
            packetsScanned++;

            if (startCode == PackStartCode)
            {
                ParsePackHeader();
            }
            else if (startCode == SystemHeaderStartCode)
            {
                SkipSystemHeader();
            }
            else if (startCode == ProgramStreamMapCode)
            {
                ParseProgramStreamMap();
            }
            else if (IsElementaryStreamCode(startCode))
            {
                // The payload is kept only while some video track still has no geometry.
                // Nothing in a program stream states a picture size, so the only place to read
                // it is the elementary stream's own sequence header, which is what
                // avformat_find_stream_info does through the codec's parser. Without it every
                // MPEG-PS track opened as "mpeg2video 0x0 @ 0 fps" while decoding 352x240
                // perfectly well.
                var scanned = ParsePesPacket(startCode, discardData: !NeedsGeometry());

                if (scanned is not null && scanned.Data.Length > 0)
                    ReadVideoGeometry(scanned);
            }
            else
            {
                SkipPesPacket();
            }
        }
        
        inputStream.Position = startPos;
    }
    
    private void EstimateDuration()
    {
        if (inputStream == null || !inputStream.CanSeek)
            return;
        
        long originalPos = inputStream.Position;
        
        // Seek to near the end
        long seekPos = Math.Max(0, fileSize - 65536);
        inputStream.Position = seekPos;
        
        long lastScr = -1;
        
        // Find the last SCR
        while (SyncToNextPacket(out uint startCode))
        {
            if (startCode == PackStartCode)
            {
                long scr = ParsePackHeader();
                if (scr > 0)
                    lastScr = scr;
            }
            else if (startCode >= 0x000001BD && startCode <= 0x000001EF)
            {
                SkipPesPacket();
            }
            else
            {
                break;
            }
        }
        
        if (lastScr > 0 && systemClockReference > 0)
        {
            // Convert 90kHz ticks to microseconds
            durationMicroseconds = (lastScr * 1_000_000) / 90_000;
        }
        else
        {
            // estimate_timings_from_pts: read the last timestamp in the file and subtract the
            // first. WHAT THIS REPLACED: file size times eight over program_mux_rate, which is
            // the rate the stream is ALLOWED to peak at and not the rate it uses - it reported
            // 75 seconds for a file ffprobe measures at 479.196, because this one declares
            // 9.36 Mbit/s and runs at 1.47.
            int streamId = DefaultStreamId();

            if (streamId >= 0 && fileSize > 0 &&
                FormatSeek.FindLastTimestamp(streamId, fileSize, MpegPsReadDts,
                                             out long last, out _))
            {
                long first = -1;

                foreach (var track in tracks.Values)
                {
                    if (track.FirstPresentationTimestamp >= 0
                        && (first < 0 || track.FirstPresentationTimestamp < first))
                    {
                        first = track.FirstPresentationTimestamp;
                    }
                }

                if (first >= 0 && last > first)
                    durationMicroseconds = ((last - first) * 1_000_000) / 90_000;
            }
        }
        
        inputStream.Position = originalPos;
    }
    
    /// <summary>
    /// Read the next packet from the stream.
    /// </summary>
    public MpegPsPacket? ReadNextPacket()
    {
        if (!isInitialized || inputStream == null || endOfStream)
            return null;
        
        while (true)
        {
            if (!SyncToNextPacket(out uint startCode))
            {
                endOfStream = true;
                return null;
            }
            
            if (startCode == EndCode)
            {
                // MPEG_program_end_code marks the end of a PROGRAM, not of the file. Two
                // programs concatenated into one file - which is how a lot of captured material
                // and most DVD rips are produced - put one in the middle, and treating it as
                // end of file silently truncated the stream: this file lost its last 4,668
                // bytes and two video frames, and the only symptom was the decoder reporting a
                // damaged slice at the very end. FFmpeg has no case for this code at all; its
                // demuxer rescans for the next start code, which is what happens here. There is
                // no length field to skip, so nothing is consumed.
                continue;
            }
            
            if (startCode == PackStartCode)
            {
                ParsePackHeader();
                continue;
            }
            
            if (startCode == SystemHeaderStartCode)
            {
                SkipSystemHeader();
                continue;
            }
            
            if (startCode == PaddingStreamCode)
            {
                SkipPesPacket();
                continue;
            }
            
            if (startCode == PrivateStream2Code)
            {
                SkipPesPacket();
                continue;
            }
            
            if (startCode == ProgramStreamMapCode)
            {
                ParseProgramStreamMap();
                continue;
            }
            
            // 0x1BE and 0x1BF are handled above, so this is audio, video,
            // private_stream_1 and the extended stream id - the same whitelist the track
            // scan uses, which is what keeps the two from disagreeing about what a track is.
            if (IsElementaryStreamCode(startCode))
            {
                return ParsePesPacket(startCode, discardData: false);
            }
            
            // (The extended stream id 0x1FD is in IsElementaryStreamCode above.)
            
            // Unknown packet type - try to skip
            SkipPesPacket();
        }
    }
    
    private bool SyncToNextPacket(out uint startCode)
    {
        startCode = 0;
        
        if (inputStream == null)
            return false;
        
        Span<byte> header = stackalloc byte[4];
        int zeroCount = 0;
        
        // Search for 0x000001 prefix
        while (true)
        {
            int b = inputStream.ReadByte();
            if (b < 0)
                return false;
            
            if (b == 0)
            {
                zeroCount++;
            }
            else if (b == 1 && zeroCount >= 2)
            {
                // Found 0x000001 prefix
                int streamId = inputStream.ReadByte();
                if (streamId < 0)
                    return false;
                
                startCode = 0x00000100 | (uint)streamId;
                return true;
            }
            else
            {
                zeroCount = 0;
            }
        }
    }
    
    private long ParsePackHeader()
    {
        if (inputStream == null)
            return -1;
        
        Span<byte> header = stackalloc byte[10];
        if (inputStream.Read(header) != 10)
            return -1;
        
        // Check MPEG-1 vs MPEG-2
        if ((header[0] & 0xC0) == 0x40)
        {
            // MPEG-2 pack header (more common)
            return ParseMpeg2PackHeader(header);
        }
        else if ((header[0] & 0xF0) == 0x20)
        {
            // MPEG-1 pack header
            return ParseMpeg1PackHeader(header);
        }
        
        return -1;
    }
    
    private long ParseMpeg2PackHeader(ReadOnlySpan<byte> data)
    {
        if (inputStream == null)
            return -1;
        
        // Extract SCR (System Clock Reference) - 33 bits + 9-bit extension
        long scr = ((long)(data[0] & 0x38) << 27) |
                   ((long)(data[0] & 0x03) << 28) |
                   ((long)data[1] << 20) |
                   ((long)(data[2] & 0xF8) << 12) |
                   ((long)(data[2] & 0x03) << 13) |
                   ((long)data[3] << 5) |
                   ((long)(data[4] & 0xF8) >> 3);
        
        systemClockReference = scr;
        
        // Extract mux rate (22 bits)
        muxRate = ((data[6] << 14) | (data[7] << 6) | (data[8] >> 2)) * 50;
        
        // Skip stuffing bytes
        int stuffingLength = data[9] & 0x07;
        if (stuffingLength > 0)
        {
            inputStream.Seek(stuffingLength, SeekOrigin.Current);
        }
        
        return scr;
    }
    
    private long ParseMpeg1PackHeader(ReadOnlySpan<byte> data)
    {
        // MPEG-1 pack header - need 8 bytes (we read 10, so seek back 2)
        if (inputStream == null)
            return -1;
        
        inputStream.Seek(-2, SeekOrigin.Current);
        
        // Extract SCR - 33 bits
        long scr = ((long)(data[0] & 0x0E) << 29) |
                   ((long)data[1] << 22) |
                   ((long)(data[2] & 0xFE) << 14) |
                   ((long)data[3] << 7) |
                   ((long)(data[4] & 0xFE) >> 1);
        
        systemClockReference = scr;
        
        // Extract mux rate (22 bits)
        muxRate = (((data[5] & 0x7F) << 15) | (data[6] << 7) | (data[7] >> 1)) * 50;
        
        return scr;
    }
    
    private void SkipSystemHeader()
    {
        if (inputStream == null)
            return;
        
        Span<byte> length = stackalloc byte[2];
        if (inputStream.Read(length) == 2)
        {
            int headerLength = BinaryPrimitives.ReadUInt16BigEndian(length);
            inputStream.Seek(headerLength, SeekOrigin.Current);
        }
    }
    
    /// <summary>
    /// Parse Program Stream Map (PSM) - provides explicit codec information.
    /// Contains stream_type values that map elementary streams to codecs.
    /// </summary>
    private void ParseProgramStreamMap()
    {
        if (inputStream == null)
            return;
        
        Span<byte> header = stackalloc byte[4];
        if (inputStream.Read(header) != 4)
            return;
        
        int length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length < 4 || length > 1024)
        {
            // Skip invalid PSM
            inputStream.Seek(length - 2, SeekOrigin.Current);
            return;
        }
        
        // header[2] contains current_next_indicator and reserved
        // header[3] contains reserved and version
        
        int programInfoLength = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(2)) & 0x0FFF;
        
        // Skip program info descriptors
        if (programInfoLength > 0 && programInfoLength < length)
        {
            inputStream.Seek(programInfoLength, SeekOrigin.Current);
        }
        
        // Read elementary stream map length
        Span<byte> esMapLengthBytes = stackalloc byte[2];
        if (inputStream.Read(esMapLengthBytes) != 2)
            return;
        
        int esMapLength = BinaryPrimitives.ReadUInt16BigEndian(esMapLengthBytes);
        int bytesRemaining = esMapLength;
        
        Span<byte> esInfo = stackalloc byte[4];
        
        while (bytesRemaining >= 4)
        {
            if (inputStream.Read(esInfo) != 4)
                break;
            
            byte streamType = esInfo[0];
            byte streamId = esInfo[1];
            int esInfoLength = BinaryPrimitives.ReadUInt16BigEndian(esInfo.Slice(2)) & 0x0FFF;
            
            bytesRemaining -= 4;
            
            // Map stream_type to codec
            var codec = MapPsmStreamType(streamType);
            if (codec != CodecType.Unknown)
            {
                int trackId = streamId;
                if (tracks.TryGetValue(trackId, out var track))
                {
                    // Update track codec if it was unknown
                    if (track.Codec == CodecType.Unknown)
                    {
                        tracks[trackId] = new MpegPsTrack
                        {
                            StreamId = track.StreamId,
                            SubStreamId = track.SubStreamId,
                            Type = track.Type,
                            Codec = codec,
                            IsConfigured = track.IsConfigured,
                            FirstPresentationTimestamp = track.FirstPresentationTimestamp,
                            LastPresentationTimestamp = track.LastPresentationTimestamp,
                            AudioChannels = track.AudioChannels,
                            AudioSampleRate = track.AudioSampleRate,
                            AudioBitsPerSample = track.AudioBitsPerSample,
                            VideoWidth = track.VideoWidth,
                            VideoHeight = track.VideoHeight,
                            FrameRate = track.FrameRate
                        };
                    }
                }
            }
            
            // Skip ES info descriptors
            if (esInfoLength > 0 && esInfoLength <= bytesRemaining)
            {
                inputStream.Seek(esInfoLength, SeekOrigin.Current);
                bytesRemaining -= esInfoLength;
            }
            else
            {
                break;
            }
        }
        
        // Skip remaining bytes including CRC
        if (bytesRemaining > 0)
        {
            inputStream.Seek(bytesRemaining, SeekOrigin.Current);
        }
    }
    
    /// <summary>
    /// Maps PSM stream_type values to codec types.
    /// Based on ISO/IEC 13818-1 Table 2-34.
    /// </summary>
    private static CodecType MapPsmStreamType(byte streamType)
    {
        return streamType switch
        {
            0x01 => CodecType.Mpeg1Video,     // MPEG-1 Video
            0x02 => CodecType.Mpeg2Video,     // MPEG-2 Video
            0x03 => CodecType.MpegAudio,      // MPEG-1 Audio Layer I/II
            0x04 => CodecType.MpegAudio,      // MPEG-1 Audio Layer III
            0x0F => CodecType.Aac,            // AAC ADTS
            0x10 => CodecType.Mpeg2Video,     // MPEG-4 Part 2 Visual
            0x11 => CodecType.Aac,            // AAC LATM
            0x1B => CodecType.H264,           // H.264/AVC
            0x24 => CodecType.Hevc,           // H.265/HEVC
            0x42 => CodecType.Hevc,           // HEVC (alternate)
            0x81 => CodecType.Ac3,            // AC-3 audio
            0x82 => CodecType.Dts,            // DTS audio
            0x83 => CodecType.TrueHd,         // TrueHD (Dolby Lossless)
            0x84 => CodecType.Eac3,           // E-AC-3 (DD+)
            0x85 => CodecType.DtsHd,          // DTS-HD
            0x86 => CodecType.DtsHd,          // DTS-HD Master Audio
            0xA1 => CodecType.Eac3,           // E-AC-3 secondary
            0xA2 => CodecType.DtsHd,          // DTS-HD secondary
            0xEA => CodecType.Vc1,            // VC-1/WMV3
            _ => CodecType.Unknown
        };
    }
    
    private void SkipPesPacket()
    {
        if (inputStream == null)
            return;
        
        Span<byte> length = stackalloc byte[2];
        if (inputStream.Read(length) == 2)
        {
            int packetLength = BinaryPrimitives.ReadUInt16BigEndian(length);
            if (packetLength > 0)
            {
                inputStream.Seek(packetLength, SeekOrigin.Current);
            }
        }
    }
    
    /// <summary>
    /// mpeg.c's "find matching stream" test, which is the whitelist of start codes that
    /// can belong to an elementary stream. Everything else is a <c>goto redo</c> there: it
    /// never reaches a stream and never becomes one.
    /// </summary>
    /// <remarks>
    /// <c>ScanForTracks</c> used the range 0x1BD..0x1EF, which swallows the padding stream
    /// (0x1BE) and private_stream_2 (0x1BF). The read loop skips both, so only the track
    /// list was wrong - this file's padding comes before its video, so it reported a track
    /// called "unknown" ahead of both real ones, out of a fall-through arm in
    /// <see cref="CreateTrack"/> that had no business being reachable.
    /// </remarks>
    private static bool IsElementaryStreamCode(uint startCode)
        => (startCode >= 0x000001C0 && startCode <= 0x000001DF)   // audio
        || (startCode >= 0x000001E0 && startCode <= 0x000001EF)   // video
        || startCode == 0x000001BD                                // private_stream_1
        || startCode == PrivateStream2Code
        || startCode == 0x000001FD;                               // extended stream id

    private MpegPsPacket? ParsePesPacket(uint startCode, bool discardData)
    {
        if (inputStream == null)
            return null;
        
        byte streamId = (byte)(startCode & 0xFF);
        
        Span<byte> lengthBytes = stackalloc byte[2];
        if (inputStream.Read(lengthBytes) != 2)
            return null;
        
        int packetLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
        if (packetLength == 0)
        {
            // Unbounded packet - not supported for now
            return null;
        }
        
        // Read the entire PES packet
        byte[] pesData = new byte[packetLength];
        int bytesRead = 0;
        while (bytesRead < packetLength)
        {
            int read = inputStream.Read(pesData, bytesRead, packetLength - bytesRead);
            if (read <= 0)
                break;
            bytesRead += read;
        }
        
        if (bytesRead < 3)
            return null;
        
        // Parse PES header
        long presentationTimestamp = -1;
        long decodingTimestamp = -1;
        int dataOffset = 0;
        int subStreamId = 0;
        
        // Check for MPEG-2 PES header
        if ((pesData[0] & 0xC0) == 0x80)
        {
            // MPEG-2 PES
            int ptsFlag = (pesData[1] >> 7) & 1;
            int dtsFlag = (pesData[1] >> 6) & 1;
            int headerDataLength = pesData[2];
            dataOffset = 3 + headerDataLength;
            
            if (ptsFlag != 0 && headerDataLength >= 5)
            {
                presentationTimestamp = ExtractTimestamp(pesData.AsSpan(3, 5));
            }
            
            if (dtsFlag != 0 && headerDataLength >= 10)
            {
                decodingTimestamp = ExtractTimestamp(pesData.AsSpan(8, 5));
            }
        }
        else
        {
            // MPEG-1 PES - skip stuffing bytes
            while (dataOffset < pesData.Length && pesData[dataOffset] == 0xFF)
            {
                dataOffset++;
            }
            
            if (dataOffset < pesData.Length)
            {
                byte marker = pesData[dataOffset];
                
                if ((marker & 0xC0) == 0x40)
                {
                    // STD buffer size
                    dataOffset += 2;
                }
                
                if (dataOffset < pesData.Length)
                {
                    marker = pesData[dataOffset];
                    
                    if ((marker & 0xF0) == 0x20)
                    {
                        // PTS only
                        if (dataOffset + 5 <= pesData.Length)
                        {
                            presentationTimestamp = ExtractTimestamp(pesData.AsSpan(dataOffset, 5));
                            dataOffset += 5;
                        }
                    }
                    else if ((marker & 0xF0) == 0x30)
                    {
                        // PTS and DTS
                        if (dataOffset + 10 <= pesData.Length)
                        {
                            presentationTimestamp = ExtractTimestamp(pesData.AsSpan(dataOffset, 5));
                            decodingTimestamp = ExtractTimestamp(pesData.AsSpan(dataOffset + 5, 5));
                            dataOffset += 10;
                        }
                    }
                    else if (marker == 0x0F)
                    {
                        // No timestamps
                        dataOffset++;
                    }
                }
            }
        }
        
        // Handle private stream 1 (DVD audio/subtitle)
        if (streamId == 0xBD && dataOffset < pesData.Length)
        {
            subStreamId = pesData[dataOffset];
            dataOffset++;
            
            // Handle different substream types
            if (subStreamId >= Ac3SubstreamBase && subStreamId <= 0x87)
            {
                // AC-3: skip 3 bytes (frame count, first access unit offset)
                dataOffset += 3;
            }
            else if (subStreamId >= DtsSubstreamBase && subStreamId <= 0x8F)
            {
                // DTS: skip 3 bytes
                dataOffset += 3;
            }
            else if (subStreamId >= LpcmSubstreamBase && subStreamId <= 0xAF)
            {
                // LPCM: skip 6 bytes (audio header)
                dataOffset += 6;
            }
        }
        
        // Create or update track
        int trackId = (streamId == 0xBD) ? (0xBD00 | subStreamId) : streamId;
        
        if (!tracks.TryGetValue(trackId, out var track))
        {
            track = CreateTrack(streamId, subStreamId);
            tracks[trackId] = track;
        }
        
        // Update track timestamps
        if (presentationTimestamp >= 0)
        {
            if (track.FirstPresentationTimestamp < 0)
                track.FirstPresentationTimestamp = presentationTimestamp;
            track.LastPresentationTimestamp = presentationTimestamp;
        }
        
        // The header, with no payload: the track scan and the seek's timestamp probe both want
        // what the PES header says and not the bytes behind it, which is the split
        // mpegps_read_pes_header makes. This used to return null, so a caller could not tell
        // "no packet" from "a packet whose data you asked me not to keep" - and the seek's
        // probe read the first "no packet" as "no timestamp anywhere in this file" and gave up.
        if (discardData)
        {
            return new MpegPsPacket
            {
                TrackId = trackId,
                StreamId = streamId,
                SubStreamId = subStreamId,
                PresentationTimestamp = presentationTimestamp,
                DecodingTimestamp = decodingTimestamp,
                Data = [],
            };
        }

        // Extract payload
        if (dataOffset >= pesData.Length)
            return null;
        
        int payloadLength = pesData.Length - dataOffset;
        byte[] payload = new byte[payloadLength];
        Array.Copy(pesData, dataOffset, payload, 0, payloadLength);
        
        return new MpegPsPacket
        {
            TrackId = trackId,
            StreamId = streamId,
            SubStreamId = subStreamId,
            PresentationTimestamp = presentationTimestamp,
            DecodingTimestamp = decodingTimestamp,
            Data = payload
        };
    }
    
    private static long ExtractTimestamp(ReadOnlySpan<byte> data)
    {
        // 33-bit timestamp encoded in 5 bytes
        return ((long)(data[0] & 0x0E) << 29) |
               ((long)data[1] << 22) |
               ((long)(data[2] & 0xFE) << 14) |
               ((long)data[3] << 7) |
               ((long)(data[4] >> 1));
    }
    
    private MpegPsTrack CreateTrack(byte streamId, int subStreamId)
    {
        TrackType trackType;
        CodecType codec;
        
        if (streamId >= AudioStreamIdBase && streamId <= 0xDF)
        {
            trackType = TrackType.Audio;
            codec = CodecType.MpegAudio;
        }
        else if (streamId >= VideoStreamIdBase && streamId <= 0xEF)
        {
            trackType = TrackType.Video;
            codec = CodecType.Mpeg2Video;
        }
        else if (streamId == 0xBD)
        {
            // Private stream 1 - DVD/DVD-Audio/Blu-ray
            (trackType, codec) = ClassifyPrivateStream1Substream(subStreamId);
        }
        else if (streamId == 0xFD)
        {
            // Extended stream ID (Blu-ray/EVOB) - VC-1, H.264
            trackType = TrackType.Video;
            codec = CodecType.Vc1; // Will be refined based on substream
        }
        else
        {
            // Unreachable now that both callers apply mpeg.c's whitelist. It used to be
            // how a padding stream became a track called "unknown".
            throw new InvalidDataException(
                $"MPEG-PS: stream id 0x{streamId:X2} is not an elementary stream.");
        }
        
        return new MpegPsTrack
        {
            StreamId = streamId,
            SubStreamId = subStreamId,
            Type = trackType,
            Codec = codec
        };
    }
    
    /// <summary>
    /// Classifies a private stream 1 substream by its ID.
    /// </summary>
    private (TrackType Type, CodecType Codec) ClassifyPrivateStream1Substream(int subStreamId)
    {
        // In DVD-Audio (.aob files), 0xA0-0xA7 is MLP/TrueHD, not LPCM
        if (isDvdAudio && subStreamId >= 0xA0 && subStreamId <= 0xA7)
        {
            return (TrackType.Audio, CodecType.Mlp);
        }
        
        return subStreamId switch
        {
            // AC-3 (Dolby Digital): 0x80-0x87
            >= 0x80 and <= 0x87 => (TrackType.Audio, CodecType.Ac3),
            
            // DTS / DTS-HD: 0x88-0x8F
            >= 0x88 and <= 0x8F => (TrackType.Audio, CodecType.Dts),
            
            // SDDS (rare): 0x90-0x97
            >= 0x90 and <= 0x97 => (TrackType.Audio, CodecType.Unknown),
            
            // DVD LPCM: 0xA0-0xA7 (regular DVD)
            >= 0xA0 and <= 0xA7 => (TrackType.Audio, CodecType.Lpcm),
            
            // TrueHD (Blu-ray): 0xB0-0xB7
            >= 0xB0 and <= 0xB7 => (TrackType.Audio, CodecType.TrueHd),
            
            // E-AC-3 (Dolby Digital Plus): 0xC0-0xC7
            >= 0xC0 and <= 0xC7 => (TrackType.Audio, CodecType.Eac3),
            
            // DVD subtitles: 0x20-0x3F
            >= 0x20 and <= 0x3F => (TrackType.Subtitle, CodecType.DvdSubtitle),
            
            // CVD subtitles: 0x70-0x73
            >= 0x70 and <= 0x73 => (TrackType.Subtitle, CodecType.CvdSubtitle),
            
            // SVCD OGT subtitles: 0x00-0x03
            >= 0x00 and <= 0x03 => (TrackType.Subtitle, CodecType.SvcdSubtitle),
            
            // Unknown
            _ => (TrackType.Unknown, CodecType.Unknown)
        };
    }
    
    /// <summary>No timestamp: <c>AV_NOPTS_VALUE</c>.</summary>
    private const long NoTimestamp = FormatSeek.NoTimestamp;

    /// <summary>
    /// Seek to a specific time.
    /// </summary>
    /// <remarks>
    /// <c>ff_seek_frame_binary</c> through <see cref="FormatSeek.GenSearch"/>: a program stream has no
    /// index, so the position is found by reading timestamps out of the file and narrowing in on
    /// the target, which is what FFmpeg does for every format whose demuxer offers a
    /// <c>read_timestamp</c> and no index of its own.
    /// <para>
    /// WHAT THIS REPLACED. A byte position computed from the mux rate:
    /// <c>estimatedPosition = targetScr * muxRate / 90000</c>. <c>program_mux_rate</c> is in
    /// units of 50 bytes per second, so that number was fifty times too small and the seek
    /// barely moved - <c>-ss 20</c> on <c>Video-MPG-MPG.mpg</c> landed near the start of the
    /// file and the trim then threw away 20.1 million audio samples, 457 seconds of audio,
    /// to find a two-second window. The content came out wrong as well.
    /// </para>
    /// </remarks>
    public bool Seek(long timeMicroseconds)
    {
        if (!isInitialized || inputStream == null || !inputStream.CanSeek)
            return false;

        // The timestamps in the file are 90 kHz, as every MPEG system timestamp is.
        long targetTimestamp = (timeMicroseconds * 90_000) / 1_000_000;

        int streamId = DefaultStreamId();
        if (streamId < 0)
            return false;

        long position = FormatSeek.SeekFrameBinary(streamId, targetTimestamp, fileSize,
                                                   MpegPsReadDts);
        if (position < 0)
            return false;

        inputStream.Position = position;
        endOfStream = false;
        return true;
    }

    /// <summary>
    /// <c>av_find_default_stream_index</c>: the stream a seek is aimed at. Video when there is
    /// one, because that is where the keyframes are.
    /// </summary>
    private int DefaultStreamId()
    {
        int fallback = -1;

        foreach (var track in tracks.Values)
        {
            if (track.Type == TrackType.Video)
                return track.StreamId;
            if (fallback < 0)
                fallback = track.StreamId;
        }

        return fallback;
    }

    /// <summary>
    /// <c>mpegps_read_dts</c>: the decode timestamp of the first packet of one stream at or
    /// after a byte position, and where that packet's start code is.
    /// </summary>
    /// <param name="positionLimit">
    /// How far to read before giving up. The C passes <c>INT64_MAX</c> everywhere but the
    /// backward walk of <see cref="FormatSeek.FindLastTimestamp"/>, where a bounded window is
    /// the point.
    /// </param>
    private long MpegPsReadDts(int streamId, ref long position, long positionLimit = long.MaxValue)
    {
        if (inputStream == null || position < 0 || position >= fileSize)
            return NoTimestamp;

        inputStream.Position = position;

        while (inputStream.Position < positionLimit)
        {
            if (!SyncToNextPacket(out uint startCode))
                return NoTimestamp;

            // The start code has been consumed, so the packet began four bytes back.
            long packetStart = inputStream.Position - 4;

            if (startCode == EndCode)
                continue;

            if (startCode == PackStartCode)
            {
                ParsePackHeader();
                continue;
            }

            if (startCode == SystemHeaderStartCode)
            {
                SkipSystemHeader();
                continue;
            }

            if (startCode == ProgramStreamMapCode)
            {
                ParseProgramStreamMap();
                continue;
            }

            if (!IsElementaryStreamCode(startCode) || startCode == PaddingStreamCode
                || startCode == PrivateStream2Code)
            {
                SkipPesPacket();
                continue;
            }

            // The payload is read and dropped: this is a probe, and the data belongs to
            // whatever reads the file after the seek.
            var packet = ParsePesPacket(startCode, discardData: true);
            if (packet is null)
                return NoTimestamp;

            // "*pts = *dts = AV_NOPTS_VALUE; ... if (PTS) *dts = *pts = get_pts();" - a packet
            // with only a presentation timestamp decodes at that time too.
            long dts = packet.DecodingTimestamp >= 0
                ? packet.DecodingTimestamp
                : packet.PresentationTimestamp;

            if (packet.StreamId == streamId && dts >= 0)
            {
                position = packetStart;
                return dts;
            }
        }

        return NoTimestamp;
    }

    
    /// <summary>
    /// Reset to the beginning of the stream.
    /// </summary>
    public void Reset()
    {
        if (inputStream != null && inputStream.CanSeek)
        {
            inputStream.Position = 0;
            endOfStream = false;
        }
    }
    
    /// <summary>
    /// Get a track by ID.
    /// </summary>
    public MpegPsTrack? GetTrack(int trackId)
    {
        return tracks.GetValueOrDefault(trackId);
    }
    
    /// <summary>
    /// Check if a file is an MPEG PS file.
    /// </summary>
    public static bool IsMpegPsFile(string path)
    {
        if (!File.Exists(path))
            return false;
        
        try
        {
            using var stream = File.OpenRead(path);
            return IsMpegPsStream(stream);
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Check if a stream contains MPEG PS data.
    /// </summary>
    public static bool IsMpegPsStream(Stream stream)
    {
        if (!stream.CanRead)
            return false;
        
        long originalPosition = stream.CanSeek ? stream.Position : 0;
        
        try
        {
            Span<byte> header = stackalloc byte[4];
            if (stream.Read(header) != 4)
                return false;
            
            if (stream.CanSeek)
                stream.Position = originalPosition;
            
            uint startCode = BinaryPrimitives.ReadUInt32BigEndian(header);
            return startCode == PackStartCode;
        }
        catch
        {
            if (stream.CanSeek)
                stream.Position = originalPosition;
            return false;
        }
    }
    
    public void Dispose()
    {
        if (ownsStream)
        {
            inputStream?.Dispose();
        }
        inputStream = null;
        tracks.Clear();
        isInitialized = false;
    }
}

/// <summary>
/// A packet read from an MPEG PS stream.
/// </summary>
public sealed class MpegPsPacket
{
    /// <summary>
    /// Combined track ID (stream_id or 0xBD00 | substream_id).
    /// </summary>
    public int TrackId { get; init; }
    
    /// <summary>
    /// PES stream ID (0xBD for private, 0xC0-0xDF for audio, 0xE0-0xEF for video).
    /// </summary>
    public byte StreamId { get; init; }
    
    /// <summary>
    /// Substream ID for private stream 1 packets.
    /// </summary>
    public int SubStreamId { get; init; }
    
    /// <summary>
    /// Presentation timestamp in 90kHz ticks, or -1 if not present.
    /// </summary>
    public long PresentationTimestamp { get; init; } = -1;
    
    /// <summary>
    /// Decoding timestamp in 90kHz ticks, or -1 if not present.
    /// </summary>
    public long DecodingTimestamp { get; init; } = -1;
    
    /// <summary>
    /// Presentation timestamp in microseconds.
    /// </summary>
    public long PresentationTimestampMicroseconds => 
        PresentationTimestamp >= 0 ? (PresentationTimestamp * 1_000_000) / 90_000 : -1;
    
    /// <summary>
    /// The packet payload data.
    /// </summary>
    public required byte[] Data { get; init; }
}

/// <summary>
/// Codec types for MPEG PS streams.
/// </summary>
public enum CodecType
{
    Unknown,
    Mpeg1Video,
    Mpeg2Video,
    H264,
    Hevc,
    Vc1,
    MpegAudio,
    Ac3,
    Eac3,
    Dts,
    DtsHd,
    Lpcm,
    Mlp,
    TrueHd,
    Aac,
    DvdSubtitle,
    CvdSubtitle,
    SvcdSubtitle
}

/// <summary>
/// Track types.
/// </summary>
public enum TrackType
{
    Unknown,
    Video,
    Audio,
    Subtitle
}

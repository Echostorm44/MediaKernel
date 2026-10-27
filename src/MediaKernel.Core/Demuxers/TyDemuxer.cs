// MediaKernel - TiVo TY Stream Demuxer
// Demuxes TY (TiVo DVR) streams containing MPEG-2 video and AC-3/MPEG audio
// Reference: VLC's modules/demux/ty.c, MultimediaWiki TiVo page

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// TiVo DVR hardware type.
/// </summary>
public enum TivoType
{
    /// <summary>Unknown TiVo type.</summary>
    Unknown,
    
    /// <summary>Stand-Alone TiVo (cable/antenna).</summary>
    StandAlone,
    
    /// <summary>DirecTV TiVo (satellite).</summary>
    DirecTv
}

/// <summary>
/// TiVo Series (hardware generation).
/// </summary>
public enum TivoSeries
{
    /// <summary>Unknown series.</summary>
    Unknown,
    
    /// <summary>Series 1 TiVo (original).</summary>
    Series1,
    
    /// <summary>Series 2 TiVo.</summary>
    Series2
}

/// <summary>
/// Audio codec type in TY stream.
/// </summary>
public enum TyAudioType
{
    /// <summary>Unknown audio type.</summary>
    Unknown,
    
    /// <summary>AC-3 (Dolby Digital) audio.</summary>
    Ac3,
    
    /// <summary>MPEG-1 Layer 2 audio.</summary>
    Mpeg
}

/// <summary>
/// Result from probing a stream for TY content.
/// </summary>
public readonly struct TyProbeResult
{
    /// <summary>Whether valid TY content was detected.</summary>
    public bool IsTy { get; init; }
    
    /// <summary>Confidence level 0-100.</summary>
    public int Confidence { get; init; }
    
    /// <summary>TiVo series if detected.</summary>
    public TivoSeries Series { get; init; }
    
    /// <summary>TiVo type if detected.</summary>
    public TivoType Type { get; init; }
    
    /// <summary>Audio codec type if detected.</summary>
    public TyAudioType AudioType { get; init; }
}

/// <summary>
/// Represents a track in a TY stream.
/// </summary>
public sealed class TyTrack
{
    /// <summary>Track index (0 = video, 1 = audio).</summary>
    public int Index { get; init; }
    
    /// <summary>True if this is a video track.</summary>
    public bool IsVideo { get; init; }
    
    /// <summary>Codec identifier.</summary>
    public string CodecId { get; init; } = string.Empty;
}

/// <summary>
/// TY stream record header.
/// </summary>
internal readonly struct TyRecordHeader
{
    /// <summary>Record payload size in bytes.</summary>
    public readonly int RecordSize;
    
    /// <summary>Record type (0xe0 = video, 0xc0 = audio).</summary>
    public readonly byte RecordType;
    
    /// <summary>Sub-record type (determines PES handling).</summary>
    public readonly byte SubRecordType;
    
    /// <summary>Extended data bytes (for CC/XDS).</summary>
    public readonly byte ExtendedData0;
    public readonly byte ExtendedData1;
    
    /// <summary>Whether this is an extended record (no payload).</summary>
    public readonly bool IsExtended;
    
    /// <summary>TY timestamp in nanoseconds.</summary>
    public readonly long TyPtsNanoseconds;

    public TyRecordHeader(int recordSize, byte recordType, byte subRecordType,
                          byte extData0, byte extData1, bool isExtended, long tyPts)
    {
        RecordSize = recordSize;
        RecordType = recordType;
        SubRecordType = subRecordType;
        ExtendedData0 = extData0;
        ExtendedData1 = extData1;
        IsExtended = isExtended;
        TyPtsNanoseconds = tyPts;
    }
}

/// <summary>
/// Represents a packet read from a TY stream.
/// </summary>
public readonly struct TyPacket
{
    /// <summary>True if this is a video packet.</summary>
    public bool IsVideo { get; init; }
    
    /// <summary>Presentation timestamp in ticks (or -1 if not present).</summary>
    public long PresentationTimestampTicks { get; init; }
    
    /// <summary>Track index (0 = video, 1 = audio).</summary>
    public int TrackIndex { get; init; }
    
    /// <summary>Payload data.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }
    
    /// <summary>Whether this is a keyframe (for video).</summary>
    public bool IsKeyframe { get; init; }
}

/// <summary>
/// Demuxer for TiVo TY stream format (DVR recordings).
/// </summary>
/// <remarks>
/// TY files contain MPEG-2 video and AC-3 or MPEG audio in a non-standard
/// multiplexed format. Each file consists of 128KB chunks, with the first
/// chunk of each 512MB part being a master header containing a seek table.
/// </remarks>
public sealed class TyDemuxer : IDisposable
{
    private const uint TivoFileId = 0xf5467abd;
    private const int ChunkSize = 128 * 1024;       // 128KB
    private const long PartLength = 0x20000000;     // 512MB
    private const int RecordHeaderSize = 16;
    private const int ChunkHeaderSize = 4;
    private const int MaxRecordsPerChunk = (ChunkSize - ChunkHeaderSize) / RecordHeaderSize;
    
    // PES header lengths
    private const int Series1PesLength = 11;
    private const int Series2PesLength = 16;
    private const int Ac3PesLength = 14;
    private const int VideoPesLength = 16;
    
    // PTS offsets
    private const int DtivosPtsOffset = 6;
    private const int SaPtsOffset = 9;
    private const int Ac3PtsOffset = 9;
    private const int VideoPtsOffset = 9;
    
    // PES start codes
    private static readonly byte[] VideoPacketHeader = [0x00, 0x00, 0x01, 0xe0];
    private static readonly byte[] MpegAudioPacketHeader = [0x00, 0x00, 0x01, 0xc0];
    private static readonly byte[] Ac3AudioPacketHeader = [0x00, 0x00, 0x01, 0xbd];
    
    private readonly Stream _stream;
    private readonly byte[] _chunkBuffer;
    private readonly List<TyTrack> _tracks = new();
    
    private TivoType _tivoType = TivoType.Unknown;
    private TivoSeries _tivoSeries = TivoSeries.Unknown;
    private TyAudioType _audioType = TyAudioType.Unknown;
    private int _pesLength;
    private int _ptsOffset;
    
    private TyRecordHeader[] _recordHeaders = [];
    private int _currentRecord;
    private int _numRecords;
    private int _currentChunk;
    private int _stuffBytes;        // Padding at end of chunk
    private bool _hasMasterChunk;
    private bool _isFirstChunk = true;
    private long _streamLength;
    private bool _isDisposed;
    
    private long _lastAudioPts = -1;
    private long _lastVideoPts = -1;
    
    // Partial PES buffer for audio
    private readonly byte[] _pesBuffer = new byte[20];
    private int _pesBufCount;

    /// <summary>
    /// Creates a new TY demuxer for the given stream.
    /// </summary>
    public TyDemuxer(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _chunkBuffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        _streamLength = stream.CanSeek ? stream.Length : 0;
    }

    /// <summary>Gets detected tracks.</summary>
    public IReadOnlyList<TyTrack> Tracks => _tracks;
    
    /// <summary>Gets the TiVo series.</summary>
    public TivoSeries Series => _tivoSeries;
    
    /// <summary>Gets the TiVo type.</summary>
    public TivoType Type => _tivoType;
    
    /// <summary>Gets the audio codec type.</summary>
    public TyAudioType AudioType => _audioType;

    /// <summary>
    /// Probes a stream to detect TY content.
    /// </summary>
    public static TyProbeResult Probe(Stream stream)
    {
        if (!stream.CanRead)
            return new TyProbeResult { IsTy = false };

        Span<byte> header = stackalloc byte[12];
        long originalPos = stream.CanSeek ? stream.Position : 0;
        
        try
        {
            int read = stream.Read(header);
            if (read < 12)
                return new TyProbeResult { IsTy = false };

            // Check for TY file signature
            uint fileId = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint version = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            uint chunkSize = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);

            if (fileId == TivoFileId)
            {
                // Found TiVo signature - validate version and chunk size
                if (version == 0x02 && chunkSize == ChunkSize)
                {
                    return new TyProbeResult
                    {
                        IsTy = true,
                        Confidence = 100
                    };
                }
                // Wrong version or chunk size
                return new TyProbeResult { IsTy = false };
            }

            // Could be a TY file without master header - check for valid chunk
            int numRecs = header[0];
            if (numRecs >= 5 && numRecs < MaxRecordsPerChunk)
            {
                return new TyProbeResult
                {
                    IsTy = true,
                    Confidence = 40
                };
            }

            return new TyProbeResult { IsTy = false };
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = originalPos;
        }
    }

    /// <summary>
    /// Initializes the demuxer by probing the stream and setting up tracks.
    /// </summary>
    public bool Initialize()
    {
        if (_isDisposed)
            return false;

        // Probe the first few chunks to determine stream type
        if (!ProbeStream())
            return false;

        // Set up video track (always MPEG-2)
        _tracks.Add(new TyTrack
        {
            Index = 0,
            IsVideo = true,
            CodecId = "MPEG2VIDEO"
        });

        // Set up audio track
        _tracks.Add(new TyTrack
        {
            Index = 1,
            IsVideo = false,
            CodecId = _audioType == TyAudioType.Ac3 ? "AC3" : "MP2"
        });

        return true;
    }

    /// <summary>
    /// Reads the next packet from the stream.
    /// </summary>
    public bool ReadPacket(out TyPacket packet)
    {
        packet = default;

        if (_isDisposed)
            return false;

        // Load next chunk if needed
        if (_isFirstChunk || _currentRecord >= _numRecords)
        {
            if (!GetChunkHeader())
                return false;
        }

        // Get current record header
        if (_currentRecord >= _numRecords)
            return false;

        ref readonly var recHdr = ref _recordHeaders[_currentRecord];
        _currentRecord++;

        // Extended records have no payload (CC/XDS data)
        if (recHdr.IsExtended)
        {
            // Skip extended records for now (closed caption data)
            return ReadPacket(out packet);
        }

        // Skip zero-length records
        if (recHdr.RecordSize <= 0)
            return ReadPacket(out packet);

        // Read the record payload
        byte[] payload = ArrayPool<byte>.Shared.Rent(recHdr.RecordSize);
        try
        {
            int read = _stream.Read(payload, 0, recHdr.RecordSize);
            if (read != recHdr.RecordSize)
                return false;

            // Process based on record type
            bool isVideo = recHdr.RecordType == 0xe0;
            bool isAudio = recHdr.RecordType == 0xc0 || recHdr.RecordType == 0xbd;
            
            if (isVideo)
            {
                packet = ProcessVideoRecord(recHdr, payload.AsMemory(0, recHdr.RecordSize));
                return true;
            }
            else if (isAudio)
            {
                packet = ProcessAudioRecord(recHdr, payload.AsMemory(0, recHdr.RecordSize));
                return true;
            }
            
            // Unknown record type, skip and try next
            return ReadPacket(out packet);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    private TyPacket ProcessVideoRecord(in TyRecordHeader recHdr, ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        long pts = _lastVideoPts;
        bool isKeyframe = false;

        // Check for video PES header
        if (recHdr.SubRecordType != 0x02 && recHdr.SubRecordType != 0x0c &&
            recHdr.SubRecordType != 0x08 && span.Length > 4)
        {
            int pesOffset = FindPesHeader(VideoPacketHeader, span, Math.Min(span.Length - 4, 5));
            if (pesOffset >= 0 && pesOffset + VideoPtsOffset + 5 <= span.Length)
            {
                pts = ExtractPts(span.Slice(pesOffset + VideoPtsOffset));
                _lastVideoPts = pts;
            }
        }

        // Determine if keyframe based on sub-record type
        // 0x07 = sequence header, 0x08 = I-frame, 0x0c = GOP header
        isKeyframe = recHdr.SubRecordType == 0x07 || 
                     recHdr.SubRecordType == 0x08 || 
                     recHdr.SubRecordType == 0x0c;

        return new TyPacket
        {
            IsVideo = true,
            TrackIndex = 0,
            PresentationTimestampTicks = pts >= 0 ? pts * 100 / 9 : -1, // 90kHz to ticks
            Payload = data.ToArray(),
            IsKeyframe = isKeyframe
        };
    }

    private TyPacket ProcessAudioRecord(in TyRecordHeader recHdr, ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        long pts = _lastAudioPts;

        // Look for PES header based on audio type and sub-record type
        if (recHdr.SubRecordType == 0x03 || recHdr.SubRecordType == 0x04)
        {
            // MPEG audio
            var pesHeader = MpegAudioPacketHeader;
            int pesOffset = FindPesHeader(pesHeader, span, 5);
            if (pesOffset >= 0 && pesOffset + _ptsOffset + 5 <= span.Length)
            {
                pts = ExtractPts(span.Slice(pesOffset + _ptsOffset));
                _lastAudioPts = pts;
            }
        }
        else if (recHdr.SubRecordType == 0x09)
        {
            // AC-3 audio
            int pesOffset = FindPesHeader(Ac3AudioPacketHeader, span, 5);
            if (pesOffset >= 0 && pesOffset + Ac3PtsOffset + 5 <= span.Length)
            {
                pts = ExtractPts(span.Slice(pesOffset + Ac3PtsOffset));
                _lastAudioPts = pts;
            }
        }

        return new TyPacket
        {
            IsVideo = false,
            TrackIndex = 1,
            PresentationTimestampTicks = pts >= 0 ? pts * 100 / 9 : -1, // 90kHz to ticks
            Payload = data.ToArray(),
            IsKeyframe = false
        };
    }

    private bool ProbeStream()
    {
        // Peek at first 3 chunks to determine stream characteristics
        const int peekChunks = 3;
        byte[] peekBuffer = ArrayPool<byte>.Shared.Rent(peekChunks * ChunkSize);
        
        try
        {
            long startPos = _stream.CanSeek ? _stream.Position : 0;
            int totalRead = _stream.Read(peekBuffer, 0, peekChunks * ChunkSize);
            
            if (totalRead < ChunkSize)
                return false;

            // Check for master header
            uint fileId = BinaryPrimitives.ReadUInt32BigEndian(peekBuffer.AsSpan(0, 4));
            _hasMasterChunk = fileId == TivoFileId;

            // Analyze chunks
            int offset = _hasMasterChunk ? ChunkSize : 0; // Skip master if present
            
            for (int i = 0; i < peekChunks && offset + ChunkSize <= totalRead; i++)
            {
                AnalyzeChunk(peekBuffer.AsSpan(offset, ChunkSize));
                
                if (_tivoSeries != TivoSeries.Unknown &&
                    _audioType != TyAudioType.Unknown &&
                    _tivoType != TivoType.Unknown)
                    break;
                    
                offset += ChunkSize;
            }

            // Seek back to start
            if (_stream.CanSeek)
                _stream.Position = startPos;

            // Set PES length and PTS offset based on detected types
            ConfigurePesParameters();

            return _tivoSeries != TivoSeries.Unknown || _audioType != TyAudioType.Unknown;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(peekBuffer);
        }
    }

    private void AnalyzeChunk(ReadOnlySpan<byte> chunk)
    {
        // Skip master chunk headers
        if (BinaryPrimitives.ReadUInt32BigEndian(chunk) == TivoFileId)
            return;

        int numRecs = chunk[0];
        if (numRecs < 5 || numRecs >= MaxRecordsPerChunk)
            return;

        // Parse record headers and count types
        int num6e0 = 0, numBe0 = 0, num9c0 = 0, num3c0 = 0;
        
        for (int i = 0; i < numRecs; i++)
        {
            var recSpan = chunk.Slice(ChunkHeaderSize + i * RecordHeaderSize, RecordHeaderSize);
            byte recType = recSpan[3];
            byte subRecType = (byte)(recSpan[2] & 0x0f);
            int combined = (subRecType << 8) | recType;

            switch (combined)
            {
                case 0x6e0: num6e0++; break;
                case 0xbe0: numBe0++; break;
                case 0x3c0: num3c0++; break;
                case 0x9c0: num9c0++; break;
            }
        }

        // Determine series from video packet types
        if (num6e0 > 0)
            _tivoSeries = TivoSeries.Series1;
        else if (numBe0 > 0)
            _tivoSeries = TivoSeries.Series2;

        // Determine audio type
        if (num9c0 > 0)
        {
            _audioType = TyAudioType.Ac3;
            _tivoType = TivoType.DirecTv;
        }
        else if (num3c0 > 0)
        {
            _audioType = TyAudioType.Mpeg;
        }
    }

    private void ConfigurePesParameters()
    {
        // Set PES header length based on series
        _pesLength = _tivoSeries switch
        {
            TivoSeries.Series1 => Series1PesLength,
            TivoSeries.Series2 => Series2PesLength,
            _ => Series1PesLength
        };

        // Override for AC-3
        if (_audioType == TyAudioType.Ac3)
            _pesLength = Ac3PesLength;

        // Set PTS offset based on TiVo type
        _ptsOffset = _tivoType switch
        {
            TivoType.StandAlone => SaPtsOffset,
            TivoType.DirecTv => _audioType == TyAudioType.Ac3 ? Ac3PtsOffset : DtivosPtsOffset,
            _ => SaPtsOffset
        };
    }

    private bool GetChunkHeader()
    {
        // Skip any padding from previous chunk
        if (_stuffBytes > 0 && _stream.CanSeek)
        {
            _stream.Seek(_stuffBytes, SeekOrigin.Current);
            _stuffBytes = 0;
        }

        // Read chunk header (4 bytes)
        Span<byte> header = stackalloc byte[4];
        if (_stream.Read(header) < 4)
            return false;

        _currentChunk++;

        // Check for EOF
        if (BinaryPrimitives.ReadUInt32BigEndian(header) == 0)
            return false;

        // Check for master chunk (part header)
        if (BinaryPrimitives.ReadUInt32BigEndian(header) == TivoFileId)
        {
            // Skip master chunk and try again
            if (_stream.CanSeek)
            {
                _stream.Seek(ChunkSize - 4, SeekOrigin.Current);
                return GetChunkHeader();
            }
            return false;
        }

        // Parse record count
        bool is16Bit = (header[3] & 0x80) != 0;
        if (is16Bit)
        {
            _numRecords = (header[1] << 8) | header[0];
        }
        else
        {
            _numRecords = header[0];
        }

        if (_numRecords <= 0 || _numRecords >= MaxRecordsPerChunk)
            return false;

        _currentRecord = 0;
        _isFirstChunk = false;

        // Read record headers
        int headerBytes = _numRecords * RecordHeaderSize;
        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(headerBytes);
        
        try
        {
            if (_stream.Read(headerBuffer, 0, headerBytes) < headerBytes)
                return false;

            _recordHeaders = ParseRecordHeaders(headerBuffer.AsSpan(0, headerBytes), _numRecords, out int payloadSize);
            _stuffBytes = ChunkSize - 4 - headerBytes - payloadSize;
            
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    private static TyRecordHeader[] ParseRecordHeaders(ReadOnlySpan<byte> buffer, int numRecs, out int payloadSize)
    {
        payloadSize = 0;
        var headers = new TyRecordHeader[numRecs];

        for (int i = 0; i < numRecs; i++)
        {
            var recSpan = buffer.Slice(i * RecordHeaderSize, RecordHeaderSize);
            
            byte recType = recSpan[3];
            byte subRecType = (byte)(recSpan[2] & 0x0f);
            bool isExtended = (recSpan[0] & 0x80) != 0;

            int recSize;
            byte ext0 = 0, ext1 = 0;
            long tyPts = 0;

            if (isExtended)
            {
                // Extended record - extract CC/XDS data
                ext0 = (byte)(((recSpan[0] & 0x0f) << 4) | ((recSpan[1] & 0xf0) >> 4));
                ext1 = (byte)(((recSpan[1] & 0x0f) << 4) | ((recSpan[2] & 0xf0) >> 4));
                recSize = 0;
            }
            else
            {
                // Normal record
                recSize = ((recSpan[0] << 8) | recSpan[1]) << 4 | (recSpan[2] >> 4);
                tyPts = BinaryPrimitives.ReadInt64BigEndian(recSpan.Slice(8, 8));
                payloadSize += recSize;
            }

            headers[i] = new TyRecordHeader(recSize, recType, subRecType, ext0, ext1, isExtended, tyPts);
        }

        return headers;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindPesHeader(ReadOnlySpan<byte> header, ReadOnlySpan<byte> buffer, int searchLen)
    {
        int maxSearch = Math.Min(buffer.Length - header.Length, searchLen);
        for (int i = 0; i <= maxSearch; i++)
        {
            if (buffer.Slice(i, header.Length).SequenceEqual(header))
                return i;
        }
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ExtractPts(ReadOnlySpan<byte> buffer)
    {
        // MPEG PTS is 33 bits spread across 5 bytes
        // Format: 0010xxx1 xxxxxxxx xxxxxxx1 xxxxxxxx xxxxxxx1
        if (buffer.Length < 5)
            return -1;

        // Verify marker bits
        if ((buffer[0] & 0x01) != 1 || (buffer[2] & 0x01) != 1 || (buffer[4] & 0x01) != 1)
            return -1;

        long pts = ((long)(buffer[0] & 0x0e) << 29) |
                   ((long)buffer[1] << 22) |
                   ((long)(buffer[2] & 0xfe) << 14) |
                   ((long)buffer[3] << 7) |
                   ((long)(buffer[4] & 0xfe) >> 1);

        return pts;
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        ArrayPool<byte>.Shared.Return(_chunkBuffer);
        _isDisposed = true;
    }
}

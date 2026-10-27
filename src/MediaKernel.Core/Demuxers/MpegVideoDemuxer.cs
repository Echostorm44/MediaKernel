// MediaKernel - MPEG-1/2 Video Elementary Stream Demuxer
// Parses raw MPEG video elementary streams (.m1v, .m2v, .mpgv files)
// Reference: VLC's demux/mpeg/mpgv.c, ISO/IEC 11172-2, ISO/IEC 13818-2

using System.Buffers;
using MediaKernel.Core.Codecs.Mpeg2;
using MediaKernel.Core.Packetizers;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Result from probing a stream for MPEG-1/2 video content.
/// </summary>
public readonly struct MpegVideoProbeResult
{
    /// <summary>Whether valid MPEG video content was detected.</summary>
    public bool IsMpegVideo { get; init; }
    
    /// <summary>Confidence level 0-100.</summary>
    public int Confidence { get; init; }
    
    /// <summary>Detected MPEG version (1 or 2).</summary>
    public MpegVideoVersion Version { get; init; }
    
    /// <summary>Frame width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Frame height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Frame rate.</summary>
    public double FrameRate { get; init; }
}

/// <summary>
/// Demuxer for raw MPEG-1/2 video elementary streams.
/// </summary>
public sealed class MpegVideoDemuxer : IDisposable
{
    private const int MaxSyncSearchSize = 65536;
    private const int SequenceHeaderCode = 0xB3;
    private const int PictureStartCode = 0x00;
    private const int GroupStartCode = 0xB8;
    
    private readonly Stream _stream;
    private readonly MpegVideoPacketizer _packetizer;
    private long _streamLength;
    private long _currentPts;
    private long _frameDurationTicks;
    private int _frameCount;
    private long _dataStartPosition;
    private byte[] _readBuffer = new byte[4096];
    private bool _disposed;
    private Mpeg2SequenceHeader _sequenceHeader;
    private bool _hasSequenceHeader;
    
    /// <summary>MPEG video version (1 or 2).</summary>
    public MpegVideoVersion Version => _packetizer.Version;
    
    /// <summary>Frame width in pixels.</summary>
    public int Width => _packetizer.Width;
    
    /// <summary>Frame height in pixels.</summary>
    public int Height => _packetizer.Height;
    
    /// <summary>Frame rate.</summary>
    public double FrameRate => _packetizer.FrameRate;
    
    /// <summary>Whether the stream is progressive (not interlaced).</summary>
    public bool IsProgressive => _packetizer.IsProgressive;
    
    /// <summary>Duration of the stream.</summary>
    public TimeSpan Duration { get; private set; }
    
    /// <summary>Codec identifier.</summary>
    public string Codec => _packetizer.Version == MpegVideoVersion.Mpeg1 ? "mpeg1video" : "mpeg2video";
    
    /// <summary>Creates a new MPEG video demuxer.</summary>
    /// <param name="stream">Source stream containing raw MPEG video data.</param>
    public MpegVideoDemuxer(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _packetizer = new MpegVideoPacketizer();
    }
    
    /// <summary>
    /// Probes a stream to check if it contains valid MPEG video data.
    /// </summary>
    /// <param name="stream">Stream to probe (position will be reset).</param>
    /// <returns>Probe result with detection info.</returns>
    public static MpegVideoProbeResult Probe(Stream stream)
    {
        if (!stream.CanSeek)
            return new MpegVideoProbeResult { IsMpegVideo = false };
        
        long startPosition = stream.Position;
        
        try
        {
            int probeSize = (int)Math.Min(MaxSyncSearchSize, stream.Length - stream.Position);
            if (probeSize < 8)
                return new MpegVideoProbeResult { IsMpegVideo = false };
            
            byte[] buffer = new byte[probeSize];
            int bytesRead = stream.Read(buffer, 0, probeSize);
            if (bytesRead < 8)
                return new MpegVideoProbeResult { IsMpegVideo = false };
            
            var data = buffer.AsSpan(0, bytesRead);
            
            // Find sequence header start code (00 00 01 B3)
            int seqOffset = FindSequenceHeader(data);
            if (seqOffset < 0)
                return new MpegVideoProbeResult { IsMpegVideo = false };
            
            // Try to parse sequence header
            var seqData = data[seqOffset..];
            if (seqData.Length < 12)
                return new MpegVideoProbeResult { IsMpegVideo = false };
            
            if (!TryParseSequenceHeader(seqData, out var header))
                return new MpegVideoProbeResult { IsMpegVideo = false };
            
            int confidence = 70;
            
            // Look for picture start code to increase confidence
            int picOffset = FindStartCode(data[(seqOffset + 8)..], PictureStartCode);
            if (picOffset >= 0)
                confidence = 90;
            
            // Check for MPEG-2 extension to determine version
            var version = MpegVideoVersion.Mpeg1;
            int extOffset = FindStartCode(data[(seqOffset + 8)..], 0xB5); // Extension start code
            if (extOffset >= 0)
                version = MpegVideoVersion.Mpeg2;
            
            return new MpegVideoProbeResult
            {
                IsMpegVideo = true,
                Confidence = confidence,
                Version = version,
                Width = header.Width,
                Height = header.Height,
                FrameRate = header.FrameRate
            };
        }
        finally
        {
            stream.Position = startPosition;
        }
    }
    
    /// <summary>
    /// Parses stream headers and prepares for frame extraction.
    /// </summary>
    /// <returns>True if valid MPEG video was found.</returns>
    public bool Parse()
    {
        _streamLength = _stream.CanSeek ? _stream.Length : 0;
        
        var buffer = ArrayPool<byte>.Shared.Rent(MaxSyncSearchSize);
        try
        {
            long initialPosition = _stream.CanSeek ? _stream.Position : 0;
            int bytesRead = _stream.Read(buffer, 0, MaxSyncSearchSize);
            if (bytesRead < 8)
                return false;
            
            var data = buffer.AsSpan(0, bytesRead);
            
            // Find sequence header
            int seqOffset = FindSequenceHeader(data);
            if (seqOffset < 0)
                return false;
            
            _dataStartPosition = initialPosition + seqOffset;
            
            // Parse sequence header
            var seqData = data[seqOffset..];
            if (!TryParseSequenceHeader(seqData, out _sequenceHeader))
                return false;
            
            _hasSequenceHeader = true;
            
            // Feed initial data to packetizer to detect version
            _packetizer.Feed(seqData);
            
            // Calculate frame duration
            if (_packetizer.FrameRate > 0)
            {
                _frameDurationTicks = (long)(TimeSpan.TicksPerSecond / _packetizer.FrameRate);
            }
            else if (_sequenceHeader.FrameRate > 0)
            {
                _frameDurationTicks = (long)(TimeSpan.TicksPerSecond / _sequenceHeader.FrameRate);
            }
            else
            {
                _frameDurationTicks = TimeSpan.TicksPerSecond / 25; // Default 25fps
            }
            
            // Estimate duration
            EstimateDuration();
            
            // Reset to data start
            if (_stream.CanSeek)
            {
                _stream.Position = _dataStartPosition;
            }
            
            _packetizer.Reset();
            _currentPts = 0;
            _frameCount = 0;
            
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    
    /// <summary>
    /// Reads the next video frame from the stream.
    /// </summary>
    /// <returns>The demuxer packet containing frame data, or null at end of stream.</returns>
    public DemuxerPacket? ReadFrame()
    {
        // Try to get picture from packetizer first
        MpegVideoPicture picture;
        while (!_packetizer.TryGetPicture(out picture))
        {
            // Read more data from stream
            int bytesRead = _stream.Read(_readBuffer, 0, _readBuffer.Length);
            if (bytesRead == 0)
            {
                // End of stream - flush packetizer
                _packetizer.Flush();
                if (_packetizer.TryGetPicture(out picture))
                {
                    return CreatePacket(picture);
                }
                return null;
            }
            
            _packetizer.Feed(_readBuffer.AsSpan(0, bytesRead), _currentPts, _currentPts);
        }
        
        return CreatePacket(picture);
    }
    
    private DemuxerPacket CreatePacket(MpegVideoPicture picture)
    {
        long pts = picture.PresentationTimestamp >= 0 ? picture.PresentationTimestamp : _currentPts;
        long dts = picture.DecodeTimestamp >= 0 ? picture.DecodeTimestamp : pts;
        
        byte[] data = picture.Data.ToArray();
        
        _currentPts += _frameDurationTicks;
        _frameCount++;
        
        return new DemuxerPacket(
            trackId: 0,
            presentationTimestamp: pts,
            decodeTimestamp: dts,
            duration: _frameDurationTicks,
            isKeyframe: picture.IsKeyframe,
            data: data
        );
    }
    
    /// <summary>
    /// Seeks to a position in the stream.
    /// </summary>
    /// <param name="timestamp">Target timestamp.</param>
    /// <returns>Result of the seek operation.</returns>
    public SeekResult Seek(TimeSpan timestamp)
    {
        if (!_stream.CanSeek)
            return SeekResult.Failed("Stream is not seekable");
        
        if (Duration <= TimeSpan.Zero)
            return SeekResult.Failed("Duration unknown");
        
        // Calculate approximate position based on timestamp ratio
        double ratio = timestamp.TotalSeconds / Duration.TotalSeconds;
        long videoDataSize = _streamLength - _dataStartPosition;
        long targetPosition = _dataStartPosition + (long)(videoDataSize * ratio);
        
        _stream.Position = Math.Max(_dataStartPosition, Math.Min(targetPosition, _streamLength));
        
        // Find next sequence header or I-frame for accurate positioning
        var buffer = ArrayPool<byte>.Shared.Rent(MaxSyncSearchSize);
        try
        {
            int bytesRead = _stream.Read(buffer, 0, buffer.Length);
            
            // Look for sequence header first
            int seqOffset = FindSequenceHeader(buffer.AsSpan(0, bytesRead));
            if (seqOffset >= 0)
            {
                _stream.Position = _stream.Position - bytesRead + seqOffset;
            }
            else
            {
                // Look for any start code
                int startOffset = FindStartCode(buffer.AsSpan(0, bytesRead), -1);
                if (startOffset >= 0)
                {
                    _stream.Position = _stream.Position - bytesRead + startOffset;
                }
                else
                {
                    _stream.Position = _dataStartPosition;
                    _currentPts = 0;
                    _packetizer.Reset();
                    return SeekResult.Failed("Could not find sync after seek");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        
        _currentPts = timestamp.Ticks;
        _packetizer.Reset();
        return SeekResult.Succeeded(_currentPts, isApproximate: true);
    }
    
    /// <summary>
    /// Gets track information for this video stream.
    /// </summary>
    public DemuxerTrackInfo GetTrackInfo()
    {
        string name = _packetizer.Version == MpegVideoVersion.Mpeg1
            ? "MPEG-1 Video"
            : "MPEG-2 Video";
        
        return new DemuxerTrackInfo
        {
            TrackId = 0,
            TrackType = DemuxerTrackType.Video,
            CodecId = Codec,
            Name = name,
            Width = Width,
            Height = Height,
            FrameRate = FrameRate
        };
    }
    
    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _packetizer.Dispose();
            _disposed = true;
        }
    }
    
    private static int FindSequenceHeader(ReadOnlySpan<byte> data)
    {
        // Search for 00 00 01 B3
        for (int i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] == 0x00 && data[i + 1] == 0x00 && 
                data[i + 2] == 0x01 && data[i + 3] == SequenceHeaderCode)
            {
                return i;
            }
        }
        return -1;
    }
    
    private static int FindStartCode(ReadOnlySpan<byte> data, int startCode)
    {
        // Search for 00 00 01 xx
        for (int i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] == 0x00 && data[i + 1] == 0x00 && data[i + 2] == 0x01)
            {
                if (startCode < 0 || data[i + 3] == startCode)
                    return i;
            }
        }
        return -1;
    }
    
    private static bool TryParseSequenceHeader(ReadOnlySpan<byte> data, out Mpeg2SequenceHeader header)
    {
        header = default;
        
        // Minimum sequence header: start code (4) + width/height (3) + aspect/rate (1) + bitrate (3) = 11 bytes
        if (data.Length < 12)
            return false;
        
        // Verify start code
        if (data[0] != 0x00 || data[1] != 0x00 || data[2] != 0x01 || data[3] != SequenceHeaderCode)
            return false;
        
        // Parse horizontal_size (12 bits) and vertical_size (12 bits)
        int width = (data[4] << 4) | (data[5] >> 4);
        int height = ((data[5] & 0x0F) << 8) | data[6];
        
        if (width == 0 || height == 0)
            return false;
        
        // aspect_ratio_information (4 bits) + frame_rate_code (4 bits)
        int frameRateCode = data[7] & 0x0F;
        
        double frameRate = frameRateCode switch
        {
            1 => 24000.0 / 1001.0, // 23.976
            2 => 24.0,
            3 => 25.0,
            4 => 30000.0 / 1001.0, // 29.97
            5 => 30.0,
            6 => 50.0,
            7 => 60000.0 / 1001.0, // 59.94
            8 => 60.0,
            _ => 25.0 // Default
        };
        
        // bit_rate_value (18 bits) starting at bit 64
        int bitrateValue = ((data[8] << 10) | (data[9] << 2) | (data[10] >> 6)) & 0x3FFFF;
        int bitrate = bitrateValue * 400; // Units of 400 bps
        
        header = new Mpeg2SequenceHeader
        {
            Width = width,
            Height = height,
            FrameRate = frameRate,
            Bitrate = bitrate
        };
        
        return true;
    }
    
    private void EstimateDuration()
    {
        if (_streamLength <= 0)
            return;
        
        long videoDataSize = _streamLength - _dataStartPosition;
        
        // If we have bitrate, calculate duration
        if (_sequenceHeader.Bitrate > 0)
        {
            double seconds = videoDataSize * 8.0 / _sequenceHeader.Bitrate;
            Duration = TimeSpan.FromSeconds(seconds);
        }
        else if (_frameDurationTicks > 0)
        {
            // Estimate average frame size (rough approximation)
            // Assume 1MB/minute as rough estimate for MPEG-2
            double avgBitrate = 8_000_000; // 8 Mbps default estimate
            double seconds = videoDataSize * 8.0 / avgBitrate;
            Duration = TimeSpan.FromSeconds(seconds);
        }
    }
}

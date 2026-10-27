// MediaKernel - DTS (Digital Theater Systems) Elementary Stream Demuxer
// Parses raw DTS audio elementary streams (.dts files)
// Reference: VLC's demux/mpeg/es.c, ETSI TS 102 114

using System.Buffers;
using MediaKernel.Core.Packetizers;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Result from probing a stream for DTS audio content.
/// </summary>
public readonly struct DtsProbeResult
{
    /// <summary>Whether valid DTS audio content was detected.</summary>
    public bool IsDts { get; init; }
    
    /// <summary>Confidence level 0-100.</summary>
    public int Confidence { get; init; }
    
    /// <summary>DTS profile (DTS, DTS-HD, DTS Express).</summary>
    public DtsProfile Profile { get; init; }
    
    /// <summary>Sync word type detected.</summary>
    public DtsSyncType SyncType { get; init; }
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }
    
    /// <summary>Number of audio channels (including LFE).</summary>
    public int ChannelCount { get; init; }
    
    /// <summary>Bitrate in bps.</summary>
    public int Bitrate { get; init; }
}

/// <summary>
/// Demuxer for raw DTS (Digital Theater Systems) audio elementary streams.
/// Supports DTS Core, DTS-HD Master Audio, DTS-HD High Resolution, and DTS Express.
/// </summary>
public sealed class DtsDemuxer : IDisposable
{
    private const int MaxSyncSearchSize = 16384;
    private const int MinHeaderSize = 14;
    
    private readonly Stream _stream;
    private long _streamLength;
    private DtsFrameHeader _firstFrameHeader;
    private long _currentPts;
    private long _frameDurationTicks;
    private int _frameCount;
    private long _dataStartPosition;
    private byte[] _readBuffer = new byte[8192];
    private bool _disposed;
    
    /// <summary>DTS profile type (DTS Core, DTS-HD, DTS Express).</summary>
    public DtsProfile Profile => _firstFrameHeader.Profile;
    
    /// <summary>Sync word type detected.</summary>
    public DtsSyncType SyncType => _firstFrameHeader.SyncType;
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate => _firstFrameHeader.SampleRate;
    
    /// <summary>Number of audio channels (including LFE).</summary>
    public int ChannelCount => _firstFrameHeader.TotalChannels;
    
    /// <summary>Bitrate in bps.</summary>
    public int Bitrate => _firstFrameHeader.Bitrate;
    
    /// <summary>Number of samples per frame.</summary>
    public int SamplesPerFrame => _firstFrameHeader.SampleCount;
    
    /// <summary>Duration of the stream.</summary>
    public TimeSpan Duration { get; private set; }
    
    /// <summary>Codec identifier.</summary>
    public string Codec => _firstFrameHeader.Profile switch
    {
        DtsProfile.DtsHd => "dtshd",
        DtsProfile.DtsExpress => "dtse",
        _ => "dts"
    };
    
    /// <summary>Creates a new DTS demuxer.</summary>
    /// <param name="stream">Source stream containing raw DTS data.</param>
    public DtsDemuxer(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }
    
    /// <summary>
    /// Probes a stream to check if it contains valid DTS audio data.
    /// </summary>
    /// <param name="stream">Stream to probe (position will be reset).</param>
    /// <returns>Probe result with detection info.</returns>
    public static DtsProbeResult Probe(Stream stream)
    {
        if (!stream.CanSeek)
            return new DtsProbeResult { IsDts = false };
        
        long startPosition = stream.Position;
        
        try
        {
            int probeSize = (int)Math.Min(MaxSyncSearchSize, stream.Length - stream.Position);
            if (probeSize < MinHeaderSize)
                return new DtsProbeResult { IsDts = false };
            
            byte[] buffer = new byte[probeSize];
            int bytesRead = stream.Read(buffer, 0, probeSize);
            if (bytesRead < MinHeaderSize)
                return new DtsProbeResult { IsDts = false };
            
            var data = buffer.AsSpan(0, bytesRead);
            
            // Try to find DTS sync
            int syncOffset = DtsPacketizer.FindSyncWord(data);
            if (syncOffset < 0)
                return new DtsProbeResult { IsDts = false };
            
            var frameData = data[syncOffset..];
            
            // Try to parse first frame
            if (!DtsPacketizer.TryParseHeader(frameData, out var header))
                return new DtsProbeResult { IsDts = false };
            
            int confidence = 60;
            
            // Verify by checking next frame
            int nextFrameOffset = syncOffset + header.FrameSize;
            if (nextFrameOffset + MinHeaderSize <= bytesRead)
            {
                var nextData = data[nextFrameOffset..];
                
                if (DtsPacketizer.TryParseHeader(nextData, out var nextHeader))
                {
                    if (nextHeader.SampleRate == header.SampleRate &&
                        nextHeader.TotalChannels == header.TotalChannels)
                    {
                        confidence = 90;
                    }
                }
            }
            
            return new DtsProbeResult
            {
                IsDts = true,
                Confidence = confidence,
                Profile = header.Profile,
                SyncType = header.SyncType,
                SampleRate = header.SampleRate,
                ChannelCount = header.TotalChannels,
                Bitrate = header.Bitrate
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
    /// <returns>True if valid DTS was found.</returns>
    public bool Parse()
    {
        _streamLength = _stream.CanSeek ? _stream.Length : 0;
        
        var buffer = ArrayPool<byte>.Shared.Rent(MaxSyncSearchSize);
        try
        {
            long initialPosition = _stream.CanSeek ? _stream.Position : 0;
            int bytesRead = _stream.Read(buffer, 0, MaxSyncSearchSize);
            if (bytesRead < MinHeaderSize)
                return false;
            
            var data = buffer.AsSpan(0, bytesRead);
            
            // Find first DTS sync
            int syncOffset = DtsPacketizer.FindSyncWord(data);
            if (syncOffset < 0)
                return false;
            
            _dataStartPosition = initialPosition + syncOffset;
            
            var frameData = data[syncOffset..];
            
            // Parse first frame header
            if (!DtsPacketizer.TryParseHeader(frameData, out _firstFrameHeader))
                return false;
            
            // Calculate frame duration
            if (_firstFrameHeader.SampleRate > 0 && _firstFrameHeader.SampleCount > 0)
            {
                _frameDurationTicks = TimeSpan.TicksPerSecond * _firstFrameHeader.SampleCount / _firstFrameHeader.SampleRate;
            }
            else
            {
                // Default: 512 samples per block, typically 32 blocks = 1024 samples at 48kHz
                _frameDurationTicks = TimeSpan.TicksPerSecond * 512 / 48000;
            }
            
            // Estimate duration
            EstimateDuration();
            
            // Reset to data start
            if (_stream.CanSeek)
            {
                _stream.Position = _dataStartPosition;
            }
            
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
    /// Reads the next audio frame from the stream.
    /// </summary>
    /// <returns>The demuxer packet containing frame data, or null at end of stream.</returns>
    public DemuxerPacket? ReadFrame()
    {
        // Read enough for header
        int bytesRead = _stream.Read(_readBuffer, 0, MinHeaderSize + 8);
        if (bytesRead < MinHeaderSize)
            return null;
        
        var headerData = _readBuffer.AsSpan(0, bytesRead);
        
        // Find sync if needed
        int attempts = 0;
        while (!DtsPacketizer.HasSyncWord(headerData) && attempts < 1000)
        {
            int syncOffset = DtsPacketizer.FindSyncWord(headerData);
            if (syncOffset > 0 && syncOffset < bytesRead - MinHeaderSize)
            {
                // Move to sync position
                Array.Copy(_readBuffer, syncOffset, _readBuffer, 0, bytesRead - syncOffset);
                int additional = _stream.Read(_readBuffer, bytesRead - syncOffset, syncOffset);
                bytesRead = bytesRead - syncOffset + additional;
                headerData = _readBuffer.AsSpan(0, bytesRead);
            }
            else
            {
                bytesRead = _stream.Read(_readBuffer, 0, MinHeaderSize + 8);
                if (bytesRead < MinHeaderSize)
                    return null;
                headerData = _readBuffer.AsSpan(0, bytesRead);
            }
            attempts++;
        }
        
        if (!DtsPacketizer.HasSyncWord(headerData))
            return null;
        
        // Parse header
        if (!DtsPacketizer.TryParseHeader(headerData, out var header))
            return null;
        
        // Validate frame size
        if (header.FrameSize < MinHeaderSize || header.FrameSize > 65536)
            return null;
        
        // Ensure buffer is large enough
        if (_readBuffer.Length < header.FrameSize)
        {
            Array.Resize(ref _readBuffer, header.FrameSize + 1024);
        }
        
        // Read remaining bytes of the frame
        int bytesNeeded = header.FrameSize - bytesRead;
        if (bytesNeeded > 0)
        {
            int remaining = _stream.Read(_readBuffer, bytesRead, bytesNeeded);
            if (remaining < bytesNeeded)
                return null;
        }
        
        // Create packet with complete frame
        byte[] frameData = new byte[header.FrameSize];
        _readBuffer.AsSpan(0, header.FrameSize).CopyTo(frameData);
        
        long pts = _currentPts;
        long duration = header.SampleRate > 0 && header.SampleCount > 0
            ? TimeSpan.TicksPerSecond * header.SampleCount / header.SampleRate
            : _frameDurationTicks;
        
        _currentPts += duration;
        _frameCount++;
        
        return new DemuxerPacket(
            trackId: 0,
            presentationTimestamp: pts,
            decodeTimestamp: pts,
            duration: duration,
            isKeyframe: true,
            data: frameData
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
        long audioDataSize = _streamLength - _dataStartPosition;
        long targetPosition = _dataStartPosition + (long)(audioDataSize * ratio);
        
        _stream.Position = Math.Max(_dataStartPosition, Math.Min(targetPosition, _streamLength));
        
        // Find next sync
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int bytesRead = _stream.Read(buffer, 0, buffer.Length);
            int syncOffset = DtsPacketizer.FindSyncWord(buffer.AsSpan(0, bytesRead));
            
            if (syncOffset >= 0)
            {
                _stream.Position = _stream.Position - bytesRead + syncOffset;
            }
            else
            {
                _stream.Position = _dataStartPosition;
                _currentPts = 0;
                return SeekResult.Failed("Could not find sync after seek");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        
        _currentPts = timestamp.Ticks;
        return SeekResult.Succeeded(_currentPts, isApproximate: true);
    }
    
    /// <summary>
    /// Gets track information for this audio stream.
    /// </summary>
    public DemuxerTrackInfo GetTrackInfo()
    {
        string name = _firstFrameHeader.Profile switch
        {
            DtsProfile.DtsHd => "DTS-HD Master Audio",
            DtsProfile.DtsExpress => "DTS Express (LBR)",
            _ => "DTS Digital Surround"
        };
        
        return new DemuxerTrackInfo
        {
            TrackId = 0,
            TrackType = DemuxerTrackType.Audio,
            CodecId = Codec,
            Name = name,
            SampleRate = _firstFrameHeader.SampleRate,
            ChannelCount = _firstFrameHeader.TotalChannels,
            BitsPerSample = 24  // DTS core is 24-bit, DTS-HD can be higher
        };
    }
    
    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
    
    private void EstimateDuration()
    {
        if (_streamLength <= 0 || _firstFrameHeader.SampleRate <= 0)
            return;
        
        long audioDataSize = _streamLength - _dataStartPosition;
        
        if (_firstFrameHeader.FrameSize > 0 && _firstFrameHeader.SampleCount > 0)
        {
            // Estimate frames from average frame size
            double estimatedFrames = (double)audioDataSize / _firstFrameHeader.FrameSize;
            double samplesTotal = estimatedFrames * _firstFrameHeader.SampleCount;
            Duration = TimeSpan.FromSeconds(samplesTotal / _firstFrameHeader.SampleRate);
        }
        else if (_firstFrameHeader.Bitrate > 0)
        {
            // Fallback to bitrate calculation
            double seconds = audioDataSize * 8.0 / _firstFrameHeader.Bitrate;
            Duration = TimeSpan.FromSeconds(seconds);
        }
    }
}

// MediaKernel - TrueHD/MLP (Meridian Lossless Packing) Elementary Stream Demuxer
// Parses raw TrueHD and MLP lossless audio elementary streams (.thd, .mlp files)
// Reference: VLC's demux/mpeg/es.c, Dolby TrueHD spec

using System.Buffers;
using MediaKernel.Core.Packetizers;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Result from probing a stream for TrueHD/MLP audio content.
/// </summary>
public readonly struct MlpProbeResult
{
    /// <summary>Whether valid TrueHD/MLP audio content was detected.</summary>
    public bool IsMlp { get; init; }
    
    /// <summary>Confidence level 0-100.</summary>
    public int Confidence { get; init; }
    
    /// <summary>Stream type (MLP or TrueHD).</summary>
    public MlpStreamType StreamType { get; init; }
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }
    
    /// <summary>Number of audio channels.</summary>
    public int ChannelCount { get; init; }
    
    /// <summary>Bitrate in bps (0 for VBR).</summary>
    public int Bitrate { get; init; }
    
    /// <summary>Whether the stream uses variable bitrate.</summary>
    public bool IsVbr { get; init; }
}

/// <summary>
/// Demuxer for raw TrueHD and MLP (Meridian Lossless Packing) audio elementary streams.
/// </summary>
public sealed class MlpDemuxer : IDisposable
{
    private const int MaxSyncSearchSize = 65536;  // MLP can have large gaps between major syncs
    private const int MinHeaderSize = 8;
    private const int MajorSyncHeaderSize = 28;
    
    private readonly Stream _stream;
    private long _streamLength;
    private MlpFrameHeader _firstMajorSyncHeader;
    private MlpFrameHeader _lastMajorSync;
    private long _currentPts;
    private long _frameDurationTicks;
    private int _frameCount;
    private long _dataStartPosition;
    private byte[] _readBuffer = new byte[8192];
    private bool _disposed;
    
    /// <summary>Stream type (MLP for DVD-Audio or TrueHD for Blu-ray).</summary>
    public MlpStreamType StreamType => _firstMajorSyncHeader.StreamType;
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate => _firstMajorSyncHeader.SampleRate;
    
    /// <summary>Number of audio channels.</summary>
    public int ChannelCount => _firstMajorSyncHeader.ChannelCount;
    
    /// <summary>Bitrate in bps (0 for VBR).</summary>
    public int Bitrate => _firstMajorSyncHeader.Bitrate;
    
    /// <summary>Whether the stream uses variable bitrate.</summary>
    public bool IsVbr => _firstMajorSyncHeader.IsVbr;
    
    /// <summary>Number of samples per frame.</summary>
    public int SamplesPerFrame => _firstMajorSyncHeader.SampleCount;
    
    /// <summary>Duration of the stream.</summary>
    public TimeSpan Duration { get; private set; }
    
    /// <summary>Codec identifier.</summary>
    public string Codec => _firstMajorSyncHeader.StreamType == MlpStreamType.TrueHd ? "truehd" : "mlp";
    
    /// <summary>Creates a new TrueHD/MLP demuxer.</summary>
    /// <param name="stream">Source stream containing raw TrueHD/MLP data.</param>
    public MlpDemuxer(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }
    
    /// <summary>
    /// Probes a stream to check if it contains valid TrueHD/MLP audio data.
    /// </summary>
    /// <param name="stream">Stream to probe (position will be reset).</param>
    /// <returns>Probe result with detection info.</returns>
    public static MlpProbeResult Probe(Stream stream)
    {
        if (!stream.CanSeek)
            return new MlpProbeResult { IsMlp = false };
        
        long startPosition = stream.Position;
        
        try
        {
            int probeSize = (int)Math.Min(MaxSyncSearchSize, stream.Length - stream.Position);
            if (probeSize < MajorSyncHeaderSize)
                return new MlpProbeResult { IsMlp = false };
            
            byte[] buffer = new byte[probeSize];
            int bytesRead = stream.Read(buffer, 0, probeSize);
            if (bytesRead < MajorSyncHeaderSize)
                return new MlpProbeResult { IsMlp = false };
            
            var data = buffer.AsSpan(0, bytesRead);
            
            // Find major sync pattern
            int syncOffset = MlpPacketizer.FindMajorSync(data);
            if (syncOffset < 0)
                return new MlpProbeResult { IsMlp = false };
            
            var frameData = data[syncOffset..];
            
            // Try to parse major sync header
            if (!MlpPacketizer.TryParseMajorSyncHeader(frameData, out var header))
                return new MlpProbeResult { IsMlp = false };
            
            int confidence = 70;  // Major sync found
            
            // Verify by checking if we can find another frame
            if (header.FrameSize > 0)
            {
                int nextFrameOffset = syncOffset + header.FrameSize;
                if (nextFrameOffset + MinHeaderSize <= bytesRead)
                {
                    var nextData = data[nextFrameOffset..];
                    int nextFrameSize = MlpPacketizer.GetFrameSize(nextData);
                    
                    if (nextFrameSize >= MinHeaderSize && nextFrameSize <= 65536)
                    {
                        confidence = 90;
                    }
                }
            }
            
            return new MlpProbeResult
            {
                IsMlp = true,
                Confidence = confidence,
                StreamType = header.StreamType,
                SampleRate = header.SampleRate,
                ChannelCount = header.ChannelCount,
                Bitrate = header.Bitrate,
                IsVbr = header.IsVbr
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
    /// <returns>True if valid TrueHD/MLP was found.</returns>
    public bool Parse()
    {
        _streamLength = _stream.CanSeek ? _stream.Length : 0;
        
        var buffer = ArrayPool<byte>.Shared.Rent(MaxSyncSearchSize);
        try
        {
            long initialPosition = _stream.CanSeek ? _stream.Position : 0;
            int bytesRead = _stream.Read(buffer, 0, MaxSyncSearchSize);
            if (bytesRead < MajorSyncHeaderSize)
                return false;
            
            var data = buffer.AsSpan(0, bytesRead);
            
            // Find first major sync
            int syncOffset = MlpPacketizer.FindMajorSync(data);
            if (syncOffset < 0)
                return false;
            
            _dataStartPosition = initialPosition + syncOffset;
            
            var frameData = data[syncOffset..];
            
            // Parse first major sync header
            if (!MlpPacketizer.TryParseMajorSyncHeader(frameData, out _firstMajorSyncHeader))
                return false;
            
            _lastMajorSync = _firstMajorSyncHeader;
            
            // Calculate frame duration
            if (_firstMajorSyncHeader.SampleRate > 0 && _firstMajorSyncHeader.SampleCount > 0)
            {
                _frameDurationTicks = TimeSpan.TicksPerSecond * _firstMajorSyncHeader.SampleCount / _firstMajorSyncHeader.SampleRate;
            }
            else
            {
                // Default: 40 samples at 48kHz (unlikely but safe fallback)
                _frameDurationTicks = TimeSpan.TicksPerSecond * 40 / 48000;
            }
            
            // Estimate duration
            EstimateDuration(data, bytesRead);
            
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
        // Read enough for frame size header
        int bytesRead = _stream.Read(_readBuffer, 0, MinHeaderSize);
        if (bytesRead < 2)
            return null;
        
        // Get frame size from first two bytes
        int frameSize = MlpPacketizer.GetFrameSize(_readBuffer.AsSpan(0, bytesRead));
        if (frameSize < MinHeaderSize || frameSize > 65536)
        {
            // Try to resync
            return TryResyncAndRead();
        }
        
        // Ensure buffer is large enough
        if (_readBuffer.Length < frameSize)
        {
            Array.Resize(ref _readBuffer, frameSize + 1024);
        }
        
        // Read remaining bytes of the frame
        int bytesNeeded = frameSize - bytesRead;
        if (bytesNeeded > 0)
        {
            int remaining = _stream.Read(_readBuffer, bytesRead, bytesNeeded);
            if (remaining < bytesNeeded)
                return null;
            bytesRead += remaining;
        }
        
        // Parse header to get sample count for this frame
        var headerSpan = _readBuffer.AsSpan(0, bytesRead);
        if (MlpPacketizer.TryParseHeader(headerSpan, out var header))
        {
            // Update major sync cache if applicable
            if (header.IsMajorSync)
            {
                _lastMajorSync = header;
            }
        }
        
        // Create packet with complete frame
        byte[] frameData = new byte[frameSize];
        _readBuffer.AsSpan(0, frameSize).CopyTo(frameData);
        
        long pts = _currentPts;
        
        // Use sample count from header if available, else cached value
        int sampleCount = header.SampleCount > 0 ? header.SampleCount : _lastMajorSync.SampleCount;
        if (sampleCount == 0)
            sampleCount = _firstMajorSyncHeader.SampleCount;
        
        long duration = _lastMajorSync.SampleRate > 0 && sampleCount > 0
            ? TimeSpan.TicksPerSecond * sampleCount / _lastMajorSync.SampleRate
            : _frameDurationTicks;
        
        _currentPts += duration;
        _frameCount++;
        
        return new DemuxerPacket(
            trackId: 0,
            presentationTimestamp: pts,
            decodeTimestamp: pts,
            duration: duration,
            isKeyframe: header.IsMajorSync,
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
        
        // Find next major sync for accurate positioning
        var buffer = ArrayPool<byte>.Shared.Rent(MaxSyncSearchSize);
        try
        {
            int bytesRead = _stream.Read(buffer, 0, buffer.Length);
            int syncOffset = MlpPacketizer.FindMajorSync(buffer.AsSpan(0, bytesRead));
            
            if (syncOffset >= 0)
            {
                _stream.Position = _stream.Position - bytesRead + syncOffset;
                
                // Update cached header from major sync
                var frameData = buffer.AsSpan(syncOffset, bytesRead - syncOffset);
                if (MlpPacketizer.TryParseMajorSyncHeader(frameData, out var header))
                {
                    _lastMajorSync = header;
                }
            }
            else
            {
                // No major sync found, try to find any frame
                _stream.Position = _dataStartPosition;
                _currentPts = 0;
                return SeekResult.Failed("Could not find major sync after seek");
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
        string name = _firstMajorSyncHeader.StreamType == MlpStreamType.TrueHd
            ? "Dolby TrueHD"
            : "MLP (Meridian Lossless Packing)";
        
        return new DemuxerTrackInfo
        {
            TrackId = 0,
            TrackType = DemuxerTrackType.Audio,
            CodecId = Codec,
            Name = name,
            SampleRate = _firstMajorSyncHeader.SampleRate,
            ChannelCount = _firstMajorSyncHeader.ChannelCount,
            BitsPerSample = 24  // TrueHD/MLP support up to 24-bit
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
    
    private DemuxerPacket? TryResyncAndRead()
    {
        // Search for major sync
        var buffer = ArrayPool<byte>.Shared.Rent(MaxSyncSearchSize);
        try
        {
            // Copy what we already have
            _readBuffer.AsSpan(0, 8).CopyTo(buffer.AsSpan(0, 8));
            
            int bytesRead = 8 + _stream.Read(buffer, 8, MaxSyncSearchSize - 8);
            int syncOffset = MlpPacketizer.FindMajorSync(buffer.AsSpan(0, bytesRead));
            
            if (syncOffset >= 0)
            {
                // Seek to the sync position and try again
                _stream.Position -= bytesRead - syncOffset;
                return ReadFrame();
            }
            
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    
    private void EstimateDuration(ReadOnlySpan<byte> initialData, int initialDataLength)
    {
        if (_streamLength <= 0 || _firstMajorSyncHeader.SampleRate <= 0)
            return;
        
        long audioDataSize = _streamLength - _dataStartPosition;
        
        // Calculate average frame size from first N frames
        int totalFrameSize = 0;
        int frameCount = 0;
        int offset = 0;
        
        while (offset + MinHeaderSize < initialDataLength && frameCount < 100)
        {
            int frameSize = MlpPacketizer.GetFrameSize(initialData[offset..]);
            if (frameSize < MinHeaderSize || frameSize > 65536)
                break;
            
            if (offset + frameSize > initialDataLength)
                break;
            
            totalFrameSize += frameSize;
            frameCount++;
            offset += frameSize;
        }
        
        if (frameCount > 0)
        {
            double avgFrameSize = (double)totalFrameSize / frameCount;
            double estimatedFrames = audioDataSize / avgFrameSize;
            double samplesTotal = estimatedFrames * _firstMajorSyncHeader.SampleCount;
            Duration = TimeSpan.FromSeconds(samplesTotal / _firstMajorSyncHeader.SampleRate);
        }
        else if (_firstMajorSyncHeader.Bitrate > 0)
        {
            // Fallback to bitrate calculation
            double seconds = audioDataSize * 8.0 / _firstMajorSyncHeader.Bitrate;
            Duration = TimeSpan.FromSeconds(seconds);
        }
    }
}

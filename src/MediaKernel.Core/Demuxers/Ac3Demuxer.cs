// MediaKernel - AC-3 (Dolby Digital) Elementary Stream Demuxer
// Parses raw AC-3 audio elementary streams (.ac3, .a52 files)
// Reference: VLC's demux/mpeg/es.c, ATSC A/52

using System.Buffers;
using MediaKernel.Core.Codecs.AC3;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Result from probing a stream for AC-3 audio content.
/// </summary>
public readonly struct Ac3ProbeResult
{
    /// <summary>Whether valid AC-3 audio content was detected.</summary>
    public bool IsAc3 { get; init; }
    
    /// <summary>Confidence level 0-100.</summary>
    public int Confidence { get; init; }
    
    /// <summary>Whether this is E-AC-3 (Enhanced AC-3).</summary>
    public bool IsEnhancedAc3 { get; init; }
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }
    
    /// <summary>Number of audio channels.</summary>
    public int ChannelCount { get; init; }
    
    /// <summary>Bitrate in bps.</summary>
    public int Bitrate { get; init; }
}

/// <summary>
/// Demuxer for raw AC-3/E-AC-3 (Dolby Digital) audio elementary streams.
/// </summary>
public sealed class Ac3Demuxer : IDisposable
{
    private const int MaxSyncSearchSize = 16384;
    private const int SamplesPerFrame = 256 * 6;  // 6 audio blocks × 256 samples
    
    private readonly Stream _stream;
    private long _streamLength;
    private Ac3SyncFrame _firstFrameHeader;
    private long _currentPts;
    private long _frameDurationTicks;
    private int _frameCount;
    private long _dataStartPosition;
    private byte[] _readBuffer = new byte[4096];
    private bool _disposed;
    private bool _bigEndian;
    
    /// <summary>Whether the stream is E-AC-3 (Enhanced AC-3).</summary>
    public bool IsEnhancedAc3 => _firstFrameHeader.IsEnhancedAc3;
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate => _firstFrameHeader.SampleRate;
    
    /// <summary>Number of audio channels (including LFE).</summary>
    public int ChannelCount => _firstFrameHeader.TotalChannels;
    
    /// <summary>Bitrate in bps.</summary>
    public int Bitrate => _firstFrameHeader.Bitrate;
    
    /// <summary>Duration of the stream.</summary>
    public TimeSpan Duration { get; private set; }
    
    /// <summary>Codec identifier.</summary>
    public string Codec => _firstFrameHeader.IsEnhancedAc3 ? "eac3" : "ac3";
    
    /// <summary>Creates a new AC-3 demuxer.</summary>
    /// <param name="stream">Source stream containing raw AC-3 data.</param>
    public Ac3Demuxer(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }
    
    /// <summary>
    /// Probes a stream to check if it contains valid AC-3 audio data.
    /// </summary>
    /// <param name="stream">Stream to probe (position will be reset).</param>
    /// <returns>Probe result with detection info.</returns>
    public static Ac3ProbeResult Probe(Stream stream)
    {
        if (!stream.CanSeek)
            return new Ac3ProbeResult { IsAc3 = false };
        
        long startPosition = stream.Position;
        
        try
        {
            // Deliberately not derived from stream.Length: a live stream reports no length,
            // and Read simply returns fewer bytes when the source has fewer to give.
            int probeSize = MaxSyncSearchSize;
            if (probeSize < 7)
                return new Ac3ProbeResult { IsAc3 = false };
            
            byte[] buffer = new byte[probeSize];
            int bytesRead = stream.ReadAtLeast(buffer.AsSpan(0, probeSize), probeSize, throwOnEndOfStream: false);
            if (bytesRead < 7)
                return new Ac3ProbeResult { IsAc3 = false };
            
            var data = buffer.AsSpan(0, bytesRead);
            
            // Try to find AC-3 sync (0x0B77 or byte-swapped 0x770B)
            int syncOffset = FindAc3Sync(data, out bool bigEndian);
            if (syncOffset < 0)
                return new Ac3ProbeResult { IsAc3 = false };
            
            var frameData = data[syncOffset..];
            if (bigEndian)
            {
                frameData = SwapBytes(frameData);
            }
            
            // Try to parse first frame
            if (!Ac3FrameParser.TryParseHeader(frameData, out var header))
                return new Ac3ProbeResult { IsAc3 = false };
            
            int confidence = 60;
            
            // Verify by checking next frame
            int nextFrameOffset = syncOffset + header.FrameSize;
            if (nextFrameOffset + 7 <= bytesRead)
            {
                var nextData = data[nextFrameOffset..];
                if (bigEndian)
                    nextData = SwapBytes(nextData);
                
                if (Ac3FrameParser.TryParseHeader(nextData, out var nextHeader))
                {
                    if (nextHeader.SampleRate == header.SampleRate &&
                        nextHeader.TotalChannels == header.TotalChannels)
                    {
                        confidence = 90;
                    }
                }
            }
            
            return new Ac3ProbeResult
            {
                IsAc3 = true,
                Confidence = confidence,
                IsEnhancedAc3 = header.IsEnhancedAc3,
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
    /// <returns>True if valid AC-3 was found.</returns>
    public bool Parse()
    {
        _streamLength = _stream.CanSeek ? _stream.Length : 0;
        
        var buffer = ArrayPool<byte>.Shared.Rent(MaxSyncSearchSize);
        try
        {
            long initialPosition = _stream.CanSeek ? _stream.Position : 0;
            int bytesRead = _stream.ReadAtLeast(buffer.AsSpan(0, MaxSyncSearchSize), MaxSyncSearchSize, throwOnEndOfStream: false);
            if (bytesRead < 7)
                return false;
            
            var data = buffer.AsSpan(0, bytesRead);
            
            // Find first AC-3 sync
            int syncOffset = FindAc3Sync(data, out _bigEndian);
            if (syncOffset < 0)
                return false;
            
            _dataStartPosition = initialPosition + syncOffset;
            
            var frameData = data[syncOffset..];
            if (_bigEndian)
            {
                frameData = SwapBytes(frameData);
            }
            
            // Parse first frame header
            if (!Ac3FrameParser.TryParseHeader(frameData, out _firstFrameHeader))
                return false;
            
            // Calculate frame duration (1536 samples per frame at sample rate)
            _frameDurationTicks = TimeSpan.TicksPerSecond * SamplesPerFrame / _firstFrameHeader.SampleRate;
            
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
        // ReadAtLeast, not Read: a socket returns short reads at any point, and treating one
        // as end of stream truncated a live stream at the first metadata boundary.
        // Read sync word and header
        int bytesRead = _stream.ReadAtLeast(_readBuffer.AsSpan(0, 8), 8, throwOnEndOfStream: false);
        if (bytesRead < 7)
            return null;
        
        // Check for sync word
        var headerData = _readBuffer.AsSpan(0, bytesRead);
        if (_bigEndian)
        {
            // Swap for parsing
            for (int i = 0; i < bytesRead - 1; i += 2)
            {
                (headerData[i], headerData[i + 1]) = (headerData[i + 1], headerData[i]);
            }
        }
        
        // Find sync if needed
        int attempts = 0;
        while (!HasAc3SyncWord(headerData) && attempts < 1000)
        {
            int syncOffset = FindAc3Sync(headerData, out bool newBigEndian);
            if (syncOffset > 0 && syncOffset < bytesRead - 7)
            {
                // Move to sync position
                Array.Copy(_readBuffer, syncOffset, _readBuffer, 0, bytesRead - syncOffset);
                int additional = _stream.ReadAtLeast(_readBuffer.AsSpan(bytesRead - syncOffset, syncOffset), syncOffset, throwOnEndOfStream: false);
                bytesRead = bytesRead - syncOffset + additional;
                _bigEndian = newBigEndian;
                headerData = _readBuffer.AsSpan(0, bytesRead);
                if (_bigEndian)
                {
                    for (int i = 0; i < bytesRead - 1; i += 2)
                    {
                        (headerData[i], headerData[i + 1]) = (headerData[i + 1], headerData[i]);
                    }
                }
            }
            else
            {
                bytesRead = _stream.ReadAtLeast(_readBuffer.AsSpan(0, 8), 8, throwOnEndOfStream: false);
                if (bytesRead < 7)
                    return null;
                headerData = _readBuffer.AsSpan(0, bytesRead);
                if (_bigEndian)
                {
                    for (int i = 0; i < bytesRead - 1; i += 2)
                    {
                        (headerData[i], headerData[i + 1]) = (headerData[i + 1], headerData[i]);
                    }
                }
            }
            attempts++;
        }
        
        if (!HasAc3SyncWord(headerData))
            return null;
        
        // Parse header
        if (!Ac3FrameParser.TryParseHeader(headerData, out var header))
            return null;
        
        // Ensure buffer is large enough
        if (_readBuffer.Length < header.FrameSize)
        {
            Array.Resize(ref _readBuffer, header.FrameSize);
        }
        
        // Read remaining bytes of the frame
        int bytesNeeded = header.FrameSize - bytesRead;
        if (bytesNeeded > 0)
        {
            int remaining = _stream.ReadAtLeast(_readBuffer.AsSpan(bytesRead, bytesNeeded), bytesNeeded, throwOnEndOfStream: false);
            if (remaining < bytesNeeded)
                return null;
        }
        
        // The frame goes out as a window onto the read buffer rather than a copy: see the
        // lifetime note on DemuxerPacket.Data. The copy cost about a kilobyte of garbage per
        // packet, which over an hour of playback is a great deal of collector work for a
        // buffer nothing holds past the next read.
        // A big-endian stream is byte-swapped in place, which it could not be when the
        // destination was a fresh array and the source was this same buffer.
        if (_bigEndian)
        {
            for (int i = 0; i < header.FrameSize - 1; i += 2)
                (_readBuffer[i], _readBuffer[i + 1]) = (_readBuffer[i + 1], _readBuffer[i]);
        }

        var frameData = _readBuffer.AsMemory(0, header.FrameSize);
        
        long pts = _currentPts;
        
        _currentPts += _frameDurationTicks;
        _frameCount++;
        
        return new DemuxerPacket(
            trackId: 0,
            presentationTimestamp: pts,
            decodeTimestamp: pts,
            duration: _frameDurationTicks,
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
            int syncOffset = FindAc3Sync(buffer.AsSpan(0, bytesRead), out bool bigEndian);
            
            if (syncOffset >= 0)
            {
                _stream.Position = _stream.Position - bytesRead + syncOffset;
                _bigEndian = bigEndian;
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
        return new DemuxerTrackInfo
        {
            TrackId = 0,
            TrackType = DemuxerTrackType.Audio,
            CodecId = Codec,
            Name = _firstFrameHeader.IsEnhancedAc3 ? "E-AC-3 (Dolby Digital Plus)" : "AC-3 (Dolby Digital)",
            SampleRate = _firstFrameHeader.SampleRate,
            ChannelCount = _firstFrameHeader.TotalChannels,
            BitsPerSample = 16
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
    
    private static int FindAc3Sync(ReadOnlySpan<byte> data, out bool bigEndian)
    {
        bigEndian = false;
        
        for (int i = 0; i < data.Length - 1; i++)
        {
            // Check little-endian: 0x0B 0x77
            if (data[i] == 0x0B && data[i + 1] == 0x77)
            {
                bigEndian = false;
                return i;
            }
            // Check big-endian: 0x77 0x0B
            if (data[i] == 0x77 && data[i + 1] == 0x0B)
            {
                bigEndian = true;
                return i;
            }
        }
        
        return -1;
    }
    
    private static bool HasAc3SyncWord(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return false;
        return data[0] == 0x0B && data[1] == 0x77;
    }
    
    private static Span<byte> SwapBytes(ReadOnlySpan<byte> data)
    {
        byte[] swapped = new byte[data.Length];
        for (int i = 0; i < data.Length - 1; i += 2)
        {
            swapped[i] = data[i + 1];
            swapped[i + 1] = data[i];
        }
        if ((data.Length & 1) == 1)
        {
            swapped[data.Length - 1] = data[data.Length - 1];
        }
        return swapped;
    }
    
    private void EstimateDuration()
    {
        if (_streamLength <= 0 || _firstFrameHeader.SampleRate <= 0)
            return;
        
        long audioDataSize = _streamLength - _dataStartPosition;
        
        if (_firstFrameHeader.FrameSize > 0)
        {
            // Estimate frames from average frame size
            double estimatedFrames = (double)audioDataSize / _firstFrameHeader.FrameSize;
            double samplesTotal = estimatedFrames * SamplesPerFrame;
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

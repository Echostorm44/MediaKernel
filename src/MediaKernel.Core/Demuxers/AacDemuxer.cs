// MediaKernel - AAC Audio Elementary Stream Demuxer
// Parses raw AAC audio elementary streams (.aac, .m4a files with ADTS framing)
// Reference: VLC's demux/mpeg/es.c, ISO/IEC 14496-3

using System.Buffers;
using MediaKernel.Core.Codecs.AAC;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Result from probing a stream for AAC audio content.
/// </summary>
public readonly struct AacProbeResult
{
    /// <summary>Whether valid AAC audio content was detected.</summary>
    public bool IsAac { get; init; }
    
    /// <summary>Confidence level 0-100.</summary>
    public int Confidence { get; init; }
    
    /// <summary>Audio object type if detected.</summary>
    public AudioObjectType Profile { get; init; }
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }
    
    /// <summary>Number of audio channels.</summary>
    public int ChannelCount { get; init; }
}

/// <summary>
/// Demuxer for raw AAC audio elementary streams with ADTS framing.
/// </summary>
public sealed class AacDemuxer : IDisposable
{
    private const int MaxSyncSearchSize = 16384;
    private const int Id3v2HeaderSize = 10;
    
    private readonly Stream _stream;
    private long _streamLength;
    private AdtsHeader _firstFrameHeader;
    private long _currentPts;
    private long _frameDurationTicks;
    private int _frameCount;
    private long _dataStartPosition;
    private byte[] _readBuffer = new byte[4096];
    private bool _disposed;
    
    /// <summary>AAC audio object type/profile.</summary>
    public AudioObjectType Profile => _firstFrameHeader.Profile;
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate => _firstFrameHeader.SampleRate;
    
    /// <summary>Number of audio channels.</summary>
    public int ChannelCount => GetChannelCount(_firstFrameHeader.ChannelConfiguration);
    
    /// <summary>Duration of the stream.</summary>
    public TimeSpan Duration { get; private set; }
    
    /// <summary>Codec identifier.</summary>
    public string Codec => "aac";
    
    /// <summary>Creates a new AAC demuxer.</summary>
    /// <param name="stream">Source stream containing raw AAC data.</param>
    public AacDemuxer(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }
    
    /// <summary>
    /// Probes a stream to check if it contains valid AAC audio data with ADTS framing.
    /// </summary>
    /// <param name="stream">Stream to probe (position will be reset).</param>
    /// <returns>Probe result with detection info.</returns>
    public static AacProbeResult Probe(Stream stream)
    {
        if (!stream.CanSeek)
            return new AacProbeResult { IsAac = false };
        
        long startPosition = stream.Position;
        
        try
        {
            // Deliberately not derived from stream.Length: a live stream reports no length,
            // and Read simply returns fewer bytes when the source has fewer to give.
            int probeSize = MaxSyncSearchSize;
            if (probeSize < 7)
                return new AacProbeResult { IsAac = false };
            
            byte[] buffer = new byte[probeSize];
            int bytesRead = stream.ReadAtLeast(buffer.AsSpan(0, probeSize), probeSize, throwOnEndOfStream: false);
            if (bytesRead < 7)
                return new AacProbeResult { IsAac = false };
            
            var data = buffer.AsSpan(0, bytesRead);
            
            // Skip ID3v2 tag if present
            int offset = SkipId3v2Tag(data);
            if (offset >= bytesRead - 7)
                return new AacProbeResult { IsAac = false };
            
            // Try to find ADTS sync
            int syncOffset = FindAdtsSync(data[offset..]);
            if (syncOffset < 0)
                return new AacProbeResult { IsAac = false };
            
            offset += syncOffset;
            
            // Try to parse first frame
            if (!AdtsParser.TryParseHeader(data[offset..], out var header))
                return new AacProbeResult { IsAac = false };
            
            int confidence = 60;
            
            // Verify by checking next frame
            int nextFrameOffset = offset + header.FrameLength;
            if (nextFrameOffset + 7 <= bytesRead)
            {
                if (AdtsParser.TryParseHeader(data[nextFrameOffset..], out var nextHeader))
                {
                    // Headers should be consistent
                    if (nextHeader.SampleRate == header.SampleRate &&
                        nextHeader.ChannelConfiguration == header.ChannelConfiguration)
                    {
                        confidence = 90;
                    }
                }
            }
            
            return new AacProbeResult
            {
                IsAac = true,
                Confidence = confidence,
                Profile = header.Profile,
                SampleRate = header.SampleRate,
                ChannelCount = GetChannelCount(header.ChannelConfiguration)
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
    /// <returns>True if valid AAC ADTS was found.</returns>
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
            
            // Skip ID3v2 tag if present. The tag routinely runs past this buffer - embedded cover
            // art alone is often hundreds of kilobytes - so re-read from just beyond it rather than
            // indexing into a span that ends before the audio starts, which threw.
            int tagSize = SkipId3v2Tag(data);
            long bufferBase = initialPosition;
            if (tagSize > 0)
            {
                if (_stream.CanSeek)
                {
                    _stream.Position = initialPosition + tagSize;
                }
                else if (!SkipForward(tagSize - bytesRead))
                {
                    return false;
                }

                bytesRead = _stream.ReadAtLeast(buffer.AsSpan(0, MaxSyncSearchSize), MaxSyncSearchSize,
                                               throwOnEndOfStream: false);
                if (bytesRead < 7)
                    return false;

                data = buffer.AsSpan(0, bytesRead);
                bufferBase = initialPosition + tagSize;
            }
            
            // Find first ADTS sync
            int syncOffset = FindAdtsSync(data);
            if (syncOffset < 0)
                return false;
            
            int offset = syncOffset;
            _dataStartPosition = bufferBase + offset;
            
            // Parse first frame header
            if (!AdtsParser.TryParseHeader(data[offset..], out _firstFrameHeader))
                return false;
            
            // Calculate frame duration (1024 samples per AAC frame)
            _frameDurationTicks = TimeSpan.TicksPerSecond * 1024 / _firstFrameHeader.SampleRate;
            
            // Estimate duration by scanning through file
            EstimateDuration(data, bytesRead, offset);
            
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
        // Read frame header (7 bytes minimum, 9 with CRC)
        int bytesRead = _stream.ReadAtLeast(_readBuffer.AsSpan(0, 9), 9, throwOnEndOfStream: false);
        if (bytesRead < 7)
            return null;
        
        // Find sync if needed
        int attempts = 0;
        while (!AdtsParser.HasSyncWord(_readBuffer.AsSpan(0, Math.Min(2, bytesRead))) && attempts < 1000)
        {
            // Shift buffer and read more
            int syncOffset = FindAdtsSync(_readBuffer.AsSpan(0, bytesRead));
            if (syncOffset > 0)
            {
                // Move to sync position
                Array.Copy(_readBuffer, syncOffset, _readBuffer, 0, bytesRead - syncOffset);
                int additional = _stream.ReadAtLeast(_readBuffer.AsSpan(bytesRead - syncOffset, syncOffset), syncOffset, throwOnEndOfStream: false);
                if (additional < syncOffset && bytesRead - syncOffset < 7)
                    return null;
                bytesRead = bytesRead - syncOffset + additional;
            }
            else
            {
                // Read fresh data
                bytesRead = _stream.ReadAtLeast(_readBuffer.AsSpan(0, 9), 9, throwOnEndOfStream: false);
                if (bytesRead < 7)
                    return null;
            }
            attempts++;
        }
        
        if (bytesRead < 2 || !AdtsParser.HasSyncWord(_readBuffer.AsSpan(0, 2)))
            return null;
        
        // Parse header
        if (!AdtsParser.TryParseHeader(_readBuffer.AsSpan(0, 7), out var header))
            return null;
        
        // Ensure buffer is large enough for the full frame
        if (_readBuffer.Length < header.FrameLength)
        {
            Array.Resize(ref _readBuffer, header.FrameLength);
        }
        
        // Read remaining bytes of the frame
        int bytesNeeded = header.FrameLength - bytesRead;
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
        var frameData = _readBuffer.AsMemory(0, header.FrameLength);
        
        long pts = _currentPts;
        
        _currentPts += _frameDurationTicks;
        _frameCount++;
        
        return new DemuxerPacket(
            trackId: 0,
            presentationTimestamp: pts,
            decodeTimestamp: pts,
            duration: _frameDurationTicks,
            isKeyframe: true,  // All audio frames are keyframes
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
        
        // AAC is typically CBR at the frame level (same samples/frame)
        // Calculate approximate position based on timestamp ratio
        if (Duration > TimeSpan.Zero)
        {
            double ratio = timestamp.TotalSeconds / Duration.TotalSeconds;
            long audioDataSize = _streamLength - _dataStartPosition;
            long targetPosition = _dataStartPosition + (long)(audioDataSize * ratio);
            
            _stream.Position = Math.Max(_dataStartPosition, Math.Min(targetPosition, _streamLength));
        }
        else
        {
            return SeekResult.Failed("Duration unknown");
        }
        
        // Find next sync
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int bytesRead = _stream.Read(buffer, 0, buffer.Length);
            int syncOffset = FindAdtsSync(buffer.AsSpan(0, bytesRead));
            
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
        return new DemuxerTrackInfo
        {
            TrackId = 0,
            TrackType = DemuxerTrackType.Audio,
            CodecId = "aac",
            Name = GetProfileName(_firstFrameHeader.Profile),
            SampleRate = _firstFrameHeader.SampleRate,
            ChannelCount = GetChannelCount(_firstFrameHeader.ChannelConfiguration),
            BitsPerSample = 16
        };
    }
    
    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            // Stream is owned by caller
        }
    }
    
    private static int SkipId3v2Tag(ReadOnlySpan<byte> data)
    {
        if (data.Length < Id3v2HeaderSize)
            return 0;
        
        if (data[0] == 'I' && data[1] == 'D' && data[2] == '3')
        {
            int size = ((data[6] & 0x7F) << 21) |
                       ((data[7] & 0x7F) << 14) |
                       ((data[8] & 0x7F) << 7) |
                       (data[9] & 0x7F);
            
            return Id3v2HeaderSize + size;
        }
        
        return 0;
    }
    
    /// <summary>Reads and discards <paramref name="count"/> bytes, for a stream that cannot seek.</summary>
    private bool SkipForward(long count)
    {
        if (count <= 0)
            return true;

        var scratch = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (count > 0)
            {
                int want = (int)Math.Min(count, scratch.Length);
                int got = _stream.Read(scratch, 0, want);
                if (got == 0)
                    return false;
                count -= got;
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>How many frames after the first must line up before a sync is believed.</summary>
    private const int SyncChainFrames = 3;

    private static int FindAdtsSync(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length - 1; i++)
        {
            if (data[i] == 0xFF && (data[i + 1] & 0xF0) == 0xF0 &&
                i + 7 <= data.Length && AdtsParser.TryParseHeader(data[i..], out _) &&
                ChainsFromHere(data[i..]))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Whether a run of frames links up from this position, each one's length landing on the next
    /// header. MPEG audio shares ADTS's twelve sync bits, so a single valid-looking header is not
    /// evidence - an MP3 stream will supply one. Runs out of data rather than failing, so a short
    /// probe buffer still accepts a genuine stream.
    /// </summary>
    private static bool ChainsFromHere(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        for (int frame = 0; frame < SyncChainFrames; frame++)
        {
            if (!AdtsParser.TryParseHeader(data[offset..], out var header) || !header.IsValid)
                return false;
            if (header.FrameLength <= header.HeaderSize)
                return false;

            offset += header.FrameLength;
            if (offset + 7 > data.Length)
                return true;
        }

        return true;
    }
    
    private void EstimateDuration(ReadOnlySpan<byte> initialData, int dataLength, int startOffset)
    {
        if (_streamLength <= 0 || _firstFrameHeader.SampleRate <= 0)
            return;
        
        // Count frames in initial probe data to estimate average frame size
        int frameCount = 0;
        int totalBytes = 0;
        int offset = startOffset;
        
        while (offset + 7 < dataLength && frameCount < 50)
        {
            if (AdtsParser.TryParseHeader(initialData[offset..], out var header))
            {
                frameCount++;
                totalBytes += header.FrameLength;
                offset += header.FrameLength;
            }
            else
            {
                break;
            }
        }
        
        if (frameCount > 0)
        {
            double avgFrameSize = (double)totalBytes / frameCount;
            long audioDataSize = _streamLength - _dataStartPosition;
            double estimatedFrames = audioDataSize / avgFrameSize;
            double samplesTotal = estimatedFrames * 1024;  // 1024 samples per AAC frame
            Duration = TimeSpan.FromSeconds(samplesTotal / _firstFrameHeader.SampleRate);
        }
    }
    
    private static string GetProfileName(AudioObjectType profile)
    {
        return profile switch
        {
            AudioObjectType.AacMain => "AAC Main",
            AudioObjectType.AacLowComplexity => "AAC-LC",
            AudioObjectType.AacScalableSampleRate => "AAC-SSR",
            AudioObjectType.AacLongTermPrediction => "AAC-LTP",
            AudioObjectType.SpectralBandReplication => "HE-AAC (SBR)",
            AudioObjectType.AacScalable => "AAC Scalable",
            AudioObjectType.ParametricStereo => "HE-AACv2 (PS)",
            _ => "AAC"
        };
    }
    
    private static int GetChannelCount(ChannelConfiguration config)
    {
        return config switch
        {
            ChannelConfiguration.Mono => 1,
            ChannelConfiguration.Stereo => 2,
            ChannelConfiguration.ThreeChannels => 3,
            ChannelConfiguration.FourChannels => 4,
            ChannelConfiguration.FiveChannels => 5,
            ChannelConfiguration.FivePointOne => 6,
            ChannelConfiguration.SevenPointOne => 8,
            _ => 2  // Default to stereo for unknown
        };
    }
}

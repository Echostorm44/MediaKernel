// MediaKernel - MPEG Audio Demuxer (MP3, MP2, MP1)
// Parses raw MPEG audio elementary streams (.mp3, .mp2, .mp1)
// Reference: VLC's demux/mpeg/es.c, ISO/IEC 11172-3, ISO/IEC 13818-3

using System.Buffers;
using System.Buffers.Binary;
using MediaKernel.Core.Codecs.MP3;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// Result from probing a stream for MPEG audio content.
/// </summary>
public readonly struct MpegAudioProbeResult
{
    /// <summary>Whether valid MPEG audio content was detected.</summary>
    public bool IsMpegAudio { get; init; }
    
    /// <summary>Confidence level 0-100.</summary>
    public int Confidence { get; init; }
    
    /// <summary>MPEG version if detected.</summary>
    public MpegVersion Version { get; init; }
    
    /// <summary>Layer if detected.</summary>
    public MpegLayer Layer { get; init; }
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }
    
    /// <summary>Number of audio channels.</summary>
    public int ChannelCount { get; init; }
    
    /// <summary>Bit rate in kbps.</summary>
    public int BitRate { get; init; }
}

/// <summary>
/// Demuxer for raw MPEG audio elementary streams (MP3, MP2, MP1).
/// Handles ID3v2 tags, Xing/VBRI headers for VBR files, and frame-by-frame parsing.
/// </summary>
public sealed class MpegAudioDemuxer : IDisposable
{
    private const int MaxSyncSearchSize = 16384;
    private const int Id3v2HeaderSize = 10;
    
    private readonly Stream _stream;
    private long _streamOffset;  // Offset to first audio frame (after ID3 tags)
    private long _streamLength;
    private Mp3FrameHeader _firstFrameHeader;
    private long _currentPts;
    private long _frameDurationTicks;
    private int _frameCount;
    private bool _isVbr;
    private XingHeader? _xingHeader;
    private long _dataStartPosition;
    private byte[] _readBuffer = new byte[4096];
    private bool _disposed;
    
    /// <summary>MPEG version of the stream.</summary>
    public MpegVersion Version => _firstFrameHeader.Version;
    
    /// <summary>Layer (1, 2, or 3).</summary>
    public MpegLayer Layer => _firstFrameHeader.Layer;
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate => _firstFrameHeader.SampleRate;
    
    /// <summary>Number of audio channels.</summary>
    public int ChannelCount => _firstFrameHeader.ChannelCount;
    
    /// <summary>Bit rate in kbps (0 for VBR).</summary>
    public int BitRate => _isVbr ? 0 : _firstFrameHeader.BitRate;
    
    /// <summary>Whether the stream is variable bit rate.</summary>
    public bool IsVbr => _isVbr;
    
    /// <summary>Duration of the stream.</summary>
    public TimeSpan Duration { get; private set; }
    
    /// <summary>Codec identifier (mp1, mp2, mp3).</summary>
    public string Codec => GetCodecId(_firstFrameHeader);
    
    /// <summary>Creates a new MPEG audio demuxer.</summary>
    /// <param name="stream">Source stream containing raw MPEG audio data.</param>
    public MpegAudioDemuxer(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }
    
    /// <summary>
    /// Probes a stream to check if it contains valid MPEG audio data.
    /// </summary>
    /// <param name="stream">Stream to probe (position will be reset).</param>
    /// <returns>Probe result with detection info.</returns>
    public static MpegAudioProbeResult Probe(Stream stream)
    {
        if (!stream.CanSeek)
            return new MpegAudioProbeResult { IsMpegAudio = false };
        
        long startPosition = stream.Position;
        
        try
        {
            // Deliberately not derived from stream.Length: a live stream reports no length,
            // and Read simply returns fewer bytes when the source has fewer to give.
            int probeSize = MaxSyncSearchSize;
            if (probeSize < 4)
                return new MpegAudioProbeResult { IsMpegAudio = false };
            
            byte[] buffer = new byte[probeSize];
            int bytesRead = stream.ReadAtLeast(buffer.AsSpan(0, probeSize), probeSize, throwOnEndOfStream: false);
            if (bytesRead < 4)
                return new MpegAudioProbeResult { IsMpegAudio = false };
            
            var data = buffer.AsSpan(0, bytesRead);
            
            // Skip ID3v2 tag if present
            int offset = SkipId3v2Tag(data);
            if (offset >= bytesRead)
                return new MpegAudioProbeResult { IsMpegAudio = false };
            
            // Try to find valid MPEG audio sync
            int syncOffset = Mp3FrameParser.FindSyncWord(data[offset..]);
            if (syncOffset < 0)
                return new MpegAudioProbeResult { IsMpegAudio = false };
            
            offset += syncOffset;
            
            // Try to parse first frame
            if (!Mp3FrameParser.TryParseHeader(data[offset..], out var header))
                return new MpegAudioProbeResult { IsMpegAudio = false };
            
            int confidence = 60;
            
            // Verify by checking next frame
            int nextFrameOffset = offset + header.FrameSize;
            if (nextFrameOffset + 4 <= bytesRead)
            {
                if (Mp3FrameParser.TryParseHeader(data[nextFrameOffset..], out var nextHeader))
                {
                    // Headers should be consistent (same sample rate and channels)
                    if (nextHeader.SampleRate == header.SampleRate &&
                        nextHeader.ChannelCount == header.ChannelCount)
                    {
                        confidence = 90;
                    }
                }
            }
            
            return new MpegAudioProbeResult
            {
                IsMpegAudio = true,
                Confidence = confidence,
                Version = header.Version,
                Layer = header.Layer,
                SampleRate = header.SampleRate,
                ChannelCount = header.ChannelCount,
                BitRate = header.BitRate
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
    /// <returns>True if valid MPEG audio was found.</returns>
    public bool Parse()
    {
        _streamLength = _stream.CanSeek ? _stream.Length : 0;
        
        var buffer = ArrayPool<byte>.Shared.Rent(MaxSyncSearchSize);
        try
        {
            int bytesRead = _stream.ReadAtLeast(buffer.AsSpan(0, MaxSyncSearchSize), MaxSyncSearchSize, throwOnEndOfStream: false);
            if (bytesRead < 4)
                return false;
            
            var data = buffer.AsSpan(0, bytesRead);
            
            // Skip ID3v2 tag if present. The tag routinely runs past this buffer - embedded cover
            // art alone is often hundreds of kilobytes - so re-read from just beyond it instead of
            // indexing into a span that ends long before the audio starts.
            int tagSize = SkipId3v2Tag(data);
            long bufferBase = 0;
            if (tagSize > 0)
            {
                if (_stream.CanSeek)
                {
                    _stream.Position = tagSize;
                }
                else if (!SkipForward(tagSize - bytesRead))
                {
                    return false;
                }

                bytesRead = _stream.ReadAtLeast(buffer.AsSpan(0, MaxSyncSearchSize), MaxSyncSearchSize, throwOnEndOfStream: false);
                if (bytesRead < 4)
                    return false;
                data = buffer.AsSpan(0, bytesRead);
                bufferBase = tagSize;
            }

            int offset = 0;
            _streamOffset = tagSize;
            
            // Find first sync word
            int syncOffset = Mp3FrameParser.FindSyncWord(data);
            if (syncOffset < 0)
                return false;
            
            offset += syncOffset;
            _dataStartPosition = bufferBase + offset;
            
            // Parse first frame header
            if (!Mp3FrameParser.TryParseHeader(data[offset..], out _firstFrameHeader))
                return false;
            
            // Calculate frame duration
            _frameDurationTicks = TimeSpan.TicksPerSecond * _firstFrameHeader.SamplesPerFrame / _firstFrameHeader.SampleRate;
            
            // Check for Xing/VBRI header in first frame
            _xingHeader = ParseXingHeader(data.Slice(offset, Math.Min(_firstFrameHeader.FrameSize, bytesRead - offset)));
            if (_xingHeader.HasValue)
            {
                _isVbr = true;
                // Skip past the Xing/Info header frame — it's metadata, not audio.
                // FFmpeg's mp3 demuxer (mp3dec.c) also skips this frame.
                _dataStartPosition += _firstFrameHeader.FrameSize;
            }
            
            // Estimate duration
            EstimateDuration();
            
            // Reset to data start (after Xing frame if present)
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
        // Read frame header
        int bytesRead = _stream.ReadAtLeast(_readBuffer.AsSpan(0, 4), 4, throwOnEndOfStream: false);
        if (bytesRead < 4)
            return null;
        
        // Find sync if needed
        int syncOffset = 0;
        int bufferedBeyondHeader = 0; // Extra bytes in buffer past the 4-byte header
        
        while (!IsSyncWord(_readBuffer.AsSpan(syncOffset, 2)))
        {
            // Try to resync
            syncOffset = Mp3FrameParser.FindSyncWord(_readBuffer.AsSpan(syncOffset, bytesRead - syncOffset));
            if (syncOffset < 0)
            {
                // Try reading more data
                bytesRead = _stream.ReadAtLeast(_readBuffer, _readBuffer.Length, throwOnEndOfStream: false);
                if (bytesRead < 4)
                    return null;
                syncOffset = 0;
            }
        }
        
        // Ensure we have 4 bytes starting at sync
        if (syncOffset > 0)
        {
            // Move data from sync position to start of buffer
            int available = bytesRead - syncOffset;
            if (available > 0 && available <= bytesRead)
                Array.Copy(_readBuffer, syncOffset, _readBuffer, 0, available);
            else
                available = 0;
            // Read remaining bytes to fill 4-byte header
            int needed = 4 - available;
            if (needed > 0)
            {
                int remaining = _stream.ReadAtLeast(_readBuffer.AsSpan(available, needed), needed, throwOnEndOfStream: false);
                if (remaining < needed)
                    return null;
                bufferedBeyondHeader = 0;
            }
            else
            {
                // We already have more bytes than just the header
                bufferedBeyondHeader = available - 4;
            }
        }
        
        // Parse header
        if (!Mp3FrameParser.TryParseHeader(_readBuffer.AsSpan(0, 4), out var header))
            return null;
        
        // Read rest of frame
        int frameDataSize = header.FrameSize - 4;
        if (frameDataSize <= 0)
            return null;
        
        // Ensure buffer is large enough
        if (_readBuffer.Length < header.FrameSize)
        {
            Array.Resize(ref _readBuffer, header.FrameSize);
        }
        
        // Only read bytes we don't already have in the buffer
        int toRead = frameDataSize - bufferedBeyondHeader;
        int frameSize = header.FrameSize;
        if (toRead > 0)
        {
            bytesRead = _stream.ReadAtLeast(_readBuffer.AsSpan(4 + bufferedBeyondHeader, toRead), toRead, throwOnEndOfStream: false);
            if (bytesRead < toRead)
            {
                // END OF STREAM INSIDE THE LAST FRAME, and the frame still goes out. FFmpeg
                // reaches the same place by a different route: mp3_read_packet does not parse
                // frames at all, it hands fixed-size chunks to ff_mpegaudio_parse, and at the
                // flush call (buf_size == 0) ff_combine_frame turns END_NOT_FOUND into
                // "ends here" and emits whatever it has accumulated. So a truncated final
                // frame is decoded, not dropped.
                //
                // This returned null, which cost the last frame of every MP3 whose final frame
                // is short - and that is most of them, since an encoder stops where the audio
                // does. Audio-MP3-reservoir.mp3 is 53 bytes short of its 60th frame: ffmpeg
                // decodes 60 frames of it and this demuxer produced 59. Nothing noticed,
                // because fate-mp3 compares what both decoders produce for the frames they
                // agree exist. It matters for the radio recorder, where every track's last
                // frame is this case.
                if (bytesRead <= 0)
                    return null;

                frameSize = 4 + bufferedBeyondHeader + bytesRead;

                // The rest of the frame is absent, not zero. Clearing it keeps the decoder
                // reading a defined value rather than the previous frame's bytes, which is
                // what AV_INPUT_BUFFER_PADDING_SIZE does for the C.
                _readBuffer.AsSpan(frameSize, header.FrameSize - frameSize).Clear();
            }
        }
        
        // If we buffered data BEYOND this frame (from the sync search read-ahead),
        // seek the stream back so the next ReadFrame() picks up at the right position.
        int totalBuffered = 4 + bufferedBeyondHeader;
        int excessBytes = totalBuffered - frameSize;
        if (excessBytes > 0 && _stream.CanSeek)
        {
            _stream.Position -= excessBytes;
        }
        
        // The frame goes out as a window onto the read buffer rather than a copy: see the
        // lifetime note on DemuxerPacket.Data. The copy cost about a kilobyte of garbage per
        // packet, which over an hour of playback is a great deal of collector work for a
        // buffer nothing holds past the next read.
        var frameData = _readBuffer.AsMemory(0, frameSize);
        
        long pts = _currentPts;
        long duration = TimeSpan.TicksPerSecond * header.SamplesPerFrame / header.SampleRate;
        
        _currentPts += duration;
        _frameCount++;
        
        return new DemuxerPacket(
            trackId: 0,
            presentationTimestamp: pts,
            decodeTimestamp: pts,
            duration: duration,
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
        
        // For CBR, we can calculate position directly
        // For VBR with Xing TOC, use the table
        long targetPosition;
        
        if (_xingHeader?.TocData != null && Duration > TimeSpan.Zero)
        {
            // Use Xing TOC
            double percent = timestamp.TotalSeconds / Duration.TotalSeconds * 100;
            int tocIndex = Math.Clamp((int)percent, 0, 99);
            
            long audioDataSize = _streamLength - _dataStartPosition;
            targetPosition = _dataStartPosition + (audioDataSize * _xingHeader.Value.TocData[tocIndex] / 256);
        }
        else if (_firstFrameHeader.BitRate > 0)
        {
            // CBR: calculate position from bit rate
            double bytesPerSecond = _firstFrameHeader.BitRate * 1000.0 / 8.0;
            targetPosition = _dataStartPosition + (long)(timestamp.TotalSeconds * bytesPerSecond);
        }
        else
        {
            return SeekResult.Failed("Cannot seek in VBR stream without TOC");
        }
        
        // Seek to position
        _stream.Position = Math.Max(_dataStartPosition, Math.Min(targetPosition, _streamLength));
        
        // Find next sync word
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int bytesRead = _stream.Read(buffer, 0, buffer.Length);
            int syncOffset = Mp3FrameParser.FindSyncWord(buffer.AsSpan(0, bytesRead));
            
            if (syncOffset > 0)
            {
                _stream.Position = _stream.Position - bytesRead + syncOffset;
            }
            else if (syncOffset < 0)
            {
                // Couldn't find sync, go back to start
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
        return SeekResult.Succeeded(_currentPts, isApproximate: _isVbr);
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
            CodecId = GetCodecId(_firstFrameHeader),
            Name = GetCodecName(_firstFrameHeader),
            SampleRate = _firstFrameHeader.SampleRate,
            ChannelCount = _firstFrameHeader.ChannelCount,
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
    
    /// <summary>
    /// Discards <paramref name="count"/> bytes from a stream that cannot seek, which is the normal
    /// case for a network stream. Returns false if the stream ends first.
    /// </summary>
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

    private static int SkipId3v2Tag(ReadOnlySpan<byte> data)
    {
        if (data.Length < Id3v2HeaderSize)
            return 0;
        
        // ID3v2 header: "ID3" + version (2 bytes) + flags (1 byte) + size (4 bytes syncsafe)
        if (data[0] == 'I' && data[1] == 'D' && data[2] == '3')
        {
            // Parse syncsafe integer (7 bits per byte)
            int size = ((data[6] & 0x7F) << 21) |
                       ((data[7] & 0x7F) << 14) |
                       ((data[8] & 0x7F) << 7) |
                       (data[9] & 0x7F);
            
            return Id3v2HeaderSize + size;
        }
        
        return 0;
    }
    
    private static bool IsSyncWord(ReadOnlySpan<byte> data)
    {
        return data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0;
    }
    
    private void EstimateDuration()
    {
        if (_streamLength <= 0 || _firstFrameHeader.SampleRate <= 0)
            return;
        
        long audioDataSize = _streamLength - _dataStartPosition;
        
        if (_xingHeader.HasValue && _xingHeader.Value.TotalFrames > 0)
        {
            // VBR with Xing: use frame count
            double samplesTotal = (double)_xingHeader.Value.TotalFrames * _firstFrameHeader.SamplesPerFrame;
            Duration = TimeSpan.FromSeconds(samplesTotal / _firstFrameHeader.SampleRate);
        }
        else if (_firstFrameHeader.BitRate > 0)
        {
            // CBR: calculate from bit rate
            double seconds = audioDataSize * 8.0 / (_firstFrameHeader.BitRate * 1000.0);
            Duration = TimeSpan.FromSeconds(seconds);
        }
    }
    
    private static string GetCodecId(Mp3FrameHeader header)
    {
        return header.Layer switch
        {
            MpegLayer.Layer1 => "mp1",
            MpegLayer.Layer2 => "mp2",
            MpegLayer.Layer3 => "mp3",
            _ => "mpga"
        };
    }
    
    private static string GetCodecName(Mp3FrameHeader header)
    {
        var version = header.Version switch
        {
            MpegVersion.Mpeg1 => "MPEG-1",
            MpegVersion.Mpeg2 => "MPEG-2",
            MpegVersion.Mpeg25 => "MPEG-2.5",
            _ => "MPEG"
        };
        
        var layer = header.Layer switch
        {
            MpegLayer.Layer1 => "Layer 1",
            MpegLayer.Layer2 => "Layer 2",
            MpegLayer.Layer3 => "Layer 3",
            _ => "Audio"
        };
        
        return $"{version} {layer}";
    }
    
    private static XingHeader? ParseXingHeader(ReadOnlySpan<byte> frameData)
    {
        // Xing/Info header appears after side info in first frame
        // Side info size: MPEG-1 mono: 17, MPEG-1 stereo: 32
        //                 MPEG-2 mono: 9, MPEG-2 stereo: 17
        
        if (frameData.Length < 36)  // Minimum: 4 header + 9 side_info + 4 tag + 4 flags + padding
            return null;
        
        // Search for "Xing" or "Info" tag
        ReadOnlySpan<byte> xingTag = "Xing"u8;
        ReadOnlySpan<byte> infoTag = "Info"u8;
        ReadOnlySpan<byte> vbriTag = "VBRI"u8;
        
        int offset = -1;
        int searchEnd = frameData.Length - 4; // Need at least 4 bytes for tag
        for (int i = 0; i < searchEnd; i++)
        {
            if (frameData.Slice(i, 4).SequenceEqual(xingTag) ||
                frameData.Slice(i, 4).SequenceEqual(infoTag))
            {
                offset = i;
                break;
            }
            if (frameData.Slice(i, 4).SequenceEqual(vbriTag))
            {
                // VBRI header format (different from Xing)
                return ParseVbriHeader(frameData[i..]);
            }
        }
        
        if (offset < 0)
            return null;
        
        var data = frameData[offset..];
        if (data.Length < 8)
            return null;
        
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);
        int pos = 8;
        
        uint totalFrames = 0;
        uint totalBytes = 0;
        byte[]? tocData = null;
        
        if ((flags & 0x01) != 0)  // Frames field present
        {
            if (pos + 4 > data.Length) return null;
            totalFrames = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            pos += 4;
        }
        
        if ((flags & 0x02) != 0)  // Bytes field present
        {
            if (pos + 4 > data.Length) return null;
            totalBytes = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            pos += 4;
        }
        
        if ((flags & 0x04) != 0)  // TOC field present
        {
            if (pos + 100 > data.Length) return null;
            tocData = data.Slice(pos, 100).ToArray();
            pos += 100;
        }
        
        return new XingHeader
        {
            TotalFrames = totalFrames,
            TotalBytes = totalBytes,
            TocData = tocData
        };
    }
    
    private static XingHeader? ParseVbriHeader(ReadOnlySpan<byte> data)
    {
        // VBRI header format
        if (data.Length < 26)
            return null;
        
        // Skip "VBRI" tag and version (2 bytes)
        uint totalBytes = BinaryPrimitives.ReadUInt32BigEndian(data[10..14]);
        uint totalFrames = BinaryPrimitives.ReadUInt32BigEndian(data[14..18]);
        
        return new XingHeader
        {
            TotalFrames = totalFrames,
            TotalBytes = totalBytes,
            TocData = null
        };
    }
    
    /// <summary>
    /// Xing/VBRI VBR header information.
    /// </summary>
    private readonly struct XingHeader
    {
        public uint TotalFrames { get; init; }
        public uint TotalBytes { get; init; }
        public byte[]? TocData { get; init; }
    }
}

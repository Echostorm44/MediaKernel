// Microsoft Smooth Streaming (MSS/ISM) Types
// Based on Microsoft IIS Smooth Streaming specification

namespace MediaKernel.Core.Streaming;

/// <summary>
/// Stream type in Smooth Streaming.
/// </summary>
public enum SmoothStreamType
{
    Unknown,
    Video,
    Audio,
    Text
}

/// <summary>
/// Parsed Smooth Streaming manifest (ISM/ISMC).
/// </summary>
public sealed class SmoothManifest
{
    /// <summary>Whether manifest was parsed successfully.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Major version of the manifest format.</summary>
    public int MajorVersion { get; init; }
    
    /// <summary>Minor version of the manifest format.</summary>
    public int MinorVersion { get; init; }
    
    /// <summary>Time scale in ticks per second (typically 10000000 = 100ns ticks).</summary>
    public long TimeScale { get; init; } = 10_000_000;
    
    /// <summary>Total duration in time scale units.</summary>
    public long Duration { get; init; }
    
    /// <summary>Whether this is a live stream.</summary>
    public bool IsLive { get; init; }
    
    /// <summary>Number of segments available for lookahead (live streams).</summary>
    public int LookaheadCount { get; init; }
    
    /// <summary>DVR window length for live streams (time scale units).</summary>
    public long? DvrWindowLength { get; init; }
    
    /// <summary>Base URI for resolving relative URLs.</summary>
    public string BaseUri { get; init; } = string.Empty;
    
    /// <summary>Stream indices (tracks) in the manifest.</summary>
    public List<SmoothStreamIndex> StreamIndices { get; } = [];
    
    /// <summary>Content protection information.</summary>
    public SmoothProtection? Protection { get; init; }
    
    /// <summary>
    /// Gets total duration as TimeSpan.
    /// </summary>
    public TimeSpan GetDuration() => 
        TimeSpan.FromTicks(Duration * TimeSpan.TicksPerSecond / TimeScale);
    
    /// <summary>
    /// Gets all video streams.
    /// </summary>
    public IEnumerable<SmoothStreamIndex> GetVideoStreams() =>
        StreamIndices.Where(s => s.Type == SmoothStreamType.Video);
    
    /// <summary>
    /// Gets all audio streams.
    /// </summary>
    public IEnumerable<SmoothStreamIndex> GetAudioStreams() =>
        StreamIndices.Where(s => s.Type == SmoothStreamType.Audio);
    
    /// <summary>
    /// Gets all text/subtitle streams.
    /// </summary>
    public IEnumerable<SmoothStreamIndex> GetTextStreams() =>
        StreamIndices.Where(s => s.Type == SmoothStreamType.Text);
}

/// <summary>
/// A stream index (track) in Smooth Streaming.
/// </summary>
public sealed class SmoothStreamIndex
{
    /// <summary>Stream type (video/audio/text).</summary>
    public SmoothStreamType Type { get; init; }
    
    /// <summary>Stream name.</summary>
    public string Name { get; init; } = string.Empty;
    
    /// <summary>Language code (e.g., "eng", "spa").</summary>
    public string Language { get; init; } = string.Empty;
    
    /// <summary>Subtype for text streams (e.g., "CAPT" for captions).</summary>
    public string Subtype { get; init; } = string.Empty;
    
    /// <summary>URL template for fragment requests.</summary>
    public string UrlTemplate { get; init; } = string.Empty;
    
    /// <summary>Time scale for this stream (inherits from manifest if not specified).</summary>
    public long TimeScale { get; init; } = 10_000_000;
    
    /// <summary>Number of chunks in the stream.</summary>
    public int ChunkCount { get; init; }
    
    /// <summary>Quality levels available for this stream.</summary>
    public List<SmoothQualityLevel> QualityLevels { get; } = [];
    
    /// <summary>Chunk timeline for segment timing.</summary>
    public List<SmoothChunk> Chunks { get; } = [];
    
    /// <summary>
    /// Builds segment URL for a specific quality and time.
    /// </summary>
    /// <param name="qualityLevel">Quality level to use.</param>
    /// <param name="startTime">Segment start time in time scale units.</param>
    /// <param name="baseUri">Base URI for relative URLs.</param>
    public string BuildSegmentUrl(SmoothQualityLevel qualityLevel, long startTime, string baseUri)
    {
        string url = UrlTemplate
            .Replace("{bitrate}", qualityLevel.Bitrate.ToString())
            .Replace("{Bitrate}", qualityLevel.Bitrate.ToString())
            .Replace("{start time}", startTime.ToString())
            .Replace("{start_time}", startTime.ToString());
        
        if (!string.IsNullOrEmpty(baseUri) && !url.StartsWith("http://") && !url.StartsWith("https://"))
        {
            url = baseUri.TrimEnd('/') + "/" + url;
        }
        
        return url;
    }
}

/// <summary>
/// Quality level within a stream index.
/// </summary>
public sealed class SmoothQualityLevel
{
    /// <summary>Quality index (0-based).</summary>
    public int Index { get; init; }
    
    /// <summary>Bitrate in bits per second.</summary>
    public int Bitrate { get; init; }
    
    /// <summary>FourCC codec identifier (e.g., "H264", "AACL", "WVC1").</summary>
    public string FourCC { get; init; } = string.Empty;
    
    /// <summary>Maximum width for video.</summary>
    public int MaxWidth { get; init; }
    
    /// <summary>Maximum height for video.</summary>
    public int MaxHeight { get; init; }
    
    /// <summary>Codec private data (hex-encoded, e.g., SPS/PPS for H.264).</summary>
    public string CodecPrivateData { get; init; } = string.Empty;
    
    // Audio-specific properties
    
    /// <summary>Audio tag identifier.</summary>
    public int AudioTag { get; init; }
    
    /// <summary>Number of audio channels.</summary>
    public int Channels { get; init; }
    
    /// <summary>Audio sampling rate in Hz.</summary>
    public int SamplingRate { get; init; }
    
    /// <summary>Bits per audio sample.</summary>
    public int BitsPerSample { get; init; }
    
    /// <summary>Audio packet size.</summary>
    public int PacketSize { get; init; }
    
    /// <summary>NAL unit length size for H.264/HEVC (typically 4).</summary>
    public int NALUnitLengthField { get; init; } = 4;
    
    /// <summary>
    /// Gets decoded codec private data as bytes.
    /// </summary>
    public byte[] GetCodecPrivateDataBytes()
    {
        if (string.IsNullOrEmpty(CodecPrivateData))
            return [];
        
        // Hex-encoded string to bytes
        int length = CodecPrivateData.Length / 2;
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = Convert.ToByte(CodecPrivateData.Substring(i * 2, 2), 16);
        }
        return bytes;
    }
}

/// <summary>
/// Chunk (segment) timing information.
/// </summary>
public readonly struct SmoothChunk
{
    /// <summary>Chunk number (optional).</summary>
    public long Number { get; init; }
    
    /// <summary>Start time in time scale units.</summary>
    public long StartTime { get; init; }
    
    /// <summary>Duration in time scale units.</summary>
    public long Duration { get; init; }
    
    /// <summary>Repeat count (0 = single occurrence, N = N additional repeats).</summary>
    public int RepeatCount { get; init; }
    
    /// <summary>
    /// Gets the total number of segments represented (1 + RepeatCount).
    /// </summary>
    public int TotalSegments => 1 + RepeatCount;
    
    /// <summary>
    /// Gets the end time of the last segment in this chunk entry.
    /// </summary>
    public long EndTime => StartTime + (Duration * TotalSegments);
}

/// <summary>
/// Content protection/DRM information.
/// </summary>
public sealed class SmoothProtection
{
    /// <summary>Protection headers by system ID.</summary>
    public Dictionary<Guid, string> ProtectionHeaders { get; } = [];
    
    /// <summary>
    /// Checks if PlayReady protection is present.
    /// </summary>
    public bool HasPlayReady => ProtectionHeaders.ContainsKey(PlayReadySystemId);
    
    /// <summary>
    /// Checks if Widevine protection is present.
    /// </summary>
    public bool HasWidevine => ProtectionHeaders.ContainsKey(WidevineSystemId);
    
    /// <summary>PlayReady DRM system ID.</summary>
    public static readonly Guid PlayReadySystemId = new("9a04f079-9840-4286-ab92-e65be0885f95");
    
    /// <summary>Widevine DRM system ID.</summary>
    public static readonly Guid WidevineSystemId = new("edef8ba9-79d6-4ace-a3c8-27dcd51d21ed");
}

/// <summary>
/// Segment iterator for navigating through chunks.
/// </summary>
public sealed class SmoothSegmentIterator
{
    private readonly SmoothStreamIndex _stream;
    private readonly long _timeScale;
    private int _chunkIndex;
    private int _repeatIndex;
    private long _currentTime;
    
    public SmoothSegmentIterator(SmoothStreamIndex stream, long manifestTimeScale)
    {
        _stream = stream;
        _timeScale = stream.TimeScale != 0 ? stream.TimeScale : manifestTimeScale;
        Reset();
    }
    
    /// <summary>Current segment start time in time scale units.</summary>
    public long CurrentStartTime => _currentTime;
    
    /// <summary>Current segment duration in time scale units.</summary>
    public long CurrentDuration => _chunkIndex < _stream.Chunks.Count 
        ? _stream.Chunks[_chunkIndex].Duration 
        : 0;
    
    /// <summary>Current segment start time as TimeSpan.</summary>
    public TimeSpan CurrentStartTimeSpan => 
        TimeSpan.FromTicks(_currentTime * TimeSpan.TicksPerSecond / _timeScale);
    
    /// <summary>Whether there are more segments.</summary>
    public bool HasMore => _chunkIndex < _stream.Chunks.Count;
    
    /// <summary>Resets iterator to beginning.</summary>
    public void Reset()
    {
        _chunkIndex = 0;
        _repeatIndex = 0;
        _currentTime = _stream.Chunks.Count > 0 ? _stream.Chunks[0].StartTime : 0;
    }
    
    /// <summary>Advances to next segment.</summary>
    /// <returns>True if advanced, false if at end.</returns>
    public bool MoveNext()
    {
        if (_chunkIndex >= _stream.Chunks.Count)
            return false;
        
        var chunk = _stream.Chunks[_chunkIndex];
        _repeatIndex++;
        
        if (_repeatIndex > chunk.RepeatCount)
        {
            _chunkIndex++;
            _repeatIndex = 0;
            
            if (_chunkIndex < _stream.Chunks.Count)
            {
                _currentTime = _stream.Chunks[_chunkIndex].StartTime;
            }
        }
        else
        {
            _currentTime += chunk.Duration;
        }
        
        return _chunkIndex < _stream.Chunks.Count;
    }
    
    /// <summary>
    /// Seeks to segment containing the specified time.
    /// </summary>
    /// <param name="timeScaleUnits">Time in time scale units.</param>
    /// <returns>True if found, false if time is beyond content.</returns>
    public bool SeekTo(long timeScaleUnits)
    {
        Reset();
        
        for (int i = 0; i < _stream.Chunks.Count; i++)
        {
            var chunk = _stream.Chunks[i];
            long chunkEnd = chunk.EndTime;
            
            if (timeScaleUnits < chunkEnd)
            {
                _chunkIndex = i;
                
                // Find repeat index within chunk
                long timeInChunk = timeScaleUnits - chunk.StartTime;
                _repeatIndex = chunk.Duration > 0 
                    ? (int)(timeInChunk / chunk.Duration) 
                    : 0;
                _repeatIndex = Math.Min(_repeatIndex, chunk.RepeatCount);
                
                _currentTime = chunk.StartTime + (_repeatIndex * chunk.Duration);
                return true;
            }
        }
        
        return false;
    }
}

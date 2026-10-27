// HLS (HTTP Live Streaming) Types
// Based on Apple HLS specification

namespace MediaKernel.Core.Streaming;

/// <summary>
/// Type of HLS playlist.
/// </summary>
public enum HlsPlaylistType
{
    /// <summary>Master playlist containing variant streams.</summary>
    Master,
    
    /// <summary>Media playlist containing segments.</summary>
    Media,
    
    /// <summary>VOD (Video on Demand) playlist - has end marker.</summary>
    Vod,
    
    /// <summary>Live/Event playlist - no end marker.</summary>
    Live
}

/// <summary>
/// HLS encryption method.
/// </summary>
public enum HlsEncryptionMethod
{
    /// <summary>No encryption.</summary>
    None,
    
    /// <summary>AES-128 encryption.</summary>
    Aes128,
    
    /// <summary>Sample-AES encryption.</summary>
    SampleAes
}

/// <summary>
/// Represents a variant stream in a master playlist.
/// </summary>
public sealed class HlsVariant
{
    /// <summary>Bandwidth in bits per second.</summary>
    public int Bandwidth { get; init; }
    
    /// <summary>Average bandwidth in bits per second (optional).</summary>
    public int? AverageBandwidth { get; init; }
    
    /// <summary>Video width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Video height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Frame rate (optional).</summary>
    public float? FrameRate { get; init; }
    
    /// <summary>Codec string (e.g., "avc1.42e00a,mp4a.40.2").</summary>
    public string Codecs { get; init; } = string.Empty;
    
    /// <summary>URI to the media playlist.</summary>
    public string Uri { get; init; } = string.Empty;
    
    /// <summary>Audio group ID (for alternate audio).</summary>
    public string? AudioGroupId { get; init; }
    
    /// <summary>Subtitle group ID.</summary>
    public string? SubtitleGroupId { get; init; }
    
    /// <summary>Closed captions group ID.</summary>
    public string? ClosedCaptionsGroupId { get; init; }
}

/// <summary>
/// Represents an alternate rendition (audio, subtitles, etc.).
/// </summary>
public sealed class HlsRendition
{
    /// <summary>Type of rendition.</summary>
    public HlsRenditionType Type { get; init; }
    
    /// <summary>Group ID for associating with variants.</summary>
    public string GroupId { get; init; } = string.Empty;
    
    /// <summary>Human-readable name.</summary>
    public string Name { get; init; } = string.Empty;
    
    /// <summary>Language code (e.g., "en", "es").</summary>
    public string? Language { get; init; }
    
    /// <summary>Whether this is the default rendition.</summary>
    public bool IsDefault { get; init; }
    
    /// <summary>Whether this should be auto-selected.</summary>
    public bool IsAutoSelect { get; init; }
    
    /// <summary>URI to the rendition playlist (optional for embedded).</summary>
    public string? Uri { get; init; }
    
    /// <summary>Characteristics (e.g., "public.accessibility.describes-video").</summary>
    public string? Characteristics { get; init; }
}

/// <summary>
/// Rendition type.
/// </summary>
public enum HlsRenditionType
{
    Audio,
    Video,
    Subtitles,
    ClosedCaptions
}

/// <summary>
/// Represents a media segment in a media playlist.
/// </summary>
public sealed class HlsSegment
{
    /// <summary>Segment duration in seconds.</summary>
    public float Duration { get; init; }
    
    /// <summary>Segment title (optional).</summary>
    public string? Title { get; init; }
    
    /// <summary>URI to the segment.</summary>
    public string Uri { get; init; } = string.Empty;
    
    /// <summary>Sequence number.</summary>
    public long SequenceNumber { get; init; }
    
    /// <summary>Whether this segment starts a discontinuity.</summary>
    public bool IsDiscontinuity { get; init; }
    
    /// <summary>Byte range start (optional).</summary>
    public long? ByteRangeStart { get; init; }
    
    /// <summary>Byte range length (optional).</summary>
    public long? ByteRangeLength { get; init; }
    
    /// <summary>Program date/time (optional).</summary>
    public DateTimeOffset? ProgramDateTime { get; init; }
    
    /// <summary>Encryption key info (null if unencrypted).</summary>
    public HlsKeyInfo? KeyInfo { get; init; }
}

/// <summary>
/// Encryption key information.
/// </summary>
public sealed class HlsKeyInfo
{
    /// <summary>Encryption method.</summary>
    public HlsEncryptionMethod Method { get; init; }
    
    /// <summary>URI to the encryption key.</summary>
    public string? Uri { get; init; }
    
    /// <summary>Initialization vector (hex string).</summary>
    public string? InitializationVector { get; init; }
    
    /// <summary>Key format (e.g., "identity").</summary>
    public string? KeyFormat { get; init; }
}

/// <summary>
/// Parsed HLS master playlist.
/// </summary>
public sealed class HlsMasterPlaylist
{
    /// <summary>HLS version.</summary>
    public int Version { get; init; } = 3;
    
    /// <summary>Variant streams.</summary>
    public List<HlsVariant> Variants { get; } = [];
    
    /// <summary>Alternate renditions (audio, subtitles).</summary>
    public List<HlsRendition> Renditions { get; } = [];
    
    /// <summary>Whether playlist was parsed successfully.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Base URI for resolving relative URIs.</summary>
    public string BaseUri { get; init; } = string.Empty;
    
    /// <summary>
    /// Gets variants sorted by bandwidth (highest first).
    /// </summary>
    public IEnumerable<HlsVariant> GetVariantsByBandwidth()
    {
        return Variants.OrderByDescending(v => v.Bandwidth);
    }
    
    /// <summary>
    /// Selects the best variant for the given available bandwidth.
    /// </summary>
    public HlsVariant? SelectVariantForBandwidth(int availableBandwidth)
    {
        // Select highest quality that fits within available bandwidth
        // Use 80% of available bandwidth as safety margin
        int targetBandwidth = (int)(availableBandwidth * 0.8);
        
        return Variants
            .Where(v => v.Bandwidth <= targetBandwidth)
            .OrderByDescending(v => v.Bandwidth)
            .FirstOrDefault()
            ?? Variants.OrderBy(v => v.Bandwidth).FirstOrDefault();
    }
}

/// <summary>
/// Parsed HLS media playlist.
/// </summary>
public sealed class HlsMediaPlaylist
{
    /// <summary>HLS version.</summary>
    public int Version { get; init; } = 3;
    
    /// <summary>Target duration in seconds.</summary>
    public int TargetDuration { get; init; }
    
    /// <summary>Media sequence number of first segment.</summary>
    public long MediaSequence { get; init; }
    
    /// <summary>Discontinuity sequence number.</summary>
    public long DiscontinuitySequence { get; init; }
    
    /// <summary>Playlist type (VOD, EVENT, or omitted for live).</summary>
    public HlsPlaylistType PlaylistType { get; init; } = HlsPlaylistType.Live;
    
    /// <summary>Whether playlist has ended (VOD).</summary>
    public bool HasEnded { get; init; }
    
    /// <summary>Segments in the playlist.</summary>
    public List<HlsSegment> Segments { get; } = [];
    
    /// <summary>Whether playlist was parsed successfully.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Base URI for resolving relative URIs.</summary>
    public string BaseUri { get; init; } = string.Empty;
    
    /// <summary>Low-Latency HLS information (null if not LL-HLS).</summary>
    public HlsLowLatencyInfo? LowLatencyInfo { get; init; }
    
    /// <summary>
    /// Gets total duration of all segments in seconds.
    /// </summary>
    public float TotalDuration => Segments.Sum(s => s.Duration);
    
    /// <summary>
    /// Gets the segment at the specified position (time in seconds).
    /// </summary>
    public HlsSegment? GetSegmentAtTime(float timeSeconds)
    {
        float accumulated = 0;
        
        foreach (var segment in Segments)
        {
            if (accumulated + segment.Duration > timeSeconds)
                return segment;
            
            accumulated += segment.Duration;
        }
        
        return Segments.LastOrDefault();
    }
}

// ========== Low-Latency HLS (LL-HLS) Types ==========

/// <summary>
/// Represents a partial segment in LL-HLS.
/// </summary>
public sealed class HlsPartialSegment
{
    /// <summary>Duration in seconds.</summary>
    public float Duration { get; init; }
    
    /// <summary>URI to the partial segment.</summary>
    public string Uri { get; init; } = string.Empty;
    
    /// <summary>Whether this partial is independently decodable.</summary>
    public bool IsIndependent { get; init; }
    
    /// <summary>Byte range start (optional).</summary>
    public long? ByteRangeStart { get; init; }
    
    /// <summary>Byte range length (optional).</summary>
    public long? ByteRangeLength { get; init; }
    
    /// <summary>Whether this is a gap (missing partial).</summary>
    public bool IsGap { get; init; }
}

/// <summary>
/// Represents a preload hint for upcoming content.
/// </summary>
public sealed class HlsPreloadHint
{
    /// <summary>Type of hint (PART, MAP).</summary>
    public HlsPreloadHintType Type { get; init; }
    
    /// <summary>URI to the hinted resource.</summary>
    public string Uri { get; init; } = string.Empty;
    
    /// <summary>Byte range start (optional).</summary>
    public long? ByteRangeStart { get; init; }
    
    /// <summary>Byte range length (optional).</summary>
    public long? ByteRangeLength { get; init; }
}

/// <summary>
/// Type of preload hint.
/// </summary>
public enum HlsPreloadHintType
{
    Part,
    Map
}

/// <summary>
/// Rendition report for server-side tracking of other renditions.
/// </summary>
public sealed class HlsRenditionReport
{
    /// <summary>URI to the rendition playlist.</summary>
    public string Uri { get; init; } = string.Empty;
    
    /// <summary>Last media sequence number.</summary>
    public long? LastMediaSequence { get; init; }
    
    /// <summary>Last partial segment index.</summary>
    public int? LastPartIndex { get; init; }
}

/// <summary>
/// Low-Latency HLS playlist information.
/// </summary>
public sealed class HlsLowLatencyInfo
{
    /// <summary>Part target duration in seconds.</summary>
    public float PartTargetDuration { get; init; }
    
    /// <summary>Partial segments for the current incomplete segment.</summary>
    public List<HlsPartialSegment> PartialSegments { get; } = [];
    
    /// <summary>Preload hints for upcoming content.</summary>
    public List<HlsPreloadHint> PreloadHints { get; } = [];
    
    /// <summary>Rendition reports for other qualities.</summary>
    public List<HlsRenditionReport> RenditionReports { get; } = [];
    
    /// <summary>Server control parameters.</summary>
    public HlsServerControl? ServerControl { get; init; }
    
    /// <summary>Whether this is a delta playlist update.</summary>
    public bool IsDeltaUpdate { get; init; }
    
    /// <summary>Skip tag for delta updates (number of old segments to skip).</summary>
    public int SkipSegments { get; init; }
}

/// <summary>
/// Server control parameters for LL-HLS.
/// </summary>
public sealed class HlsServerControl
{
    /// <summary>Whether server supports blocking playlist reload.</summary>
    public bool CanBlockReload { get; init; }
    
    /// <summary>Whether server can skip delivering unchanged segments.</summary>
    public bool CanSkipUntil { get; init; }
    
    /// <summary>Duration to hold request before responding (for long polling).</summary>
    public float? HoldBack { get; init; }
    
    /// <summary>Part hold back duration.</summary>
    public float? PartHoldBack { get; init; }
    
    /// <summary>Whether server can skip date ranges.</summary>
    public bool CanSkipDateRanges { get; init; }
}

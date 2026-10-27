// DASH (Dynamic Adaptive Streaming over HTTP) Types
// Based on MPEG-DASH specification

namespace MediaKernel.Core.Streaming;

/// <summary>
/// MPD presentation type.
/// </summary>
public enum DashPresentationType
{
    /// <summary>Static (VOD) content.</summary>
    Static,
    
    /// <summary>Dynamic (live) content.</summary>
    Dynamic
}

/// <summary>
/// Content type for an adaptation set.
/// </summary>
public enum DashContentType
{
    Unknown,
    Video,
    Audio,
    Subtitles
}

/// <summary>
/// DASH segment addressing mode.
/// </summary>
public enum DashSegmentMode
{
    /// <summary>Using SegmentTemplate for URL generation.</summary>
    Template,
    
    /// <summary>Using SegmentList with explicit URLs.</summary>
    List,
    
    /// <summary>Using SegmentBase for single-segment content.</summary>
    Base
}

/// <summary>
/// Parsed DASH MPD manifest.
/// </summary>
public sealed class DashManifest
{
    /// <summary>Whether manifest was parsed successfully.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Presentation type (static/dynamic).</summary>
    public DashPresentationType Type { get; init; }
    
    /// <summary>MPD profiles (e.g., urn:mpeg:dash:profile:isoff-live:2011).</summary>
    public string Profiles { get; init; } = string.Empty;
    
    /// <summary>Minimum buffer time in seconds.</summary>
    public float MinBufferTimeSeconds { get; init; }
    
    /// <summary>Total media presentation duration (for VOD).</summary>
    public TimeSpan? MediaPresentationDuration { get; init; }
    
    /// <summary>Minimum update period for live manifests.</summary>
    public TimeSpan? MinimumUpdatePeriod { get; init; }
    
    /// <summary>Availability start time for live content.</summary>
    public DateTimeOffset? AvailabilityStartTime { get; init; }
    
    /// <summary>Base URI for resolving relative URLs.</summary>
    public string BaseUri { get; init; } = string.Empty;
    
    /// <summary>Periods in the manifest.</summary>
    public List<DashPeriod> Periods { get; } = [];
    
    /// <summary>
    /// Gets all video adaptation sets across all periods.
    /// </summary>
    public IEnumerable<DashAdaptationSet> GetVideoAdaptationSets()
    {
        return Periods.SelectMany(p => p.AdaptationSets)
            .Where(a => a.ContentType == DashContentType.Video);
    }
    
    /// <summary>
    /// Gets all audio adaptation sets across all periods.
    /// </summary>
    public IEnumerable<DashAdaptationSet> GetAudioAdaptationSets()
    {
        return Periods.SelectMany(p => p.AdaptationSets)
            .Where(a => a.ContentType == DashContentType.Audio);
    }
}

/// <summary>
/// A period within the MPD.
/// </summary>
public sealed class DashPeriod
{
    /// <summary>Period ID.</summary>
    public string Id { get; init; } = string.Empty;
    
    /// <summary>Start time of the period.</summary>
    public TimeSpan Start { get; init; }
    
    /// <summary>Duration of the period (optional).</summary>
    public TimeSpan? Duration { get; init; }
    
    /// <summary>Adaptation sets in this period.</summary>
    public List<DashAdaptationSet> AdaptationSets { get; } = [];
    
    /// <summary>Base URL for this period (optional).</summary>
    public string? BaseUrl { get; init; }
}

/// <summary>
/// An adaptation set (group of interchangeable streams).
/// </summary>
public sealed class DashAdaptationSet
{
    /// <summary>Adaptation set ID.</summary>
    public int Id { get; init; }
    
    /// <summary>Content type (video/audio/subtitles).</summary>
    public DashContentType ContentType { get; init; }
    
    /// <summary>MIME type (e.g., "video/mp4").</summary>
    public string MimeType { get; init; } = string.Empty;
    
    /// <summary>Codec string.</summary>
    public string Codecs { get; init; } = string.Empty;
    
    /// <summary>Language code (for audio/subtitles).</summary>
    public string? Language { get; init; }
    
    /// <summary>Width (for video).</summary>
    public int Width { get; init; }
    
    /// <summary>Height (for video).</summary>
    public int Height { get; init; }
    
    /// <summary>Frame rate as string (e.g., "30000/1001").</summary>
    public string? FrameRate { get; init; }
    
    /// <summary>Segment template (if using template mode).</summary>
    public DashSegmentTemplate? SegmentTemplate { get; init; }
    
    /// <summary>Representations (quality variants).</summary>
    public List<DashRepresentation> Representations { get; } = [];
    
    /// <summary>Base URL for this adaptation set.</summary>
    public string? BaseUrl { get; init; }
    
    /// <summary>
    /// Gets representations sorted by bandwidth (highest first).
    /// </summary>
    public IEnumerable<DashRepresentation> GetRepresentationsByBandwidth()
    {
        return Representations.OrderByDescending(r => r.Bandwidth);
    }
    
    /// <summary>
    /// Selects best representation for given bandwidth.
    /// </summary>
    public DashRepresentation? SelectRepresentationForBandwidth(int availableBandwidth)
    {
        // Use 80% safety margin
        int targetBandwidth = (int)(availableBandwidth * 0.8);
        
        return Representations
            .Where(r => r.Bandwidth <= targetBandwidth)
            .OrderByDescending(r => r.Bandwidth)
            .FirstOrDefault()
            ?? Representations.OrderBy(r => r.Bandwidth).FirstOrDefault();
    }
}

/// <summary>
/// A representation (specific quality variant).
/// </summary>
public sealed class DashRepresentation
{
    /// <summary>Representation ID.</summary>
    public string Id { get; init; } = string.Empty;
    
    /// <summary>Bandwidth in bits per second.</summary>
    public int Bandwidth { get; init; }
    
    /// <summary>Width (for video).</summary>
    public int Width { get; init; }
    
    /// <summary>Height (for video).</summary>
    public int Height { get; init; }
    
    /// <summary>Codec string (overrides AdaptationSet if present).</summary>
    public string? Codecs { get; init; }
    
    /// <summary>MIME type (overrides AdaptationSet if present).</summary>
    public string? MimeType { get; init; }
    
    /// <summary>Segment template (overrides AdaptationSet if present).</summary>
    public DashSegmentTemplate? SegmentTemplate { get; init; }
    
    /// <summary>Base URL for this representation.</summary>
    public string? BaseUrl { get; init; }
    
    /// <summary>Audio sample rate (for audio).</summary>
    public int? AudioSampleRate { get; init; }
}

/// <summary>
/// Segment template for URL generation.
/// </summary>
public sealed class DashSegmentTemplate
{
    /// <summary>URL template for media segments.</summary>
    public string Media { get; init; } = string.Empty;
    
    /// <summary>URL for initialization segment.</summary>
    public string? Initialization { get; init; }
    
    /// <summary>Timescale (units per second).</summary>
    public long Timescale { get; init; } = 1;
    
    /// <summary>Segment duration in timescale units.</summary>
    public long Duration { get; init; }
    
    /// <summary>Start number for segment numbering.</summary>
    public long StartNumber { get; init; } = 1;
    
    /// <summary>Segment timeline (for variable segment durations).</summary>
    public List<DashSegmentTimelineEntry>? SegmentTimeline { get; init; }
    
    /// <summary>
    /// Gets segment duration in seconds.
    /// </summary>
    public float DurationSeconds => Timescale > 0 ? (float)Duration / Timescale : 0;
    
    /// <summary>
    /// Generates the media segment URL for a given segment number.
    /// </summary>
    public string GetMediaUrl(long segmentNumber, string representationId)
    {
        return Media
            .Replace("$Number$", segmentNumber.ToString())
            .Replace("$RepresentationID$", representationId)
            .Replace("$Bandwidth$", "")  // Should be filled from representation
            .Replace("$Time$", "");      // Should be calculated from timeline
    }
    
    /// <summary>
    /// Generates the initialization segment URL.
    /// </summary>
    public string? GetInitializationUrl(string representationId)
    {
        return Initialization?
            .Replace("$RepresentationID$", representationId)
            .Replace("$Bandwidth$", "");
    }
}

/// <summary>
/// Entry in a segment timeline.
/// </summary>
public sealed class DashSegmentTimelineEntry
{
    /// <summary>Start time (t attribute).</summary>
    public long? StartTime { get; init; }
    
    /// <summary>Duration (d attribute).</summary>
    public long Duration { get; init; }
    
    /// <summary>Repeat count (r attribute, 0 = no repeat).</summary>
    public int RepeatCount { get; init; }
}

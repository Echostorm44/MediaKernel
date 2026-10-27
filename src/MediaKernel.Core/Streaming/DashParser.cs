// DASH (Dynamic Adaptive Streaming over HTTP) Parser
// Parses MPD manifests per MPEG-DASH specification

using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// Parser for DASH MPD manifests.
/// </summary>
public static class DashParser
{
    private const string MpdNamespace = "urn:mpeg:dash:schema:mpd:2011";

    /// <summary>
    /// Parses an MPD manifest from XML string.
    /// </summary>
    public static DashManifest Parse(string xmlContent, string baseUri = "")
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
            return new DashManifest { IsValid = false, BaseUri = baseUri };
        
        try
        {
            var document = XDocument.Parse(xmlContent);
            var mpd = document.Root;
            
            if (mpd == null || mpd.Name.LocalName != "MPD")
                return new DashManifest { IsValid = false, BaseUri = baseUri };
            
            XNamespace ns = mpd.GetDefaultNamespace();
            
            var manifest = new DashManifest
            {
                IsValid = true,
                BaseUri = baseUri,
                Type = ParsePresentationType(mpd.Attribute("type")?.Value),
                Profiles = mpd.Attribute("profiles")?.Value ?? string.Empty,
                MinBufferTimeSeconds = (float)ParseDuration(mpd.Attribute("minBufferTime")?.Value).TotalSeconds,
                MediaPresentationDuration = ParseNullableDuration(mpd.Attribute("mediaPresentationDuration")?.Value),
                MinimumUpdatePeriod = ParseNullableDuration(mpd.Attribute("minimumUpdatePeriod")?.Value),
                AvailabilityStartTime = ParseNullableDateTimeOffset(mpd.Attribute("availabilityStartTime")?.Value)
            };
            
            // Parse BaseURL at MPD level
            var mpdBaseUrl = mpd.Element(ns + "BaseURL")?.Value;
            string effectiveBaseUri = !string.IsNullOrEmpty(mpdBaseUrl) 
                ? ResolveUri(baseUri, mpdBaseUrl) 
                : baseUri;
            
            // Parse Periods
            foreach (var periodElement in mpd.Elements(ns + "Period"))
            {
                var period = ParsePeriod(periodElement, ns, effectiveBaseUri);
                manifest.Periods.Add(period);
            }
            
            return manifest;
        }
        catch (XmlException)
        {
            return new DashManifest { IsValid = false, BaseUri = baseUri };
        }
    }

    private static DashPeriod ParsePeriod(XElement periodElement, XNamespace ns, string baseUri)
    {
        var periodBaseUrl = periodElement.Element(ns + "BaseURL")?.Value;
        string effectiveBaseUri = !string.IsNullOrEmpty(periodBaseUrl) 
            ? ResolveUri(baseUri, periodBaseUrl) 
            : baseUri;
        
        var period = new DashPeriod
        {
            Id = periodElement.Attribute("id")?.Value ?? string.Empty,
            Start = ParseDuration(periodElement.Attribute("start")?.Value),
            Duration = ParseNullableDuration(periodElement.Attribute("duration")?.Value),
            BaseUrl = periodBaseUrl
        };
        
        foreach (var adaptationSetElement in periodElement.Elements(ns + "AdaptationSet"))
        {
            var adaptationSet = ParseAdaptationSet(adaptationSetElement, ns, effectiveBaseUri);
            period.AdaptationSets.Add(adaptationSet);
        }
        
        return period;
    }

    private static DashAdaptationSet ParseAdaptationSet(XElement element, XNamespace ns, string baseUri)
    {
        var adaptationBaseUrl = element.Element(ns + "BaseURL")?.Value;
        string effectiveBaseUri = !string.IsNullOrEmpty(adaptationBaseUrl) 
            ? ResolveUri(baseUri, adaptationBaseUrl) 
            : baseUri;
        
        var mimeType = element.Attribute("mimeType")?.Value ?? string.Empty;
        
        var adaptationSet = new DashAdaptationSet
        {
            Id = ParseInt(element.Attribute("id")?.Value, 0),
            ContentType = DetermineContentType(
                element.Attribute("contentType")?.Value,
                mimeType),
            MimeType = mimeType,
            Codecs = element.Attribute("codecs")?.Value ?? string.Empty,
            Language = element.Attribute("lang")?.Value,
            Width = ParseInt(element.Attribute("width")?.Value, 0),
            Height = ParseInt(element.Attribute("height")?.Value, 0),
            FrameRate = element.Attribute("frameRate")?.Value,
            SegmentTemplate = ParseSegmentTemplate(element.Element(ns + "SegmentTemplate"), ns),
            BaseUrl = adaptationBaseUrl
        };
        
        foreach (var representationElement in element.Elements(ns + "Representation"))
        {
            var representation = ParseRepresentation(representationElement, ns, effectiveBaseUri);
            adaptationSet.Representations.Add(representation);
        }
        
        return adaptationSet;
    }

    private static DashRepresentation ParseRepresentation(XElement element, XNamespace ns, string baseUri)
    {
        var representationBaseUrl = element.Element(ns + "BaseURL")?.Value;
        
        return new DashRepresentation
        {
            Id = element.Attribute("id")?.Value ?? string.Empty,
            Bandwidth = ParseInt(element.Attribute("bandwidth")?.Value, 0),
            Width = ParseInt(element.Attribute("width")?.Value, 0),
            Height = ParseInt(element.Attribute("height")?.Value, 0),
            Codecs = element.Attribute("codecs")?.Value,
            MimeType = element.Attribute("mimeType")?.Value,
            SegmentTemplate = ParseSegmentTemplate(element.Element(ns + "SegmentTemplate"), ns),
            BaseUrl = representationBaseUrl,
            AudioSampleRate = ParseNullableInt(element.Attribute("audioSamplingRate")?.Value)
        };
    }

    private static DashSegmentTemplate? ParseSegmentTemplate(XElement? element, XNamespace ns)
    {
        if (element == null)
            return null;
        
        List<DashSegmentTimelineEntry>? timeline = null;
        var timelineElement = element.Element(ns + "SegmentTimeline");
        
        if (timelineElement != null)
        {
            timeline = [];
            foreach (var sElement in timelineElement.Elements(ns + "S"))
            {
                var entry = new DashSegmentTimelineEntry
                {
                    StartTime = ParseNullableLong(sElement.Attribute("t")?.Value),
                    Duration = ParseLong(sElement.Attribute("d")?.Value, 0),
                    RepeatCount = ParseInt(sElement.Attribute("r")?.Value, 0)
                };
                timeline.Add(entry);
            }
        }
        
        return new DashSegmentTemplate
        {
            Media = element.Attribute("media")?.Value ?? string.Empty,
            Initialization = element.Attribute("initialization")?.Value,
            Timescale = ParseLong(element.Attribute("timescale")?.Value, 1),
            Duration = ParseLong(element.Attribute("duration")?.Value, 0),
            StartNumber = ParseLong(element.Attribute("startNumber")?.Value, 1),
            SegmentTimeline = timeline
        };
    }

    private static DashPresentationType ParsePresentationType(string? value)
    {
        return value?.Equals("dynamic", StringComparison.OrdinalIgnoreCase) == true
            ? DashPresentationType.Dynamic
            : DashPresentationType.Static;
    }

    private static DashContentType DetermineContentType(string? contentType, string mimeType)
    {
        // First check explicit contentType attribute
        if (!string.IsNullOrEmpty(contentType))
        {
            return contentType.ToLowerInvariant() switch
            {
                "video" => DashContentType.Video,
                "audio" => DashContentType.Audio,
                "text" => DashContentType.Subtitles,
                _ => DashContentType.Unknown
            };
        }
        
        // Fall back to mime type
        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return DashContentType.Video;
        if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return DashContentType.Audio;
        if (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Contains("ttml", StringComparison.OrdinalIgnoreCase))
            return DashContentType.Subtitles;
        
        return DashContentType.Unknown;
    }

    /// <summary>
    /// Parses ISO 8601 duration format (PT#H#M#S).
    /// </summary>
    private static TimeSpan ParseDuration(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith("PT", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.Zero;
        
        // Remove PT prefix
        var durationStr = value.AsSpan(2);
        
        double hours = 0, minutes = 0, seconds = 0;
        int startIndex = 0;
        
        for (int i = 0; i < durationStr.Length; i++)
        {
            char c = durationStr[i];
            
            if (c == 'H' || c == 'h')
            {
                hours = ParseDouble(durationStr[startIndex..i].ToString(), 0);
                startIndex = i + 1;
            }
            else if (c == 'M' || c == 'm')
            {
                minutes = ParseDouble(durationStr[startIndex..i].ToString(), 0);
                startIndex = i + 1;
            }
            else if (c == 'S' || c == 's')
            {
                seconds = ParseDouble(durationStr[startIndex..i].ToString(), 0);
                startIndex = i + 1;
            }
        }
        
        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan? ParseNullableDuration(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        
        var duration = ParseDuration(value);
        return duration != TimeSpan.Zero ? duration : null;
    }

    private static DateTimeOffset? ParseNullableDateTimeOffset(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
            return result;
        
        return null;
    }

    private static int ParseInt(string? value, int defaultValue)
    {
        if (string.IsNullOrEmpty(value))
            return defaultValue;
        
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) 
            ? result 
            : defaultValue;
    }

    private static int? ParseNullableInt(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) 
            ? result 
            : null;
    }

    private static long ParseLong(string? value, long defaultValue)
    {
        if (string.IsNullOrEmpty(value))
            return defaultValue;
        
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) 
            ? result 
            : defaultValue;
    }

    private static long? ParseNullableLong(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) 
            ? result 
            : null;
    }

    private static double ParseDouble(string value, double defaultValue)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) 
            ? result 
            : defaultValue;
    }

    /// <summary>
    /// Resolves a relative URI against a base URI.
    /// </summary>
    public static string ResolveUri(string baseUri, string relativeUri)
    {
        if (string.IsNullOrEmpty(baseUri) || Uri.IsWellFormedUriString(relativeUri, UriKind.Absolute))
            return relativeUri;
        
        if (Uri.TryCreate(baseUri, UriKind.Absolute, out var baseUriObj) &&
            Uri.TryCreate(baseUriObj, relativeUri, out var resolvedUri))
        {
            return resolvedUri.ToString();
        }
        
        // Fallback: simple path join
        if (baseUri.EndsWith('/'))
            return baseUri + relativeUri;
        
        int lastSlash = baseUri.LastIndexOf('/');
        if (lastSlash >= 0)
            return baseUri[..(lastSlash + 1)] + relativeUri;
        
        return relativeUri;
    }
}

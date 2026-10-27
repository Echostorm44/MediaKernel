// HLS (HTTP Live Streaming) Parser
// Parses M3U8 master and media playlists per Apple HLS specification

using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// Parser for HLS M3U8 playlists.
/// </summary>
public static partial class HlsParser
{
    private const string ExtM3U = "#EXTM3U";
    private const string ExtVersion = "#EXT-X-VERSION:";
    private const string ExtStreamInf = "#EXT-X-STREAM-INF:";
    private const string ExtMedia = "#EXT-X-MEDIA:";
    private const string ExtInf = "#EXTINF:";
    private const string ExtTargetDuration = "#EXT-X-TARGETDURATION:";
    private const string ExtMediaSequence = "#EXT-X-MEDIA-SEQUENCE:";
    private const string ExtDiscontinuitySequence = "#EXT-X-DISCONTINUITY-SEQUENCE:";
    private const string ExtPlaylistType = "#EXT-X-PLAYLIST-TYPE:";
    private const string ExtEndList = "#EXT-X-ENDLIST";
    private const string ExtDiscontinuity = "#EXT-X-DISCONTINUITY";
    private const string ExtKey = "#EXT-X-KEY:";
    private const string ExtByteRange = "#EXT-X-BYTERANGE:";
    private const string ExtProgramDateTime = "#EXT-X-PROGRAM-DATE-TIME:";
    
    // Low-Latency HLS tags
    private const string ExtPartInf = "#EXT-X-PART-INF:";
    private const string ExtPart = "#EXT-X-PART:";
    private const string ExtPreloadHint = "#EXT-X-PRELOAD-HINT:";
    private const string ExtServerControl = "#EXT-X-SERVER-CONTROL:";
    private const string ExtRenditionReport = "#EXT-X-RENDITION-REPORT:";
    private const string ExtSkip = "#EXT-X-SKIP:";

    /// <summary>
    /// Determines if the playlist content is a master playlist.
    /// </summary>
    public static bool IsMasterPlaylist(ReadOnlySpan<char> content)
    {
        // Master playlists contain EXT-X-STREAM-INF tags
        return content.Contains(ExtStreamInf.AsSpan(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses a master playlist.
    /// </summary>
    public static HlsMasterPlaylist ParseMasterPlaylist(string content, string baseUri = "")
    {
        if (string.IsNullOrWhiteSpace(content))
            return new HlsMasterPlaylist { IsValid = false, BaseUri = baseUri };
        
        var lines = content.Split('\n', StringSplitOptions.None);
        
        if (lines.Length == 0 || !lines[0].Trim().Equals(ExtM3U, StringComparison.Ordinal))
            return new HlsMasterPlaylist { IsValid = false, BaseUri = baseUri };
        
        int version = 3;
        var variants = new List<HlsVariant>();
        var renditions = new List<HlsRendition>();
        
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            if (string.IsNullOrEmpty(line) || (line.StartsWith('#') && !line.StartsWith("#EXT")))
                continue;
            
            if (line.StartsWith(ExtVersion, StringComparison.Ordinal))
            {
                version = ParseInt(line.AsSpan(ExtVersion.Length), 3);
            }
            else if (line.StartsWith(ExtStreamInf, StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line.AsSpan(ExtStreamInf.Length));
                
                // Next non-empty, non-comment line is the URI
                string? uri = null;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    var nextLine = lines[j].Trim();
                    if (!string.IsNullOrEmpty(nextLine) && !nextLine.StartsWith('#'))
                    {
                        uri = nextLine;
                        i = j;
                        break;
                    }
                }
                
                if (uri != null)
                {
                    var variant = new HlsVariant
                    {
                        Bandwidth = GetAttributeInt(attributes, "BANDWIDTH"),
                        AverageBandwidth = GetAttributeNullableInt(attributes, "AVERAGE-BANDWIDTH"),
                        Width = ParseResolutionWidth(GetAttributeValue(attributes, "RESOLUTION")),
                        Height = ParseResolutionHeight(GetAttributeValue(attributes, "RESOLUTION")),
                        FrameRate = GetAttributeNullableFloat(attributes, "FRAME-RATE"),
                        Codecs = GetAttributeValue(attributes, "CODECS") ?? string.Empty,
                        Uri = uri,
                        AudioGroupId = GetAttributeValue(attributes, "AUDIO"),
                        SubtitleGroupId = GetAttributeValue(attributes, "SUBTITLES"),
                        ClosedCaptionsGroupId = GetAttributeValue(attributes, "CLOSED-CAPTIONS")
                    };
                    
                    variants.Add(variant);
                }
            }
            else if (line.StartsWith(ExtMedia, StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line.AsSpan(ExtMedia.Length));
                
                var rendition = new HlsRendition
                {
                    Type = ParseRenditionType(GetAttributeValue(attributes, "TYPE")),
                    GroupId = GetAttributeValue(attributes, "GROUP-ID") ?? string.Empty,
                    Name = GetAttributeValue(attributes, "NAME") ?? string.Empty,
                    Language = GetAttributeValue(attributes, "LANGUAGE"),
                    IsDefault = GetAttributeValue(attributes, "DEFAULT")?.Equals("YES", StringComparison.OrdinalIgnoreCase) == true,
                    IsAutoSelect = GetAttributeValue(attributes, "AUTOSELECT")?.Equals("YES", StringComparison.OrdinalIgnoreCase) == true,
                    Uri = GetAttributeValue(attributes, "URI"),
                    Characteristics = GetAttributeValue(attributes, "CHARACTERISTICS")
                };
                
                renditions.Add(rendition);
            }
        }
        
        var playlist = new HlsMasterPlaylist { Version = version, IsValid = true, BaseUri = baseUri };
        playlist.Variants.AddRange(variants);
        playlist.Renditions.AddRange(renditions);
        
        return playlist;
    }

    /// <summary>
    /// Parses a media playlist.
    /// </summary>
    public static HlsMediaPlaylist ParseMediaPlaylist(string content, string baseUri = "")
    {
        if (string.IsNullOrWhiteSpace(content))
            return new HlsMediaPlaylist { IsValid = false, BaseUri = baseUri };
        
        var lines = content.Split('\n', StringSplitOptions.None);
        
        if (lines.Length == 0 || !lines[0].Trim().Equals(ExtM3U, StringComparison.Ordinal))
            return new HlsMediaPlaylist { IsValid = false, BaseUri = baseUri };
        
        int version = 3;
        int targetDuration = 10;
        long mediaSequence = 0;
        long discontinuitySequence = 0;
        var playlistType = HlsPlaylistType.Live;
        bool hasEnded = false;
        HlsKeyInfo? currentKey = null;
        long? currentByteRangeStart = null;
        DateTimeOffset? currentProgramDateTime = null;
        bool nextIsDiscontinuity = false;
        
        var segments = new List<HlsSegment>();
        long sequenceNumber = 0;
        
        // LL-HLS state
        float partTargetDuration = 0;
        var partialSegments = new List<HlsPartialSegment>();
        var preloadHints = new List<HlsPreloadHint>();
        var renditionReports = new List<HlsRenditionReport>();
        HlsServerControl? serverControl = null;
        int skipSegments = 0;
        
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            if (string.IsNullOrEmpty(line) || (line.StartsWith('#') && !line.StartsWith("#EXT")))
                continue;
            
            if (line.StartsWith(ExtVersion, StringComparison.Ordinal))
            {
                version = ParseInt(line.AsSpan(ExtVersion.Length), 3);
            }
            else if (line.StartsWith(ExtTargetDuration, StringComparison.Ordinal))
            {
                targetDuration = ParseInt(line.AsSpan(ExtTargetDuration.Length), 10);
            }
            else if (line.StartsWith(ExtMediaSequence, StringComparison.Ordinal))
            {
                mediaSequence = ParseLong(line.AsSpan(ExtMediaSequence.Length), 0);
                sequenceNumber = mediaSequence;
            }
            else if (line.StartsWith(ExtDiscontinuitySequence, StringComparison.Ordinal))
            {
                discontinuitySequence = ParseLong(line.AsSpan(ExtDiscontinuitySequence.Length), 0);
            }
            else if (line.StartsWith(ExtPlaylistType, StringComparison.Ordinal))
            {
                var typeStr = line.AsSpan(ExtPlaylistType.Length);
                playlistType = typeStr.Equals("VOD", StringComparison.OrdinalIgnoreCase) 
                    ? HlsPlaylistType.Vod 
                    : typeStr.Equals("EVENT", StringComparison.OrdinalIgnoreCase)
                        ? HlsPlaylistType.Live
                        : HlsPlaylistType.Live;
            }
            else if (line.Equals(ExtEndList, StringComparison.Ordinal))
            {
                hasEnded = true;
            }
            else if (line.Equals(ExtDiscontinuity, StringComparison.Ordinal))
            {
                nextIsDiscontinuity = true;
            }
            else if (line.StartsWith(ExtKey, StringComparison.Ordinal))
            {
                currentKey = ParseKeyInfo(line.AsSpan(ExtKey.Length));
            }
            else if (line.StartsWith(ExtByteRange, StringComparison.Ordinal))
            {
                var (length, offset) = ParseByteRange(line.AsSpan(ExtByteRange.Length));
                currentByteRangeStart = offset;
            }
            else if (line.StartsWith(ExtProgramDateTime, StringComparison.Ordinal))
            {
                var dateStr = line.AsSpan(ExtProgramDateTime.Length).ToString();
                if (DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                    currentProgramDateTime = dt;
            }
            else if (line.StartsWith(ExtInf, StringComparison.Ordinal))
            {
                var (duration, title) = ParseExtInf(line.AsSpan(ExtInf.Length));
                
                // Next non-empty, non-comment line is the URI
                string? uri = null;
                long? byteRangeLength = null;
                long? byteRangeStart = currentByteRangeStart;
                
                for (int j = i + 1; j < lines.Length; j++)
                {
                    var nextLine = lines[j].Trim();
                    
                    if (nextLine.StartsWith(ExtByteRange, StringComparison.Ordinal))
                    {
                        var (len, off) = ParseByteRange(nextLine.AsSpan(ExtByteRange.Length));
                        byteRangeLength = len;
                        byteRangeStart = off ?? currentByteRangeStart;
                    }
                    else if (!string.IsNullOrEmpty(nextLine) && !nextLine.StartsWith('#'))
                    {
                        uri = nextLine;
                        i = j;
                        break;
                    }
                }
                
                if (uri != null)
                {
                    var segment = new HlsSegment
                    {
                        Duration = duration,
                        Title = title,
                        Uri = uri,
                        SequenceNumber = sequenceNumber++,
                        IsDiscontinuity = nextIsDiscontinuity,
                        ByteRangeStart = byteRangeStart,
                        ByteRangeLength = byteRangeLength,
                        ProgramDateTime = currentProgramDateTime,
                        KeyInfo = currentKey
                    };
                    
                    segments.Add(segment);
                    nextIsDiscontinuity = false;
                    
                    // Update byte range start for next segment if using packed segments
                    if (byteRangeLength.HasValue && byteRangeStart.HasValue)
                        currentByteRangeStart = byteRangeStart.Value + byteRangeLength.Value;
                    else
                        currentByteRangeStart = null;
                }
            }
            // LL-HLS tags
            else if (line.StartsWith(ExtPartInf, StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line.AsSpan(ExtPartInf.Length));
                if (attributes.TryGetValue("PART-TARGET", out var partTarget) && 
                    float.TryParse(partTarget, NumberStyles.Float, CultureInfo.InvariantCulture, out var pt))
                {
                    partTargetDuration = pt;
                }
            }
            else if (line.StartsWith(ExtPart, StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line.AsSpan(ExtPart.Length));
                
                var partUri = GetAttributeValue(attributes, "URI") ?? string.Empty;
                var duration = GetAttributeNullableFloat(attributes, "DURATION") ?? 0;
                var independent = GetAttributeValue(attributes, "INDEPENDENT")?.Equals("YES", StringComparison.OrdinalIgnoreCase) == true;
                var gap = GetAttributeValue(attributes, "GAP")?.Equals("YES", StringComparison.OrdinalIgnoreCase) == true;
                
                long? byteRangeStart = null;
                long? byteRangeLength = null;
                var byteRange = GetAttributeValue(attributes, "BYTERANGE");
                if (!string.IsNullOrEmpty(byteRange))
                {
                    var (len, off) = ParseByteRange(byteRange.AsSpan());
                    byteRangeLength = len;
                    byteRangeStart = off;
                }
                
                var partial = new HlsPartialSegment
                {
                    Duration = duration,
                    Uri = partUri,
                    IsIndependent = independent,
                    IsGap = gap,
                    ByteRangeStart = byteRangeStart,
                    ByteRangeLength = byteRangeLength
                };
                
                partialSegments.Add(partial);
            }
            else if (line.StartsWith(ExtPreloadHint, StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line.AsSpan(ExtPreloadHint.Length));
                
                var hintType = GetAttributeValue(attributes, "TYPE")?.ToUpperInvariant() switch
                {
                    "PART" => HlsPreloadHintType.Part,
                    "MAP" => HlsPreloadHintType.Map,
                    _ => HlsPreloadHintType.Part
                };
                
                var hintUri = GetAttributeValue(attributes, "URI") ?? string.Empty;
                
                long? byteRangeStart = null;
                long? byteRangeLength = null;
                var brStart = GetAttributeValue(attributes, "BYTERANGE-START");
                var brLen = GetAttributeValue(attributes, "BYTERANGE-LENGTH");
                if (!string.IsNullOrEmpty(brStart) && long.TryParse(brStart, out var brs))
                    byteRangeStart = brs;
                if (!string.IsNullOrEmpty(brLen) && long.TryParse(brLen, out var brl))
                    byteRangeLength = brl;
                
                var hint = new HlsPreloadHint
                {
                    Type = hintType,
                    Uri = hintUri,
                    ByteRangeStart = byteRangeStart,
                    ByteRangeLength = byteRangeLength
                };
                
                preloadHints.Add(hint);
            }
            else if (line.StartsWith(ExtServerControl, StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line.AsSpan(ExtServerControl.Length));
                
                serverControl = new HlsServerControl
                {
                    CanBlockReload = GetAttributeValue(attributes, "CAN-BLOCK-RELOAD")?.Equals("YES", StringComparison.OrdinalIgnoreCase) == true,
                    CanSkipUntil = GetAttributeNullableFloat(attributes, "CAN-SKIP-UNTIL").HasValue,
                    HoldBack = GetAttributeNullableFloat(attributes, "HOLD-BACK"),
                    PartHoldBack = GetAttributeNullableFloat(attributes, "PART-HOLD-BACK"),
                    CanSkipDateRanges = GetAttributeValue(attributes, "CAN-SKIP-DATERANGES")?.Equals("YES", StringComparison.OrdinalIgnoreCase) == true
                };
            }
            else if (line.StartsWith(ExtRenditionReport, StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line.AsSpan(ExtRenditionReport.Length));
                
                var report = new HlsRenditionReport
                {
                    Uri = GetAttributeValue(attributes, "URI") ?? string.Empty,
                    LastMediaSequence = long.TryParse(GetAttributeValue(attributes, "LAST-MSN"), out var lmsn) ? lmsn : null,
                    LastPartIndex = int.TryParse(GetAttributeValue(attributes, "LAST-PART"), out var lp) ? lp : null
                };
                
                renditionReports.Add(report);
            }
            else if (line.StartsWith(ExtSkip, StringComparison.Ordinal))
            {
                var attributes = ParseAttributes(line.AsSpan(ExtSkip.Length));
                if (attributes.TryGetValue("SKIPPED-SEGMENTS", out var skipped) && 
                    int.TryParse(skipped, out var ss))
                {
                    skipSegments = ss;
                }
            }
        }
        
        // Build LL-HLS info if any LL-HLS tags were found
        HlsLowLatencyInfo? lowLatencyInfo = null;
        if (partTargetDuration > 0 || partialSegments.Count > 0 || preloadHints.Count > 0 || 
            serverControl != null || skipSegments > 0 || renditionReports.Count > 0)
        {
            lowLatencyInfo = new HlsLowLatencyInfo
            {
                PartTargetDuration = partTargetDuration,
                ServerControl = serverControl,
                IsDeltaUpdate = skipSegments > 0,
                SkipSegments = skipSegments
            };
            lowLatencyInfo.PartialSegments.AddRange(partialSegments);
            lowLatencyInfo.PreloadHints.AddRange(preloadHints);
            lowLatencyInfo.RenditionReports.AddRange(renditionReports);
        }
        
        var playlist = new HlsMediaPlaylist
        {
            Version = version,
            TargetDuration = targetDuration,
            MediaSequence = mediaSequence,
            DiscontinuitySequence = discontinuitySequence,
            PlaylistType = hasEnded ? HlsPlaylistType.Vod : playlistType,
            HasEnded = hasEnded,
            IsValid = true,
            BaseUri = baseUri,
            LowLatencyInfo = lowLatencyInfo
        };
        
        playlist.Segments.AddRange(segments);
        
        return playlist;
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

    // Parsing helper methods
    
    private static Dictionary<string, string> ParseAttributes(ReadOnlySpan<char> attributeString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var str = attributeString.ToString();
        
        // Match key=value or key="value" patterns
        var matches = AttributeRegex().Matches(str);
        
        foreach (Match match in matches)
        {
            var key = match.Groups[1].Value;
            var value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            result[key] = value;
        }
        
        return result;
    }

    [GeneratedRegex(@"([A-Z0-9-]+)=(?:""([^""]*)""|([^,]*))")]
    private static partial Regex AttributeRegex();

    private static string? GetAttributeValue(Dictionary<string, string> attributes, string key)
    {
        return attributes.TryGetValue(key, out var value) ? value : null;
    }

    private static int GetAttributeInt(Dictionary<string, string> attributes, string key)
    {
        if (attributes.TryGetValue(key, out var value) && int.TryParse(value, out var result))
            return result;
        return 0;
    }

    private static int? GetAttributeNullableInt(Dictionary<string, string> attributes, string key)
    {
        if (attributes.TryGetValue(key, out var value) && int.TryParse(value, out var result))
            return result;
        return null;
    }

    private static float? GetAttributeNullableFloat(Dictionary<string, string> attributes, string key)
    {
        if (attributes.TryGetValue(key, out var value) && 
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;
        return null;
    }

    private static int ParseInt(ReadOnlySpan<char> span, int defaultValue)
    {
        return int.TryParse(span.Trim(), out var result) ? result : defaultValue;
    }

    private static long ParseLong(ReadOnlySpan<char> span, long defaultValue)
    {
        return long.TryParse(span.Trim(), out var result) ? result : defaultValue;
    }

    private static int ParseResolutionWidth(string? resolution)
    {
        if (string.IsNullOrEmpty(resolution))
            return 0;
        
        int xIndex = resolution.IndexOf('x');
        if (xIndex > 0 && int.TryParse(resolution.AsSpan(0, xIndex), out var width))
            return width;
        
        return 0;
    }

    private static int ParseResolutionHeight(string? resolution)
    {
        if (string.IsNullOrEmpty(resolution))
            return 0;
        
        int xIndex = resolution.IndexOf('x');
        if (xIndex >= 0 && xIndex < resolution.Length - 1 && 
            int.TryParse(resolution.AsSpan(xIndex + 1), out var height))
            return height;
        
        return 0;
    }

    private static HlsRenditionType ParseRenditionType(string? type)
    {
        return type?.ToUpperInvariant() switch
        {
            "AUDIO" => HlsRenditionType.Audio,
            "VIDEO" => HlsRenditionType.Video,
            "SUBTITLES" => HlsRenditionType.Subtitles,
            "CLOSED-CAPTIONS" => HlsRenditionType.ClosedCaptions,
            _ => HlsRenditionType.Audio
        };
    }

    private static (float Duration, string? Title) ParseExtInf(ReadOnlySpan<char> value)
    {
        var str = value.ToString();
        int commaIndex = str.IndexOf(',');
        
        string durationStr;
        string? title = null;
        
        if (commaIndex >= 0)
        {
            durationStr = str[..commaIndex];
            title = commaIndex < str.Length - 1 ? str[(commaIndex + 1)..].Trim() : null;
            if (string.IsNullOrEmpty(title))
                title = null;
        }
        else
        {
            durationStr = str;
        }
        
        if (float.TryParse(durationStr.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
            return (duration, title);
        
        return (0, title);
    }

    private static HlsKeyInfo ParseKeyInfo(ReadOnlySpan<char> attributeString)
    {
        var attributes = ParseAttributes(attributeString);
        
        var methodStr = GetAttributeValue(attributes, "METHOD");
        var method = methodStr?.ToUpperInvariant() switch
        {
            "NONE" => HlsEncryptionMethod.None,
            "AES-128" => HlsEncryptionMethod.Aes128,
            "SAMPLE-AES" => HlsEncryptionMethod.SampleAes,
            _ => HlsEncryptionMethod.None
        };
        
        return new HlsKeyInfo
        {
            Method = method,
            Uri = GetAttributeValue(attributes, "URI"),
            InitializationVector = GetAttributeValue(attributes, "IV"),
            KeyFormat = GetAttributeValue(attributes, "KEYFORMAT")
        };
    }

    private static (long? Length, long? Offset) ParseByteRange(ReadOnlySpan<char> value)
    {
        var str = value.Trim().ToString();
        int atIndex = str.IndexOf('@');
        
        if (atIndex >= 0)
        {
            if (long.TryParse(str.AsSpan(0, atIndex), out var length) &&
                long.TryParse(str.AsSpan(atIndex + 1), out var offset))
                return (length, offset);
        }
        else
        {
            if (long.TryParse(str, out var length))
                return (length, null);
        }
        
        return (null, null);
    }
}

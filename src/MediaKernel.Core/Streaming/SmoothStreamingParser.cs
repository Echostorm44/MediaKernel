// Microsoft Smooth Streaming (MSS/ISM) Parser
// Parses ISM/ISMC manifests per Microsoft IIS Smooth Streaming specification

using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace MediaKernel.Core.Streaming;

/// <summary>
/// Parser for Microsoft Smooth Streaming manifests.
/// </summary>
public static class SmoothStreamingParser
{
    /// <summary>
    /// Parses a Smooth Streaming manifest from XML string.
    /// </summary>
    /// <param name="xmlContent">The manifest XML content.</param>
    /// <param name="baseUri">Base URI for resolving relative URLs.</param>
    /// <returns>Parsed manifest.</returns>
    public static SmoothManifest Parse(string xmlContent, string baseUri = "")
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
            return new SmoothManifest { IsValid = false, BaseUri = baseUri };
        
        try
        {
            var document = XDocument.Parse(xmlContent);
            var root = document.Root;
            
            if (root == null || root.Name.LocalName != "SmoothStreamingMedia")
                return new SmoothManifest { IsValid = false, BaseUri = baseUri };
            
            var manifest = new SmoothManifest
            {
                IsValid = true,
                BaseUri = baseUri,
                MajorVersion = ParseInt(root.Attribute("MajorVersion")?.Value, 2),
                MinorVersion = ParseInt(root.Attribute("MinorVersion")?.Value, 0),
                TimeScale = ParseLong(root.Attribute("TimeScale")?.Value, 10_000_000),
                Duration = ParseLong(root.Attribute("Duration")?.Value, 0),
                IsLive = ParseBool(root.Attribute("IsLive")?.Value, false),
                LookaheadCount = ParseInt(root.Attribute("LookAheadCount")?.Value, 2),
                DvrWindowLength = ParseNullableLong(root.Attribute("DVRWindowLength")?.Value),
                Protection = ParseProtection(root.Element("Protection"))
            };
            
            // Parse StreamIndex elements
            foreach (var streamElement in root.Elements("StreamIndex"))
            {
                var stream = ParseStreamIndex(streamElement, manifest.TimeScale);
                manifest.StreamIndices.Add(stream);
            }
            
            return manifest;
        }
        catch (XmlException)
        {
            return new SmoothManifest { IsValid = false, BaseUri = baseUri };
        }
    }
    
    /// <summary>
    /// Checks if content appears to be a Smooth Streaming manifest.
    /// </summary>
    public static bool CanParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        
        // Quick check for root element
        return content.Contains("<SmoothStreamingMedia", StringComparison.OrdinalIgnoreCase);
    }
    
    private static SmoothStreamIndex ParseStreamIndex(XElement element, long manifestTimeScale)
    {
        string typeStr = element.Attribute("Type")?.Value ?? "";
        var streamType = typeStr.ToLowerInvariant() switch
        {
            "video" => SmoothStreamType.Video,
            "audio" => SmoothStreamType.Audio,
            "text" => SmoothStreamType.Text,
            _ => SmoothStreamType.Unknown
        };
        
        var stream = new SmoothStreamIndex
        {
            Type = streamType,
            Name = element.Attribute("Name")?.Value ?? "",
            Language = element.Attribute("Language")?.Value ?? "",
            Subtype = element.Attribute("Subtype")?.Value ?? "",
            UrlTemplate = element.Attribute("Url")?.Value ?? "",
            TimeScale = ParseLong(element.Attribute("TimeScale")?.Value, manifestTimeScale),
            ChunkCount = ParseInt(element.Attribute("Chunks")?.Value, 0)
        };
        
        // Parse QualityLevel elements
        foreach (var qualityElement in element.Elements("QualityLevel"))
        {
            var quality = ParseQualityLevel(qualityElement, streamType);
            stream.QualityLevels.Add(quality);
        }
        
        // Parse chunk timeline (c elements)
        long currentTime = 0;
        foreach (var chunkElement in element.Elements("c"))
        {
            var chunk = ParseChunk(chunkElement, ref currentTime);
            stream.Chunks.Add(chunk);
        }
        
        return stream;
    }
    
    private static SmoothQualityLevel ParseQualityLevel(XElement element, SmoothStreamType streamType)
    {
        return new SmoothQualityLevel
        {
            Index = ParseInt(element.Attribute("Index")?.Value, 0),
            Bitrate = ParseInt(element.Attribute("Bitrate")?.Value, 0),
            FourCC = element.Attribute("FourCC")?.Value ?? "",
            CodecPrivateData = element.Attribute("CodecPrivateData")?.Value ?? "",
            NALUnitLengthField = ParseInt(element.Attribute("NALUnitLengthField")?.Value, 4),
            // Video attributes
            MaxWidth = ParseInt(element.Attribute("MaxWidth")?.Value, 0),
            MaxHeight = ParseInt(element.Attribute("MaxHeight")?.Value, 0),
            // Audio attributes
            AudioTag = ParseInt(element.Attribute("AudioTag")?.Value, 0),
            Channels = ParseInt(element.Attribute("Channels")?.Value, 0),
            SamplingRate = ParseInt(element.Attribute("SamplingRate")?.Value, 0),
            BitsPerSample = ParseInt(element.Attribute("BitsPerSample")?.Value, 0),
            PacketSize = ParseInt(element.Attribute("PacketSize")?.Value, 0)
        };
    }
    
    private static SmoothChunk ParseChunk(XElement element, ref long currentTime)
    {
        // "t" attribute sets explicit start time
        if (element.Attribute("t") is { } tAttr)
        {
            currentTime = ParseLong(tAttr.Value, currentTime);
        }
        
        long duration = ParseLong(element.Attribute("d")?.Value, 0);
        int repeatCount = ParseInt(element.Attribute("r")?.Value, 0);
        long number = ParseLong(element.Attribute("n")?.Value, 0);
        
        var chunk = new SmoothChunk
        {
            Number = number,
            StartTime = currentTime,
            Duration = duration,
            RepeatCount = repeatCount
        };
        
        // Advance current time for next chunk
        currentTime += duration * (1 + repeatCount);
        
        return chunk;
    }
    
    private static SmoothProtection? ParseProtection(XElement? element)
    {
        if (element == null)
            return null;
        
        var protection = new SmoothProtection();
        
        foreach (var headerElement in element.Elements("ProtectionHeader"))
        {
            string? systemIdStr = headerElement.Attribute("SystemID")?.Value;
            if (string.IsNullOrEmpty(systemIdStr))
                continue;
            
            if (Guid.TryParse(systemIdStr, out Guid systemId))
            {
                string headerData = headerElement.Value?.Trim() ?? "";
                protection.ProtectionHeaders[systemId] = headerData;
            }
        }
        
        return protection.ProtectionHeaders.Count > 0 ? protection : null;
    }
    
    private static int ParseInt(string? value, int defaultValue)
    {
        if (string.IsNullOrEmpty(value))
            return defaultValue;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) 
            ? result 
            : defaultValue;
    }
    
    private static long ParseLong(string? value, long defaultValue)
    {
        if (string.IsNullOrEmpty(value))
            return defaultValue;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) 
            ? result 
            : defaultValue;
    }
    
    private static long? ParseNullableLong(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) 
            ? result 
            : null;
    }
    
    private static bool ParseBool(string? value, bool defaultValue)
    {
        if (string.IsNullOrEmpty(value))
            return defaultValue;
        return value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value == "1";
    }
}

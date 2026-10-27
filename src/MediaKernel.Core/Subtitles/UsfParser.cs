// MediaKernel - USF (Universal Subtitle Format) Parser
// Parses USF XML-based subtitle files.
// Reference: VLC subsusf.c
//
// USF is an XML-based subtitle format supporting:
// - Styles (font, size, color, italic, bold, underline)
// - Positioning (alignment, margins)
// - Karaoke timing
// - Embedded images

using System.Drawing;
using System.Text;
using System.Xml;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Text alignment for USF subtitles.
/// </summary>
public enum UsfAlignment
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    MiddleCenter,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

/// <summary>
/// A style definition in a USF file.
/// </summary>
public sealed class UsfStyle
{
    public string Name { get; set; } = "Default";
    public string? FontName { get; set; }
    public int FontSize { get; set; } = 24;
    public Color FontColor { get; set; } = Color.White;
    public Color OutlineColor { get; set; } = Color.Black;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strikethrough { get; set; }
    public int OutlineThickness { get; set; }
    public int ShadowOffset { get; set; }
    public UsfAlignment Alignment { get; set; } = UsfAlignment.BottomCenter;
    public int MarginHorizontal { get; set; }
    public int MarginVertical { get; set; } = 10;
}

/// <summary>
/// A karaoke segment in a USF subtitle.
/// </summary>
public readonly struct UsfKaraokeSegment
{
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string Text { get; init; }
}

/// <summary>
/// A subtitle event in a USF file.
/// </summary>
public sealed class UsfSubtitle
{
    public TimeSpan StartTime { get; init; }
    public TimeSpan EndTime { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? StyleName { get; set; }
    public UsfAlignment? Alignment { get; set; }
    public int? MarginHorizontal { get; set; }
    public int? MarginVertical { get; set; }
    public List<UsfKaraokeSegment>? KaraokeSegments { get; set; }
}

/// <summary>
/// Metadata from USF header.
/// </summary>
public sealed class UsfMetadata
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Language { get; set; }
    public string? Comment { get; set; }
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
}

/// <summary>
/// Result of parsing a USF file.
/// </summary>
public sealed class UsfParseResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public UsfMetadata Metadata { get; init; } = new();
    public List<UsfStyle> Styles { get; init; } = new();
    public List<UsfSubtitle> Subtitles { get; init; } = new();
}

/// <summary>
/// Parses USF (Universal Subtitle Format) subtitle files.
/// </summary>
public static class UsfParser
{
    /// <summary>
    /// Parses a USF file from a stream.
    /// </summary>
    public static UsfParseResult Parse(Stream stream, Encoding? encoding = null)
    {
        using var reader = new StreamReader(stream, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd());
    }
    
    /// <summary>
    /// Parses a USF file from text content.
    /// </summary>
    public static UsfParseResult Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new UsfParseResult { Success = false, ErrorMessage = "Empty content" };
        
        try
        {
            var result = new UsfParseResult { Success = true };
            var metadata = new UsfMetadata();
            var styles = new List<UsfStyle>();
            var subtitles = new List<UsfSubtitle>();
            
            var doc = new XmlDocument();
            doc.LoadXml(content);
            
            // Find USF root element
            var root = doc.DocumentElement;
            if (root == null || !string.Equals(root.Name, "USFSubtitles", StringComparison.OrdinalIgnoreCase))
            {
                return new UsfParseResult { Success = false, ErrorMessage = "Not a valid USF file (missing USFSubtitles root)" };
            }
            
            // Parse metadata
            var metadataNode = root.SelectSingleNode("metadata");
            if (metadataNode != null)
            {
                ParseMetadata(metadataNode, metadata);
            }
            
            // Parse styles
            var stylesNode = root.SelectSingleNode("styles");
            if (stylesNode != null)
            {
                ParseStyles(stylesNode, styles);
            }
            
            // Ensure we have a default style
            if (!styles.Any(s => string.Equals(s.Name, "Default", StringComparison.OrdinalIgnoreCase)))
            {
                styles.Insert(0, new UsfStyle { Name = "Default" });
            }
            
            // Parse subtitles
            var subtitlesNode = root.SelectSingleNode("subtitles");
            if (subtitlesNode != null)
            {
                ParseSubtitles(subtitlesNode, subtitles, styles);
            }
            
            return new UsfParseResult
            {
                Success = true,
                Metadata = metadata,
                Styles = styles,
                Subtitles = subtitles
            };
        }
        catch (XmlException ex)
        {
            return new UsfParseResult { Success = false, ErrorMessage = $"XML parsing error: {ex.Message}" };
        }
        catch (Exception ex)
        {
            return new UsfParseResult { Success = false, ErrorMessage = $"Error: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Probes content to check if it's a USF file.
    /// </summary>
    public static bool Probe(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        
        // Check for XML declaration and USFSubtitles root
        var trimmed = content.TrimStart();
        if (!trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("<USFSubtitles", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        
        return content.Contains("<USFSubtitles", StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Probes a stream to check if it contains USF content.
    /// </summary>
    public static bool Probe(Stream stream, Encoding? encoding = null)
    {
        var position = stream.Position;
        try
        {
            using var reader = new StreamReader(stream, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var buffer = new char[1024];
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read > 0)
            {
                var content = new string(buffer, 0, read);
                return Probe(content);
            }
            return false;
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = position;
        }
    }
    
    private static void ParseMetadata(XmlNode metadataNode, UsfMetadata metadata)
    {
        foreach (XmlNode child in metadataNode.ChildNodes)
        {
            switch (child.Name.ToLowerInvariant())
            {
                case "title":
                    metadata.Title = child.InnerText;
                    break;
                case "author":
                    metadata.Author = child.InnerText;
                    break;
                case "language":
                    metadata.Language = child.InnerText;
                    break;
                case "comment":
                    metadata.Comment = child.InnerText;
                    break;
                case "resolution":
                    if (child.Attributes != null)
                    {
                        var x = child.Attributes["x"]?.Value;
                        var y = child.Attributes["y"]?.Value;
                        if (int.TryParse(x, out int width)) metadata.OriginalWidth = width;
                        if (int.TryParse(y, out int height)) metadata.OriginalHeight = height;
                    }
                    break;
            }
        }
    }
    
    private static void ParseStyles(XmlNode stylesNode, List<UsfStyle> styles)
    {
        var styleNodes = stylesNode.SelectNodes("style");
        if (styleNodes == null) return;
        
        foreach (XmlNode styleNode in styleNodes)
        {
            var style = new UsfStyle();
            
            // Get style name
            var nameAttr = styleNode.Attributes?["name"];
            if (nameAttr != null)
                style.Name = nameAttr.Value;
            
            // Parse fontstyle element
            var fontStyleNode = styleNode.SelectSingleNode("fontstyle");
            if (fontStyleNode != null)
            {
                ParseFontStyle(fontStyleNode, style);
            }
            
            // Parse position element
            var positionNode = styleNode.SelectSingleNode("position");
            if (positionNode != null)
            {
                ParsePosition(positionNode, style);
            }
            
            styles.Add(style);
        }
    }
    
    private static void ParseFontStyle(XmlNode fontStyleNode, UsfStyle style)
    {
        var attrs = fontStyleNode.Attributes;
        if (attrs == null) return;
        
        // Font face
        var face = attrs["face"]?.Value;
        if (!string.IsNullOrEmpty(face))
            style.FontName = face;
        
        // Font size
        var size = attrs["size"]?.Value;
        if (!string.IsNullOrEmpty(size))
        {
            if (size.StartsWith('+') || size.StartsWith('-'))
            {
                // Relative size
                if (int.TryParse(size, out int delta))
                {
                    if (delta >= -5 && delta <= 5)
                        style.FontSize += (delta * style.FontSize) / 10;
                    else if (delta < -5)
                        style.FontSize = -delta;
                    else
                        style.FontSize = delta;
                }
            }
            else if (int.TryParse(size, out int absSize))
            {
                style.FontSize = absSize;
            }
        }
        
        // Font color
        var color = attrs["color"]?.Value;
        if (!string.IsNullOrEmpty(color))
            style.FontColor = ParseColor(color);
        
        // Outline color
        var outlineColor = attrs["outline-color"]?.Value ?? attrs["outlinecolor"]?.Value;
        if (!string.IsNullOrEmpty(outlineColor))
            style.OutlineColor = ParseColor(outlineColor);
        
        // Outline thickness
        var outline = attrs["outline"]?.Value ?? attrs["outline-level"]?.Value;
        if (!string.IsNullOrEmpty(outline) && int.TryParse(outline, out int outlineLevel))
            style.OutlineThickness = outlineLevel;
        
        // Shadow
        var shadow = attrs["shadow"]?.Value ?? attrs["shadow-level"]?.Value;
        if (!string.IsNullOrEmpty(shadow) && int.TryParse(shadow, out int shadowLevel))
            style.ShadowOffset = shadowLevel;
        
        // Style flags
        var italic = attrs["italic"]?.Value;
        if (string.Equals(italic, "yes", StringComparison.OrdinalIgnoreCase))
            style.Italic = true;
        
        var weight = attrs["weight"]?.Value;
        if (string.Equals(weight, "bold", StringComparison.OrdinalIgnoreCase))
            style.Bold = true;
        
        var underline = attrs["underline"]?.Value;
        if (string.Equals(underline, "yes", StringComparison.OrdinalIgnoreCase))
            style.Underline = true;
        
        var strikethrough = attrs["strikethrough"]?.Value;
        if (string.Equals(strikethrough, "yes", StringComparison.OrdinalIgnoreCase))
            style.Strikethrough = true;
    }
    
    private static void ParsePosition(XmlNode positionNode, UsfStyle style)
    {
        var attrs = positionNode.Attributes;
        if (attrs == null) return;
        
        // Alignment
        var alignment = attrs["alignment"]?.Value;
        if (!string.IsNullOrEmpty(alignment))
            style.Alignment = ParseAlignment(alignment);
        
        // Margins
        var marginH = attrs["horizontal-margin"]?.Value;
        if (!string.IsNullOrEmpty(marginH))
        {
            var value = marginH.TrimEnd('%');
            if (int.TryParse(value, out int h))
                style.MarginHorizontal = h;
        }
        
        var marginV = attrs["vertical-margin"]?.Value;
        if (!string.IsNullOrEmpty(marginV))
        {
            var value = marginV.TrimEnd('%');
            if (int.TryParse(value, out int v))
                style.MarginVertical = v;
        }
    }
    
    private static void ParseSubtitles(XmlNode subtitlesNode, List<UsfSubtitle> subtitles, List<UsfStyle> styles)
    {
        var subtitleNodes = subtitlesNode.SelectNodes("subtitle");
        if (subtitleNodes == null) return;
        
        foreach (XmlNode subtitleNode in subtitleNodes)
        {
            var attrs = subtitleNode.Attributes;
            if (attrs == null) continue;
            
            // Parse timing
            var startStr = attrs["start"]?.Value;
            var stopStr = attrs["stop"]?.Value ?? attrs["end"]?.Value;
            
            if (string.IsNullOrEmpty(startStr) || string.IsNullOrEmpty(stopStr))
                continue;
            
            if (!TryParseTime(startStr, out var startTime) ||
                !TryParseTime(stopStr, out var endTime))
                continue;
            
            // Parse text content
            var textNode = subtitleNode.SelectSingleNode("text");
            if (textNode == null) continue;
            
            var subtitle = new UsfSubtitle
            {
                StartTime = startTime,
                EndTime = endTime,
                Text = ExtractText(textNode),
                StyleName = textNode.Attributes?["style"]?.Value
            };
            
            // Parse position attributes on text element
            if (textNode.Attributes != null)
            {
                var alignment = textNode.Attributes["alignment"]?.Value;
                if (!string.IsNullOrEmpty(alignment))
                    subtitle.Alignment = ParseAlignment(alignment);
                
                var marginH = textNode.Attributes["horizontal-margin"]?.Value;
                if (!string.IsNullOrEmpty(marginH) && int.TryParse(marginH.TrimEnd('%'), out int h))
                    subtitle.MarginHorizontal = h;
                
                var marginV = textNode.Attributes["vertical-margin"]?.Value;
                if (!string.IsNullOrEmpty(marginV) && int.TryParse(marginV.TrimEnd('%'), out int v))
                    subtitle.MarginVertical = v;
            }
            
            // Parse karaoke segments if present
            var karaokeNodes = textNode.SelectNodes("karaoke");
            if (karaokeNodes != null && karaokeNodes.Count > 0)
            {
                subtitle.KaraokeSegments = new List<UsfKaraokeSegment>();
                foreach (XmlNode karaokeNode in karaokeNodes)
                {
                    var kStart = karaokeNode.Attributes?["start"]?.Value;
                    var kStop = karaokeNode.Attributes?["stop"]?.Value;
                    
                    if (TryParseTime(kStart, out var kStartTime) &&
                        TryParseTime(kStop, out var kEndTime))
                    {
                        subtitle.KaraokeSegments.Add(new UsfKaraokeSegment
                        {
                            Start = kStartTime,
                            End = kEndTime,
                            Text = karaokeNode.InnerText
                        });
                    }
                }
            }
            
            subtitles.Add(subtitle);
        }
    }
    
    private static string ExtractText(XmlNode textNode)
    {
        var sb = new StringBuilder();
        ExtractTextRecursive(textNode, sb);
        return sb.ToString().Trim();
    }
    
    private static void ExtractTextRecursive(XmlNode node, StringBuilder sb)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            switch (child.NodeType)
            {
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    sb.Append(child.Value);
                    break;
                case XmlNodeType.Element:
                    // Handle formatting elements
                    switch (child.Name.ToLowerInvariant())
                    {
                        case "br":
                            sb.Append('\n');
                            break;
                        case "karaoke":
                            // Skip karaoke elements in plain text (handled separately)
                            sb.Append(child.InnerText);
                            break;
                        default:
                            // Recursively extract from other elements (b, i, u, font, etc.)
                            ExtractTextRecursive(child, sb);
                            break;
                    }
                    break;
            }
        }
    }
    
    private static bool TryParseTime(string? timeStr, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrEmpty(timeStr))
            return false;
        
        // Format: HH:MM:SS.mmm or HH:MM:SS,mmm
        timeStr = timeStr.Replace(',', '.');
        
        var parts = timeStr.Split(':');
        if (parts.Length != 3)
            return false;
        
        if (!int.TryParse(parts[0], out int hours))
            return false;
        if (!int.TryParse(parts[1], out int minutes))
            return false;
        
        // Parse seconds and milliseconds
        var secondsParts = parts[2].Split('.');
        if (!int.TryParse(secondsParts[0], out int seconds))
            return false;
        
        int milliseconds = 0;
        if (secondsParts.Length > 1)
        {
            var msStr = secondsParts[1].PadRight(3, '0');
            if (msStr.Length > 3) msStr = msStr[..3];
            int.TryParse(msStr, out milliseconds);
        }
        
        result = new TimeSpan(0, hours, minutes, seconds, milliseconds);
        return true;
    }
    
    private static UsfAlignment ParseAlignment(string alignment)
    {
        return alignment.ToLowerInvariant() switch
        {
            "topleft" => UsfAlignment.TopLeft,
            "topcenter" => UsfAlignment.TopCenter,
            "topright" => UsfAlignment.TopRight,
            "middleleft" => UsfAlignment.MiddleLeft,
            "middlecenter" => UsfAlignment.MiddleCenter,
            "middleright" => UsfAlignment.MiddleRight,
            "bottomleft" => UsfAlignment.BottomLeft,
            "bottomcenter" => UsfAlignment.BottomCenter,
            "bottomright" => UsfAlignment.BottomRight,
            _ => UsfAlignment.BottomCenter
        };
    }
    
    private static Color ParseColor(string colorStr)
    {
        if (string.IsNullOrEmpty(colorStr))
            return Color.White;
        
        // Handle #RRGGBB or #AARRGGBB format
        if (colorStr.StartsWith('#'))
        {
            colorStr = colorStr[1..];
            
            if (colorStr.Length == 6)
            {
                if (int.TryParse(colorStr, System.Globalization.NumberStyles.HexNumber, null, out int rgb))
                {
                    return Color.FromArgb(
                        255,
                        (rgb >> 16) & 0xFF,
                        (rgb >> 8) & 0xFF,
                        rgb & 0xFF);
                }
            }
            else if (colorStr.Length == 8)
            {
                if (int.TryParse(colorStr, System.Globalization.NumberStyles.HexNumber, null, out int argb))
                {
                    return Color.FromArgb(argb);
                }
            }
        }
        
        // Try named colors
        try
        {
            return Color.FromName(colorStr);
        }
        catch
        {
            return Color.White;
        }
    }
    
    /// <summary>
    /// Converts USF parse result to a SubtitleTrack.
    /// </summary>
    public static SubtitleTrack ToSubtitleTrack(UsfParseResult result)
    {
        var track = new SubtitleTrack
        {
            Format = SubtitleFormat.Usf,
            Title = result.Metadata.Title,
            Language = result.Metadata.Language
        };
        
        foreach (var sub in result.Subtitles)
        {
            long startUs = (long)(sub.StartTime.TotalSeconds * 1_000_000);
            long endUs = (long)(sub.EndTime.TotalSeconds * 1_000_000);
            
            var cue = new SubtitleCue(
                startUs,
                endUs,
                sub.Text,
                SubtitleType.StyledText,
                sub.StyleName);
            
            track.AddCue(cue);
        }
        
        return track;
    }
}

// TTML (Timed Text Markup Language) subtitle parser
// Supports TTML used in DASH streaming, Netflix, Disney+, and other services

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Parser for TTML (Timed Text Markup Language) subtitle format.
/// Supports IMSC1 profile commonly used in streaming services.
/// </summary>
public sealed class TtmlParser
{
    // TTML namespace URIs
    private const string TtmlNamespace = "http://www.w3.org/ns/ttml";
    private const string TtmlStylingNamespace = "http://www.w3.org/ns/ttml#styling";
    private const string TtmlParameterNamespace = "http://www.w3.org/ns/ttml#parameter";
    private const string SmpteNamespace = "http://www.smpte-ra.org/schemas/2052-1/2010/smpte-tt";

    // Time parsing patterns
    private static readonly Regex ClockTimePattern = new(
        @"^(\d+):(\d{2}):(\d{2})(?:\.(\d+)|:(\d+))?$",
        RegexOptions.Compiled);

    private static readonly Regex OffsetTimePattern = new(
        @"^(\d+(?:\.\d+)?)(h|m|s|ms|f|t)$",
        RegexOptions.Compiled);

    // Default frame rate for frame-based timing
    private int frameRate = 30;
    private int subFrameRate = 1;
    private int tickRate = 1;

    /// <summary>
    /// Parses a TTML subtitle file from a stream.
    /// </summary>
    public TtmlDocument Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = false,
            DtdProcessing = DtdProcessing.Ignore
        });

        return ParseDocument(reader);
    }

    /// <summary>
    /// Parses a TTML subtitle file from a string.
    /// </summary>
    public TtmlDocument Parse(string ttmlContent)
    {
        ArgumentNullException.ThrowIfNull(ttmlContent);

        using var stringReader = new StringReader(ttmlContent);
        using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = false,
            DtdProcessing = DtdProcessing.Ignore
        });

        return ParseDocument(xmlReader);
    }

    private TtmlDocument ParseDocument(XmlReader reader)
    {
        var document = new TtmlDocument();
        var styles = new Dictionary<string, TtmlStyle>();
        var regions = new Dictionary<string, TtmlRegion>();

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            string localName = reader.LocalName;

            // Parse root <tt> element for timing parameters
            if (localName == "tt")
            {
                ParseTimingParameters(reader);
                document.Language = reader.GetAttribute("xml:lang") ?? "";
            }
            // Parse <head> section for styles and regions
            else if (localName == "head")
            {
                ParseHead(reader, styles, regions);
            }
            // Parse <body> section for content
            else if (localName == "body")
            {
                ParseBody(reader, document, styles, regions);
            }
        }

        document.Styles = styles;
        document.Regions = regions;

        return document;
    }

    private void ParseTimingParameters(XmlReader reader)
    {
        // ttp:frameRate
        string? frameRateStr = reader.GetAttribute("frameRate", TtmlParameterNamespace);
        if (!string.IsNullOrEmpty(frameRateStr) && int.TryParse(frameRateStr, out int fr))
        {
            frameRate = fr;
        }

        // ttp:subFrameRate
        string? subFrameRateStr = reader.GetAttribute("subFrameRate", TtmlParameterNamespace);
        if (!string.IsNullOrEmpty(subFrameRateStr) && int.TryParse(subFrameRateStr, out int sfr))
        {
            subFrameRate = sfr;
        }

        // ttp:tickRate
        string? tickRateStr = reader.GetAttribute("tickRate", TtmlParameterNamespace);
        if (!string.IsNullOrEmpty(tickRateStr) && int.TryParse(tickRateStr, out int tr))
        {
            tickRate = tr;
        }
    }

    private void ParseHead(XmlReader reader, Dictionary<string, TtmlStyle> styles, Dictionary<string, TtmlRegion> regions)
    {
        int depth = reader.Depth;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth <= depth)
                break;

            if (reader.NodeType != XmlNodeType.Element)
                continue;

            if (reader.LocalName == "style")
            {
                var style = ParseStyle(reader);
                if (!string.IsNullOrEmpty(style.Id))
                {
                    styles[style.Id] = style;
                }
            }
            else if (reader.LocalName == "region")
            {
                var region = ParseRegion(reader);
                if (!string.IsNullOrEmpty(region.Id))
                {
                    regions[region.Id] = region;
                }
            }
        }
    }

    private TtmlStyle ParseStyle(XmlReader reader)
    {
        var style = new TtmlStyle
        {
            Id = reader.GetAttribute("id", "http://www.w3.org/XML/1998/namespace") 
                 ?? reader.GetAttribute("id") ?? ""
        };

        // Parse styling attributes
        style.FontFamily = reader.GetAttribute("fontFamily", TtmlStylingNamespace) ?? "";
        style.FontSize = reader.GetAttribute("fontSize", TtmlStylingNamespace) ?? "";
        style.FontStyle = reader.GetAttribute("fontStyle", TtmlStylingNamespace) ?? "";
        style.FontWeight = reader.GetAttribute("fontWeight", TtmlStylingNamespace) ?? "";
        style.Color = reader.GetAttribute("color", TtmlStylingNamespace) ?? "";
        style.BackgroundColor = reader.GetAttribute("backgroundColor", TtmlStylingNamespace) ?? "";
        style.TextAlign = reader.GetAttribute("textAlign", TtmlStylingNamespace) ?? "";
        style.TextOutline = reader.GetAttribute("textOutline", TtmlStylingNamespace) ?? "";
        style.WrapOption = reader.GetAttribute("wrapOption", TtmlStylingNamespace) ?? "";

        return style;
    }

    private TtmlRegion ParseRegion(XmlReader reader)
    {
        var region = new TtmlRegion
        {
            Id = reader.GetAttribute("id", "http://www.w3.org/XML/1998/namespace")
                 ?? reader.GetAttribute("id") ?? ""
        };

        // Parse region attributes
        region.Origin = reader.GetAttribute("origin", TtmlStylingNamespace) ?? "";
        region.Extent = reader.GetAttribute("extent", TtmlStylingNamespace) ?? "";
        region.DisplayAlign = reader.GetAttribute("displayAlign", TtmlStylingNamespace) ?? "";
        region.Overflow = reader.GetAttribute("overflow", TtmlStylingNamespace) ?? "";

        return region;
    }

    private void ParseBody(XmlReader reader, TtmlDocument document, 
        Dictionary<string, TtmlStyle> styles, Dictionary<string, TtmlRegion> regions)
    {
        int depth = reader.Depth;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth <= depth)
                break;

            if (reader.NodeType != XmlNodeType.Element)
                continue;

            if (reader.LocalName == "div")
            {
                ParseDiv(reader, document, styles, regions);
            }
            else if (reader.LocalName == "p")
            {
                var cue = ParseParagraph(reader, styles, regions);
                if (cue != null)
                {
                    document.Cues.Add(cue);
                }
            }
        }
    }

    private void ParseDiv(XmlReader reader, TtmlDocument document,
        Dictionary<string, TtmlStyle> styles, Dictionary<string, TtmlRegion> regions)
    {
        int depth = reader.Depth;

        // Div-level timing (inherited by child paragraphs)
        TimeSpan? divBegin = ParseTimeAttribute(reader.GetAttribute("begin"));
        TimeSpan? divEnd = ParseTimeAttribute(reader.GetAttribute("end"));

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth <= depth)
                break;

            if (reader.NodeType != XmlNodeType.Element)
                continue;

            if (reader.LocalName == "p")
            {
                var cue = ParseParagraph(reader, styles, regions, divBegin, divEnd);
                if (cue != null)
                {
                    document.Cues.Add(cue);
                }
            }
        }
    }

    private TtmlCue? ParseParagraph(XmlReader reader, 
        Dictionary<string, TtmlStyle> styles, 
        Dictionary<string, TtmlRegion> regions,
        TimeSpan? parentBegin = null,
        TimeSpan? parentEnd = null)
    {
        // Parse timing
        TimeSpan? begin = ParseTimeAttribute(reader.GetAttribute("begin")) ?? parentBegin;
        TimeSpan? end = ParseTimeAttribute(reader.GetAttribute("end")) ?? parentEnd;
        TimeSpan? duration = ParseTimeAttribute(reader.GetAttribute("dur"));

        if (begin == null)
            return null;

        // Calculate end time from duration if not specified
        if (end == null && duration != null)
        {
            end = begin.Value + duration.Value;
        }

        if (end == null)
            return null;

        // Apply parent offset
        if (parentBegin != null)
        {
            begin = parentBegin.Value + begin.Value;
            end = parentBegin.Value + end.Value;
        }

        var cue = new TtmlCue
        {
            StartTime = begin.Value,
            EndTime = end.Value,
            RegionId = reader.GetAttribute("region") ?? "",
            StyleId = reader.GetAttribute("style") ?? ""
        };

        // Read paragraph content
        var textBuilder = new StringBuilder();
        int depth = reader.Depth;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth <= depth)
                break;

            switch (reader.NodeType)
            {
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    textBuilder.Append(reader.Value);
                    break;

                case XmlNodeType.Element:
                    if (reader.LocalName == "br")
                    {
                        textBuilder.Append('\n');
                    }
                    else if (reader.LocalName == "span")
                    {
                        // Read span content
                        if (!reader.IsEmptyElement)
                        {
                            string spanText = reader.ReadInnerXml();
                            textBuilder.Append(CleanupText(spanText));
                        }
                    }
                    break;
            }
        }

        cue.Text = CleanupText(textBuilder.ToString());

        // Apply style if referenced
        if (!string.IsNullOrEmpty(cue.StyleId) && styles.TryGetValue(cue.StyleId, out var style))
        {
            cue.AppliedStyle = style;
        }

        // Apply region if referenced
        if (!string.IsNullOrEmpty(cue.RegionId) && regions.TryGetValue(cue.RegionId, out var region))
        {
            cue.AppliedRegion = region;
        }

        return cue;
    }

    private TimeSpan? ParseTimeAttribute(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Try clock time format: HH:MM:SS.mmm or HH:MM:SS:frames
        var clockMatch = ClockTimePattern.Match(value);
        if (clockMatch.Success)
        {
            int hours = int.Parse(clockMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            int minutes = int.Parse(clockMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            int seconds = int.Parse(clockMatch.Groups[3].Value, CultureInfo.InvariantCulture);

            double fractionalSeconds = 0;

            // Milliseconds fraction
            if (clockMatch.Groups[4].Success)
            {
                string fraction = clockMatch.Groups[4].Value;
                fractionalSeconds = double.Parse("0." + fraction, CultureInfo.InvariantCulture);
            }
            // Frame count
            else if (clockMatch.Groups[5].Success)
            {
                int frames = int.Parse(clockMatch.Groups[5].Value, CultureInfo.InvariantCulture);
                fractionalSeconds = (double)frames / frameRate;
            }

            return new TimeSpan(0, hours, minutes, seconds, (int)(fractionalSeconds * 1000));
        }

        // Try offset time format: 10s, 100ms, 5.5h, etc.
        var offsetMatch = OffsetTimePattern.Match(value);
        if (offsetMatch.Success)
        {
            double amount = double.Parse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            string unit = offsetMatch.Groups[2].Value;

            double seconds = unit switch
            {
                "h" => amount * 3600,
                "m" => amount * 60,
                "s" => amount,
                "ms" => amount / 1000,
                "f" => amount / frameRate,
                "t" => amount / tickRate,
                _ => amount
            };

            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    private static string CleanupText(string text)
    {
        // Remove XML tags that might remain
        text = Regex.Replace(text, @"<[^>]+>", "");

        // Normalize whitespace
        text = Regex.Replace(text, @"[\r\n]+", "\n");
        text = Regex.Replace(text, @"[ \t]+", " ");

        return text.Trim();
    }

    /// <summary>
    /// Converts TTML document to SubtitleCue list for rendering.
    /// </summary>
    public static List<SubtitleCue> ToSubtitleCues(TtmlDocument document)
    {
        var cues = new List<SubtitleCue>();

        foreach (var ttmlCue in document.Cues)
        {
            long startMicroseconds = (long)(ttmlCue.StartTime.TotalMilliseconds * 1000);
            long endMicroseconds = (long)(ttmlCue.EndTime.TotalMilliseconds * 1000);

            // Determine position from region
            float horizontalPercent = 50f;
            float verticalPercent = 95f;
            var horizontalAlignment = SubtitleAlignment.Center;
            var verticalAlignment = SubtitleVerticalAlignment.Bottom;

            if (ttmlCue.AppliedRegion != null && !string.IsNullOrEmpty(ttmlCue.AppliedRegion.Origin))
            {
                (double x, double y) = ParsePercentagePosition(ttmlCue.AppliedRegion.Origin);
                horizontalPercent = (float)(x * 100);
                verticalPercent = (float)(y * 100);
            }

            var position = new SubtitlePosition(
                horizontalPercent,
                verticalPercent,
                horizontalAlignment,
                verticalAlignment);

            var cue = new SubtitleCue(
                startMicroseconds,
                endMicroseconds,
                ttmlCue.Text,
                SubtitleType.StyledText,
                ttmlCue.StyleId,
                position);

            cues.Add(cue);
        }

        return cues;
    }

    /// <summary>
    /// Parses a TTML color value to ARGB format.
    /// </summary>
    public static uint ParseColorToArgb(string colorValue)
    {
        if (string.IsNullOrEmpty(colorValue))
            return 0xFFFFFFFF; // White

        // Named colors
        if (colorValue.Equals("white", StringComparison.OrdinalIgnoreCase))
            return 0xFFFFFFFF;
        if (colorValue.Equals("black", StringComparison.OrdinalIgnoreCase))
            return 0xFF000000;
        if (colorValue.Equals("red", StringComparison.OrdinalIgnoreCase))
            return 0xFFFF0000;
        if (colorValue.Equals("green", StringComparison.OrdinalIgnoreCase))
            return 0xFF008000;
        if (colorValue.Equals("blue", StringComparison.OrdinalIgnoreCase))
            return 0xFF0000FF;
        if (colorValue.Equals("yellow", StringComparison.OrdinalIgnoreCase))
            return 0xFFFFFF00;
        if (colorValue.Equals("cyan", StringComparison.OrdinalIgnoreCase))
            return 0xFF00FFFF;
        if (colorValue.Equals("magenta", StringComparison.OrdinalIgnoreCase))
            return 0xFFFF00FF;
        if (colorValue.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return 0x00000000;

        // #RRGGBB or #RRGGBBAA
        if (colorValue.StartsWith('#'))
        {
            string hex = colorValue[1..];
            if (hex.Length >= 6)
            {
                byte r = Convert.ToByte(hex[0..2], 16);
                byte g = Convert.ToByte(hex[2..4], 16);
                byte b = Convert.ToByte(hex[4..6], 16);
                byte a = hex.Length >= 8 ? Convert.ToByte(hex[6..8], 16) : (byte)255;
                return (uint)((a << 24) | (r << 16) | (g << 8) | b);
            }
        }

        // rgba(r,g,b,a) format
        if (colorValue.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(colorValue, @"rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([\d.]+)\s*\)");
            if (match.Success)
            {
                byte r = byte.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                byte g = byte.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                byte b = byte.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                byte a = (byte)(double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) * 255);
                return (uint)((a << 24) | (r << 16) | (g << 8) | b);
            }
        }

        return 0xFFFFFFFF; // Default white
    }

    private static (double x, double y) ParsePercentagePosition(string origin)
    {
        // Format: "10% 80%" or "10% 80%"
        var parts = origin.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            double x = ParsePercentage(parts[0]);
            double y = ParsePercentage(parts[1]);
            return (x, y);
        }
        return (0.5, 0.9); // Default: center, near bottom
    }

    private static double ParsePercentage(string value)
    {
        value = value.Trim();
        if (value.EndsWith('%'))
        {
            value = value[..^1];
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
            {
                return pct / 100.0;
            }
        }
        return 0;
    }
}

/// <summary>
/// Represents a parsed TTML document.
/// </summary>
public sealed class TtmlDocument
{
    /// <summary>Language of the document.</summary>
    public string Language { get; set; } = "";

    /// <summary>All parsed subtitle cues.</summary>
    public List<TtmlCue> Cues { get; } = new();

    /// <summary>Named styles defined in the document.</summary>
    public Dictionary<string, TtmlStyle> Styles { get; set; } = new();

    /// <summary>Named regions defined in the document.</summary>
    public Dictionary<string, TtmlRegion> Regions { get; set; } = new();
}

/// <summary>
/// A single TTML subtitle cue (paragraph element).
/// </summary>
public sealed class TtmlCue
{
    /// <summary>Start time of the cue.</summary>
    public TimeSpan StartTime { get; set; }

    /// <summary>End time of the cue.</summary>
    public TimeSpan EndTime { get; set; }

    /// <summary>Text content of the cue.</summary>
    public string Text { get; set; } = "";

    /// <summary>ID of the referenced style.</summary>
    public string StyleId { get; set; } = "";

    /// <summary>ID of the referenced region.</summary>
    public string RegionId { get; set; } = "";

    /// <summary>Resolved style, if any.</summary>
    public TtmlStyle? AppliedStyle { get; set; }

    /// <summary>Resolved region, if any.</summary>
    public TtmlRegion? AppliedRegion { get; set; }
}

/// <summary>
/// TTML style definition.
/// </summary>
public sealed class TtmlStyle
{
    public string Id { get; set; } = "";
    public string FontFamily { get; set; } = "";
    public string FontSize { get; set; } = "";
    public string FontStyle { get; set; } = "";
    public string FontWeight { get; set; } = "";
    public string Color { get; set; } = "";
    public string BackgroundColor { get; set; } = "";
    public string TextAlign { get; set; } = "";
    public string TextOutline { get; set; } = "";
    public string WrapOption { get; set; } = "";
}

/// <summary>
/// TTML region definition for positioning.
/// </summary>
public sealed class TtmlRegion
{
    public string Id { get; set; } = "";
    public string Origin { get; set; } = "";
    public string Extent { get; set; } = "";
    public string DisplayAlign { get; set; } = "";
    public string Overflow { get; set; } = "";
}

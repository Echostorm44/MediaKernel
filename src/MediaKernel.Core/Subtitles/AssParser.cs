// ASS/SSA Subtitle Parser
// Advanced SubStation Alpha format with full style support

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Parser for ASS (Advanced SubStation Alpha) and SSA (SubStation Alpha) subtitle files.
/// </summary>
/// <remarks>
/// ASS Format:
/// [Script Info]
/// Title: Example
/// PlayResX: 1920
/// PlayResY: 1080
/// 
/// [V4+ Styles]
/// Format: Name, Fontname, Fontsize, ...
/// Style: Default,Arial,48,&amp;H00FFFFFF,...
/// 
/// [Events]
/// Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
/// Dialogue: 0,0:00:01.00,0:00:04.00,Default,,0,0,0,,First subtitle
/// </remarks>
public static class AssParser
{
    private const string ScriptInfoSection = "[Script Info]";
    private const string StylesSection = "[V4+ Styles]";
    private const string StylesSectionLegacy = "[V4 Styles]";
    private const string EventsSection = "[Events]";
    
    /// <summary>
    /// Parses an ASS/SSA file from text content.
    /// </summary>
    public static SubtitleTrack Parse(string content)
    {
        var track = new SubtitleTrack
        {
            Format = SubtitleFormat.Ass
        };
        
        if (string.IsNullOrEmpty(content))
            return track;
        
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        
        string currentSection = string.Empty;
        string[]? styleFormat = null;
        string[]? eventFormat = null;
        int playResX = 0;
        int playResY = 0;
        string title = string.Empty;
        
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("!:"))
                continue; // Comment or empty
            
            // Check for section headers
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line;
                continue;
            }
            
            // Parse based on current section
            switch (currentSection)
            {
                case ScriptInfoSection:
                    ParseScriptInfo(line, ref title, ref playResX, ref playResY);
                    break;
                
                case StylesSection:
                case StylesSectionLegacy:
                    if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                    {
                        styleFormat = ParseFormat(line);
                    }
                    else if (line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase) && styleFormat != null)
                    {
                        if (TryParseStyle(line, styleFormat, out var style))
                        {
                            track.AddStyle(style);
                        }
                    }
                    break;
                
                case EventsSection:
                    if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                    {
                        eventFormat = ParseFormat(line);
                    }
                    else if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) && eventFormat != null)
                    {
                        if (TryParseDialogue(line, eventFormat, out var cue))
                        {
                            track.AddCue(cue);
                        }
                    }
                    break;
            }
        }
        
        // Update track metadata
        track.Title = title;
        track.PlayResolution = (playResX, playResY);
        
        return track;
    }
    
    /// <summary>
    /// Parses an ASS/SSA file from a stream.
    /// </summary>
    public static SubtitleTrack Parse(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
    
    /// <summary>
    /// Checks if content appears to be ASS/SSA format.
    /// </summary>
    public static bool IsAss(ReadOnlySpan<char> content)
    {
        return content.Contains(ScriptInfoSection.AsSpan(), StringComparison.OrdinalIgnoreCase) ||
               content.Contains(StylesSection.AsSpan(), StringComparison.OrdinalIgnoreCase) ||
               content.Contains(EventsSection.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }
    
    private static void ParseScriptInfo(string line, ref string title, ref int playResX, ref int playResY)
    {
        int colonPos = line.IndexOf(':');
        if (colonPos < 0)
            return;
        
        var key = line[..colonPos].Trim();
        var value = line[(colonPos + 1)..].Trim();
        
        switch (key.ToLowerInvariant())
        {
            case "title":
                title = value;
                break;
            case "playresx":
                int.TryParse(value, out playResX);
                break;
            case "playresy":
                int.TryParse(value, out playResY);
                break;
        }
    }
    
    private static string[] ParseFormat(string line)
    {
        int colonPos = line.IndexOf(':');
        if (colonPos < 0)
            return [];
        
        var formatPart = line[(colonPos + 1)..];
        return formatPart.Split(',', StringSplitOptions.TrimEntries);
    }
    
    private static bool TryParseStyle(string line, string[] format, out SubtitleStyle style)
    {
        style = new SubtitleStyle();
        
        int colonPos = line.IndexOf(':');
        if (colonPos < 0)
            return false;
        
        var values = line[(colonPos + 1)..].Split(',', StringSplitOptions.TrimEntries);
        
        if (values.Length < format.Length)
            return false;
        
        // Build style from format/value pairs
        string name = "Default";
        string fontName = "Arial";
        float fontSize = 48;
        uint primaryColor = 0xFFFFFFFF;
        uint secondaryColor = 0xFF00FFFF;
        uint outlineColor = 0xFF000000;
        uint backColor = 0x80000000;
        bool bold = false;
        bool italic = false;
        bool underline = false;
        bool strikeout = false;
        float outlineWidth = 2;
        float shadowDepth = 2;
        int marginL = 10;
        int marginR = 10;
        int marginV = 10;
        int alignment = 2;
        int borderStyle = 1;
        
        for (int i = 0; i < format.Length && i < values.Length; i++)
        {
            var fieldName = format[i].ToLowerInvariant();
            var fieldValue = values[i];
            
            switch (fieldName)
            {
                case "name":
                    name = fieldValue;
                    break;
                case "fontname":
                    fontName = fieldValue;
                    break;
                case "fontsize":
                    float.TryParse(fieldValue, out fontSize);
                    break;
                case "primarycolour":
                    primaryColor = ParseAssColor(fieldValue);
                    break;
                case "secondarycolour":
                    secondaryColor = ParseAssColor(fieldValue);
                    break;
                case "outlinecolour":
                case "tertiarycolour":
                    outlineColor = ParseAssColor(fieldValue);
                    break;
                case "backcolour":
                    backColor = ParseAssColor(fieldValue);
                    break;
                case "bold":
                    bold = fieldValue == "-1" || fieldValue == "1";
                    break;
                case "italic":
                    italic = fieldValue == "-1" || fieldValue == "1";
                    break;
                case "underline":
                    underline = fieldValue == "-1" || fieldValue == "1";
                    break;
                case "strikeout":
                    strikeout = fieldValue == "-1" || fieldValue == "1";
                    break;
                case "outline":
                    float.TryParse(fieldValue, out outlineWidth);
                    break;
                case "shadow":
                    float.TryParse(fieldValue, out shadowDepth);
                    break;
                case "marginl":
                    int.TryParse(fieldValue, out marginL);
                    break;
                case "marginr":
                    int.TryParse(fieldValue, out marginR);
                    break;
                case "marginv":
                    int.TryParse(fieldValue, out marginV);
                    break;
                case "alignment":
                    int.TryParse(fieldValue, out alignment);
                    break;
                case "borderstyle":
                    int.TryParse(fieldValue, out borderStyle);
                    break;
            }
        }
        
        style = new SubtitleStyle
        {
            Name = name,
            FontName = fontName,
            FontSize = fontSize,
            PrimaryColor = primaryColor,
            SecondaryColor = secondaryColor,
            OutlineColor = outlineColor,
            BackColor = backColor,
            Bold = bold,
            Italic = italic,
            Underline = underline,
            Strikeout = strikeout,
            OutlineWidth = outlineWidth,
            ShadowDepth = shadowDepth,
            MarginLeft = marginL,
            MarginRight = marginR,
            MarginVertical = marginV,
            Alignment = alignment,
            BorderStyle = borderStyle
        };
        
        return true;
    }
    
    private static bool TryParseDialogue(string line, string[] format, out SubtitleCue cue)
    {
        cue = default;
        
        int colonPos = line.IndexOf(':');
        if (colonPos < 0)
            return false;
        
        // Split by comma, but the Text field (last) may contain commas
        var afterColon = line[(colonPos + 1)..];
        var values = new List<string>();
        
        // Split first N-1 fields normally, take remainder as text
        int textFieldIndex = Array.FindIndex(format, f => 
            f.Equals("Text", StringComparison.OrdinalIgnoreCase));
        
        if (textFieldIndex < 0)
            textFieldIndex = format.Length - 1;
        
        var parts = afterColon.Split(',');
        
        for (int i = 0; i < textFieldIndex && i < parts.Length; i++)
        {
            values.Add(parts[i].Trim());
        }
        
        // Join remaining parts as text
        if (parts.Length > textFieldIndex)
        {
            var textParts = parts.AsSpan()[textFieldIndex..];
            values.Add(string.Join(",", textParts.ToArray()).Trim());
        }
        
        // Extract fields
        string? styleName = null;
        long startUs = 0;
        long endUs = 0;
        string text = string.Empty;
        
        for (int i = 0; i < format.Length && i < values.Count; i++)
        {
            var fieldName = format[i].ToLowerInvariant();
            var fieldValue = values[i];
            
            switch (fieldName)
            {
                case "start":
                    SubtitleTimestamp.TryParseAss(fieldValue.AsSpan(), out startUs);
                    break;
                case "end":
                    SubtitleTimestamp.TryParseAss(fieldValue.AsSpan(), out endUs);
                    break;
                case "style":
                    styleName = fieldValue;
                    break;
                case "text":
                    text = ProcessAssText(fieldValue);
                    break;
            }
        }
        
        cue = new SubtitleCue(
            startUs,
            endUs,
            text,
            SubtitleType.StyledText,
            styleName);
        
        return true;
    }
    
    /// <summary>
    /// Processes ASS text, handling override tags and special characters.
    /// </summary>
    private static string ProcessAssText(string text)
    {
        // Replace \N and \n with newlines
        text = text.Replace("\\N", "\n").Replace("\\n", "\n");
        
        // Replace \h with non-breaking space
        text = text.Replace("\\h", "\u00A0");
        
        // Strip override tags {...}
        var result = new System.Text.StringBuilder(text.Length);
        bool inTag = false;
        
        foreach (char c in text)
        {
            if (c == '{')
            {
                inTag = true;
                continue;
            }
            
            if (c == '}')
            {
                inTag = false;
                continue;
            }
            
            if (!inTag)
            {
                result.Append(c);
            }
        }
        
        return result.ToString();
    }
    
    /// <summary>
    /// Parses ASS color format: &amp;HAABBGGRR or &amp;HBBGGRR or decimal.
    /// </summary>
    private static uint ParseAssColor(string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0xFFFFFFFF;
        
        // Remove &H prefix if present
        if (value.StartsWith("&H", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        
        // Remove trailing & if present
        if (value.EndsWith('&'))
            value = value[..^1];
        
        // Try parse as hex
        if (uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out uint colorBgr))
        {
            // ASS uses AABBGGRR format, convert to AARRGGBB
            uint a = (colorBgr >> 24) & 0xFF;
            uint b = (colorBgr >> 16) & 0xFF;
            uint g = (colorBgr >> 8) & 0xFF;
            uint r = colorBgr & 0xFF;
            
            // Invert alpha (ASS: 00=opaque, FF=transparent)
            a = 255 - a;
            
            return (a << 24) | (r << 16) | (g << 8) | b;
        }
        
        // Try parse as decimal (some older formats)
        if (uint.TryParse(value, out uint decimalColor))
        {
            return decimalColor | 0xFF000000; // Assume fully opaque
        }
        
        return 0xFFFFFFFF;
    }
}

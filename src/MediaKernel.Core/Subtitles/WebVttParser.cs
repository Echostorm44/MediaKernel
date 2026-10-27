// WebVTT Subtitle Parser
// Supports cue settings, voice tags, and positioning

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Parser for WebVTT (Web Video Text Tracks) subtitle files.
/// </summary>
/// <remarks>
/// WebVTT Format:
/// WEBVTT
/// 
/// 00:00:01.000 --> 00:00:04.000
/// First subtitle text
/// 
/// cue-id
/// 00:00:05.000 --> 00:00:08.000 position:50% align:center
/// Second subtitle with &lt;v Speaker&gt;voice tags&lt;/v&gt;
/// </remarks>
public static class WebVttParser
{
    private const string WebVttHeader = "WEBVTT";
    private const string TimeSeparator = " --> ";
    
    /// <summary>
    /// Parses a WebVTT file from text content.
    /// </summary>
    public static SubtitleTrack Parse(string content)
    {
        var track = new SubtitleTrack
        {
            Format = SubtitleFormat.WebVtt
        };
        
        if (string.IsNullOrEmpty(content))
            return track;
        
        var rawLines = content.Split(['\r', '\n'], StringSplitOptions.None);
        var lines = new string[rawLines.Length];
        for (int i = 0; i < rawLines.Length; i++)
            lines[i] = rawLines[i].Trim();
        
        int lineIndex = 0;
        
        // Validate WebVTT header
        while (lineIndex < lines.Length && string.IsNullOrEmpty(lines[lineIndex]))
            lineIndex++;
        
        if (lineIndex >= lines.Length)
            return track;
        
        // Check for WEBVTT header
        if (!lines[lineIndex].StartsWith(WebVttHeader, StringComparison.Ordinal))
            return track;
        
        lineIndex++;
        
        // Skip header metadata (until first blank line)
        while (lineIndex < lines.Length && !string.IsNullOrEmpty(lines[lineIndex]))
            lineIndex++;
        
        // Parse cues
        while (lineIndex < lines.Length)
        {
            // Skip blank lines
            while (lineIndex < lines.Length && string.IsNullOrEmpty(lines[lineIndex]))
                lineIndex++;
            
            if (lineIndex >= lines.Length)
                break;
            
            // Try to parse a cue
            if (TryParseCue(lines, ref lineIndex, out var cue))
            {
                track.AddCue(cue);
            }
        }
        
        return track;
    }
    
    /// <summary>
    /// Parses a WebVTT file from a stream.
    /// </summary>
    public static SubtitleTrack Parse(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
    
    /// <summary>
    /// Checks if content appears to be WebVTT format.
    /// </summary>
    public static bool IsWebVtt(ReadOnlySpan<char> content)
    {
        content = content.TrimStart();
        return content.StartsWith(WebVttHeader.AsSpan(), StringComparison.Ordinal);
    }
    
    private static bool TryParseCue(string[] lines, ref int lineIndex, out SubtitleCue cue)
    {
        cue = default;
        
        if (lineIndex >= lines.Length)
            return false;
        
        var firstLine = lines[lineIndex].AsSpan();
        
        // Check if this is a cue identifier or timing line
        int arrowPos = firstLine.IndexOf(TimeSeparator.AsSpan());
        string? cueId = null;
        
        if (arrowPos < 0)
        {
            // This is a cue identifier, next line should be timing
            cueId = lines[lineIndex];
            lineIndex++;
            
            if (lineIndex >= lines.Length)
                return false;
            
            firstLine = lines[lineIndex].AsSpan();
            arrowPos = firstLine.IndexOf(TimeSeparator.AsSpan());
            
            if (arrowPos < 0)
                return false;
        }
        
        // Parse timing line
        if (!TryParseTiming(firstLine, out long startUs, out long endUs, out var position))
            return false;
        
        lineIndex++;
        
        // Collect text lines until blank line or end
        var textBuilder = new System.Text.StringBuilder();
        
        while (lineIndex < lines.Length && !string.IsNullOrWhiteSpace(lines[lineIndex]))
        {
            if (textBuilder.Length > 0)
                textBuilder.AppendLine();
            
            // Strip voice and other tags
            string strippedLine = StripTags(lines[lineIndex]);
            textBuilder.Append(strippedLine);
            lineIndex++;
        }
        
        string text = textBuilder.ToString().Trim();
        
        cue = new SubtitleCue(
            startUs,
            endUs,
            text,
            SubtitleType.StyledText,
            null,
            position);
        
        return true;
    }
    
    private static bool TryParseTiming(
        ReadOnlySpan<char> line,
        out long startUs,
        out long endUs,
        out SubtitlePosition position)
    {
        startUs = 0;
        endUs = 0;
        position = SubtitlePosition.Default;
        
        int arrowPos = line.IndexOf(TimeSeparator.AsSpan());
        if (arrowPos < 0)
            return false;
        
        var startPart = line[..arrowPos].Trim();
        var afterArrow = line[(arrowPos + TimeSeparator.Length)..];
        
        // End timestamp might be followed by cue settings
        int spacePos = afterArrow.IndexOf(' ');
        ReadOnlySpan<char> endPart;
        ReadOnlySpan<char> settings;
        
        if (spacePos > 0)
        {
            endPart = afterArrow[..spacePos].Trim();
            settings = afterArrow[(spacePos + 1)..].Trim();
        }
        else
        {
            endPart = afterArrow.Trim();
            settings = ReadOnlySpan<char>.Empty;
        }
        
        if (!SubtitleTimestamp.TryParseWebVtt(startPart, out startUs))
            return false;
        
        if (!SubtitleTimestamp.TryParseWebVtt(endPart, out endUs))
            return false;
        
        // Parse cue settings
        if (!settings.IsEmpty)
        {
            position = ParseCueSettings(settings);
        }
        
        return true;
    }
    
    private static SubtitlePosition ParseCueSettings(ReadOnlySpan<char> settings)
    {
        float horizontal = 50f;
        float vertical = 95f;
        var hAlign = SubtitleAlignment.Center;
        var vAlign = SubtitleVerticalAlignment.Bottom;
        
        // Parse settings like "position:50% align:center line:90%"
        var settingsStr = settings.ToString();
        var parts = settingsStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var part in parts)
        {
            int colonPos = part.IndexOf(':');
            if (colonPos < 0)
                continue;
            
            var key = part[..colonPos];
            var value = part[(colonPos + 1)..];
            
            switch (key)
            {
                case "position":
                    if (TryParsePercent(value, out float pos))
                        horizontal = pos;
                    break;
                
                case "line":
                    if (TryParsePercent(value, out float line))
                        vertical = line;
                    break;
                
                case "align":
                    hAlign = value switch
                    {
                        "start" or "left" => SubtitleAlignment.Left,
                        "center" or "middle" => SubtitleAlignment.Center,
                        "end" or "right" => SubtitleAlignment.Right,
                        _ => SubtitleAlignment.Center
                    };
                    break;
                
                case "vertical":
                    // rl = right-to-left, lr = left-to-right (vertical text)
                    break;
            }
        }
        
        return new SubtitlePosition(horizontal, vertical, hAlign, vAlign);
    }
    
    private static bool TryParsePercent(string value, out float percent)
    {
        percent = 0;
        
        if (value.EndsWith('%'))
            value = value[..^1];
        
        return float.TryParse(value, out percent);
    }
    
    private static string StripTags(string text)
    {
        // Strip WebVTT tags: <v Speaker>, <c.classname>, <i>, <b>, etc.
        var result = new System.Text.StringBuilder(text.Length);
        bool inTag = false;
        
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            
            if (c == '<')
            {
                inTag = true;
                continue;
            }
            
            if (c == '>')
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
}

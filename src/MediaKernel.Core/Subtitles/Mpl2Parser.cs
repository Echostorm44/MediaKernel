// MPL2 Subtitle Parser
// Format: [start][end] text
// Times are in deciseconds (100ms units)
// | is converted to newlines
// Leading / on lines indicates italic (stripped)

using System.Text.RegularExpressions;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Parser for MPL2 subtitle format.
/// Format: [start][end] text or [start][] text (for auto-duration)
/// Times are in deciseconds (100ms = 1 unit).
/// </summary>
public static partial class Mpl2Parser
{
    // Regex for MPL2 line: [start][end] text or [start][] text
    [GeneratedRegex(@"^\[(\d+)\]\[(\d*)\]\s*(.+)$")]
    private static partial Regex LinePattern();
    
    private const int DefaultDurationDeciseconds = 30; // 3 seconds default
    
    /// <summary>
    /// Parses MPL2 subtitle content.
    /// </summary>
    /// <param name="content">The MPL2 subtitle content.</param>
    /// <returns>List of subtitle cues.</returns>
    public static List<SubtitleCue> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];
        
        var cues = new List<SubtitleCue>();
        var regex = LinePattern();
        
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        
        foreach (string line in lines)
        {
            var match = regex.Match(line);
            if (!match.Success)
                continue;
            
            string startStr = match.Groups[1].Value;
            string endStr = match.Groups[2].Value;
            string text = match.Groups[3].Value;
            
            if (!int.TryParse(startStr, out int startDs))
                continue;
            
            // End time can be empty - use default duration
            int endDs;
            if (string.IsNullOrEmpty(endStr) || !int.TryParse(endStr, out endDs))
            {
                endDs = startDs + DefaultDurationDeciseconds;
            }
            
            // Convert deciseconds to microseconds (1 ds = 100ms = 100000 us)
            long startUs = (long)startDs * 100_000;
            long endUs = (long)endDs * 100_000;
            
            // Process text: | → newline, strip leading / (italic marker)
            string processedText = ProcessText(text);
            
            if (!string.IsNullOrWhiteSpace(processedText))
            {
                cues.Add(SubtitleCue.CreateText(startUs, endUs, processedText));
            }
        }
        
        return cues;
    }
    
    /// <summary>
    /// Processes MPL2 text: converts | to newlines and strips italic markers.
    /// </summary>
    private static string ProcessText(string text)
    {
        // Replace | with newlines
        text = text.Replace('|', '\n');
        
        // Process each line: strip leading / (italic marker)
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith('/'))
            {
                lines[i] = lines[i][1..];
            }
        }
        
        return string.Join('\n', lines).Trim();
    }
    
    /// <summary>
    /// Checks if content appears to be MPL2 format.
    /// </summary>
    /// <param name="content">The subtitle content to check.</param>
    /// <returns>True if content appears to be MPL2 format.</returns>
    public static bool CanParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        
        var regex = LinePattern();
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        
        // Check first few non-empty lines for MPL2 pattern
        int matchCount = 0;
        int checkedLines = 0;
        
        foreach (string line in lines)
        {
            if (checkedLines >= 5)
                break;
            
            if (regex.IsMatch(line))
                matchCount++;
            
            checkedLines++;
        }
        
        // At least one match in first 5 lines suggests MPL2
        return matchCount > 0;
    }
}

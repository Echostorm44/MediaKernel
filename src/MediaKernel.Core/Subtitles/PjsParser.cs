// PhoenixSub (PJS) Subtitle Parser
// Format: start,end,"text"
// Times are in 1/10th second units (100ms = 1 unit)

using System.Text.RegularExpressions;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Parser for PhoenixSub (PJS) subtitle format.
/// Format: start,end,"text"
/// Times are in deciseconds (1/10th second = 100ms = 1 unit).
/// </summary>
public static partial class PjsParser
{
    // Regex for PJS line: start,end,"text"
    [GeneratedRegex(@"^(\d+),(\d+),\""(.+)\""\s*$")]
    private static partial Regex LinePattern();
    
    /// <summary>
    /// Parses PJS subtitle content.
    /// </summary>
    /// <param name="content">The PJS subtitle content.</param>
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
            
            if (!int.TryParse(startStr, out int startDs) ||
                !int.TryParse(endStr, out int endDs))
                continue;
            
            // Convert deciseconds to microseconds (1 ds = 100ms = 100000 us)
            long startUs = (long)startDs * 100_000;
            long endUs = (long)endDs * 100_000;
            
            // Process text: | → newline (common in subtitle formats)
            string processedText = text.Replace('|', '\n').Trim();
            
            if (!string.IsNullOrWhiteSpace(processedText))
            {
                cues.Add(SubtitleCue.CreateText(startUs, endUs, processedText));
            }
        }
        
        return cues;
    }
    
    /// <summary>
    /// Checks if content appears to be PJS format.
    /// </summary>
    /// <param name="content">The subtitle content to check.</param>
    /// <returns>True if content appears to be PJS format.</returns>
    public static bool CanParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        
        var regex = LinePattern();
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        
        // Check first few non-empty lines for PJS pattern
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
        
        // At least one match in first 5 lines suggests PJS
        return matchCount > 0;
    }
}

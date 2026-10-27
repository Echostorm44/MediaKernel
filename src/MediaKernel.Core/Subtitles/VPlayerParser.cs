// VPlayer Subtitle Parser
// Format: HH:MM:SS|text
// No end time - use next cue start as end time

using System.Text.RegularExpressions;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Parser for VPlayer subtitle format.
/// Format: HH:MM:SS|text or HH:MM:SS:text
/// No explicit end time - calculated from next cue start.
/// </summary>
public static partial class VPlayerParser
{
    // Regex for VPlayer line: HH:MM:SS|text or HH:MM:SS:text (pipe or colon separator after seconds)
    // Must not match SRT timestamp format (uses --> arrow)
    [GeneratedRegex(@"^(\d{1,2}):(\d{2}):(\d{2})[|:](.+)$")]
    private static partial Regex LinePattern();
    
    private const long DefaultEndOffsetMicroseconds = 3_000_000; // 3 seconds default
    
    /// <summary>
    /// Parses VPlayer subtitle content.
    /// </summary>
    /// <param name="content">The VPlayer subtitle content.</param>
    /// <returns>List of subtitle cues.</returns>
    public static List<SubtitleCue> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];
        
        var entries = new List<(long startUs, string text)>();
        var regex = LinePattern();
        
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        
        foreach (string line in lines)
        {
            var match = regex.Match(line);
            if (!match.Success)
                continue;
            
            if (!int.TryParse(match.Groups[1].Value, out int hours) ||
                !int.TryParse(match.Groups[2].Value, out int minutes) ||
                !int.TryParse(match.Groups[3].Value, out int seconds))
                continue;
            
            string text = match.Groups[4].Value.Trim();
            
            // Convert to microseconds
            long startUs = ((long)hours * 3600 + minutes * 60 + seconds) * 1_000_000;
            
            if (!string.IsNullOrWhiteSpace(text))
            {
                entries.Add((startUs, text));
            }
        }
        
        // Now build cues with end times from next start
        var cues = new List<SubtitleCue>();
        
        for (int i = 0; i < entries.Count; i++)
        {
            long startUs = entries[i].startUs;
            long endUs;
            
            if (i + 1 < entries.Count)
            {
                // End time is start of next cue
                endUs = entries[i + 1].startUs;
            }
            else
            {
                // Last cue - use default duration
                endUs = startUs + DefaultEndOffsetMicroseconds;
            }
            
            cues.Add(SubtitleCue.CreateText(startUs, endUs, entries[i].text));
        }
        
        return cues;
    }
    
    /// <summary>
    /// Checks if content appears to be VPlayer format.
    /// </summary>
    /// <param name="content">The subtitle content to check.</param>
    /// <returns>True if content appears to be VPlayer format.</returns>
    public static bool CanParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        
        var regex = LinePattern();
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        
        // Check first few non-empty lines for VPlayer pattern
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
        
        // At least one match in first 5 lines suggests VPlayer
        return matchCount > 0;
    }
}

// Legacy Subtitle Parsers
// Ports of VLC's subtitle.c parsers for older/regional formats:
// - SubViewer 1.0/2.0 (SUB_TYPE_SUBVIEWER, SUB_TYPE_SUBVIEW1)
// - AQTitle (SUB_TYPE_AQT) 
// - MPSub (SUB_TYPE_MPSUB)
// - DKS (SUB_TYPE_DKS) - Korean subtitles
// - JacoSub (SUB_TYPE_JACOSUB)

using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Parser for SubViewer 2.0 subtitle format (.sub files).
/// Format: HH:MM:SS.cs,HH:MM:SS.cs followed by text lines with [br] breaks.
/// </summary>
public static partial class SubViewerParser
{
    // SubViewer 2 timing: HH:MM:SS.cs,HH:MM:SS.cs (cs = centiseconds)
    [GeneratedRegex(@"^(\d{1,2}):(\d{2}):(\d{2})\.(\d{2}),(\d{1,2}):(\d{2}):(\d{2})\.(\d{2})\s*$")]
    private static partial Regex TimingPattern();
    
    /// <summary>
    /// Parses SubViewer 2.0 subtitle content.
    /// </summary>
    public static List<SubtitleCue> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];
        
        var cues = new List<SubtitleCue>();
        var timingRegex = TimingPattern();
        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            var match = timingRegex.Match(line);
            
            if (!match.Success)
                continue;
            
            // Parse timing: HH:MM:SS.cs
            int h1 = int.Parse(match.Groups[1].Value);
            int m1 = int.Parse(match.Groups[2].Value);
            int s1 = int.Parse(match.Groups[3].Value);
            int cs1 = int.Parse(match.Groups[4].Value);
            
            int h2 = int.Parse(match.Groups[5].Value);
            int m2 = int.Parse(match.Groups[6].Value);
            int s2 = int.Parse(match.Groups[7].Value);
            int cs2 = int.Parse(match.Groups[8].Value);
            
            long startUs = ((h1 * 3600L + m1 * 60L + s1) * 1_000_000L) + (cs1 * 10_000L);
            long endUs = ((h2 * 3600L + m2 * 60L + s2) * 1_000_000L) + (cs2 * 10_000L);
            
            // Collect text lines until empty line or next timing
            var textLines = new List<string>();
            while (++i < lines.Length)
            {
                string textLine = lines[i].Trim();
                if (string.IsNullOrEmpty(textLine))
                    break;
                if (timingRegex.IsMatch(textLine))
                {
                    i--; // Reprocess this line as timing
                    break;
                }
                
                // Replace [br] with newlines
                textLine = textLine.Replace("[br]", "\n", StringComparison.OrdinalIgnoreCase);
                textLines.Add(textLine);
            }
            
            string text = string.Join("\n", textLines).Trim();
            if (!string.IsNullOrEmpty(text))
            {
                cues.Add(SubtitleCue.CreateText(startUs, endUs, text));
            }
        }
        
        return cues;
    }
    
    /// <summary>
    /// Checks if content appears to be SubViewer 2.0 format.
    /// </summary>
    public static bool CanParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        
        // SubViewer 2 files often have [INFORMATION] header or timing lines
        return content.Contains("[INFORMATION]", StringComparison.OrdinalIgnoreCase) ||
               TimingPattern().IsMatch(content);
    }
}

/// <summary>
/// Parser for SubViewer 1.0 subtitle format.
/// Format: [HH:MM:SS] text [HH:MM:SS]
/// </summary>
public static partial class SubViewer1Parser
{
    [GeneratedRegex(@"^\[(\d{1,2}):(\d{2}):(\d{2})\]\s*$")]
    private static partial Regex TimingPattern();
    
    /// <summary>
    /// Parses SubViewer 1.0 subtitle content.
    /// </summary>
    public static List<SubtitleCue> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];
        
        var cues = new List<SubtitleCue>();
        var timingRegex = TimingPattern();
        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            var match = timingRegex.Match(line);
            
            if (!match.Success)
                continue;
            
            int h1 = int.Parse(match.Groups[1].Value);
            int m1 = int.Parse(match.Groups[2].Value);
            int s1 = int.Parse(match.Groups[3].Value);
            
            long startUs = (h1 * 3600L + m1 * 60L + s1) * 1_000_000L;
            
            // Next line is text
            if (++i >= lines.Length)
                break;
            
            string text = lines[i].Trim();
            if (string.IsNullOrEmpty(text))
                continue;
            
            // Look for end timing on next line
            long endUs = startUs + 3_000_000L; // Default 3 second duration
            
            if (i + 1 < lines.Length)
            {
                var endMatch = timingRegex.Match(lines[i + 1].Trim());
                if (endMatch.Success)
                {
                    int h2 = int.Parse(endMatch.Groups[1].Value);
                    int m2 = int.Parse(endMatch.Groups[2].Value);
                    int s2 = int.Parse(endMatch.Groups[3].Value);
                    endUs = (h2 * 3600L + m2 * 60L + s2) * 1_000_000L;
                    i++; // Skip end timing
                }
            }
            
            cues.Add(SubtitleCue.CreateText(startUs, endUs, text));
        }
        
        return cues;
    }
    
    /// <summary>
    /// Checks if content appears to be SubViewer 1.0 format.
    /// </summary>
    public static bool CanParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        
        return TimingPattern().IsMatch(content);
    }
}

/// <summary>
/// Parser for AQTitle subtitle format.
/// Format: -->> frame followed by text lines.
/// </summary>
public static partial class AqtParser
{
    [GeneratedRegex(@"^-->> (\d+)\s*$")]
    private static partial Regex FramePattern();
    
    /// <summary>
    /// Default frame duration in microseconds per frame (25fps = 40000us/frame).
    /// </summary>
    public const long DefaultMicrosecondsPerFrame = 40_000; // 25fps
    
    /// <summary>
    /// Parses AQTitle subtitle content.
    /// </summary>
    /// <param name="content">The AQTitle content.</param>
    /// <param name="microsecondsPerFrame">Microseconds per frame (based on video fps).</param>
    public static List<SubtitleCue> Parse(string content, long microsecondsPerFrame = DefaultMicrosecondsPerFrame)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];
        
        var cues = new List<SubtitleCue>();
        var frameRegex = FramePattern();
        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        
        long currentStartUs = -1;
        var textLines = new List<string>();
        
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            var match = frameRegex.Match(line);
            
            if (match.Success)
            {
                long frame = long.Parse(match.Groups[1].Value);
                long timeUs = frame * microsecondsPerFrame;
                
                // If we have accumulated text, create cue with previous start -> current time
                if (currentStartUs >= 0 && textLines.Count > 0)
                {
                    string text = string.Join("\n", textLines).Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        cues.Add(SubtitleCue.CreateText(currentStartUs, timeUs, text));
                    }
                    textLines.Clear();
                }
                
                currentStartUs = timeUs;
            }
            else if (currentStartUs >= 0)
            {
                // Text line after frame marker
                textLines.Add(line);
            }
        }
        
        // Handle any remaining text at end of file
        if (currentStartUs >= 0 && textLines.Count > 0)
        {
            string text = string.Join("\n", textLines).Trim();
            if (!string.IsNullOrEmpty(text))
            {
                // Default 3 second duration for last subtitle
                cues.Add(SubtitleCue.CreateText(currentStartUs, currentStartUs + 3_000_000, text));
            }
        }
        
        return cues;
    }
    
    /// <summary>
    /// Checks if content appears to be AQTitle format.
    /// </summary>
    public static bool CanParse(string content)
    {
        return !string.IsNullOrWhiteSpace(content) && FramePattern().IsMatch(content);
    }
}

/// <summary>
/// Parser for MPSub subtitle format (MPlayer).
/// Format: FORMAT=TIME or FORMAT=fps, then wait duration pairs followed by text.
/// </summary>
public static partial class MpSubParser
{
    [GeneratedRegex(@"^FORMAT\s*=\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FormatPattern();
    
    [GeneratedRegex(@"^(-?[\d.]+)\s+(-?[\d.]+)\s*$")]
    private static partial Regex TimingPattern();
    
    /// <summary>
    /// Parses MPSub subtitle content.
    /// </summary>
    public static List<SubtitleCue> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];
        
        var cues = new List<SubtitleCue>();
        var formatRegex = FormatPattern();
        var timingRegex = TimingPattern();
        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        
        // Default: TIME format (centiseconds)
        double timeFactor = 100.0; // Multiply by this to get centiseconds
        double totalTime = 0.0;
        
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            
            // Skip comments and empty lines
            if (line.StartsWith('#') || string.IsNullOrEmpty(line))
                continue;
            
            // Check for format specification
            var formatMatch = formatRegex.Match(line);
            if (formatMatch.Success)
            {
                string formatValue = formatMatch.Groups[1].Value.Trim();
                if (formatValue.Equals("TIME", StringComparison.OrdinalIgnoreCase))
                {
                    timeFactor = 100.0; // centiseconds
                }
                else if (double.TryParse(formatValue, CultureInfo.InvariantCulture, out double fps) && fps > 0)
                {
                    // Frame-based: factor = 1 (we'll convert frames to time using fps)
                    timeFactor = 1.0;
                }
                continue;
            }
            
            // Check for timing line
            var timingMatch = timingRegex.Match(line);
            if (timingMatch.Success)
            {
                double wait = double.Parse(timingMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                double duration = double.Parse(timingMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                
                totalTime += wait * timeFactor;
                long startUs = (long)(totalTime * 10_000); // Convert centiseconds to microseconds
                
                totalTime += duration * timeFactor;
                long endUs = (long)(totalTime * 10_000);
                
                // Collect text lines until empty line
                var textLines = new List<string>();
                while (++i < lines.Length)
                {
                    string textLine = lines[i];
                    if (string.IsNullOrWhiteSpace(textLine))
                        break;
                    textLines.Add(textLine);
                }
                
                string text = string.Join("\n", textLines).Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    cues.Add(SubtitleCue.CreateText(startUs, endUs, text));
                }
            }
        }
        
        return cues;
    }
    
    /// <summary>
    /// Checks if content appears to be MPSub format.
    /// </summary>
    public static bool CanParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        
        // Look for FORMAT= line
        return FormatPattern().IsMatch(content);
    }
}

/// <summary>
/// Parser for DKS subtitle format (Korean).
/// Format: [HH:MM:SS]text with [br] line breaks.
/// </summary>
public static partial class DksParser
{
    [GeneratedRegex(@"^\[(\d{1,2}):(\d{2}):(\d{2})\](.*)$")]
    private static partial Regex LinePattern();
    
    /// <summary>
    /// Parses DKS subtitle content.
    /// </summary>
    public static List<SubtitleCue> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];
        
        var cues = new List<SubtitleCue>();
        var lineRegex = LinePattern();
        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            var match = lineRegex.Match(line);
            
            if (!match.Success)
                continue;
            
            int h1 = int.Parse(match.Groups[1].Value);
            int m1 = int.Parse(match.Groups[2].Value);
            int s1 = int.Parse(match.Groups[3].Value);
            string text = match.Groups[4].Value;
            
            long startUs = (h1 * 3600L + m1 * 60L + s1) * 1_000_000L;
            
            // Look for end timing on next line
            long endUs = startUs + 3_000_000L; // Default 3 second duration
            
            if (i + 1 < lines.Length)
            {
                var endMatch = lineRegex.Match(lines[i + 1].Trim());
                if (endMatch.Success && string.IsNullOrEmpty(endMatch.Groups[4].Value))
                {
                    // Next line is just timing (end marker)
                    int h2 = int.Parse(endMatch.Groups[1].Value);
                    int m2 = int.Parse(endMatch.Groups[2].Value);
                    int s2 = int.Parse(endMatch.Groups[3].Value);
                    endUs = (h2 * 3600L + m2 * 60L + s2) * 1_000_000L;
                    i++; // Skip end timing
                }
            }
            
            // Replace [br] with newlines
            text = text.Replace("[br]", "\n", StringComparison.OrdinalIgnoreCase).Trim();
            
            if (!string.IsNullOrEmpty(text))
            {
                cues.Add(SubtitleCue.CreateText(startUs, endUs, text));
            }
        }
        
        return cues;
    }
    
    /// <summary>
    /// Checks if content appears to be DKS format.
    /// </summary>
    public static bool CanParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        
        return LinePattern().IsMatch(content);
    }
}

/// <summary>
/// Parser for JacoSub subtitle format (.jss files).
/// Format: start end directive text
/// Times in HH:MM:SS.cs or relative format.
/// </summary>
public static partial class JacoSubParser
{
    // JacoSub timing: HH:MM:SS.cs HH:MM:SS.cs directive text
    [GeneratedRegex(@"^(\d{1,2}):(\d{2}):(\d{2})\.(\d{2})\s+(\d{1,2}):(\d{2}):(\d{2})\.(\d{2})\s+(\w+)\s+(.*)$")]
    private static partial Regex DialoguePattern();
    
    // Simpler JacoSub format: start end text
    [GeneratedRegex(@"^(\d{1,2}):(\d{2}):(\d{2})\.(\d{2})\s+(\d{1,2}):(\d{2}):(\d{2})\.(\d{2})\s+(.*)$")]
    private static partial Regex SimplePattern();
    
    /// <summary>
    /// Parses JacoSub subtitle content.
    /// </summary>
    public static List<SubtitleCue> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];
        
        var cues = new List<SubtitleCue>();
        var dialogueRegex = DialoguePattern();
        var simpleRegex = SimplePattern();
        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        
        foreach (string line in lines)
        {
            string trimmedLine = line.Trim();
            
            // Skip comments and empty lines
            if (trimmedLine.StartsWith('#') || string.IsNullOrEmpty(trimmedLine))
                continue;
            
            // Try full format with directive first
            var match = dialogueRegex.Match(trimmedLine);
            if (match.Success)
            {
                var (startUs, endUs) = ParseTimings(match);
                string directive = match.Groups[9].Value;
                string text = match.Groups[10].Value;
                
                // Check if it's a known dialogue directive
                if (directive.Equals("D", StringComparison.OrdinalIgnoreCase) ||
                    directive.Equals("NTP", StringComparison.OrdinalIgnoreCase))
                {
                    text = ProcessJssText(text);
                    if (!string.IsNullOrEmpty(text))
                    {
                        cues.Add(SubtitleCue.CreateText(startUs, endUs, text));
                    }
                    continue;
                }
                // Not a known directive - fall through to try simple format
            }
            
            // Try simple format (timing followed by text, no directive)
            match = simpleRegex.Match(trimmedLine);
            if (match.Success)
            {
                var (startUs, endUs) = ParseTimings(match);
                string text = ProcessJssText(match.Groups[9].Value);
                
                if (!string.IsNullOrEmpty(text))
                {
                    cues.Add(SubtitleCue.CreateText(startUs, endUs, text));
                }
            }
        }
        
        return cues;
    }
    
    private static (long startUs, long endUs) ParseTimings(Match match)
    {
        int h1 = int.Parse(match.Groups[1].Value);
        int m1 = int.Parse(match.Groups[2].Value);
        int s1 = int.Parse(match.Groups[3].Value);
        int cs1 = int.Parse(match.Groups[4].Value);
        
        int h2 = int.Parse(match.Groups[5].Value);
        int m2 = int.Parse(match.Groups[6].Value);
        int s2 = int.Parse(match.Groups[7].Value);
        int cs2 = int.Parse(match.Groups[8].Value);
        
        long startUs = ((h1 * 3600L + m1 * 60L + s1) * 1_000_000L) + (cs1 * 10_000L);
        long endUs = ((h2 * 3600L + m2 * 60L + s2) * 1_000_000L) + (cs2 * 10_000L);
        
        return (startUs, endUs);
    }
    
    private static string ProcessJssText(string text)
    {
        // JacoSub uses ~ for line breaks
        return text.Replace("~", "\n").Trim();
    }
    
    /// <summary>
    /// Checks if content appears to be JacoSub format.
    /// </summary>
    public static bool CanParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        
        return DialoguePattern().IsMatch(content) || SimplePattern().IsMatch(content);
    }
}

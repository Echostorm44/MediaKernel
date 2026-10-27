// MediaKernel - MicroDVD Subtitle Parser
// Format: {startFrame}{endFrame}Line1|Line2|Line3
// Reference: VLC modules/demux/subtitle.c ParseMicroDvd

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Parser for MicroDVD subtitle format (.sub files with frame-based timing).
/// </summary>
/// <remarks>
/// MicroDVD format uses frame numbers for timing:
/// - {startFrame}{endFrame}Text with | as line breaks
/// - {1}{1}23.976 sets the frame rate
/// - Common with anime fansubs and older video files
/// </remarks>
public static partial class MicroDvdParser
{
    // Pattern: {startFrame}{endFrame}text or {startFrame}{}text
    [GeneratedRegex(@"^\{(\d+)\}\{(\d*)\}(.*)$", RegexOptions.Compiled)]
    private static partial Regex CuePattern();
    
    /// <summary>
    /// Default frame rate if not specified in the file.
    /// </summary>
    public const double DefaultFrameRate = 23.976;
    
    /// <summary>
    /// Parses MicroDVD content from a string.
    /// </summary>
    /// <param name="content">The MicroDVD content to parse.</param>
    /// <param name="frameRate">Frame rate to use (if null, auto-detect from file or use default).</param>
    /// <returns>List of parsed subtitle cues.</returns>
    public static List<SubtitleCue> Parse(string content, double? frameRate = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        
        var cues = new List<SubtitleCue>();
        double effectiveFrameRate = frameRate ?? DefaultFrameRate;
        
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int cueIndex = 0;
        
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            
            var match = CuePattern().Match(line.Trim());
            if (!match.Success)
                continue;
            
            int startFrame = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            string endFrameStr = match.Groups[2].Value;
            string text = match.Groups[3].Value;
            
            // Check for frame rate setting: {1}{1}23.976
            if (startFrame == 1 && (string.IsNullOrEmpty(endFrameStr) || endFrameStr == "1"))
            {
                if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double detectedFps))
                {
                    if (detectedFps > 0 && frameRate == null)
                    {
                        effectiveFrameRate = detectedFps;
                    }
                    continue; // This line is not a subtitle
                }
            }
            
            int endFrame = string.IsNullOrEmpty(endFrameStr) ? -1 : int.Parse(endFrameStr, CultureInfo.InvariantCulture);
            
            // Convert frame numbers to timespans
            TimeSpan startTime = FrameToTimeSpan(startFrame, effectiveFrameRate);
            TimeSpan endTime = endFrame >= 0 
                ? FrameToTimeSpan(endFrame, effectiveFrameRate) 
                : startTime + TimeSpan.FromSeconds(3); // Default 3 second duration
            
            // Replace | with newlines and process text
            string processedText = ProcessText(text);
            
            if (!string.IsNullOrWhiteSpace(processedText))
            {
                long startUs = (long)(startTime.TotalSeconds * 1_000_000);
                long endUs = (long)(endTime.TotalSeconds * 1_000_000);
                cues.Add(SubtitleCue.CreateText(startUs, endUs, processedText));
            }
        }
        
        return cues;
    }
    
    /// <summary>
    /// Parses MicroDVD content from a file.
    /// </summary>
    /// <param name="filePath">Path to the MicroDVD subtitle file.</param>
    /// <param name="frameRate">Frame rate to use (if null, auto-detect from file or use default).</param>
    /// <returns>List of parsed subtitle cues.</returns>
    public static List<SubtitleCue> ParseFile(string filePath, double? frameRate = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        
        string content = File.ReadAllText(filePath);
        return Parse(content, frameRate);
    }
    
    /// <summary>
    /// Parses MicroDVD content from a stream.
    /// </summary>
    /// <param name="stream">Stream containing MicroDVD content.</param>
    /// <param name="frameRate">Frame rate to use (if null, auto-detect from file or use default).</param>
    /// <returns>List of parsed subtitle cues.</returns>
    public static List<SubtitleCue> Parse(Stream stream, double? frameRate = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        
        using var reader = new StreamReader(stream);
        string content = reader.ReadToEnd();
        return Parse(content, frameRate);
    }
    
    /// <summary>
    /// Detects if content is likely MicroDVD format.
    /// </summary>
    /// <param name="content">Content to check.</param>
    /// <returns>True if content appears to be MicroDVD format.</returns>
    public static bool CanParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;
        
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        
        // Check first few non-empty lines for MicroDVD pattern
        int matchCount = 0;
        int checkedCount = 0;
        
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            
            if (CuePattern().IsMatch(line.Trim()))
                matchCount++;
            
            checkedCount++;
            if (checkedCount >= 5)
                break;
        }
        
        // If at least 2 of first 5 lines match, it's likely MicroDVD
        return matchCount >= 2;
    }
    
    /// <summary>
    /// Detects the frame rate from the file content.
    /// </summary>
    /// <param name="content">Content to check.</param>
    /// <returns>Detected frame rate, or null if not found.</returns>
    public static double? DetectFrameRate(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;
        
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines.Take(10)) // Check first 10 lines
        {
            var match = CuePattern().Match(line.Trim());
            if (!match.Success)
                continue;
            
            int startFrame = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            string endFrameStr = match.Groups[2].Value;
            string text = match.Groups[3].Value;
            
            // Frame rate setting: {1}{1}23.976 or {1}{}23.976
            if (startFrame == 1 && (string.IsNullOrEmpty(endFrameStr) || endFrameStr == "1"))
            {
                if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) && fps > 0)
                {
                    return fps;
                }
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Converts a frame number to a TimeSpan based on frame rate.
    /// </summary>
    private static TimeSpan FrameToTimeSpan(int frame, double frameRate)
    {
        if (frameRate <= 0)
            frameRate = DefaultFrameRate;
        
        double seconds = frame / frameRate;
        return TimeSpan.FromSeconds(seconds);
    }
    
    /// <summary>
    /// Processes MicroDVD text (converts | to newlines, strips formatting).
    /// </summary>
    private static string ProcessText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        
        // Replace | with newline
        text = text.Replace('|', '\n');
        
        // Strip common MicroDVD style tags
        // {y:i} = italic, {y:b} = bold, {y:u} = underline
        // {c:$BBGGRR} = color, {f:fontname} = font, {s:size} = size
        text = StripStyleTags(text);
        
        return text.Trim();
    }
    
    /// <summary>
    /// Strips MicroDVD style tags from text.
    /// </summary>
    private static string StripStyleTags(string text)
    {
        // Simple style tags: {y:i}, {y:b}, {y:u}, {y:s}
        // Color: {c:$RRGGBB}, Font: {f:name}, Size: {s:size}
        // Position: {o:x,y}, {P:x,y}
        
        int i = 0;
        var result = new System.Text.StringBuilder(text.Length);
        
        while (i < text.Length)
        {
            if (text[i] == '{' && i + 2 < text.Length)
            {
                // Find closing brace
                int closeIndex = text.IndexOf('}', i + 1);
                if (closeIndex > i)
                {
                    string tag = text.Substring(i + 1, closeIndex - i - 1);
                    
                    // Check if it's a style tag (starts with y:, c:, f:, s:, o:, p:, C:, F:, S:, Y:)
                    if (tag.Length >= 2 && tag[1] == ':')
                    {
                        char tagType = char.ToLowerInvariant(tag[0]);
                        if (tagType == 'y' || tagType == 'c' || tagType == 'f' || 
                            tagType == 's' || tagType == 'o' || tagType == 'p')
                        {
                            i = closeIndex + 1;
                            continue;
                        }
                    }
                }
            }
            
            result.Append(text[i]);
            i++;
        }
        
        return result.ToString();
    }
}

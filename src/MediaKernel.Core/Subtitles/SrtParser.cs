// SRT (SubRip) Subtitle Parser
// Zero-allocation parsing using ReadOnlySpan<char>

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Parser for SRT (SubRip) subtitle files.
/// </summary>
/// <remarks>
/// SRT Format:
/// 1
/// 00:00:01,000 --> 00:00:04,000
/// First subtitle text
/// (optional second line)
/// 
/// 2
/// 00:00:05,000 --> 00:00:08,000
/// Second subtitle text
/// </remarks>
public static class SrtParser
{
    private static readonly char[] NewlineChars = ['\r', '\n'];
    private const string TimeSeparator = " --> ";
    
    /// <summary>
    /// Parses an SRT file from text content.
    /// </summary>
    /// <param name="content">The complete SRT file content.</param>
    /// <returns>A subtitle track containing all parsed cues.</returns>
    public static SubtitleTrack Parse(string content)
    {
        var track = new SubtitleTrack
        {
            Format = SubtitleFormat.Srt
        };
        
        if (string.IsNullOrEmpty(content))
            return track;
        
        // Split into cue blocks (separated by blank lines)
        var blocks = SplitIntoBlocks(content);
        
        foreach (var block in blocks)
        {
            if (TryParseBlock(block, out var cue))
            {
                track.AddCue(cue);
            }
        }
        
        return track;
    }
    
    /// <summary>
    /// Parses an SRT file from a stream.
    /// </summary>
    public static SubtitleTrack Parse(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
    
    /// <summary>
    /// Tries to parse a single SRT cue block.
    /// </summary>
    public static bool TryParseBlock(ReadOnlySpan<char> block, out SubtitleCue cue)
    {
        cue = default;
        block = block.Trim();
        
        if (block.IsEmpty)
            return false;
        
        // Find first line (cue index)
        int firstLineEnd = block.IndexOfAny('\r', '\n');
        if (firstLineEnd < 0)
            return false;
        
        var indexLine = block[..firstLineEnd].Trim();
        
        // Skip cue index validation - just need it to be a number
        if (!IsNumeric(indexLine))
            return false;
        
        // Advance past newlines
        block = SkipNewlines(block[(firstLineEnd)..]);
        if (block.IsEmpty)
            return false;
        
        // Find second line (timing)
        int secondLineEnd = block.IndexOfAny('\r', '\n');
        ReadOnlySpan<char> timingLine;
        ReadOnlySpan<char> textContent;
        
        if (secondLineEnd < 0)
        {
            // Only timing line, no text
            timingLine = block;
            textContent = ReadOnlySpan<char>.Empty;
        }
        else
        {
            timingLine = block[..secondLineEnd].Trim();
            textContent = SkipNewlines(block[(secondLineEnd)..]);
        }
        
        // Parse timing line
        if (!TryParseTiming(timingLine, out long startUs, out long endUs))
            return false;
        
        // Build text from remaining lines
        string text = BuildText(textContent);
        
        cue = new SubtitleCue(startUs, endUs, text, SubtitleType.Text);
        return true;
    }
    
    /// <summary>
    /// Parses a timing line: "00:00:01,000 --> 00:00:04,000"
    /// </summary>
    private static bool TryParseTiming(ReadOnlySpan<char> line, out long startUs, out long endUs)
    {
        startUs = 0;
        endUs = 0;
        
        // Find arrow separator
        int arrowIndex = line.IndexOf(TimeSeparator.AsSpan());
        if (arrowIndex < 0)
            return false;
        
        var startPart = line[..arrowIndex].Trim();
        var endPart = line[(arrowIndex + TimeSeparator.Length)..].Trim();
        
        // End part might have positioning info after the timestamp
        int spaceIndex = endPart.IndexOf(' ');
        if (spaceIndex > 0)
            endPart = endPart[..spaceIndex];
        
        if (!SubtitleTimestamp.TryParseSrt(startPart, out startUs))
            return false;
        
        if (!SubtitleTimestamp.TryParseSrt(endPart, out endUs))
            return false;
        
        return true;
    }
    
    /// <summary>
    /// Builds subtitle text from remaining content, handling multiple lines.
    /// </summary>
    private static string BuildText(ReadOnlySpan<char> content)
    {
        if (content.IsEmpty)
            return string.Empty;
        
        // Convert \r\n or \r to \n for consistent line breaks
        // Also strip HTML-like tags (e.g., <i>, <b>, </i>, </b>)
        var builder = new System.Text.StringBuilder(content.Length);
        bool inTag = false;
        bool lastWasNewline = false;
        
        foreach (char c in content)
        {
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
            
            if (inTag)
                continue;
            
            if (c == '\r')
                continue; // Skip \r
            
            if (c == '\n')
            {
                if (!lastWasNewline)
                {
                    builder.Append('\n');
                    lastWasNewline = true;
                }
                continue;
            }
            
            builder.Append(c);
            lastWasNewline = false;
        }
        
        return builder.ToString().Trim();
    }
    
    /// <summary>
    /// Splits content into blocks separated by blank lines.
    /// </summary>
    private static List<string> SplitIntoBlocks(string content)
    {
        var blocks = new List<string>();
        var lines = content.Split(NewlineChars, StringSplitOptions.None);
        var currentBlock = new System.Text.StringBuilder();
        
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                if (currentBlock.Length > 0)
                {
                    blocks.Add(currentBlock.ToString());
                    currentBlock.Clear();
                }
            }
            else
            {
                if (currentBlock.Length > 0)
                    currentBlock.AppendLine();
                currentBlock.Append(line);
            }
        }
        
        if (currentBlock.Length > 0)
        {
            blocks.Add(currentBlock.ToString());
        }
        
        return blocks;
    }
    
    private static bool IsNumeric(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return false;
        
        foreach (char c in text)
        {
            if (c < '0' || c > '9')
                return false;
        }
        return true;
    }
    
    private static ReadOnlySpan<char> SkipNewlines(ReadOnlySpan<char> text)
    {
        int i = 0;
        while (i < text.Length && (text[i] == '\r' || text[i] == '\n'))
            i++;
        return text[i..];
    }
}

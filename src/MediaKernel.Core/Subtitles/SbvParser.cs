// MediaKernel - SBV (YouTube SubViewer) Parser
// Common format used by YouTube for subtitle downloads

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Parser for SBV (SubViewer/YouTube) subtitle files.
/// Format uses H:MM:SS.mmm,H:MM:SS.mmm timestamps with multi-line text.
/// </summary>
public sealed class SbvParser
{
    /// <summary>
    /// Parse an SBV file from a stream.
    /// </summary>
    public List<SubtitleCue> Parse(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return ParseInternal(reader);
    }

    /// <summary>
    /// Parse SBV content from a string.
    /// </summary>
    public List<SubtitleCue> Parse(string content)
    {
        using var reader = new StringReader(content);
        return ParseInternal(reader);
    }

    /// <summary>
    /// Parse SBV content from a TextReader.
    /// </summary>
    public List<SubtitleCue> Parse(TextReader reader)
    {
        return ParseInternal(reader);
    }

    private List<SubtitleCue> ParseInternal(TextReader reader)
    {
        var cues = new List<SubtitleCue>();
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();

            // Skip empty lines
            if (string.IsNullOrEmpty(line))
                continue;

            // Try to parse timestamp line: H:MM:SS.mmm,H:MM:SS.mmm
            if (TryParseTimestampLine(line, out long startUs, out long endUs))
            {
                // Read text lines until empty line
                var textBuilder = new StringBuilder();
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrEmpty(line.Trim()))
                        break;

                    if (textBuilder.Length > 0)
                        textBuilder.Append('\n');
                    textBuilder.Append(line.Trim());
                }

                string text = textBuilder.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    cues.Add(new SubtitleCue(
                        startUs,
                        endUs,
                        text,
                        SubtitleType.Text,
                        null,
                        SubtitlePosition.Default,
                        null));
                }
            }
        }

        return cues;
    }

    private static bool TryParseTimestampLine(ReadOnlySpan<char> line, out long startUs, out long endUs)
    {
        startUs = 0;
        endUs = 0;

        // Format: H:MM:SS.mmm,H:MM:SS.mmm or 0:00:00.000,0:00:05.000
        int commaIndex = line.IndexOf(',');
        if (commaIndex < 0)
            return false;

        ReadOnlySpan<char> startPart = line.Slice(0, commaIndex).Trim();
        ReadOnlySpan<char> endPart = line.Slice(commaIndex + 1).Trim();

        if (!TryParseTimestamp(startPart, out startUs))
            return false;

        if (!TryParseTimestamp(endPart, out endUs))
            return false;

        return startUs < endUs;
    }

    private static bool TryParseTimestamp(ReadOnlySpan<char> timestamp, out long microseconds)
    {
        microseconds = 0;

        // Format: H:MM:SS.mmm or HH:MM:SS.mmm
        int firstColon = timestamp.IndexOf(':');
        if (firstColon < 0)
            return false;

        int secondColon = timestamp.Slice(firstColon + 1).IndexOf(':');
        if (secondColon < 0)
            return false;
        secondColon += firstColon + 1;

        int dot = timestamp.IndexOf('.');
        if (dot < 0)
            return false;

        // Parse hours
        if (!int.TryParse(timestamp.Slice(0, firstColon), out int hours))
            return false;

        // Parse minutes
        if (!int.TryParse(timestamp.Slice(firstColon + 1, secondColon - firstColon - 1), out int minutes))
            return false;

        // Parse seconds
        if (!int.TryParse(timestamp.Slice(secondColon + 1, dot - secondColon - 1), out int seconds))
            return false;

        // Parse milliseconds
        ReadOnlySpan<char> msSpan = timestamp.Slice(dot + 1);
        if (!int.TryParse(msSpan, out int milliseconds))
            return false;

        // Convert to microseconds
        microseconds = ((long)hours * 3600 + minutes * 60 + seconds) * 1_000_000L + milliseconds * 1000L;
        return true;
    }

    /// <summary>
    /// Check if content appears to be an SBV file.
    /// </summary>
    public static bool IsSbvContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        // Look for SBV timestamp pattern in first few lines
        using var reader = new StringReader(content);
        for (int i = 0; i < 10; i++)
        {
            string? line = reader.ReadLine();
            if (line == null)
                break;

            line = line.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            // Check for timestamp pattern: digit(s):digit:digit.digit,digit(s):digit:digit.digit
            if (TryParseTimestampLine(line, out _, out _))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if a file appears to be an SBV file.
    /// </summary>
    public static bool IsSbvFile(Stream stream)
    {
        if (!stream.CanSeek)
            return false;

        long position = stream.Position;
        try
        {
            byte[] buffer = new byte[256];
            int read = stream.Read(buffer, 0, buffer.Length);
            string content = Encoding.UTF8.GetString(buffer, 0, read);
            return IsSbvContent(content);
        }
        finally
        {
            stream.Position = position;
        }
    }
}

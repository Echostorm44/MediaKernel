// MediaKernel - SCC (Scenarist Closed Caption) Parser
// Industry-standard format for broadcast television closed captions

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Parser for SCC (Scenarist Closed Caption) files.
/// Used in broadcast television and professional video production.
/// Format uses SMPTE timecodes and CEA-608 encoded caption data.
/// </summary>
public sealed class SccParser
{
    private readonly double frameRate;
    private readonly bool dropFrame;

    /// <summary>
    /// Creates an SCC parser with the specified frame rate.
    /// </summary>
    /// <param name="frameRate">Video frame rate (default 29.97 for NTSC)</param>
    public SccParser(double frameRate = 29.97)
    {
        this.frameRate = frameRate;
        this.dropFrame = Math.Abs(frameRate - 29.97) < 0.1 || Math.Abs(frameRate - 59.94) < 0.1;
    }

    /// <summary>
    /// Parse an SCC file from a stream.
    /// </summary>
    public List<SubtitleCue> Parse(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return Parse(reader);
    }

    /// <summary>
    /// Parse SCC content from a string.
    /// </summary>
    public List<SubtitleCue> Parse(string content)
    {
        using var reader = new StringReader(content);
        return ParseInternal(reader);
    }

    /// <summary>
    /// Parse SCC content from a TextReader.
    /// </summary>
    public List<SubtitleCue> Parse(TextReader reader)
    {
        return ParseInternal(reader);
    }

    private List<SubtitleCue> ParseInternal(TextReader reader)
    {
        var cues = new List<SubtitleCue>();
        var rawCues = new List<(long startUs, string text)>();
        string? line;
        bool headerFound = false;

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();

            // Skip empty lines
            if (string.IsNullOrEmpty(line))
                continue;

            // Check for SCC header
            if (line.StartsWith("Scenarist_SCC V", StringComparison.OrdinalIgnoreCase))
            {
                headerFound = true;
                continue;
            }

            // Skip if no header found yet (for robustness, allow parsing without header)
            if (!headerFound && !line.Contains(':'))
                continue;

            // Parse timecode line: HH:MM:SS:FF or HH:MM:SS;FF
            if (TryParseTimecode(line, out long startMicroseconds, out string captionData))
            {
                // Decode CEA-608 data to text
                string text = DecodeCea608(captionData);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    rawCues.Add((startMicroseconds, text));
                }
            }
        }

        // Convert to SubtitleCue with estimated end times
        for (int i = 0; i < rawCues.Count; i++)
        {
            var (startUs, text) = rawCues[i];

            // End time is start of next cue, or 3 seconds later if last cue
            long endUs = i + 1 < rawCues.Count
                ? rawCues[i + 1].startUs
                : startUs + 3_000_000; // 3 seconds default duration

            // If consecutive cues overlap too much, use minimum duration
            if (endUs <= startUs)
            {
                endUs = startUs + 1_000_000; // 1 second minimum
            }

            cues.Add(new SubtitleCue(
                startUs,
                endUs,
                text,
                SubtitleType.Text,
                null,
                SubtitlePosition.Default,
                null));
        }

        return cues;
    }

    private bool TryParseTimecode(string line, out long microseconds, out string captionData)
    {
        microseconds = 0;
        captionData = string.Empty;

        // Format: HH:MM:SS:FF<tab>caption_data or HH:MM:SS;FF<tab>caption_data
        int tabIndex = line.IndexOf('\t');
        if (tabIndex < 0)
        {
            // Try space as separator
            int spaceIndex = line.IndexOf(' ', 11); // After timecode
            if (spaceIndex > 0)
            {
                tabIndex = spaceIndex;
            }
            else
            {
                return false;
            }
        }

        string timecodeStr = line.Substring(0, tabIndex).Trim();
        captionData = line.Substring(tabIndex + 1).Trim();

        // Parse HH:MM:SS:FF or HH:MM:SS;FF
        // Semi-colon indicates drop-frame timecode
        char separator = timecodeStr.Length > 8 ? timecodeStr[8] : ':';
        bool isDropFrame = separator == ';';

        if (!TryParseTimecodeComponents(timecodeStr, out int hours, out int minutes, out int seconds, out int frames))
        {
            return false;
        }

        // Convert to microseconds
        microseconds = TimecodeToMicroseconds(hours, minutes, seconds, frames, isDropFrame);
        return true;
    }

    private static bool TryParseTimecodeComponents(string timecode, out int hours, out int minutes, out int seconds, out int frames)
    {
        hours = minutes = seconds = frames = 0;

        // Replace both : and ; for parsing
        string normalized = timecode.Replace(';', ':');
        string[] parts = normalized.Split(':');

        if (parts.Length != 4)
            return false;

        return int.TryParse(parts[0], out hours) &&
               int.TryParse(parts[1], out minutes) &&
               int.TryParse(parts[2], out seconds) &&
               int.TryParse(parts[3], out frames);
    }

    private long TimecodeToMicroseconds(int hours, int minutes, int seconds, int frames, bool isDropFrame)
    {
        long totalFrames;

        if (isDropFrame && dropFrame)
        {
            // Drop-frame timecode calculation
            // NTSC drops 2 frames every minute except every 10th minute
            int totalMinutes = hours * 60 + minutes;
            int dropCount = 2 * (totalMinutes - totalMinutes / 10);

            // Calculate frame number
            long framesPerHour = (long)(3600 * frameRate);
            long framesPerMinute = (long)(60 * frameRate);
            long framesPerSecond = (long)Math.Round(frameRate);

            totalFrames = hours * framesPerHour + minutes * framesPerMinute + seconds * framesPerSecond + frames - dropCount;
        }
        else
        {
            // Non-drop-frame: simple calculation
            double secondsTotal = hours * 3600.0 + minutes * 60.0 + seconds + frames / frameRate;
            return (long)(secondsTotal * 1_000_000);
        }

        // Convert frames to microseconds
        double secondsFromFrames = totalFrames / frameRate;
        return (long)(secondsFromFrames * 1_000_000);
    }

    /// <summary>
    /// Decode CEA-608 caption data to readable text.
    /// CEA-608 data is pairs of hex bytes representing control codes and characters.
    /// </summary>
    private static string DecodeCea608(string captionData)
    {
        if (string.IsNullOrEmpty(captionData))
            return string.Empty;

        var result = new StringBuilder();
        string[] pairs = captionData.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (string pair in pairs)
        {
            if (pair.Length != 4)
                continue;

            if (!byte.TryParse(pair.Substring(0, 2), NumberStyles.HexNumber, null, out byte b1) ||
                !byte.TryParse(pair.Substring(2, 2), NumberStyles.HexNumber, null, out byte b2))
            {
                continue;
            }

            // Strip parity bits
            b1 &= 0x7F;
            b2 &= 0x7F;

            // Skip control codes (values < 0x20 are control codes when not part of extended chars)
            // Check for special characters and control codes
            if (IsControlCode(b1, b2))
            {
                // Handle specific control codes
                if (b1 == 0x14 || b1 == 0x1C) // Caption control codes
                {
                    switch (b2)
                    {
                        case 0x2D: // Carriage return
                        case 0x2E: // Erase non-displayed memory
                        case 0x2F: // End of caption (flip)
                            if (result.Length > 0 && result[^1] != '\n')
                                result.Append('\n');
                            break;
                        case 0x20: // Resume caption loading
                        case 0x25: // Roll-up 2
                        case 0x26: // Roll-up 3
                        case 0x27: // Roll-up 4
                        case 0x29: // Resume direct captioning
                            // Mode changes - ignore for text extraction
                            break;
                    }
                }
                continue;
            }

            // Standard ASCII characters
            if (b1 >= 0x20 && b1 <= 0x7F)
            {
                char c1 = MapCea608Char(b1);
                if (c1 != '\0')
                    result.Append(c1);
            }

            if (b2 >= 0x20 && b2 <= 0x7F)
            {
                char c2 = MapCea608Char(b2);
                if (c2 != '\0')
                    result.Append(c2);
            }
        }

        return result.ToString().Trim();
    }

    private static bool IsControlCode(byte b1, byte b2)
    {
        // Control codes have specific patterns
        // Mid-row codes: 0x11-0x17 or 0x19-0x1F with 0x20-0x2F
        // PAC (Preamble Address Codes): 0x11-0x17 or 0x19-0x1F with 0x40-0x7F
        // Misc control: 0x14, 0x15, 0x1C, 0x1D with 0x20-0x2F

        if (b1 >= 0x10 && b1 <= 0x1F)
            return true;

        return false;
    }

    private static char MapCea608Char(byte b)
    {
        // CEA-608 uses a modified ASCII character set
        // Most standard ASCII characters map directly
        return b switch
        {
            0x2A => 'á', // Special characters
            0x5C => 'é',
            0x5E => 'í',
            0x5F => 'ó',
            0x60 => 'ú',
            0x7B => 'ç',
            0x7C => '÷',
            0x7D => 'Ñ',
            0x7E => 'ñ',
            0x7F => '█', // Block character
            _ => b >= 0x20 && b <= 0x7E ? (char)b : '\0'
        };
    }

    /// <summary>
    /// Check if a file appears to be an SCC file.
    /// </summary>
    public static bool IsSccFile(Stream stream)
    {
        if (!stream.CanSeek)
            return false;

        long position = stream.Position;
        try
        {
            byte[] buffer = new byte[32];
            int read = stream.Read(buffer, 0, buffer.Length);
            string header = Encoding.ASCII.GetString(buffer, 0, read);
            return header.StartsWith("Scenarist_SCC V", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            stream.Position = position;
        }
    }

    /// <summary>
    /// Get the SCC format version from a file.
    /// </summary>
    public static string? GetVersion(Stream stream)
    {
        if (!stream.CanSeek)
            return null;

        long position = stream.Position;
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            string? line = reader.ReadLine();
            if (line != null && line.StartsWith("Scenarist_SCC V", StringComparison.OrdinalIgnoreCase))
            {
                return line.Substring(15).Trim();
            }
            return null;
        }
        finally
        {
            stream.Position = position;
        }
    }
}

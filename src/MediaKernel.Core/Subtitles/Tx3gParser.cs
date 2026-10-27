// MediaKernel - tx3g/3GPP Timed Text Subtitle Parser
// Parses MP4/QuickTime text track samples (tx3g format)
// Reference: 3GPP TS 26.245, ISO/IEC 14496-17

using System.Buffers.Binary;
using System.Text;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Text style flags for tx3g subtitles.
/// </summary>
[Flags]
public enum Tx3gStyleFlags
{
    /// <summary>No style.</summary>
    None = 0,
    
    /// <summary>Bold text.</summary>
    Bold = 0x01,
    
    /// <summary>Italic text.</summary>
    Italic = 0x02,
    
    /// <summary>Underlined text.</summary>
    Underline = 0x04
}

/// <summary>
/// A text style record from a tx3g 'styl' box.
/// </summary>
public readonly struct Tx3gStyleRecord
{
    /// <summary>Start character offset (0-based).</summary>
    public int StartChar { get; init; }
    
    /// <summary>End character offset (exclusive).</summary>
    public int EndChar { get; init; }
    
    /// <summary>Font ID.</summary>
    public int FontId { get; init; }
    
    /// <summary>Style flags (bold, italic, underline).</summary>
    public Tx3gStyleFlags Flags { get; init; }
    
    /// <summary>Font size in points.</summary>
    public int FontSize { get; init; }
    
    /// <summary>Text color (RGBA).</summary>
    public uint TextColor { get; init; }
    
    /// <summary>Gets the red component (0-255).</summary>
    public byte Red => (byte)((TextColor >> 24) & 0xFF);
    
    /// <summary>Gets the green component (0-255).</summary>
    public byte Green => (byte)((TextColor >> 16) & 0xFF);
    
    /// <summary>Gets the blue component (0-255).</summary>
    public byte Blue => (byte)((TextColor >> 8) & 0xFF);
    
    /// <summary>Gets the alpha component (0-255).</summary>
    public byte Alpha => (byte)(TextColor & 0xFF);
}

/// <summary>
/// Highlight color record from tx3g 'hclr' box.
/// </summary>
public readonly struct Tx3gHighlightColor
{
    /// <summary>Highlight color (RGBA).</summary>
    public uint Color { get; init; }
}

/// <summary>
/// Highlight range from tx3g 'hlit' box.
/// </summary>
public readonly struct Tx3gHighlight
{
    /// <summary>Start character.</summary>
    public int StartChar { get; init; }
    
    /// <summary>End character.</summary>
    public int EndChar { get; init; }
}

/// <summary>
/// Scroll delay from tx3g 'dlay' box.
/// </summary>
public readonly struct Tx3gScrollDelay
{
    /// <summary>Scroll delay in media timescale units.</summary>
    public int Delay { get; init; }
}

/// <summary>
/// Karaoke timing from tx3g 'krok' box.
/// </summary>
public readonly struct Tx3gKaraoke
{
    /// <summary>Highlight start time.</summary>
    public int StartTime { get; init; }
    
    /// <summary>Karaoke entries.</summary>
    public Tx3gKaraokeEntry[] Entries { get; init; }
}

/// <summary>
/// A single karaoke entry.
/// </summary>
public readonly struct Tx3gKaraokeEntry
{
    /// <summary>End time for this syllable.</summary>
    public int EndTime { get; init; }
    
    /// <summary>Start character.</summary>
    public int StartChar { get; init; }
    
    /// <summary>End character.</summary>
    public int EndChar { get; init; }
}

/// <summary>
/// Blink range from tx3g 'blnk' box.
/// </summary>
public readonly struct Tx3gBlink
{
    /// <summary>Start character.</summary>
    public int StartChar { get; init; }
    
    /// <summary>End character.</summary>
    public int EndChar { get; init; }
}

/// <summary>
/// Text wrap mode from tx3g 'twrp' box.
/// </summary>
public enum Tx3gWrapMode : byte
{
    /// <summary>No wrapping.</summary>
    None = 0,
    
    /// <summary>Automatic line wrapping.</summary>
    Automatic = 1
}

/// <summary>
/// Parsed tx3g subtitle sample.
/// </summary>
public sealed class Tx3gSample
{
    /// <summary>The subtitle text.</summary>
    public string Text { get; init; } = string.Empty;
    
    /// <summary>Style records.</summary>
    public Tx3gStyleRecord[] Styles { get; init; } = [];
    
    /// <summary>Highlight ranges.</summary>
    public Tx3gHighlight[] Highlights { get; init; } = [];
    
    /// <summary>Highlight color.</summary>
    public Tx3gHighlightColor? HighlightColor { get; init; }
    
    /// <summary>Karaoke timing.</summary>
    public Tx3gKaraoke? Karaoke { get; init; }
    
    /// <summary>Blink ranges.</summary>
    public Tx3gBlink[] Blinks { get; init; } = [];
    
    /// <summary>Scroll delay.</summary>
    public Tx3gScrollDelay? ScrollDelay { get; init; }
    
    /// <summary>Text wrap mode.</summary>
    public Tx3gWrapMode? WrapMode { get; init; }
    
    /// <summary>
    /// Converts to a plain text SubtitleCue.
    /// </summary>
    public SubtitleCue ToSubtitleCue(long startUs, long endUs)
    {
        return SubtitleCue.CreateText(startUs, endUs, Text);
    }
    
    /// <summary>
    /// Converts to HTML-formatted text with inline styles.
    /// </summary>
    public string ToHtml()
    {
        if (Styles.Length == 0)
            return EscapeHtml(Text);
        
        var sb = new StringBuilder();
        int lastEnd = 0;
        
        // Sort styles by start position
        var sortedStyles = Styles.OrderBy(s => s.StartChar).ToArray();
        
        foreach (var style in sortedStyles)
        {
            // Add unstyled text before this style
            if (style.StartChar > lastEnd && lastEnd < Text.Length)
            {
                int len = Math.Min(style.StartChar - lastEnd, Text.Length - lastEnd);
                sb.Append(EscapeHtml(Text.Substring(lastEnd, len)));
            }
            
            // Add styled text
            int startIdx = Math.Min(style.StartChar, Text.Length);
            int endIdx = Math.Min(style.EndChar, Text.Length);
            
            if (endIdx > startIdx)
            {
                string styledText = Text.Substring(startIdx, endIdx - startIdx);
                
                // Build inline style
                var styleAttrs = new List<string>();
                if ((style.Flags & Tx3gStyleFlags.Bold) != 0)
                    styleAttrs.Add("font-weight:bold");
                if ((style.Flags & Tx3gStyleFlags.Italic) != 0)
                    styleAttrs.Add("font-style:italic");
                if ((style.Flags & Tx3gStyleFlags.Underline) != 0)
                    styleAttrs.Add("text-decoration:underline");
                if (style.FontSize > 0)
                    styleAttrs.Add($"font-size:{style.FontSize}pt");
                styleAttrs.Add($"color:rgba({style.Red},{style.Green},{style.Blue},{style.Alpha / 255.0:F2})");
                
                sb.Append($"<span style=\"{string.Join(";", styleAttrs)}\">");
                sb.Append(EscapeHtml(styledText));
                sb.Append("</span>");
            }
            
            lastEnd = Math.Max(lastEnd, endIdx);
        }
        
        // Add remaining unstyled text
        if (lastEnd < Text.Length)
        {
            sb.Append(EscapeHtml(Text.Substring(lastEnd)));
        }
        
        return sb.ToString();
    }
    
    private static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\n", "<br/>");
    }
}

/// <summary>
/// Parser for tx3g/3GPP Timed Text subtitle samples.
/// Used in MP4/MOV containers for timed text tracks.
/// </summary>
public static class Tx3gParser
{
    // Box type FourCCs
    private const uint BoxStyl = 0x7374796C; // 'styl'
    private const uint BoxHlit = 0x686C6974; // 'hlit'
    private const uint BoxHclr = 0x68636C72; // 'hclr'
    private const uint BoxKrok = 0x6B726F6B; // 'krok'
    private const uint BoxDlay = 0x646C6179; // 'dlay'
    private const uint BoxDrpo = 0x6472706F; // 'drpo'
    private const uint BoxDrpt = 0x64727074; // 'drpt'
    private const uint BoxHref = 0x68726566; // 'href'
    private const uint BoxTbox = 0x74626F78; // 'tbox'
    private const uint BoxBlnk = 0x626C6E6B; // 'blnk'
    private const uint BoxTwrp = 0x74777270; // 'twrp'
    
    /// <summary>
    /// Parse a tx3g subtitle sample.
    /// </summary>
    /// <param name="data">Raw sample data from MP4 track.</param>
    /// <returns>Parsed subtitle sample.</returns>
    public static Tx3gSample Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return new Tx3gSample();
        
        // First 2 bytes: text length
        int textLength = BinaryPrimitives.ReadUInt16BigEndian(data);
        
        if (data.Length < 2 + textLength)
            return new Tx3gSample();
        
        // Extract text
        var textData = data.Slice(2, textLength);
        string text = DecodeText(textData);
        
        // Normalize line endings
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        
        // Parse modifier boxes after text
        var styles = new List<Tx3gStyleRecord>();
        var highlights = new List<Tx3gHighlight>();
        var blinks = new List<Tx3gBlink>();
        Tx3gHighlightColor? highlightColor = null;
        Tx3gKaraoke? karaoke = null;
        Tx3gScrollDelay? scrollDelay = null;
        Tx3gWrapMode? wrapMode = null;
        
        int offset = 2 + textLength;
        while (offset + 8 <= data.Length)
        {
            // Box header: 4 bytes size, 4 bytes type
            int boxSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            uint boxType = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
            
            if (boxSize < 8 || offset + boxSize > data.Length)
                break;
            
            var boxPayload = data.Slice(offset + 8, boxSize - 8);
            
            switch (boxType)
            {
                case BoxStyl:
                    ParseStyleBox(boxPayload, styles);
                    break;
                    
                case BoxHlit:
                    if (boxPayload.Length >= 4)
                    {
                        highlights.Add(new Tx3gHighlight
                        {
                            StartChar = BinaryPrimitives.ReadUInt16BigEndian(boxPayload),
                            EndChar = BinaryPrimitives.ReadUInt16BigEndian(boxPayload[2..])
                        });
                    }
                    break;
                    
                case BoxHclr:
                    if (boxPayload.Length >= 4)
                    {
                        highlightColor = new Tx3gHighlightColor
                        {
                            Color = BinaryPrimitives.ReadUInt32BigEndian(boxPayload)
                        };
                    }
                    break;
                    
                case BoxKrok:
                    karaoke = ParseKaraokeBox(boxPayload);
                    break;
                    
                case BoxDlay:
                    if (boxPayload.Length >= 4)
                    {
                        scrollDelay = new Tx3gScrollDelay
                        {
                            Delay = (int)BinaryPrimitives.ReadUInt32BigEndian(boxPayload)
                        };
                    }
                    break;
                    
                case BoxBlnk:
                    if (boxPayload.Length >= 4)
                    {
                        blinks.Add(new Tx3gBlink
                        {
                            StartChar = BinaryPrimitives.ReadUInt16BigEndian(boxPayload),
                            EndChar = BinaryPrimitives.ReadUInt16BigEndian(boxPayload[2..])
                        });
                    }
                    break;
                    
                case BoxTwrp:
                    if (boxPayload.Length >= 1)
                    {
                        wrapMode = (Tx3gWrapMode)boxPayload[0];
                    }
                    break;
            }
            
            offset += boxSize;
        }
        
        return new Tx3gSample
        {
            Text = text,
            Styles = [.. styles],
            Highlights = [.. highlights],
            HighlightColor = highlightColor,
            Karaoke = karaoke,
            Blinks = [.. blinks],
            ScrollDelay = scrollDelay,
            WrapMode = wrapMode
        };
    }
    
    private static string DecodeText(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return Encoding.UTF8.GetString(data);
        
        // Check for UTF-16 BOM
        if (data[0] == 0xFE && data[1] == 0xFF)
        {
            // UTF-16 Big Endian
            return Encoding.BigEndianUnicode.GetString(data[2..]);
        }
        else if (data[0] == 0xFF && data[1] == 0xFE)
        {
            // UTF-16 Little Endian
            return Encoding.Unicode.GetString(data[2..]);
        }
        
        // Default: UTF-8
        return Encoding.UTF8.GetString(data);
    }
    
    private static void ParseStyleBox(ReadOnlySpan<byte> payload, List<Tx3gStyleRecord> styles)
    {
        if (payload.Length < 2)
            return;
        
        int recordCount = BinaryPrimitives.ReadUInt16BigEndian(payload);
        int offset = 2;
        
        for (int i = 0; i < recordCount && offset + 12 <= payload.Length; i++)
        {
            var record = new Tx3gStyleRecord
            {
                StartChar = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]),
                EndChar = BinaryPrimitives.ReadUInt16BigEndian(payload[(offset + 2)..]),
                FontId = BinaryPrimitives.ReadUInt16BigEndian(payload[(offset + 4)..]),
                Flags = (Tx3gStyleFlags)payload[offset + 6],
                FontSize = payload[offset + 7],
                TextColor = BinaryPrimitives.ReadUInt32BigEndian(payload[(offset + 8)..])
            };
            
            styles.Add(record);
            offset += 12;
        }
    }
    
    private static Tx3gKaraoke? ParseKaraokeBox(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6)
            return null;
        
        int startTime = (int)BinaryPrimitives.ReadUInt32BigEndian(payload);
        int entryCount = BinaryPrimitives.ReadUInt16BigEndian(payload[4..]);
        
        var entries = new List<Tx3gKaraokeEntry>();
        int offset = 6;
        
        for (int i = 0; i < entryCount && offset + 8 <= payload.Length; i++)
        {
            var entry = new Tx3gKaraokeEntry
            {
                EndTime = (int)BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]),
                StartChar = BinaryPrimitives.ReadUInt16BigEndian(payload[(offset + 4)..]),
                EndChar = BinaryPrimitives.ReadUInt16BigEndian(payload[(offset + 6)..])
            };
            
            entries.Add(entry);
            offset += 8;
        }
        
        return new Tx3gKaraoke
        {
            StartTime = startTime,
            Entries = [.. entries]
        };
    }
    
    /// <summary>
    /// Probe data to check if it looks like a tx3g sample.
    /// </summary>
    public static bool Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return false;
        
        int textLength = BinaryPrimitives.ReadUInt16BigEndian(data);
        
        // Text length should be reasonable
        if (textLength > data.Length - 2 || textLength > 10000)
            return false;
        
        // If there's additional data, check for valid box headers
        int offset = 2 + textLength;
        if (offset + 8 <= data.Length)
        {
            int boxSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            uint boxType = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
            
            // Box size should be reasonable
            if (boxSize < 8 || boxSize > data.Length - offset)
                return false;
            
            // Check for known box types
            return boxType switch
            {
                BoxStyl or BoxHlit or BoxHclr or BoxKrok or 
                BoxDlay or BoxBlnk or BoxTwrp => true,
                _ => textLength > 0 // Accept if there's just text
            };
        }
        
        // Just text is valid
        return textLength > 0 || data.Length == 2;
    }
}

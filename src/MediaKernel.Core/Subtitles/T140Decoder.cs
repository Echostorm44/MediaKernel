// MediaKernel - T.140 Real-Time Text Parser
// ITU-T T.140 text decoder for real-time text subtitles (RFC 4103)
//
// T.140 is a simple UTF-8 text protocol used for:
// - Real-time text in VoIP/RTP (RFC 4103)
// - Accessibility features (hearing impaired)
// - Live captioning

using System.Text;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// T.140 control characters and protocol constants.
/// </summary>
public static class T140Control
{
    /// <summary>Backspace (erase last character).</summary>
    public const char Backspace = '\x08';
    
    /// <summary>Line separator (new line).</summary>
    public const char LineSeparator = '\u2028';
    
    /// <summary>Bell (alert).</summary>
    public const char Bell = '\x07';
    
    /// <summary>Escape (start of control sequence).</summary>
    public const char Escape = '\x1B';
    
    /// <summary>SOS (Start of String).</summary>
    public const char StartOfString = '\x98';
    
    /// <summary>ST (String Terminator).</summary>
    public const char StringTerminator = '\x9C';
    
    /// <summary>SGR (Select Graphic Rendition) - for text formatting.</summary>
    public const char SelectGraphicRendition = 'm';
    
    /// <summary>INT (Interrupt).</summary>
    public const char Interrupt = '\xA1';
    
    /// <summary>BOM (Byte Order Mark) for UTF-8.</summary>
    public const char ByteOrderMark = '\uFEFF';
}

/// <summary>
/// A T.140 text block (received packet).
/// </summary>
public readonly struct T140Block
{
    /// <summary>Presentation timestamp in microseconds.</summary>
    public long TimestampMicroseconds { get; init; }
    
    /// <summary>The text content.</summary>
    public string Text { get; init; }
    
    /// <summary>Whether this block ends the current subtitle.</summary>
    public bool IsEndOfSubtitle { get; init; }
    
    /// <summary>Sequence number (for ordering).</summary>
    public int SequenceNumber { get; init; }
}

/// <summary>
/// State machine for accumulating T.140 real-time text.
/// </summary>
public sealed class T140Accumulator
{
    private readonly StringBuilder textBuffer = new();
    private int lastSequenceNumber = -1;
    
    /// <summary>Gets the current accumulated text.</summary>
    public string CurrentText => textBuffer.ToString();
    
    /// <summary>Gets whether the buffer is empty.</summary>
    public bool IsEmpty => textBuffer.Length == 0;
    
    /// <summary>
    /// Processes an incoming T.140 block.
    /// </summary>
    /// <returns>True if the subtitle should be displayed.</returns>
    public bool ProcessBlock(T140Block block)
    {
        // Check for out-of-order packets
        if (block.SequenceNumber > 0 && lastSequenceNumber >= 0)
        {
            if (block.SequenceNumber <= lastSequenceNumber)
                return false; // Duplicate or old packet
        }
        lastSequenceNumber = block.SequenceNumber;
        
        foreach (char c in block.Text)
        {
            ProcessCharacter(c);
        }
        
        return true;
    }
    
    /// <summary>
    /// Processes a single character according to T.140 rules.
    /// </summary>
    public void ProcessCharacter(char c)
    {
        switch (c)
        {
            case T140Control.Backspace:
                // Erase last character
                if (textBuffer.Length > 0)
                    textBuffer.Length--;
                break;
                
            case T140Control.LineSeparator:
            case '\n':
                textBuffer.Append('\n');
                break;
                
            case T140Control.ByteOrderMark:
                // Ignore BOM
                break;
                
            case T140Control.Bell:
                // Ignore bell
                break;
                
            case T140Control.Escape:
                // Start of escape sequence - ignore for now
                break;
                
            default:
                if (!char.IsControl(c) || c == '\t')
                {
                    textBuffer.Append(c);
                }
                break;
        }
    }
    
    /// <summary>
    /// Clears the accumulated text.
    /// </summary>
    public void Clear()
    {
        textBuffer.Clear();
        lastSequenceNumber = -1;
    }
    
    /// <summary>
    /// Creates a subtitle cue from the current accumulated text.
    /// </summary>
    public SubtitleCue CreateCue(long startMicroseconds, long endMicroseconds)
    {
        return new SubtitleCue(
            startMicroseconds,
            endMicroseconds,
            CurrentText,
            SubtitleType.Text);
    }
}

/// <summary>
/// Decoder for T.140 real-time text protocol.
/// </summary>
public static class T140Decoder
{
    /// <summary>
    /// Decodes T.140 data from a byte array.
    /// </summary>
    public static T140Block Decode(ReadOnlySpan<byte> data, long timestampMicroseconds, int sequenceNumber = 0)
    {
        // T.140 is always UTF-8
        string text = Encoding.UTF8.GetString(data);
        
        // Check for end-of-subtitle marker (empty text or special sequence)
        bool isEnd = text.Length == 0 || 
                     text.Contains(T140Control.StringTerminator) ||
                     text.EndsWith("\n\n", StringComparison.Ordinal);
        
        return new T140Block
        {
            TimestampMicroseconds = timestampMicroseconds,
            Text = text,
            IsEndOfSubtitle = isEnd,
            SequenceNumber = sequenceNumber
        };
    }
    
    /// <summary>
    /// Decodes T.140 data from a stream.
    /// </summary>
    public static T140Block Decode(Stream stream, long timestampMicroseconds, int sequenceNumber = 0)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string text = reader.ReadToEnd();
        
        bool isEnd = text.Length == 0 || 
                     text.Contains(T140Control.StringTerminator) ||
                     text.EndsWith("\n\n", StringComparison.Ordinal);
        
        return new T140Block
        {
            TimestampMicroseconds = timestampMicroseconds,
            Text = text,
            IsEndOfSubtitle = isEnd,
            SequenceNumber = sequenceNumber
        };
    }
    
    /// <summary>
    /// Encodes text for T.140 transmission.
    /// </summary>
    public static byte[] Encode(string text)
    {
        // T.140 uses UTF-8 encoding
        return Encoding.UTF8.GetBytes(text);
    }
    
    /// <summary>
    /// Encodes text with redundancy for T.140 RTP (RFC 4103).
    /// </summary>
    public static byte[] EncodeWithRedundancy(string text, string? previousText = null)
    {
        // Simple implementation - in real RTP, would add redundancy header
        return Encode(text);
    }
    
    /// <summary>
    /// Strips T.140 control characters from text for display.
    /// </summary>
    public static string StripControlCharacters(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (!char.IsControl(c) || c == '\n' || c == '\t')
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
    
    /// <summary>
    /// Probes data to check if it's valid T.140.
    /// </summary>
    public static bool Probe(ReadOnlySpan<byte> data)
    {
        // T.140 is UTF-8 text - check if it's valid UTF-8
        try
        {
            _ = Encoding.UTF8.GetString(data);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

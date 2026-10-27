// Subtitle Types - Core data structures for subtitle handling
// Supports text-based (SRT, WebVTT, ASS) and bitmap-based (PGS, VOBSUB) subtitles

using System.Runtime.InteropServices;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Represents a single subtitle cue (text or bitmap).
/// </summary>
public readonly struct SubtitleCue
{
    /// <summary>Start time in microseconds (from media start).</summary>
    public readonly long StartTimeMicroseconds;
    
    /// <summary>End time in microseconds.</summary>
    public readonly long EndTimeMicroseconds;
    
    /// <summary>Subtitle text (for text-based subtitles).</summary>
    public readonly string Text;
    
    /// <summary>Style name (for ASS/SSA).</summary>
    public readonly string? StyleName;
    
    /// <summary>Position and layout settings.</summary>
    public readonly SubtitlePosition Position;
    
    /// <summary>Bitmap data (for PGS/VOBSUB, null for text).</summary>
    public readonly SubtitleBitmap? Bitmap;
    
    /// <summary>Type of subtitle.</summary>
    public readonly SubtitleType Type;
    
    /// <summary>Duration in microseconds.</summary>
    public long DurationMicroseconds => EndTimeMicroseconds - StartTimeMicroseconds;
    
    /// <summary>Start time as TimeSpan.</summary>
    public TimeSpan StartTime => TimeSpan.FromTicks(StartTimeMicroseconds * 10);
    
    /// <summary>End time as TimeSpan.</summary>
    public TimeSpan EndTime => TimeSpan.FromTicks(EndTimeMicroseconds * 10);
    
    public SubtitleCue(
        long startTimeMicroseconds,
        long endTimeMicroseconds,
        string text,
        SubtitleType type = SubtitleType.Text,
        string? styleName = null,
        SubtitlePosition position = default,
        SubtitleBitmap? bitmap = null)
    {
        StartTimeMicroseconds = startTimeMicroseconds;
        EndTimeMicroseconds = endTimeMicroseconds;
        Text = text;
        Type = type;
        StyleName = styleName;
        Position = position;
        Bitmap = bitmap;
    }
    
    /// <summary>
    /// Creates a text subtitle cue.
    /// </summary>
    public static SubtitleCue CreateText(long startUs, long endUs, string text)
        => new(startUs, endUs, text, SubtitleType.Text);
    
    /// <summary>
    /// Creates a styled subtitle cue (ASS/SSA).
    /// </summary>
    public static SubtitleCue CreateStyled(long startUs, long endUs, string text, string styleName)
        => new(startUs, endUs, text, SubtitleType.StyledText, styleName);
    
    /// <summary>
    /// Creates a bitmap subtitle cue (PGS/VOBSUB).
    /// </summary>
    public static SubtitleCue CreateBitmap(long startUs, long endUs, SubtitleBitmap bitmap)
        => new(startUs, endUs, string.Empty, SubtitleType.Bitmap, null, default, bitmap);
}

/// <summary>
/// Type of subtitle content.
/// </summary>
public enum SubtitleType
{
    /// <summary>Plain text (SRT).</summary>
    Text,
    /// <summary>Styled text with formatting (ASS/SSA, WebVTT).</summary>
    StyledText,
    /// <summary>Bitmap/image (PGS, VOBSUB).</summary>
    Bitmap
}

/// <summary>
/// Subtitle positioning information.
/// </summary>
public readonly struct SubtitlePosition
{
    /// <summary>Horizontal position (0-100, percentage of screen width).</summary>
    public readonly float HorizontalPercent;
    
    /// <summary>Vertical position (0-100, percentage of screen height).</summary>
    public readonly float VerticalPercent;
    
    /// <summary>Horizontal alignment.</summary>
    public readonly SubtitleAlignment HorizontalAlignment;
    
    /// <summary>Vertical alignment.</summary>
    public readonly SubtitleVerticalAlignment VerticalAlignment;
    
    /// <summary>Default bottom-center position.</summary>
    public static SubtitlePosition Default => new(50f, 95f, 
        SubtitleAlignment.Center, SubtitleVerticalAlignment.Bottom);
    
    public SubtitlePosition(
        float horizontalPercent,
        float verticalPercent,
        SubtitleAlignment horizontalAlignment,
        SubtitleVerticalAlignment verticalAlignment)
    {
        HorizontalPercent = horizontalPercent;
        VerticalPercent = verticalPercent;
        HorizontalAlignment = horizontalAlignment;
        VerticalAlignment = verticalAlignment;
    }
}

/// <summary>
/// Horizontal text alignment.
/// </summary>
public enum SubtitleAlignment
{
    Left,
    Center,
    Right
}

/// <summary>
/// Vertical text alignment.
/// </summary>
public enum SubtitleVerticalAlignment
{
    Top,
    Middle,
    Bottom
}

/// <summary>
/// Bitmap subtitle image data.
/// </summary>
public sealed class SubtitleBitmap
{
    /// <summary>Image width in pixels.</summary>
    public int Width { get; }
    
    /// <summary>Image height in pixels.</summary>
    public int Height { get; }
    
    /// <summary>X position on screen (pixels from left).</summary>
    public int PositionX { get; }
    
    /// <summary>Y position on screen (pixels from top).</summary>
    public int PositionY { get; }
    
    /// <summary>Pixel data (RGBA8888 format).</summary>
    public byte[] PixelData { get; }
    
    /// <summary>Stride (bytes per row).</summary>
    public int Stride => Width * 4;
    
    public SubtitleBitmap(int width, int height, int posX, int posY, byte[] pixelData)
    {
        Width = width;
        Height = height;
        PositionX = posX;
        PositionY = posY;
        PixelData = pixelData;
    }
}

/// <summary>
/// ASS/SSA style definition.
/// </summary>
public sealed class SubtitleStyle
{
    /// <summary>Style name (e.g., "Default", "Italic").</summary>
    public string Name { get; init; } = "Default";
    
    /// <summary>Font name.</summary>
    public string FontName { get; init; } = "Arial";
    
    /// <summary>Font size in pixels.</summary>
    public float FontSize { get; init; } = 48f;
    
    /// <summary>Primary text color (ARGB).</summary>
    public uint PrimaryColor { get; init; } = 0xFFFFFFFF;
    
    /// <summary>Secondary color (for karaoke).</summary>
    public uint SecondaryColor { get; init; } = 0xFF00FFFF;
    
    /// <summary>Outline color.</summary>
    public uint OutlineColor { get; init; } = 0xFF000000;
    
    /// <summary>Shadow color.</summary>
    public uint BackColor { get; init; } = 0x80000000;
    
    /// <summary>Bold text.</summary>
    public bool Bold { get; init; }
    
    /// <summary>Italic text.</summary>
    public bool Italic { get; init; }
    
    /// <summary>Underline text.</summary>
    public bool Underline { get; init; }
    
    /// <summary>Strikeout text.</summary>
    public bool Strikeout { get; init; }
    
    /// <summary>Outline width in pixels.</summary>
    public float OutlineWidth { get; init; } = 2f;
    
    /// <summary>Shadow depth in pixels.</summary>
    public float ShadowDepth { get; init; } = 2f;
    
    /// <summary>Horizontal margin (pixels from edge).</summary>
    public int MarginLeft { get; init; } = 10;
    
    /// <summary>Horizontal margin (pixels from edge).</summary>
    public int MarginRight { get; init; } = 10;
    
    /// <summary>Vertical margin (pixels from edge).</summary>
    public int MarginVertical { get; init; } = 10;
    
    /// <summary>Text alignment (numpad style: 1-9).</summary>
    public int Alignment { get; init; } = 2; // Bottom-center
    
    /// <summary>Border style (1=outline+shadow, 3=opaque box).</summary>
    public int BorderStyle { get; init; } = 1;
    
    /// <summary>Default style.</summary>
    public static SubtitleStyle Default => new();
}

/// <summary>
/// Parsed subtitle track containing all cues.
/// </summary>
public sealed class SubtitleTrack
{
    /// <summary>Track title/name.</summary>
    public string Title { get; set; } = string.Empty;
    
    /// <summary>Language code (e.g., "en", "ja").</summary>
    public string Language { get; set; } = string.Empty;
    
    /// <summary>Subtitle format.</summary>
    public SubtitleFormat Format { get; set; }
    
    /// <summary>All cues in the track, sorted by start time.</summary>
    public IReadOnlyList<SubtitleCue> Cues => cues;
    
    /// <summary>Style definitions (for ASS/SSA).</summary>
    public IReadOnlyDictionary<string, SubtitleStyle> Styles => styles;
    
    /// <summary>Video resolution for positioning (ASS/SSA).</summary>
    public (int Width, int Height) PlayResolution { get; set; } = (0, 0);
    
    private readonly List<SubtitleCue> cues = [];
    private readonly Dictionary<string, SubtitleStyle> styles = [];
    
    /// <summary>
    /// Adds a cue to the track.
    /// </summary>
    public void AddCue(SubtitleCue cue)
    {
        // Insert in sorted order by start time
        int index = cues.BinarySearch(cue, SubtitleCueComparer.Instance);
        if (index < 0)
            index = ~index;
        cues.Insert(index, cue);
    }
    
    /// <summary>
    /// Adds a style definition.
    /// </summary>
    public void AddStyle(SubtitleStyle style)
    {
        styles[style.Name] = style;
    }
    
    /// <summary>
    /// Gets cues active at the specified time.
    /// </summary>
    public IEnumerable<SubtitleCue> GetCuesAtTime(long timeMicroseconds)
    {
        foreach (var cue in cues)
        {
            if (cue.StartTimeMicroseconds <= timeMicroseconds && 
                cue.EndTimeMicroseconds > timeMicroseconds)
            {
                yield return cue;
            }
            else if (cue.StartTimeMicroseconds > timeMicroseconds)
            {
                // Past the target time, no more matches possible
                break;
            }
        }
    }
    
    /// <summary>
    /// Gets the next cue after the specified time.
    /// </summary>
    public SubtitleCue? GetNextCue(long timeMicroseconds)
    {
        foreach (var cue in cues)
        {
            if (cue.StartTimeMicroseconds > timeMicroseconds)
                return cue;
        }
        return null;
    }
    
    private sealed class SubtitleCueComparer : IComparer<SubtitleCue>
    {
        public static readonly SubtitleCueComparer Instance = new();
        
        public int Compare(SubtitleCue x, SubtitleCue y)
            => x.StartTimeMicroseconds.CompareTo(y.StartTimeMicroseconds);
    }
}

/// <summary>
/// Subtitle file formats.
/// </summary>
public enum SubtitleFormat
{
    /// <summary>Unknown format.</summary>
    Unknown,
    /// <summary>SubRip (.srt).</summary>
    Srt,
    /// <summary>WebVTT (.vtt).</summary>
    WebVtt,
    /// <summary>Advanced SubStation Alpha (.ass).</summary>
    Ass,
    /// <summary>SubStation Alpha (.ssa).</summary>
    Ssa,
    /// <summary>Blu-ray Presentation Graphics Stream.</summary>
    Pgs,
    /// <summary>DVD VOBSUB.</summary>
    VobSub,
    /// <summary>Universal Subtitle Format (.usf).</summary>
    Usf,
    /// <summary>Kate overlay codec (Ogg/karaoke).</summary>
    Kate,
    /// <summary>ARIB STD-B24 (Japanese broadcast, ISDB).</summary>
    Arib
}

/// <summary>
/// Timestamp utilities for subtitle parsing.
/// </summary>
public static class SubtitleTimestamp
{
    /// <summary>
    /// Parses an SRT timestamp (hh:mm:ss,mmm).
    /// </summary>
    public static bool TryParseSrt(ReadOnlySpan<char> text, out long microseconds)
    {
        microseconds = 0;
        text = text.Trim();
        
        // Format: hh:mm:ss,mmm or hh:mm:ss.mmm
        // Minimum length: 12 characters (00:00:00,000)
        if (text.Length < 12)
            return false;
        
        // Parse hours
        if (!TryParseInt(text[..2], out int hours))
            return false;
        
        if (text[2] != ':')
            return false;
        
        // Parse minutes
        if (!TryParseInt(text.Slice(3, 2), out int minutes))
            return false;
        
        if (text[5] != ':')
            return false;
        
        // Parse seconds
        if (!TryParseInt(text.Slice(6, 2), out int seconds))
            return false;
        
        // Separator can be comma or period
        char sep = text[8];
        if (sep != ',' && sep != '.')
            return false;
        
        // Parse milliseconds (3 digits)
        if (!TryParseInt(text.Slice(9, 3), out int millis))
            return false;
        
        // Convert to microseconds
        microseconds = (long)hours * 3_600_000_000L +
                       (long)minutes * 60_000_000L +
                       (long)seconds * 1_000_000L +
                       (long)millis * 1_000L;
        
        return true;
    }
    
    /// <summary>
    /// Parses a WebVTT timestamp (hh:mm:ss.mmm or mm:ss.mmm).
    /// </summary>
    public static bool TryParseWebVtt(ReadOnlySpan<char> text, out long microseconds)
    {
        microseconds = 0;
        text = text.Trim();
        
        // WebVTT allows optional hours: mm:ss.mmm or hh:mm:ss.mmm
        int colonCount = 0;
        foreach (char c in text)
        {
            if (c == ':')
                colonCount++;
        }
        
        int hours = 0;
        int parseOffset = 0;
        
        if (colonCount == 2)
        {
            // Has hours
            int firstColon = text.IndexOf(':');
            if (!TryParseInt(text[..firstColon], out hours))
                return false;
            parseOffset = firstColon + 1;
        }
        
        var remaining = text[parseOffset..];
        
        // Parse minutes
        int colonPos = remaining.IndexOf(':');
        if (colonPos < 0)
            return false;
        
        if (!TryParseInt(remaining[..colonPos], out int minutes))
            return false;
        
        remaining = remaining[(colonPos + 1)..];
        
        // Parse seconds and milliseconds
        int dotPos = remaining.IndexOf('.');
        if (dotPos < 0)
            return false;
        
        if (!TryParseInt(remaining[..dotPos], out int seconds))
            return false;
        
        var millisStr = remaining[(dotPos + 1)..];
        if (!TryParseInt(millisStr, out int millis))
            return false;
        
        // Handle different millisecond precisions (1-3 digits)
        if (millisStr.Length == 1)
            millis *= 100;
        else if (millisStr.Length == 2)
            millis *= 10;
        
        microseconds = (long)hours * 3_600_000_000L +
                       (long)minutes * 60_000_000L +
                       (long)seconds * 1_000_000L +
                       (long)millis * 1_000L;
        
        return true;
    }
    
    /// <summary>
    /// Parses an ASS/SSA timestamp (h:mm:ss.cc).
    /// </summary>
    public static bool TryParseAss(ReadOnlySpan<char> text, out long microseconds)
    {
        microseconds = 0;
        text = text.Trim();
        
        // Format: h:mm:ss.cc (1 hour digit, centiseconds)
        // Minimum length: 10 characters (0:00:00.00)
        if (text.Length < 10)
            return false;
        
        // Parse hours (1+ digits)
        int firstColon = text.IndexOf(':');
        if (firstColon < 1)
            return false;
        
        if (!TryParseInt(text[..firstColon], out int hours))
            return false;
        
        var remaining = text[(firstColon + 1)..];
        
        // Parse minutes (2 digits)
        if (remaining.Length < 2 || remaining[2] != ':')
            return false;
        
        if (!TryParseInt(remaining[..2], out int minutes))
            return false;
        
        remaining = remaining[3..];
        
        // Parse seconds (2 digits)
        if (remaining.Length < 2 || remaining[2] != '.')
            return false;
        
        if (!TryParseInt(remaining[..2], out int seconds))
            return false;
        
        remaining = remaining[3..];
        
        // Parse centiseconds (2 digits)
        if (remaining.Length < 2)
            return false;
        
        if (!TryParseInt(remaining[..2], out int centis))
            return false;
        
        microseconds = (long)hours * 3_600_000_000L +
                       (long)minutes * 60_000_000L +
                       (long)seconds * 1_000_000L +
                       (long)centis * 10_000L;
        
        return true;
    }
    
    private static bool TryParseInt(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        foreach (char c in text)
        {
            if (c < '0' || c > '9')
                return false;
            value = value * 10 + (c - '0');
        }
        return text.Length > 0;
    }
}

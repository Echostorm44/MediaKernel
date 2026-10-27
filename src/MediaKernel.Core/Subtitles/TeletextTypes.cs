// Teletext Types
// ETSI EN 300 706 - Enhanced Teletext specification
// Level 1.5 subtitle support for legacy European broadcasts

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Teletext national character set identifiers.
/// </summary>
public enum TeletextCharacterSet : byte
{
    English = 0,
    French = 1,
    SwedishFinnishHungarian = 2,
    CzechSlovak = 3,
    German = 4,
    PortugueseSpanish = 5,
    Italian = 6,
    Romanian = 7
}

/// <summary>
/// Teletext page flags from packet header.
/// </summary>
[Flags]
public enum TeletextPageFlags : uint
{
    None = 0,
    
    /// <summary>Erase current page before displaying new content.</summary>
    ErasePage = 0x000080,
    
    /// <summary>Page contains subtitle content.</summary>
    Subtitle = 0x008000,
    
    /// <summary>Don't display header row.</summary>
    SuppressHeader = 0x010000,
    
    /// <summary>Page content has been updated.</summary>
    Update = 0x020000,
    
    /// <summary>Don't display the page.</summary>
    InhibitDisplay = 0x080000,
    
    /// <summary>Magazine serial mode (affects header suppression).</summary>
    MagazineSerial = 0x100000
}

/// <summary>
/// A decoded teletext subtitle cue.
/// </summary>
public readonly struct TeletextSubtitleCue
{
    /// <summary>Page number (e.g., 888 for subtitles).</summary>
    public readonly int PageNumber;
    
    /// <summary>Magazine number (1-8).</summary>
    public readonly int Magazine;
    
    /// <summary>Presentation timestamp in microseconds.</summary>
    public readonly long PresentationTimeMicroseconds;
    
    /// <summary>Decoded text lines (up to 24).</summary>
    public readonly string[] Lines;
    
    /// <summary>Whether the page should be erased before display.</summary>
    public readonly bool ErasePage;
    
    public TeletextSubtitleCue(int pageNumber, int magazine, long pts, string[] lines, bool erasePage)
    {
        PageNumber = pageNumber;
        Magazine = magazine;
        PresentationTimeMicroseconds = pts;
        Lines = lines;
        ErasePage = erasePage;
    }
}

/// <summary>
/// State for a single teletext magazine.
/// </summary>
internal sealed class TeletextMagazineState
{
    public int CurrentPage { get; set; }
    public TeletextCharacterSet CharacterSet { get; set; } = TeletextCharacterSet.English;
    public bool IsSubtitle { get; set; }
    public readonly string[] Lines = new string[32];
    
    public void Clear()
    {
        CurrentPage = 0;
        IsSubtitle = false;
        Array.Clear(Lines);
    }
}

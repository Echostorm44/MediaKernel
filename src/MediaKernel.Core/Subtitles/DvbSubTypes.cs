// DVB Subtitle Types
// EN 300 743 - Digital Video Broadcasting (DVB) subtitling systems

using System.Runtime.InteropServices;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// DVB subtitle segment types.
/// </summary>
public enum DvbSubSegmentType : byte
{
    /// <summary>Page Composition Segment - defines which regions compose the page.</summary>
    PageComposition = 0x10,
    
    /// <summary>Region Composition Segment - defines region dimensions and object placement.</summary>
    RegionComposition = 0x11,
    
    /// <summary>CLUT Definition Segment - color lookup table for palette.</summary>
    ClutDefinition = 0x12,
    
    /// <summary>Object Data Segment - contains RLE-compressed bitmap or characters.</summary>
    ObjectData = 0x13,
    
    /// <summary>Display Definition Segment - canvas dimensions and windowing.</summary>
    DisplayDefinition = 0x14,
    
    /// <summary>Alternate CLUT Segment - extended CLUT with HDR support.</summary>
    AlternateClut = 0x16,
    
    /// <summary>End of Display Set - marks end of subtitle epoch.</summary>
    EndOfDisplay = 0x80,
    
    /// <summary>Stuffing - padding/unused data.</summary>
    Stuffing = 0xFF
}

/// <summary>
/// DVB page state for change detection.
/// </summary>
public enum DvbSubPageState : byte
{
    /// <summary>Normal case - acquisition point or mode change.</summary>
    Normal = 0x00,
    
    /// <summary>Acquisition point - initial/full page data.</summary>
    AcquisitionPoint = 0x01,
    
    /// <summary>Mode change - epoch boundary, clears all buffers.</summary>
    ModeChange = 0x02,
    
    /// <summary>Reserved.</summary>
    Reserved = 0x03
}

/// <summary>
/// DVB object type for rendering.
/// </summary>
public enum DvbSubObjectType : byte
{
    /// <summary>Basic object - bitmap graphic.</summary>
    BasicBitmap = 0x00,
    
    /// <summary>Basic character - text glyph.</summary>
    BasicCharacter = 0x01,
    
    /// <summary>Composite string.</summary>
    CompositeString = 0x02,
    
    /// <summary>Reserved.</summary>
    Reserved = 0x03
}

/// <summary>
/// DVB object coding method.
/// </summary>
public enum DvbSubObjectCoding : byte
{
    /// <summary>Pixels coded using 2-bit/pixel code string.</summary>
    Pixels = 0x00,
    
    /// <summary>Coded as a string of characters.</summary>
    String = 0x01
}

/// <summary>
/// DVB region depth (bits per pixel).
/// </summary>
public enum DvbSubRegionDepth : byte
{
    /// <summary>2 bits per pixel (4 colors).</summary>
    Bpp2 = 1,
    
    /// <summary>4 bits per pixel (16 colors).</summary>
    Bpp4 = 2,
    
    /// <summary>8 bits per pixel (256 colors).</summary>
    Bpp8 = 3
}

/// <summary>
/// A 32-bit ARGB color entry in a CLUT.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct DvbSubColor
{
    public readonly byte B;
    public readonly byte G;
    public readonly byte R;
    public readonly byte A;
    
    public DvbSubColor(byte r, byte g, byte b, byte a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }
    
    /// <summary>
    /// Creates a color from YCbCr and transparency.
    /// </summary>
    public static DvbSubColor FromYCbCrT(byte y, byte cb, byte cr, byte t)
    {
        // ITU-R BT.601 YCbCr to RGB conversion
        int yOffset = y - 16;
        int cbOffset = cb - 128;
        int crOffset = cr - 128;
        
        int r = (298 * yOffset + 409 * crOffset + 128) >> 8;
        int g = (298 * yOffset - 100 * cbOffset - 208 * crOffset + 128) >> 8;
        int b = (298 * yOffset + 516 * cbOffset + 128) >> 8;
        
        // Transparency: 0 = fully transparent, 255 = fully opaque (opposite of DVB)
        byte alpha = (byte)(255 - t);
        
        return new DvbSubColor(
            (byte)Math.Clamp(r, 0, 255),
            (byte)Math.Clamp(g, 0, 255),
            (byte)Math.Clamp(b, 0, 255),
            alpha);
    }
    
    /// <summary>Fully transparent black.</summary>
    public static readonly DvbSubColor Transparent = new(0, 0, 0, 0);
    
    /// <summary>Fully opaque black.</summary>
    public static readonly DvbSubColor Black = new(0, 0, 0, 255);
}

/// <summary>
/// Color Lookup Table for DVB subtitles (up to 256 entries).
/// </summary>
public sealed class DvbSubClut
{
    public byte Id { get; set; }
    public byte Version { get; set; }
    
    /// <summary>2-bit palette (4 colors).</summary>
    public readonly DvbSubColor[] Palette2Bit = new DvbSubColor[4];
    
    /// <summary>4-bit palette (16 colors).</summary>
    public readonly DvbSubColor[] Palette4Bit = new DvbSubColor[16];
    
    /// <summary>8-bit palette (256 colors).</summary>
    public readonly DvbSubColor[] Palette8Bit = new DvbSubColor[256];
    
    public DvbSubClut(byte id)
    {
        Id = id;
        InitializeDefaultPalettes();
    }
    
    private void InitializeDefaultPalettes()
    {
        // Default 2-bit palette (transparent, white, black, gray)
        Palette2Bit[0] = DvbSubColor.Transparent;
        Palette2Bit[1] = new DvbSubColor(255, 255, 255, 255);
        Palette2Bit[2] = DvbSubColor.Black;
        Palette2Bit[3] = new DvbSubColor(128, 128, 128, 255);
        
        // Default 4-bit and 8-bit palettes use first 4 entries same as 2-bit
        for (int i = 0; i < 4; i++)
            Palette4Bit[i] = Palette2Bit[i];
        for (int i = 0; i < 4; i++)
            Palette8Bit[i] = Palette2Bit[i];
    }
    
    /// <summary>
    /// Gets the appropriate palette for a given region depth.
    /// </summary>
    public DvbSubColor[] GetPalette(DvbSubRegionDepth depth) => depth switch
    {
        DvbSubRegionDepth.Bpp2 => Palette2Bit,
        DvbSubRegionDepth.Bpp4 => Palette4Bit,
        DvbSubRegionDepth.Bpp8 => Palette8Bit,
        _ => Palette4Bit
    };
}

/// <summary>
/// An object reference within a region.
/// </summary>
public readonly struct DvbSubObjectRef
{
    public readonly ushort ObjectId;
    public readonly DvbSubObjectType Type;
    public readonly byte ForegroundCode;
    public readonly byte BackgroundCode;
    public readonly ushort HorizontalPosition;
    public readonly ushort VerticalPosition;
    
    public DvbSubObjectRef(ushort objectId, DvbSubObjectType type, byte fg, byte bg, ushort x, ushort y)
    {
        ObjectId = objectId;
        Type = type;
        ForegroundCode = fg;
        BackgroundCode = bg;
        HorizontalPosition = x;
        VerticalPosition = y;
    }
}

/// <summary>
/// A region containing objects to render.
/// </summary>
public sealed class DvbSubRegion
{
    public byte Id { get; set; }
    public byte Version { get; set; }
    public ushort Width { get; set; }
    public ushort Height { get; set; }
    public DvbSubRegionDepth Depth { get; set; }
    public byte ClutId { get; set; }
    public byte BackgroundPixelCode { get; set; }
    
    /// <summary>Object references within this region.</summary>
    public List<DvbSubObjectRef> Objects { get; } = [];
    
    /// <summary>Pixel buffer (palette indices).</summary>
    public byte[]? PixelBuffer { get; set; }
    
    public DvbSubRegion(byte id) => Id = id;
    
    /// <summary>
    /// Ensures pixel buffer is allocated for region dimensions.
    /// </summary>
    public void EnsurePixelBuffer()
    {
        int size = Width * Height;
        if (PixelBuffer == null || PixelBuffer.Length < size)
        {
            PixelBuffer = new byte[size];
        }
        Array.Fill(PixelBuffer, BackgroundPixelCode, 0, size);
    }
}

/// <summary>
/// A region definition on a page (position and ID).
/// </summary>
public readonly struct DvbSubRegionDef
{
    public readonly byte RegionId;
    public readonly ushort HorizontalAddress;
    public readonly ushort VerticalAddress;
    
    public DvbSubRegionDef(byte id, ushort x, ushort y)
    {
        RegionId = id;
        HorizontalAddress = x;
        VerticalAddress = y;
    }
}

/// <summary>
/// A page composition containing region placements.
/// </summary>
public sealed class DvbSubPage
{
    public byte TimeoutSeconds { get; set; }
    public byte Version { get; set; }
    public DvbSubPageState State { get; set; }
    
    /// <summary>Regions placed on this page.</summary>
    public List<DvbSubRegionDef> Regions { get; } = [];
}

/// <summary>
/// Object data (RLE-compressed bitmap).
/// </summary>
public sealed class DvbSubObject
{
    public ushort Id { get; set; }
    public byte Version { get; set; }
    public DvbSubObjectCoding CodingMethod { get; set; }
    public bool NonModifyingColorFlag { get; set; }
    
    /// <summary>Top field RLE data.</summary>
    public byte[]? TopFieldData { get; set; }
    
    /// <summary>Bottom field RLE data (interlaced) or null if progressive.</summary>
    public byte[]? BottomFieldData { get; set; }
    
    /// <summary>Character codes (for text objects).</summary>
    public string? Text { get; set; }
    
    public DvbSubObject(ushort id) => Id = id;
}

/// <summary>
/// Display definition segment for canvas size and windowing.
/// </summary>
public readonly struct DvbSubDisplayDefinition
{
    public readonly byte Version;
    public readonly bool DisplayWindow;
    public readonly ushort DisplayWidth;
    public readonly ushort DisplayHeight;
    public readonly ushort WindowHorizontalMin;
    public readonly ushort WindowHorizontalMax;
    public readonly ushort WindowVerticalMin;
    public readonly ushort WindowVerticalMax;
    
    public DvbSubDisplayDefinition(
        byte version, bool displayWindow,
        ushort displayWidth, ushort displayHeight,
        ushort windowHMin, ushort windowHMax,
        ushort windowVMin, ushort windowVMax)
    {
        Version = version;
        DisplayWindow = displayWindow;
        DisplayWidth = displayWidth;
        DisplayHeight = displayHeight;
        WindowHorizontalMin = windowHMin;
        WindowHorizontalMax = windowHMax;
        WindowVerticalMin = windowVMin;
        WindowVerticalMax = windowVMax;
    }
    
    /// <summary>Default SD display (720x576).</summary>
    public static readonly DvbSubDisplayDefinition DefaultSD = new(0, false, 720, 576, 0, 719, 0, 575);
}

/// <summary>
/// A decoded DVB subtitle image ready for display.
/// </summary>
public readonly struct DvbSubtitleImage
{
    /// <summary>Presentation timestamp in microseconds.</summary>
    public readonly long PresentationTimeMicroseconds;
    
    /// <summary>Display duration in microseconds (0 = until next).</summary>
    public readonly long DurationMicroseconds;
    
    /// <summary>Horizontal position on screen.</summary>
    public readonly ushort X;
    
    /// <summary>Vertical position on screen.</summary>
    public readonly ushort Y;
    
    /// <summary>Image width in pixels.</summary>
    public readonly ushort Width;
    
    /// <summary>Image height in pixels.</summary>
    public readonly ushort Height;
    
    /// <summary>BGRA pixel data (4 bytes per pixel).</summary>
    public readonly byte[] PixelData;
    
    public DvbSubtitleImage(long pts, long duration, ushort x, ushort y, ushort width, ushort height, byte[] pixelData)
    {
        PresentationTimeMicroseconds = pts;
        DurationMicroseconds = duration;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        PixelData = pixelData;
    }
}

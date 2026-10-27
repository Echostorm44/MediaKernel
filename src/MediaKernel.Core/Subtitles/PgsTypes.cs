// PGS (Presentation Graphics Stream) Types
// Blu-ray subtitle format specification per US Patent US 20090185789 A1

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// PGS segment types.
/// </summary>
public enum PgsSegmentType : byte
{
    /// <summary>Palette Definition Segment - defines color palette.</summary>
    PaletteDefinition = 0x14,
    
    /// <summary>Object Definition Segment - contains RLE-compressed bitmap.</summary>
    ObjectDefinition = 0x15,
    
    /// <summary>Presentation Composition Segment - controls display composition.</summary>
    PresentationComposition = 0x16,
    
    /// <summary>Window Definition Segment - defines display windows.</summary>
    WindowDefinition = 0x17,
    
    /// <summary>End of Display Set Segment - marks end of display set.</summary>
    EndOfDisplaySet = 0x80
}

/// <summary>
/// PGS composition state indicating type of display update.
/// </summary>
public enum PgsCompositionState : byte
{
    /// <summary>Normal display update, contains only changed elements.</summary>
    Normal = 0x00,
    
    /// <summary>Acquisition point for display refresh in middle of epoch.</summary>
    AcquisitionPoint = 0x40,
    
    /// <summary>Epoch start - defines a new display with all segments.</summary>
    EpochStart = 0x80
}

/// <summary>
/// PGS segment header (13 bytes).
/// </summary>
public readonly struct PgsSegmentHeader
{
    /// <summary>Magic number "PG" (0x5047).</summary>
    public const ushort MagicNumber = 0x5047;
    
    /// <summary>Presentation timestamp in 90kHz ticks.</summary>
    public readonly uint PresentationTimestamp;
    
    /// <summary>Decoding timestamp in 90kHz ticks (usually 0).</summary>
    public readonly uint DecodingTimestamp;
    
    /// <summary>Segment type.</summary>
    public readonly PgsSegmentType SegmentType;
    
    /// <summary>Size of segment data following header.</summary>
    public readonly ushort SegmentSize;
    
    /// <summary>Presentation time in microseconds.</summary>
    public long PresentationTimeMicroseconds => (long)PresentationTimestamp * 1_000_000 / 90_000;
    
    public PgsSegmentHeader(uint pts, uint dts, PgsSegmentType type, ushort size)
    {
        PresentationTimestamp = pts;
        DecodingTimestamp = dts;
        SegmentType = type;
        SegmentSize = size;
    }
    
    /// <summary>
    /// Tries to parse a segment header from data.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out PgsSegmentHeader header, out int bytesConsumed)
    {
        header = default;
        bytesConsumed = 0;
        
        if (data.Length < 13)
            return false;
        
        // Check magic number "PG"
        ushort magic = (ushort)((data[0] << 8) | data[1]);
        if (magic != MagicNumber)
            return false;
        
        uint pts = (uint)((data[2] << 24) | (data[3] << 16) | (data[4] << 8) | data[5]);
        uint dts = (uint)((data[6] << 24) | (data[7] << 16) | (data[8] << 8) | data[9]);
        var segmentType = (PgsSegmentType)data[10];
        ushort segmentSize = (ushort)((data[11] << 8) | data[12]);
        
        header = new PgsSegmentHeader(pts, dts, segmentType, segmentSize);
        bytesConsumed = 13;
        return true;
    }
}

/// <summary>
/// PGS Presentation Composition Segment (PCS) - controls subtitle display.
/// </summary>
public struct PgsPresentationComposition
{
    /// <summary>Video width in pixels.</summary>
    public ushort VideoWidth;
    
    /// <summary>Video height in pixels.</summary>
    public ushort VideoHeight;
    
    /// <summary>Composition number (incremented on each update).</summary>
    public ushort CompositionNumber;
    
    /// <summary>Composition state.</summary>
    public PgsCompositionState CompositionState;
    
    /// <summary>If true, this is a palette-only update.</summary>
    public bool PaletteUpdateOnly;
    
    /// <summary>Palette ID for palette-only updates.</summary>
    public byte PaletteId;
    
    /// <summary>Composition objects defining what to display.</summary>
    public PgsCompositionObject[] CompositionObjects;
    
    /// <summary>
    /// Parses a PCS from segment data.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out PgsPresentationComposition pcs)
    {
        pcs = default;
        
        if (data.Length < 11)
            return false;
        
        pcs.VideoWidth = (ushort)((data[0] << 8) | data[1]);
        pcs.VideoHeight = (ushort)((data[2] << 8) | data[3]);
        // data[4] = frame rate (always 0x10, ignored)
        pcs.CompositionNumber = (ushort)((data[5] << 8) | data[6]);
        pcs.CompositionState = (PgsCompositionState)data[7];
        pcs.PaletteUpdateOnly = data[8] == 0x80;
        pcs.PaletteId = data[9];
        
        int objectCount = data[10];
        pcs.CompositionObjects = new PgsCompositionObject[objectCount];
        
        int offset = 11;
        for (int i = 0; i < objectCount; i++)
        {
            if (offset + 8 > data.Length)
                return false;
            
            var obj = new PgsCompositionObject
            {
                ObjectId = (ushort)((data[offset] << 8) | data[offset + 1]),
                WindowId = data[offset + 2],
                IsCropped = data[offset + 3] == 0x40,
                HorizontalPosition = (ushort)((data[offset + 4] << 8) | data[offset + 5]),
                VerticalPosition = (ushort)((data[offset + 6] << 8) | data[offset + 7])
            };
            
            offset += 8;
            
            if (obj.IsCropped && offset + 8 <= data.Length)
            {
                obj.CropHorizontalPosition = (ushort)((data[offset] << 8) | data[offset + 1]);
                obj.CropVerticalPosition = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
                obj.CropWidth = (ushort)((data[offset + 4] << 8) | data[offset + 5]);
                obj.CropHeight = (ushort)((data[offset + 6] << 8) | data[offset + 7]);
                offset += 8;
            }
            
            pcs.CompositionObjects[i] = obj;
        }
        
        return true;
    }
}

/// <summary>
/// PGS composition object defining image placement.
/// </summary>
public struct PgsCompositionObject
{
    /// <summary>ID of the ODS containing the image.</summary>
    public ushort ObjectId;
    
    /// <summary>ID of the window where image is displayed.</summary>
    public byte WindowId;
    
    /// <summary>Whether the image is cropped.</summary>
    public bool IsCropped;
    
    /// <summary>X position on screen.</summary>
    public ushort HorizontalPosition;
    
    /// <summary>Y position on screen.</summary>
    public ushort VerticalPosition;
    
    /// <summary>X position of crop region (if cropped).</summary>
    public ushort CropHorizontalPosition;
    
    /// <summary>Y position of crop region (if cropped).</summary>
    public ushort CropVerticalPosition;
    
    /// <summary>Width of crop region (if cropped).</summary>
    public ushort CropWidth;
    
    /// <summary>Height of crop region (if cropped).</summary>
    public ushort CropHeight;
}

/// <summary>
/// PGS Window Definition Segment (WDS) - defines display windows.
/// </summary>
public struct PgsWindowDefinition
{
    /// <summary>Window ID.</summary>
    public byte WindowId;
    
    /// <summary>X position of window.</summary>
    public ushort HorizontalPosition;
    
    /// <summary>Y position of window.</summary>
    public ushort VerticalPosition;
    
    /// <summary>Window width.</summary>
    public ushort Width;
    
    /// <summary>Window height.</summary>
    public ushort Height;
    
    /// <summary>
    /// Parses window definitions from segment data.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out PgsWindowDefinition[] windows)
    {
        windows = [];
        
        if (data.Length < 1)
            return false;
        
        int windowCount = data[0];
        windows = new PgsWindowDefinition[windowCount];
        
        int offset = 1;
        for (int i = 0; i < windowCount; i++)
        {
            if (offset + 9 > data.Length)
                return false;
            
            windows[i] = new PgsWindowDefinition
            {
                WindowId = data[offset],
                HorizontalPosition = (ushort)((data[offset + 1] << 8) | data[offset + 2]),
                VerticalPosition = (ushort)((data[offset + 3] << 8) | data[offset + 4]),
                Width = (ushort)((data[offset + 5] << 8) | data[offset + 6]),
                Height = (ushort)((data[offset + 7] << 8) | data[offset + 8])
            };
            
            offset += 9;
        }
        
        return true;
    }
}

/// <summary>
/// PGS palette entry in YCbCrA format.
/// </summary>
public readonly struct PgsPaletteEntry
{
    /// <summary>Luminance (Y).</summary>
    public readonly byte Y;
    
    /// <summary>Chroma red (Cr).</summary>
    public readonly byte Cr;
    
    /// <summary>Chroma blue (Cb).</summary>
    public readonly byte Cb;
    
    /// <summary>Alpha (transparency).</summary>
    public readonly byte Alpha;
    
    public PgsPaletteEntry(byte y, byte cr, byte cb, byte alpha)
    {
        Y = y;
        Cr = cr;
        Cb = cb;
        Alpha = alpha;
    }
    
    /// <summary>
    /// Converts YCbCr to ARGB.
    /// </summary>
    public uint ToArgb()
    {
        // BT.601 YCbCr to RGB conversion
        int y = Y - 16;
        int cb = Cb - 128;
        int cr = Cr - 128;
        
        int r = Math.Clamp((298 * y + 409 * cr + 128) >> 8, 0, 255);
        int g = Math.Clamp((298 * y - 100 * cb - 208 * cr + 128) >> 8, 0, 255);
        int b = Math.Clamp((298 * y + 516 * cb + 128) >> 8, 0, 255);
        
        return (uint)((Alpha << 24) | (r << 16) | (g << 8) | b);
    }
}

/// <summary>
/// PGS Palette Definition Segment (PDS) - defines color palette.
/// </summary>
public struct PgsPaletteDefinition
{
    /// <summary>Palette ID.</summary>
    public byte PaletteId;
    
    /// <summary>Version number within epoch.</summary>
    public byte VersionNumber;
    
    /// <summary>Palette entries (up to 256).</summary>
    public PgsPaletteEntry[] Entries;
    
    /// <summary>
    /// Parses a palette definition from segment data.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out PgsPaletteDefinition palette)
    {
        palette = default;
        
        if (data.Length < 2)
            return false;
        
        palette.PaletteId = data[0];
        palette.VersionNumber = data[1];
        
        // Each entry is 5 bytes: index, Y, Cr, Cb, Alpha
        int entryCount = (data.Length - 2) / 5;
        palette.Entries = new PgsPaletteEntry[256];
        
        int offset = 2;
        for (int i = 0; i < entryCount; i++)
        {
            byte index = data[offset];
            byte y = data[offset + 1];
            byte cr = data[offset + 2];
            byte cb = data[offset + 3];
            byte alpha = data[offset + 4];
            
            palette.Entries[index] = new PgsPaletteEntry(y, cr, cb, alpha);
            offset += 5;
        }
        
        return true;
    }
}

/// <summary>
/// PGS Object Definition Segment (ODS) - contains RLE-compressed bitmap.
/// </summary>
public struct PgsObjectDefinition
{
    /// <summary>Object ID.</summary>
    public ushort ObjectId;
    
    /// <summary>Version number.</summary>
    public byte VersionNumber;
    
    /// <summary>Whether this is the first fragment.</summary>
    public bool IsFirstInSequence;
    
    /// <summary>Whether this is the last fragment.</summary>
    public bool IsLastInSequence;
    
    /// <summary>Total length of RLE data.</summary>
    public int DataLength;
    
    /// <summary>Image width.</summary>
    public ushort Width;
    
    /// <summary>Image height.</summary>
    public ushort Height;
    
    /// <summary>RLE-compressed image data.</summary>
    public byte[] RleData;
    
    /// <summary>
    /// Parses an object definition header from segment data.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out PgsObjectDefinition obj)
    {
        obj = default;
        
        if (data.Length < 4)
            return false;
        
        obj.ObjectId = (ushort)((data[0] << 8) | data[1]);
        obj.VersionNumber = data[2];
        
        byte flags = data[3];
        obj.IsFirstInSequence = (flags & 0x80) != 0;
        obj.IsLastInSequence = (flags & 0x40) != 0;
        
        int headerSize = 4;
        
        if (obj.IsFirstInSequence)
        {
            if (data.Length < 11)
                return false;
            
            obj.DataLength = (data[4] << 16) | (data[5] << 8) | data[6];
            obj.Width = (ushort)((data[7] << 8) | data[8]);
            obj.Height = (ushort)((data[9] << 8) | data[10]);
            headerSize = 11;
        }
        
        // Copy RLE data
        int rleSize = data.Length - headerSize;
        if (rleSize > 0)
        {
            obj.RleData = data[headerSize..].ToArray();
        }
        else
        {
            obj.RleData = [];
        }
        
        return true;
    }
}

/// <summary>
/// Decoded PGS subtitle image with timing.
/// </summary>
public sealed class PgsSubtitleImage
{
    /// <summary>Start time in microseconds.</summary>
    public long StartTimeMicroseconds { get; init; }
    
    /// <summary>End time in microseconds (set when next composition arrives).</summary>
    public long EndTimeMicroseconds { get; set; }
    
    /// <summary>X position on screen.</summary>
    public int X { get; init; }
    
    /// <summary>Y position on screen.</summary>
    public int Y { get; init; }
    
    /// <summary>Image width.</summary>
    public int Width { get; init; }
    
    /// <summary>Image height.</summary>
    public int Height { get; init; }
    
    /// <summary>ARGB pixel data (row-major).</summary>
    public uint[] Pixels { get; init; } = [];
}

// VOBSUB/SPU (Sub Picture Unit) Types
// DVD subtitle format

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// SPU control command types.
/// </summary>
public enum SpuCommand : byte
{
    /// <summary>Force display (menu buttons).</summary>
    ForceDisplay = 0x00,
    
    /// <summary>Start subtitle display.</summary>
    StartDisplay = 0x01,
    
    /// <summary>Stop subtitle display.</summary>
    StopDisplay = 0x02,
    
    /// <summary>Set 4-color palette indices.</summary>
    SetPalette = 0x03,
    
    /// <summary>Set alpha channel for 4 colors.</summary>
    SetAlphaChannel = 0x04,
    
    /// <summary>Set display coordinates.</summary>
    SetCoordinates = 0x05,
    
    /// <summary>Set RLE data offsets for interlaced fields.</summary>
    SetOffsets = 0x06,
    
    /// <summary>Set per-pixel color/contrast (PXCTLI).</summary>
    SetColorContrast = 0x07,
    
    /// <summary>End of control sequence.</summary>
    End = 0xFF
}

/// <summary>
/// SPU packet header with size and RLE information.
/// </summary>
public readonly struct SpuPacketHeader
{
    /// <summary>Total packet size in bytes.</summary>
    public readonly ushort PacketSize;
    
    /// <summary>Size of RLE data (excludes control sequences).</summary>
    public readonly ushort RleDataSize;
    
    /// <summary>Offset to control sequence data.</summary>
    public int ControlSequenceOffset => 4 + RleDataSize;
    
    public SpuPacketHeader(ushort packetSize, ushort rleDataSize)
    {
        PacketSize = packetSize;
        RleDataSize = rleDataSize;
    }
    
    /// <summary>
    /// Parses an SPU packet header.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out SpuPacketHeader header)
    {
        header = default;
        
        if (data.Length < 4)
            return false;
        
        ushort packetSize = (ushort)((data[0] << 8) | data[1]);
        ushort rleSize = (ushort)(((data[2] << 8) | data[3]) - 4);
        
        header = new SpuPacketHeader(packetSize, rleSize);
        return true;
    }
}

/// <summary>
/// SPU control sequence entry.
/// </summary>
public struct SpuControlSequence
{
    /// <summary>Display timestamp in microseconds.</summary>
    public long DisplayTimeMicroseconds;
    
    /// <summary>Offset to next control sequence.</summary>
    public ushort NextSequenceOffset;
    
    /// <summary>Whether display should start.</summary>
    public bool StartDisplay;
    
    /// <summary>Whether display should stop.</summary>
    public bool StopDisplay;
    
    /// <summary>Force display (for menu buttons).</summary>
    public bool ForceDisplay;
    
    /// <summary>4-entry palette indices.</summary>
    public byte[] PaletteIndices;
    
    /// <summary>4-entry alpha values (4-bit, 0x0-0xF).</summary>
    public byte[] AlphaValues;
    
    /// <summary>X start coordinate.</summary>
    public ushort X1;
    
    /// <summary>X end coordinate.</summary>
    public ushort X2;
    
    /// <summary>Y start coordinate.</summary>
    public ushort Y1;
    
    /// <summary>Y end coordinate.</summary>
    public ushort Y2;
    
    /// <summary>RLE data offset for even field.</summary>
    public ushort EvenFieldOffset;
    
    /// <summary>RLE data offset for odd field.</summary>
    public ushort OddFieldOffset;
    
    /// <summary>Width of the subtitle.</summary>
    public int Width => X2 - X1 + 1;
    
    /// <summary>Height of the subtitle.</summary>
    public int Height => Y2 - Y1 + 1;
}

/// <summary>
/// Decoded DVD subtitle image.
/// </summary>
public sealed class VobsubSubtitleImage
{
    /// <summary>Start time in microseconds.</summary>
    public long StartTimeMicroseconds { get; init; }
    
    /// <summary>End time in microseconds.</summary>
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
    
    /// <summary>Whether this is a forced subtitle (menu).</summary>
    public bool IsForced { get; init; }
}

/// <summary>
/// CLUT (Color Lookup Table) palette for DVD subtitles.
/// </summary>
public sealed class VobsubPalette
{
    private readonly uint[] colors = new uint[16];
    
    /// <summary>
    /// Sets a palette entry (YUV format as stored in DVD).
    /// </summary>
    public void SetEntry(int index, byte y, byte u, byte v)
    {
        if (index is >= 0 and < 16)
        {
            // Store in AYUV format
            colors[index] = (uint)((0xFF << 24) | (y << 16) | (u << 8) | v);
        }
    }
    
    /// <summary>
    /// Gets an ARGB color from the palette.
    /// </summary>
    /// <param name="index">Palette index (0-15).</param>
    /// <param name="alpha">Alpha value (0-15, will be scaled to 0-255).</param>
    public uint GetArgb(int index, int alpha)
    {
        if (index is < 0 or >= 16)
            return 0;
        
        uint yuv = colors[index];
        int y = (int)((yuv >> 16) & 0xFF);
        int u = (int)((yuv >> 8) & 0xFF);
        int v = (int)(yuv & 0xFF);
        
        // Convert YUV to RGB (BT.601)
        int c = y - 16;
        int d = u - 128;
        int e = v - 128;
        
        int r = Math.Clamp((298 * c + 409 * e + 128) >> 8, 0, 255);
        int g = Math.Clamp((298 * c - 100 * d - 208 * e + 128) >> 8, 0, 255);
        int b = Math.Clamp((298 * c + 516 * d + 128) >> 8, 0, 255);
        
        // Scale 4-bit alpha to 8-bit
        int a8 = alpha * 17;  // 0x0F * 17 = 255
        
        return (uint)((a8 << 24) | (r << 16) | (g << 8) | b);
    }
    
    /// <summary>
    /// Creates a default grayscale palette.
    /// </summary>
    public static VobsubPalette CreateDefault()
    {
        var palette = new VobsubPalette();
        
        // Default DVD palette is typically grayscale
        for (int i = 0; i < 16; i++)
        {
            byte y = (byte)(i * 17);  // 0, 17, 34, ... 255
            palette.SetEntry(i, y, 128, 128);  // Neutral chroma
        }
        
        return palette;
    }
}

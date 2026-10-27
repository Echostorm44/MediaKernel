// CVD (Chaoji VCD) Subtitle Decoder
// Decodes CVD subtitle format used on Chinese Video CDs

using System;
using System.Buffers.Binary;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Color entry for CVD subtitle palette (YCbCr + Alpha).
/// </summary>
public readonly struct CvdColor
{
    public readonly byte Y;
    public readonly byte Cb;
    public readonly byte Cr;
    public readonly byte Alpha;
    
    public CvdColor(byte y, byte cb, byte cr, byte alpha = 255)
    {
        Y = y;
        Cb = cb;
        Cr = cr;
        Alpha = alpha;
    }
    
    /// <summary>
    /// Converts YCbCr to BGRA.
    /// </summary>
    public uint ToBgra()
    {
        // BT.601 YCbCr to RGB conversion
        int c = Y - 16;
        int d = Cb - 128;
        int e = Cr - 128;
        
        int r = Math.Clamp((298 * c + 409 * e + 128) >> 8, 0, 255);
        int g = Math.Clamp((298 * c - 100 * d - 208 * e + 128) >> 8, 0, 255);
        int b = Math.Clamp((298 * c + 516 * d + 128) >> 8, 0, 255);
        
        return (uint)((Alpha << 24) | (r << 16) | (g << 8) | b);
    }
}

/// <summary>
/// Decoded CVD subtitle image.
/// </summary>
public sealed class CvdSubtitleImage
{
    /// <summary>Start time in microseconds.</summary>
    public long StartTimeMicroseconds { get; init; }
    
    /// <summary>Duration in microseconds.</summary>
    public long DurationMicroseconds { get; init; }
    
    /// <summary>X position on screen.</summary>
    public int X { get; init; }
    
    /// <summary>Y position on screen.</summary>
    public int Y { get; init; }
    
    /// <summary>Width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>BGRA pixel data.</summary>
    public byte[] Pixels { get; init; } = [];
}

/// <summary>
/// Decoder for CVD (Chaoji VCD) subtitles.
/// CVD subtitles are used on Chinese Video CDs.
/// Unlike DVD subtitles, image data comes first and metadata is at the end.
/// </summary>
public sealed class CvdSubDecoder
{
    // Metadata command tags
    private const byte TagDuration = 0x04;
    private const byte TagPosition = 0x17;
    private const byte TagEndPosition = 0x1F;
    private const byte TagPalette0 = 0x24;
    private const byte TagPalette1 = 0x25;
    private const byte TagPalette2 = 0x26;
    private const byte TagPalette3 = 0x27;
    private const byte TagHighlightPalette0 = 0x2C;
    private const byte TagHighlightPalette1 = 0x2D;
    private const byte TagHighlightPalette2 = 0x2E;
    private const byte TagHighlightPalette3 = 0x2F;
    private const byte TagTransparency = 0x37;
    private const byte TagHighlightTransparency = 0x3F;
    private const byte TagFirstFieldOffset = 0x47;
    private const byte TagSecondFieldOffset = 0x4F;
    
    private readonly CvdColor[] palette = new CvdColor[4];
    private readonly CvdColor[] highlightPalette = new CvdColor[4];
    private long basePts;
    
    /// <summary>
    /// Sets the base PTS for timestamp calculations.
    /// </summary>
    public void SetBasePts(long ptsMicroseconds)
    {
        basePts = ptsMicroseconds;
    }
    
    /// <summary>
    /// Decodes a CVD subtitle packet.
    /// </summary>
    public CvdSubtitleImage? DecodePacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return null;
        
        // CVD header: 2 bytes total size, 2 bytes metadata offset
        int totalSize = BinaryPrimitives.ReadUInt16BigEndian(data) + 4;
        int metadataOffset = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2));
        
        if (metadataOffset > data.Length || totalSize > data.Length)
            return null;
        
        int metadataLength = totalSize - metadataOffset;
        if (metadataLength < 0)
            return null;
        
        // Parse metadata (comes at end of packet)
        var metadata = ParseMetadata(data.Slice(metadataOffset, Math.Min(metadataLength, data.Length - metadataOffset)));
        
        if (metadata.Width <= 0 || metadata.Height <= 0)
            return null;
        
        // Decode RLE image data (starts at offset 4)
        int imageOffset = 4;
        var pixels = DecodeRle(data.Slice(imageOffset, metadataOffset - imageOffset), 
            metadata.Width, metadata.Height,
            metadata.FirstFieldOffset, metadata.SecondFieldOffset);
        
        return new CvdSubtitleImage
        {
            StartTimeMicroseconds = basePts,
            DurationMicroseconds = metadata.DurationMicroseconds,
            X = (metadata.X * 3) / 4,  // Adjust for aspect ratio
            Y = metadata.Y,
            Width = metadata.Width,
            Height = metadata.Height,
            Pixels = pixels
        };
    }
    
    /// <summary>
    /// Parses CVD metadata block.
    /// </summary>
    private CvdMetadata ParseMetadata(ReadOnlySpan<byte> data)
    {
        var metadata = new CvdMetadata();
        
        // Reset palette to defaults
        for (int i = 0; i < 4; i++)
        {
            palette[i] = new CvdColor(0, 128, 128, 0);
            highlightPalette[i] = new CvdColor(0, 128, 128, 0);
        }
        
        int offset = 0;
        while (offset + 4 <= data.Length)
        {
            byte tag = data[offset];
            byte p1 = data[offset + 1];
            byte p2 = data[offset + 2];
            byte p3 = data[offset + 3];
            
            switch (tag)
            {
                case TagDuration:
                    // Duration in 90kHz ticks -> convert to microseconds
                    int durationTicks = (p1 << 16) | (p2 << 8) | p3;
                    metadata.DurationMicroseconds = (durationTicks * 1_000_000L) / 90_000;
                    break;
                    
                case TagPosition:
                    // Top-left position: X = bits 11:6 of p2 + bits 3:0 of p1 << 6
                    //                   Y = bits 1:0 of p2 << 8 + p3
                    metadata.X = ((p1 & 0x0F) << 6) | (p2 >> 2);
                    metadata.Y = ((p2 & 0x03) << 8) | p3;
                    break;
                    
                case TagEndPosition:
                    // Bottom-right position
                    int endX = ((p1 & 0x0F) << 6) | (p2 >> 2);
                    int endY = ((p2 & 0x03) << 8) | p3;
                    metadata.Width = endX - metadata.X + 1;
                    metadata.Height = endY - metadata.Y + 1;
                    break;
                    
                case TagPalette0:
                case TagPalette1:
                case TagPalette2:
                case TagPalette3:
                    int paletteIdx = tag - TagPalette0;
                    palette[paletteIdx] = new CvdColor(p1, p2, p3, palette[paletteIdx].Alpha);
                    break;
                    
                case TagHighlightPalette0:
                case TagHighlightPalette1:
                case TagHighlightPalette2:
                case TagHighlightPalette3:
                    int hlIdx = tag - TagHighlightPalette0;
                    highlightPalette[hlIdx] = new CvdColor(p1, p2, p3, highlightPalette[hlIdx].Alpha);
                    break;
                    
                case TagTransparency:
                    // Transparency for primary palette (4 bits each)
                    palette[0] = new CvdColor(palette[0].Y, palette[0].Cb, palette[0].Cr, 
                        (byte)((p3 & 0x0F) << 4));
                    palette[1] = new CvdColor(palette[1].Y, palette[1].Cb, palette[1].Cr, 
                        (byte)((p3 >> 4) << 4));
                    palette[2] = new CvdColor(palette[2].Y, palette[2].Cb, palette[2].Cr, 
                        (byte)((p2 & 0x0F) << 4));
                    palette[3] = new CvdColor(palette[3].Y, palette[3].Cb, palette[3].Cr, 
                        (byte)((p2 >> 4) << 4));
                    break;
                    
                case TagHighlightTransparency:
                    highlightPalette[0] = new CvdColor(highlightPalette[0].Y, highlightPalette[0].Cb, 
                        highlightPalette[0].Cr, (byte)((p2 & 0x0F) << 4));
                    highlightPalette[1] = new CvdColor(highlightPalette[1].Y, highlightPalette[1].Cb, 
                        highlightPalette[1].Cr, (byte)((p2 >> 4) << 4));
                    highlightPalette[2] = new CvdColor(highlightPalette[2].Y, highlightPalette[2].Cb, 
                        highlightPalette[2].Cr, (byte)((p1 & 0x0F) << 4));
                    highlightPalette[3] = new CvdColor(highlightPalette[3].Y, highlightPalette[3].Cb, 
                        highlightPalette[3].Cr, (byte)((p1 >> 4) << 4));
                    break;
                    
                case TagFirstFieldOffset:
                    metadata.FirstFieldOffset = ((p2 << 8) | p3) - 4;  // Relative to image start
                    break;
                    
                case TagSecondFieldOffset:
                    metadata.SecondFieldOffset = ((p2 << 8) | p3) - 4;  // Relative to image start
                    break;
            }
            
            offset += 4;
        }
        
        return metadata;
    }
    
    /// <summary>
    /// Decodes CVD RLE-compressed interlaced image data.
    /// CVD uses 2-bit color + 2-bit count nibbles.
    /// </summary>
    private byte[] DecodeRle(ReadOnlySpan<byte> data, int width, int height, 
        int firstFieldOffset, int secondFieldOffset)
    {
        var pixels = new byte[width * height * 4];
        var reader = new CvdNibbleReader(data);
        
        // Process interlaced fields
        for (int field = 0; field < 2; field++)
        {
            int fieldOffset = field == 0 ? firstFieldOffset : secondFieldOffset;
            if (fieldOffset < 0 || fieldOffset >= data.Length)
                fieldOffset = 0;
            
            reader.Reset(fieldOffset);
            
            for (int row = field; row < height; row += 2)
            {
                int column = 0;
                while (column < width)
                {
                    byte nibble = reader.ReadNibble();
                    
                    if (nibble == 0)
                    {
                        // Zero nibble: fill rest of line with color from next nibble
                        byte colorNibble = reader.ReadNibble();
                        int color = colorNibble & 0x03;
                        int count = width - column;
                        FillPixels(pixels, row * width + column, count, palette[color]);
                        break;
                    }
                    
                    // Normal nibble: 2-bit count (1-3) + 2-bit color
                    int repeatCount = (nibble >> 2) & 0x03;
                    int colorIndex = nibble & 0x03;
                    
                    if (repeatCount == 0)
                        repeatCount = 1;  // Treat 0 as 1
                    
                    int pixelCount = Math.Min(repeatCount, width - column);
                    FillPixels(pixels, row * width + column, pixelCount, palette[colorIndex]);
                    column += pixelCount;
                }
            }
        }
        
        return pixels;
    }
    
    /// <summary>
    /// Fills pixels with a color.
    /// </summary>
    private static void FillPixels(byte[] pixels, int offset, int count, CvdColor color)
    {
        uint bgra = color.ToBgra();
        int pixelOffset = offset * 4;
        
        for (int i = 0; i < count && pixelOffset + 3 < pixels.Length; i++)
        {
            pixels[pixelOffset] = (byte)(bgra & 0xFF);         // B
            pixels[pixelOffset + 1] = (byte)((bgra >> 8) & 0xFF);  // G
            pixels[pixelOffset + 2] = (byte)((bgra >> 16) & 0xFF); // R
            pixels[pixelOffset + 3] = (byte)((bgra >> 24) & 0xFF); // A
            pixelOffset += 4;
        }
    }
    
    private struct CvdMetadata
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public long DurationMicroseconds;
        public int FirstFieldOffset;
        public int SecondFieldOffset;
    }
}

/// <summary>
/// Nibble reader for CVD RLE data.
/// </summary>
internal ref struct CvdNibbleReader
{
    private readonly ReadOnlySpan<byte> data;
    private int byteOffset;
    private bool highNibble;
    
    public CvdNibbleReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        byteOffset = 0;
        highNibble = true;
    }
    
    public void Reset(int offset)
    {
        byteOffset = offset;
        highNibble = true;
    }
    
    public byte ReadNibble()
    {
        if (byteOffset >= data.Length)
            return 0;
        
        byte result;
        if (highNibble)
        {
            result = (byte)(data[byteOffset] >> 4);
            highNibble = false;
        }
        else
        {
            result = (byte)(data[byteOffset] & 0x0F);
            byteOffset++;
            highNibble = true;
        }
        
        return result;
    }
}

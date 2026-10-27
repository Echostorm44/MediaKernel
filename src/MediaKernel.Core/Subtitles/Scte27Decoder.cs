// MediaKernel - SCTE-27 Subtitles Decoder
// Cable TV bitmap subtitles (ANSI/SCTE 27 standard)
// Reference: SCTE 27 - Subtitling Methods for Broadcast Cable

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Display standard for SCTE-27 subtitles.
/// </summary>
public enum Scte27DisplayStandard
{
    /// <summary>480i (NTSC) - 720x480 @ 29.97fps.</summary>
    Ntsc480i = 0,
    
    /// <summary>576i (PAL) - 720x576 @ 25fps.</summary>
    Pal576i = 1,
    
    /// <summary>720p (HD) - 1280x720 @ 59.94fps.</summary>
    Hd720p = 2,
    
    /// <summary>1080i (HD) - 1920x1080 @ 59.94fps.</summary>
    Hd1080i = 3
}

/// <summary>
/// Outline style for SCTE-27 bitmaps.
/// </summary>
public enum Scte27OutlineStyle
{
    /// <summary>No outline or shadow.</summary>
    None = 0,
    
    /// <summary>Circular outline around characters.</summary>
    Outline = 1,
    
    /// <summary>Drop shadow behind characters.</summary>
    Shadow = 2,
    
    /// <summary>Reserved style.</summary>
    Reserved = 3
}

/// <summary>
/// Color in SCTE-27 YUV format.
/// </summary>
public readonly struct Scte27Color
{
    /// <summary>Luminance component (0-255).</summary>
    public byte Y { get; init; }
    
    /// <summary>Cb chrominance component (0-255).</summary>
    public byte U { get; init; }
    
    /// <summary>Cr chrominance component (0-255).</summary>
    public byte V { get; init; }
    
    /// <summary>Alpha component (0=transparent, 255=opaque).</summary>
    public byte Alpha { get; init; }
    
    /// <summary>Transparent color.</summary>
    public static Scte27Color Transparent => new() { Y = 0, U = 128, V = 128, Alpha = 0 };
    
    /// <summary>
    /// Converts to ARGB format.
    /// </summary>
    public uint ToArgb()
    {
        // YUV to RGB conversion (BT.601)
        int yVal = Y - 16;
        int uVal = U - 128;
        int vVal = V - 128;
        
        int r = Clamp((298 * yVal + 409 * vVal + 128) >> 8);
        int g = Clamp((298 * yVal - 100 * uVal - 208 * vVal + 128) >> 8);
        int b = Clamp((298 * yVal + 516 * uVal + 128) >> 8);
        
        return ((uint)Alpha << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp(int value) => value < 0 ? 0 : (value > 255 ? 255 : value);
}

/// <summary>
/// Decoded SCTE-27 subtitle bitmap.
/// </summary>
public sealed class Scte27Subtitle
{
    /// <summary>Display standard (resolution info).</summary>
    public Scte27DisplayStandard DisplayStandard { get; init; }
    
    /// <summary>X position of the subtitle.</summary>
    public int X { get; init; }
    
    /// <summary>Y position of the subtitle.</summary>
    public int Y { get; init; }
    
    /// <summary>Width of the bitmap.</summary>
    public int Width { get; init; }
    
    /// <summary>Height of the bitmap.</summary>
    public int Height { get; init; }
    
    /// <summary>Display duration in frame counts.</summary>
    public int DisplayDurationFrames { get; init; }
    
    /// <summary>Whether to clear display before showing.</summary>
    public bool PreClearDisplay { get; init; }
    
    /// <summary>ARGB pixel data (Width * Height).</summary>
    public uint[] PixelData { get; init; } = [];
    
    /// <summary>Gets display duration in microseconds.</summary>
    public long DisplayDurationMicroseconds
    {
        get
        {
            long frameDurationUs = DisplayStandard switch
            {
                Scte27DisplayStandard.Ntsc480i => 33367,  // 29.97 fps
                Scte27DisplayStandard.Pal576i => 40000,   // 25 fps
                Scte27DisplayStandard.Hd720p => 16683,    // 59.94 fps
                Scte27DisplayStandard.Hd1080i => 16683,   // 59.94 fps
                _ => 40000
            };
            return DisplayDurationFrames * frameDurationUs;
        }
    }
    
    /// <summary>Gets the original picture width for this display standard.</summary>
    public int OriginalWidth => DisplayStandard switch
    {
        Scte27DisplayStandard.Ntsc480i => 720,
        Scte27DisplayStandard.Pal576i => 720,
        Scte27DisplayStandard.Hd720p => 1280,
        Scte27DisplayStandard.Hd1080i => 1920,
        _ => 720
    };
    
    /// <summary>Gets the original picture height for this display standard.</summary>
    public int OriginalHeight => DisplayStandard switch
    {
        Scte27DisplayStandard.Ntsc480i => 480,
        Scte27DisplayStandard.Pal576i => 576,
        Scte27DisplayStandard.Hd720p => 720,
        Scte27DisplayStandard.Hd1080i => 1080,
        _ => 480
    };
}

/// <summary>
/// Decoder for SCTE-27 subtitles (cable TV bitmap subtitles).
/// </summary>
public sealed class Scte27Decoder
{
    private int segmentId;
    private int segmentSize;
    private byte[] segmentBuffer = new byte[65536];
    
    /// <summary>
    /// Decode an SCTE-27 section.
    /// </summary>
    /// <param name="data">Raw section data starting with table_id.</param>
    /// <returns>Decoded subtitle or null if not valid.</returns>
    public Scte27Subtitle? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return null;
        
        int tableId = data[0];
        if (tableId != 0xC6)
            return null; // Not SCTE-27
        
        int sectionLength = ((data[1] & 0x0F) << 8) | data[2];
        if (sectionLength < 6 || data.Length < 3 + sectionLength)
            return null;
        
        int protocolVersion = data[3] & 0x3F;
        if (protocolVersion != 0)
            return null; // Unsupported version
        
        bool segmentationOverlay = (data[3] & 0x40) != 0;
        
        if (segmentationOverlay)
        {
            // Segmented message - reassemble
            if (sectionLength < 10)
                return null;
            
            int id = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
            int lastSegment = (data[6] << 4) | (data[7] >> 4);
            int segmentIndex = ((data[7] & 0x0F) << 8) | data[8];
            
            if (segmentIndex > lastSegment)
                return null;
            
            int payloadStart = 9;
            int payloadLength = sectionLength - 6; // Minus header and CRC
            
            if (segmentIndex == 0)
            {
                // First segment
                segmentId = id;
                segmentSize = 0;
            }
            else if (segmentId != id || segmentSize <= 0)
            {
                // Out of sequence
                return null;
            }
            
            // Append to buffer
            if (segmentSize + payloadLength > segmentBuffer.Length)
                return null;
            
            data.Slice(payloadStart, payloadLength).CopyTo(segmentBuffer.AsSpan(segmentSize));
            segmentSize += payloadLength;
            
            if (segmentIndex < lastSegment)
                return null; // Not complete yet
            
            // Complete - decode
            return DecodeSubtitleMessage(segmentBuffer.AsSpan(0, segmentSize));
        }
        else
        {
            // Non-segmented message
            int payloadStart = 4;
            int payloadLength = sectionLength - 1; // Minus protocol_version byte
            
            return DecodeSubtitleMessage(data.Slice(payloadStart, payloadLength));
        }
    }
    
    private Scte27Subtitle? DecodeSubtitleMessage(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
            return null;
        
        bool preClearDisplay = (data[3] & 0x80) != 0;
        int displayStandard = data[3] & 0x1F;
        int subtitleType = data[8] >> 4;
        int displayDuration = ((data[8] & 0x07) << 8) | data[9];
        int blockLength = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        
        if (blockLength > data.Length - 12)
            return null;
        
        if (subtitleType != 1)
            return null; // Only simple_bitmap is supported
        
        var bitmapData = data.Slice(12, blockLength);
        return DecodeSimpleBitmap(bitmapData, (Scte27DisplayStandard)displayStandard, displayDuration, preClearDisplay);
    }
    
    private Scte27Subtitle? DecodeSimpleBitmap(ReadOnlySpan<byte> data, Scte27DisplayStandard displayStandard, int displayDuration, bool preClearDisplay)
    {
        var reader = new BitReader(data);
        
        reader.Skip(5);
        bool isFramed = reader.ReadBit();
        int outlineStyle = reader.ReadBits(2);
        var characterColor = ReadColor(ref reader);
        
        int topH = reader.ReadBits(12);
        int topV = reader.ReadBits(12);
        int bottomH = reader.ReadBits(12);
        int bottomV = reader.ReadBits(12);
        
        if (topH >= bottomH || topV >= bottomV)
            return null;
        
        int frameTopH = topH;
        int frameTopV = topV;
        int frameBottomH = bottomH;
        int frameBottomV = bottomV;
        var frameColor = Scte27Color.Transparent;
        
        if (isFramed)
        {
            frameTopH = reader.ReadBits(12);
            frameTopV = reader.ReadBits(12);
            frameBottomH = reader.ReadBits(12);
            frameBottomV = reader.ReadBits(12);
            frameColor = ReadColor(ref reader);
            
            if (frameTopH > topH || frameTopV > topV ||
                frameBottomH < bottomH || frameBottomV < bottomV)
                return null;
        }
        
        int outlineThickness = 0;
        var outlineColor = Scte27Color.Transparent;
        int shadowRight = 0;
        int shadowBottom = 0;
        var shadowColor = Scte27Color.Transparent;
        
        if (outlineStyle == 1)
        {
            reader.Skip(4);
            outlineThickness = reader.ReadBits(4);
            outlineColor = ReadColor(ref reader);
        }
        else if (outlineStyle == 2)
        {
            shadowRight = reader.ReadBits(4);
            shadowBottom = reader.ReadBits(4);
            shadowColor = ReadColor(ref reader);
        }
        else if (outlineStyle == 3)
        {
            reader.Skip(24);
        }
        
        reader.Skip(16); // bitmap_compressed_length
        
        int bitmapWidth = bottomH - topH;
        int bitmapHeight = bottomV - topV;
        int bitmapSize = bitmapWidth * bitmapHeight;
        
        if (bitmapSize <= 0 || bitmapSize > 4_000_000)
            return null;
        
        // Decode RLE bitmap
        bool[] bitmap = new bool[bitmapSize];
        int position = 0;
        
        while (position < bitmapSize && !reader.IsEof)
        {
            int runOnLength = 0;
            int runOffLength = 0;
            
            if (!reader.ReadBit())
            {
                if (!reader.ReadBit())
                {
                    if (!reader.ReadBit())
                    {
                        if (reader.ReadBits(2) == 1)
                        {
                            // Skip to end of line
                            int nextLine = ((position / bitmapWidth) + 1) * bitmapWidth;
                            nextLine = Math.Min(nextLine, bitmapSize);
                            while (position < nextLine)
                                bitmap[position++] = false;
                        }
                    }
                    else
                    {
                        runOnLength = 4;
                    }
                }
                else
                {
                    runOffLength = 6;
                }
            }
            else
            {
                runOnLength = 3;
                runOffLength = 5;
            }
            
            if (runOnLength > 0)
            {
                int run = reader.ReadBits(runOnLength);
                if (run == 0)
                    run = 1 << runOnLength;
                
                while (position < bitmapSize && run > 0)
                {
                    bitmap[position++] = true;
                    run--;
                }
            }
            
            if (runOffLength > 0)
            {
                int run = reader.ReadBits(runOffLength);
                if (run == 0)
                    run = 1 << runOffLength;
                
                while (position < bitmapSize && run > 0)
                {
                    bitmap[position++] = false;
                    run--;
                }
            }
        }
        
        // Fill remaining with transparent
        while (position < bitmapSize)
            bitmap[position++] = false;
        
        // Calculate margins for outline/shadow
        int marginH = 0;
        int marginV = 0;
        if (outlineStyle == 1)
        {
            marginH = marginV = outlineThickness;
        }
        else if (outlineStyle == 2)
        {
            marginH = shadowRight;
            marginV = shadowBottom;
        }
        
        frameTopH -= marginH;
        frameTopV -= marginV;
        frameBottomH += marginH;
        frameBottomV += marginV;
        
        int frameWidth = frameBottomH - frameTopH;
        int frameHeight = frameBottomV - frameTopV;
        int bitmapOffsetH = topH - frameTopH;
        int bitmapOffsetV = topV - frameTopV;
        
        if (frameWidth <= 0 || frameHeight <= 0)
            return null;
        
        // Render to ARGB
        uint[] pixels = new uint[frameWidth * frameHeight];
        uint frameArgb = frameColor.ToArgb();
        uint charArgb = characterColor.ToArgb();
        uint outlineArgb = outlineColor.ToArgb();
        uint shadowArgb = shadowColor.ToArgb();
        
        // Fill with frame color
        Array.Fill(pixels, frameArgb);
        
        // Draw outline or shadow
        if (outlineStyle == 1 && outlineThickness > 0)
        {
            // Precompute circle mask
            bool[,] circle = new bool[outlineThickness + 1, outlineThickness + 1];
            for (int dy = 0; dy <= outlineThickness; dy++)
            {
                for (int dx = 0; dx <= outlineThickness; dx++)
                {
                    circle[dy, dx] = (dx > 0 || dy > 0) &&
                                     dx * dx + dy * dy <= outlineThickness * outlineThickness;
                }
            }
            
            for (int by = 0; by < bitmapHeight; by++)
            {
                for (int bx = 0; bx < bitmapWidth; bx++)
                {
                    if (!bitmap[by * bitmapWidth + bx])
                        continue;
                    
                    for (int dy = 0; dy <= outlineThickness; dy++)
                    {
                        for (int dx = 0; dx <= outlineThickness; dx++)
                        {
                            if (!circle[dy, dx])
                                continue;
                            
                            SetPixelSafe(pixels, frameWidth, frameHeight,
                                        bx + bitmapOffsetH + dx, by + bitmapOffsetV + dy, outlineArgb);
                            SetPixelSafe(pixels, frameWidth, frameHeight,
                                        bx + bitmapOffsetH - dx, by + bitmapOffsetV + dy, outlineArgb);
                            SetPixelSafe(pixels, frameWidth, frameHeight,
                                        bx + bitmapOffsetH + dx, by + bitmapOffsetV - dy, outlineArgb);
                            SetPixelSafe(pixels, frameWidth, frameHeight,
                                        bx + bitmapOffsetH - dx, by + bitmapOffsetV - dy, outlineArgb);
                        }
                    }
                }
            }
        }
        else if (outlineStyle == 2)
        {
            // Shadow
            for (int by = 0; by < bitmapHeight; by++)
            {
                for (int bx = 0; bx < bitmapWidth; bx++)
                {
                    if (bitmap[by * bitmapWidth + bx])
                    {
                        SetPixelSafe(pixels, frameWidth, frameHeight,
                                    bx + bitmapOffsetH + shadowRight,
                                    by + bitmapOffsetV + shadowBottom, shadowArgb);
                    }
                }
            }
        }
        
        // Draw character
        for (int by = 0; by < bitmapHeight; by++)
        {
            for (int bx = 0; bx < bitmapWidth; bx++)
            {
                if (bitmap[by * bitmapWidth + bx])
                {
                    int px = bx + bitmapOffsetH;
                    int py = by + bitmapOffsetV;
                    if (px >= 0 && px < frameWidth && py >= 0 && py < frameHeight)
                        pixels[py * frameWidth + px] = charArgb;
                }
            }
        }
        
        return new Scte27Subtitle
        {
            DisplayStandard = displayStandard,
            X = frameTopH,
            Y = frameTopV,
            Width = frameWidth,
            Height = frameHeight,
            DisplayDurationFrames = displayDuration,
            PreClearDisplay = preClearDisplay,
            PixelData = pixels
        };
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetPixelSafe(uint[] pixels, int width, int height, int x, int y, uint color)
    {
        if (x >= 0 && x < width && y >= 0 && y < height)
            pixels[y * width + x] = color;
    }
    
    private static Scte27Color ReadColor(ref BitReader reader)
    {
        byte y = (byte)(reader.ReadBits(5) << 3);
        byte alpha = (byte)(reader.ReadBit() ? 0xFF : 0x80);
        byte v = (byte)(reader.ReadBits(5) << 3);
        byte u = (byte)(reader.ReadBits(5) << 3);
        
        return new Scte27Color { Y = y, U = u, V = v, Alpha = alpha };
    }
    
    /// <summary>
    /// Probe data to check if it looks like SCTE-27.
    /// </summary>
    public static bool Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return false;
        
        return data[0] == 0xC6; // SCTE-27 table_id
    }
    
    /// <summary>
    /// Simple bit reader for SCTE-27 decoding.
    /// </summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> data;
        private int byteOffset;
        private int bitOffset;
        
        public BitReader(ReadOnlySpan<byte> data)
        {
            this.data = data;
            byteOffset = 0;
            bitOffset = 0;
        }
        
        public bool IsEof => byteOffset >= data.Length;
        
        public bool ReadBit()
        {
            if (byteOffset >= data.Length)
                return false;
            
            bool bit = ((data[byteOffset] >> (7 - bitOffset)) & 1) != 0;
            bitOffset++;
            if (bitOffset >= 8)
            {
                bitOffset = 0;
                byteOffset++;
            }
            return bit;
        }
        
        public int ReadBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                value = (value << 1) | (ReadBit() ? 1 : 0);
            }
            return value;
        }
        
        public void Skip(int bits)
        {
            int totalBits = byteOffset * 8 + bitOffset + bits;
            byteOffset = totalBits / 8;
            bitOffset = totalBits % 8;
        }
    }
}

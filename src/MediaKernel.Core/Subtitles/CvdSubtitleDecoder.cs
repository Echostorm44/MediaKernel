// MediaKernel - CVD (Chaoji VCD) Subtitle Decoder
// Bitmap subtitles from CVD (Chaoji VCD) discs
// Reference: VLC cvdsub.c

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Decoded CVD subtitle image.
/// </summary>
public sealed class CvdSubtitle
{
    /// <summary>X position of the subtitle.</summary>
    public int X { get; init; }
    
    /// <summary>Y position of the subtitle.</summary>
    public int Y { get; init; }
    
    /// <summary>Width of the bitmap.</summary>
    public int Width { get; init; }
    
    /// <summary>Height of the bitmap.</summary>
    public int Height { get; init; }
    
    /// <summary>Display duration in microseconds.</summary>
    public long DurationMicroseconds { get; init; }
    
    /// <summary>ARGB pixel data (Width * Height).</summary>
    public uint[] PixelData { get; init; } = [];
    
    /// <summary>Primary palette (4 colors, YUV+Alpha).</summary>
    public CvdPaletteEntry[] Palette { get; init; } = new CvdPaletteEntry[4];
    
    /// <summary>Highlight palette (4 colors, YUV+Alpha).</summary>
    public CvdPaletteEntry[] HighlightPalette { get; init; } = new CvdPaletteEntry[4];
}

/// <summary>
/// CVD palette entry (YUV + Alpha).
/// </summary>
public readonly struct CvdPaletteEntry
{
    /// <summary>Luminance (Y).</summary>
    public byte Y { get; init; }
    
    /// <summary>Cb (U).</summary>
    public byte U { get; init; }
    
    /// <summary>Cr (V).</summary>
    public byte V { get; init; }
    
    /// <summary>Alpha (0=transparent, 255=opaque).</summary>
    public byte Alpha { get; init; }
    
    /// <summary>
    /// Convert to ARGB.
    /// </summary>
    public uint ToArgb()
    {
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
/// Decoder for CVD (Chaoji VCD) subtitles.
/// </summary>
public sealed class CvdSubtitleDecoder
{
    private int state;
    private byte[] spuBuffer = new byte[65536];
    private int spuBufferLength;
    private int spuSize;
    private int metadataOffset;
    private int metadataLength;
    private int imageOffset;
    private int firstFieldOffset;
    private int secondFieldOffset;
    private long duration;
    private int xStart;
    private int yStart;
    private int width;
    private int height;
    private readonly CvdPaletteEntry[] palette = new CvdPaletteEntry[4];
    private readonly CvdPaletteEntry[] highlightPalette = new CvdPaletteEntry[4];
    
    private const int StateEmpty = 0;
    private const int StatePartial = 1;
    private const int StateComplete = 2;
    
    /// <summary>
    /// Decode a CVD subtitle packet.
    /// </summary>
    /// <param name="data">Packet data (without the 1-byte header).</param>
    /// <param name="hasTimestamp">True if this is the first packet (has PTS).</param>
    /// <returns>Decoded subtitle or null if incomplete/invalid.</returns>
    public CvdSubtitle? Decode(ReadOnlySpan<byte> data, bool hasTimestamp)
    {
        // First packet detection is based on presence of timestamp
        if (state == StateEmpty && !hasTimestamp)
            return null;
        
        if (data.Length < 1)
            return null;
        
        // Skip 1-byte header
        var payload = data[1..];
        
        // First packet - parse header
        if (state == StateEmpty)
        {
            if (payload.Length < 4)
                return null;
            
            ParseHeader(payload);
        }
        
        // Append to buffer
        if (spuBufferLength + payload.Length > spuBuffer.Length)
            return null;
        
        payload.CopyTo(spuBuffer.AsSpan(spuBufferLength));
        spuBufferLength += payload.Length;
        
        // Check if complete
        if (spuBufferLength >= spuSize)
        {
            var spu = spuBuffer.AsSpan(0, spuBufferLength);
            ParseMetadata(spu);
            var result = RenderImage(spu);
            
            state = StateEmpty;
            spuBufferLength = 0;
            
            return result;
        }
        else
        {
            state = StatePartial;
            return null;
        }
    }
    
    /// <summary>
    /// Decode a complete CVD subtitle packet (all data in one buffer).
    /// </summary>
    public CvdSubtitle? DecodeComplete(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
            return null;
        
        // Skip 1-byte header
        var payload = data[1..];
        
        ParseHeader(payload);
        
        if (payload.Length < spuSize)
            return null;
        
        ParseMetadata(payload);
        return RenderImage(payload);
    }
    
    /// <summary>
    /// Reset decoder state.
    /// </summary>
    public void Reset()
    {
        state = StateEmpty;
        spuBufferLength = 0;
    }
    
    private void ParseHeader(ReadOnlySpan<byte> data)
    {
        spuSize = BinaryPrimitives.ReadUInt16BigEndian(data) + 4;
        metadataOffset = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        
        if (spuSize > metadataOffset)
            metadataLength = spuSize - metadataOffset;
        else
            metadataLength = 0;
        
        imageOffset = 4;
        
        // Reset values
        xStart = yStart = 0;
        width = height = 0;
        duration = 0;
        firstFieldOffset = 0;
        secondFieldOffset = 0;
        
        for (int i = 0; i < 4; i++)
        {
            palette[i] = default;
            highlightPalette[i] = default;
        }
    }
    
    private void ParseMetadata(ReadOnlySpan<byte> data)
    {
        if (metadataOffset >= data.Length)
            return;
        
        var meta = data[metadataOffset..];
        int remaining = Math.Min(metadataLength, meta.Length);
        int offset = 0;
        
        while (offset + 4 <= remaining)
        {
            byte tag = meta[offset];
            byte p1 = meta[offset + 1];
            byte p2 = meta[offset + 2];
            byte p3 = meta[offset + 3];
            
            switch (tag)
            {
                case 0x04: // Duration in 1/90000ths of a second
                    duration = ((p1 << 16) | (p2 << 8) | p3) * 1000000L / 90000;
                    break;
                    
                case 0x17: // Upper-left position
                    xStart = ((p1 & 0x0F) << 6) | (p2 >> 2);
                    yStart = ((p2 & 0x03) << 8) | p3;
                    break;
                    
                case 0x1F: // Bottom-right position
                    int lastX = ((p1 & 0x0F) << 6) | (p2 >> 2);
                    int lastY = ((p2 & 0x03) << 8) | p3;
                    width = lastX - xStart + 1;
                    height = lastY - yStart + 1;
                    break;
                    
                case 0x24: // Primary palette 0
                case 0x25: // Primary palette 1
                case 0x26: // Primary palette 2
                case 0x27: // Primary palette 3
                    int palIdx = tag - 0x24;
                    palette[palIdx] = new CvdPaletteEntry { Y = p1, U = p2, V = p3, Alpha = 255 };
                    break;
                    
                case 0x2C: // Highlight palette 0
                case 0x2D: // Highlight palette 1
                case 0x2E: // Highlight palette 2
                case 0x2F: // Highlight palette 3
                    int hpalIdx = tag - 0x2C;
                    highlightPalette[hpalIdx] = new CvdPaletteEntry { Y = p1, U = p2, V = p3, Alpha = 255 };
                    break;
                    
                case 0x37: // Transparency for primary palette
                    palette[0] = palette[0] with { Alpha = (byte)((p3 & 0x0F) << 4) };
                    palette[1] = palette[1] with { Alpha = (byte)((p3 >> 4) << 4) };
                    palette[2] = palette[2] with { Alpha = (byte)((p2 & 0x0F) << 4) };
                    palette[3] = palette[3] with { Alpha = (byte)((p2 >> 4) << 4) };
                    break;
                    
                case 0x3F: // Transparency for highlight palette
                    highlightPalette[0] = highlightPalette[0] with { Alpha = (byte)((p2 & 0x0F) << 4) };
                    highlightPalette[1] = highlightPalette[1] with { Alpha = (byte)((p2 >> 4) << 4) };
                    highlightPalette[2] = highlightPalette[2] with { Alpha = (byte)((p1 & 0x0F) << 4) };
                    highlightPalette[3] = highlightPalette[3] with { Alpha = (byte)((p1 >> 4) << 4) };
                    break;
                    
                case 0x47: // First field (even rows) offset
                    firstFieldOffset = ((p2 << 8) | p3) - imageOffset;
                    break;
                    
                case 0x4F: // Second field (odd rows) offset
                    secondFieldOffset = ((p2 << 8) | p3) - imageOffset;
                    break;
            }
            
            offset += 4;
        }
    }
    
    private CvdSubtitle? RenderImage(ReadOnlySpan<byte> data)
    {
        if (width <= 0 || height <= 0 || width > 2000 || height > 1200)
            return null;
        
        var pixels = new uint[width * height];
        
        // Precompute ARGB palette
        uint[] argbPalette = new uint[4];
        for (int i = 0; i < 4; i++)
            argbPalette[i] = palette[i].ToArgb();
        
        if (imageOffset >= data.Length)
            return null;
        
        var imageData = data[imageOffset..];
        var reader = new NibbleReader(imageData);
        
        // Interlaced: decode even rows first, then odd rows
        for (int field = 0; field < 2; field++)
        {
            // Position reader at field start
            if (field == 0 && firstFieldOffset > 0)
                reader = new NibbleReader(imageData[firstFieldOffset..]);
            else if (field == 1 && secondFieldOffset > 0)
                reader = new NibbleReader(imageData[secondFieldOffset..]);
            
            for (int row = field; row < height; row += 2)
            {
                int col = 0;
                while (col < width && !reader.IsEof)
                {
                    int nibble = reader.ReadNibble();
                    
                    if (nibble == 0)
                    {
                        // Fill rest of line with next color
                        int color = reader.ReadNibble();
                        if (color < 4)
                        {
                            while (col < width)
                            {
                                pixels[row * width + col] = argbPalette[color];
                                col++;
                            }
                        }
                    }
                    else
                    {
                        // Normal: 2-bit count + 2-bit color
                        int count = nibble >> 2;
                        int color = nibble & 0x03;
                        
                        count = Math.Min(count, width - col);
                        for (int i = 0; i < count && col < width; i++)
                        {
                            pixels[row * width + col] = argbPalette[color];
                            col++;
                        }
                    }
                }
                
                reader.AlignToByte();
            }
        }
        
        return new CvdSubtitle
        {
            X = xStart * 3 / 4, // CVD uses 4:3 aspect ratio correction
            Y = yStart,
            Width = width,
            Height = height,
            DurationMicroseconds = duration,
            PixelData = pixels,
            Palette = (CvdPaletteEntry[])palette.Clone(),
            HighlightPalette = (CvdPaletteEntry[])highlightPalette.Clone()
        };
    }
    
    /// <summary>
    /// Probe data to check if it could be CVD subtitle.
    /// </summary>
    public static bool Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
            return false;
        
        // After 1-byte header, first 2 bytes are SPU size
        int size = BinaryPrimitives.ReadUInt16BigEndian(data[1..]) + 4;
        int metaOffset = BinaryPrimitives.ReadUInt16BigEndian(data[3..]);
        
        // Size should be reasonable and metadata offset should be within size
        return size > 4 && size < 65536 && metaOffset > 0 && metaOffset <= size;
    }
    
    /// <summary>
    /// Nibble reader for CVD RLE decoding.
    /// </summary>
    private ref struct NibbleReader
    {
        private readonly ReadOnlySpan<byte> data;
        private int byteOffset;
        private bool highNibble;
        
        public NibbleReader(ReadOnlySpan<byte> data)
        {
            this.data = data;
            byteOffset = 0;
            highNibble = true;
        }
        
        public bool IsEof => byteOffset >= data.Length;
        
        public int ReadNibble()
        {
            if (byteOffset >= data.Length)
                return 0;
            
            int nibble;
            if (highNibble)
            {
                nibble = data[byteOffset] >> 4;
                highNibble = false;
            }
            else
            {
                nibble = data[byteOffset] & 0x0F;
                highNibble = true;
                byteOffset++;
            }
            
            return nibble;
        }
        
        public void AlignToByte()
        {
            if (!highNibble)
            {
                highNibble = true;
                byteOffset++;
            }
        }
    }
}

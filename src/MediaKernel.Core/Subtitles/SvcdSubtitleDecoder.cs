// MediaKernel - SVCD/OGT Subtitle Decoder
// Overlay Graphics Text subtitles from SVCD (Super Video CD) discs
// Reference: VLC svcdsub.c

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Decoded SVCD/OGT subtitle image.
/// </summary>
public sealed class SvcdSubtitle
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
    
    /// <summary>Subtitle image number.</summary>
    public int ImageNumber { get; init; }
    
    /// <summary>ARGB pixel data (Width * Height).</summary>
    public uint[] PixelData { get; init; } = [];
    
    /// <summary>Palette (4 colors, YUV+Alpha).</summary>
    public SvcdPaletteEntry[] Palette { get; init; } = new SvcdPaletteEntry[4];
}

/// <summary>
/// SVCD palette entry (YUV + Transparency).
/// </summary>
public readonly struct SvcdPaletteEntry
{
    /// <summary>Luminance (Y).</summary>
    public byte Y { get; init; }
    
    /// <summary>Cb (U).</summary>
    public byte U { get; init; }
    
    /// <summary>Cr (V).</summary>
    public byte V { get; init; }
    
    /// <summary>Transparency (0=opaque, 255=transparent).</summary>
    public byte Transparency { get; init; }
    
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
        int a = 255 - Transparency;
        
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp(int value) => value < 0 ? 0 : (value > 255 ? 255 : value);
}

/// <summary>
/// Decoder for SVCD/OGT (Overlay Graphics Text) subtitles.
/// </summary>
public sealed class SvcdSubtitleDecoder
{
    private int state;
    private byte[] spuBuffer = new byte[65536];
    private int spuBufferLength;
    private int spuSize;
    private int imageNumber;
    private int packetNumber;
    private int imageOffset;
    private int secondFieldOffset;
    private long duration;
    private int xStart;
    private int yStart;
    private int width;
    private int height;
    private readonly SvcdPaletteEntry[] palette = new SvcdPaletteEntry[4];
    
    private const int StateEmpty = 0;
    private const int StatePartial = 1;
    private const int StateComplete = 2;
    
    /// <summary>
    /// Decode an SVCD subtitle packet.
    /// </summary>
    /// <param name="data">Raw packet data starting with channel byte.</param>
    /// <returns>Decoded subtitle or null if incomplete/invalid.</returns>
    public SvcdSubtitle? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
            return null;
        
        // Skip dummy ES header (2 bytes), then read packet info
        int offset = 2;
        
        bool isComplete = (data[offset] & 0x80) != 0;
        int packetNum = data[offset++] & 0x7F;
        int imageNum = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        
        // Check sequence
        int expectedImage;
        int expectedPacket;
        
        if (state == StateEmpty)
        {
            expectedImage = imageNumber + 1;
            expectedPacket = 0;
        }
        else
        {
            expectedImage = imageNumber;
            expectedPacket = packetNumber + 1;
        }
        
        imageNumber = imageNum;
        packetNumber = packetNum;
        
        // Parse header on first packet
        if (packetNum == 0)
        {
            if (!ParseHeader(data[offset..]))
                return null;
        }
        
        // Append payload to buffer
        var payload = data[offset..];
        if (spuBufferLength + payload.Length > spuBuffer.Length)
            return null;
        
        payload.CopyTo(spuBuffer.AsSpan(spuBufferLength));
        spuBufferLength += payload.Length;
        
        if (isComplete)
        {
            state = StateComplete;
            var result = RenderImage(spuBuffer.AsSpan(0, spuBufferLength));
            
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
    /// Decode a complete SVCD subtitle packet (all data in one buffer).
    /// </summary>
    public SvcdSubtitle? DecodeComplete(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30)
            return null;
        
        // Skip dummy ES header and packet header
        int offset = 5; // 2 dummy + 1 packet + 2 image
        
        if (!ParseHeader(data[offset..]))
            return null;
        
        return RenderImage(data[offset..]);
    }
    
    /// <summary>
    /// Reset decoder state.
    /// </summary>
    public void Reset()
    {
        state = StateEmpty;
        spuBufferLength = 0;
        imageNumber = -1;
        packetNumber = 0;
    }
    
    private bool ParseHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 27)
            return false;
        
        int offset = 0;
        
        spuSize = BinaryPrimitives.ReadUInt16BigEndian(data);
        offset += 2;
        
        byte options = data[offset++];
        offset++; // Skip unused byte
        
        // Duration (optional, based on options bit 3)
        if ((options & 0x08) != 0)
        {
            if (data.Length < offset + 4)
                return false;
            uint durationTicks = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            duration = (long)(durationTicks * 1000000.0 / 90000.0);
            offset += 4;
        }
        else
        {
            duration = 0; // Ephemeral subtitle
        }
        
        if (data.Length < offset + 25)
            return false;
        
        xStart = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        yStart = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        width = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        height = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        
        // Palette: 4 entries, each Y, Cr, Cb, T
        for (int i = 0; i < 4; i++)
        {
            palette[i] = new SvcdPaletteEntry
            {
                Y = data[offset++],
                V = data[offset++], // Cr
                U = data[offset++], // Cb
                Transparency = data[offset++]
            };
        }
        
        byte command = data[offset++];
        
        // Skip shift duration if command indicates shift
        if (command != 0)
        {
            if (data.Length < offset + 4)
                return false;
            offset += 4;
        }
        
        if (data.Length < offset + 2)
            return false;
        
        secondFieldOffset = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        
        imageOffset = offset;
        
        return true;
    }
    
    private SvcdSubtitle? RenderImage(ReadOnlySpan<byte> data)
    {
        if (width <= 0 || height <= 0 || width > 2000 || height > 1200)
            return null;
        
        if (imageOffset >= data.Length)
            return null;
        
        var pixels = new uint[width * height];
        
        // Precompute ARGB palette
        uint[] argbPalette = new uint[4];
        for (int i = 0; i < 4; i++)
            argbPalette[i] = palette[i].ToArgb();
        
        var imageData = data[imageOffset..];
        
        // Decode interlaced image
        for (int field = 0; field < 2; field++)
        {
            ReadOnlySpan<byte> fieldData;
            if (field == 0)
            {
                fieldData = imageData;
            }
            else if (secondFieldOffset > 0 && secondFieldOffset < imageData.Length)
            {
                fieldData = imageData[secondFieldOffset..];
            }
            else
            {
                continue;
            }
            
            var reader = new BitReader2(fieldData);
            
            for (int row = field; row < height; row += 2)
            {
                int col = 0;
                while (col < width && !reader.IsEof)
                {
                    int color = reader.ReadBits(2);
                    
                    if (color == 0)
                    {
                        // RLE: 0 followed by count-1 (2 bits)
                        int count = reader.ReadBits(2);
                        if (count > 0)
                        {
                            count = Math.Min(count, width - col - 1);
                            for (int i = 0; i <= count && col < width; i++)
                            {
                                pixels[row * width + col] = argbPalette[0];
                                col++;
                            }
                        }
                        else
                        {
                            pixels[row * width + col] = argbPalette[0];
                            col++;
                        }
                    }
                    else
                    {
                        pixels[row * width + col] = argbPalette[color];
                        col++;
                    }
                }
                
                reader.AlignToByte();
            }
        }
        
        return new SvcdSubtitle
        {
            X = xStart,
            Y = yStart,
            Width = width,
            Height = height,
            DurationMicroseconds = duration,
            ImageNumber = imageNumber,
            PixelData = pixels,
            Palette = (SvcdPaletteEntry[])palette.Clone()
        };
    }
    
    /// <summary>
    /// Probe data to check if it could be SVCD subtitle.
    /// </summary>
    public static bool Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
            return false;
        
        // Skip 2-byte dummy, check packet number
        int packetByte = data[2];
        int imageNum = BinaryPrimitives.ReadUInt16BigEndian(data[3..]);
        
        // Check if it looks valid
        if (data.Length >= 7)
        {
            int spuSize = BinaryPrimitives.ReadUInt16BigEndian(data[5..]);
            return spuSize > 0 && spuSize < 65536;
        }
        
        return imageNum >= 0;
    }
    
    /// <summary>
    /// 2-bit reader for SVCD RLE decoding.
    /// </summary>
    private ref struct BitReader2
    {
        private readonly ReadOnlySpan<byte> data;
        private int byteOffset;
        private int bitOffset;
        
        public BitReader2(ReadOnlySpan<byte> data)
        {
            this.data = data;
            byteOffset = 0;
            bitOffset = 0;
        }
        
        public bool IsEof => byteOffset >= data.Length;
        
        public int ReadBits(int count)
        {
            int result = 0;
            for (int i = 0; i < count; i++)
            {
                if (byteOffset >= data.Length)
                    return result;
                
                int bit = (data[byteOffset] >> (7 - bitOffset)) & 1;
                result = (result << 1) | bit;
                
                bitOffset++;
                if (bitOffset >= 8)
                {
                    bitOffset = 0;
                    byteOffset++;
                }
            }
            return result;
        }
        
        public void AlignToByte()
        {
            if (bitOffset > 0)
            {
                bitOffset = 0;
                byteOffset++;
            }
        }
    }
}

// SVCD (OGT - Overlay Graphics Text) Subtitle Decoder
// Decodes Philips OGT subtitles used on Super Video CDs

using System;
using System.Buffers.Binary;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Decoded SVCD subtitle image.
/// </summary>
public sealed class SvcdSubtitleImage
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
    
    /// <summary>Image number in stream.</summary>
    public int ImageNumber { get; init; }
}

/// <summary>
/// Decoder for SVCD (Super VCD) OGT subtitles.
/// Uses Philips Overlay Graphics Text format with 2-bit palette indices
/// and simple RLE for transparent (color 0) runs.
/// </summary>
public sealed class SvcdSubDecoder
{
    private readonly CvdColor[] palette = new CvdColor[4];
    private long basePts;
    
    /// <summary>
    /// Sets the base PTS for timestamp calculations.
    /// </summary>
    public void SetBasePts(long ptsMicroseconds)
    {
        basePts = ptsMicroseconds;
    }
    
    /// <summary>
    /// Decodes an SVCD subtitle packet.
    /// </summary>
    /// <remarks>
    /// SVCD packet format (after 5-byte PES header stripped):
    /// - u16: packet size
    /// - byte: options (bit 3 = has duration)
    /// - byte: unused
    /// - [u32: duration in 90kHz ticks (optional)]
    /// - u16: x position
    /// - u16: y position
    /// - u16: width
    /// - u16: height
    /// - byte[16]: palette (4 entries x 4 bytes: Y, Cr, Cb, T)
    /// - byte: command
    /// - [u32: shift duration (if command)]
    /// - u16: second field offset
    /// - byte[]: RLE image data
    /// </remarks>
    public SvcdSubtitleImage? DecodePacket(ReadOnlySpan<byte> data, int imageNumber = 0)
    {
        if (data.Length < 4)
            return null;
        
        int offset = 0;
        
        // Packet size
        int packetSize = BinaryPrimitives.ReadUInt16BigEndian(data);
        if (packetSize > data.Length)
            packetSize = data.Length;
        offset += 2;
        
        if (offset + 2 > data.Length)
            return null;
        
        // Options and unused byte
        byte options = data[offset++];
        offset++;  // Skip unused
        
        // Duration (optional, indicated by bit 3 of options)
        long durationMicroseconds = 0;
        if ((options & 0x08) != 0)
        {
            if (offset + 4 > data.Length)
                return null;
            int durationTicks = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset));
            durationMicroseconds = (durationTicks * 1_000_000L) / 90_000;
            offset += 4;
        }
        
        // Position and dimensions
        if (offset + 8 > data.Length)
            return null;
        
        int x = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset));
        int y = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2));
        int width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 4));
        int height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 6));
        offset += 8;
        
        if (width <= 0 || height <= 0 || width > 2048 || height > 2048)
            return null;
        
        // Palette (4 entries x 4 bytes: Y, Cr, Cb, Transparency)
        if (offset + 16 > data.Length)
            return null;
        
        for (int i = 0; i < 4; i++)
        {
            byte py = data[offset];      // Y
            byte cr = data[offset + 1];  // Cr (V)
            byte cb = data[offset + 2];  // Cb (U)
            byte t = data[offset + 3];   // Transparency (0 = transparent)
            
            // Convert transparency to alpha (SVCD: 0 = transparent, 255 = opaque)
            byte alpha = t;
            palette[i] = new CvdColor(py, cb, cr, alpha);
            offset += 4;
        }
        
        // Command byte
        if (offset >= data.Length)
            return null;
        
        byte command = data[offset++];
        
        // Skip shift duration if command indicates shift
        if (command != 0)
        {
            if (offset + 4 > data.Length)
                return null;
            offset += 4;  // Skip shift duration
        }
        
        // Second field offset
        if (offset + 2 > data.Length)
            return null;
        
        int secondFieldOffset = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset));
        offset += 2;
        
        // Image data starts here
        int imageOffset = offset;
        
        // Decode RLE image
        var pixels = DecodeRle(data.Slice(imageOffset), width, height, secondFieldOffset);
        
        return new SvcdSubtitleImage
        {
            StartTimeMicroseconds = basePts,
            DurationMicroseconds = durationMicroseconds,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Pixels = pixels,
            ImageNumber = imageNumber
        };
    }
    
    /// <summary>
    /// Decodes SVCD RLE-compressed interlaced image data.
    /// SVCD uses 2-bit palette indices. Color 0 has special RLE:
    /// When color 0 is read, the next 2 bits give (count - 1), 
    /// meaning runs of 1-4 transparent pixels.
    /// </summary>
    private byte[] DecodeRle(ReadOnlySpan<byte> data, int width, int height, int secondFieldOffset)
    {
        var pixels = new byte[width * height * 4];
        var reader = new SvcdBitReader(data);
        
        // Process interlaced fields
        for (int field = 0; field < 2; field++)
        {
            if (field == 1)
            {
                // Reset to second field position
                reader = new SvcdBitReader(data.Slice(secondFieldOffset));
            }
            
            for (int row = field; row < height; row += 2)
            {
                for (int column = 0; column < width;)
                {
                    int color = reader.ReadBits(2);
                    
                    if (color == 0)
                    {
                        // Color 0 with RLE: next 2 bits = count - 1
                        int count = reader.ReadBits(2);
                        if (count > 0)
                        {
                            // Fill count+1 pixels with color 0 (transparent)
                            int fillCount = Math.Min(count + 1, width - column);
                            FillPixels(pixels, row * width + column, fillCount, palette[0]);
                            column += fillCount;
                            continue;
                        }
                        // count == 0 means just one pixel of color 0
                    }
                    
                    // Single pixel
                    FillPixels(pixels, row * width + column, 1, palette[color]);
                    column++;
                }
                
                // Align to byte boundary after each line
                reader.AlignToByte();
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
            pixels[pixelOffset] = (byte)(bgra & 0xFF);             // B
            pixels[pixelOffset + 1] = (byte)((bgra >> 8) & 0xFF);  // G
            pixels[pixelOffset + 2] = (byte)((bgra >> 16) & 0xFF); // R
            pixels[pixelOffset + 3] = (byte)((bgra >> 24) & 0xFF); // A
            pixelOffset += 4;
        }
    }
}

/// <summary>
/// Bit reader for SVCD RLE data.
/// </summary>
internal ref struct SvcdBitReader
{
    private readonly ReadOnlySpan<byte> data;
    private int byteOffset;
    private int bitOffset;  // 0-7, bits remaining in current byte (from MSB)
    
    public SvcdBitReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        byteOffset = 0;
        bitOffset = 0;
    }
    
    public int ReadBits(int count)
    {
        if (byteOffset >= data.Length)
            return 0;
        
        int result = 0;
        int bitsRemaining = count;
        
        while (bitsRemaining > 0 && byteOffset < data.Length)
        {
            int bitsAvailable = 8 - bitOffset;
            int bitsToRead = Math.Min(bitsRemaining, bitsAvailable);
            
            // Extract bits from current byte (MSB first)
            int mask = ((1 << bitsToRead) - 1) << (bitsAvailable - bitsToRead);
            int bits = (data[byteOffset] & mask) >> (bitsAvailable - bitsToRead);
            
            result = (result << bitsToRead) | bits;
            
            bitOffset += bitsToRead;
            bitsRemaining -= bitsToRead;
            
            if (bitOffset >= 8)
            {
                byteOffset++;
                bitOffset = 0;
            }
        }
        
        return result;
    }
    
    public void AlignToByte()
    {
        if (bitOffset != 0)
        {
            byteOffset++;
            bitOffset = 0;
        }
    }
}

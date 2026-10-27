// MediaKernel - BMP Image Demuxer
// Demuxes Windows Bitmap (BMP) files.
// Reference: https://docs.microsoft.com/en-us/windows/win32/gdi/bitmap-storage

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// BMP compression types.
/// </summary>
public enum BmpCompression
{
    /// <summary>Uncompressed RGB.</summary>
    Rgb = 0,
    
    /// <summary>RLE-8 encoded (8-bit only).</summary>
    Rle8 = 1,
    
    /// <summary>RLE-4 encoded (4-bit only).</summary>
    Rle4 = 2,
    
    /// <summary>Bitfields (16/32-bit).</summary>
    Bitfields = 3,
    
    /// <summary>JPEG compression (not commonly used).</summary>
    Jpeg = 4,
    
    /// <summary>PNG compression (not commonly used).</summary>
    Png = 5,
    
    /// <summary>RGBA with alpha bitfield.</summary>
    AlphaBitfields = 6
}

/// <summary>
/// Result from probing a stream for BMP content.
/// </summary>
public readonly struct BmpProbeResult
{
    /// <summary>Whether valid BMP content was detected.</summary>
    public bool IsBmp { get; init; }
    
    /// <summary>Image width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Image height in pixels (absolute value, may be negative for top-down).</summary>
    public int Height { get; init; }
    
    /// <summary>Bits per pixel.</summary>
    public int BitsPerPixel { get; init; }
    
    /// <summary>Compression type.</summary>
    public BmpCompression Compression { get; init; }
    
    /// <summary>Whether image is top-down (negative height).</summary>
    public bool IsTopDown { get; init; }
    
    /// <summary>Size of the file in bytes.</summary>
    public int FileSize { get; init; }
}

/// <summary>
/// A decoded BMP frame.
/// </summary>
public sealed class BmpFrame
{
    /// <summary>Frame image data in BGRA format (4 bytes per pixel).</summary>
    public byte[] ImageData { get; init; } = [];
    
    /// <summary>Image width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Image height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Source bits per pixel.</summary>
    public int SourceBitsPerPixel { get; init; }
    
    /// <summary>Whether the source had an alpha channel.</summary>
    public bool HasAlpha { get; init; }
}

/// <summary>
/// Demuxer for Windows Bitmap (BMP) files.
/// Supports:
/// - 1, 4, 8, 16, 24, and 32-bit images
/// - Top-down and bottom-up orientations
/// - RLE-4 and RLE-8 compression
/// - Bitfield masks for 16/32-bit images
/// </summary>
public sealed class BmpDemuxer : IDisposable
{
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private bool isParsed;
    private bool isDisposed;
    private bool frameRead;
    
    // BMP header fields
    private int fileSize;
    private int dataOffset;
    private int infoHeaderSize;
    private int width;
    private int height;
    private bool isTopDown;
    private int bitsPerPixel;
    private BmpCompression compression;
    private int imageDataSize;
    private int colorsUsed;
    
    // Color table for indexed images
    private byte[]? colorTable;
    
    // Bitfield masks for 16/32-bit images
    private uint redMask;
    private uint greenMask;
    private uint blueMask;
    private uint alphaMask;
    private int redShift;
    private int greenShift;
    private int blueShift;
    private int alphaShift;
    private int redScale;
    private int greenScale;
    private int blueScale;
    private int alphaScale;
    
    /// <summary>
    /// Creates a new BMP demuxer.
    /// </summary>
    public BmpDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
    }
    
    /// <summary>Gets whether the BMP has been successfully parsed.</summary>
    public bool IsParsed => isParsed;
    
    /// <summary>Gets the image width.</summary>
    public int Width => width;
    
    /// <summary>Gets the image height.</summary>
    public int Height => height;
    
    /// <summary>Gets the bits per pixel.</summary>
    public int BitsPerPixel => bitsPerPixel;
    
    /// <summary>Gets the compression type.</summary>
    public BmpCompression Compression => compression;
    
    /// <summary>Gets whether all frames have been read.</summary>
    public bool IsEndOfStream => frameRead;
    
    /// <summary>Gets the codec name.</summary>
    public string Codec => "bmp";
    
    /// <summary>
    /// Probe a stream for BMP content without fully parsing.
    /// </summary>
    public static BmpProbeResult Probe(Stream stream)
    {
        Span<byte> header = stackalloc byte[54];
        
        long originalPosition = stream.Position;
        int bytesRead = stream.Read(header);
        stream.Position = originalPosition;
        
        if (bytesRead < 54)
            return new BmpProbeResult { IsBmp = false };
        
        return ProbeHeader(header);
    }
    
    /// <summary>
    /// Probe a buffer for BMP content.
    /// </summary>
    public static BmpProbeResult Probe(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 54)
            return new BmpProbeResult { IsBmp = false };
        
        return ProbeHeader(buffer);
    }
    
    private static BmpProbeResult ProbeHeader(ReadOnlySpan<byte> header)
    {
        // Check BM signature
        if (header[0] != 'B' || header[1] != 'M')
            return new BmpProbeResult { IsBmp = false };
        
        int fileSize = BinaryPrimitives.ReadInt32LittleEndian(header[2..]);
        int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(header[10..]);
        int infoHeaderSize = BinaryPrimitives.ReadInt32LittleEndian(header[14..]);
        
        // Validate header size (BITMAPINFOHEADER = 40, BITMAPV4HEADER = 108, BITMAPV5HEADER = 124)
        if (infoHeaderSize < 40)
            return new BmpProbeResult { IsBmp = false };
        
        int w = BinaryPrimitives.ReadInt32LittleEndian(header[18..]);
        int h = BinaryPrimitives.ReadInt32LittleEndian(header[22..]);
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        var comp = (BmpCompression)BinaryPrimitives.ReadInt32LittleEndian(header[30..]);
        
        // Validate dimensions and bit depth
        if (w <= 0 || w > 65535)
            return new BmpProbeResult { IsBmp = false };
        
        int absHeight = h < 0 ? -h : h;
        if (absHeight <= 0 || absHeight > 65535)
            return new BmpProbeResult { IsBmp = false };
        
        // Validate bit depth
        if (bpp != 1 && bpp != 4 && bpp != 8 && bpp != 16 && bpp != 24 && bpp != 32)
            return new BmpProbeResult { IsBmp = false };
        
        return new BmpProbeResult
        {
            IsBmp = true,
            Width = w,
            Height = absHeight,
            BitsPerPixel = bpp,
            Compression = comp,
            IsTopDown = h < 0,
            FileSize = fileSize
        };
    }
    
    /// <summary>
    /// Parse the BMP file header and DIB header.
    /// </summary>
    public bool Parse()
    {
        if (isParsed) return true;
        
        try
        {
            Span<byte> header = stackalloc byte[14];
            if (stream.Read(header) < 14) return false;
            
            // Validate BM signature
            if (header[0] != 'B' || header[1] != 'M')
                return false;
            
            fileSize = BinaryPrimitives.ReadInt32LittleEndian(header[2..]);
            dataOffset = BinaryPrimitives.ReadInt32LittleEndian(header[10..]);
            
            // Read DIB header size first
            Span<byte> headerSizeBytes = stackalloc byte[4];
            if (stream.Read(headerSizeBytes) < 4) return false;
            infoHeaderSize = BinaryPrimitives.ReadInt32LittleEndian(headerSizeBytes);
            
            if (infoHeaderSize < 40) return false; // Minimum BITMAPINFOHEADER
            
            // Read rest of DIB header
            int dibHeaderRemaining = infoHeaderSize - 4;
            byte[] dibHeader = new byte[dibHeaderRemaining];
            if (stream.Read(dibHeader) < dibHeaderRemaining) return false;
            
            width = BinaryPrimitives.ReadInt32LittleEndian(dibHeader.AsSpan(0));
            int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dibHeader.AsSpan(4));
            
            isTopDown = rawHeight < 0;
            height = rawHeight < 0 ? -rawHeight : rawHeight;
            
            // planes at offset 8 (unused)
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dibHeader.AsSpan(10));
            compression = (BmpCompression)BinaryPrimitives.ReadInt32LittleEndian(dibHeader.AsSpan(12));
            imageDataSize = BinaryPrimitives.ReadInt32LittleEndian(dibHeader.AsSpan(16));
            // horizontal resolution at 20
            // vertical resolution at 24
            colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(dibHeader.AsSpan(28));
            
            // Validate
            if (width <= 0 || height <= 0)
                return false;
            
            if (bitsPerPixel != 1 && bitsPerPixel != 4 && bitsPerPixel != 8 && 
                bitsPerPixel != 16 && bitsPerPixel != 24 && bitsPerPixel != 32)
                return false;
            
            // Read bitfields for 16/32-bit images with BITFIELDS compression
            if (compression == BmpCompression.Bitfields || compression == BmpCompression.AlphaBitfields)
            {
                if (bitsPerPixel != 16 && bitsPerPixel != 32)
                    return false;
                
                // Bitfields may be in the header (V4/V5) or after it
                if (infoHeaderSize >= 56)
                {
                    // Bitfields in extended header
                    redMask = BinaryPrimitives.ReadUInt32LittleEndian(dibHeader.AsSpan(36));
                    greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dibHeader.AsSpan(40));
                    blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dibHeader.AsSpan(44));
                    
                    if (infoHeaderSize >= 60 || compression == BmpCompression.AlphaBitfields)
                    {
                        alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(dibHeader.AsSpan(48));
                    }
                }
                else
                {
                    // Bitfields after header
                    Span<byte> masks = stackalloc byte[16];
                    int masksToRead = compression == BmpCompression.AlphaBitfields ? 16 : 12;
                    if (stream.Read(masks[..masksToRead]) < masksToRead) return false;
                    
                    redMask = BinaryPrimitives.ReadUInt32LittleEndian(masks);
                    greenMask = BinaryPrimitives.ReadUInt32LittleEndian(masks[4..]);
                    blueMask = BinaryPrimitives.ReadUInt32LittleEndian(masks[8..]);
                    
                    if (masksToRead == 16)
                        alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(masks[12..]);
                }
                
                CalculateMaskShifts();
            }
            else if (bitsPerPixel == 16)
            {
                // Default 16-bit masks: RGB555
                redMask = 0x7C00;
                greenMask = 0x03E0;
                blueMask = 0x001F;
                alphaMask = 0;
                CalculateMaskShifts();
            }
            else if (bitsPerPixel == 32 && compression == BmpCompression.Rgb)
            {
                // Default 32-bit: BGRX (no alpha)
                blueMask = 0x000000FF;
                greenMask = 0x0000FF00;
                redMask = 0x00FF0000;
                alphaMask = 0xFF000000;
                CalculateMaskShifts();
            }
            
            // Read color table for indexed images
            if (bitsPerPixel <= 8)
            {
                int colorCount = colorsUsed > 0 ? colorsUsed : (1 << bitsPerPixel);
                colorTable = new byte[colorCount * 4];
                
                // Seek to color table position
                stream.Position = 14 + infoHeaderSize;
                if (stream.Read(colorTable) < colorTable.Length)
                    return false;
            }
            
            isParsed = true;
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    private void CalculateMaskShifts()
    {
        redShift = CountTrailingZeros(redMask);
        greenShift = CountTrailingZeros(greenMask);
        blueShift = CountTrailingZeros(blueMask);
        alphaShift = alphaMask != 0 ? CountTrailingZeros(alphaMask) : 0;
        
        int redBits = CountBits(redMask);
        int greenBits = CountBits(greenMask);
        int blueBits = CountBits(blueMask);
        int alphaBits = alphaMask != 0 ? CountBits(alphaMask) : 0;
        
        redScale = redBits > 0 ? 255 / ((1 << redBits) - 1) : 0;
        greenScale = greenBits > 0 ? 255 / ((1 << greenBits) - 1) : 0;
        blueScale = blueBits > 0 ? 255 / ((1 << blueBits) - 1) : 0;
        alphaScale = alphaBits > 0 ? 255 / ((1 << alphaBits) - 1) : 0;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountTrailingZeros(uint value)
    {
        if (value == 0) return 32;
        int count = 0;
        while ((value & 1) == 0)
        {
            value >>= 1;
            count++;
        }
        return count;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountBits(uint value)
    {
        int count = 0;
        while (value != 0)
        {
            count += (int)(value & 1);
            value >>= 1;
        }
        return count;
    }
    
    /// <summary>
    /// Read and decode the BMP image.
    /// </summary>
    public BmpFrame? ReadFrame()
    {
        if (!isParsed || frameRead)
            return null;
        
        frameRead = true;
        
        try
        {
            // Seek to pixel data
            stream.Position = dataOffset;
            
            // Calculate row size (padded to 4-byte boundary)
            int rowSizeBytes = ((width * bitsPerPixel + 31) / 32) * 4;
            
            // Read all pixel data
            int dataSize = imageDataSize > 0 ? imageDataSize : rowSizeBytes * height;
            byte[] pixelData = new byte[dataSize];
            stream.Read(pixelData);
            
            // Allocate output BGRA buffer
            byte[] output = new byte[width * height * 4];
            
            // Decode based on compression and bit depth
            if (compression == BmpCompression.Rle8)
            {
                DecodeRle8(pixelData, output);
            }
            else if (compression == BmpCompression.Rle4)
            {
                DecodeRle4(pixelData, output);
            }
            else
            {
                DecodeUncompressed(pixelData, output, rowSizeBytes);
            }
            
            bool hasAlpha = alphaMask != 0 && bitsPerPixel == 32;
            
            return new BmpFrame
            {
                ImageData = output,
                Width = width,
                Height = height,
                SourceBitsPerPixel = bitsPerPixel,
                HasAlpha = hasAlpha
            };
        }
        catch
        {
            return null;
        }
    }
    
    private void DecodeUncompressed(byte[] pixelData, byte[] output, int rowSizeBytes)
    {
        for (int y = 0; y < height; y++)
        {
            // BMP rows are stored bottom-up by default
            int srcY = isTopDown ? y : (height - 1 - y);
            int srcRowOffset = srcY * rowSizeBytes;
            int dstRowOffset = y * width * 4;
            
            DecodeRow(pixelData.AsSpan(srcRowOffset, rowSizeBytes), output.AsSpan(dstRowOffset, width * 4));
        }
    }
    
    private void DecodeRow(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int x = 0;
        int srcOffset = 0;
        
        switch (bitsPerPixel)
        {
            case 1:
                DecodePaletted1(src, dst, ref x, ref srcOffset);
                break;
            case 4:
                DecodePaletted4(src, dst, ref x, ref srcOffset);
                break;
            case 8:
                DecodePaletted8(src, dst, ref x, ref srcOffset);
                break;
            case 16:
                Decode16Bit(src, dst, ref x, ref srcOffset);
                break;
            case 24:
                Decode24Bit(src, dst, ref x, ref srcOffset);
                break;
            case 32:
                Decode32Bit(src, dst, ref x, ref srcOffset);
                break;
        }
    }
    
    private void DecodePaletted1(ReadOnlySpan<byte> src, Span<byte> dst, ref int x, ref int srcOffset)
    {
        while (x < width && srcOffset < src.Length)
        {
            byte b = src[srcOffset++];
            for (int bit = 7; bit >= 0 && x < width; bit--, x++)
            {
                int colorIndex = (b >> bit) & 1;
                WritePaletteColor(dst, x * 4, colorIndex);
            }
        }
    }
    
    private void DecodePaletted4(ReadOnlySpan<byte> src, Span<byte> dst, ref int x, ref int srcOffset)
    {
        while (x < width && srcOffset < src.Length)
        {
            byte b = src[srcOffset++];
            
            // High nibble
            if (x < width)
            {
                int colorIndex = (b >> 4) & 0x0F;
                WritePaletteColor(dst, x * 4, colorIndex);
                x++;
            }
            
            // Low nibble
            if (x < width)
            {
                int colorIndex = b & 0x0F;
                WritePaletteColor(dst, x * 4, colorIndex);
                x++;
            }
        }
    }
    
    private void DecodePaletted8(ReadOnlySpan<byte> src, Span<byte> dst, ref int x, ref int srcOffset)
    {
        while (x < width && srcOffset < src.Length)
        {
            int colorIndex = src[srcOffset++];
            WritePaletteColor(dst, x * 4, colorIndex);
            x++;
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WritePaletteColor(Span<byte> dst, int offset, int colorIndex)
    {
        if (colorTable == null || colorIndex * 4 + 3 >= colorTable.Length)
        {
            // Default to black
            dst[offset] = 0;
            dst[offset + 1] = 0;
            dst[offset + 2] = 0;
            dst[offset + 3] = 255;
            return;
        }
        
        int paletteOffset = colorIndex * 4;
        dst[offset] = colorTable[paletteOffset];         // B
        dst[offset + 1] = colorTable[paletteOffset + 1]; // G
        dst[offset + 2] = colorTable[paletteOffset + 2]; // R
        dst[offset + 3] = 255;                            // A (palette entries have no alpha)
    }
    
    private void Decode16Bit(ReadOnlySpan<byte> src, Span<byte> dst, ref int x, ref int srcOffset)
    {
        while (x < width && srcOffset + 1 < src.Length)
        {
            uint pixel = BinaryPrimitives.ReadUInt16LittleEndian(src[srcOffset..]);
            srcOffset += 2;
            
            int r = (int)((pixel & redMask) >> redShift);
            int g = (int)((pixel & greenMask) >> greenShift);
            int b = (int)((pixel & blueMask) >> blueShift);
            
            // Scale to 8-bit
            r = Math.Min(255, r * redScale);
            g = Math.Min(255, g * greenScale);
            b = Math.Min(255, b * blueScale);
            
            int dstOffset = x * 4;
            dst[dstOffset] = (byte)b;
            dst[dstOffset + 1] = (byte)g;
            dst[dstOffset + 2] = (byte)r;
            dst[dstOffset + 3] = 255;
            
            x++;
        }
    }
    
    private void Decode24Bit(ReadOnlySpan<byte> src, Span<byte> dst, ref int x, ref int srcOffset)
    {
        while (x < width && srcOffset + 2 < src.Length)
        {
            int dstOffset = x * 4;
            dst[dstOffset] = src[srcOffset];         // B
            dst[dstOffset + 1] = src[srcOffset + 1]; // G
            dst[dstOffset + 2] = src[srcOffset + 2]; // R
            dst[dstOffset + 3] = 255;                 // A
            srcOffset += 3;
            x++;
        }
    }
    
    private void Decode32Bit(ReadOnlySpan<byte> src, Span<byte> dst, ref int x, ref int srcOffset)
    {
        bool hasAlphaChannel = alphaMask != 0;
        
        while (x < width && srcOffset + 3 < src.Length)
        {
            int dstOffset = x * 4;
            
            if (compression == BmpCompression.Bitfields || compression == BmpCompression.AlphaBitfields)
            {
                uint pixel = BinaryPrimitives.ReadUInt32LittleEndian(src[srcOffset..]);
                
                int r = (int)((pixel & redMask) >> redShift);
                int g = (int)((pixel & greenMask) >> greenShift);
                int b = (int)((pixel & blueMask) >> blueShift);
                int a = hasAlphaChannel ? (int)((pixel & alphaMask) >> alphaShift) : 255;
                
                // Scale to 8-bit if needed
                if (redScale > 1) r = Math.Min(255, r * redScale);
                if (greenScale > 1) g = Math.Min(255, g * greenScale);
                if (blueScale > 1) b = Math.Min(255, b * blueScale);
                if (alphaScale > 1) a = Math.Min(255, a * alphaScale);
                
                dst[dstOffset] = (byte)b;
                dst[dstOffset + 1] = (byte)g;
                dst[dstOffset + 2] = (byte)r;
                dst[dstOffset + 3] = (byte)a;
            }
            else
            {
                // Standard BGRX format
                dst[dstOffset] = src[srcOffset];         // B
                dst[dstOffset + 1] = src[srcOffset + 1]; // G
                dst[dstOffset + 2] = src[srcOffset + 2]; // R
                dst[dstOffset + 3] = src[srcOffset + 3]; // X/A (usually 0 or ignored)
                
                // Force alpha to 255 for BGRX
                if (!hasAlphaChannel)
                    dst[dstOffset + 3] = 255;
            }
            
            srcOffset += 4;
            x++;
        }
    }
    
    private void DecodeRle8(byte[] pixelData, byte[] output)
    {
        int x = 0;
        int y = 0;
        int srcOffset = 0;
        
        // Initialize output to transparent
        Array.Fill<byte>(output, 0);
        
        while (srcOffset < pixelData.Length - 1 && y < height)
        {
            byte first = pixelData[srcOffset++];
            byte second = pixelData[srcOffset++];
            
            if (first == 0)
            {
                // Escape codes
                switch (second)
                {
                    case 0: // End of line
                        x = 0;
                        y++;
                        break;
                    case 1: // End of bitmap
                        return;
                    case 2: // Delta
                        if (srcOffset + 1 >= pixelData.Length) return;
                        x += pixelData[srcOffset++];
                        y += pixelData[srcOffset++];
                        break;
                    default: // Absolute mode
                        int count = second;
                        for (int i = 0; i < count && srcOffset < pixelData.Length; i++)
                        {
                            if (x < width && y < height)
                            {
                                int outputY = isTopDown ? y : (height - 1 - y);
                                int dstOffset = (outputY * width + x) * 4;
                                WritePaletteColor(output.AsSpan(), dstOffset, pixelData[srcOffset]);
                            }
                            srcOffset++;
                            x++;
                        }
                        // Pad to word boundary
                        if ((count & 1) != 0 && srcOffset < pixelData.Length)
                            srcOffset++;
                        break;
                }
            }
            else
            {
                // Encoded mode: repeat color 'first' times
                for (int i = 0; i < first; i++)
                {
                    if (x < width && y < height)
                    {
                        int outputY = isTopDown ? y : (height - 1 - y);
                        int dstOffset = (outputY * width + x) * 4;
                        WritePaletteColor(output.AsSpan(), dstOffset, second);
                    }
                    x++;
                }
            }
        }
    }
    
    private void DecodeRle4(byte[] pixelData, byte[] output)
    {
        int x = 0;
        int y = 0;
        int srcOffset = 0;
        
        // Initialize output to transparent
        Array.Fill<byte>(output, 0);
        
        while (srcOffset < pixelData.Length - 1 && y < height)
        {
            byte first = pixelData[srcOffset++];
            byte second = pixelData[srcOffset++];
            
            if (first == 0)
            {
                // Escape codes
                switch (second)
                {
                    case 0: // End of line
                        x = 0;
                        y++;
                        break;
                    case 1: // End of bitmap
                        return;
                    case 2: // Delta
                        if (srcOffset + 1 >= pixelData.Length) return;
                        x += pixelData[srcOffset++];
                        y += pixelData[srcOffset++];
                        break;
                    default: // Absolute mode
                        int count = second;
                        int bytesToRead = (count + 1) / 2;
                        bool highNibble = true;
                        byte currentByte = 0;
                        
                        for (int i = 0; i < count && srcOffset < pixelData.Length; i++)
                        {
                            if (highNibble)
                            {
                                currentByte = pixelData[srcOffset++];
                            }
                            
                            int colorIndex = highNibble ? ((currentByte >> 4) & 0x0F) : (currentByte & 0x0F);
                            highNibble = !highNibble;
                            
                            if (x < width && y < height)
                            {
                                int outputY = isTopDown ? y : (height - 1 - y);
                                int dstOffset = (outputY * width + x) * 4;
                                WritePaletteColor(output.AsSpan(), dstOffset, colorIndex);
                            }
                            x++;
                        }
                        // Pad to word boundary
                        if ((bytesToRead & 1) != 0 && srcOffset < pixelData.Length)
                            srcOffset++;
                        break;
                }
            }
            else
            {
                // Encoded mode: alternating colors 'first' times
                int color1 = (second >> 4) & 0x0F;
                int color2 = second & 0x0F;
                
                for (int i = 0; i < first; i++)
                {
                    int colorIndex = (i & 1) == 0 ? color1 : color2;
                    
                    if (x < width && y < height)
                    {
                        int outputY = isTopDown ? y : (height - 1 - y);
                        int dstOffset = (outputY * width + x) * 4;
                        WritePaletteColor(output.AsSpan(), dstOffset, colorIndex);
                    }
                    x++;
                }
            }
        }
    }
    
    /// <summary>
    /// Get track information for this BMP.
    /// </summary>
    public DemuxerTrackInfo GetTrackInfo()
    {
        return new DemuxerTrackInfo
        {
            TrackId = 0,
            TrackType = DemuxerTrackType.Video,
            CodecId = "bmp",
            Width = width,
            Height = height
        };
    }
    
    /// <summary>
    /// Seek to the beginning (BMP is a single-frame format).
    /// </summary>
    public bool SeekToFrame(int frameIndex)
    {
        if (frameIndex != 0)
            return false;
        
        frameRead = false;
        return true;
    }
    
    public void Dispose()
    {
        if (!isDisposed)
        {
            if (!leaveOpen)
                stream.Dispose();
            isDisposed = true;
        }
    }
}

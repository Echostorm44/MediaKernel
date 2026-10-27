// MediaKernel - Video Screenshot and Thumbnail System
// PNG/BMP encoding and video frame capture functionality

using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;

namespace MediaKernel.Core.Video;

/// <summary>
/// Image format for saving screenshots.
/// </summary>
public enum ImageFormat
{
    /// <summary>PNG format (lossless, larger files).</summary>
    Png,
    /// <summary>BMP format (uncompressed, largest files).</summary>
    Bmp,
    /// <summary>TGA format (simple, widely supported).</summary>
    Tga,
    /// <summary>PPM format (simple text-based).</summary>
    Ppm
}

/// <summary>
/// Screenshot capture settings.
/// </summary>
public sealed class ScreenshotSettings
{
    /// <summary>Output format.</summary>
    public ImageFormat Format { get; set; } = ImageFormat.Png;
    
    /// <summary>Output directory for screenshots.</summary>
    public string OutputDirectory { get; set; } = string.Empty;
    
    /// <summary>Filename template. Use placeholders: {date}, {time}, {index}.</summary>
    public string FilenameTemplate { get; set; } = "screenshot_{date}_{time}";
    
    /// <summary>Whether to include timestamp overlay.</summary>
    public bool IncludeTimestamp { get; set; }
    
    /// <summary>PNG compression level (0-9).</summary>
    public int CompressionLevel { get; set; } = 6;
}

/// <summary>
/// Thumbnail generation settings.
/// </summary>
public sealed class ThumbnailSettings
{
    /// <summary>Maximum width (maintains aspect ratio).</summary>
    public int MaxWidth { get; set; } = 320;
    
    /// <summary>Maximum height (maintains aspect ratio).</summary>
    public int MaxHeight { get; set; } = 180;
    
    /// <summary>Output format.</summary>
    public ImageFormat Format { get; set; } = ImageFormat.Png;
    
    /// <summary>Number of thumbnails to generate for preview strip.</summary>
    public int ThumbnailCount { get; set; } = 10;
    
    /// <summary>Whether to skip black frames.</summary>
    public bool SkipBlackFrames { get; set; } = true;
    
    /// <summary>Black frame threshold (average luminance 0-255).</summary>
    public int BlackFrameThreshold { get; set; } = 16;
}

/// <summary>
/// Video screenshot capture and saving functionality.
/// </summary>
public sealed class ScreenshotCapture
{
    private int screenshotIndex;
    
    /// <summary>
    /// Capture current frame to memory.
    /// </summary>
    public byte[] CaptureFrame(ReadOnlySpan<byte> rgbaFrame, int width, int height, ImageFormat format)
    {
        return format switch
        {
            ImageFormat.Png => EncodePng(rgbaFrame, width, height),
            ImageFormat.Bmp => EncodeBmp(rgbaFrame, width, height),
            ImageFormat.Tga => EncodeTga(rgbaFrame, width, height),
            ImageFormat.Ppm => EncodePpm(rgbaFrame, width, height),
            _ => EncodePng(rgbaFrame, width, height)
        };
    }
    
    /// <summary>
    /// Save frame to file.
    /// </summary>
    public string SaveToFile(ReadOnlySpan<byte> rgbaFrame, int width, int height, ScreenshotSettings settings)
    {
        string directory = string.IsNullOrEmpty(settings.OutputDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            : settings.OutputDirectory;
        
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        string extension = settings.Format switch
        {
            ImageFormat.Png => ".png",
            ImageFormat.Bmp => ".bmp",
            ImageFormat.Tga => ".tga",
            ImageFormat.Ppm => ".ppm",
            _ => ".png"
        };
        
        string filename = settings.FilenameTemplate
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"))
            .Replace("{time}", DateTime.Now.ToString("HH-mm-ss"))
            .Replace("{index}", (++screenshotIndex).ToString("D4"));
        
        string path = Path.Combine(directory, filename + extension);
        
        byte[] imageData = CaptureFrame(rgbaFrame, width, height, settings.Format);
        File.WriteAllBytes(path, imageData);
        
        return path;
    }
    
    /// <summary>
    /// Encode frame as PNG.
    /// </summary>
    public static byte[] EncodePng(ReadOnlySpan<byte> rgbaFrame, int width, int height)
    {
        using var stream = new MemoryStream();
        WritePng(stream, rgbaFrame, width, height);
        return stream.ToArray();
    }
    
    /// <summary>
    /// Encode frame as BMP.
    /// </summary>
    public static byte[] EncodeBmp(ReadOnlySpan<byte> rgbaFrame, int width, int height)
    {
        using var stream = new MemoryStream();
        WriteBmp(stream, rgbaFrame, width, height);
        return stream.ToArray();
    }
    
    /// <summary>
    /// Encode frame as TGA.
    /// </summary>
    public static byte[] EncodeTga(ReadOnlySpan<byte> rgbaFrame, int width, int height)
    {
        using var stream = new MemoryStream();
        WriteTga(stream, rgbaFrame, width, height);
        return stream.ToArray();
    }
    
    /// <summary>
    /// Encode frame as PPM.
    /// </summary>
    public static byte[] EncodePpm(ReadOnlySpan<byte> rgbaFrame, int width, int height)
    {
        using var stream = new MemoryStream();
        WritePpm(stream, rgbaFrame, width, height);
        return stream.ToArray();
    }
    
    private static void WritePng(Stream stream, ReadOnlySpan<byte> rgba, int width, int height)
    {
        // PNG file structure:
        // - 8-byte signature
        // - IHDR chunk (header)
        // - IDAT chunk(s) (compressed image data)
        // - IEND chunk (end marker)
        
        // Write PNG signature
        ReadOnlySpan<byte> signature = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        stream.Write(signature);
        
        // Write IHDR chunk
        WriteIhdr(stream, width, height);
        
        // Write IDAT chunk(s) with compressed pixel data
        WriteIdat(stream, rgba, width, height);
        
        // Write IEND chunk
        WriteIend(stream);
    }
    
    private static void WriteIhdr(Stream stream, int width, int height)
    {
        byte[] data = new byte[13];
        
        // Width (4 bytes, big-endian)
        data[0] = (byte)(width >> 24);
        data[1] = (byte)(width >> 16);
        data[2] = (byte)(width >> 8);
        data[3] = (byte)width;
        
        // Height (4 bytes, big-endian)
        data[4] = (byte)(height >> 24);
        data[5] = (byte)(height >> 16);
        data[6] = (byte)(height >> 8);
        data[7] = (byte)height;
        
        data[8] = 8;   // Bit depth (8 bits per channel)
        data[9] = 6;   // Color type (6 = RGBA)
        data[10] = 0;  // Compression method (0 = deflate)
        data[11] = 0;  // Filter method (0 = adaptive)
        data[12] = 0;  // Interlace method (0 = none)
        
        WriteChunk(stream, "IHDR", data);
    }
    
    private static void WriteIdat(Stream stream, ReadOnlySpan<byte> rgba, int width, int height)
    {
        // Prepare raw pixel data with filter bytes
        // Each row starts with a filter type byte (0 = None)
        int rowSize = width * 4 + 1; // 4 bytes per pixel + 1 filter byte
        byte[] rawData = new byte[height * rowSize];
        
        for (int y = 0; y < height; y++)
        {
            int rawRowOffset = y * rowSize;
            int srcRowOffset = y * width * 4;
            
            rawData[rawRowOffset] = 0; // Filter type: None
            
            // Copy row data (RGBA)
            rgba.Slice(srcRowOffset, width * 4).CopyTo(rawData.AsSpan(rawRowOffset + 1));
        }
        
        // Compress using zlib (deflate with zlib header)
        using var compressedStream = new MemoryStream();
        
        // Write zlib header
        compressedStream.WriteByte(0x78); // CMF (compression method + info)
        compressedStream.WriteByte(0x9C); // FLG (compression level = default)
        
        using (var deflate = new DeflateStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(rawData, 0, rawData.Length);
        }
        
        // Calculate and write Adler-32 checksum
        uint adler = ComputeAdler32(rawData);
        compressedStream.WriteByte((byte)(adler >> 24));
        compressedStream.WriteByte((byte)(adler >> 16));
        compressedStream.WriteByte((byte)(adler >> 8));
        compressedStream.WriteByte((byte)adler);
        
        WriteChunk(stream, "IDAT", compressedStream.ToArray());
    }
    
    private static void WriteIend(Stream stream)
    {
        WriteChunk(stream, "IEND", Array.Empty<byte>());
    }
    
    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        // Length (4 bytes, big-endian)
        int length = data.Length;
        stream.WriteByte((byte)(length >> 24));
        stream.WriteByte((byte)(length >> 16));
        stream.WriteByte((byte)(length >> 8));
        stream.WriteByte((byte)length);
        
        // Type (4 ASCII bytes)
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, 4);
        
        // Data
        stream.Write(data, 0, data.Length);
        
        // CRC32 of type + data
        uint crc = ComputeCrc32(typeBytes, data);
        stream.WriteByte((byte)(crc >> 24));
        stream.WriteByte((byte)(crc >> 16));
        stream.WriteByte((byte)(crc >> 8));
        stream.WriteByte((byte)crc);
    }
    
    private static uint ComputeCrc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        
        foreach (byte b in type)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        
        foreach (byte b in data)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        
        return ~crc;
    }
    
    private static uint ComputeAdler32(byte[] data)
    {
        uint a = 1, b = 0;
        const uint mod = 65521;
        
        foreach (byte d in data)
        {
            a = (a + d) % mod;
            b = (b + a) % mod;
        }
        
        return (b << 16) | a;
    }
    
    private static readonly uint[] Crc32Table = GenerateCrc32Table();
    
    private static uint[] GenerateCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                if ((c & 1) != 0)
                    c = 0xEDB88320 ^ (c >> 1);
                else
                    c >>= 1;
            }
            table[n] = c;
        }
        return table;
    }
    
    private static void WriteBmp(Stream stream, ReadOnlySpan<byte> rgba, int width, int height)
    {
        // BMP stores rows bottom-to-top, and uses BGRA instead of RGBA
        int rowSize = width * 4;
        int paddedRowSize = (rowSize + 3) & ~3; // Pad to 4-byte boundary
        int pixelDataSize = paddedRowSize * height;
        int fileSize = 14 + 40 + pixelDataSize; // File header + DIB header + pixels
        
        // BMP File Header (14 bytes)
        stream.WriteByte((byte)'B');
        stream.WriteByte((byte)'M');
        WriteInt32LE(stream, fileSize);
        WriteInt16LE(stream, 0); // Reserved
        WriteInt16LE(stream, 0); // Reserved
        WriteInt32LE(stream, 14 + 40); // Pixel data offset
        
        // DIB Header (BITMAPINFOHEADER - 40 bytes)
        WriteInt32LE(stream, 40); // Header size
        WriteInt32LE(stream, width);
        WriteInt32LE(stream, height);
        WriteInt16LE(stream, 1); // Color planes
        WriteInt16LE(stream, 32); // Bits per pixel
        WriteInt32LE(stream, 0); // Compression (BI_RGB = none)
        WriteInt32LE(stream, pixelDataSize);
        WriteInt32LE(stream, 2835); // Horizontal resolution (72 DPI)
        WriteInt32LE(stream, 2835); // Vertical resolution
        WriteInt32LE(stream, 0); // Colors in palette
        WriteInt32LE(stream, 0); // Important colors
        
        // Pixel data (bottom-to-top, BGRA)
        byte[] row = new byte[paddedRowSize];
        for (int y = height - 1; y >= 0; y--)
        {
            int srcOffset = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int si = srcOffset + x * 4;
                int di = x * 4;
                row[di] = rgba[si + 2];     // B
                row[di + 1] = rgba[si + 1]; // G
                row[di + 2] = rgba[si];     // R
                row[di + 3] = rgba[si + 3]; // A
            }
            stream.Write(row, 0, paddedRowSize);
        }
    }
    
    private static void WriteTga(Stream stream, ReadOnlySpan<byte> rgba, int width, int height)
    {
        // TGA Header (18 bytes)
        stream.WriteByte(0);  // ID length
        stream.WriteByte(0);  // Color map type (none)
        stream.WriteByte(2);  // Image type (uncompressed true-color)
        
        // Color map specification (5 bytes)
        WriteInt16LE(stream, 0); // First entry index
        WriteInt16LE(stream, 0); // Color map length
        stream.WriteByte(0);     // Color map entry size
        
        // Image specification
        WriteInt16LE(stream, 0); // X origin
        WriteInt16LE(stream, 0); // Y origin
        WriteInt16LE(stream, (short)width);
        WriteInt16LE(stream, (short)height);
        stream.WriteByte(32);    // Bits per pixel
        stream.WriteByte(0x28);  // Image descriptor (origin upper-left, 8-bit alpha)
        
        // Pixel data (BGRA, top-to-bottom)
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                stream.WriteByte(rgba[offset + 2]); // B
                stream.WriteByte(rgba[offset + 1]); // G
                stream.WriteByte(rgba[offset]);     // R
                stream.WriteByte(rgba[offset + 3]); // A
            }
        }
    }
    
    private static void WritePpm(Stream stream, ReadOnlySpan<byte> rgba, int width, int height)
    {
        // PPM P6 format (binary RGB)
        string header = $"P6\n{width} {height}\n255\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        
        // Pixel data (RGB only, no alpha)
        for (int i = 0; i < width * height; i++)
        {
            int offset = i * 4;
            stream.WriteByte(rgba[offset]);     // R
            stream.WriteByte(rgba[offset + 1]); // G
            stream.WriteByte(rgba[offset + 2]); // B
        }
    }
    
    private static void WriteInt32LE(Stream stream, int value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }
    
    private static void WriteInt16LE(Stream stream, short value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
    }
}

/// <summary>
/// Video thumbnail generator.
/// </summary>
public sealed class ThumbnailGenerator
{
    private readonly ThumbnailSettings settings;
    
    public ThumbnailGenerator(ThumbnailSettings? settings = null)
    {
        this.settings = settings ?? new ThumbnailSettings();
    }
    
    /// <summary>
    /// Generate a scaled thumbnail from a video frame.
    /// </summary>
    public byte[] GenerateThumbnail(ReadOnlySpan<byte> rgbaFrame, int width, int height)
    {
        // Calculate scaled dimensions maintaining aspect ratio
        var (scaledWidth, scaledHeight) = CalculateScaledDimensions(width, height);
        
        // Scale the frame
        byte[] scaledFrame = ScaleFrame(rgbaFrame, width, height, scaledWidth, scaledHeight);
        
        // Encode to selected format
        return settings.Format switch
        {
            ImageFormat.Png => ScreenshotCapture.EncodePng(scaledFrame, scaledWidth, scaledHeight),
            ImageFormat.Bmp => ScreenshotCapture.EncodeBmp(scaledFrame, scaledWidth, scaledHeight),
            ImageFormat.Tga => ScreenshotCapture.EncodeTga(scaledFrame, scaledWidth, scaledHeight),
            ImageFormat.Ppm => ScreenshotCapture.EncodePpm(scaledFrame, scaledWidth, scaledHeight),
            _ => ScreenshotCapture.EncodePng(scaledFrame, scaledWidth, scaledHeight)
        };
    }
    
    /// <summary>
    /// Check if a frame is mostly black (for skipping).
    /// </summary>
    public bool IsBlackFrame(ReadOnlySpan<byte> rgbaFrame, int width, int height)
    {
        if (!settings.SkipBlackFrames)
            return false;
        
        long totalLuminance = 0;
        int pixelCount = width * height;
        
        // Sample every 16th pixel for speed
        int sampleStep = 16;
        int sampledCount = 0;
        
        for (int i = 0; i < pixelCount; i += sampleStep)
        {
            int offset = i * 4;
            // Quick luminance approximation: (R + 2*G + B) / 4
            int lum = (rgbaFrame[offset] + 2 * rgbaFrame[offset + 1] + rgbaFrame[offset + 2]) >> 2;
            totalLuminance += lum;
            sampledCount++;
        }
        
        int averageLuminance = (int)(totalLuminance / sampledCount);
        return averageLuminance < settings.BlackFrameThreshold;
    }
    
    /// <summary>
    /// Calculate scaled dimensions maintaining aspect ratio.
    /// </summary>
    public (int width, int height) CalculateScaledDimensions(int sourceWidth, int sourceHeight)
    {
        float widthRatio = (float)settings.MaxWidth / sourceWidth;
        float heightRatio = (float)settings.MaxHeight / sourceHeight;
        float ratio = Math.Min(widthRatio, heightRatio);
        
        // Don't upscale
        if (ratio >= 1.0f)
            return (sourceWidth, sourceHeight);
        
        int scaledWidth = (int)(sourceWidth * ratio);
        int scaledHeight = (int)(sourceHeight * ratio);
        
        // Ensure minimum of 1
        return (Math.Max(1, scaledWidth), Math.Max(1, scaledHeight));
    }
    
    /// <summary>
    /// Scale a frame using bilinear interpolation.
    /// </summary>
    public static byte[] ScaleFrame(ReadOnlySpan<byte> source, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        byte[] result = new byte[dstWidth * dstHeight * 4];
        
        float xRatio = (float)srcWidth / dstWidth;
        float yRatio = (float)srcHeight / dstHeight;
        
        for (int y = 0; y < dstHeight; y++)
        {
            float srcY = y * yRatio;
            int y0 = (int)srcY;
            int y1 = Math.Min(y0 + 1, srcHeight - 1);
            float yFrac = srcY - y0;
            
            for (int x = 0; x < dstWidth; x++)
            {
                float srcX = x * xRatio;
                int x0 = (int)srcX;
                int x1 = Math.Min(x0 + 1, srcWidth - 1);
                float xFrac = srcX - x0;
                
                // Get four source pixels
                int s00 = (y0 * srcWidth + x0) * 4;
                int s01 = (y0 * srcWidth + x1) * 4;
                int s10 = (y1 * srcWidth + x0) * 4;
                int s11 = (y1 * srcWidth + x1) * 4;
                
                // Bilinear interpolation for each channel
                int dstOffset = (y * dstWidth + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    float top = source[s00 + c] * (1 - xFrac) + source[s01 + c] * xFrac;
                    float bottom = source[s10 + c] * (1 - xFrac) + source[s11 + c] * xFrac;
                    result[dstOffset + c] = (byte)(top * (1 - yFrac) + bottom * yFrac);
                }
            }
        }
        
        return result;
    }
}

/// <summary>
/// Thumbnail strip generator for video preview.
/// </summary>
public readonly struct ThumbnailInfo
{
    /// <summary>Thumbnail image data.</summary>
    public byte[] ImageData { get; init; }
    
    /// <summary>Thumbnail width.</summary>
    public int Width { get; init; }
    
    /// <summary>Thumbnail height.</summary>
    public int Height { get; init; }
    
    /// <summary>Timestamp in the video (seconds).</summary>
    public double Timestamp { get; init; }
    
    /// <summary>Image format.</summary>
    public ImageFormat Format { get; init; }
}

/// <summary>
/// Video frame analysis utilities.
/// </summary>
public static class FrameAnalysis
{
    /// <summary>
    /// Calculate average luminance of a frame.
    /// </summary>
    public static float CalculateAverageLuminance(ReadOnlySpan<byte> rgbaFrame, int width, int height)
    {
        long total = 0;
        int pixelCount = width * height;
        
        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 4;
            // ITU-R BT.601: Y = 0.299*R + 0.587*G + 0.114*B
            int lum = (rgbaFrame[offset] * 77 + rgbaFrame[offset + 1] * 150 + rgbaFrame[offset + 2] * 29) >> 8;
            total += lum;
        }
        
        return (float)total / pixelCount;
    }
    
    /// <summary>
    /// Calculate histogram for a frame (per channel).
    /// </summary>
    public static (int[] red, int[] green, int[] blue) CalculateHistogram(ReadOnlySpan<byte> rgbaFrame, int width, int height)
    {
        int[] red = new int[256];
        int[] green = new int[256];
        int[] blue = new int[256];
        
        int pixelCount = width * height;
        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 4;
            red[rgbaFrame[offset]]++;
            green[rgbaFrame[offset + 1]]++;
            blue[rgbaFrame[offset + 2]]++;
        }
        
        return (red, green, blue);
    }
    
    /// <summary>
    /// Calculate color variance (for scene change detection).
    /// </summary>
    public static float CalculateColorVariance(ReadOnlySpan<byte> rgbaFrame, int width, int height)
    {
        float avgLum = CalculateAverageLuminance(rgbaFrame, width, height);
        
        float sumSquaredDiff = 0;
        int pixelCount = width * height;
        
        // Sample for performance
        int sampleStep = 4;
        int sampledCount = 0;
        
        for (int i = 0; i < pixelCount; i += sampleStep)
        {
            int offset = i * 4;
            int lum = (rgbaFrame[offset] * 77 + rgbaFrame[offset + 1] * 150 + rgbaFrame[offset + 2] * 29) >> 8;
            float diff = lum - avgLum;
            sumSquaredDiff += diff * diff;
            sampledCount++;
        }
        
        return sumSquaredDiff / sampledCount;
    }
    
    /// <summary>
    /// Detect if two frames are likely from different scenes.
    /// </summary>
    public static bool IsSceneChange(ReadOnlySpan<byte> frame1, ReadOnlySpan<byte> frame2, int width, int height, float threshold = 30f)
    {
        // Compare average colors and histogram differences
        float lum1 = CalculateAverageLuminance(frame1, width, height);
        float lum2 = CalculateAverageLuminance(frame2, width, height);
        
        float lumDiff = MathF.Abs(lum1 - lum2);
        
        if (lumDiff > threshold)
            return true;
        
        // Check color distribution difference
        var (r1, g1, b1) = CalculateHistogram(frame1, width, height);
        var (r2, g2, b2) = CalculateHistogram(frame2, width, height);
        
        float histDiff = CalculateHistogramDifference(r1, r2) +
                         CalculateHistogramDifference(g1, g2) +
                         CalculateHistogramDifference(b1, b2);
        
        // Normalize by pixel count
        float normalizedDiff = histDiff / (width * height * 3);
        
        return normalizedDiff > threshold / 100f;
    }
    
    private static float CalculateHistogramDifference(int[] hist1, int[] hist2)
    {
        float diff = 0;
        for (int i = 0; i < 256; i++)
        {
            diff += MathF.Abs(hist1[i] - hist2[i]);
        }
        return diff;
    }
}

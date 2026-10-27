// MediaKernel - PNG Image Demuxer
// Demuxes PNG (Portable Network Graphics) still images.
// Reference: PNG Specification 1.2 (ISO/IEC 15948:2003)
//
// Supports:
// - All color types: Grayscale, RGB, Indexed, Grayscale+Alpha, RGBA
// - Bit depths: 1, 2, 4, 8, 16 bits per sample
// - Interlaced (Adam7) and non-interlaced images
// - Critical chunks: IHDR, IDAT, IEND, PLTE
// - Ancillary chunks: tRNS, gAMA, cHRM, sRGB, iCCP, pHYs, tEXt, iTXt, zTXt

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// PNG color type.
/// </summary>
public enum PngColorType : byte
{
    /// <summary>Grayscale (1, 2, 4, 8, or 16 bits).</summary>
    Grayscale = 0,
    
    /// <summary>RGB truecolor (8 or 16 bits per channel).</summary>
    Rgb = 2,
    
    /// <summary>Indexed color with palette (1, 2, 4, or 8 bits).</summary>
    Indexed = 3,
    
    /// <summary>Grayscale with alpha (8 or 16 bits per channel).</summary>
    GrayscaleAlpha = 4,
    
    /// <summary>RGBA truecolor with alpha (8 or 16 bits per channel).</summary>
    Rgba = 6
}

/// <summary>
/// PNG interlace method.
/// </summary>
public enum PngInterlaceMethod : byte
{
    /// <summary>No interlacing.</summary>
    None = 0,
    
    /// <summary>Adam7 interlacing.</summary>
    Adam7 = 1
}

/// <summary>
/// Result from probing a stream for PNG content.
/// </summary>
public readonly struct PngProbeResult
{
    /// <summary>Whether valid PNG content was detected.</summary>
    public bool IsPng { get; init; }
    
    /// <summary>Image width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Image height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Bit depth (1, 2, 4, 8, or 16).</summary>
    public int BitDepth { get; init; }
    
    /// <summary>Color type.</summary>
    public PngColorType ColorType { get; init; }
    
    /// <summary>Interlace method.</summary>
    public PngInterlaceMethod InterlaceMethod { get; init; }
    
    /// <summary>Confidence score (0-100).</summary>
    public int Confidence { get; init; }
    
    /// <summary>Whether the image has an alpha channel.</summary>
    public bool HasAlpha => ColorType == PngColorType.GrayscaleAlpha || ColorType == PngColorType.Rgba;
    
    /// <summary>Number of channels (1-4).</summary>
    public int ChannelCount => ColorType switch
    {
        PngColorType.Grayscale => 1,
        PngColorType.Rgb => 3,
        PngColorType.Indexed => 1,
        PngColorType.GrayscaleAlpha => 2,
        PngColorType.Rgba => 4,
        _ => 0
    };
}

/// <summary>
/// A decoded PNG frame.
/// </summary>
public sealed class PngFrame
{
    /// <summary>Raw PNG data (compressed).</summary>
    public byte[] RawData { get; init; } = [];
    
    /// <summary>Image width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Image height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Bit depth.</summary>
    public int BitDepth { get; init; }
    
    /// <summary>Color type.</summary>
    public PngColorType ColorType { get; init; }
    
    /// <summary>Interlace method.</summary>
    public PngInterlaceMethod InterlaceMethod { get; init; }
    
    /// <summary>Whether the image has an alpha channel.</summary>
    public bool HasAlpha => ColorType == PngColorType.GrayscaleAlpha || ColorType == PngColorType.Rgba;
    
    /// <summary>Whether the image is interlaced.</summary>
    public bool IsInterlaced => InterlaceMethod == PngInterlaceMethod.Adam7;
    
    /// <summary>Number of channels.</summary>
    public int ChannelCount => ColorType switch
    {
        PngColorType.Grayscale => 1,
        PngColorType.Rgb => 3,
        PngColorType.Indexed => 1,
        PngColorType.GrayscaleAlpha => 2,
        PngColorType.Rgba => 4,
        _ => 0
    };
    
    /// <summary>Gamma value (0 if not specified).</summary>
    public double Gamma { get; init; }
    
    /// <summary>Whether sRGB color space is specified.</summary>
    public bool IsSrgb { get; init; }
}

/// <summary>
/// PNG chunk information.
/// </summary>
public readonly struct PngChunk
{
    /// <summary>Chunk length (data only, excluding type and CRC).</summary>
    public int Length { get; init; }
    
    /// <summary>Chunk type (4 ASCII characters).</summary>
    public string Type { get; init; }
    
    /// <summary>Offset in stream to chunk data.</summary>
    public long DataOffset { get; init; }
    
    /// <summary>Whether this is a critical chunk.</summary>
    public bool IsCritical => Type.Length > 0 && char.IsUpper(Type[0]);
    
    /// <summary>Whether this chunk is safe to copy.</summary>
    public bool IsSafeToCopy => Type.Length >= 4 && char.IsLower(Type[3]);
}

/// <summary>
/// Demuxer for PNG still images.
/// </summary>
/// <remarks>
/// This demuxer parses PNG image headers and extracts metadata.
/// The raw compressed data can be passed to a PNG decoder.
/// </remarks>
public sealed class PngDemuxer : IDisposable
{
    /// <summary>PNG signature bytes.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private bool isParsed;
    private bool isDisposed;
    private bool frameRead;
    
    // Parsed PNG info
    private int width;
    private int height;
    private int bitDepth;
    private PngColorType colorType;
    private PngInterlaceMethod interlaceMethod;
    private double gamma;
    private bool isSrgb;
    private byte[]? imageData;
    private readonly List<PngChunk> chunks = [];
    
    /// <summary>
    /// Creates a new PNG demuxer.
    /// </summary>
    public PngDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
    }
    
    /// <summary>Gets whether the PNG has been successfully parsed.</summary>
    public bool IsParsed => isParsed;
    
    /// <summary>Gets the image width.</summary>
    public int Width => width;
    
    /// <summary>Gets the image height.</summary>
    public int Height => height;
    
    /// <summary>Gets the bit depth.</summary>
    public int BitDepth => bitDepth;
    
    /// <summary>Gets the color type.</summary>
    public PngColorType ColorType => colorType;
    
    /// <summary>Gets the interlace method.</summary>
    public PngInterlaceMethod InterlaceMethod => interlaceMethod;
    
    /// <summary>Gets whether the image has an alpha channel.</summary>
    public bool HasAlpha => colorType == PngColorType.GrayscaleAlpha || colorType == PngColorType.Rgba;
    
    /// <summary>Gets the number of channels.</summary>
    public int ChannelCount => colorType switch
    {
        PngColorType.Grayscale => 1,
        PngColorType.Rgb => 3,
        PngColorType.Indexed => 1,
        PngColorType.GrayscaleAlpha => 2,
        PngColorType.Rgba => 4,
        _ => 0
    };
    
    /// <summary>Gets the gamma value (0 if not specified).</summary>
    public double Gamma => gamma;
    
    /// <summary>Gets whether sRGB color space is specified.</summary>
    public bool IsSrgb => isSrgb;
    
    /// <summary>Gets whether all frames have been read.</summary>
    public bool IsEndOfStream => frameRead;
    
    /// <summary>Gets the codec name.</summary>
    public string Codec => "png";
    
    /// <summary>Gets the parsed chunks.</summary>
    public IReadOnlyList<PngChunk> Chunks => chunks;
    
    /// <summary>
    /// Probe a stream for PNG content without fully parsing.
    /// </summary>
    public static PngProbeResult Probe(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[33]; // Signature (8) + IHDR length (4) + type (4) + data (13) + CRC (4)
        
        long originalPosition = stream.Position;
        int bytesRead = stream.Read(buffer);
        stream.Position = originalPosition;
        
        if (bytesRead < 33)
            return new PngProbeResult { IsPng = false };
        
        return ProbeBuffer(buffer[..bytesRead]);
    }
    
    /// <summary>
    /// Probe a buffer for PNG content.
    /// </summary>
    public static PngProbeResult Probe(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 33)
            return new PngProbeResult { IsPng = false };
        
        return ProbeBuffer(buffer);
    }
    
    private static PngProbeResult ProbeBuffer(ReadOnlySpan<byte> buffer)
    {
        // Check PNG signature
        if (!buffer[..8].SequenceEqual(PngSignature))
            return new PngProbeResult { IsPng = false };
        
        // Read IHDR chunk (must be first)
        int ihdrLength = BinaryPrimitives.ReadInt32BigEndian(buffer[8..]);
        if (ihdrLength != 13) // IHDR is always 13 bytes
            return new PngProbeResult { IsPng = false };
        
        // Check IHDR type
        if (buffer[12] != 'I' || buffer[13] != 'H' || buffer[14] != 'D' || buffer[15] != 'R')
            return new PngProbeResult { IsPng = false };
        
        // Parse IHDR data
        int w = BinaryPrimitives.ReadInt32BigEndian(buffer[16..]);
        int h = BinaryPrimitives.ReadInt32BigEndian(buffer[20..]);
        int depth = buffer[24];
        var color = (PngColorType)buffer[25];
        int compression = buffer[26];
        int filter = buffer[27];
        var interlace = (PngInterlaceMethod)buffer[28];
        
        // Validate
        if (w <= 0 || w > 0x7FFFFFFF || h <= 0 || h > 0x7FFFFFFF)
            return new PngProbeResult { IsPng = false };
        
        if (compression != 0) // Only deflate is supported
            return new PngProbeResult { IsPng = false };
        
        if (filter != 0) // Only adaptive filtering is supported
            return new PngProbeResult { IsPng = false };
        
        if (interlace > PngInterlaceMethod.Adam7)
            return new PngProbeResult { IsPng = false };
        
        // Validate bit depth for color type
        if (!IsValidBitDepth(depth, color))
            return new PngProbeResult { IsPng = false };
        
        return new PngProbeResult
        {
            IsPng = true,
            Width = w,
            Height = h,
            BitDepth = depth,
            ColorType = color,
            InterlaceMethod = interlace,
            Confidence = 95
        };
    }
    
    private static bool IsValidBitDepth(int bitDepth, PngColorType colorType)
    {
        return colorType switch
        {
            PngColorType.Grayscale => bitDepth is 1 or 2 or 4 or 8 or 16,
            PngColorType.Rgb => bitDepth is 8 or 16,
            PngColorType.Indexed => bitDepth is 1 or 2 or 4 or 8,
            PngColorType.GrayscaleAlpha => bitDepth is 8 or 16,
            PngColorType.Rgba => bitDepth is 8 or 16,
            _ => false
        };
    }
    
    /// <summary>
    /// Parse the PNG file.
    /// </summary>
    public bool Parse()
    {
        if (isParsed) return true;
        ObjectDisposedException.ThrowIf(isDisposed, this);
        
        try
        {
            if (stream.CanSeek)
                stream.Position = 0;
            
            // Read entire file
            long length = stream.CanSeek ? stream.Length : 0;
            if (length == 0)
                length = 50 * 1024 * 1024; // 50MB max for unseekable streams
            
            using var ms = new MemoryStream((int)Math.Min(length, 50 * 1024 * 1024));
            stream.CopyTo(ms);
            imageData = ms.ToArray();
            
            if (imageData.Length < 33)
                return false;
            
            // Check signature
            if (!imageData.AsSpan(0, 8).SequenceEqual(PngSignature))
                return false;
            
            // Parse chunks
            int offset = 8;
            bool foundIhdr = false;
            bool foundIend = false;
            
            while (offset + 12 <= imageData.Length)
            {
                int chunkLength = BinaryPrimitives.ReadInt32BigEndian(imageData.AsSpan(offset));
                if (chunkLength < 0 || offset + 12 + chunkLength > imageData.Length)
                    break;
                
                string chunkType = Encoding.ASCII.GetString(imageData, offset + 4, 4);
                
                chunks.Add(new PngChunk
                {
                    Length = chunkLength,
                    Type = chunkType,
                    DataOffset = offset + 8
                });
                
                // Parse specific chunks
                switch (chunkType)
                {
                    case "IHDR":
                        if (!ParseIhdr(imageData.AsSpan(offset + 8, chunkLength)))
                            return false;
                        foundIhdr = true;
                        break;
                    
                    case "gAMA":
                        ParseGama(imageData.AsSpan(offset + 8, chunkLength));
                        break;
                    
                    case "sRGB":
                        isSrgb = true;
                        break;
                    
                    case "IEND":
                        foundIend = true;
                        break;
                }
                
                // Move to next chunk
                offset += 12 + chunkLength; // length(4) + type(4) + data + crc(4)
                
                if (foundIend)
                    break;
            }
            
            if (!foundIhdr)
                return false;
            
            isParsed = true;
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    private bool ParseIhdr(ReadOnlySpan<byte> data)
    {
        if (data.Length < 13)
            return false;
        
        width = BinaryPrimitives.ReadInt32BigEndian(data);
        height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        bitDepth = data[8];
        colorType = (PngColorType)data[9];
        int compression = data[10];
        int filter = data[11];
        interlaceMethod = (PngInterlaceMethod)data[12];
        
        // Validate
        if (width <= 0 || height <= 0)
            return false;
        
        if (compression != 0 || filter != 0)
            return false;
        
        if (!IsValidBitDepth(bitDepth, colorType))
            return false;
        
        return true;
    }
    
    private void ParseGama(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4)
        {
            uint gammaInt = BinaryPrimitives.ReadUInt32BigEndian(data);
            gamma = gammaInt / 100000.0; // Stored as gamma * 100000
        }
    }
    
    /// <summary>
    /// Read the PNG frame.
    /// </summary>
    public PngFrame? ReadFrame()
    {
        if (!isParsed && !Parse())
            return null;
        
        if (frameRead)
            return null;
        
        frameRead = true;
        
        return new PngFrame
        {
            RawData = imageData!,
            Width = width,
            Height = height,
            BitDepth = bitDepth,
            ColorType = colorType,
            InterlaceMethod = interlaceMethod,
            Gamma = gamma,
            IsSrgb = isSrgb
        };
    }
    
    /// <summary>
    /// Reset to allow re-reading.
    /// </summary>
    public void Reset()
    {
        frameRead = false;
        if (stream.CanSeek)
            stream.Position = 0;
    }
    
    /// <summary>
    /// Get track information.
    /// </summary>
    public DemuxerTrackInfo? GetTrackInfo()
    {
        if (!isParsed && !Parse())
            return null;
        
        return new DemuxerTrackInfo
        {
            TrackId = 0,
            TrackType = DemuxerTrackType.Video,
            CodecId = Codec,
            Width = width,
            Height = height,
            BitsPerSample = bitDepth * ChannelCount
        };
    }
    
    /// <summary>
    /// Check if a specific chunk type exists.
    /// </summary>
    public bool HasChunk(string chunkType)
    {
        foreach (var chunk in chunks)
        {
            if (chunk.Type == chunkType)
                return true;
        }
        return false;
    }
    
    /// <summary>
    /// Get all chunks of a specific type.
    /// </summary>
    public IEnumerable<PngChunk> GetChunks(string chunkType)
    {
        foreach (var chunk in chunks)
        {
            if (chunk.Type == chunkType)
                yield return chunk;
        }
    }
    
    /// <inheritdoc/>
    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;
        
        if (!leaveOpen)
            stream.Dispose();
    }
}

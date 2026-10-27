// MediaKernel - Targa (TGA) Image Demuxer
// Demuxes Targa/TGA (Truevision Graphics Adapter) still images.
// Reference: Truevision TGA File Format Specification 2.0
//
// Supports:
// - Uncompressed color-mapped (Type 1)
// - Uncompressed true-color (Type 2)
// - Uncompressed grayscale (Type 3)
// - RLE-compressed color-mapped (Type 9)
// - RLE-compressed true-color (Type 10)
// - RLE-compressed grayscale (Type 11)
// - Pixel depths: 8, 15, 16, 24, 32 bits
// - TGA 2.0 footer detection

using System.Buffers.Binary;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// TGA image type.
/// </summary>
public enum TgaImageType : byte
{
    /// <summary>No image data.</summary>
    NoImage = 0,
    
    /// <summary>Uncompressed color-mapped.</summary>
    UncompressedColorMapped = 1,
    
    /// <summary>Uncompressed true-color (RGB/RGBA).</summary>
    UncompressedTrueColor = 2,
    
    /// <summary>Uncompressed grayscale.</summary>
    UncompressedGrayscale = 3,
    
    /// <summary>RLE-compressed color-mapped.</summary>
    RleColorMapped = 9,
    
    /// <summary>RLE-compressed true-color.</summary>
    RleTrueColor = 10,
    
    /// <summary>RLE-compressed grayscale.</summary>
    RleGrayscale = 11
}

/// <summary>
/// TGA image origin (corner where pixel data starts).
/// </summary>
public enum TgaImageOrigin
{
    /// <summary>Bottom-left origin (most common).</summary>
    BottomLeft = 0,
    
    /// <summary>Bottom-right origin.</summary>
    BottomRight = 1,
    
    /// <summary>Top-left origin.</summary>
    TopLeft = 2,
    
    /// <summary>Top-right origin.</summary>
    TopRight = 3
}

/// <summary>
/// TGA probe result.
/// </summary>
public readonly struct TgaProbeResult
{
    public bool IsTga { get; init; }
    public int Confidence { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int PixelDepth { get; init; }
    public TgaImageType ImageType { get; init; }
    public TgaImageOrigin Origin { get; init; }
    public bool HasColorMap { get; init; }
    public bool HasAlpha { get; init; }
    public bool IsRleCompressed { get; init; }
    public bool HasFooter { get; init; }
}

/// <summary>
/// TGA image frame.
/// </summary>
public sealed class TgaFrame
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int PixelDepth { get; init; }
    public TgaImageType ImageType { get; init; }
    public TgaImageOrigin Origin { get; init; }
    public bool HasColorMap { get; init; }
    public bool HasAlpha { get; init; }
    public int AlphaBits { get; init; }
    public bool IsRleCompressed { get; init; }
    public bool HasFooter { get; init; }
    public byte[] RawData { get; init; } = [];
    public string? ImageId { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorComments { get; init; }
    public string? SoftwareId { get; init; }
    public DateTime? Timestamp { get; init; }
}

/// <summary>
/// Demuxes Targa (TGA) still images.
/// </summary>
public sealed class TgaDemuxer : IDisposable
{
    // TGA 2.0 footer signature (17 chars + null = 18 bytes)
    private static readonly byte[] Tga2FooterSignature = "TRUEVISION-XFILE.\0"u8.ToArray();
    
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly long startPosition;
    private bool disposed;
    
    private byte[]? fileData;
    private bool frameRead;
    
    public TgaDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
        startPosition = stream.CanSeek ? stream.Position : 0;
    }
    
    public bool IsParsed { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int PixelDepth { get; private set; }
    public TgaImageType ImageType { get; private set; }
    public TgaImageOrigin Origin { get; private set; }
    public bool HasColorMap { get; private set; }
    public bool HasAlpha { get; private set; }
    public int AlphaBits { get; private set; }
    public bool IsRleCompressed { get; private set; }
    public bool HasFooter { get; private set; }
    public int ImageIdLength { get; private set; }
    public string? ImageId { get; private set; }
    public int ColorMapFirstEntry { get; private set; }
    public int ColorMapLength { get; private set; }
    public int ColorMapEntrySize { get; private set; }
    public int XOrigin { get; private set; }
    public int YOrigin { get; private set; }
    public bool IsEndOfStream => frameRead;
    public int ChannelCount => PixelDepth == 32 || PixelDepth == 16 ? 4 : PixelDepth == 24 ? 3 : 1;
    
    // TGA 2.0 extension data
    public string? AuthorName { get; private set; }
    public string? AuthorComments { get; private set; }
    public string? SoftwareId { get; private set; }
    public DateTime? Timestamp { get; private set; }
    public uint ExtensionAreaOffset { get; private set; }
    public uint DeveloperAreaOffset { get; private set; }
    
    /// <summary>
    /// Probes a stream to determine if it contains a valid TGA file.
    /// </summary>
    public static TgaProbeResult Probe(Stream stream)
    {
        Span<byte> header = stackalloc byte[18];
        int read = stream.Read(header);
        
        if (read < 18)
            return new TgaProbeResult { IsTga = false };
        
        // Try to check footer if stream is seekable
        bool hasFooter = false;
        if (stream.CanSeek && stream.Length >= 18 + 26)
        {
            long pos = stream.Position;
            stream.Position = stream.Length - 26;
            Span<byte> footer = stackalloc byte[26];
            if (stream.Read(footer) == 26)
            {
                hasFooter = footer[8..26].SequenceEqual(Tga2FooterSignature);
            }
            stream.Position = pos;
        }
        
        return ProbeInternal(header, hasFooter);
    }
    
    /// <summary>
    /// Probes data to determine if it contains a valid TGA file.
    /// </summary>
    public static TgaProbeResult Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18)
            return new TgaProbeResult { IsTga = false };
        
        // Check footer if data is long enough
        bool hasFooter = false;
        if (data.Length >= 18 + 26)
        {
            // Footer is last 26 bytes, signature "TRUEVISION-XFILE.\0" is at offset 8 within footer
            int footerStart = data.Length - 26;
            hasFooter = data.Slice(footerStart + 8, 18).SequenceEqual(Tga2FooterSignature);
        }
        
        return ProbeInternal(data[..18], hasFooter);
    }
    
    private static TgaProbeResult ProbeInternal(ReadOnlySpan<byte> header, bool hasFooter)
    {
        // TGA header (18 bytes):
        // 0: ID length
        // 1: Color map type (0 or 1)
        // 2: Image type
        // 3-7: Color map specification
        // 8-17: Image specification
        
        byte idLength = header[0];
        byte colorMapType = header[1];
        byte imageType = header[2];
        
        // Validate color map type (must be 0 or 1)
        if (colorMapType > 1)
            return new TgaProbeResult { IsTga = false };
        
        // Validate image type
        if (!IsValidImageType(imageType))
            return new TgaProbeResult { IsTga = false };
        
        // Color map entry size (offset 7)
        byte colorMapEntrySize = header[7];
        if (colorMapType != 0 && colorMapEntrySize != 0 &&
            colorMapEntrySize != 8 && colorMapEntrySize != 15 &&
            colorMapEntrySize != 16 && colorMapEntrySize != 24 && colorMapEntrySize != 32)
            return new TgaProbeResult { IsTga = false };
        
        // Width and height (must be > 0)
        int width = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        
        if (width == 0 || height == 0)
            return new TgaProbeResult { IsTga = false };
        
        // Pixel depth
        byte pixelDepth = header[16];
        if (pixelDepth != 8 && pixelDepth != 15 && pixelDepth != 16 &&
            pixelDepth != 24 && pixelDepth != 32)
            return new TgaProbeResult { IsTga = false };
        
        // Image descriptor (offset 17)
        byte imageDescriptor = header[17];
        if ((imageDescriptor & 0xC0) != 0) // Reserved bits must be 0
            return new TgaProbeResult { IsTga = false };
        
        int alphaBits = imageDescriptor & 0x0F;
        var origin = (TgaImageOrigin)((imageDescriptor >> 4) & 0x03);
        
        // Determine confidence
        int confidence = hasFooter ? 95 : 70; // Without footer, detection is less reliable
        
        var imgType = (TgaImageType)imageType;
        bool isRle = imageType >= 9;
        bool hasAlpha = pixelDepth == 32 || (pixelDepth == 16 && alphaBits > 0);
        
        return new TgaProbeResult
        {
            IsTga = true,
            Confidence = confidence,
            Width = width,
            Height = height,
            PixelDepth = pixelDepth,
            ImageType = imgType,
            Origin = origin,
            HasColorMap = colorMapType == 1,
            HasAlpha = hasAlpha,
            IsRleCompressed = isRle,
            HasFooter = hasFooter
        };
    }
    
    private static bool IsValidImageType(byte imageType)
    {
        return imageType is 0 or 1 or 2 or 3 or 9 or 10 or 11;
    }
    
    /// <summary>
    /// Parses the TGA structure.
    /// </summary>
    public bool Parse()
    {
        if (IsParsed) return true;
        
        stream.Position = startPosition;
        
        // Read entire file
        fileData = new byte[stream.Length - startPosition];
        int read = stream.Read(fileData);
        
        if (read < 18)
            return false;
        
        // Check for TGA 2.0 footer
        if (fileData.Length >= 26)
        {
            var footerSpan = fileData.AsSpan(fileData.Length - 26);
            if (footerSpan[8..26].SequenceEqual(Tga2FooterSignature))
            {
                HasFooter = true;
                ExtensionAreaOffset = BinaryPrimitives.ReadUInt32LittleEndian(footerSpan);
                DeveloperAreaOffset = BinaryPrimitives.ReadUInt32LittleEndian(footerSpan[4..]);
            }
        }
        
        var probeResult = Probe(fileData);
        if (!probeResult.IsTga)
            return false;
        
        // Parse header
        ImageIdLength = fileData[0];
        HasColorMap = fileData[1] == 1;
        ImageType = (TgaImageType)fileData[2];
        
        // Color map specification
        ColorMapFirstEntry = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan(3));
        ColorMapLength = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan(5));
        ColorMapEntrySize = fileData[7];
        
        // Image specification
        XOrigin = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan(8));
        YOrigin = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan(10));
        Width = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan(12));
        Height = BinaryPrimitives.ReadUInt16LittleEndian(fileData.AsSpan(14));
        PixelDepth = fileData[16];
        
        byte imageDescriptor = fileData[17];
        AlphaBits = imageDescriptor & 0x0F;
        Origin = (TgaImageOrigin)((imageDescriptor >> 4) & 0x03);
        
        HasAlpha = PixelDepth == 32 || (PixelDepth == 16 && AlphaBits > 0);
        IsRleCompressed = (int)ImageType >= 9;
        
        // Parse image ID
        if (ImageIdLength > 0 && fileData.Length >= 18 + ImageIdLength)
        {
            ImageId = Encoding.ASCII.GetString(fileData, 18, ImageIdLength).TrimEnd('\0');
        }
        
        // Parse extension area if present
        if (HasFooter && ExtensionAreaOffset > 0 && ExtensionAreaOffset + 495 <= fileData.Length)
        {
            ParseExtensionArea(fileData.AsSpan((int)ExtensionAreaOffset));
        }
        
        IsParsed = true;
        return true;
    }
    
    private void ParseExtensionArea(ReadOnlySpan<byte> data)
    {
        if (data.Length < 495)
            return;
        
        // Extension area size (should be 495)
        ushort extSize = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (extSize != 495)
            return;
        
        // Author name (41 bytes at offset 2)
        AuthorName = Encoding.ASCII.GetString(data.Slice(2, 41).ToArray()).TrimEnd('\0').TrimEnd();
        
        // Author comments (324 bytes at offset 43, 4 lines of 81 chars)
        AuthorComments = Encoding.ASCII.GetString(data.Slice(43, 324).ToArray()).TrimEnd('\0').TrimEnd();
        
        // Date/time (12 bytes at offset 367)
        ushort month = BinaryPrimitives.ReadUInt16LittleEndian(data[367..]);
        ushort day = BinaryPrimitives.ReadUInt16LittleEndian(data[369..]);
        ushort year = BinaryPrimitives.ReadUInt16LittleEndian(data[371..]);
        ushort hour = BinaryPrimitives.ReadUInt16LittleEndian(data[373..]);
        ushort minute = BinaryPrimitives.ReadUInt16LittleEndian(data[375..]);
        ushort second = BinaryPrimitives.ReadUInt16LittleEndian(data[377..]);
        
        if (year > 0 && month > 0 && month <= 12 && day > 0 && day <= 31)
        {
            try
            {
                Timestamp = new DateTime(year, month, day, hour, minute, second);
            }
            catch
            {
                // Invalid date
            }
        }
        
        // Software ID (41 bytes at offset 426)
        SoftwareId = Encoding.ASCII.GetString(data.Slice(426, 41).ToArray()).TrimEnd('\0').TrimEnd();
    }
    
    /// <summary>
    /// Reads the image as a frame.
    /// </summary>
    public TgaFrame? ReadFrame()
    {
        if (!IsParsed || fileData == null) return null;
        if (frameRead) return null;
        
        frameRead = true;
        
        return new TgaFrame
        {
            Width = Width,
            Height = Height,
            PixelDepth = PixelDepth,
            ImageType = ImageType,
            Origin = Origin,
            HasColorMap = HasColorMap,
            HasAlpha = HasAlpha,
            AlphaBits = AlphaBits,
            IsRleCompressed = IsRleCompressed,
            HasFooter = HasFooter,
            RawData = fileData,
            ImageId = ImageId,
            AuthorName = AuthorName,
            AuthorComments = AuthorComments,
            SoftwareId = SoftwareId,
            Timestamp = Timestamp
        };
    }
    
    /// <summary>
    /// Resets to allow reading the frame again.
    /// </summary>
    public void Reset()
    {
        frameRead = false;
    }
    
    /// <summary>
    /// Gets track information.
    /// </summary>
    public DemuxerTrackInfo? GetTrackInfo()
    {
        if (!IsParsed) return null;
        
        return new DemuxerTrackInfo
        {
            TrackType = DemuxerTrackType.Video,
            TrackId = 0,
            CodecId = "targa",
            Width = Width,
            Height = Height,
            BitsPerSample = PixelDepth,
            ChannelCount = ChannelCount
        };
    }
    
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        
        fileData = null;
        
        if (!leaveOpen)
            stream.Dispose();
    }
}

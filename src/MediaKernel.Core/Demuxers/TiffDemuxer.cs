// MediaKernel - TIFF Image Demuxer
// Demuxes TIFF (Tagged Image File Format) still images.
// Reference: TIFF Specification 6.0
//
// Supports:
// - Little-endian (II) and big-endian (MM) byte orders
// - Bilevel, grayscale, palette color, and RGB images
// - Bit depths: 1, 4, 8, 16, 32 bits per sample
// - Compression: None, LZW, Deflate, JPEG, PackBits
// - Multiple IFDs (multi-page TIFF)
// - EXIF and GPS metadata

using System.Buffers.Binary;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// TIFF byte order (endianness).
/// </summary>
public enum TiffByteOrder
{
    /// <summary>Little-endian (Intel).</summary>
    LittleEndian,
    
    /// <summary>Big-endian (Motorola).</summary>
    BigEndian
}

/// <summary>
/// TIFF photometric interpretation (color model).
/// </summary>
public enum TiffPhotometricInterpretation : ushort
{
    /// <summary>Bilevel: 0 is white.</summary>
    WhiteIsZero = 0,
    
    /// <summary>Bilevel: 0 is black.</summary>
    BlackIsZero = 1,
    
    /// <summary>RGB color.</summary>
    Rgb = 2,
    
    /// <summary>Palette color (indexed).</summary>
    Palette = 3,
    
    /// <summary>Transparency mask.</summary>
    TransparencyMask = 4,
    
    /// <summary>CMYK color.</summary>
    Cmyk = 5,
    
    /// <summary>YCbCr color (JPEG).</summary>
    YCbCr = 6,
    
    /// <summary>CIE L*a*b* color.</summary>
    CieLab = 8,
    
    /// <summary>ICC L*a*b* color.</summary>
    IccLab = 9,
    
    /// <summary>ITU L*a*b* color.</summary>
    ItuLab = 10,
    
    /// <summary>CFA (Color Filter Array) for RAW images.</summary>
    ColorFilterArray = 32803,
    
    /// <summary>Linear raw sensor data.</summary>
    LinearRaw = 34892
}

/// <summary>
/// TIFF compression method.
/// </summary>
public enum TiffCompression : ushort
{
    /// <summary>No compression.</summary>
    None = 1,
    
    /// <summary>CCITT Group 3 1-dimensional (fax).</summary>
    CcittGroup3 = 2,
    
    /// <summary>CCITT T.4 2-dimensional (fax).</summary>
    CcittT4 = 3,
    
    /// <summary>CCITT T.6 (fax).</summary>
    CcittT6 = 4,
    
    /// <summary>LZW compression.</summary>
    Lzw = 5,
    
    /// <summary>Old-style JPEG (deprecated).</summary>
    OldJpeg = 6,
    
    /// <summary>JPEG compression.</summary>
    Jpeg = 7,
    
    /// <summary>Adobe Deflate.</summary>
    AdobeDeflate = 8,
    
    /// <summary>JBIG black-and-white.</summary>
    JbigBw = 9,
    
    /// <summary>JBIG color.</summary>
    JbigColor = 10,
    
    /// <summary>PackBits RLE.</summary>
    PackBits = 32773,
    
    /// <summary>Deflate (zlib).</summary>
    Deflate = 32946
}

/// <summary>
/// TIFF sample format.
/// </summary>
public enum TiffSampleFormat : ushort
{
    /// <summary>Unsigned integer.</summary>
    UnsignedInteger = 1,
    
    /// <summary>Signed integer.</summary>
    SignedInteger = 2,
    
    /// <summary>IEEE floating point.</summary>
    FloatingPoint = 3,
    
    /// <summary>Undefined data format.</summary>
    Undefined = 4,
    
    /// <summary>Complex integer.</summary>
    ComplexInteger = 5,
    
    /// <summary>IEEE complex floating point.</summary>
    ComplexFloat = 6
}

/// <summary>
/// TIFF extra samples type.
/// </summary>
public enum TiffExtraSamples : ushort
{
    /// <summary>Unspecified extra samples.</summary>
    Unspecified = 0,
    
    /// <summary>Associated alpha (pre-multiplied).</summary>
    AssociatedAlpha = 1,
    
    /// <summary>Unassociated alpha (straight).</summary>
    UnassociatedAlpha = 2
}

/// <summary>
/// TIFF resolution unit.
/// </summary>
public enum TiffResolutionUnit : ushort
{
    /// <summary>No absolute unit (aspect ratio only).</summary>
    None = 1,
    
    /// <summary>Dots per inch.</summary>
    Inch = 2,
    
    /// <summary>Dots per centimeter.</summary>
    Centimeter = 3
}

/// <summary>
/// TIFF IFD (Image File Directory) entry.
/// </summary>
public readonly struct TiffTag
{
    public TiffTag(ushort id, ushort type, uint count, uint valueOffset, byte[] rawValue)
    {
        Id = id;
        Type = type;
        Count = count;
        ValueOffset = valueOffset;
        RawValue = rawValue;
    }
    
    public ushort Id { get; }
    public ushort Type { get; }
    public uint Count { get; }
    public uint ValueOffset { get; }
    public byte[] RawValue { get; }
    
    public bool IsInline => GetTypeSize(Type) * Count <= 4;
    
    private static int GetTypeSize(ushort type) => type switch
    {
        1 => 1,  // BYTE
        2 => 1,  // ASCII
        3 => 2,  // SHORT
        4 => 4,  // LONG
        5 => 8,  // RATIONAL
        6 => 1,  // SBYTE
        7 => 1,  // UNDEFINED
        8 => 2,  // SSHORT
        9 => 4,  // SLONG
        10 => 8, // SRATIONAL
        11 => 4, // FLOAT
        12 => 8, // DOUBLE
        _ => 1
    };
}

/// <summary>
/// TIFF probe result.
/// </summary>
public readonly struct TiffProbeResult
{
    public bool IsTiff { get; init; }
    public int Confidence { get; init; }
    public TiffByteOrder ByteOrder { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitsPerSample { get; init; }
    public int SamplesPerPixel { get; init; }
    public TiffPhotometricInterpretation PhotometricInterpretation { get; init; }
    public TiffCompression Compression { get; init; }
    public int IfdCount { get; init; }
    public bool HasAlpha { get; init; }
    public bool IsBigTiff { get; init; }
}

/// <summary>
/// TIFF image frame.
/// </summary>
public sealed class TiffFrame
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitsPerSample { get; init; }
    public int SamplesPerPixel { get; init; }
    public TiffPhotometricInterpretation PhotometricInterpretation { get; init; }
    public TiffCompression Compression { get; init; }
    public TiffSampleFormat SampleFormat { get; init; }
    public bool HasAlpha { get; init; }
    public TiffExtraSamples ExtraSamples { get; init; }
    public int PageIndex { get; init; }
    public int TotalPages { get; init; }
    public byte[] RawData { get; init; } = [];
    public double XResolution { get; init; }
    public double YResolution { get; init; }
    public TiffResolutionUnit ResolutionUnit { get; init; }
    public string? Description { get; init; }
    public string? Software { get; init; }
    public string? Artist { get; init; }
    public string? Copyright { get; init; }
    public DateTime? DateTime { get; init; }
}

/// <summary>
/// Demuxes TIFF (Tagged Image File Format) still images.
/// </summary>
public sealed class TiffDemuxer : IDisposable
{
    private static readonly byte[] LittleEndianMarker = [(byte)'I', (byte)'I', 0x2A, 0x00];
    private static readonly byte[] BigEndianMarker = [(byte)'M', (byte)'M', 0x00, 0x2A];
    private static readonly byte[] BigTiffLittleEndianMarker = [(byte)'I', (byte)'I', 0x2B, 0x00];
    private static readonly byte[] BigTiffBigEndianMarker = [(byte)'M', (byte)'M', 0x00, 0x2B];
    
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly long startPosition;
    private bool disposed;
    
    private TiffByteOrder byteOrder;
    private bool isBigTiff;
    private readonly List<long> ifdOffsets = [];
    private readonly List<List<TiffTag>> ifds = [];
    private int currentIfd;
    private bool frameRead;
    
    public TiffDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
        startPosition = stream.CanSeek ? stream.Position : 0;
    }
    
    public bool IsParsed { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int BitsPerSample { get; private set; } = 1;
    public int SamplesPerPixel { get; private set; } = 1;
    public TiffByteOrder ByteOrder => byteOrder;
    public bool IsBigTiff => isBigTiff;
    public TiffPhotometricInterpretation PhotometricInterpretation { get; private set; }
    public TiffCompression Compression { get; private set; } = TiffCompression.None;
    public TiffSampleFormat SampleFormat { get; private set; } = TiffSampleFormat.UnsignedInteger;
    public bool HasAlpha { get; private set; }
    public TiffExtraSamples ExtraSamples { get; private set; }
    public int IfdCount => ifds.Count;
    public int CurrentPage => currentIfd;
    public double XResolution { get; private set; }
    public double YResolution { get; private set; }
    public TiffResolutionUnit ResolutionUnit { get; private set; } = TiffResolutionUnit.None;
    public string? ImageDescription { get; private set; }
    public string? Software { get; private set; }
    public string? Artist { get; private set; }
    public string? Copyright { get; private set; }
    public DateTime? DateTime { get; private set; }
    public int ChannelCount => SamplesPerPixel;
    public bool IsEndOfStream => frameRead;
    
    public IReadOnlyList<TiffTag> Tags => ifds.Count > currentIfd ? ifds[currentIfd] : [];
    
    /// <summary>
    /// Probes a stream to determine if it contains a valid TIFF file.
    /// </summary>
    public static TiffProbeResult Probe(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        int read = stream.Read(header);
        
        if (read < 8)
            return new TiffProbeResult { IsTiff = false };
        
        return Probe(header);
    }
    
    /// <summary>
    /// Probes data to determine if it contains a valid TIFF file.
    /// </summary>
    public static TiffProbeResult Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            return new TiffProbeResult { IsTiff = false };
        
        TiffByteOrder order;
        bool isBigTiff = false;
        int confidence;
        
        if (data[..4].SequenceEqual(LittleEndianMarker))
        {
            order = TiffByteOrder.LittleEndian;
            confidence = 95;
        }
        else if (data[..4].SequenceEqual(BigEndianMarker))
        {
            order = TiffByteOrder.BigEndian;
            confidence = 95;
        }
        else if (data.Length >= 8 && data[..4].SequenceEqual(BigTiffLittleEndianMarker))
        {
            order = TiffByteOrder.LittleEndian;
            isBigTiff = true;
            confidence = 95;
        }
        else if (data.Length >= 8 && data[..4].SequenceEqual(BigTiffBigEndianMarker))
        {
            order = TiffByteOrder.BigEndian;
            isBigTiff = true;
            confidence = 95;
        }
        else
        {
            return new TiffProbeResult { IsTiff = false };
        }
        
        // Read IFD offset
        long ifdOffset;
        if (isBigTiff)
        {
            if (data.Length < 16)
                return new TiffProbeResult { IsTiff = false };
            
            // BigTIFF has offset size at byte 4-5, must be 8
            ushort offsetSize = ReadUInt16(data[4..], order);
            if (offsetSize != 8)
                return new TiffProbeResult { IsTiff = false };
            
            ifdOffset = (long)ReadUInt64(data[8..], order);
        }
        else
        {
            ifdOffset = ReadUInt32(data[4..], order);
        }
        
        if (ifdOffset < 8)
            return new TiffProbeResult { IsTiff = false };
        
        return new TiffProbeResult
        {
            IsTiff = true,
            Confidence = confidence,
            ByteOrder = order,
            IsBigTiff = isBigTiff
        };
    }
    
    /// <summary>
    /// Parses the TIFF structure.
    /// </summary>
    public bool Parse()
    {
        if (IsParsed) return true;
        
        if (!stream.CanSeek)
            return false;
        
        stream.Position = startPosition;
        
        Span<byte> header = stackalloc byte[16];
        int read = stream.Read(header);
        
        if (read < 8)
            return false;
        
        var probeResult = Probe(header[..read]);
        if (!probeResult.IsTiff)
            return false;
        
        byteOrder = probeResult.ByteOrder;
        isBigTiff = probeResult.IsBigTiff;
        
        // Get first IFD offset
        long firstIfdOffset;
        if (isBigTiff)
        {
            if (read < 16) return false;
            firstIfdOffset = (long)ReadUInt64(header[8..], byteOrder);
        }
        else
        {
            firstIfdOffset = ReadUInt32(header[4..], byteOrder);
        }
        
        // Parse all IFDs
        long nextIfdOffset = firstIfdOffset;
        while (nextIfdOffset != 0)
        {
            stream.Position = startPosition + nextIfdOffset;
            
            var ifd = ParseIfd(out nextIfdOffset);
            if (ifd == null)
                break;
            
            ifdOffsets.Add(nextIfdOffset);
            ifds.Add(ifd);
        }
        
        if (ifds.Count == 0)
            return false;
        
        // Extract info from first IFD
        ExtractImageInfo(ifds[0]);
        
        IsParsed = true;
        return true;
    }
    
    private List<TiffTag>? ParseIfd(out long nextIfdOffset)
    {
        nextIfdOffset = 0;
        
        int entryCount;
        int entrySize;
        
        if (isBigTiff)
        {
            Span<byte> countBytes = stackalloc byte[8];
            if (stream.Read(countBytes) < 8) return null;
            entryCount = (int)ReadUInt64(countBytes, byteOrder);
            entrySize = 20; // BigTIFF entry size
        }
        else
        {
            Span<byte> countBytes = stackalloc byte[2];
            if (stream.Read(countBytes) < 2) return null;
            entryCount = ReadUInt16(countBytes, byteOrder);
            entrySize = 12; // Standard TIFF entry size
        }
        
        if (entryCount < 0 || entryCount > 65535)
            return null;
        
        var tags = new List<TiffTag>(entryCount);
        Span<byte> entryBuffer = stackalloc byte[20]; // Max size for BigTIFF
        
        for (int i = 0; i < entryCount; i++)
        {
            if (stream.Read(entryBuffer[..entrySize]) < entrySize)
                return null;
            
            var tag = ParseTag(entryBuffer[..entrySize]);
            tags.Add(tag);
        }
        
        // Read next IFD offset
        if (isBigTiff)
        {
            Span<byte> offsetBytes = stackalloc byte[8];
            if (stream.Read(offsetBytes) >= 8)
                nextIfdOffset = (long)ReadUInt64(offsetBytes, byteOrder);
        }
        else
        {
            Span<byte> offsetBytes = stackalloc byte[4];
            if (stream.Read(offsetBytes) >= 4)
                nextIfdOffset = ReadUInt32(offsetBytes, byteOrder);
        }
        
        return tags;
    }
    
    private TiffTag ParseTag(ReadOnlySpan<byte> data)
    {
        ushort id = ReadUInt16(data, byteOrder);
        ushort type = ReadUInt16(data[2..], byteOrder);
        uint count;
        uint valueOffset;
        
        if (isBigTiff)
        {
            count = (uint)ReadUInt64(data[4..], byteOrder);
            valueOffset = (uint)ReadUInt64(data[12..], byteOrder);
        }
        else
        {
            count = ReadUInt32(data[4..], byteOrder);
            valueOffset = ReadUInt32(data[8..], byteOrder);
        }
        
        // For inline values, store them directly
        byte[] rawValue;
        int typeSize = GetTypeSize(type);
        int totalSize = typeSize * (int)count;
        int maxInlineSize = isBigTiff ? 8 : 4;
        
        if (totalSize <= maxInlineSize)
        {
            rawValue = new byte[totalSize];
            int valueFieldOffset = isBigTiff ? 12 : 8;
            // Only copy up to the available bytes in the value field
            data.Slice(valueFieldOffset, Math.Min(totalSize, maxInlineSize)).CopyTo(rawValue);
        }
        else
        {
            rawValue = [];
        }
        
        return new TiffTag(id, type, count, valueOffset, rawValue);
    }
    
    private static int GetTypeSize(ushort type) => type switch
    {
        1 => 1,  // BYTE
        2 => 1,  // ASCII
        3 => 2,  // SHORT
        4 => 4,  // LONG
        5 => 8,  // RATIONAL
        6 => 1,  // SBYTE
        7 => 1,  // UNDEFINED
        8 => 2,  // SSHORT
        9 => 4,  // SLONG
        10 => 8, // SRATIONAL
        11 => 4, // FLOAT
        12 => 8, // DOUBLE
        _ => 1
    };
    
    private void ExtractImageInfo(List<TiffTag> tags)
    {
        foreach (var tag in tags)
        {
            switch (tag.Id)
            {
                case 256: // ImageWidth
                    Width = ReadTagInt(tag);
                    break;
                case 257: // ImageLength (Height)
                    Height = ReadTagInt(tag);
                    break;
                case 258: // BitsPerSample
                    BitsPerSample = ReadTagInt(tag);
                    break;
                case 259: // Compression
                    Compression = (TiffCompression)ReadTagInt(tag);
                    break;
                case 262: // PhotometricInterpretation
                    PhotometricInterpretation = (TiffPhotometricInterpretation)ReadTagInt(tag);
                    break;
                case 270: // ImageDescription
                    ImageDescription = ReadTagString(tag);
                    break;
                case 277: // SamplesPerPixel
                    SamplesPerPixel = ReadTagInt(tag);
                    break;
                case 282: // XResolution
                    XResolution = ReadTagRational(tag);
                    break;
                case 283: // YResolution
                    YResolution = ReadTagRational(tag);
                    break;
                case 296: // ResolutionUnit
                    ResolutionUnit = (TiffResolutionUnit)ReadTagInt(tag);
                    break;
                case 305: // Software
                    Software = ReadTagString(tag);
                    break;
                case 306: // DateTime
                    DateTime = TryParseDateTime(ReadTagString(tag));
                    break;
                case 315: // Artist
                    Artist = ReadTagString(tag);
                    break;
                case 338: // ExtraSamples
                    ExtraSamples = (TiffExtraSamples)ReadTagInt(tag);
                    HasAlpha = ExtraSamples is TiffExtraSamples.AssociatedAlpha or TiffExtraSamples.UnassociatedAlpha;
                    break;
                case 339: // SampleFormat
                    SampleFormat = (TiffSampleFormat)ReadTagInt(tag);
                    break;
                case 33432: // Copyright
                    Copyright = ReadTagString(tag);
                    break;
            }
        }
        
        // Infer alpha from samples per pixel
        if (!HasAlpha && SamplesPerPixel == 4 && PhotometricInterpretation == TiffPhotometricInterpretation.Rgb)
            HasAlpha = true;
        if (!HasAlpha && SamplesPerPixel == 2 && PhotometricInterpretation == TiffPhotometricInterpretation.BlackIsZero)
            HasAlpha = true;
    }
    
    private int ReadTagInt(TiffTag tag)
    {
        if (tag.RawValue.Length > 0)
        {
            return tag.Type switch
            {
                1 => tag.RawValue[0],       // BYTE
                3 => ReadUInt16(tag.RawValue, byteOrder), // SHORT
                4 => (int)ReadUInt32(tag.RawValue, byteOrder), // LONG
                _ => tag.RawValue[0]
            };
        }
        
        // Read from offset
        long pos = stream.Position;
        stream.Position = startPosition + tag.ValueOffset;
        
        Span<byte> buffer = stackalloc byte[4];
        stream.Read(buffer);
        stream.Position = pos;
        
        return tag.Type switch
        {
            1 => buffer[0],
            3 => ReadUInt16(buffer, byteOrder),
            4 => (int)ReadUInt32(buffer, byteOrder),
            _ => buffer[0]
        };
    }
    
    private string? ReadTagString(TiffTag tag)
    {
        if (tag.Type != 2) return null;
        if (tag.Count == 0) return null;
        
        byte[] data;
        if (tag.RawValue.Length > 0)
        {
            data = tag.RawValue;
        }
        else
        {
            long pos = stream.Position;
            stream.Position = startPosition + tag.ValueOffset;
            
            data = new byte[tag.Count];
            stream.Read(data);
            stream.Position = pos;
        }
        
        // Remove null terminator
        int len = data.Length;
        while (len > 0 && data[len - 1] == 0) len--;
        
        return Encoding.ASCII.GetString(data, 0, len);
    }
    
    private double ReadTagRational(TiffTag tag)
    {
        if (tag.Type != 5) return 0;
        
        Span<byte> buffer = stackalloc byte[8];
        
        if (tag.RawValue.Length >= 8)
        {
            tag.RawValue.AsSpan(0, 8).CopyTo(buffer);
        }
        else
        {
            long pos = stream.Position;
            stream.Position = startPosition + tag.ValueOffset;
            stream.Read(buffer);
            stream.Position = pos;
        }
        
        uint numerator = ReadUInt32(buffer, byteOrder);
        uint denominator = ReadUInt32(buffer[4..], byteOrder);
        
        if (denominator == 0) return 0;
        return (double)numerator / denominator;
    }
    
    private static DateTime? TryParseDateTime(string? str)
    {
        if (string.IsNullOrEmpty(str)) return null;
        
        // TIFF DateTime format: "YYYY:MM:DD HH:MM:SS"
        if (str.Length >= 19 && 
            System.DateTime.TryParseExact(
                str[..19], 
                "yyyy:MM:dd HH:mm:ss", 
                null, 
                System.Globalization.DateTimeStyles.None, 
                out var dt))
        {
            return dt;
        }
        
        return null;
    }
    
    /// <summary>
    /// Reads the current page as a frame.
    /// </summary>
    public TiffFrame? ReadFrame()
    {
        if (!IsParsed) return null;
        if (frameRead) return null;
        if (currentIfd >= ifds.Count) return null;
        
        frameRead = true;
        
        // Read entire file as raw data
        stream.Position = startPosition;
        byte[] rawData = new byte[stream.Length - startPosition];
        stream.Read(rawData);
        
        return new TiffFrame
        {
            Width = Width,
            Height = Height,
            BitsPerSample = BitsPerSample,
            SamplesPerPixel = SamplesPerPixel,
            PhotometricInterpretation = PhotometricInterpretation,
            Compression = Compression,
            SampleFormat = SampleFormat,
            HasAlpha = HasAlpha,
            ExtraSamples = ExtraSamples,
            PageIndex = currentIfd,
            TotalPages = ifds.Count,
            RawData = rawData,
            XResolution = XResolution,
            YResolution = YResolution,
            ResolutionUnit = ResolutionUnit,
            Description = ImageDescription,
            Software = Software,
            Artist = Artist,
            Copyright = Copyright,
            DateTime = DateTime
        };
    }
    
    /// <summary>
    /// Selects a page in a multi-page TIFF.
    /// </summary>
    public bool SelectPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= ifds.Count)
            return false;
        
        currentIfd = pageIndex;
        frameRead = false;
        ExtractImageInfo(ifds[currentIfd]);
        return true;
    }
    
    /// <summary>
    /// Resets to the first frame.
    /// </summary>
    public void Reset()
    {
        currentIfd = 0;
        frameRead = false;
        if (ifds.Count > 0)
            ExtractImageInfo(ifds[0]);
    }
    
    /// <summary>
    /// Gets track information for the video track.
    /// </summary>
    public DemuxerTrackInfo? GetTrackInfo()
    {
        if (!IsParsed) return null;
        
        return new DemuxerTrackInfo
        {
            TrackType = DemuxerTrackType.Video,
            TrackId = 0,
            CodecId = "tiff",
            Width = Width,
            Height = Height,
            BitsPerSample = BitsPerSample,
            ChannelCount = SamplesPerPixel
        };
    }
    
    /// <summary>
    /// Gets a tag by ID from the current IFD.
    /// </summary>
    public TiffTag? GetTag(ushort tagId)
    {
        if (currentIfd >= ifds.Count) return null;
        
        foreach (var tag in ifds[currentIfd])
        {
            if (tag.Id == tagId)
                return tag;
        }
        
        return null;
    }
    
    /// <summary>
    /// Checks if a tag exists in the current IFD.
    /// </summary>
    public bool HasTag(ushort tagId)
    {
        if (currentIfd >= ifds.Count) return false;
        
        foreach (var tag in ifds[currentIfd])
        {
            if (tag.Id == tagId)
                return true;
        }
        
        return false;
    }
    
    private static ushort ReadUInt16(ReadOnlySpan<byte> data, TiffByteOrder order)
    {
        return order == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data)
            : BinaryPrimitives.ReadUInt16BigEndian(data);
    }
    
    private static uint ReadUInt32(ReadOnlySpan<byte> data, TiffByteOrder order)
    {
        return order == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data)
            : BinaryPrimitives.ReadUInt32BigEndian(data);
    }
    
    private static ulong ReadUInt64(ReadOnlySpan<byte> data, TiffByteOrder order)
    {
        return order == TiffByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(data)
            : BinaryPrimitives.ReadUInt64BigEndian(data);
    }
    
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        
        if (!leaveOpen)
            stream.Dispose();
    }
}

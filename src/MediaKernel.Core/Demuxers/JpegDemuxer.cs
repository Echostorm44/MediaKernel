// MediaKernel - JPEG Image Demuxer
// Demuxes JPEG still images (JFIF/EXIF formats).
// Reference: ITU-T T.81, JFIF 1.02, EXIF 2.32
//
// Supports:
// - JFIF (Joint Photographic File Interchange Format)
// - EXIF (Exchangeable Image File Format) including camera metadata
// - Baseline DCT, Extended Sequential, Progressive, and Lossless JPEG
// - Grayscale and color images (YCbCr, RGB)
// - 4:4:4, 4:2:2, 4:2:0, 4:4:0 chroma subsampling

using System.Buffers.Binary;
using System.Text;
using MediaKernel.Core.Codecs.Mjpeg;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// JPEG format type.
/// </summary>
public enum JpegFormatType
{
    /// <summary>Unknown JPEG format.</summary>
    Unknown = 0,
    
    /// <summary>JFIF format (APP0 marker with "JFIF\0" identifier).</summary>
    Jfif = 1,
    
    /// <summary>EXIF format (APP1 marker with "Exif\0\0" identifier).</summary>
    Exif = 2,
    
    /// <summary>SPIFF format (APP8 marker with "SPIFF\0" identifier).</summary>
    Spiff = 3,
    
    /// <summary>Raw JPEG (no application markers or unrecognized format).</summary>
    Raw = 4,
    
    /// <summary>Adobe JPEG (APP14 marker with "Adobe" identifier).</summary>
    Adobe = 5
}

/// <summary>
/// EXIF orientation values (camera rotation).
/// </summary>
public enum ExifOrientation
{
    /// <summary>Normal orientation (no rotation).</summary>
    Normal = 1,
    
    /// <summary>Flipped horizontally.</summary>
    FlipHorizontal = 2,
    
    /// <summary>Rotated 180 degrees.</summary>
    Rotate180 = 3,
    
    /// <summary>Flipped vertically.</summary>
    FlipVertical = 4,
    
    /// <summary>Rotated 90 degrees CW then flipped horizontally.</summary>
    Rotate90CwFlipHorizontal = 5,
    
    /// <summary>Rotated 90 degrees clockwise.</summary>
    Rotate90Cw = 6,
    
    /// <summary>Rotated 90 degrees CCW then flipped horizontally.</summary>
    Rotate90CcwFlipHorizontal = 7,
    
    /// <summary>Rotated 90 degrees counter-clockwise.</summary>
    Rotate90Ccw = 8,
    
    /// <summary>Unknown orientation.</summary>
    Unknown = 0
}

/// <summary>
/// Result from probing a stream for JPEG content.
/// </summary>
public readonly struct JpegProbeResult
{
    /// <summary>Whether valid JPEG content was detected.</summary>
    public bool IsJpeg { get; init; }
    
    /// <summary>JPEG format type (JFIF, EXIF, etc.).</summary>
    public JpegFormatType FormatType { get; init; }
    
    /// <summary>Image width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Image height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Bits per component (typically 8 or 12).</summary>
    public int BitsPerComponent { get; init; }
    
    /// <summary>Number of color components (1 = grayscale, 3 = color).</summary>
    public int ComponentCount { get; init; }
    
    /// <summary>JPEG encoding type.</summary>
    public JpegEncodingType EncodingType { get; init; }
    
    /// <summary>Chroma subsampling pattern.</summary>
    public JpegSubsampling Subsampling { get; init; }
    
    /// <summary>EXIF orientation (camera rotation).</summary>
    public ExifOrientation Orientation { get; init; }
    
    /// <summary>Confidence score (0-100).</summary>
    public int Confidence { get; init; }
}

/// <summary>
/// A decoded JPEG frame.
/// </summary>
public sealed class JpegFrame
{
    /// <summary>Raw JPEG data (compressed).</summary>
    public byte[] RawData { get; init; } = [];
    
    /// <summary>Image width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Image height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Bits per component.</summary>
    public int BitsPerComponent { get; init; }
    
    /// <summary>Number of color components.</summary>
    public int ComponentCount { get; init; }
    
    /// <summary>JPEG encoding type.</summary>
    public JpegEncodingType EncodingType { get; init; }
    
    /// <summary>Chroma subsampling pattern.</summary>
    public JpegSubsampling Subsampling { get; init; }
    
    /// <summary>JPEG format type.</summary>
    public JpegFormatType FormatType { get; init; }
    
    /// <summary>EXIF orientation.</summary>
    public ExifOrientation Orientation { get; init; }
    
    /// <summary>Whether image is grayscale.</summary>
    public bool IsGrayscale => ComponentCount == 1;
    
    /// <summary>Whether image is progressive JPEG.</summary>
    public bool IsProgressive => EncodingType == JpegEncodingType.Progressive;
}

/// <summary>
/// Demuxer for JPEG still images.
/// </summary>
/// <remarks>
/// This demuxer handles single JPEG images. For Motion JPEG streams,
/// use the MjpegDemuxer instead.
/// 
/// The demuxer validates JPEG structure but does not decode the image.
/// The raw compressed data can be passed to a JPEG decoder.
/// </remarks>
public sealed class JpegDemuxer : IDisposable
{
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private bool isParsed;
    private bool isDisposed;
    private bool frameRead;
    
    // Parsed JPEG info
    private int width;
    private int height;
    private int bitsPerComponent;
    private int componentCount;
    private JpegEncodingType encodingType;
    private JpegSubsampling subsampling;
    private JpegFormatType formatType;
    private ExifOrientation orientation = ExifOrientation.Normal;
    private byte[]? imageData;
    
    /// <summary>
    /// Creates a new JPEG demuxer.
    /// </summary>
    public JpegDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
    }
    
    /// <summary>Gets whether the JPEG has been successfully parsed.</summary>
    public bool IsParsed => isParsed;
    
    /// <summary>Gets the image width.</summary>
    public int Width => width;
    
    /// <summary>Gets the image height.</summary>
    public int Height => height;
    
    /// <summary>Gets the bits per component.</summary>
    public int BitsPerComponent => bitsPerComponent;
    
    /// <summary>Gets the number of color components.</summary>
    public int ComponentCount => componentCount;
    
    /// <summary>Gets the JPEG encoding type.</summary>
    public JpegEncodingType EncodingType => encodingType;
    
    /// <summary>Gets the chroma subsampling pattern.</summary>
    public JpegSubsampling Subsampling => subsampling;
    
    /// <summary>Gets the JPEG format type.</summary>
    public JpegFormatType FormatType => formatType;
    
    /// <summary>Gets the EXIF orientation.</summary>
    public ExifOrientation Orientation => orientation;
    
    /// <summary>Gets whether all frames have been read.</summary>
    public bool IsEndOfStream => frameRead;
    
    /// <summary>Gets the codec name.</summary>
    public string Codec => encodingType switch
    {
        JpegEncodingType.Progressive => "jpeg-progressive",
        JpegEncodingType.Lossless => "jpeg-lossless",
        _ => "jpeg"
    };
    
    /// <summary>
    /// Probe a stream for JPEG content without fully parsing.
    /// </summary>
    public static JpegProbeResult Probe(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4096];
        
        long originalPosition = stream.Position;
        int bytesRead = stream.Read(buffer);
        stream.Position = originalPosition;
        
        if (bytesRead < 12)
            return new JpegProbeResult { IsJpeg = false };
        
        return ProbeBuffer(buffer[..bytesRead]);
    }
    
    /// <summary>
    /// Probe a buffer for JPEG content.
    /// </summary>
    public static JpegProbeResult Probe(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 12)
            return new JpegProbeResult { IsJpeg = false };
        
        return ProbeBuffer(buffer);
    }
    
    private static JpegProbeResult ProbeBuffer(ReadOnlySpan<byte> buffer)
    {
        // Check SOI marker
        if (buffer[0] != 0xFF || buffer[1] != JpegMarkers.StartOfImage)
            return new JpegProbeResult { IsJpeg = false };
        
        int confidence = 50; // Valid SOI
        var formatType = JpegFormatType.Unknown;
        var orientation = ExifOrientation.Normal;
        
        // Look for APP markers to determine format
        int offset = 2;
        while (offset < buffer.Length - 3)
        {
            if (buffer[offset] != 0xFF)
            {
                offset++;
                continue;
            }
            
            // Skip fill bytes
            while (offset < buffer.Length && buffer[offset] == 0xFF)
                offset++;
            
            if (offset >= buffer.Length)
                break;
            
            byte marker = buffer[offset++];
            
            // Check for format-identifying APP markers
            switch (marker)
            {
                case JpegMarkers.App0:
                    if (offset + 7 <= buffer.Length)
                    {
                        int len = BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]);
                        // Check for "JFIF\0"
                        if (len >= 7 && offset + 6 < buffer.Length &&
                            buffer[offset + 2] == 'J' && buffer[offset + 3] == 'F' &&
                            buffer[offset + 4] == 'I' && buffer[offset + 5] == 'F' &&
                            buffer[offset + 6] == 0)
                        {
                            formatType = JpegFormatType.Jfif;
                            confidence = 90;
                        }
                        offset += len;
                    }
                    break;
                
                case JpegMarkers.App1:
                    if (offset + 8 <= buffer.Length)
                    {
                        int len = BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]);
                        // Check for "Exif\0\0"
                        if (len >= 8 && offset + 7 < buffer.Length &&
                            buffer[offset + 2] == 'E' && buffer[offset + 3] == 'x' &&
                            buffer[offset + 4] == 'i' && buffer[offset + 5] == 'f' &&
                            buffer[offset + 6] == 0 && buffer[offset + 7] == 0)
                        {
                            formatType = JpegFormatType.Exif;
                            confidence = 90;
                            
                            // Try to parse orientation
                            if (offset + len <= buffer.Length)
                            {
                                orientation = TryParseExifOrientation(buffer[(offset + 8)..Math.Min(offset + len, buffer.Length)]);
                            }
                        }
                        // Check for XMP
                        else if (len >= 29 && offset + 30 < buffer.Length &&
                                 buffer[(offset + 2)..(offset + 30)].SequenceEqual("http://ns.adobe.com/xap/1.0/"u8))
                        {
                            formatType = JpegFormatType.Exif;
                            confidence = 85;
                        }
                        offset += len;
                    }
                    break;
                
                case 0xE8: // APP8
                    if (offset + 8 <= buffer.Length)
                    {
                        int len = BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]);
                        // Check for "SPIFF\0"
                        if (len >= 8 && offset + 7 < buffer.Length &&
                            buffer[offset + 2] == 'S' && buffer[offset + 3] == 'P' &&
                            buffer[offset + 4] == 'I' && buffer[offset + 5] == 'F' &&
                            buffer[offset + 6] == 'F' && buffer[offset + 7] == 0)
                        {
                            formatType = JpegFormatType.Spiff;
                            confidence = 90;
                        }
                        offset += len;
                    }
                    break;
                
                case 0xEE: // APP14
                    if (offset + 7 <= buffer.Length)
                    {
                        int len = BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]);
                        // Check for "Adobe"
                        if (len >= 7 && offset + 6 < buffer.Length &&
                            buffer[offset + 2] == 'A' && buffer[offset + 3] == 'd' &&
                            buffer[offset + 4] == 'o' && buffer[offset + 5] == 'b' &&
                            buffer[offset + 6] == 'e')
                        {
                            formatType = JpegFormatType.Adobe;
                            confidence = 85;
                        }
                        offset += len;
                    }
                    break;
                
                case JpegMarkers.StartOfFrameBaseline:
                case JpegMarkers.StartOfFrameExtended:
                case JpegMarkers.StartOfFrameProgressive:
                case JpegMarkers.StartOfFrameLossless:
                    // Found SOF - parse dimensions
                    if (MjpegHeaderParser.TryParseFrameHeader(buffer, out var header))
                    {
                        if (formatType == JpegFormatType.Unknown)
                            formatType = JpegFormatType.Raw;
                        
                        confidence = Math.Max(confidence, 80);
                        
                        return new JpegProbeResult
                        {
                            IsJpeg = true,
                            FormatType = formatType,
                            Width = header.Width,
                            Height = header.Height,
                            BitsPerComponent = header.BitsPerComponent,
                            ComponentCount = header.ComponentCount,
                            EncodingType = header.EncodingType,
                            Subsampling = header.Subsampling,
                            Orientation = orientation,
                            Confidence = confidence
                        };
                    }
                    goto exitLoop;
                
                case JpegMarkers.EndOfImage:
                    goto exitLoop;
                
                case 0x00: // Stuffed byte
                case 0xD0: case 0xD1: case 0xD2: case 0xD3:
                case 0xD4: case 0xD5: case 0xD6: case 0xD7: // RST markers
                    break;
                
                default:
                    // Skip marker with length
                    if (offset + 2 <= buffer.Length)
                    {
                        int len = BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]);
                        offset += len;
                    }
                    break;
            }
        }
        
    exitLoop:
        if (formatType == JpegFormatType.Unknown)
            formatType = JpegFormatType.Raw;
        
        return new JpegProbeResult
        {
            IsJpeg = confidence >= 50,
            FormatType = formatType,
            Orientation = orientation,
            Confidence = confidence
        };
    }
    
    private static ExifOrientation TryParseExifOrientation(ReadOnlySpan<byte> exifData)
    {
        if (exifData.Length < 8)
            return ExifOrientation.Normal;
        
        // Check TIFF header
        bool isLittleEndian;
        if (exifData[0] == 'I' && exifData[1] == 'I')
            isLittleEndian = true;
        else if (exifData[0] == 'M' && exifData[1] == 'M')
            isLittleEndian = false;
        else
            return ExifOrientation.Normal;
        
        // Verify magic number
        ushort magic = isLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(exifData[2..])
            : BinaryPrimitives.ReadUInt16BigEndian(exifData[2..]);
        
        if (magic != 42)
            return ExifOrientation.Normal;
        
        // Get IFD0 offset
        uint ifdOffset = isLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(exifData[4..])
            : BinaryPrimitives.ReadUInt32BigEndian(exifData[4..]);
        
        if (ifdOffset + 2 > (uint)exifData.Length)
            return ExifOrientation.Normal;
        
        // Parse IFD0 entries
        ushort entryCount = isLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(exifData[(int)ifdOffset..])
            : BinaryPrimitives.ReadUInt16BigEndian(exifData[(int)ifdOffset..]);
        
        int pos = (int)ifdOffset + 2;
        for (int i = 0; i < entryCount && pos + 12 <= exifData.Length; i++)
        {
            ushort tag = isLittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(exifData[pos..])
                : BinaryPrimitives.ReadUInt16BigEndian(exifData[pos..]);
            
            // Orientation tag is 0x0112
            if (tag == 0x0112)
            {
                ushort orientationValue = isLittleEndian
                    ? BinaryPrimitives.ReadUInt16LittleEndian(exifData[(pos + 8)..])
                    : BinaryPrimitives.ReadUInt16BigEndian(exifData[(pos + 8)..]);
                
                return orientationValue switch
                {
                    1 => ExifOrientation.Normal,
                    2 => ExifOrientation.FlipHorizontal,
                    3 => ExifOrientation.Rotate180,
                    4 => ExifOrientation.FlipVertical,
                    5 => ExifOrientation.Rotate90CwFlipHorizontal,
                    6 => ExifOrientation.Rotate90Cw,
                    7 => ExifOrientation.Rotate90CcwFlipHorizontal,
                    8 => ExifOrientation.Rotate90Ccw,
                    _ => ExifOrientation.Unknown
                };
            }
            
            pos += 12;
        }
        
        return ExifOrientation.Normal;
    }
    
    /// <summary>
    /// Parse the JPEG file.
    /// </summary>
    public bool Parse()
    {
        if (isParsed) return true;
        ObjectDisposedException.ThrowIf(isDisposed, this);
        
        try
        {
            if (stream.CanSeek)
                stream.Position = 0;
            
            // Read entire file (JPEG files are typically small)
            long length = stream.CanSeek ? stream.Length : 0;
            if (length == 0)
                length = 10 * 1024 * 1024; // 10MB max for unseekable streams
            
            using var ms = new MemoryStream((int)Math.Min(length, 10 * 1024 * 1024));
            stream.CopyTo(ms);
            imageData = ms.ToArray();
            
            if (imageData.Length < 12)
                return false;
            
            // Check SOI
            if (imageData[0] != 0xFF || imageData[1] != JpegMarkers.StartOfImage)
                return false;
            
            // Parse header using existing parser
            if (!MjpegHeaderParser.TryParseFrameHeader(imageData, out var header))
                return false;
            
            width = header.Width;
            height = header.Height;
            bitsPerComponent = header.BitsPerComponent;
            componentCount = header.ComponentCount;
            encodingType = header.EncodingType;
            subsampling = header.Subsampling;
            
            // Detect format type and orientation
            var probeResult = ProbeBuffer(imageData);
            formatType = probeResult.FormatType;
            orientation = probeResult.Orientation;
            
            isParsed = true;
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Read the JPEG frame.
    /// </summary>
    public JpegFrame? ReadFrame()
    {
        if (!isParsed && !Parse())
            return null;
        
        if (frameRead)
            return null;
        
        frameRead = true;
        
        return new JpegFrame
        {
            RawData = imageData!,
            Width = width,
            Height = height,
            BitsPerComponent = bitsPerComponent,
            ComponentCount = componentCount,
            EncodingType = encodingType,
            Subsampling = subsampling,
            FormatType = formatType,
            Orientation = orientation
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
            BitsPerSample = bitsPerComponent
        };
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

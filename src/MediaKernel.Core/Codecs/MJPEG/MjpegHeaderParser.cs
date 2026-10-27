// MJPEG/JPEG frame header parsing support
// Reference: ITU-T T.81 (JPEG standard)

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Mjpeg;

/// <summary>
/// JPEG marker codes.
/// </summary>
public static class JpegMarkers
{
    /// <summary>Start of image.</summary>
    public const byte StartOfImage = 0xD8;
    
    /// <summary>End of image.</summary>
    public const byte EndOfImage = 0xD9;
    
    /// <summary>Start of frame (baseline DCT).</summary>
    public const byte StartOfFrameBaseline = 0xC0;
    
    /// <summary>Start of frame (extended sequential DCT).</summary>
    public const byte StartOfFrameExtended = 0xC1;
    
    /// <summary>Start of frame (progressive DCT).</summary>
    public const byte StartOfFrameProgressive = 0xC2;
    
    /// <summary>Start of frame (lossless).</summary>
    public const byte StartOfFrameLossless = 0xC3;
    
    /// <summary>Define Huffman tables.</summary>
    public const byte DefineHuffmanTable = 0xC4;
    
    /// <summary>Start of scan.</summary>
    public const byte StartOfScan = 0xDA;
    
    /// <summary>Define quantization table.</summary>
    public const byte DefineQuantizationTable = 0xDB;
    
    /// <summary>Define restart interval.</summary>
    public const byte DefineRestartInterval = 0xDD;
    
    /// <summary>APP0 (JFIF).</summary>
    public const byte App0 = 0xE0;
    
    /// <summary>APP1 (EXIF).</summary>
    public const byte App1 = 0xE1;
    
    /// <summary>Comment.</summary>
    public const byte Comment = 0xFE;
    
    /// <summary>Fill byte (padding).</summary>
    public const byte Fill = 0xFF;
}

/// <summary>
/// JPEG encoding type.
/// </summary>
public enum JpegEncodingType : byte
{
    /// <summary>Baseline DCT (most common).</summary>
    Baseline = 0,
    
    /// <summary>Extended sequential DCT.</summary>
    Extended = 1,
    
    /// <summary>Progressive DCT.</summary>
    Progressive = 2,
    
    /// <summary>Lossless.</summary>
    Lossless = 3,
    
    /// <summary>Unknown encoding.</summary>
    Unknown = 255
}

/// <summary>
/// JPEG chroma subsampling patterns.
/// </summary>
public enum JpegSubsampling : byte
{
    /// <summary>4:4:4 (no subsampling).</summary>
    Yuv444 = 0,
    
    /// <summary>4:2:2 (horizontal subsampling).</summary>
    Yuv422 = 1,
    
    /// <summary>4:2:0 (both dimensions subsampled).</summary>
    Yuv420 = 2,
    
    /// <summary>4:4:0 (vertical subsampling only).</summary>
    Yuv440 = 3,
    
    /// <summary>Grayscale (no chroma).</summary>
    Grayscale = 4,
    
    /// <summary>Unknown subsampling.</summary>
    Unknown = 255
}

/// <summary>
/// JPEG frame header information.
/// </summary>
public readonly struct JpegFrameHeader
{
    /// <summary>Whether a valid frame was found.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Frame width in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Frame height in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Sample precision (bits per component, usually 8).</summary>
    public int BitsPerComponent { get; init; }
    
    /// <summary>Number of color components (1 = grayscale, 3 = YCbCr/RGB).</summary>
    public int ComponentCount { get; init; }
    
    /// <summary>Encoding type (baseline, progressive, etc.).</summary>
    public JpegEncodingType EncodingType { get; init; }
    
    /// <summary>Chroma subsampling pattern.</summary>
    public JpegSubsampling Subsampling { get; init; }
    
    /// <summary>Offset to the Start of Frame marker.</summary>
    public int SofOffset { get; init; }
    
    /// <summary>Offset to the Start of Scan marker (compressed data).</summary>
    public int SosOffset { get; init; }
    
    /// <summary>Offset to End of Image marker.</summary>
    public int EoiOffset { get; init; }
    
    /// <summary>Total frame size in bytes (including EOI).</summary>
    public int FrameSize => EoiOffset > 0 ? EoiOffset + 2 : 0;
    
    /// <summary>Whether this is a progressive JPEG.</summary>
    public bool IsProgressive => EncodingType == JpegEncodingType.Progressive;
    
    /// <summary>Whether this is grayscale.</summary>
    public bool IsGrayscale => ComponentCount == 1;
}

/// <summary>
/// MJPEG header parser for frame parsing.
/// </summary>
/// <remarks>
/// Parses JPEG/MJPEG frame headers for demuxer support.
/// MJPEG is simply a sequence of JPEG images - each frame is independently decodable.
/// </remarks>
public static class MjpegHeaderParser
{
    /// <summary>MJPEG/JPEG start marker.</summary>
    private static readonly byte[] StartMarker = [0xFF, 0xD8];
    
    /// <summary>Common MJPEG start pattern (SOI + APP0).</summary>
    private static readonly byte[] MjpegStartPattern = [0xFF, 0xD8, 0xFF, 0xE0];
    
    /// <summary>
    /// Parses a JPEG frame header.
    /// </summary>
    /// <param name="data">Data containing the JPEG frame.</param>
    /// <param name="header">Parsed header on success.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseFrameHeader(ReadOnlySpan<byte> data, out JpegFrameHeader header)
    {
        header = default;
        
        if (data.Length < 4)
            return false;
        
        // Check SOI marker
        if (data[0] != 0xFF || data[1] != JpegMarkers.StartOfImage)
            return false;
        
        int width = 0;
        int height = 0;
        int bitsPerComponent = 8;
        int componentCount = 0;
        var encodingType = JpegEncodingType.Unknown;
        var subsampling = JpegSubsampling.Unknown;
        int sofOffset = -1;
        int sosOffset = -1;
        int eoiOffset = -1;
        
        // Parse markers
        int offset = 2;
        while (offset < data.Length - 1)
        {
            // Find next marker
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }
            
            // Skip fill bytes
            while (offset < data.Length && data[offset] == 0xFF)
            {
                offset++;
            }
            
            if (offset >= data.Length)
                break;
            
            byte marker = data[offset++];
            
            // Handle marker
            switch (marker)
            {
                case JpegMarkers.EndOfImage:
                    eoiOffset = offset - 2;
                    goto done;
                
                case JpegMarkers.StartOfFrameBaseline:
                case JpegMarkers.StartOfFrameExtended:
                case JpegMarkers.StartOfFrameProgressive:
                case JpegMarkers.StartOfFrameLossless:
                    if (offset + 8 > data.Length)
                        goto done;
                    
                    sofOffset = offset - 2;
                    encodingType = marker switch
                    {
                        JpegMarkers.StartOfFrameBaseline => JpegEncodingType.Baseline,
                        JpegMarkers.StartOfFrameExtended => JpegEncodingType.Extended,
                        JpegMarkers.StartOfFrameProgressive => JpegEncodingType.Progressive,
                        JpegMarkers.StartOfFrameLossless => JpegEncodingType.Lossless,
                        _ => JpegEncodingType.Unknown
                    };
                    
                    // SOF format:
                    // 2 bytes: length
                    // 1 byte: precision
                    // 2 bytes: height (big-endian)
                    // 2 bytes: width (big-endian)
                    // 1 byte: number of components
                    // Then per component: 1 byte ID, 1 byte sampling factors, 1 byte quant table
                    
                    int length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
                    bitsPerComponent = data[offset + 2];
                    height = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 3)..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..]);
                    componentCount = data[offset + 7];
                    
                    // Parse component sampling factors to determine subsampling
                    if (componentCount >= 1 && length >= 11)
                    {
                        // First component (Y) sampling factors
                        byte ySamplingFactors = data[offset + 9];
                        int hSampleY = (ySamplingFactors >> 4) & 0x0F;
                        int vSampleY = ySamplingFactors & 0x0F;
                        
                        if (componentCount == 1)
                        {
                            subsampling = JpegSubsampling.Grayscale;
                        }
                        else if (componentCount >= 3 && length >= 17)
                        {
                            // Cb and Cr sampling factors
                            byte cbSamplingFactors = data[offset + 12];
                            int hSampleCb = (cbSamplingFactors >> 4) & 0x0F;
                            int vSampleCb = cbSamplingFactors & 0x0F;
                            
                            // Determine subsampling based on relative factors
                            if (hSampleY == 1 && vSampleY == 1)
                            {
                                subsampling = JpegSubsampling.Yuv444;
                            }
                            else if (hSampleY == 2 && vSampleY == 1)
                            {
                                subsampling = JpegSubsampling.Yuv422;
                            }
                            else if (hSampleY == 2 && vSampleY == 2)
                            {
                                subsampling = JpegSubsampling.Yuv420;
                            }
                            else if (hSampleY == 1 && vSampleY == 2)
                            {
                                subsampling = JpegSubsampling.Yuv440;
                            }
                        }
                    }
                    
                    offset += length;
                    break;
                
                case JpegMarkers.StartOfScan:
                    sosOffset = offset - 2;
                    // After SOS, it's entropy-coded data - scan for EOI
                    offset = FindEndOfImage(data, offset);
                    break;
                
                case 0x00: // Escaped 0xFF byte in data
                    break;
                
                case 0xD0: // RST0
                case 0xD1:
                case 0xD2:
                case 0xD3:
                case 0xD4:
                case 0xD5:
                case 0xD6:
                case 0xD7: // RST7 - restart markers have no length
                    break;
                
                default:
                    // Most markers have a length field
                    if (offset + 2 > data.Length)
                        goto done;
                    
                    int markerLength = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
                    offset += markerLength;
                    break;
            }
        }
        
    done:
        // Find EOI if not found yet
        if (eoiOffset < 0)
        {
            eoiOffset = FindEndOfImage(data, 2) - 2;
        }
        
        header = new JpegFrameHeader
        {
            IsValid = width > 0 && height > 0,
            Width = width,
            Height = height,
            BitsPerComponent = bitsPerComponent,
            ComponentCount = componentCount,
            EncodingType = encodingType,
            Subsampling = subsampling,
            SofOffset = sofOffset,
            SosOffset = sosOffset,
            EoiOffset = eoiOffset
        };
        
        return header.IsValid;
    }
    
    /// <summary>
    /// Checks if the data starts with a valid JPEG/MJPEG frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsJpegFrame(ReadOnlySpan<byte> data)
    {
        return data.Length >= 2 &&
               data[0] == 0xFF &&
               data[1] == JpegMarkers.StartOfImage;
    }
    
    /// <summary>
    /// Checks if the data starts with an MJPEG frame (SOI + APP0).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMjpegFrame(ReadOnlySpan<byte> data)
    {
        return data.Length >= 4 &&
               data[0] == 0xFF &&
               data[1] == JpegMarkers.StartOfImage &&
               data[2] == 0xFF &&
               data[3] == JpegMarkers.App0;
    }
    
    /// <summary>
    /// Finds the next JPEG frame start in the data.
    /// </summary>
    /// <param name="data">Data to search.</param>
    /// <param name="offset">Start offset to search from.</param>
    /// <returns>Offset to the SOI marker, or -1 if not found.</returns>
    public static int FindFrameStart(ReadOnlySpan<byte> data, int offset = 0)
    {
        for (int i = offset; i < data.Length - 1; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == JpegMarkers.StartOfImage)
            {
                return i;
            }
        }
        
        return -1;
    }
    
    /// <summary>
    /// Finds the end of the current JPEG frame.
    /// </summary>
    /// <param name="data">Data to search.</param>
    /// <param name="offset">Start offset (after SOI).</param>
    /// <returns>Offset just after the EOI marker, or data.Length if not found.</returns>
    public static int FindEndOfImage(ReadOnlySpan<byte> data, int offset)
    {
        for (int i = offset; i < data.Length - 1; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == JpegMarkers.EndOfImage)
            {
                return i + 2;
            }
        }
        
        return data.Length;
    }
    
    /// <summary>
    /// Gets a human-readable description of the frame header.
    /// </summary>
    public static string GetDescription(in JpegFrameHeader header)
    {
        string encoding = header.EncodingType switch
        {
            JpegEncodingType.Baseline => "Baseline",
            JpegEncodingType.Progressive => "Progressive",
            JpegEncodingType.Extended => "Extended",
            JpegEncodingType.Lossless => "Lossless",
            _ => "Unknown"
        };
        
        string subsampling = header.Subsampling switch
        {
            JpegSubsampling.Yuv444 => "4:4:4",
            JpegSubsampling.Yuv422 => "4:2:2",
            JpegSubsampling.Yuv420 => "4:2:0",
            JpegSubsampling.Yuv440 => "4:4:0",
            JpegSubsampling.Grayscale => "Grayscale",
            _ => ""
        };
        
        return $"JPEG {encoding} {header.Width}x{header.Height} " +
               $"{header.BitsPerComponent}bit {subsampling}".TrimEnd();
    }
}

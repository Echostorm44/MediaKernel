// MediaKernel - Kate Overlay Codec Decoder
// Decodes Kate streams from Ogg containers for karaoke and subtitles
// Reference: libkate bitstream specification, VLC kate.c
//
// Kate supports:
// - Timed text/subtitles with styles and regions
// - Animated overlays with motion curves (karaoke highlighting)
// - Bitmap subtitles with palettes
// - Multiple text encodings and directionalities

using System.Buffers.Binary;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Text;

namespace MediaKernel.Core.Subtitles;

#region Enums

/// <summary>
/// Kate packet types (high bit set = header, cleared = data).
/// </summary>
public enum KatePacketType : byte
{
    // Headers (0x80-0x8F)
    IdHeader = 0x80,
    CommentHeader = 0x81,
    RegionsHeader = 0x82,
    StylesHeader = 0x83,
    CurvesHeader = 0x84,
    MotionsHeader = 0x85,
    PalettesHeader = 0x86,
    BitmapsHeader = 0x87,
    FontRangesHeader = 0x88,
    
    // Data (0x00-0x7F)
    TextData = 0x00,
    Keepalive = 0x01,
    Repeat = 0x02,
    EndOfStream = 0x7F
}

/// <summary>
/// Text encoding used by Kate streams.
/// </summary>
public enum KateTextEncoding : byte
{
    Utf8 = 0
}

/// <summary>
/// Text directionality.
/// </summary>
public enum KateTextDirectionality : byte
{
    LeftToRightTopToBottom = 0,
    RightToLeftTopToBottom = 1,
    TopToBottomRightToLeft = 2,
    TopToBottomLeftToRight = 3
}

/// <summary>
/// Kate curve types for motion animation.
/// </summary>
public enum KateCurveType : byte
{
    None = 0,
    StaticPoint = 1,
    LinearSegments = 2,
    CatmullRomSpline = 3,
    BezierCubicSpline = 4,
    BSpline = 5
}

/// <summary>
/// Motion semantics define what property a motion controls.
/// </summary>
public enum KateMotionSemantics : byte
{
    // 0-127 reserved for Kate
    ExternalTextPath = 1,
    InternalMarkerBitmapPosition = 2,
    TextAlignment = 3,
    TextPositionX = 4,
    TextPositionY = 5,
    TextSize = 6,
    MarkerPosition = 7,
    GlyphPointerXY = 8,
    GlyphPointerZ = 9,
    TextColorRG = 10,
    TextColorBA = 11,
    BackgroundColorRG = 12,
    BackgroundColorBA = 13,
    DrawColorRG = 14,
    DrawColorBA = 15,
    StyleMorph = 16,
    TextPathSection = 17,
    DrawWidth = 18,
    HorizontalMargins = 19,
    VerticalMargins = 20,
    BitmapPositionX = 21,
    BitmapPositionY = 22,
    BitmapSize = 23,
    MarkerBitmap = 24,
    GlyphPointer = 25
    // 128-255 for application use
}

/// <summary>
/// Space metric for region coordinates.
/// </summary>
public enum KateSpaceMetric : byte
{
    Pixels = 0,
    Percentage = 1,
    Millionths = 2
}

/// <summary>
/// Markup type for text interpretation.
/// </summary>
public enum KateMarkupType : byte
{
    None = 0,
    Simple = 1
}

#endregion

#region Data Structures

/// <summary>
/// Kate ID header information (64 bytes).
/// </summary>
public sealed class KateInfo
{
    /// <summary>Kate bitstream magic: 0x80 + "kate\0\0\0".</summary>
    public static readonly byte[] Magic = [0x80, 0x6B, 0x61, 0x74, 0x65, 0x00, 0x00, 0x00];
    
    public byte VersionMajor { get; init; }
    public byte VersionMinor { get; init; }
    public byte NumHeaders { get; init; }
    public KateTextEncoding TextEncoding { get; init; }
    public KateTextDirectionality Directionality { get; init; }
    public byte GranuleShift { get; init; }
    public int CanvasWidth { get; init; }
    public int CanvasHeight { get; init; }
    public uint GranuleRateNumerator { get; init; }
    public uint GranuleRateDenominator { get; init; }
    public string Language { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    
    /// <summary>Calculates granule rate (granules per second).</summary>
    public double GranuleRate => GranuleRateDenominator > 0 
        ? (double)GranuleRateNumerator / GranuleRateDenominator 
        : 1000.0;
}

/// <summary>
/// RGBA color structure.
/// </summary>
public readonly struct KateColor
{
    public readonly byte R;
    public readonly byte G;
    public readonly byte B;
    public readonly byte A;
    
    public KateColor(byte r, byte g, byte b, byte a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }
    
    public uint ToArgb() => ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;
    
    public static KateColor FromArgb(uint argb) => new(
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF),
        (byte)((argb >> 24) & 0xFF));
    
    public static KateColor White => new(255, 255, 255, 255);
    public static KateColor Black => new(0, 0, 0, 255);
    public static KateColor Transparent => new(0, 0, 0, 0);
}

/// <summary>
/// Kate style definition.
/// </summary>
public sealed class KateStyle
{
    public float HorizontalAlignment { get; set; }
    public float VerticalAlignment { get; set; }
    public float FontWidth { get; set; }
    public float FontHeight { get; set; }
    public float MarginLeft { get; set; }
    public float MarginTop { get; set; }
    public float MarginRight { get; set; }
    public float MarginBottom { get; set; }
    public KateColor TextColor { get; set; } = KateColor.White;
    public KateColor BackgroundColor { get; set; } = KateColor.Transparent;
    public KateColor DrawColor { get; set; } = KateColor.Black;
    public KateSpaceMetric FontMetric { get; set; }
    public KateSpaceMetric MarginMetric { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strike { get; set; }
    public bool Justify { get; set; }
    public string? FontName { get; set; }
}

/// <summary>
/// Kate region definition.
/// </summary>
public sealed class KateRegion
{
    public KateSpaceMetric Metric { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int DefaultStyleIndex { get; set; } = -1;
    public bool Clip { get; set; }
}

/// <summary>
/// Kate curve definition for motion animation.
/// </summary>
public sealed class KateCurve
{
    public KateCurveType Type { get; set; }
    public int NumPoints { get; set; }
    public float[] PointsX { get; set; } = [];
    public float[] PointsY { get; set; } = [];
}

/// <summary>
/// Kate motion definition for animated properties.
/// </summary>
public sealed class KateMotion
{
    public List<KateCurve> Curves { get; set; } = [];
    public float[] CurveDurations { get; set; } = [];
    public byte MappingX { get; set; }
    public byte MappingY { get; set; }
    public KateMotionSemantics Semantics { get; set; }
    public bool Periodic { get; set; }
}

/// <summary>
/// Kate palette for bitmap subtitles.
/// </summary>
public sealed class KatePalette
{
    public KateColor[] Colors { get; set; } = [];
}

/// <summary>
/// Kate bitmap for image-based subtitles.
/// </summary>
public sealed class KateBitmap
{
    public int Width { get; set; }
    public int Height { get; set; }
    public byte Bpp { get; set; }
    public byte[] Pixels { get; set; } = [];
    public int PaletteIndex { get; set; } = -1;
}

/// <summary>
/// A decoded Kate text event.
/// </summary>
public sealed class KateEvent
{
    /// <summary>Event start time in microseconds.</summary>
    public long StartTimeMicroseconds { get; set; }
    
    /// <summary>Event end time in microseconds.</summary>
    public long EndTimeMicroseconds { get; set; }
    
    /// <summary>Event duration in microseconds.</summary>
    public long DurationMicroseconds => EndTimeMicroseconds - StartTimeMicroseconds;
    
    /// <summary>Text content (UTF-8).</summary>
    public string Text { get; set; } = string.Empty;
    
    /// <summary>Text encoding override.</summary>
    public KateTextEncoding TextEncoding { get; set; }
    
    /// <summary>Text markup type.</summary>
    public KateMarkupType MarkupType { get; set; }
    
    /// <summary>Language override.</summary>
    public string? Language { get; set; }
    
    /// <summary>Region index (-1 if none).</summary>
    public int RegionIndex { get; set; } = -1;
    
    /// <summary>Inline region definition.</summary>
    public KateRegion? Region { get; set; }
    
    /// <summary>Style index (-1 if none).</summary>
    public int StyleIndex { get; set; } = -1;
    
    /// <summary>Inline style definition.</summary>
    public KateStyle? Style { get; set; }
    
    /// <summary>Secondary style index (-1 if none).</summary>
    public int SecondaryStyleIndex { get; set; } = -1;
    
    /// <summary>Inline secondary style.</summary>
    public KateStyle? SecondaryStyle { get; set; }
    
    /// <summary>Attached motions for animation.</summary>
    public List<KateMotion>? Motions { get; set; }
    
    /// <summary>Bitmap for image-based subtitles.</summary>
    public KateBitmap? Bitmap { get; set; }
    
    /// <summary>Palette index for bitmap.</summary>
    public int PaletteIndex { get; set; } = -1;
    
    /// <summary>Event ID (optional).</summary>
    public int? Id { get; set; }
}

#endregion

#region Bit Reader

/// <summary>
/// Bit-level reader for Kate bitstream format.
/// Reads little-endian bytes, bits from LSB to MSB within each byte.
/// </summary>
public ref struct KateBitReader
{
    private readonly ReadOnlySpan<byte> data;
    private int bytePosition;
    private int bitPosition;
    
    public KateBitReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        bytePosition = 0;
        bitPosition = 0;
    }
    
    public int BytePosition => bytePosition;
    public int BitPosition => bitPosition;
    public int BitsRemaining => (data.Length - bytePosition) * 8 - bitPosition;
    public bool HasMore => bytePosition < data.Length || bitPosition > 0;
    
    /// <summary>
    /// Reads a single bit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit()
    {
        if (bytePosition >= data.Length)
            return 0;
        
        int bit = (data[bytePosition] >> bitPosition) & 1;
        bitPosition++;
        if (bitPosition >= 8)
        {
            bitPosition = 0;
            bytePosition++;
        }
        return bit;
    }
    
    /// <summary>
    /// Reads multiple bits (up to 32).
    /// </summary>
    public uint ReadBits(int count)
    {
        uint result = 0;
        for (int i = 0; i < count; i++)
        {
            result |= (uint)ReadBit() << i;
        }
        return result;
    }
    
    /// <summary>
    /// Reads a byte (8 bits).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        if (bitPosition == 0 && bytePosition < data.Length)
        {
            return data[bytePosition++];
        }
        return (byte)ReadBits(8);
    }
    
    /// <summary>
    /// Reads a 32-bit little-endian unsigned integer.
    /// </summary>
    public uint ReadUInt32()
    {
        if (bitPosition == 0 && bytePosition + 4 <= data.Length)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(bytePosition, 4));
            bytePosition += 4;
            return value;
        }
        return (uint)(ReadByte() | (ReadByte() << 8) | (ReadByte() << 16) | (ReadByte() << 24));
    }
    
    /// <summary>
    /// Reads a 64-bit little-endian unsigned integer.
    /// </summary>
    public ulong ReadUInt64()
    {
        if (bitPosition == 0 && bytePosition + 8 <= data.Length)
        {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(bytePosition, 8));
            bytePosition += 8;
            return value;
        }
        return ReadUInt32() | ((ulong)ReadUInt32() << 32);
    }
    
    /// <summary>
    /// Reads a Kate variable-length integer (32v format).
    /// Values 0-14 are stored as 4 bits. Others use extended format.
    /// </summary>
    public int ReadVariableInt()
    {
        uint nibble = ReadBits(4);
        if (nibble < 15)
            return (int)nibble;
        
        // Extended format: sign (1 bit) + bit count - 1 (5 bits) + value
        int negative = ReadBit();
        int numBits = (int)ReadBits(5) + 1;
        int value = (int)ReadBits(numBits);
        
        return negative == 1 ? -value : value;
    }
    
    /// <summary>
    /// Reads a Kate floating-point value (custom format).
    /// </summary>
    public float ReadFloat()
    {
        // Kate uses a simple format: sign + mantissa bits + exponent
        // Read the mantissa and sign
        int sign = ReadBit();
        int mantissaBits = (int)ReadBits(5);
        
        if (mantissaBits == 0)
            return 0.0f;
        
        // Read mantissa (without leading 1)
        uint mantissa = ReadBits(mantissaBits);
        // Add implicit leading 1
        mantissa |= 1u << mantissaBits;
        
        // Read exponent
        int exponentSign = ReadBit();
        int exponentBits = (int)ReadBits(4);
        int exponent = 0;
        if (exponentBits > 0)
        {
            exponent = (int)ReadBits(exponentBits);
            if (exponentSign == 1)
                exponent = -exponent;
        }
        
        // Combine
        float value = mantissa * MathF.Pow(2, exponent - mantissaBits);
        return sign == 1 ? -value : value;
    }
    
    /// <summary>
    /// Reads a UTF-8 string with variable-length prefix.
    /// </summary>
    public string ReadString()
    {
        int length = ReadVariableInt();
        if (length <= 0)
            return string.Empty;
        
        // Align to byte boundary if possible
        AlignToByte();
        
        if (bytePosition + length <= data.Length)
        {
            string result = Encoding.UTF8.GetString(data.Slice(bytePosition, length));
            bytePosition += length;
            return result;
        }
        
        // Fallback: read byte by byte
        Span<byte> buffer = length <= 256 ? stackalloc byte[length] : new byte[length];
        for (int i = 0; i < length; i++)
            buffer[i] = ReadByte();
        return Encoding.UTF8.GetString(buffer);
    }
    
    /// <summary>
    /// Reads bytes into a span.
    /// </summary>
    public void ReadBytes(Span<byte> destination)
    {
        AlignToByte();
        if (bytePosition + destination.Length <= data.Length)
        {
            data.Slice(bytePosition, destination.Length).CopyTo(destination);
            bytePosition += destination.Length;
        }
        else
        {
            for (int i = 0; i < destination.Length; i++)
                destination[i] = ReadByte();
        }
    }
    
    /// <summary>
    /// Reads a Kate color (4 bytes RGBA).
    /// </summary>
    public KateColor ReadColor()
    {
        byte r = ReadByte();
        byte g = ReadByte();
        byte b = ReadByte();
        byte a = ReadByte();
        return new KateColor(r, g, b, a);
    }
    
    /// <summary>
    /// Aligns to the next byte boundary.
    /// </summary>
    public void AlignToByte()
    {
        if (bitPosition > 0)
        {
            bitPosition = 0;
            bytePosition++;
        }
    }
    
    /// <summary>
    /// Skips a warp (extensibility mechanism).
    /// Warps allow future format extensions while maintaining backward compatibility.
    /// </summary>
    public void SkipWarp()
    {
        // A warp is a chain of single warps, ended by an empty warp (0 bits)
        while (true)
        {
            int numBits = ReadVariableInt();
            if (numBits == 0)
                break;
            
            // Skip the warp data
            for (int i = 0; i < numBits; i++)
                ReadBit();
        }
    }
}

#endregion

#region Kate Decoder

/// <summary>
/// Decoder for Kate overlay codec streams.
/// </summary>
public sealed class KateDecoder
{
    private readonly List<KateStyle> styles = [];
    private readonly List<KateRegion> regions = [];
    private readonly List<KateCurve> curves = [];
    private readonly List<KateMotion> motions = [];
    private readonly List<KatePalette> palettes = [];
    private readonly List<KateBitmap> bitmaps = [];
    private readonly List<string> comments = [];
    
    private KateInfo? info;
    private int headersReceived;
    private bool isInitialized;
    
    /// <summary>Gets stream information (available after ID header is parsed).</summary>
    public KateInfo? Info => info;
    
    /// <summary>Gets whether all headers have been received.</summary>
    public bool IsReady => isInitialized;
    
    /// <summary>Gets defined styles.</summary>
    public IReadOnlyList<KateStyle> Styles => styles;
    
    /// <summary>Gets defined regions.</summary>
    public IReadOnlyList<KateRegion> Regions => regions;
    
    /// <summary>Gets Vorbis-style comments.</summary>
    public IReadOnlyList<string> Comments => comments;
    
    /// <summary>
    /// Probes data to check if it's a Kate stream.
    /// </summary>
    public static bool Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            return false;
        
        // Check for Kate magic: 0x80 + "kate\0\0\0"
        return data[0] == 0x80 &&
               data[1] == 'k' &&
               data[2] == 'a' &&
               data[3] == 't' &&
               data[4] == 'e' &&
               data[5] == 0 &&
               data[6] == 0 &&
               data[7] == 0;
    }
    
    /// <summary>
    /// Decodes a Kate packet.
    /// </summary>
    /// <returns>A KateEvent if the packet contains displayable content, null otherwise.</returns>
    public KateEvent? DecodePacket(ReadOnlySpan<byte> data, long granulePosition = 0)
    {
        if (data.Length == 0)
            return null;
        
        byte packetType = data[0];
        
        // Header packets have MSB set
        if ((packetType & 0x80) != 0)
        {
            DecodeHeader(data);
            return null;
        }
        
        // Data packets require headers first
        if (!isInitialized)
            return null;
        
        return (KatePacketType)packetType switch
        {
            KatePacketType.TextData => DecodeTextData(data, granulePosition),
            KatePacketType.Keepalive => null,
            KatePacketType.Repeat => null, // TODO: handle repeats
            KatePacketType.EndOfStream => null,
            _ => null
        };
    }
    
    /// <summary>
    /// Converts a Kate event to a subtitle cue.
    /// </summary>
    public SubtitleCue ToSubtitleCue(KateEvent ev)
    {
        // Determine position from region/style
        var position = SubtitlePosition.Default;
        if (ev.Region != null)
        {
            float hPct = ev.Region.Metric == KateSpaceMetric.Percentage 
                ? ev.Region.X 
                : (info?.CanvasWidth > 0 ? (float)ev.Region.X / info.CanvasWidth * 100 : 50);
            float vPct = ev.Region.Metric == KateSpaceMetric.Percentage 
                ? ev.Region.Y 
                : (info?.CanvasHeight > 0 ? (float)ev.Region.Y / info.CanvasHeight * 100 : 90);
            position = new SubtitlePosition(hPct, vPct, SubtitleAlignment.Center, SubtitleVerticalAlignment.Bottom);
        }
        else if (ev.RegionIndex >= 0 && ev.RegionIndex < regions.Count)
        {
            var region = regions[ev.RegionIndex];
            float hPct = region.Metric == KateSpaceMetric.Percentage 
                ? region.X 
                : (info?.CanvasWidth > 0 ? (float)region.X / info.CanvasWidth * 100 : 50);
            float vPct = region.Metric == KateSpaceMetric.Percentage 
                ? region.Y 
                : (info?.CanvasHeight > 0 ? (float)region.Y / info.CanvasHeight * 100 : 90);
            position = new SubtitlePosition(hPct, vPct, SubtitleAlignment.Center, SubtitleVerticalAlignment.Bottom);
        }
        
        // Determine subtitle type
        var subType = ev.Bitmap != null ? SubtitleType.Bitmap : 
                     (ev.Motions?.Count > 0 ? SubtitleType.StyledText : SubtitleType.Text);
        
        return new SubtitleCue(
            ev.StartTimeMicroseconds,
            ev.EndTimeMicroseconds,
            ev.Text,
            subType,
            null,
            position,
            ev.Bitmap != null ? ConvertBitmap(ev) : null);
    }
    
    /// <summary>
    /// Converts all decoded events to a subtitle track.
    /// </summary>
    public SubtitleTrack ToSubtitleTrack(IEnumerable<KateEvent> events)
    {
        var track = new SubtitleTrack
        {
            Format = SubtitleFormat.Kate,
            Language = info?.Language ?? string.Empty,
            Title = info?.Category ?? string.Empty,
            PlayResolution = (info?.CanvasWidth ?? 0, info?.CanvasHeight ?? 0)
        };
        
        foreach (var ev in events)
        {
            track.AddCue(ToSubtitleCue(ev));
        }
        
        return track;
    }
    
    private void DecodeHeader(ReadOnlySpan<byte> data)
    {
        var packetType = (KatePacketType)data[0];
        
        // Verify Kate magic for headers
        if (data.Length < 8)
            return;
        
        // Check magic: "kate\0\0\0" at bytes 1-7
        if (data[1] != 'k' || data[2] != 'a' || data[3] != 't' || data[4] != 'e' ||
            data[5] != 0 || data[6] != 0 || data[7] != 0)
            return;
        
        switch (packetType)
        {
            case KatePacketType.IdHeader:
                DecodeIdHeader(data);
                break;
            case KatePacketType.CommentHeader:
                DecodeCommentHeader(data);
                break;
            case KatePacketType.RegionsHeader:
                DecodeRegionsHeader(data);
                break;
            case KatePacketType.StylesHeader:
                DecodeStylesHeader(data);
                break;
            case KatePacketType.CurvesHeader:
                DecodeCurvesHeader(data);
                break;
            case KatePacketType.MotionsHeader:
                DecodeMotionsHeader(data);
                break;
            case KatePacketType.PalettesHeader:
                DecodePalettesHeader(data);
                break;
            case KatePacketType.BitmapsHeader:
                DecodeBitmapsHeader(data);
                break;
            case KatePacketType.FontRangesHeader:
                // Font ranges not commonly used, skip for now
                break;
        }
        
        headersReceived++;
        if (info != null && headersReceived >= info.NumHeaders)
            isInitialized = true;
    }
    
    private void DecodeIdHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 64)
            return;
        
        // ID header is 64 bytes, fixed format
        info = new KateInfo
        {
            VersionMajor = data[9],
            VersionMinor = data[10],
            NumHeaders = data[11],
            TextEncoding = (KateTextEncoding)data[12],
            Directionality = (KateTextDirectionality)data[13],
            GranuleShift = data[15],
            CanvasWidth = DecodeCanvas(data[16], data[17]),
            CanvasHeight = DecodeCanvas(data[18], data[19]),
            GranuleRateNumerator = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(24, 4)),
            GranuleRateDenominator = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(28, 4)),
            Language = ReadNullTerminatedString(data.Slice(32, 16)),
            Category = ReadNullTerminatedString(data.Slice(48, 16))
        };
    }
    
    private static int DecodeCanvas(byte shiftAndLow, byte high)
    {
        // Canvas size: 4-bit shift in high nibble of first byte,
        // 12-bit value in low nibble of first byte + second byte
        int shift = (shiftAndLow >> 4) & 0x0F;
        int value = ((shiftAndLow & 0x0F) << 8) | high;
        return value << shift;
    }
    
    private void DecodeCommentHeader(ReadOnlySpan<byte> data)
    {
        // Skip packet type + magic (8 bytes)
        if (data.Length < 8)
            return;
            
        var remaining = data[8..];
        int offset = 0;
        
        // Vorbis-style comments use 32-bit little-endian lengths
        if (remaining.Length < offset + 4)
            return;
        int vendorLength = BinaryPrimitives.ReadInt32LittleEndian(remaining[offset..]);
        offset += 4;
        
        if (vendorLength > 0 && vendorLength <= remaining.Length - offset)
        {
            // Skip vendor string
            offset += vendorLength;
        }
        
        if (remaining.Length < offset + 4)
            return;
        int numComments = BinaryPrimitives.ReadInt32LittleEndian(remaining[offset..]);
        offset += 4;
        
        for (int i = 0; i < numComments; i++)
        {
            if (remaining.Length < offset + 4)
                break;
            int length = BinaryPrimitives.ReadInt32LittleEndian(remaining[offset..]);
            offset += 4;
            
            if (length > 0 && length <= remaining.Length - offset)
            {
                comments.Add(Encoding.UTF8.GetString(remaining.Slice(offset, length)));
                offset += length;
            }
        }
    }
    
    private void DecodeRegionsHeader(ReadOnlySpan<byte> data)
    {
        var reader = new KateBitReader(data[8..]);
        int numRegions = reader.ReadVariableInt();
        
        for (int i = 0; i < numRegions && reader.BitsRemaining > 0; i++)
        {
            var region = new KateRegion
            {
                Metric = (KateSpaceMetric)reader.ReadByte(),
                X = reader.ReadVariableInt(),
                Y = reader.ReadVariableInt(),
                Width = reader.ReadVariableInt(),
                Height = reader.ReadVariableInt(),
                DefaultStyleIndex = reader.ReadVariableInt()
            };
            
            // Read warp (extensibility)
            ReadRegionWarp(ref reader, region);
            
            regions.Add(region);
        }
    }
    
    private static void ReadRegionWarp(ref KateBitReader reader, KateRegion region)
    {
        // Check for warp content
        int warpBits = reader.ReadVariableInt();
        if (warpBits > 0)
        {
            // Version 0.2+: clip flag
            region.Clip = reader.ReadBit() == 1;
            
            // Skip remaining warp bits
            for (int i = 1; i < warpBits; i++)
                reader.ReadBit();
            
            // Chain to next warp
            reader.SkipWarp();
        }
    }
    
    private void DecodeStylesHeader(ReadOnlySpan<byte> data)
    {
        var reader = new KateBitReader(data[8..]);
        int numStyles = reader.ReadVariableInt();
        
        for (int i = 0; i < numStyles && reader.BitsRemaining > 0; i++)
        {
            var style = new KateStyle
            {
                HorizontalAlignment = reader.ReadFloat(),
                VerticalAlignment = reader.ReadFloat(),
                FontWidth = reader.ReadFloat(),
                FontHeight = reader.ReadFloat(),
                MarginLeft = reader.ReadFloat(),
                MarginTop = reader.ReadFloat(),
                MarginRight = reader.ReadFloat(),
                MarginBottom = reader.ReadFloat(),
                TextColor = reader.ReadColor(),
                BackgroundColor = reader.ReadColor(),
                DrawColor = reader.ReadColor(),
                FontMetric = (KateSpaceMetric)reader.ReadByte(),
                MarginMetric = (KateSpaceMetric)reader.ReadByte(),
                Bold = reader.ReadBit() == 1,
                Italic = reader.ReadBit() == 1,
                Underline = reader.ReadBit() == 1,
                Strike = reader.ReadBit() == 1
            };
            
            // Read warp (extensibility)
            ReadStyleWarp(ref reader, style);
            
            styles.Add(style);
        }
    }
    
    private static void ReadStyleWarp(ref KateBitReader reader, KateStyle style)
    {
        int warpBits = reader.ReadVariableInt();
        if (warpBits > 0)
        {
            int bitsRead = 0;
            
            // Version 0.2+: justify flag and font name
            style.Justify = reader.ReadBit() == 1;
            bitsRead++;
            
            int fontNameLen = reader.ReadVariableInt();
            // Count bits used by variable int (rough estimate)
            bitsRead += fontNameLen < 15 ? 4 : 15;
            
            if (fontNameLen > 0 && fontNameLen < 1000)
            {
                var buffer = new byte[fontNameLen];
                reader.ReadBytes(buffer);
                style.FontName = Encoding.UTF8.GetString(buffer);
            }
            
            // Skip remaining warp bits
            reader.SkipWarp();
        }
    }
    
    private void DecodeCurvesHeader(ReadOnlySpan<byte> data)
    {
        var reader = new KateBitReader(data[8..]);
        int numCurves = reader.ReadVariableInt();
        
        for (int i = 0; i < numCurves && reader.BitsRemaining > 0; i++)
        {
            var curve = ReadCurve(ref reader);
            curves.Add(curve);
        }
    }
    
    private static KateCurve ReadCurve(ref KateBitReader reader)
    {
        var curve = new KateCurve
        {
            Type = (KateCurveType)reader.ReadByte(),
            NumPoints = reader.ReadVariableInt()
        };
        
        // Skip warp
        reader.SkipWarp();
        
        // Read control points
        if (curve.NumPoints > 0)
        {
            curve.PointsX = new float[curve.NumPoints];
            curve.PointsY = new float[curve.NumPoints];
            
            for (int j = 0; j < curve.NumPoints; j++)
                curve.PointsX[j] = reader.ReadFloat();
            
            for (int j = 0; j < curve.NumPoints; j++)
                curve.PointsY[j] = reader.ReadFloat();
        }
        
        return curve;
    }
    
    private void DecodeMotionsHeader(ReadOnlySpan<byte> data)
    {
        var reader = new KateBitReader(data[8..]);
        int numMotions = reader.ReadVariableInt();
        
        for (int i = 0; i < numMotions && reader.BitsRemaining > 0; i++)
        {
            var motion = ReadMotion(ref reader);
            motions.Add(motion);
        }
    }
    
    private KateMotion ReadMotion(ref KateBitReader reader)
    {
        var motion = new KateMotion();
        int numCurves = reader.ReadVariableInt();
        
        for (int i = 0; i < numCurves; i++)
        {
            bool isPredefined = reader.ReadBit() == 1;
            if (isPredefined)
            {
                int curveIndex = reader.ReadVariableInt();
                if (curveIndex >= 0 && curveIndex < curves.Count)
                    motion.Curves.Add(curves[curveIndex]);
            }
            else
            {
                motion.Curves.Add(ReadCurve(ref reader));
            }
        }
        
        // Read curve durations
        motion.CurveDurations = new float[numCurves];
        for (int i = 0; i < numCurves; i++)
            motion.CurveDurations[i] = reader.ReadFloat();
        
        motion.MappingX = reader.ReadByte();
        motion.MappingY = reader.ReadByte();
        motion.Semantics = (KateMotionSemantics)reader.ReadByte();
        motion.Periodic = reader.ReadBit() == 1;
        
        // Skip warp
        reader.SkipWarp();
        
        return motion;
    }
    
    private void DecodePalettesHeader(ReadOnlySpan<byte> data)
    {
        var reader = new KateBitReader(data[8..]);
        int numPalettes = reader.ReadVariableInt();
        
        for (int i = 0; i < numPalettes && reader.BitsRemaining > 0; i++)
        {
            int numColors = reader.ReadVariableInt();
            var palette = new KatePalette
            {
                Colors = new KateColor[numColors]
            };
            
            for (int j = 0; j < numColors; j++)
                palette.Colors[j] = reader.ReadColor();
            
            palettes.Add(palette);
        }
    }
    
    private void DecodeBitmapsHeader(ReadOnlySpan<byte> data)
    {
        var reader = new KateBitReader(data[8..]);
        int numBitmaps = reader.ReadVariableInt();
        
        for (int i = 0; i < numBitmaps && reader.BitsRemaining > 0; i++)
        {
            var bitmap = ReadBitmap(ref reader);
            bitmaps.Add(bitmap);
        }
    }
    
    private static KateBitmap ReadBitmap(ref KateBitReader reader)
    {
        var bitmap = new KateBitmap
        {
            Width = reader.ReadVariableInt(),
            Height = reader.ReadVariableInt(),
            Bpp = reader.ReadByte()
        };
        
        int pixelCount = bitmap.Width * bitmap.Height;
        if (pixelCount > 0 && pixelCount < 4096 * 4096) // Reasonable limit
        {
            // Calculate bytes needed based on bpp
            int bytesPerPixel = (bitmap.Bpp + 7) / 8;
            int byteCount = pixelCount * bytesPerPixel;
            
            bitmap.Pixels = new byte[byteCount];
            reader.ReadBytes(bitmap.Pixels);
        }
        
        bitmap.PaletteIndex = reader.ReadVariableInt();
        
        // Skip warp
        reader.SkipWarp();
        
        return bitmap;
    }
    
    private KateEvent? DecodeTextData(ReadOnlySpan<byte> data, long granulePosition)
    {
        if (data.Length < 25) // Minimum: type + times + text length
            return null;
        
        var reader = new KateBitReader(data[1..]); // Skip packet type
        
        // Read timing (64-bit values at granule rate)
        ulong startTime = reader.ReadUInt64();
        ulong duration = reader.ReadUInt64();
        ulong backlink = reader.ReadUInt64(); // Earliest active event start
        
        // Convert to microseconds
        double granuleRate = info?.GranuleRate ?? 1000.0;
        long startUs = (long)(startTime * 1_000_000.0 / granuleRate);
        long endUs = (long)((startTime + duration) * 1_000_000.0 / granuleRate);
        
        // Read text
        int textLength = (int)reader.ReadUInt32();
        string text = string.Empty;
        if (textLength > 0 && textLength < 100000)
        {
            var textBuffer = new byte[textLength];
            reader.ReadBytes(textBuffer);
            text = Encoding.UTF8.GetString(textBuffer);
        }
        
        var ev = new KateEvent
        {
            StartTimeMicroseconds = startUs,
            EndTimeMicroseconds = endUs,
            Text = text,
            TextEncoding = info?.TextEncoding ?? KateTextEncoding.Utf8
        };
        
        // Read optional ID
        if (reader.ReadBit() == 1)
            ev.Id = reader.ReadVariableInt();
        
        // Read motions
        if (reader.ReadBit() == 1)
        {
            int numMotions = reader.ReadVariableInt();
            ev.Motions = new List<KateMotion>(numMotions);
            
            for (int i = 0; i < numMotions && reader.BitsRemaining > 0; i++)
            {
                bool isPredefined = reader.ReadBit() == 1;
                if (isPredefined)
                {
                    int motionIndex = reader.ReadVariableInt();
                    if (motionIndex >= 0 && motionIndex < motions.Count)
                        ev.Motions.Add(motions[motionIndex]);
                }
                else
                {
                    ev.Motions.Add(ReadMotion(ref reader));
                }
            }
        }
        
        // Read overrides
        if (reader.ReadBit() == 1)
        {
            ReadEventOverrides(ref reader, ev);
        }
        
        return ev;
    }
    
    private void ReadEventOverrides(ref KateBitReader reader, KateEvent ev)
    {
        // Text encoding override
        if (reader.ReadBit() == 1)
            ev.TextEncoding = (KateTextEncoding)reader.ReadByte();
        
        // Directionality override (skip for now)
        if (reader.ReadBit() == 1)
            reader.ReadByte();
        
        // Language override
        if (reader.ReadBit() == 1)
            ev.Language = reader.ReadString();
        
        // Predefined region
        if (reader.ReadBit() == 1)
            ev.RegionIndex = reader.ReadVariableInt();
        
        // Inline region
        if (reader.ReadBit() == 1)
        {
            ev.Region = new KateRegion
            {
                Metric = (KateSpaceMetric)reader.ReadByte(),
                X = reader.ReadVariableInt(),
                Y = reader.ReadVariableInt(),
                Width = reader.ReadVariableInt(),
                Height = reader.ReadVariableInt(),
                DefaultStyleIndex = reader.ReadVariableInt()
            };
            ReadRegionWarp(ref reader, ev.Region);
        }
        
        // Predefined style
        if (reader.ReadBit() == 1)
            ev.StyleIndex = reader.ReadVariableInt();
        
        // Inline style
        if (reader.ReadBit() == 1)
        {
            ev.Style = ReadInlineStyle(ref reader);
        }
        
        // Predefined secondary style
        if (reader.ReadBit() == 1)
            ev.SecondaryStyleIndex = reader.ReadVariableInt();
        
        // Inline secondary style
        if (reader.ReadBit() == 1)
        {
            ev.SecondaryStyle = ReadInlineStyle(ref reader);
        }
        
        // Font mapping index (skip)
        if (reader.ReadBit() == 1)
            reader.ReadVariableInt();
        
        // Read warp for additional overrides
        ReadEventWarp(ref reader, ev);
    }
    
    private static KateStyle ReadInlineStyle(ref KateBitReader reader)
    {
        var style = new KateStyle
        {
            HorizontalAlignment = reader.ReadFloat(),
            VerticalAlignment = reader.ReadFloat(),
            FontWidth = reader.ReadFloat(),
            FontHeight = reader.ReadFloat(),
            MarginLeft = reader.ReadFloat(),
            MarginTop = reader.ReadFloat(),
            MarginRight = reader.ReadFloat(),
            MarginBottom = reader.ReadFloat(),
            TextColor = reader.ReadColor(),
            BackgroundColor = reader.ReadColor(),
            DrawColor = reader.ReadColor(),
            FontMetric = (KateSpaceMetric)reader.ReadByte(),
            MarginMetric = (KateSpaceMetric)reader.ReadByte(),
            Bold = reader.ReadBit() == 1,
            Italic = reader.ReadBit() == 1,
            Underline = reader.ReadBit() == 1,
            Strike = reader.ReadBit() == 1
        };
        
        ReadStyleWarp(ref reader, style);
        return style;
    }
    
    private void ReadEventWarp(ref KateBitReader reader, KateEvent ev)
    {
        int warpBits = reader.ReadVariableInt();
        if (warpBits == 0)
            return;
        
        // Version 0.2+: palette and bitmap
        if (reader.ReadBit() == 1)
            ev.PaletteIndex = reader.ReadVariableInt();
        
        // Inline palette (skip)
        if (reader.ReadBit() == 1)
        {
            int numColors = reader.ReadVariableInt();
            for (int i = 0; i < numColors; i++)
                reader.ReadColor();
        }
        
        // Predefined bitmap
        if (reader.ReadBit() == 1)
        {
            int bitmapIndex = reader.ReadVariableInt();
            if (bitmapIndex >= 0 && bitmapIndex < bitmaps.Count)
                ev.Bitmap = bitmaps[bitmapIndex];
        }
        
        // Inline bitmap
        if (reader.ReadBit() == 1)
        {
            ev.Bitmap = ReadBitmap(ref reader);
        }
        
        // Markup type
        if (reader.ReadBit() == 1)
            ev.MarkupType = (KateMarkupType)reader.ReadByte();
        
        // Skip remaining warp chain
        reader.SkipWarp();
    }
    
    private SubtitleBitmap? ConvertBitmap(KateEvent ev)
    {
        if (ev.Bitmap == null)
            return null;
        
        var kateBitmap = ev.Bitmap;
        
        // Get palette
        KatePalette? palette = null;
        if (kateBitmap.PaletteIndex >= 0 && kateBitmap.PaletteIndex < palettes.Count)
            palette = palettes[kateBitmap.PaletteIndex];
        else if (ev.PaletteIndex >= 0 && ev.PaletteIndex < palettes.Count)
            palette = palettes[ev.PaletteIndex];
        
        // Convert to RGBA
        int width = kateBitmap.Width;
        int height = kateBitmap.Height;
        var pixelData = new byte[width * height * 4];
        
        if (palette != null && kateBitmap.Bpp <= 8)
        {
            // Paletted bitmap
            for (int i = 0; i < kateBitmap.Pixels.Length && i < width * height; i++)
            {
                int colorIndex = kateBitmap.Pixels[i];
                var color = colorIndex < palette.Colors.Length 
                    ? palette.Colors[colorIndex] 
                    : KateColor.Transparent;
                
                int offset = i * 4;
                pixelData[offset] = color.R;
                pixelData[offset + 1] = color.G;
                pixelData[offset + 2] = color.B;
                pixelData[offset + 3] = color.A;
            }
        }
        else if (kateBitmap.Bpp == 32)
        {
            // Direct RGBA
            Array.Copy(kateBitmap.Pixels, pixelData, Math.Min(kateBitmap.Pixels.Length, pixelData.Length));
        }
        
        // Get position from region
        int posX = 0, posY = 0;
        if (ev.Region != null)
        {
            posX = ev.Region.X;
            posY = ev.Region.Y;
        }
        else if (ev.RegionIndex >= 0 && ev.RegionIndex < regions.Count)
        {
            posX = regions[ev.RegionIndex].X;
            posY = regions[ev.RegionIndex].Y;
        }
        
        return new SubtitleBitmap(width, height, posX, posY, pixelData);
    }
    
    private static string ReadNullTerminatedString(ReadOnlySpan<byte> data)
    {
        int length = 0;
        for (; length < data.Length && data[length] != 0; length++) { }
        return Encoding.ASCII.GetString(data[..length]);
    }
    
    /// <summary>
    /// Resets the decoder state.
    /// </summary>
    public void Reset()
    {
        styles.Clear();
        regions.Clear();
        curves.Clear();
        motions.Clear();
        palettes.Clear();
        bitmaps.Clear();
        comments.Clear();
        info = null;
        headersReceived = 0;
        isInitialized = false;
    }
}

#endregion

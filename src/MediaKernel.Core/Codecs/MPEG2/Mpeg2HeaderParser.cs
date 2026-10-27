// MPEG-2 video header parsing support
// Reference: ISO/IEC 13818-2 (MPEG-2 Video)

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Mpeg2;

/// <summary>
/// MPEG-2 start code values.
/// </summary>
public static class Mpeg2StartCodes
{
    public const byte Picture = 0x00;
    public const byte Slice1 = 0x01;
    public const byte SliceLast = 0xAF;
    public const byte UserData = 0xB2;
    public const byte Sequence = 0xB3;
    public const byte SequenceError = 0xB4;
    public const byte Extension = 0xB5;
    public const byte SequenceEnd = 0xB7;
    public const byte GroupOfPictures = 0xB8;
    public const byte SystemStart = 0xB9;
    public const byte PackStart = 0xBA;
    public const byte ProgramStreamMap = 0xBC;
    
    /// <summary>3-byte start code prefix (Annex B).</summary>
    public const int StartCodePrefix = 0x000001;
}

/// <summary>
/// MPEG-2 picture coding types.
/// </summary>
public enum Mpeg2PictureType : byte
{
    Reserved = 0,
    Intra = 1,        // I-frame (self-contained)
    Predictive = 2,   // P-frame (forward prediction)
    Bidirectional = 3 // B-frame (bidirectional prediction)
}

/// <summary>
/// MPEG-2 aspect ratio codes.
/// </summary>
public enum Mpeg2AspectRatio : byte
{
    Reserved = 0,
    Square = 1,       // 1:1 (SAR) - square pixels
    FourThree = 2,    // 4:3 display aspect ratio
    SixteenNine = 3,  // 16:9 display aspect ratio
    TwoTwoOne = 4     // 2.21:1 display aspect ratio
}

/// <summary>
/// MPEG-2 profile codes.
/// </summary>
public enum Mpeg2Profile : byte
{
    Reserved = 0,
    High = 1,
    SpatiallyScalable = 2,
    SnrScalable = 3,
    Main = 4,
    Simple = 5,
    FourTwoTwo = 6,
    MultiView = 7
}

/// <summary>
/// MPEG-2 level codes.
/// </summary>
public enum Mpeg2Level : byte
{
    Reserved = 0,
    High = 4,
    High1440 = 6,
    Main = 8,
    Low = 10
}

/// <summary>
/// MPEG-2 picture structure (frame/field).
/// </summary>
public enum Mpeg2PictureStructure : byte
{
    Reserved = 0,
    TopField = 1,
    BottomField = 2,
    FramePicture = 3
}

/// <summary>
/// MPEG-2 chroma format.
/// </summary>
public enum Mpeg2ChromaFormat : byte
{
    Reserved = 0,
    YUV420 = 1,
    YUV422 = 2,
    YUV444 = 3
}

/// <summary>
/// MPEG-2 sequence header information.
/// </summary>
public readonly struct Mpeg2SequenceHeader
{
    /// <summary>Horizontal resolution in pixels.</summary>
    public int Width { get; init; }
    
    /// <summary>Vertical resolution in pixels.</summary>
    public int Height { get; init; }
    
    /// <summary>Aspect ratio code.</summary>
    public Mpeg2AspectRatio AspectRatio { get; init; }
    
    /// <summary>Frame rate in frames per second.</summary>
    public double FrameRate { get; init; }
    
    /// <summary>Frame rate numerator.</summary>
    public int FrameRateNumerator { get; init; }
    
    /// <summary>Frame rate denominator.</summary>
    public int FrameRateDenominator { get; init; }
    
    /// <summary>Bitrate in bits per second (0 = variable).</summary>
    public int Bitrate { get; init; }
    
    /// <summary>Whether a profile/level extension was found.</summary>
    public bool HasExtension { get; init; }
    
    /// <summary>MPEG-2 profile (from extension).</summary>
    public Mpeg2Profile Profile { get; init; }
    
    /// <summary>MPEG-2 level (from extension).</summary>
    public Mpeg2Level Level { get; init; }
    
    /// <summary>Whether progressive scan (non-interlaced).</summary>
    public bool IsProgressive { get; init; }
    
    /// <summary>Chroma subsampling format.</summary>
    public Mpeg2ChromaFormat ChromaFormat { get; init; }
    
    /// <summary>Whether this is low-delay mode.</summary>
    public bool IsLowDelay { get; init; }
    
    /// <summary>Frame duration in microseconds.</summary>
    public long FrameDurationMicroseconds => FrameRateDenominator > 0 
        ? (long)(FrameRateDenominator * 1_000_000.0 / FrameRateNumerator) 
        : 0;
}

/// <summary>
/// MPEG-2 picture header information.
/// </summary>
public readonly struct Mpeg2PictureHeader
{
    /// <summary>Temporal reference (0-1023 modulo counter).</summary>
    public int TemporalReference { get; init; }
    
    /// <summary>Picture type (I, P, B).</summary>
    public Mpeg2PictureType PictureType { get; init; }
    
    /// <summary>VBV delay value (0xFFFF = variable rate).</summary>
    public int VbvDelay { get; init; }
    
    /// <summary>Picture structure (frame or field).</summary>
    public Mpeg2PictureStructure PictureStructure { get; init; }
    
    /// <summary>Top field first flag (for interlaced).</summary>
    public bool TopFieldFirst { get; init; }
    
    /// <summary>Repeat first field flag.</summary>
    public bool RepeatFirstField { get; init; }
    
    /// <summary>Progressive frame flag.</summary>
    public bool ProgressiveFrame { get; init; }
}

/// <summary>
/// MPEG-2 GOP (Group of Pictures) header information.
/// </summary>
public readonly struct Mpeg2GopHeader
{
    /// <summary>Timecode hours.</summary>
    public int Hours { get; init; }
    
    /// <summary>Timecode minutes.</summary>
    public int Minutes { get; init; }
    
    /// <summary>Timecode seconds.</summary>
    public int Seconds { get; init; }
    
    /// <summary>Timecode frames.</summary>
    public int Frames { get; init; }
    
    /// <summary>Drop frame flag (for 29.97 fps timecode).</summary>
    public bool DropFrame { get; init; }
    
    /// <summary>Closed GOP flag (GOP can be decoded independently).</summary>
    public bool ClosedGop { get; init; }
    
    /// <summary>Broken link flag (first B-frame references missing pictures).</summary>
    public bool BrokenLink { get; init; }
}

/// <summary>
/// MPEG-2 video header parser.
/// </summary>
/// <remarks>
/// Parses MPEG-2 video elementary stream headers for demuxer
/// and seeking support. Full decoding requires IDCT, motion compensation.
/// </remarks>
public static class Mpeg2HeaderParser
{
    
    /// <summary>
    /// Finds the next start code in the data.
    /// </summary>
    /// <param name="data">Data to search.</param>
    /// <param name="startCode">The start code value found.</param>
    /// <returns>Offset to the start code prefix (0x000001), or -1 if not found.</returns>
    public static int FindStartCode(ReadOnlySpan<byte> data, out byte startCode)
    {
        startCode = 0;
        
        for (int i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                startCode = data[i + 3];
                return i;
            }
        }
        
        return -1;
    }
    
    /// <summary>
    /// Checks if the data starts with a valid MPEG-2 sequence header.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSequenceHeader(ReadOnlySpan<byte> data)
    {
        return data.Length >= 4 &&
               data[0] == 0 && data[1] == 0 && data[2] == 1 &&
               data[3] == Mpeg2StartCodes.Sequence;
    }
    
    /// <summary>
    /// Parses an MPEG-2 sequence header.
    /// </summary>
    /// <param name="data">Data starting at sequence start code (0x000001B3).</param>
    /// <param name="header">Parsed header on success.</param>
    /// <param name="bytesConsumed">Bytes consumed including any extensions.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseSequenceHeader(ReadOnlySpan<byte> data, out Mpeg2SequenceHeader header, out int bytesConsumed)
    {
        header = default;
        bytesConsumed = 0;
        
        // Minimum: start code prefix (3) + code (1) + header (8) = 12 bytes
        if (data.Length < 12)
            return false;
        
        // Verify start code
        if (data[0] != 0 || data[1] != 0 || data[2] != 1 || data[3] != Mpeg2StartCodes.Sequence)
            return false;
        
        // Bytes 4-5: Horizontal size (12 bits)
        int horizontalSize = (data[4] << 4) | (data[5] >> 4);
        
        // Bytes 5-6: Vertical size (12 bits)
        int verticalSize = ((data[5] & 0x0F) << 8) | data[6];
        
        // Byte 7: Aspect ratio (4 bits) + Frame rate code (4 bits)
        int aspectRatioCode = data[7] >> 4;
        int frameRateCode = data[7] & 0x0F;
        
        // Bytes 8-10: Bit rate (18 bits) + marker bit
        int bitRateValue = ((data[8] << 10) | (data[9] << 2) | (data[10] >> 6)) * 400;
        
        // Get frame rate. The table is the decoder's own ff_mpeg12_frame_rate_tab, not a second
        // copy: this parser used to carry its own, which disagreed with FFmpeg at code 13 (15 fps,
        // written here as reserved) and gave an unknown rate a denominator of 1 instead of 0, so
        // FrameDurationMicroseconds divided by zero rather than reporting that it did not know.
        var rate = Mpeg12Data.Mpeg12FrameRateTab[frameRateCode];
        (int frNum, int frDen) = (rate.Num, rate.Den);
        
        // Base header size
        bytesConsumed = 12;
        
        // Look for sequence extension (MPEG-2 only)
        bool hasExtension = false;
        var profile = Mpeg2Profile.Main;
        var level = Mpeg2Level.Main;
        bool progressive = true;
        var chromaFormat = Mpeg2ChromaFormat.YUV420;
        bool lowDelay = false;
        
        // Check for extension start code after sequence header
        // Extension starts after: quantizer matrix (64 bytes if load_intra_quantiser_matrix) + optional non-intra matrix
        // For simplicity, scan forward for extension start code
        int searchStart = 12;
        while (searchStart + 5 <= data.Length)
        {
            if (data[searchStart] == 0 && data[searchStart + 1] == 0 && 
                data[searchStart + 2] == 1 && data[searchStart + 3] == Mpeg2StartCodes.Extension)
            {
                // Extension ID is in bits 4-7 of byte 4 (after start code)
                int extId = data[searchStart + 4] >> 4;
                
                if (extId == 1) // Sequence extension
                {
                    hasExtension = true;
                    
                    // Parse sequence extension
                    // Byte 4: Extension ID (4) + profile_and_level_indication (8) = 12 bits
                    int profileLevel = ((data[searchStart + 4] & 0x0F) << 4) | (data[searchStart + 5] >> 4);
                    
                    // Extract profile (bits 4-6) and level (bits 0-3)
                    profile = (Mpeg2Profile)((profileLevel >> 4) & 0x07);
                    level = (Mpeg2Level)(profileLevel & 0x0F);
                    
                    // Byte 5 bits 3-0: progressive_sequence, chroma_format (2 bits)
                    progressive = ((data[searchStart + 5] >> 3) & 1) != 0;
                    chromaFormat = (Mpeg2ChromaFormat)((data[searchStart + 5] >> 1) & 0x03);
                    
                    // Byte 6: horizontal_size_extension (2 bits), vertical_size_extension (2 bits)
                    int hSizeExt = (data[searchStart + 6] >> 6) & 0x03;
                    int vSizeExt = (data[searchStart + 6] >> 4) & 0x03;
                    
                    horizontalSize |= (hSizeExt << 12);
                    verticalSize |= (vSizeExt << 12);
                    
                    // Byte 6-7: bit_rate_extension (12 bits)
                    int brExt = ((data[searchStart + 6] & 0x0F) << 8) | data[searchStart + 7];
                    bitRateValue |= (brExt << 18) * 400;
                    
                    // Byte 8: low_delay
                    lowDelay = ((data[searchStart + 8] >> 7) & 1) != 0;
                    
                    // Frame rate extension (numerator/denominator multipliers)
                    int frExtN = (data[searchStart + 8] >> 5) & 0x03;
                    int frExtD = data[searchStart + 8] & 0x1F;
                    
                    if (frExtN > 0 || frExtD > 0)
                    {
                        frNum = frNum * (frExtN + 1);
                        frDen = frDen * (frExtD + 1);
                    }
                    
                    bytesConsumed = searchStart + 10;
                    break;
                }
            }
            searchStart++;
        }
        
        header = new Mpeg2SequenceHeader
        {
            Width = horizontalSize,
            Height = verticalSize,
            AspectRatio = (Mpeg2AspectRatio)aspectRatioCode,
            FrameRate = frDen > 0 ? (double)frNum / frDen : 0,
            FrameRateNumerator = frNum,
            FrameRateDenominator = frDen,
            Bitrate = bitRateValue,
            HasExtension = hasExtension,
            Profile = profile,
            Level = level,
            IsProgressive = progressive,
            ChromaFormat = chromaFormat,
            IsLowDelay = lowDelay
        };
        
        return true;
    }
    
    /// <summary>
    /// Parses an MPEG-2 picture header.
    /// </summary>
    public static bool TryParsePictureHeader(ReadOnlySpan<byte> data, out Mpeg2PictureHeader header, out int bytesConsumed)
    {
        header = default;
        bytesConsumed = 0;
        
        if (data.Length < 9)
            return false;
        
        // Verify start code
        if (data[0] != 0 || data[1] != 0 || data[2] != 1 || data[3] != Mpeg2StartCodes.Picture)
            return false;
        
        // Bytes 4-5: temporal_reference (10 bits) + picture_coding_type (3 bits) + vbv_delay start
        int temporalRef = (data[4] << 2) | (data[5] >> 6);
        int pictureType = (data[5] >> 3) & 0x07;
        
        // Bytes 5-7: vbv_delay (16 bits, spanning byte boundaries)
        int vbvDelay = ((data[5] & 0x07) << 13) | (data[6] << 5) | (data[7] >> 3);
        
        bytesConsumed = 8;
        
        // Look for picture coding extension
        var pictureStructure = Mpeg2PictureStructure.FramePicture;
        bool topFieldFirst = false;
        bool repeatFirstField = false;
        bool progressiveFrame = true;
        
        // Search for picture coding extension
        int searchStart = 8;
        while (searchStart + 9 <= data.Length)
        {
            if (data[searchStart] == 0 && data[searchStart + 1] == 0 && 
                data[searchStart + 2] == 1 && data[searchStart + 3] == Mpeg2StartCodes.Extension)
            {
                int extId = data[searchStart + 4] >> 4;
                
                if (extId == 8) // Picture coding extension
                {
                    // Byte 6 bits 1-0: picture_structure
                    pictureStructure = (Mpeg2PictureStructure)(data[searchStart + 6] & 0x03);
                    
                    // Byte 7: flags
                    topFieldFirst = ((data[searchStart + 7] >> 7) & 1) != 0;
                    repeatFirstField = ((data[searchStart + 7] >> 1) & 1) != 0;
                    progressiveFrame = (data[searchStart + 7] & 1) != 0;
                    
                    bytesConsumed = searchStart + 9;
                    break;
                }
            }
            searchStart++;
        }
        
        header = new Mpeg2PictureHeader
        {
            TemporalReference = temporalRef,
            PictureType = (Mpeg2PictureType)pictureType,
            VbvDelay = vbvDelay,
            PictureStructure = pictureStructure,
            TopFieldFirst = topFieldFirst,
            RepeatFirstField = repeatFirstField,
            ProgressiveFrame = progressiveFrame
        };
        
        return true;
    }
    
    /// <summary>
    /// Parses an MPEG-2 GOP header.
    /// </summary>
    public static bool TryParseGopHeader(ReadOnlySpan<byte> data, out Mpeg2GopHeader header)
    {
        header = default;
        
        if (data.Length < 8)
            return false;
        
        // Verify start code
        if (data[0] != 0 || data[1] != 0 || data[2] != 1 || data[3] != Mpeg2StartCodes.GroupOfPictures)
            return false;
        
        // Bytes 4-7: GOP timecode
        // drop_frame (1) + hours (5) + minutes (6) + marker (1) + seconds (6) + frames (6) = 25 bits
        bool dropFrame = (data[4] >> 7) != 0;
        int hours = (data[4] >> 2) & 0x1F;
        int minutes = ((data[4] & 0x03) << 4) | (data[5] >> 4);
        int seconds = ((data[5] & 0x07) << 3) | (data[6] >> 5);
        int frames = ((data[6] & 0x1F) << 1) | (data[7] >> 7);
        
        bool closedGop = ((data[7] >> 6) & 1) != 0;
        bool brokenLink = ((data[7] >> 5) & 1) != 0;
        
        header = new Mpeg2GopHeader
        {
            Hours = hours,
            Minutes = minutes,
            Seconds = seconds,
            Frames = frames,
            DropFrame = dropFrame,
            ClosedGop = closedGop,
            BrokenLink = brokenLink
        };
        
        return true;
    }
    
    /// <summary>
    /// Gets a human-readable description of the sequence header.
    /// </summary>
    public static string GetDescription(in Mpeg2SequenceHeader header)
    {
        string profile = header.HasExtension ? $"{header.Profile}@{header.Level}" : "MPEG-1";
        string scan = header.IsProgressive ? "progressive" : "interlaced";
        string aspect = header.AspectRatio switch
        {
            Mpeg2AspectRatio.Square => "1:1",
            Mpeg2AspectRatio.FourThree => "4:3",
            Mpeg2AspectRatio.SixteenNine => "16:9",
            Mpeg2AspectRatio.TwoTwoOne => "2.21:1",
            _ => "?"
        };
        
        return $"MPEG-2 {profile} {header.Width}x{header.Height} {aspect} {header.FrameRate:F2}fps {scan}";
    }
}

// VideoToolbox Types and Constants
// macOS/iOS hardware video decoding API definitions

using System.Runtime.InteropServices;

namespace MediaKernel.Core.Hardware.VideoToolbox;

/// <summary>
/// VideoToolbox status codes (OSStatus values).
/// </summary>
public enum VtStatus
{
    Success = 0,
    PropertyNotSupported = -12900,
    PropertyReadOnly = -12901,
    ParameterError = -12902,
    InvalidSession = -12903,
    AllocationFailed = -12904,
    PixelTransferNotSupported = -12905,
    CouldNotFindVideoDecoder = -12906,
    CouldNotCreateInstance = -12907,
    CouldNotFindVideoEncoder = -12908,
    VideoDecoderBadDataError = -12909,
    VideoDecoderUnsupportedDataFormatError = -12910,
    VideoDecoderMalfunctionError = -12911,
    VideoEncoderMalfunctionError = -12912,
    VideoDecoderNotAvailable = -12913,
    ImageRotationNotSupported = -12914,
    VideoEncoderNotAvailable = -12915,
    FormatDescriptionChangeNotSupported = -12916,
    InsufficientSourceColorData = -12917,
    CouldNotCreateColorCorrectionData = -12918,
    ColorSyncTransformConvertFailed = -12919,
    VideoDecoderAuthorization = -12210,
    VideoEncoderAuthorization = -12211,
    ColorCorrectionPixelTransferFailed = -12212,
    MultiPassStorageIdentifierMismatch = -12213,
    MultiPassStorageInvalid = -12214,
    FrameSiloInvalidTimeStamp = -12215,
    FrameSiloInvalidTimeRange = -12216,
    CouldNotFindTemporalFilter = -12217,
    PixelTransferNotPermitted = -12218,
    ColorCorrectionImageRotationFailed = -12219,
}

/// <summary>
/// CoreMedia video codec types (CMVideoCodecType).
/// </summary>
public enum CmVideoCodecType : uint
{
    // Uncompressed
    Argb32 = 0x00000020,
    Bgra32 = 0x42475241,  // 'BGRA'
    YCbCr420Planar = 0x79343230, // 'y420'
    YCbCr420BiPlanarFullRange = 0x34323066, // '420f'
    YCbCr420BiPlanarVideoRange = 0x34323076, // '420v'

    // H.264
    H264 = 0x61766331,  // 'avc1'

    // H.265/HEVC
    Hevc = 0x68766331,  // 'hvc1'
    HevcWithAlpha = 0x6D757861, // 'muxa'

    // VP9
    Vp9 = 0x76703039,  // 'vp09'

    // AV1
    Av1 = 0x61763031,  // 'av01'

    // ProRes
    ProRes4444 = 0x61703468,  // 'ap4h'
    ProRes422 = 0x61706368,   // 'apch'
    ProRes422HQ = 0x61706368, // 'apch'
    ProRes422LT = 0x6170636E, // 'apcn'
    ProRes422Proxy = 0x6170636F, // 'apco'
    ProResRaw = 0x61707270,   // 'aprp'

    // MPEG
    Mpeg4Video = 0x6D703476, // 'mp4v'
    Mpeg2Video = 0x6D703276, // 'mp2v'
    Mpeg1Video = 0x6D703176, // 'mp1v'

    // JPEG
    Jpeg = 0x6A706567,  // 'jpeg'
}

/// <summary>
/// VTDecodeFrameFlags for decode frame options.
/// </summary>
[Flags]
public enum VtDecodeFrameFlags : uint
{
    None = 0,
    EnableAsynchronousDecompression = 1 << 0,
    DoNotOutputFrame = 1 << 1,
    OutputFirstFrame1xRealTimePlayback = 1 << 2,
    EnableTemporalProcessing = 1 << 3,
}

/// <summary>
/// VTDecodeInfoFlags returned from decode operations.
/// </summary>
[Flags]
public enum VtDecodeInfoFlags : uint
{
    None = 0,
    Asynchronous = 1 << 0,
    FrameDropped = 1 << 1,
    ImageBufferModifiable = 1 << 2,
}

/// <summary>
/// CMTime structure for timestamps.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CmTime
{
    /// <summary>The time value (numerator).</summary>
    public long Value;
    /// <summary>The timescale (denominator).</summary>
    public int Timescale;
    /// <summary>Flags indicating validity/special values.</summary>
    public CmTimeFlags Flags;
    /// <summary>The epoch.</summary>
    public long Epoch;

    /// <summary>Invalid time.</summary>
    public static readonly CmTime Invalid = new() { Flags = CmTimeFlags.Invalid };

    /// <summary>Indefinite time.</summary>
    public static readonly CmTime Indefinite = new() { Flags = CmTimeFlags.Indefinite };

    /// <summary>Zero time.</summary>
    public static readonly CmTime Zero = new() { Value = 0, Timescale = 1, Flags = CmTimeFlags.Valid };

    /// <summary>
    /// Creates a CMTime from seconds.
    /// </summary>
    public static CmTime FromSeconds(double seconds, int timescale = 1000000)
    {
        return new CmTime
        {
            Value = (long)(seconds * timescale),
            Timescale = timescale,
            Flags = CmTimeFlags.Valid
        };
    }

    /// <summary>
    /// Converts to seconds.
    /// </summary>
    public readonly double ToSeconds()
    {
        if ((Flags & CmTimeFlags.Valid) == 0 || Timescale == 0)
            return 0;
        return (double)Value / Timescale;
    }

    /// <summary>
    /// Converts to microseconds.
    /// </summary>
    public readonly long ToMicroseconds()
    {
        if ((Flags & CmTimeFlags.Valid) == 0 || Timescale == 0)
            return 0;
        return Value * 1_000_000 / Timescale;
    }
}

/// <summary>
/// CMTime flags.
/// </summary>
[Flags]
public enum CmTimeFlags : uint
{
    Valid = 1 << 0,
    HasBeenRounded = 1 << 1,
    PositiveInfinity = 1 << 2,
    NegativeInfinity = 1 << 3,
    Indefinite = 1 << 4,
    ImpliedValueFlagsMask = PositiveInfinity | NegativeInfinity | Indefinite,
    Invalid = 0,
}

/// <summary>
/// CVPixelFormatType (Core Video pixel buffer format types).
/// </summary>
public enum CvPixelFormatType : uint
{
    // Grayscale
    OneComponent8 = 0x4C303038,  // 'L008'
    OneComponent16Half = 0x4C303068, // 'L00h'
    OneComponent32Float = 0x4C303066, // 'L00f'

    // YUV 4:2:0
    Type_420YpCbCr8BiPlanarVideoRange = 0x34323076, // '420v'
    Type_420YpCbCr8BiPlanarFullRange = 0x34323066,  // '420f'
    Type_420YpCbCr8Planar = 0x79343230,  // 'y420'
    Type_420YpCbCr8PlanarFullRange = 0x66343230, // 'f420'
    Type_420YpCbCr10BiPlanarVideoRange = 0x78343230, // 'x420'
    Type_420YpCbCr10BiPlanarFullRange = 0x78663230, // 'xf20'

    // YUV 4:2:2
    Type_422YpCbCr8 = 0x32767579,  // '2vuy'
    Type_422YpCbCr8_yuvs = 0x79757673, // 'yuvs'
    Type_422YpCbCr10 = 0x76323130,  // 'v210'
    Type_422YpCbCr16 = 0x76323136,  // 'v216'

    // YUV 4:4:4
    Type_444YpCbCr8 = 0x76333038,  // 'v308'
    Type_444YpCbCr10 = 0x76343130,  // 'v410'

    // RGB
    Type_24RGB = 0x00000018,
    Type_24BGR = 0x32344247,  // '24BG'
    Type_32ARGB = 0x00000020,
    Type_32BGRA = 0x42475241,  // 'BGRA'
    Type_32RGBA = 0x52474241,  // 'RGBA'
    Type_64ARGB = 0x62363461,  // 'b64a'
    Type_64RGBAHalf = 0x52476841, // 'RGhA'
    Type_128RGBAFloat = 0x52476641, // 'RGfA'
}

/// <summary>
/// H.264 profile constants for decoder specification.
/// </summary>
public static class H264Profile
{
    public const int Baseline = 66;
    public const int Main = 77;
    public const int Extended = 88;
    public const int High = 100;
    public const int High10 = 110;
    public const int High422 = 122;
    public const int High444 = 244;
}

/// <summary>
/// HEVC profile constants.
/// </summary>
public static class HevcProfile
{
    public const int Main = 1;
    public const int Main10 = 2;
    public const int MainStill = 3;
    public const int RangeExtension = 4;
}

/// <summary>
/// VTDecompressionSession decode frame flags.
/// </summary>
[Flags]
public enum VtSessionMode : uint
{
    None = 0,
    LowPower = 1 << 0,
    HighQuality = 1 << 1,
    EnableHardwareAcceleration = 1 << 2,
    RequireHardwareAcceleration = 1 << 3,
}

/// <summary>
/// CoreFoundation type ID placeholder.
/// </summary>
public static class CfTypes
{
    public const int True = 1;
    public const int False = 0;
}

/// <summary>
/// Invalid handle constant.
/// </summary>
public static class VtSession
{
    public const nint InvalidHandle = 0;
}

/// <summary>
/// CoreFoundation dictionary keys for VideoToolbox.
/// </summary>
public static class VtDecoderKeys
{
    // Decoder specification keys (as string literals for P/Invoke)
    public const string RequireHardwareAcceleratedVideoDecoder = "RequireHardwareAcceleratedVideoDecoder";
    public const string EnableHardwareAcceleratedVideoDecoder = "EnableHardwareAcceleratedVideoDecoder";
    public const string UsingHardwareAcceleratedVideoDecoder = "UsingHardwareAcceleratedVideoDecoder";

    // Pixel buffer attributes
    public const string PixelBufferPixelFormatType = "PixelFormatType";
    public const string PixelBufferWidth = "Width";
    public const string PixelBufferHeight = "Height";
    public const string PixelBufferOpenGLCompatibility = "OpenGLCompatibility";
    public const string PixelBufferMetalCompatibility = "MetalCompatibility";
    public const string PixelBufferIOSurfaceProperties = "IOSurfaceProperties";
}

// VA-API Types and Constants
// Linux Video Acceleration API definitions for hardware decoding

using System.Runtime.InteropServices;

namespace MediaKernel.Core.Hardware.VaApi;

/// <summary>
/// VA-API status codes.
/// </summary>
public enum VaStatus
{
    Success = 0,
    ErrorOperationFailed = 1,
    ErrorAllocationFailed = 2,
    ErrorInvalidDisplay = 3,
    ErrorInvalidConfig = 4,
    ErrorInvalidContext = 5,
    ErrorInvalidSurface = 6,
    ErrorInvalidBuffer = 7,
    ErrorInvalidImage = 8,
    ErrorInvalidSubpicture = 9,
    ErrorAttrNotSupported = 10,
    ErrorMaxNumExceeded = 11,
    ErrorUnsupportedProfile = 12,
    ErrorUnsupportedEntrypoint = 13,
    ErrorUnsupportedRtFormat = 14,
    ErrorUnsupportedBuffertype = 15,
    ErrorSurfaceBusy = 16,
    ErrorFlagNotSupported = 17,
    ErrorInvalidParameter = 18,
    ErrorResolutionNotSupported = 19,
    ErrorUnimplemented = 20,
    ErrorSurfaceInDisplaying = 21,
    ErrorInvalidImageFormat = 22,
    ErrorDecodingError = 23,
    ErrorEncodingError = 24,
    ErrorInvalidValue = 25,
    ErrorUnsupportedFilter = 26,
    ErrorInvalidFilterChain = 27,
    ErrorHwBusy = 28,
    ErrorUnsupportedMemoryType = 29,
    ErrorNotEnoughBuffer = 30,
}

/// <summary>
/// VA-API codec profiles.
/// </summary>
public enum VaProfile
{
    None = -1,
    Mpeg2Simple = 0,
    Mpeg2Main = 1,
    Mpeg4Simple = 2,
    Mpeg4AdvancedSimple = 3,
    Mpeg4Main = 4,
    H264Baseline = 5,
    H264Main = 6,
    H264High = 7,
    Vc1Simple = 8,
    Vc1Main = 9,
    Vc1Advanced = 10,
    H263Baseline = 11,
    JpegBaseline = 12,
    H264ConstrainedBaseline = 13,
    Vp8Version0_3 = 14,
    H264MultiviewHigh = 15,
    H264StereoHigh = 16,
    HevcMain = 17,
    HevcMain10 = 18,
    Vp9Profile0 = 19,
    Vp9Profile1 = 20,
    Vp9Profile2 = 21,
    Vp9Profile3 = 22,
    HevcMain12 = 23,
    HevcMain422_10 = 24,
    HevcMain422_12 = 25,
    HevcMain444 = 26,
    HevcMain444_10 = 27,
    HevcMain444_12 = 28,
    HevcSccMain = 29,
    HevcSccMain10 = 30,
    HevcSccMain444 = 31,
    Av1Profile0 = 32,
    Av1Profile1 = 33,
    HevcSccMain444_10 = 34,
    Protected = 35,
}

/// <summary>
/// VA-API entry points (decode, encode, etc.).
/// </summary>
public enum VaEntrypoint
{
    Vld = 1,                  // Variable length decoding
    Idct = 2,                 // IDCT
    MoComp = 3,               // Motion compensation
    Deblocking = 4,           // Deblocking
    EncSlice = 5,             // Encode slice
    EncPicture = 6,           // Encode picture
    EncSliceLp = 7,           // Encode slice low power
    VideoProc = 10,           // Video processing
    Fei = 11,                 // Feature extraction interface
    Stats = 12,               // Statistics
    ProtectedTeeComm = 1000,  // Protected content
    ProtectedContent = 1001,  // Protected content
}

/// <summary>
/// VA-API render target (surface) formats.
/// </summary>
[Flags]
public enum VaRtFormat : uint
{
    Yuv420 = 0x00000001,
    Yuv422 = 0x00000002,
    Yuv444 = 0x00000004,
    Yuv411 = 0x00000008,
    Yuv400 = 0x00000010,
    Yuv420_10Bpp = 0x00000100,
    Yuv420_12Bpp = 0x00001000,
    Yuv422_10Bpp = 0x00000200,
    Yuv422_12Bpp = 0x00002000,
    Yuv444_10Bpp = 0x00000400,
    Yuv444_12Bpp = 0x00004000,
    Rgb16 = 0x00010000,
    Rgb32 = 0x00020000,
    RgbP = 0x00100000,
    Protected = 0x80000000,
}

/// <summary>
/// VA-API buffer types for slice data, picture parameters, etc.
/// </summary>
public enum VaBufferType
{
    PictureParameterBufferType = 0,
    IQMatrixBufferType = 1,
    BitPlaneBufferType = 2,
    SliceGroupMapBufferType = 3,
    SliceParameterBufferType = 4,
    SliceDataBufferType = 5,
    MacroblockParameterBufferType = 6,
    ResidualDataBufferType = 7,
    DeblockingParameterBufferType = 8,
    ImageBufferType = 9,
    ProtectedSliceDataBufferType = 10,
    QMatrixBufferType = 11,
    HuffmanTableBufferType = 12,
    ProbabilityBufferType = 13,
    // Encode types
    EncCodedBufferType = 21,
    EncSequenceParameterBufferType = 22,
    EncPictureParameterBufferType = 23,
    EncSliceParameterBufferType = 24,
    EncPackedHeaderParameterBufferType = 25,
    EncPackedHeaderDataBufferType = 26,
    EncMiscParameterBufferType = 27,
    EncMacroblockParameterBufferType = 28,
    EncMacroblockMapBufferType = 29,
    // Processing
    ProcPipelineParameterBufferType = 41,
    ProcFilterParameterBufferType = 42,
}

/// <summary>
/// VA surface status flags.
/// </summary>
public enum VaSurfaceStatus
{
    Rendering = 1,   // Surface is being rendered to
    Displaying = 2,  // Surface is being displayed
    Ready = 4,       // Surface is ready for reuse
    Skipped = 8,     // Surface was skipped during decode
}

/// <summary>
/// VA-API FourCC codes for pixel formats.
/// </summary>
public static class VaFourcc
{
    public const uint Nv12 = 0x3231564E;   // 'NV12'
    public const uint Nv21 = 0x3132564E;   // 'NV21'
    public const uint Yv12 = 0x32315659;   // 'YV12'
    public const uint I420 = 0x30323449;   // 'I420'
    public const uint Iyuv = I420;
    public const uint Yuy2 = 0x32595559;   // 'YUY2'
    public const uint Uyvy = 0x59565955;   // 'UYVY'
    public const uint Y800 = 0x30303859;   // 'Y800'
    public const uint Argb = 0x42475241;   // 'ARGB'
    public const uint Rgba = 0x41424752;   // 'RGBA'
    public const uint Bgra = 0x41524742;   // 'BGRA'
    public const uint Abgr = 0x52474241;   // 'ABGR'
    public const uint Xrgb = 0x42475258;   // 'XRGB'
    public const uint Rgbx = 0x58424752;   // 'RGBX'
    public const uint Bgrx = 0x58524742;   // 'BGRX'
    public const uint Xbgr = 0x52474258;   // 'XBGR'
    public const uint P010 = 0x30313050;   // 'P010'
    public const uint P012 = 0x32313050;   // 'P012'
    public const uint P016 = 0x36313050;   // 'P016'
    public const uint Y210 = 0x30313259;   // 'Y210'
    public const uint Y212 = 0x32313259;   // 'Y212'
    public const uint Y216 = 0x36313259;   // 'Y216'
    public const uint Y410 = 0x30313459;   // 'Y410'
    public const uint Y412 = 0x32313459;   // 'Y412'
    public const uint Y416 = 0x36313459;   // 'Y416'
    public const uint Ayuv = 0x56555941;   // 'AYUV'
    public const uint Xyuv = 0x56555958;   // 'XYUV'
}

/// <summary>
/// VA-API config attribute types.
/// </summary>
public enum VaConfigAttribType
{
    RtFormat = 0,
    SpatialResidual = 1,
    SpatialClipping = 2,
    IntraResidual = 3,
    Encryption = 4,
    RateControl = 5,
    DecSliceMode = 6,
    DecJpeg = 7,
    DecProcessing = 8,
    EncPackedHeaders = 10,
    EncInterlaced = 11,
    EncMaxRefFrames = 13,
    EncMaxSlices = 14,
    EncSliceStructure = 15,
    EncMacroblockInfo = 16,
    EncQuantization = 21,
    EncIntraRefresh = 22,
    EncSkipFrame = 23,
    EncRoI = 24,
    EncRateControlExt = 25,
    MaxPictureWidth = 30,
    MaxPictureHeight = 31,
    ProcessingRate = 32,
}

/// <summary>
/// Special value for undefined config attribute.
/// </summary>
public static class VaConfigAttribValues
{
    public const uint Undefined = 0xFFFFFFFF;
}

/// <summary>
/// VA config attribute.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VaConfigAttrib
{
    public VaConfigAttribType Type;
    public uint Value;
}

/// <summary>
/// VA image format.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VaImageFormat
{
    public uint FourCC;
    public uint ByteOrder;
    public uint BitsPerPixel;
    public uint Depth;
    public uint RedMask;
    public uint GreenMask;
    public uint BlueMask;
    public uint AlphaMask;
    public fixed uint Reserved[4];
}

/// <summary>
/// VA image structure.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VaImage
{
    public uint ImageId;
    public VaImageFormat Format;
    public fixed byte Unused[4];  // Padding
    public ushort Width;
    public ushort Height;
    public uint DataSize;
    public uint NumPlanes;
    public fixed uint Pitches[3];
    public fixed uint Offsets[3];
    public int NumPalettes;
    public int EntryBytes;
    public int ComponentOrder0;
    public int ComponentOrder1;
    public int ComponentOrder2;
    public int ComponentOrder3;

    public static readonly uint InvalidId = 0xFFFFFFFF;
}

/// <summary>
/// VA surface attributes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VaSurfaceAttrib
{
    public VaSurfaceAttribType Type;
    public VaSurfaceAttribFlags Flags;
    public VaGenericValue Value;
}

/// <summary>
/// Surface attribute types.
/// </summary>
public enum VaSurfaceAttribType
{
    None = 0,
    PixelFormat = 1,
    MinWidth = 2,
    MaxWidth = 3,
    MinHeight = 4,
    MaxHeight = 5,
    MemoryType = 6,
    ExternalBufferDescriptor = 7,
    UsageHint = 8,
    DrmFormatModifiers = 9,
    Count = 10,
}

/// <summary>
/// Surface attribute flags.
/// </summary>
[Flags]
public enum VaSurfaceAttribFlags : uint
{
    Gettable = 0x00000001,
    Settable = 0x00000002,
}

/// <summary>
/// VA generic value (union type).
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public struct VaGenericValue
{
    [FieldOffset(0)]
    public VaGenericValueType Type;

    [FieldOffset(4)]
    public int IntValue;

    [FieldOffset(4)]
    public float FloatValue;

    [FieldOffset(4)]
    public nint PointerValue;
}

/// <summary>
/// Generic value types.
/// </summary>
public enum VaGenericValueType
{
    Integer = 1,
    Float = 2,
    Pointer = 3,
    Func = 4,
}

/// <summary>
/// Surface memory types.
/// </summary>
[Flags]
public enum VaSurfaceMemoryType : uint
{
    VaMemory = 0x00000001,
    SurfaceMemory = 0x00000002,
    UserPointer = 0x00000004,
    KernelDrm = 0x00000010,
    DrmPrimeFdes = 0x10000000,
    DrmPrime2 = 0x40000000,
}

/// <summary>
/// Picture decode flags.
/// </summary>
[Flags]
public enum VaPictureFlags : uint
{
    TopField = 0x00000001,
    BottomField = 0x00000002,
    ShortTermReference = 0x00000004,
    LongTermReference = 0x00000008,
    Invalid = 0x00000010,
}

/// <summary>
/// H.264 picture parameter buffer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VaPictureParameterBufferH264
{
    public VaPictureH264 CurrPic;
    public fixed uint ReferenceFrames[16 * 4];  // VAPictureH264 is 16 bytes, 16 entries
    public ushort PictureWidthInMbsMinus1;
    public ushort PictureHeightInMbsMinus1;
    public byte BitDepthLumaMinus8;
    public byte BitDepthChromaMinus8;
    public byte NumRefFrames;
    public byte SeqFields;   // Packed bitfields
    public byte PicFields;   // Packed bitfields
    public byte NumSliceGroupsMinus1;
    public byte SliceGroupMapType;
    public ushort SliceGroupChangeRateMinus1;
    public sbyte PicInitQpMinus26;
    public sbyte PicInitQsMinus26;
    public sbyte ChromaQpIndexOffset;
    public sbyte SecondChromaQpIndexOffset;
    public uint FrameNum;
}

/// <summary>
/// H.264 picture structure.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VaPictureH264
{
    public uint PictureId;       // VASurfaceID
    public uint FrameIdx;
    public VaPictureFlags Flags;
    public int TopFieldOrderCnt;
    public int BottomFieldOrderCnt;

    public static readonly uint InvalidId = 0xFFFFFFFF;
}

/// <summary>
/// H.264 IQ matrix buffer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VaIQMatrixBufferH264
{
    public fixed byte ScalingList4x4[6 * 16];
    public fixed byte ScalingList8x8[2 * 64];
}

/// <summary>
/// H.264 slice parameter buffer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VaSliceParameterBufferH264
{
    public uint SliceDataSize;
    public uint SliceDataOffset;
    public uint SliceDataFlag;
    public ushort SliceDataBitOffset;
    public ushort FirstMbInSlice;
    public byte SliceType;
    public byte DirectSpatialMvPredFlag;
    public byte NumRefIdxL0ActiveMinus1;
    public byte NumRefIdxL1ActiveMinus1;
    public byte CabacInitIdc;
    public sbyte SliceQpDelta;
    public byte DisableDeblockingFilterIdc;
    public sbyte SliceAlphaC0OffsetDiv2;
    public sbyte SliceBetaOffsetDiv2;
    public fixed uint RefPicList0[32 * 4];  // VAPictureH264
    public fixed uint RefPicList1[32 * 4];  // VAPictureH264
    public byte LumaLog2WeightDenom;
    public byte ChromaLog2WeightDenom;
    public byte LumaWeightL0Flag;
    public fixed short LumaWeightL0[32];
    public fixed short LumaOffsetL0[32];
    public byte ChromaWeightL0Flag;
    public fixed short ChromaWeightL0[32 * 2];
    public fixed short ChromaOffsetL0[32 * 2];
    public byte LumaWeightL1Flag;
    public fixed short LumaWeightL1[32];
    public fixed short LumaOffsetL1[32];
    public byte ChromaWeightL1Flag;
    public fixed short ChromaWeightL1[32 * 2];
    public fixed short ChromaOffsetL1[32 * 2];
}

/// <summary>
/// Invalid surface ID constant.
/// </summary>
public static class VaSurface
{
    public const uint InvalidId = 0xFFFFFFFF;
}

/// <summary>
/// Invalid buffer ID constant.
/// </summary>
public static class VaBuffer
{
    public const uint InvalidId = 0xFFFFFFFF;
}

/// <summary>
/// Invalid context ID constant.
/// </summary>
public static class VaContext
{
    public const uint InvalidId = 0xFFFFFFFF;
}

/// <summary>
/// Invalid config ID constant.
/// </summary>
public static class VaConfig
{
    public const uint InvalidId = 0xFFFFFFFF;
}

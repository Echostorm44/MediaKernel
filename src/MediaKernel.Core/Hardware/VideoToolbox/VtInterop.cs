// VideoToolbox P/Invoke Interop
// macOS/iOS hardware video decoding native bindings

using System.Runtime.InteropServices;

namespace MediaKernel.Core.Hardware.VideoToolbox;

/// <summary>
/// P/Invoke bindings for VideoToolbox framework.
/// </summary>
public static unsafe partial class VtInterop
{
    // Framework paths
    private const string VideoToolbox = "/System/Library/Frameworks/VideoToolbox.framework/VideoToolbox";
    private const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    #region VTDecompressionSession Functions

    /// <summary>
    /// Callback for decoded frame output.
    /// </summary>
    public delegate void VtDecompressionOutputCallback(
        nint decompressionOutputRefCon,
        nint sourceFrameRefCon,
        VtStatus status,
        VtDecodeInfoFlags infoFlags,
        nint imageBuffer,  // CVImageBufferRef
        CmTime presentationTimeStamp,
        CmTime presentationDuration);

    /// <summary>
    /// Output callback record structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VtDecompressionOutputCallbackRecord
    {
        public nint DecompressionOutputCallback;  // Function pointer
        public nint DecompressionOutputRefCon;    // User data
    }

    /// <summary>
    /// Creates a decompression session.
    /// </summary>
    [DllImport(VideoToolbox, EntryPoint = "VTDecompressionSessionCreate")]
    public static extern VtStatus VTDecompressionSessionCreate(
        nint allocator,
        nint videoFormatDescription,  // CMVideoFormatDescriptionRef
        nint videoDecoderSpecification,  // CFDictionaryRef
        nint destinationImageBufferAttributes,  // CFDictionaryRef
        VtDecompressionOutputCallbackRecord* outputCallback,
        out nint decompressionSession);

    /// <summary>
    /// Decodes a frame.
    /// </summary>
    [DllImport(VideoToolbox, EntryPoint = "VTDecompressionSessionDecodeFrame")]
    public static extern VtStatus VTDecompressionSessionDecodeFrame(
        nint session,
        nint sampleBuffer,  // CMSampleBufferRef
        VtDecodeFrameFlags decodeFlags,
        nint sourceFrameRefCon,
        out VtDecodeInfoFlags infoFlagsOut);

    /// <summary>
    /// Decodes a frame with output handler block.
    /// </summary>
    [DllImport(VideoToolbox, EntryPoint = "VTDecompressionSessionDecodeFrameWithOutputHandler")]
    public static extern VtStatus VTDecompressionSessionDecodeFrameWithOutputHandler(
        nint session,
        nint sampleBuffer,
        VtDecodeFrameFlags decodeFlags,
        out VtDecodeInfoFlags infoFlagsOut,
        nint outputHandler);  // VTDecompressionOutputHandler block

    /// <summary>
    /// Finishes delayed frames.
    /// </summary>
    [DllImport(VideoToolbox, EntryPoint = "VTDecompressionSessionFinishDelayedFrames")]
    public static extern VtStatus VTDecompressionSessionFinishDelayedFrames(nint session);

    /// <summary>
    /// Waits for async frames.
    /// </summary>
    [DllImport(VideoToolbox, EntryPoint = "VTDecompressionSessionWaitForAsynchronousFrames")]
    public static extern VtStatus VTDecompressionSessionWaitForAsynchronousFrames(nint session);

    /// <summary>
    /// Invalidates the session.
    /// </summary>
    [DllImport(VideoToolbox, EntryPoint = "VTDecompressionSessionInvalidate")]
    public static extern void VTDecompressionSessionInvalidate(nint session);

    /// <summary>
    /// Checks if hardware decode is supported for a codec.
    /// </summary>
    [DllImport(VideoToolbox, EntryPoint = "VTIsHardwareDecodeSupported")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool VTIsHardwareDecodeSupported(CmVideoCodecType codecType);

    /// <summary>
    /// Copies a property from the session.
    /// </summary>
    [DllImport(VideoToolbox, EntryPoint = "VTSessionCopyProperty")]
    public static extern VtStatus VTSessionCopyProperty(
        nint session,
        nint propertyKey,  // CFStringRef
        nint allocator,
        out nint propertyValueOut);

    /// <summary>
    /// Sets a property on the session.
    /// </summary>
    [DllImport(VideoToolbox, EntryPoint = "VTSessionSetProperty")]
    public static extern VtStatus VTSessionSetProperty(
        nint session,
        nint propertyKey,  // CFStringRef
        nint propertyValue);

    #endregion

    #region CMVideoFormatDescription Functions

    /// <summary>
    /// Creates a video format description from H.264 parameter sets.
    /// </summary>
    [DllImport(CoreMedia, EntryPoint = "CMVideoFormatDescriptionCreateFromH264ParameterSets")]
    public static extern VtStatus CMVideoFormatDescriptionCreateFromH264ParameterSets(
        nint allocator,
        nuint parameterSetCount,
        byte** parameterSetPointers,
        nuint* parameterSetSizes,
        int nalUnitHeaderLength,
        out nint formatDescriptionOut);

    /// <summary>
    /// Creates a video format description from HEVC parameter sets.
    /// </summary>
    [DllImport(CoreMedia, EntryPoint = "CMVideoFormatDescriptionCreateFromHEVCParameterSets")]
    public static extern VtStatus CMVideoFormatDescriptionCreateFromHEVCParameterSets(
        nint allocator,
        nuint parameterSetCount,
        byte** parameterSetPointers,
        nuint* parameterSetSizes,
        int nalUnitHeaderLength,
        nint extensions,  // CFDictionaryRef
        out nint formatDescriptionOut);

    /// <summary>
    /// Gets dimensions from a video format description.
    /// </summary>
    [DllImport(CoreMedia, EntryPoint = "CMVideoFormatDescriptionGetDimensions")]
    public static extern CmVideoDimensions CMVideoFormatDescriptionGetDimensions(
        nint videoFormatDescription);

    /// <summary>
    /// Gets the codec type from a format description.
    /// </summary>
    [DllImport(CoreMedia, EntryPoint = "CMFormatDescriptionGetMediaSubType")]
    public static extern CmVideoCodecType CMFormatDescriptionGetMediaSubType(
        nint formatDescription);

    #endregion

    #region CMSampleBuffer Functions

    /// <summary>
    /// Creates a sample buffer from block buffer.
    /// </summary>
    [DllImport(CoreMedia, EntryPoint = "CMSampleBufferCreate")]
    public static extern VtStatus CMSampleBufferCreate(
        nint allocator,
        nint dataBuffer,  // CMBlockBufferRef
        [MarshalAs(UnmanagedType.U1)] bool dataReady,
        nint makeDataReadyCallback,
        nint makeDataReadyRefcon,
        nint formatDescription,  // CMFormatDescriptionRef
        nint numSamples,
        nint numSampleTimingEntries,
        CmSampleTimingInfo* sampleTimingArray,
        nint numSampleSizeEntries,
        nuint* sampleSizeArray,
        out nint sampleBufferOut);

    /// <summary>
    /// Creates a block buffer from memory.
    /// </summary>
    [DllImport(CoreMedia, EntryPoint = "CMBlockBufferCreateWithMemoryBlock")]
    public static extern VtStatus CMBlockBufferCreateWithMemoryBlock(
        nint allocator,
        void* memoryBlock,
        nuint blockLength,
        nint blockAllocator,
        nint customBlockSource,
        nuint offsetToData,
        nuint dataLength,
        uint flags,
        out nint blockBufferOut);

    #endregion

    #region CVPixelBuffer Functions

    /// <summary>
    /// Gets the width of a pixel buffer.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferGetWidth")]
    public static extern nuint CVPixelBufferGetWidth(nint pixelBuffer);

    /// <summary>
    /// Gets the height of a pixel buffer.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferGetHeight")]
    public static extern nuint CVPixelBufferGetHeight(nint pixelBuffer);

    /// <summary>
    /// Gets the pixel format type.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferGetPixelFormatType")]
    public static extern CvPixelFormatType CVPixelBufferGetPixelFormatType(nint pixelBuffer);

    /// <summary>
    /// Locks the base address of the pixel buffer.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferLockBaseAddress")]
    public static extern int CVPixelBufferLockBaseAddress(nint pixelBuffer, CvPixelBufferLockFlags lockFlags);

    /// <summary>
    /// Unlocks the base address of the pixel buffer.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferUnlockBaseAddress")]
    public static extern int CVPixelBufferUnlockBaseAddress(nint pixelBuffer, CvPixelBufferLockFlags lockFlags);

    /// <summary>
    /// Gets the base address of the pixel buffer.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferGetBaseAddress")]
    public static extern nint CVPixelBufferGetBaseAddress(nint pixelBuffer);

    /// <summary>
    /// Gets the base address of a specific plane.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferGetBaseAddressOfPlane")]
    public static extern nint CVPixelBufferGetBaseAddressOfPlane(nint pixelBuffer, nuint planeIndex);

    /// <summary>
    /// Gets the bytes per row.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferGetBytesPerRow")]
    public static extern nuint CVPixelBufferGetBytesPerRow(nint pixelBuffer);

    /// <summary>
    /// Gets the bytes per row of a specific plane.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferGetBytesPerRowOfPlane")]
    public static extern nuint CVPixelBufferGetBytesPerRowOfPlane(nint pixelBuffer, nuint planeIndex);

    /// <summary>
    /// Gets the plane count.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferGetPlaneCount")]
    public static extern nuint CVPixelBufferGetPlaneCount(nint pixelBuffer);

    /// <summary>
    /// Retains a pixel buffer.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferRetain")]
    public static extern nint CVPixelBufferRetain(nint pixelBuffer);

    /// <summary>
    /// Releases a pixel buffer.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferRelease")]
    public static extern void CVPixelBufferRelease(nint pixelBuffer);

    /// <summary>
    /// Creates a pixel buffer pool.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferPoolCreate")]
    public static extern int CVPixelBufferPoolCreate(
        nint allocator,
        nint poolAttributes,  // CFDictionaryRef
        nint pixelBufferAttributes,  // CFDictionaryRef
        out nint poolOut);

    /// <summary>
    /// Creates a pixel buffer from a pool.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferPoolCreatePixelBuffer")]
    public static extern int CVPixelBufferPoolCreatePixelBuffer(
        nint allocator,
        nint pixelBufferPool,
        out nint pixelBufferOut);

    /// <summary>
    /// Releases a pixel buffer pool.
    /// </summary>
    [DllImport(CoreVideo, EntryPoint = "CVPixelBufferPoolRelease")]
    public static extern void CVPixelBufferPoolRelease(nint pool);

    #endregion

    #region CoreFoundation Functions

    /// <summary>
    /// Creates a mutable dictionary.
    /// </summary>
    [DllImport(CoreFoundation, EntryPoint = "CFDictionaryCreateMutable")]
    public static extern nint CFDictionaryCreateMutable(
        nint allocator,
        nint capacity,
        nint keyCallBacks,
        nint valueCallBacks);

    /// <summary>
    /// Sets a dictionary value.
    /// </summary>
    [DllImport(CoreFoundation, EntryPoint = "CFDictionarySetValue")]
    public static extern void CFDictionarySetValue(nint dict, nint key, nint value);

    /// <summary>
    /// Creates a number from an int.
    /// </summary>
    [DllImport(CoreFoundation, EntryPoint = "CFNumberCreate")]
    public static extern nint CFNumberCreate(nint allocator, CfNumberType type, void* value);

    /// <summary>
    /// Creates a CFString from a C string.
    /// </summary>
    [DllImport(CoreFoundation, EntryPoint = "CFStringCreateWithCString")]
    public static extern nint CFStringCreateWithCString(
        nint allocator,
        [MarshalAs(UnmanagedType.LPStr)] string cStr,
        CfStringEncoding encoding);

    /// <summary>
    /// Releases a CoreFoundation object.
    /// </summary>
    [DllImport(CoreFoundation, EntryPoint = "CFRelease")]
    public static extern void CFRelease(nint cf);

    /// <summary>
    /// Retains a CoreFoundation object.
    /// </summary>
    [DllImport(CoreFoundation, EntryPoint = "CFRetain")]
    public static extern nint CFRetain(nint cf);

    /// <summary>
    /// Gets the Boolean true value.
    /// </summary>
    [DllImport(CoreFoundation, EntryPoint = "CFBooleanGetValue")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CFBooleanGetValue(nint boolean);

    #endregion

    #region Helper Enums and Structs

    /// <summary>
    /// Video dimensions.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CmVideoDimensions
    {
        public int Width;
        public int Height;
    }

    /// <summary>
    /// Sample timing info.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CmSampleTimingInfo
    {
        public CmTime Duration;
        public CmTime PresentationTimeStamp;
        public CmTime DecodeTimeStamp;
    }

    /// <summary>
    /// CVPixelBuffer lock flags.
    /// </summary>
    [Flags]
    public enum CvPixelBufferLockFlags : uint
    {
        None = 0,
        ReadOnly = 1,
    }

    /// <summary>
    /// CFNumber types.
    /// </summary>
    public enum CfNumberType
    {
        SInt8 = 1,
        SInt16 = 2,
        SInt32 = 3,
        SInt64 = 4,
        Float32 = 5,
        Float64 = 6,
        Char = 7,
        Short = 8,
        Int = 9,
        Long = 10,
        LongLong = 11,
        Float = 12,
        Double = 13,
    }

    /// <summary>
    /// CFString encodings.
    /// </summary>
    public enum CfStringEncoding : uint
    {
        Utf8 = 0x08000100,
        Ascii = 0x0600,
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Converts codec ID to CMVideoCodecType.
    /// </summary>
    public static CmVideoCodecType CodecIdToVideoCodecType(string codecId)
    {
        return codecId.ToLowerInvariant() switch
        {
            "h264" or "avc" or "avc1" => CmVideoCodecType.H264,
            "h265" or "hevc" or "hvc1" => CmVideoCodecType.Hevc,
            "vp9" => CmVideoCodecType.Vp9,
            "av1" => CmVideoCodecType.Av1,
            "mpeg2" or "mpeg2video" => CmVideoCodecType.Mpeg2Video,
            "mpeg4" or "mpeg4video" => CmVideoCodecType.Mpeg4Video,
            "jpeg" or "mjpeg" => CmVideoCodecType.Jpeg,
            "prores" or "prores422" => CmVideoCodecType.ProRes422,
            _ => 0,
        };
    }

    /// <summary>
    /// Converts CMVideoCodecType to HardwareCodecType.
    /// </summary>
    public static HardwareCodecType VideoCodecTypeToHardwareCodecType(CmVideoCodecType codecType)
    {
        return codecType switch
        {
            CmVideoCodecType.H264 => HardwareCodecType.H264,
            CmVideoCodecType.Hevc or CmVideoCodecType.HevcWithAlpha => HardwareCodecType.Hevc,
            CmVideoCodecType.Vp9 => HardwareCodecType.Vp9,
            CmVideoCodecType.Av1 => HardwareCodecType.Av1,
            CmVideoCodecType.Mpeg2Video => HardwareCodecType.Mpeg2,
            _ => HardwareCodecType.Unknown,
        };
    }

    /// <summary>
    /// Converts CVPixelFormatType to HardwarePixelFormat.
    /// </summary>
    public static HardwarePixelFormat CvPixelFormatToHardwareFormat(CvPixelFormatType format)
    {
        return format switch
        {
            CvPixelFormatType.Type_420YpCbCr8BiPlanarVideoRange or
            CvPixelFormatType.Type_420YpCbCr8BiPlanarFullRange => HardwarePixelFormat.Nv12,

            CvPixelFormatType.Type_420YpCbCr10BiPlanarVideoRange or
            CvPixelFormatType.Type_420YpCbCr10BiPlanarFullRange => HardwarePixelFormat.P010,

            _ => HardwarePixelFormat.Unknown,
        };
    }

    #endregion
}

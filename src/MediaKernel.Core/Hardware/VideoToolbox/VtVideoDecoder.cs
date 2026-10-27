// VideoToolbox Video Decoder
// Hardware-accelerated video decoding using VideoToolbox

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Hardware.VideoToolbox;

/// <summary>
/// VideoToolbox hardware video decoder.
/// </summary>
public sealed unsafe class VtVideoDecoder : IHardwareVideoDecoder
{
    private readonly string codecId;
    private readonly CmVideoCodecType videoCodecType;

    private nint decompressionSession;
    private nint formatDescription;
    private VtSurfacePool? surfacePool;
    private readonly ConcurrentQueue<HardwareDecodedFrame> decodedFrames = new();
    private VtInterop.VtDecompressionOutputCallback? outputCallback;
    private GCHandle callbackHandle;
    private bool isDisposed;

    private int width;
    private int height;

    /// <summary>
    /// Creates a new VideoToolbox video decoder.
    /// </summary>
    /// <param name="codecId">Codec ID (e.g., "h264", "hevc").</param>
    public VtVideoDecoder(string codecId)
    {
        this.codecId = codecId;
        videoCodecType = VtInterop.CodecIdToVideoCodecType(codecId);
    }

    /// <summary>
    /// Gets the codec ID this decoder handles.
    /// </summary>
    public string CodecId => codecId;

    /// <summary>
    /// Gets whether the decoder is initialized.
    /// </summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Gets the current video width.
    /// </summary>
    public int Width => width;

    /// <summary>
    /// Gets the current video height.
    /// </summary>
    public int Height => height;

    /// <summary>
    /// Gets the output pixel format.
    /// </summary>
    public HardwarePixelFormat OutputFormat { get; private set; } = HardwarePixelFormat.Nv12;

    /// <summary>
    /// Gets the CM video codec type.
    /// </summary>
    public CmVideoCodecType VideoCodecType => videoCodecType;

    /// <summary>
    /// Initializes the decoder with stream parameters.
    /// </summary>
    public bool Initialize(int width, int height, ReadOnlySpan<byte> codecPrivateData)
    {
        if (IsInitialized) return true;
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsIOS()) return false;
        if (videoCodecType == 0) return false;

        this.width = width;
        this.height = height;

        // Create format description from codec private data
        if (!CreateFormatDescription(codecPrivateData))
            return false;

        // Create surface pool
        surfacePool = new VtSurfacePool(width, height, 16);
        if (!surfacePool.IsInitialized)
        {
            ReleaseFormatDescription();
            return false;
        }

        OutputFormat = VtInterop.CvPixelFormatToHardwareFormat(surfacePool.PixelFormat);

        // Create decompression session
        if (!CreateDecompressionSession())
        {
            surfacePool.Dispose();
            surfacePool = null;
            ReleaseFormatDescription();
            return false;
        }

        IsInitialized = true;
        return true;
    }

    /// <summary>
    /// Creates a format description from codec private data.
    /// </summary>
    private bool CreateFormatDescription(ReadOnlySpan<byte> codecPrivateData)
    {
        if (codecPrivateData.IsEmpty)
            return false;

        if (videoCodecType == CmVideoCodecType.H264)
        {
            // Parse avcC to extract SPS/PPS
            return CreateH264FormatDescription(codecPrivateData);
        }
        else if (videoCodecType == CmVideoCodecType.Hevc)
        {
            // Parse hvcC to extract VPS/SPS/PPS
            return CreateHevcFormatDescription(codecPrivateData);
        }

        return false;
    }

    /// <summary>
    /// Creates H.264 format description from avcC box.
    /// </summary>
    private bool CreateH264FormatDescription(ReadOnlySpan<byte> avcC)
    {
        // Minimal avcC parsing: version(1) + profile(1) + compat(1) + level(1) +
        // lengthSize(1) + numSPS(1) + SPS... + numPPS(1) + PPS...
        if (avcC.Length < 8)
            return false;

        int offset = 5;
        int numSps = avcC[offset++] & 0x1F;
        if (numSps == 0) return false;

        // Extract SPS
        var parameterSets = new List<byte[]>();
        var parameterSetSizes = new List<nuint>();

        for (int i = 0; i < numSps && offset + 2 <= avcC.Length; i++)
        {
            int spsLen = (avcC[offset] << 8) | avcC[offset + 1];
            offset += 2;
            if (offset + spsLen > avcC.Length) return false;

            byte[] sps = avcC.Slice(offset, spsLen).ToArray();
            parameterSets.Add(sps);
            parameterSetSizes.Add((nuint)spsLen);
            offset += spsLen;
        }

        // Extract PPS
        if (offset >= avcC.Length) return false;
        int numPps = avcC[offset++];

        for (int i = 0; i < numPps && offset + 2 <= avcC.Length; i++)
        {
            int ppsLen = (avcC[offset] << 8) | avcC[offset + 1];
            offset += 2;
            if (offset + ppsLen > avcC.Length) return false;

            byte[] pps = avcC.Slice(offset, ppsLen).ToArray();
            parameterSets.Add(pps);
            parameterSetSizes.Add((nuint)ppsLen);
            offset += ppsLen;
        }

        // NAL unit header length (usually 4)
        int nalHeaderLength = (avcC[4] & 0x03) + 1;

        // Pin arrays and create format description
        var handles = new GCHandle[parameterSets.Count];
        var pointers = stackalloc byte*[parameterSets.Count];
        var sizes = stackalloc nuint[parameterSets.Count];

        try
        {
            for (int i = 0; i < parameterSets.Count; i++)
            {
                handles[i] = GCHandle.Alloc(parameterSets[i], GCHandleType.Pinned);
                pointers[i] = (byte*)handles[i].AddrOfPinnedObject();
                sizes[i] = parameterSetSizes[i];
            }

            VtStatus status = VtInterop.CMVideoFormatDescriptionCreateFromH264ParameterSets(
                0,
                (nuint)parameterSets.Count,
                pointers,
                sizes,
                nalHeaderLength,
                out formatDescription);

            return status == VtStatus.Success && formatDescription != 0;
        }
        finally
        {
            for (int i = 0; i < handles.Length; i++)
            {
                if (handles[i].IsAllocated)
                    handles[i].Free();
            }
        }
    }

    /// <summary>
    /// Creates HEVC format description from hvcC box.
    /// </summary>
    private bool CreateHevcFormatDescription(ReadOnlySpan<byte> hvcC)
    {
        // hvcC structure is more complex - parse NAL unit arrays
        if (hvcC.Length < 23)
            return false;

        // Skip to NAL unit arrays (offset 22)
        int offset = 22;
        int numArrays = hvcC[offset++];

        var parameterSets = new List<byte[]>();
        var parameterSetSizes = new List<nuint>();

        for (int arr = 0; arr < numArrays && offset + 3 <= hvcC.Length; arr++)
        {
            // byte arrayCompleteness = (byte)(hvcC[offset] >> 7);
            // byte nalUnitType = (byte)(hvcC[offset] & 0x3F);
            offset++;

            int numNalus = (hvcC[offset] << 8) | hvcC[offset + 1];
            offset += 2;

            for (int n = 0; n < numNalus && offset + 2 <= hvcC.Length; n++)
            {
                int naluLen = (hvcC[offset] << 8) | hvcC[offset + 1];
                offset += 2;
                if (offset + naluLen > hvcC.Length) break;

                byte[] nalu = hvcC.Slice(offset, naluLen).ToArray();
                parameterSets.Add(nalu);
                parameterSetSizes.Add((nuint)naluLen);
                offset += naluLen;
            }
        }

        if (parameterSets.Count == 0)
            return false;

        // NAL unit length size (usually 4)
        int nalLengthSize = (hvcC[21] & 0x03) + 1;

        // Pin arrays and create format description
        var handles = new GCHandle[parameterSets.Count];
        var pointers = stackalloc byte*[parameterSets.Count];
        var sizes = stackalloc nuint[parameterSets.Count];

        try
        {
            for (int i = 0; i < parameterSets.Count; i++)
            {
                handles[i] = GCHandle.Alloc(parameterSets[i], GCHandleType.Pinned);
                pointers[i] = (byte*)handles[i].AddrOfPinnedObject();
                sizes[i] = parameterSetSizes[i];
            }

            VtStatus status = VtInterop.CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                0,
                (nuint)parameterSets.Count,
                pointers,
                sizes,
                nalLengthSize,
                0,  // No extensions
                out formatDescription);

            return status == VtStatus.Success && formatDescription != 0;
        }
        finally
        {
            for (int i = 0; i < handles.Length; i++)
            {
                if (handles[i].IsAllocated)
                    handles[i].Free();
            }
        }
    }

    /// <summary>
    /// Creates the decompression session.
    /// </summary>
    private bool CreateDecompressionSession()
    {
        // Create output callback
        outputCallback = OnFrameDecoded;
        callbackHandle = GCHandle.Alloc(this);

        var callbackRecord = new VtInterop.VtDecompressionOutputCallbackRecord
        {
            DecompressionOutputCallback = Marshal.GetFunctionPointerForDelegate(outputCallback),
            DecompressionOutputRefCon = GCHandle.ToIntPtr(callbackHandle)
        };

        // Create decoder specification (prefer hardware)
        nint decoderSpec = VtInterop.CFDictionaryCreateMutable(0, 1, 0, 0);

        // Create destination image buffer attributes
        nint destAttribs = VtInterop.CFDictionaryCreateMutable(0, 2, 0, 0);

        VtStatus status = VtInterop.VTDecompressionSessionCreate(
            0,
            formatDescription,
            decoderSpec,
            destAttribs,
            &callbackRecord,
            out decompressionSession);

        VtInterop.CFRelease(decoderSpec);
        VtInterop.CFRelease(destAttribs);

        if (status != VtStatus.Success)
        {
            if (callbackHandle.IsAllocated)
                callbackHandle.Free();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Callback when a frame is decoded.
    /// </summary>
    private static void OnFrameDecoded(
        nint decompressionOutputRefCon,
        nint sourceFrameRefCon,
        VtStatus status,
        VtDecodeInfoFlags infoFlags,
        nint imageBuffer,
        CmTime presentationTimeStamp,
        CmTime presentationDuration)
    {
        if (status != VtStatus.Success || imageBuffer == 0)
            return;

        var handle = GCHandle.FromIntPtr(decompressionOutputRefCon);
        if (!handle.IsAllocated) return;

        var decoder = handle.Target as VtVideoDecoder;
        if (decoder == null || decoder.isDisposed) return;

        // Retain the image buffer since we're storing it
        VtInterop.CVPixelBufferRetain(imageBuffer);

        // Get dimensions
        int bufferWidth = (int)VtInterop.CVPixelBufferGetWidth(imageBuffer);
        int bufferHeight = (int)VtInterop.CVPixelBufferGetHeight(imageBuffer);

        // Get format
        var pixelFormat = VtInterop.CVPixelBufferGetPixelFormatType(imageBuffer);
        var hwFormat = VtInterop.CvPixelFormatToHardwareFormat(pixelFormat);

        var frame = new HardwareDecodedFrame(
            surfaceHandle: imageBuffer,
            arraySliceIndex: 0,
            width: bufferWidth,
            height: bufferHeight,
            format: hwFormat,
            presentationTimestamp: presentationTimeStamp.ToMicroseconds(),
            isKeyframe: false);  // Not easily determinable from callback

        decoder.decodedFrames.Enqueue(frame);
    }

    /// <summary>
    /// Submits a compressed frame for decoding.
    /// </summary>
    public bool SubmitFrame(ReadOnlySpan<byte> data, long presentationTimestamp, bool isKeyframe)
    {
        if (!IsInitialized || decompressionSession == 0)
            return false;

        // Create block buffer from data
        nint blockBuffer;
        fixed (byte* pData = data)
        {
            VtStatus status = VtInterop.CMBlockBufferCreateWithMemoryBlock(
                0,
                pData,
                (nuint)data.Length,
                0,  // kCFAllocatorNull - don't deallocate
                0,
                0,
                (nuint)data.Length,
                0,
                out blockBuffer);

            if (status != VtStatus.Success || blockBuffer == 0)
                return false;
        }

        // Create timing info
        var timing = new VtInterop.CmSampleTimingInfo
        {
            PresentationTimeStamp = CmTime.FromSeconds((double)presentationTimestamp / 1_000_000),
            DecodeTimeStamp = CmTime.Invalid,
            Duration = CmTime.Invalid
        };

        // Create sample buffer
        nuint sampleSize = (nuint)data.Length;
        VtStatus createStatus = VtInterop.CMSampleBufferCreate(
            0,
            blockBuffer,
            true,
            0,
            0,
            formatDescription,
            1,
            1,
            &timing,
            1,
            &sampleSize,
            out nint sampleBuffer);

        VtInterop.CFRelease(blockBuffer);

        if (createStatus != VtStatus.Success || sampleBuffer == 0)
            return false;

        // Decode frame
        VtDecodeFrameFlags flags = VtDecodeFrameFlags.EnableAsynchronousDecompression;
        VtStatus decodeStatus = VtInterop.VTDecompressionSessionDecodeFrame(
            decompressionSession,
            sampleBuffer,
            flags,
            0,
            out _);

        VtInterop.CFRelease(sampleBuffer);

        return decodeStatus == VtStatus.Success;
    }

    /// <summary>
    /// Retrieves a decoded frame if available.
    /// </summary>
    public bool TryGetDecodedFrame(out HardwareDecodedFrame frame)
    {
        return decodedFrames.TryDequeue(out frame);
    }

    /// <summary>
    /// Flushes the decoder pipeline.
    /// </summary>
    public void Flush()
    {
        if (decompressionSession != 0)
        {
            VtInterop.VTDecompressionSessionFinishDelayedFrames(decompressionSession);
            VtInterop.VTDecompressionSessionWaitForAsynchronousFrames(decompressionSession);
        }
    }

    /// <summary>
    /// Resets the decoder state.
    /// </summary>
    public void Reset()
    {
        // Clear queued frames (release CVPixelBuffers)
        while (decodedFrames.TryDequeue(out var frame))
        {
            if (frame.SurfaceHandle != 0)
                VtInterop.CVPixelBufferRelease(frame.SurfaceHandle);
        }
    }

    /// <summary>
    /// Releases the format description.
    /// </summary>
    private void ReleaseFormatDescription()
    {
        if (formatDescription != 0)
        {
            VtInterop.CFRelease(formatDescription);
            formatDescription = 0;
        }
    }

    /// <summary>
    /// Disposes the decoder.
    /// </summary>
    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;

        Reset();

        if (decompressionSession != 0)
        {
            VtInterop.VTDecompressionSessionInvalidate(decompressionSession);
            VtInterop.CFRelease(decompressionSession);
            decompressionSession = 0;
        }

        if (callbackHandle.IsAllocated)
            callbackHandle.Free();

        ReleaseFormatDescription();
        surfacePool?.Dispose();
    }
}

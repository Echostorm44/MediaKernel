// VA-API Video Decoder
// Hardware-accelerated video decoding using VA-API

using System.Collections.Concurrent;

namespace MediaKernel.Core.Hardware.VaApi;

/// <summary>
/// VA-API hardware video decoder.
/// </summary>
public sealed unsafe class VaApiVideoDecoder : IHardwareVideoDecoder
{
    private readonly nint display;
    private readonly string codecId;
    private readonly VaProfile vaProfile;

    private uint configId = VaConfig.InvalidId;
    private uint contextId = VaContext.InvalidId;
    private VaApiSurfacePool? surfacePool;
    private readonly ConcurrentQueue<HardwareDecodedFrame> decodedFrames = new();
    private bool isDisposed;

    private int width;
    private int height;

    /// <summary>
    /// Creates a new VA-API video decoder.
    /// </summary>
    /// <param name="display">VA display handle.</param>
    /// <param name="codecId">Codec ID (e.g., "h264", "hevc").</param>
    /// <param name="profile">H.264/HEVC profile for codec selection.</param>
    /// <param name="bitDepth">Bit depth for 10/12-bit content.</param>
    public VaApiVideoDecoder(nint display, string codecId, int profile = 0, int bitDepth = 8)
    {
        this.display = display;
        this.codecId = codecId;
        vaProfile = VaApiInterop.CodecIdToProfile(codecId, profile, bitDepth);
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
    /// Gets the VA profile being used.
    /// </summary>
    public VaProfile Profile => vaProfile;

    /// <summary>
    /// Gets the VA config ID.
    /// </summary>
    public uint ConfigId => configId;

    /// <summary>
    /// Gets the VA context ID.
    /// </summary>
    public uint ContextId => contextId;

    /// <summary>
    /// Initializes the decoder with stream parameters.
    /// </summary>
    public bool Initialize(int width, int height, ReadOnlySpan<byte> codecPrivateData)
    {
        if (IsInitialized) return true;
        if (display == 0 || vaProfile == VaProfile.None) return false;

        this.width = width;
        this.height = height;

        // Determine RT format based on profile
        VaRtFormat rtFormat = VaApiInterop.GetProfileBitDepth(vaProfile) > 8
            ? VaRtFormat.Yuv420_10Bpp
            : VaRtFormat.Yuv420;

        OutputFormat = rtFormat == VaRtFormat.Yuv420_10Bpp
            ? HardwarePixelFormat.P010
            : HardwarePixelFormat.Nv12;

        // Create config with RT format attribute
        VaConfigAttrib attrib;
        attrib.Type = VaConfigAttribType.RtFormat;
        attrib.Value = (uint)rtFormat;

        VaStatus status = VaApiInterop.vaCreateConfig(
            display, vaProfile, VaEntrypoint.Vld,
            &attrib, 1, out configId);

        if (status != VaStatus.Success)
        {
            // Try without attributes
            status = VaApiInterop.vaCreateConfig(
                display, vaProfile, VaEntrypoint.Vld,
                null, 0, out configId);

            if (status != VaStatus.Success)
                return false;
        }

        // Create surface pool (16 surfaces is typical for H.264/HEVC)
        int surfaceCount = codecId.ToLowerInvariant() switch
        {
            "h265" or "hevc" => 20,  // HEVC can have more references
            "av1" => 18,             // AV1 can have up to 8 references + current
            _ => 16,                 // H.264 default
        };

        surfacePool = new VaApiSurfacePool(display, width, height, surfaceCount, rtFormat);
        if (!surfacePool.Initialize())
        {
            VaApiInterop.vaDestroyConfig(display, configId);
            configId = VaConfig.InvalidId;
            return false;
        }

        // Create decode context
        fixed (uint* pSurfaces = surfacePool.Surfaces)
        {
            status = VaApiInterop.vaCreateContext(
                display, configId, width, height,
                0,  // VA_PROGRESSIVE
                pSurfaces, surfacePool.SurfaceCount, out contextId);
        }

        if (status != VaStatus.Success)
        {
            surfacePool.Dispose();
            surfacePool = null;
            VaApiInterop.vaDestroyConfig(display, configId);
            configId = VaConfig.InvalidId;
            return false;
        }

        IsInitialized = true;
        return true;
    }

    /// <summary>
    /// Submits a compressed frame for decoding.
    /// Note: Actual slice/picture parameter submission is codec-specific.
    /// </summary>
    public bool SubmitFrame(ReadOnlySpan<byte> data, long presentationTimestamp, bool isKeyframe)
    {
        if (!IsInitialized || surfacePool == null)
            return false;

        // Acquire a surface
        int surfaceIndex = surfacePool.AcquireSurface(out nint surfaceHandle, out _);
        if (surfaceIndex < 0)
            return false;

        uint surfaceId = (uint)surfaceHandle;

        // Begin picture
        VaStatus status = VaApiInterop.vaBeginPicture(display, contextId, surfaceId);
        if (status != VaStatus.Success)
        {
            surfacePool.ReleaseSurface(surfaceIndex);
            return false;
        }

        // Create and submit slice data buffer
        uint sliceDataBuffer;
        fixed (byte* pData = data)
        {
            status = VaApiInterop.vaCreateBuffer(
                display, contextId, VaBufferType.SliceDataBufferType,
                (uint)data.Length, 1, pData, out sliceDataBuffer);
        }

        if (status != VaStatus.Success)
        {
            VaApiInterop.vaEndPicture(display, contextId);
            surfacePool.ReleaseSurface(surfaceIndex);
            return false;
        }

        // Render the buffer
        status = VaApiInterop.vaRenderPicture(display, contextId, &sliceDataBuffer, 1);
        VaApiInterop.vaDestroyBuffer(display, sliceDataBuffer);

        if (status != VaStatus.Success)
        {
            VaApiInterop.vaEndPicture(display, contextId);
            surfacePool.ReleaseSurface(surfaceIndex);
            return false;
        }

        // End picture
        status = VaApiInterop.vaEndPicture(display, contextId);
        if (status != VaStatus.Success)
        {
            surfacePool.ReleaseSurface(surfaceIndex);
            return false;
        }

        // Queue the decoded frame
        var frame = new HardwareDecodedFrame(
            surfaceHandle: surfaceHandle,
            arraySliceIndex: 0,
            width: width,
            height: height,
            format: OutputFormat,
            presentationTimestamp: presentationTimestamp,
            isKeyframe: isKeyframe,
            surfacePool: surfacePool,
            surfaceIndex: surfaceIndex);

        decodedFrames.Enqueue(frame);
        return true;
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
        // VA-API doesn't have a dedicated flush operation
        // Just ensure any pending frames are synced
        if (surfacePool != null)
        {
            for (int i = 0; i < surfacePool.SurfaceCount; i++)
            {
                surfacePool.SyncSurface(i);
            }
        }
    }

    /// <summary>
    /// Resets the decoder state.
    /// </summary>
    public void Reset()
    {
        // Clear queued frames
        while (decodedFrames.TryDequeue(out var frame))
        {
            frame.Dispose();
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

        if (display != 0)
        {
            if (contextId != VaContext.InvalidId)
            {
                VaApiInterop.vaDestroyContext(display, contextId);
            }

            if (configId != VaConfig.InvalidId)
            {
                VaApiInterop.vaDestroyConfig(display, configId);
            }
        }

        surfacePool?.Dispose();
    }
}

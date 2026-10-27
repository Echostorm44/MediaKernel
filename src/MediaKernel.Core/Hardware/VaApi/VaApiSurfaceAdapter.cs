// VaApiSurfaceAdapter - Zero-copy surface sharing for Linux VA-API hardware decoding
// Exports surfaces via DRM PRIME DMA-BUF for GPU compositing

using System;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Hardware.VaApi;

/// <summary>
/// Adapter for zero-copy sharing of VA-API decoder surfaces via DRM PRIME DMA-BUF.
/// Enables hardware decoded frames to be rendered by EGL/Vulkan/Wayland without CPU copies.
/// </summary>
public sealed unsafe class VaApiSurfaceAdapter : IHardwareSurfaceAdapter
{
    private nint vaDisplay;
    private int drmFd;
    private bool isInitialized;
    private bool isDisposed;

    // DRM format FourCCs
    public const uint DrmFormatNv12 = 0x3231564E; // 'NV12'
    public const uint DrmFormatP010 = 0x30313050; // 'P010'
    public const uint DrmFormatYuv420 = 0x32315559; // 'YU12' (I420)
    public const uint DrmFormatArgb8888 = 0x34325241; // 'AR24'
    public const uint DrmFormatXrgb8888 = 0x34325258; // 'XR24'

    // DRM PRIME export constants
    public const uint VaExportSurfaceReadOnly = 0x0001;
    public const uint VaExportSurfaceWriteOnly = 0x0002;
    public const uint VaExportSurfaceReadWrite = 0x0003;
    public const uint VaExportSurfaceSeparateLayers = 0x0004;
    public const uint VaExportSurfaceComposedLayers = 0x0008;

    /// <inheritdoc/>
    public GpuPlatform Platform => GpuPlatform.VaApiDmaBuf;

    /// <inheritdoc/>
    public bool IsInitialized => isInitialized && !isDisposed;

    /// <summary>
    /// Initializes the adapter with an existing VA display.
    /// </summary>
    /// <param name="display">VA display handle from the decoder factory.</param>
    /// <param name="drmFileDescriptor">DRM render node file descriptor.</param>
    /// <returns>True if initialization succeeded.</returns>
    public bool Initialize(nint display, int drmFileDescriptor)
    {
        if (display == 0 || drmFileDescriptor < 0)
            return false;

        vaDisplay = display;
        drmFd = drmFileDescriptor;
        isInitialized = true;
        return true;
    }

    /// <inheritdoc/>
    public bool TryGetSharedSurface(in HardwareDecodedFrame frame, out GpuSharedSurface sharedSurface)
    {
        sharedSurface = default;

        if (!isInitialized || isDisposed)
            return false;

        if (frame.SurfaceHandle == 0)
            return false;

        // Export the VA surface to DMA-BUF
        uint surfaceId = (uint)frame.SurfaceHandle;
        if (!TryExportSurface(surfaceId, out var exportInfo))
            return false;

        // Get DRM format based on pixel format
        uint drmFormat = frame.Format switch
        {
            HardwarePixelFormat.Nv12 => DrmFormatNv12,
            HardwarePixelFormat.P010 => DrmFormatP010,
            _ => DrmFormatNv12, // Default to NV12
        };

        sharedSurface = new GpuSharedSurface(
            sharedHandle: exportInfo.DmaBufFd,
            width: frame.Width,
            height: frame.Height,
            format: frame.Format,
            platform: GpuPlatform.VaApiDmaBuf,
            textureHandle: (nint)surfaceId,
            drmModifier: exportInfo.DrmModifier,
            drmFormat: drmFormat,
            stride: (int)exportInfo.Pitch,
            ownerAdapter: this,
            surfaceIndex: frame.ArraySliceIndex);

        return true;
    }

    /// <summary>
    /// Exports a VA surface to a DMA-BUF file descriptor.
    /// </summary>
    /// <param name="surfaceId">VA surface ID.</param>
    /// <param name="exportInfo">Output: export information including fd and modifier.</param>
    /// <returns>True if export succeeded.</returns>
    public bool TryExportSurface(uint surfaceId, out VaSurfaceExportInfo exportInfo)
    {
        exportInfo = default;

        if (!isInitialized || vaDisplay == 0)
            return false;

        VaSurfaceAttribExternalBuffers extBuf = default;
        VaStatus status = vaExportSurfaceHandle(
            vaDisplay,
            surfaceId,
            VaExportSurfaceReadWrite | VaExportSurfaceComposedLayers,
            out exportInfo);

        return status == VaStatus.Success;
    }

    /// <summary>
    /// Syncs the surface to ensure GPU operations are complete.
    /// Call before accessing the DMA-BUF from another context.
    /// </summary>
    /// <param name="surfaceId">VA surface ID.</param>
    /// <returns>True if sync succeeded.</returns>
    public bool SyncSurface(uint surfaceId)
    {
        if (!isInitialized || vaDisplay == 0)
            return false;

        VaStatus status = vaSyncSurface(vaDisplay, surfaceId);
        return status == VaStatus.Success;
    }

    /// <inheritdoc/>
    public void ReleaseSurface(int surfaceIndex)
    {
        // The DMA-BUF file descriptor should be closed by the consumer
        // The VA surface itself is managed by the decoder's surface pool
    }

    /// <inheritdoc/>
    public nint GetRenderingContext() => 0; // VA-API doesn't have a separate rendering context

    /// <inheritdoc/>
    public nint GetDeviceHandle() => vaDisplay;

    /// <summary>
    /// Gets the DRM file descriptor for this adapter.
    /// </summary>
    public int DrmFileDescriptor => drmFd;

    public void Dispose()
    {
        if (isDisposed)
            return;

        isDisposed = true;
        vaDisplay = 0;
        drmFd = -1;
        isInitialized = false;
    }

    #region P/Invoke Declarations

    private const string LibVa = "libva.so.2";

    [DllImport(LibVa, EntryPoint = "vaExportSurfaceHandle")]
    private static extern VaStatus vaExportSurfaceHandle(
        nint dpy,
        uint surfaceId,
        uint flags,
        out VaSurfaceExportInfo descriptor);

    [DllImport(LibVa, EntryPoint = "vaSyncSurface")]
    private static extern VaStatus vaSyncSurface(nint dpy, uint surfaceId);

    #endregion
}

/// <summary>
/// Information about an exported VA surface DMA-BUF.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VaSurfaceExportInfo
{
    /// <summary>DMA-BUF file descriptor.</summary>
    public nint DmaBufFd;

    /// <summary>DRM format modifier for tiled/compressed layouts.</summary>
    public ulong DrmModifier;

    /// <summary>Number of layers (planes) in the surface.</summary>
    public uint NumLayers;

    /// <summary>Number of objects (DMA-BUFs) for the surface.</summary>
    public uint NumObjects;

    /// <summary>Surface width in pixels.</summary>
    public uint Width;

    /// <summary>Surface height in pixels.</summary>
    public uint Height;

    /// <summary>DRM format fourcc.</summary>
    public uint DrmFourcc;

    /// <summary>Pitch (stride) for the first plane.</summary>
    public uint Pitch;

    /// <summary>Offset to the first plane data.</summary>
    public uint Offset;

    /// <summary>Layer information for multi-planar formats.</summary>
    public VaSurfaceExportLayerInfo Layer0;
    public VaSurfaceExportLayerInfo Layer1;
    public VaSurfaceExportLayerInfo Layer2;
    public VaSurfaceExportLayerInfo Layer3;
}

/// <summary>
/// Layer information for exported surfaces.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VaSurfaceExportLayerInfo
{
    /// <summary>DRM format fourcc for this layer.</summary>
    public uint DrmFormat;

    /// <summary>Number of planes in this layer.</summary>
    public uint NumPlanes;

    /// <summary>Object indices for each plane.</summary>
    public uint ObjectIndex0;
    public uint ObjectIndex1;
    public uint ObjectIndex2;
    public uint ObjectIndex3;

    /// <summary>Offsets for each plane.</summary>
    public uint Offset0;
    public uint Offset1;
    public uint Offset2;
    public uint Offset3;

    /// <summary>Pitches for each plane.</summary>
    public uint Pitch0;
    public uint Pitch1;
    public uint Pitch2;
    public uint Pitch3;
}

/// <summary>
/// External buffer attributes for VA surface import.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VaSurfaceAttribExternalBuffers
{
    public uint PixelFormat;
    public uint Width;
    public uint Height;
    public uint DataSize;
    public uint NumPlanes;
    public fixed uint Pitches[4];
    public fixed uint Offsets[4];
    // Can't use fixed nint[], so use explicit fields
    public nint Buffer0;
    public nint Buffer1;
    public nint Buffer2;
    public nint Buffer3;
    public nint Buffer4;
    public nint Buffer5;
    public nint Buffer6;
    public nint Buffer7;
    public nint Buffer8;
    public nint Buffer9;
    public nint Buffer10;
    public nint Buffer11;
    public nint Buffer12;
    public nint Buffer13;
    public nint Buffer14;
    public nint Buffer15;
    public uint NumBuffers;
    public uint Flags;
    public nint PrivateData;
}

// VtSurfaceAdapter - Zero-copy surface sharing for macOS/iOS VideoToolbox hardware decoding
// Uses IOSurface for Metal/OpenGL GPU interop

using System;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Hardware.VideoToolbox;

/// <summary>
/// Adapter for zero-copy sharing of VideoToolbox decoder surfaces via IOSurface.
/// Enables hardware decoded CVPixelBuffers to be rendered by Metal/OpenGL without CPU copies.
/// </summary>
public sealed unsafe class VtSurfaceAdapter : IHardwareSurfaceAdapter
{
    private nint metalDevice;
    private bool isInitialized;
    private bool isDisposed;

    // IOSurface pixel format constants
    public const uint IOSurfacePixelFormatNv12 = 0x34323076; // '420v' - NV12
    public const uint IOSurfacePixelFormatBgra = 0x42475241; // 'BGRA'
    public const uint IOSurfacePixelFormatP010 = 0x78343230; // 'x420' - P010 equivalent

    /// <inheritdoc/>
    public GpuPlatform Platform => GpuPlatform.VideoToolboxMetal;

    /// <inheritdoc/>
    public bool IsInitialized => isInitialized && !isDisposed;

    /// <summary>
    /// Initializes the adapter with an optional Metal device.
    /// </summary>
    /// <param name="device">MTLDevice handle (optional - will use default if 0).</param>
    /// <returns>True if initialization succeeded.</returns>
    public bool Initialize(nint device = 0)
    {
        if (device == 0)
        {
            // Get the default Metal device
            device = MTLCreateSystemDefaultDevice();
        }

        if (device == 0)
            return false;

        metalDevice = device;
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

        // frame.SurfaceHandle is a CVPixelBufferRef
        nint pixelBuffer = frame.SurfaceHandle;

        // Get the IOSurfaceRef from the CVPixelBuffer
        nint ioSurface = CVPixelBufferGetIOSurface(pixelBuffer);
        if (ioSurface == 0)
            return false;

        // Get surface properties
        nint width = IOSurfaceGetWidth(ioSurface);
        nint height = IOSurfaceGetHeight(ioSurface);
        uint pixelFormat = IOSurfaceGetPixelFormat(ioSurface);
        nint bytesPerRow = IOSurfaceGetBytesPerRow(ioSurface);

        sharedSurface = new GpuSharedSurface(
            sharedHandle: ioSurface,
            width: (int)width,
            height: (int)height,
            format: frame.Format,
            platform: GpuPlatform.VideoToolboxMetal,
            textureHandle: pixelBuffer,
            stride: (int)bytesPerRow,
            ownerAdapter: this,
            surfaceIndex: frame.ArraySliceIndex);

        return true;
    }

    /// <summary>
    /// Creates a Metal texture from an IOSurface for rendering.
    /// </summary>
    /// <param name="ioSurface">IOSurfaceRef handle.</param>
    /// <param name="planeIndex">Plane index (0 for Y, 1 for UV in NV12).</param>
    /// <param name="pixelFormat">Metal pixel format.</param>
    /// <returns>MTLTexture handle, or 0 on failure.</returns>
    public nint CreateMetalTextureFromIOSurface(nint ioSurface, int planeIndex, uint pixelFormat)
    {
        if (!isInitialized || metalDevice == 0 || ioSurface == 0)
            return 0;

        // Get plane dimensions
        nint width = IOSurfaceGetWidthOfPlane(ioSurface, (nuint)planeIndex);
        nint height = IOSurfaceGetHeightOfPlane(ioSurface, (nuint)planeIndex);

        if (width == 0 || height == 0)
            return 0;

        // Create texture descriptor
        nint descriptor = objc_msgSend_new(objc_getClass("MTLTextureDescriptor"));
        if (descriptor == 0)
            return 0;

        // Configure descriptor
        objc_msgSend_void_uint(descriptor, sel_registerName("setTextureType:"), 2); // MTLTextureType2D = 2
        objc_msgSend_void_uint(descriptor, sel_registerName("setPixelFormat:"), pixelFormat);
        objc_msgSend_void_nint(descriptor, sel_registerName("setWidth:"), width);
        objc_msgSend_void_nint(descriptor, sel_registerName("setHeight:"), height);
        objc_msgSend_void_uint(descriptor, sel_registerName("setUsage:"), 1); // MTLTextureUsageShaderRead = 1

        // Create texture from IOSurface
        nint texture = objc_msgSend_texture(
            metalDevice,
            sel_registerName("newTextureWithDescriptor:iosurface:plane:"),
            descriptor,
            ioSurface,
            (nuint)planeIndex);

        // Release descriptor
        objc_msgSend_void(descriptor, sel_registerName("release"));

        return texture;
    }

    /// <summary>
    /// Gets the number of planes in a CVPixelBuffer.
    /// </summary>
    public int GetPlaneCount(nint pixelBuffer)
    {
        if (pixelBuffer == 0)
            return 0;

        return (int)CVPixelBufferGetPlaneCount(pixelBuffer);
    }

    /// <summary>
    /// Gets the IOSurface ID for use with OpenGL texture caching.
    /// </summary>
    public uint GetIOSurfaceId(nint ioSurface)
    {
        if (ioSurface == 0)
            return 0;

        return IOSurfaceGetID(ioSurface);
    }

    /// <summary>
    /// Locks the IOSurface for CPU access (use sparingly - defeats zero-copy).
    /// </summary>
    public bool LockSurface(nint ioSurface, bool readOnly)
    {
        if (ioSurface == 0)
            return false;

        uint options = readOnly ? 1u : 0u; // kIOSurfaceLockReadOnly = 1
        int result = IOSurfaceLock(ioSurface, options, null);
        return result == 0;
    }

    /// <summary>
    /// Unlocks the IOSurface after CPU access.
    /// </summary>
    public bool UnlockSurface(nint ioSurface, bool readOnly)
    {
        if (ioSurface == 0)
            return false;

        uint options = readOnly ? 1u : 0u;
        int result = IOSurfaceUnlock(ioSurface, options, null);
        return result == 0;
    }

    /// <inheritdoc/>
    public void ReleaseSurface(int surfaceIndex)
    {
        // IOSurface is managed by the CVPixelBuffer which is owned by the decoder's surface pool
    }

    /// <inheritdoc/>
    public nint GetRenderingContext() => 0; // Rendering context is per-command-queue in Metal

    /// <inheritdoc/>
    public nint GetDeviceHandle() => metalDevice;

    public void Dispose()
    {
        if (isDisposed)
            return;

        isDisposed = true;

        // Don't release metalDevice - it may be shared
        metalDevice = 0;
        isInitialized = false;
    }

    #region P/Invoke and Objective-C Runtime

    private const string CoreVideoLib = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    private const string IOSurfaceLib = "/System/Library/Frameworks/IOSurface.framework/IOSurface";
    private const string MetalLib = "/System/Library/Frameworks/Metal.framework/Metal";
    private const string ObjCLib = "/usr/lib/libobjc.A.dylib";

    // CoreVideo
    [DllImport(CoreVideoLib)]
    private static extern nint CVPixelBufferGetIOSurface(nint pixelBuffer);

    [DllImport(CoreVideoLib)]
    private static extern nint CVPixelBufferGetPlaneCount(nint pixelBuffer);

    // IOSurface
    [DllImport(IOSurfaceLib)]
    private static extern nint IOSurfaceGetWidth(nint surface);

    [DllImport(IOSurfaceLib)]
    private static extern nint IOSurfaceGetHeight(nint surface);

    [DllImport(IOSurfaceLib)]
    private static extern nint IOSurfaceGetBytesPerRow(nint surface);

    [DllImport(IOSurfaceLib)]
    private static extern uint IOSurfaceGetPixelFormat(nint surface);

    [DllImport(IOSurfaceLib)]
    private static extern uint IOSurfaceGetID(nint surface);

    [DllImport(IOSurfaceLib)]
    private static extern nint IOSurfaceGetWidthOfPlane(nint surface, nuint planeIndex);

    [DllImport(IOSurfaceLib)]
    private static extern nint IOSurfaceGetHeightOfPlane(nint surface, nuint planeIndex);

    [DllImport(IOSurfaceLib)]
    private static extern int IOSurfaceLock(nint surface, uint options, uint* seed);

    [DllImport(IOSurfaceLib)]
    private static extern int IOSurfaceUnlock(nint surface, uint options, uint* seed);

    // Metal
    [DllImport(MetalLib)]
    private static extern nint MTLCreateSystemDefaultDevice();

    // Objective-C runtime
    [DllImport(ObjCLib, EntryPoint = "objc_getClass")]
    private static extern nint objc_getClass(string name);

    [DllImport(ObjCLib, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(string name);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_new(nint receiver);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void(nint receiver, nint selector);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_uint(nint receiver, nint selector, uint value);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_nint(nint receiver, nint selector, nint value);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_texture(
        nint receiver,
        nint selector,
        nint descriptor,
        nint ioSurface,
        nuint plane);

    #endregion
}

/// <summary>
/// Metal pixel format constants for texture creation.
/// </summary>
public static class MtlPixelFormat
{
    public const uint R8Unorm = 10;
    public const uint Rg8Unorm = 30;
    public const uint Rgba8Unorm = 70;
    public const uint Bgra8Unorm = 80;
    public const uint R16Unorm = 20;
    public const uint Rg16Unorm = 60;

    /// <summary>
    /// Gets the Metal pixel format for a given plane of NV12 data.
    /// </summary>
    public static uint GetNv12PlaneFormat(int planeIndex)
    {
        return planeIndex == 0 ? R8Unorm : Rg8Unorm;
    }

    /// <summary>
    /// Gets the Metal pixel format for a given plane of P010 data.
    /// </summary>
    public static uint GetP010PlaneFormat(int planeIndex)
    {
        return planeIndex == 0 ? R16Unorm : Rg16Unorm;
    }
}

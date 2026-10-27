// HardwareSurfaceAdapter - Abstraction for zero-copy GPU surface presentation
// Enables hardware decoder surfaces to be rendered by Avalonia without CPU copies

using System;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Hardware
{
    /// <summary>
    /// Represents a GPU-shareable surface that can be rendered without CPU readback.
    /// Platform-specific implementations provide the actual sharing mechanism.
    /// </summary>
    public readonly struct GpuSharedSurface : IDisposable
    {
        /// <summary>Platform-specific shared handle (HANDLE on Windows, fd on Linux, IOSurfaceRef on macOS).</summary>
        public readonly nint SharedHandle;

        /// <summary>Width of the surface in pixels.</summary>
        public readonly int Width;

        /// <summary>Height of the surface in pixels.</summary>
        public readonly int Height;

        /// <summary>Pixel format of the surface.</summary>
        public readonly HardwarePixelFormat Format;

        /// <summary>Platform identifier for rendering API selection.</summary>
        public readonly GpuPlatform Platform;

        /// <summary>For D3D11: the texture resource itself for same-device sharing.</summary>
        public readonly nint TextureHandle;

        /// <summary>For D3D11: array slice index within texture array.</summary>
        public readonly int ArraySlice;

        /// <summary>For VA-API: DRM format modifier for tiled/compressed layouts.</summary>
        public readonly ulong DrmModifier;

        /// <summary>For VA-API: DRM format fourcc code.</summary>
        public readonly uint DrmFormat;

        /// <summary>Row stride in bytes (may be 0 if not applicable).</summary>
        public readonly int Stride;

        /// <summary>Reference to owning adapter for cleanup.</summary>
        private readonly IHardwareSurfaceAdapter? ownerAdapter;

        /// <summary>Index within the adapter's surface pool.</summary>
        private readonly int surfaceIndex;

        public GpuSharedSurface(
            nint sharedHandle,
            int width,
            int height,
            HardwarePixelFormat format,
            GpuPlatform platform,
            nint textureHandle = 0,
            int arraySlice = 0,
            ulong drmModifier = 0,
            uint drmFormat = 0,
            int stride = 0,
            IHardwareSurfaceAdapter? ownerAdapter = null,
            int surfaceIndex = -1)
        {
            SharedHandle = sharedHandle;
            Width = width;
            Height = height;
            Format = format;
            Platform = platform;
            TextureHandle = textureHandle;
            ArraySlice = arraySlice;
            DrmModifier = drmModifier;
            DrmFormat = drmFormat;
            Stride = stride;
            this.ownerAdapter = ownerAdapter;
            this.surfaceIndex = surfaceIndex;
        }

        /// <summary>
        /// Returns the surface to its pool for reuse.
        /// </summary>
        public void Dispose()
        {
            ownerAdapter?.ReleaseSurface(surfaceIndex);
        }
    }

    /// <summary>
    /// GPU platform identifiers for selecting the appropriate rendering path.
    /// </summary>
    public enum GpuPlatform
    {
        /// <summary>Unknown or unsupported platform.</summary>
        Unknown = 0,

        /// <summary>Windows Direct3D 11 with DXGI shared handles.</summary>
        D3D11 = 1,

        /// <summary>Linux VA-API with DRM PRIME DMA-BUF.</summary>
        VaApiDmaBuf = 2,

        /// <summary>macOS/iOS VideoToolbox with Metal via IOSurface.</summary>
        VideoToolboxMetal = 3,

        /// <summary>OpenGL with external memory extensions.</summary>
        OpenGLExternal = 4,

        /// <summary>Vulkan with external memory.</summary>
        VulkanExternal = 5,
    }

    /// <summary>
    /// Interface for hardware surface adapters that provide zero-copy GPU sharing.
    /// </summary>
    public interface IHardwareSurfaceAdapter : IDisposable
    {
        /// <summary>Gets the GPU platform this adapter supports.</summary>
        GpuPlatform Platform { get; }

        /// <summary>Gets whether this adapter is ready for use.</summary>
        bool IsInitialized { get; }

        /// <summary>
        /// Creates a shareable surface from a hardware decoded frame.
        /// </summary>
        /// <param name="frame">The hardware decoded frame.</param>
        /// <param name="sharedSurface">Output: the shareable surface.</param>
        /// <returns>True if the surface was successfully shared.</returns>
        bool TryGetSharedSurface(in HardwareDecodedFrame frame, out GpuSharedSurface sharedSurface);

        /// <summary>
        /// Releases a shared surface back to the pool.
        /// </summary>
        /// <param name="surfaceIndex">Index of the surface to release.</param>
        void ReleaseSurface(int surfaceIndex);

        /// <summary>
        /// Gets the native rendering context handle (ID3D11DeviceContext, EGLDisplay, MTLDevice, etc.).
        /// </summary>
        nint GetRenderingContext();

        /// <summary>
        /// Gets the native device handle (ID3D11Device, VADisplay, MTLDevice, etc.).
        /// </summary>
        nint GetDeviceHandle();
    }

    /// <summary>
    /// Result of a surface import operation for rendering.
    /// </summary>
    public readonly struct ImportedSurface : IDisposable
    {
        /// <summary>Platform-specific texture/image handle for rendering.</summary>
        public readonly nint TextureHandle;

        /// <summary>For OpenGL: the GL texture name.</summary>
        public readonly uint GlTextureName;

        /// <summary>For Vulkan: the VkImage handle.</summary>
        public readonly ulong VkImageHandle;

        /// <summary>Width of the imported surface.</summary>
        public readonly int Width;

        /// <summary>Height of the imported surface.</summary>
        public readonly int Height;

        /// <summary>Reference for cleanup.</summary>
        private readonly Action? disposeAction;

        public ImportedSurface(
            nint textureHandle,
            int width,
            int height,
            uint glTextureName = 0,
            ulong vkImageHandle = 0,
            Action? disposeAction = null)
        {
            TextureHandle = textureHandle;
            Width = width;
            Height = height;
            GlTextureName = glTextureName;
            VkImageHandle = vkImageHandle;
            this.disposeAction = disposeAction;
        }

        public void Dispose()
        {
            disposeAction?.Invoke();
        }
    }

    /// <summary>
    /// Interface for GPU renderers that can import shared surfaces.
    /// </summary>
    public interface IGpuSurfaceRenderer
    {
        /// <summary>Gets the supported GPU platform.</summary>
        GpuPlatform SupportedPlatform { get; }

        /// <summary>
        /// Imports a shared surface for rendering.
        /// </summary>
        /// <param name="surface">The shared surface from a hardware adapter.</param>
        /// <param name="imported">Output: the imported texture ready for rendering.</param>
        /// <returns>True if import succeeded.</returns>
        bool TryImportSurface(in GpuSharedSurface surface, out ImportedSurface imported);

        /// <summary>
        /// Renders the imported surface to the current render target.
        /// </summary>
        /// <param name="imported">The imported surface.</param>
        /// <param name="destX">Destination X coordinate.</param>
        /// <param name="destY">Destination Y coordinate.</param>
        /// <param name="destWidth">Destination width.</param>
        /// <param name="destHeight">Destination height.</param>
        void RenderSurface(in ImportedSurface imported, float destX, float destY, float destWidth, float destHeight);
    }
}

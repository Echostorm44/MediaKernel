// VA-API Surface Pool
// Manages VA surfaces for hardware decoding

using System.Runtime.InteropServices;

namespace MediaKernel.Core.Hardware.VaApi;

/// <summary>
/// Surface pool for VA-API hardware decoding.
/// Manages allocation and recycling of VA surfaces.
/// </summary>
public sealed unsafe class VaApiSurfacePool : IHardwareSurfacePool, IDisposable
{
    private readonly nint display;
    private readonly uint[] surfaces;
    private readonly bool[] surfaceInUse;
    private readonly SemaphoreSlim semaphore;
    private readonly object lockObj = new();
    private readonly int width;
    private readonly int height;
    private readonly VaRtFormat rtFormat;
    private bool isDisposed;

    /// <summary>
    /// Creates a new VA-API surface pool.
    /// </summary>
    /// <param name="display">VA display handle.</param>
    /// <param name="width">Surface width in pixels.</param>
    /// <param name="height">Surface height in pixels.</param>
    /// <param name="surfaceCount">Number of surfaces to allocate.</param>
    /// <param name="rtFormat">Render target format (default: YUV420).</param>
    public VaApiSurfacePool(nint display, int width, int height, int surfaceCount, VaRtFormat rtFormat = VaRtFormat.Yuv420)
    {
        this.display = display;
        this.width = width;
        this.height = height;
        this.rtFormat = rtFormat;

        surfaces = new uint[surfaceCount];
        surfaceInUse = new bool[surfaceCount];
        semaphore = new SemaphoreSlim(surfaceCount, surfaceCount);
    }

    /// <summary>
    /// Gets whether the pool has been initialized successfully.
    /// </summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Gets the total number of surfaces in the pool.
    /// </summary>
    public int SurfaceCount => surfaces.Length;

    /// <summary>
    /// Gets the number of currently available surfaces.
    /// </summary>
    public int AvailableSurfaceCount => semaphore.CurrentCount;

    /// <summary>
    /// Gets the VA surface IDs (for context creation).
    /// </summary>
    public ReadOnlySpan<uint> Surfaces => surfaces;

    /// <summary>
    /// Initializes the surface pool by allocating VA surfaces.
    /// </summary>
    public bool Initialize()
    {
        if (IsInitialized) return true;

        fixed (uint* pSurfaces = surfaces)
        {
            // Create surface attributes for NV12 format
            VaSurfaceAttrib attrib;
            attrib.Type = VaSurfaceAttribType.PixelFormat;
            attrib.Flags = VaSurfaceAttribFlags.Settable;
            attrib.Value = new VaGenericValue
            {
                Type = VaGenericValueType.Integer,
                IntValue = (int)(rtFormat == VaRtFormat.Yuv420_10Bpp ? VaFourcc.P010 : VaFourcc.Nv12)
            };

            VaStatus status = VaApiInterop.vaCreateSurfaces(
                display,
                (uint)rtFormat,
                (uint)width,
                (uint)height,
                pSurfaces,
                (uint)surfaces.Length,
                &attrib,
                1);

            if (status != VaStatus.Success)
            {
                // Try without attributes
                status = VaApiInterop.vaCreateSurfaces(
                    display,
                    (uint)rtFormat,
                    (uint)width,
                    (uint)height,
                    pSurfaces,
                    (uint)surfaces.Length,
                    null,
                    0);

                if (status != VaStatus.Success)
                    return false;
            }
        }

        IsInitialized = true;
        return true;
    }

    /// <summary>
    /// Acquires an available surface for decoding.
    /// </summary>
    public int AcquireSurface(out nint surfaceHandle, out int arraySliceIndex)
    {
        surfaceHandle = 0;
        arraySliceIndex = 0;

        if (!IsInitialized || isDisposed)
            return -1;

        if (!semaphore.Wait(TimeSpan.FromMilliseconds(100)))
            return -1;

        lock (lockObj)
        {
            for (int i = 0; i < surfaces.Length; i++)
            {
                if (!surfaceInUse[i])
                {
                    surfaceInUse[i] = true;
                    surfaceHandle = (nint)surfaces[i];
                    arraySliceIndex = 0;  // VA-API uses flat surfaces, not arrays
                    return i;
                }
            }
        }

        // Should not happen since semaphore was acquired
        semaphore.Release();
        return -1;
    }

    /// <summary>
    /// Returns a surface to the pool for reuse.
    /// </summary>
    public void ReleaseSurface(int surfaceIndex)
    {
        if (surfaceIndex < 0 || surfaceIndex >= surfaces.Length)
            return;

        lock (lockObj)
        {
            if (surfaceInUse[surfaceIndex])
            {
                surfaceInUse[surfaceIndex] = false;
                semaphore.Release();
            }
        }
    }

    /// <summary>
    /// Gets a surface ID by index.
    /// </summary>
    public uint GetSurfaceId(int index)
    {
        return index >= 0 && index < surfaces.Length ? surfaces[index] : VaSurface.InvalidId;
    }

    /// <summary>
    /// Waits for a surface to complete rendering.
    /// </summary>
    public bool SyncSurface(int index)
    {
        if (index < 0 || index >= surfaces.Length)
            return false;

        VaStatus status = VaApiInterop.vaSyncSurface(display, surfaces[index]);
        return status == VaStatus.Success;
    }

    /// <summary>
    /// Disposes the surface pool and releases all VA surfaces.
    /// </summary>
    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;

        if (IsInitialized && display != 0)
        {
            fixed (uint* pSurfaces = surfaces)
            {
                VaApiInterop.vaDestroySurfaces(display, pSurfaces, surfaces.Length);
            }
        }

        semaphore.Dispose();
    }
}

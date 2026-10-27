// VideoToolbox Surface Pool
// Manages CVPixelBuffer surfaces for hardware decoding

namespace MediaKernel.Core.Hardware.VideoToolbox;

/// <summary>
/// Surface pool for VideoToolbox hardware decoding.
/// Manages CVPixelBuffer allocation and recycling.
/// </summary>
public sealed unsafe class VtSurfacePool : IHardwareSurfacePool, IDisposable
{
    private readonly nint pixelBufferPool;
    private readonly nint[] surfaces;
    private readonly bool[] surfaceInUse;
    private readonly SemaphoreSlim semaphore;
    private readonly object lockObj = new();
    private readonly int width;
    private readonly int height;
    private readonly CvPixelFormatType pixelFormat;
    private bool isDisposed;

    /// <summary>
    /// Creates a new VideoToolbox surface pool.
    /// </summary>
    /// <param name="width">Surface width in pixels.</param>
    /// <param name="height">Surface height in pixels.</param>
    /// <param name="surfaceCount">Number of surfaces to allocate.</param>
    /// <param name="pixelFormat">Pixel format (default: NV12 video range).</param>
    public VtSurfacePool(int width, int height, int surfaceCount,
        CvPixelFormatType pixelFormat = CvPixelFormatType.Type_420YpCbCr8BiPlanarVideoRange)
    {
        this.width = width;
        this.height = height;
        this.pixelFormat = pixelFormat;

        surfaces = new nint[surfaceCount];
        surfaceInUse = new bool[surfaceCount];
        semaphore = new SemaphoreSlim(surfaceCount, surfaceCount);

        // Create pool (pool handle stored but actual buffers created on demand)
        pixelBufferPool = CreatePixelBufferPool();
    }

    /// <summary>
    /// Gets whether the pool has been initialized successfully.
    /// </summary>
    public bool IsInitialized => pixelBufferPool != 0;

    /// <summary>
    /// Gets the total number of surfaces in the pool.
    /// </summary>
    public int SurfaceCount => surfaces.Length;

    /// <summary>
    /// Gets the number of currently available surfaces.
    /// </summary>
    public int AvailableSurfaceCount => semaphore.CurrentCount;

    /// <summary>
    /// Gets the pixel format of surfaces in this pool.
    /// </summary>
    public CvPixelFormatType PixelFormat => pixelFormat;

    /// <summary>
    /// Creates a CVPixelBufferPool.
    /// </summary>
    private nint CreatePixelBufferPool()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsIOS())
            return 0;

        // Create pool attributes dictionary
        nint poolAttribs = VtInterop.CFDictionaryCreateMutable(0, 2, 0, 0);
        if (poolAttribs == 0) return 0;

        // Create pixel buffer attributes dictionary
        nint pixelBufferAttribs = VtInterop.CFDictionaryCreateMutable(0, 4, 0, 0);
        if (pixelBufferAttribs == 0)
        {
            VtInterop.CFRelease(poolAttribs);
            return 0;
        }

        // Set pixel format
        int formatValue = (int)pixelFormat;
        nint formatNumber = VtInterop.CFNumberCreate(0, VtInterop.CfNumberType.SInt32, &formatValue);

        // Set width
        int widthValue = width;
        nint widthNumber = VtInterop.CFNumberCreate(0, VtInterop.CfNumberType.SInt32, &widthValue);

        // Set height
        int heightValue = height;
        nint heightNumber = VtInterop.CFNumberCreate(0, VtInterop.CfNumberType.SInt32, &heightValue);

        // Create string keys
        nint pixelFormatKey = VtInterop.CFStringCreateWithCString(0, VtDecoderKeys.PixelBufferPixelFormatType, VtInterop.CfStringEncoding.Utf8);
        nint widthKey = VtInterop.CFStringCreateWithCString(0, VtDecoderKeys.PixelBufferWidth, VtInterop.CfStringEncoding.Utf8);
        nint heightKey = VtInterop.CFStringCreateWithCString(0, VtDecoderKeys.PixelBufferHeight, VtInterop.CfStringEncoding.Utf8);

        // Set attributes
        VtInterop.CFDictionarySetValue(pixelBufferAttribs, pixelFormatKey, formatNumber);
        VtInterop.CFDictionarySetValue(pixelBufferAttribs, widthKey, widthNumber);
        VtInterop.CFDictionarySetValue(pixelBufferAttribs, heightKey, heightNumber);

        // Create pool
        int result = VtInterop.CVPixelBufferPoolCreate(0, poolAttribs, pixelBufferAttribs, out nint pool);

        // Release temporary objects
        VtInterop.CFRelease(formatNumber);
        VtInterop.CFRelease(widthNumber);
        VtInterop.CFRelease(heightNumber);
        VtInterop.CFRelease(pixelFormatKey);
        VtInterop.CFRelease(widthKey);
        VtInterop.CFRelease(heightKey);
        VtInterop.CFRelease(poolAttribs);
        VtInterop.CFRelease(pixelBufferAttribs);

        return result == 0 ? pool : 0;
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
                    // Create or reuse surface
                    if (surfaces[i] == 0)
                    {
                        int result = VtInterop.CVPixelBufferPoolCreatePixelBuffer(0, pixelBufferPool, out nint buffer);
                        if (result != 0)
                        {
                            semaphore.Release();
                            return -1;
                        }
                        surfaces[i] = buffer;
                    }

                    surfaceInUse[i] = true;
                    surfaceHandle = surfaces[i];
                    arraySliceIndex = 0;  // No array slices in CVPixelBuffer
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
    /// Gets a surface handle by index.
    /// </summary>
    public nint GetSurfaceHandle(int index)
    {
        return index >= 0 && index < surfaces.Length ? surfaces[index] : 0;
    }

    /// <summary>
    /// Disposes the surface pool.
    /// </summary>
    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;

        // Release all pixel buffers
        for (int i = 0; i < surfaces.Length; i++)
        {
            if (surfaces[i] != 0)
            {
                VtInterop.CVPixelBufferRelease(surfaces[i]);
                surfaces[i] = 0;
            }
        }

        // Release pool
        if (pixelBufferPool != 0)
        {
            VtInterop.CVPixelBufferPoolRelease(pixelBufferPool);
        }

        semaphore.Dispose();
    }
}

using System.Buffers;
using System.Runtime.InteropServices;

namespace MediaKernel.Core;

/// <summary>
/// Manages pinned memory blocks for video frame buffers.
/// Pinned memory allows GPU DMA access without GC interference.
/// </summary>
/// <remarks>
/// For YUV 4:2:0 video at 4K (3840x2160):
/// - Y plane: 3840 × 2160 = 8,294,400 bytes
/// - U plane: 1920 × 1080 = 2,073,600 bytes
/// - V plane: 1920 × 1080 = 2,073,600 bytes
/// - Total per frame: ~12.4 MB
/// 
/// With 16 frames buffered, we need ~200 MB of pinned memory.
/// </remarks>
public sealed class PinnedFramePool : IDisposable
{
    private readonly int frameWidth;
    private readonly int frameHeight;
    private readonly int lumaSize;
    private readonly int chromaSize;
    private readonly int totalFrameSize;
    private readonly int poolCapacity;
    
    private readonly byte[][] pinnedBuffers;
    private readonly GCHandle[] handles;
    private readonly nint[] pointers;
    private readonly bool[] inUse;
    private readonly object lockObject = new();
    private bool isDisposed;

    /// <summary>
    /// Creates a pool of pinned video frame buffers.
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="capacity">Number of frames to pool.</param>
    public PinnedFramePool(int width, int height, int capacity = 16)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException("Width and height must be positive");
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        frameWidth = width;
        frameHeight = height;
        poolCapacity = capacity;

        // YUV 4:2:0: Y is full res, U and V are half width and half height
        lumaSize = width * height;
        chromaSize = (width / 2) * (height / 2);
        totalFrameSize = lumaSize + (chromaSize * 2);

        pinnedBuffers = new byte[capacity][];
        handles = new GCHandle[capacity];
        pointers = new nint[capacity];
        inUse = new bool[capacity];

        // Pre-allocate all pinned buffers
        for (int i = 0; i < capacity; i++)
        {
            // Use GC.AllocateArray with pinned:true for efficient pinning
            pinnedBuffers[i] = GC.AllocateArray<byte>(totalFrameSize, pinned: true);
            handles[i] = GCHandle.Alloc(pinnedBuffers[i], GCHandleType.Pinned);
            pointers[i] = handles[i].AddrOfPinnedObject();
            inUse[i] = false;
        }
    }

    /// <summary>
    /// Gets the frame dimensions.
    /// </summary>
    public (int Width, int Height) Dimensions => (frameWidth, frameHeight);

    /// <summary>
    /// Gets the luma plane size in bytes.
    /// </summary>
    public int LumaPlaneSize => lumaSize;

    /// <summary>
    /// Gets each chroma plane size in bytes.
    /// </summary>
    public int ChromaPlaneSize => chromaSize;

    /// <summary>
    /// Rents a frame buffer from the pool.
    /// </summary>
    /// <returns>A rented frame, or null if pool is exhausted.</returns>
    public RentedFrame? RentFrame()
    {
        lock (lockObject)
        {
            for (int i = 0; i < poolCapacity; i++)
            {
                if (!inUse[i])
                {
                    inUse[i] = true;

                    return new RentedFrame(
                        index: i,
                        pool: this,
                        buffer: pinnedBuffers[i],
                        yOffset: 0, ySize: lumaSize,
                        uOffset: lumaSize, uvSize: chromaSize,
                        vOffset: lumaSize + chromaSize,
                        width: frameWidth,
                        height: frameHeight,
                        yStride: frameWidth,
                        uvStride: frameWidth / 2);
                }
            }
        }
        return null; // Pool exhausted
    }

    /// <summary>
    /// Returns a frame to the pool.
    /// </summary>
    internal void ReturnFrame(int index)
    {
        lock (lockObject)
        {
            if (index >= 0 && index < poolCapacity)
            {
                inUse[index] = false;
            }
        }
    }

    /// <summary>
    /// Gets the number of available frames in the pool.
    /// </summary>
    public int AvailableFrames
    {
        get
        {
            lock (lockObject)
            {
                int count = 0;
                for (int i = 0; i < poolCapacity; i++)
                {
                    if (!inUse[i]) count++;
                }
                return count;
            }
        }
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        isDisposed = true;

        for (int i = 0; i < poolCapacity; i++)
        {
            if (handles[i].IsAllocated)
            {
                handles[i].Free();
            }
        }
    }
}

/// <summary>
/// Represents a rented frame from the pool. Dispose to return to pool.
/// </summary>
public readonly struct RentedFrame : IDisposable
{
    private readonly int index;
    private readonly PinnedFramePool? pool;

    /// <summary>The underlying pinned byte array containing all planes contiguously.</summary>
    private readonly byte[] buffer;

    /// <summary>Byte offset of the Y plane within the buffer.</summary>
    private readonly int yOffset;

    /// <summary>Byte offset of the U plane within the buffer.</summary>
    private readonly int uOffset;

    /// <summary>Byte offset of the V plane within the buffer.</summary>
    private readonly int vOffset;

    /// <summary>Size of the Y plane in bytes.</summary>
    private readonly int ySize;

    /// <summary>Size of each chroma plane in bytes.</summary>
    private readonly int uvSize;

    /// <summary>Frame width in pixels.</summary>
    public readonly int Width;
    
    /// <summary>Frame height in pixels.</summary>
    public readonly int Height;
    
    /// <summary>Stride of the luma plane in bytes.</summary>
    public readonly int YStride;
    
    /// <summary>Stride of the chroma planes in bytes.</summary>
    public readonly int UVStride;

    internal RentedFrame(
        int index,
        PinnedFramePool pool,
        byte[] buffer,
        int yOffset, int ySize,
        int uOffset, int uvSize,
        int vOffset,
        int width, int height,
        int yStride, int uvStride)
    {
        this.index = index;
        this.pool = pool;
        this.buffer = buffer;
        this.yOffset = yOffset;
        this.ySize = ySize;
        this.uOffset = uOffset;
        this.uvSize = uvSize;
        this.vOffset = vOffset;
        Width = width;
        Height = height;
        YStride = yStride;
        UVStride = uvStride;
    }

    /// <summary>
    /// Creates a VideoFrame struct from this rented frame.
    /// The VideoFrame's planes point into the pinned buffer via Memory&lt;byte&gt;.
    /// </summary>
    public VideoFrame ToVideoFrame(long pts, long duration)
    {
        return new VideoFrame
        {
            Width = Width,
            Height = Height,
            Format = PixelFormat.Yuv420P,
            Pts = pts,
            Duration = duration,
            YPlane = new Memory<byte>(buffer, yOffset, ySize),
            UPlane = new Memory<byte>(buffer, uOffset, uvSize),
            VPlane = new Memory<byte>(buffer, vOffset, uvSize),
            YStride = YStride,
            UStride = UVStride,
            VStride = UVStride,
        };
    }

    public void Dispose()
    {
        pool?.ReturnFrame(index);
    }
}

/// <summary>
/// Thread-safe wrapper around ArrayPool for media buffer allocation.
/// Tracks statistics for debugging and optimization.
/// </summary>
public sealed class MediaBufferPool
{
    private readonly ArrayPool<byte> pool;
    private long totalRented;
    private long totalReturned;
    private long peakUsage;
    private long currentUsage;

    /// <summary>
    /// Creates a new MediaBufferPool.
    /// </summary>
    /// <param name="maxArrayLength">Maximum buffer size this pool will handle.</param>
    /// <param name="maxArraysPerBucket">Maximum arrays to keep per size bucket.</param>
    public MediaBufferPool(int maxArrayLength = 16 * 1024 * 1024, int maxArraysPerBucket = 32)
    {
        pool = ArrayPool<byte>.Create(maxArrayLength, maxArraysPerBucket);
    }

    /// <summary>
    /// Gets the shared default pool.
    /// </summary>
    public static MediaBufferPool Shared { get; } = new();

    /// <summary>
    /// Rents a buffer of at least the specified size.
    /// </summary>
    /// <param name="minimumSize">Minimum buffer size needed.</param>
    /// <returns>A buffer of at least the requested size.</returns>
    public byte[] Rent(int minimumSize)
    {
        var buffer = pool.Rent(minimumSize);
        
        Interlocked.Increment(ref totalRented);
        Interlocked.Add(ref currentUsage, buffer.Length);
        
        // Update peak usage
        long current = Interlocked.Read(ref currentUsage);
        long peak = Interlocked.Read(ref peakUsage);
        while (current > peak)
        {
            Interlocked.CompareExchange(ref peakUsage, current, peak);
            peak = Interlocked.Read(ref peakUsage);
        }

        return buffer;
    }

    /// <summary>
    /// Returns a buffer to the pool.
    /// </summary>
    /// <param name="buffer">The buffer to return.</param>
    /// <param name="clearArray">Whether to clear the array contents.</param>
    public void Return(byte[] buffer, bool clearArray = false)
    {
        Interlocked.Increment(ref totalReturned);
        Interlocked.Add(ref currentUsage, -buffer.Length);
        pool.Return(buffer, clearArray);
    }

    /// <summary>
    /// Gets pool statistics for monitoring.
    /// </summary>
    public PoolStatistics GetStatistics()
    {
        return new PoolStatistics(
            TotalRented: Interlocked.Read(ref totalRented),
            TotalReturned: Interlocked.Read(ref totalReturned),
            CurrentUsage: Interlocked.Read(ref currentUsage),
            PeakUsage: Interlocked.Read(ref peakUsage));
    }
}

/// <summary>
/// Statistics about pool usage.
/// </summary>
public readonly record struct PoolStatistics(
    long TotalRented,
    long TotalReturned,
    long CurrentUsage,
    long PeakUsage)
{
    /// <summary>
    /// Gets the number of buffers currently in use.
    /// </summary>
    public long InUse => TotalRented - TotalReturned;
}

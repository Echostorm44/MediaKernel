using System.Buffers;

namespace MediaKernel.Core.Buffers;

/// <summary>
/// The pool that frame-sized decoder buffers are rented from and returned to.
/// </summary>
/// <typeparam name="T">Element type: <see cref="byte"/> for pixels, <see cref="short"/> and
/// <see cref="sbyte"/> for the per-frame motion vector fields.</typeparam>
/// <remarks>
/// <para>
/// These deliberately do not come from <see cref="ArrayPool{T}.Shared"/> or from
/// <see cref="ArrayPool{T}.Create(int,int)"/>. Both were tried. The shared pool trims on a gen2
/// collection, and a frame buffer is past the large object heap threshold, so an allocation there
/// triggers the very collection that drops the buffers the next rent needed. The configurable pool
/// does not trim, but it rounds every request up to a power of two and keeps a fixed number per
/// size class, and it still missed roughly half the time in steady state.
/// </para>
/// <para>
/// A decoder uses a handful of distinct sizes and returns everything it rents, so an exact-length
/// free list is both simpler and strictly better: a rent for a length seen before is always a hit.
/// <see cref="Allocations"/> counts the times that was not true, which is what makes "this decoder
/// allocates nothing per frame" a measurement rather than a claim.
/// </para>
/// <para>
/// One rule makes this work: a buffer goes back to the pool it came from. Frame buffers outlive the
/// decode call that produced them - ownership passes to a decoded frame, then to a pipeline frame,
/// and is released when that is disposed - so a frame carries a reference to its pool rather than
/// assuming one. It is an <see cref="ArrayPool{T}"/> so it can be carried that way.
/// </para>
/// </remarks>
public static class FrameBufferPool<T>
{
    /// <summary>The pool itself, for handing to a frame that must return a buffer later.</summary>
    public static readonly ExactLengthArrayPool<T> Instance = new();

    /// <summary>Buffers handed out, for checking a decoder balances its rents and returns.</summary>
    public static long Rented => Instance.Rented;

    /// <summary>Buffers handed back.</summary>
    public static long Returned => Instance.Returned;

    /// <summary>
    /// Rents that could not be satisfied from the free list and had to allocate. In steady state
    /// this must stop increasing; if it tracks the frame count, something is not being returned.
    /// </summary>
    public static long Allocations => Instance.Allocations;

    /// <summary>Rents a buffer of exactly <paramref name="length"/> elements.</summary>
    public static T[] Rent(int length) => Instance.Rent(length);

    /// <summary>Returns a buffer previously obtained from <see cref="Rent"/>. Null is ignored.</summary>
    public static void Return(T[]? buffer)
    {
        if (buffer is not null)
            Instance.Return(buffer);
    }
}

/// <summary>
/// An <see cref="ArrayPool{T}"/> that keys its free lists on exact length rather than rounding to a
/// size class, and never trims. See <see cref="FrameBufferPool{T}"/> for why.
/// </summary>
public sealed class ExactLengthArrayPool<T> : ArrayPool<T>
{
    /// <summary>
    /// Distinct lengths tracked. One decoder needs a handful, but this pool is static and shared
    /// by every decoder in the process, and each resolution brings its own set of lengths - luma,
    /// chroma, per-CTB tables and the rest. Sixteen was enough for one file and not for several:
    /// decoding a second video at another resolution filled the classes, after which every rent
    /// missed and allocated. That showed up as a decoder measured at zero bytes a frame on its own
    /// reading seven kilobytes a frame once another test had run first in the same process.
    /// </summary>
    private const int MaxSizeClasses = 64;

    /// <summary>Buffers kept per length, covering a decoded picture buffer and frames in flight.</summary>
    private const int MaxBuffersPerSize = 64;

    private sealed class SizeClass
    {
        public int Length;
        public readonly T[]?[] Free = new T[MaxBuffersPerSize][];
        public int Count;
    }

    private readonly SizeClass?[] classes = new SizeClass?[MaxSizeClasses];
    private readonly object gate = new();

    private long rented;
    private long returned;
    private long allocations;


    /// <summary>Buffers handed out.</summary>
    public long Rented => Interlocked.Read(ref rented);

    /// <summary>Buffers handed back.</summary>
    public long Returned => Interlocked.Read(ref returned);

    /// <summary>Rents that had to allocate because the free list had nothing of that length.</summary>
    public long Allocations => Interlocked.Read(ref allocations);

    /// <inheritdoc />
    public override T[] Rent(int minimumLength)
    {
        Interlocked.Increment(ref rented);

        lock (gate)
        {
            var sizeClass = Find(minimumLength);
            if (sizeClass is not null && sizeClass.Count > 0)
            {
                var buffer = sizeClass.Free[--sizeClass.Count]!;
                sizeClass.Free[sizeClass.Count] = null;
                return buffer;
            }
        }

        Interlocked.Increment(ref allocations);
        return new T[minimumLength];
    }

    /// <inheritdoc />
    public override void Return(T[] array, bool clearArray = false)
    {
        if (array is null)
            return;

        Interlocked.Increment(ref returned);

        if (clearArray)
            Array.Clear(array);

        lock (gate)
        {
            var sizeClass = Find(array.Length) ?? Add(array.Length);
            if (sizeClass is null)
                return;

            // Refuse a buffer that is already free. Frames are disposable structs, so disposing a
            // copy returns the buffer without clearing the original's field, and a second dispose
            // would otherwise put the same array in the pool twice - after which two renters get
            // the same memory and corrupt each other, far from the code that caused it. The scan is
            // bounded by MaxBuffersPerSize and runs once per frame, which is nothing next to
            // decoding one.
            for (int i = 0; i < sizeClass.Count; i++)
                if (ReferenceEquals(sizeClass.Free[i], array))
                    return;

            // A pool that grew past its cap has more buffers alive than the decoder needs; dropping
            // one is better than growing without bound.
            if (sizeClass.Count < MaxBuffersPerSize)
                sizeClass.Free[sizeClass.Count++] = array;
        }
    }

    private SizeClass? Find(int length)
    {
        for (int i = 0; i < classes.Length; i++)
        {
            var candidate = classes[i];
            if (candidate is null)
                return null;
            if (candidate.Length == length)
                return candidate;
        }

        return null;
    }

    private SizeClass? Add(int length)
    {
        for (int i = 0; i < classes.Length; i++)
        {
            if (classes[i] is null)
                return classes[i] = new SizeClass { Length = length };
        }

        // More distinct lengths than expected - a resolution change every frame would do it.
        // Returning null drops the buffer rather than pooling it, which is correct but worth
        // knowing about if Allocations starts climbing.
        return null;
    }
}

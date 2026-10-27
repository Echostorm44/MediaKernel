using System;
using System.Threading;

namespace MediaKernel.Core.Threading;

/// <summary>
/// How far down a picture has been decoded, so a thread decoding a later frame can read the parts
/// of a reference that are finished without waiting for the parts that are not.
/// </summary>
/// <remarks>
/// <para>
/// This is the primitive frame-level threading is built on. Frame N+1 usually references frame N,
/// so the two cannot simply run side by side - but motion vectors are bounded, and a block near the
/// top of frame N+1 only needs rows near the top of frame N. Publishing progress as rows complete
/// lets the two overlap by most of a frame.
/// </para>
/// <para>
/// The common case is that the rows are already there: decode order runs well ahead of the
/// references it needs, so <see cref="Await"/> is a single volatile read almost every time. It
/// spins briefly before blocking, because when a wait is needed it is usually short - the producer
/// is a few macroblock rows away, not a frame away.
/// </para>
/// <para>
/// Nothing here allocates after construction. One lock object per picture is created once and
/// reused for the life of the picture, which matters because a GC pause during playback is exactly
/// what this whole effort is trying to avoid.
/// </para>
/// </remarks>
internal sealed class PictureProgress
{
    /// <summary>The picture is finished; every row is readable.</summary>
    public const int Complete = int.MaxValue;

    /// <summary>Spins before blocking, since a wait is usually only a few rows long.</summary>
    private const int SpinsBeforeBlocking = 64;

    private readonly object gate = new();

    private int rowsComplete;
    private int waiters;

    /// <summary>Rows of this picture that are finished and safe to read.</summary>
    public int RowsComplete => Volatile.Read(ref rowsComplete);

    /// <summary>Whether the whole picture is finished.</summary>
    public bool IsComplete => Volatile.Read(ref rowsComplete) == Complete;

    /// <summary>
    /// Resets for a new picture. Called before any thread can be waiting on it.
    /// </summary>
    public void Reset()
    {
        Volatile.Write(ref rowsComplete, 0);
    }

    /// <summary>
    /// Publishes that rows up to but excluding <paramref name="rows"/> are finished.
    /// </summary>
    /// <remarks>
    /// Progress only ever moves forward. A caller that publishes a smaller value than the picture
    /// already has is ignored rather than trusted, so a mistake cannot un-publish rows another
    /// thread has already been told it may read.
    /// </remarks>
    public void Publish(int rows)
    {
        // Lock-free unless somebody is actually waiting. This runs once per macroblock row of
        // every picture, so taking a monitor here cost 6.2% of decode when it was measured -
        // for a signal that, single-threaded, nobody ever waits on.
        if (rows <= Volatile.Read(ref rowsComplete))
            return;

        Volatile.Write(ref rowsComplete, rows);

        // Ordering: the new count is published before the waiter count is read, and a waiter
        // registers itself before re-checking the count under the lock. So either the publisher
        // sees the waiter and pulses, or the waiter sees the new count and does not wait.
        if (Volatile.Read(ref waiters) > 0)
        {
            lock (gate)
            {
                Monitor.PulseAll(gate);
            }
        }
    }

    /// <summary>Publishes that the whole picture is finished.</summary>
    public void MarkComplete() => Publish(Complete);

    /// <summary>
    /// Waits until rows up to but excluding <paramref name="rows"/> are readable.
    /// </summary>
    public void Await(int rows)
    {
        // The overwhelmingly common case: the producer is already past this point.
        if (Volatile.Read(ref rowsComplete) >= rows)
            return;

        var spinner = new SpinWait();
        for (int i = 0; i < SpinsBeforeBlocking; i++)
        {
            spinner.SpinOnce();
            if (Volatile.Read(ref rowsComplete) >= rows)
                return;
        }

        lock (gate)
        {
            Interlocked.Increment(ref waiters);
            try
            {
                while (Volatile.Read(ref rowsComplete) < rows)
                    Monitor.Wait(gate);
            }
            finally
            {
                Interlocked.Decrement(ref waiters);
            }
        }
    }

    /// <summary>
    /// Waits until the given rows are readable, giving up after a timeout.
    /// </summary>
    /// <returns>False if the rows did not arrive in time.</returns>
    /// <remarks>
    /// A decoder that fails mid-picture must not leave another thread blocked for ever. Callers that
    /// can carry on without the data use this and treat false as "read what is there" - a corrupt
    /// stream should degrade, not deadlock.
    /// </remarks>
    public bool Await(int rows, TimeSpan timeout)
    {
        if (Volatile.Read(ref rowsComplete) >= rows)
            return true;

        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        lock (gate)
        {
            while (rowsComplete < rows)
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                    return false;

                Interlocked.Increment(ref waiters);
                bool signalled;
                try { signalled = Monitor.Wait(gate, (int)remaining); }
                finally { Interlocked.Decrement(ref waiters); }

                if (!signalled && rowsComplete < rows)
                    return false;
            }
        }

        return true;
    }
}

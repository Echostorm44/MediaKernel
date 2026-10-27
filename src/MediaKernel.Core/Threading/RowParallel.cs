using System;
using System.Threading;

namespace MediaKernel.Core.Threading;

/// <summary>
/// Splits a range of rows across a fixed set of worker threads and waits for them, without
/// allocating anything after construction.
/// </summary>
/// <remarks>
/// <para>
/// Playback currently runs with the garbage collector never waking, and threading must not undo
/// that. <c>Parallel.For</c> cannot be used: it allocates per call, and the lambda it takes
/// allocates a closure the moment it captures anything - which is how the work would inevitably be
/// described. So the work is an interface implemented by the object that already holds the state,
/// and the range is passed as two integers. Nothing is captured, so nothing is allocated.
/// </para>
/// <para>
/// The partition is contiguous and computed the same way every call, so a given row is always
/// handled by the same participant for a given range and worker count. That is not required for
/// correctness - the caller's rows must be independent, or threading them would be wrong regardless
/// - but it makes a divergence reproducible rather than a race to chase.
/// </para>
/// <para>
/// The calling thread takes a share of the work instead of blocking immediately, so a two-worker
/// pool uses three participants. With a worker count of one the work runs inline on the caller and
/// no threads are involved at all, which is the default and keeps the single-threaded path exactly
/// as it was.
/// </para>
/// </remarks>
internal sealed class RowParallel : IDisposable
{
    /// <summary>A unit of work over a half-open range of rows.</summary>
    /// <remarks>
    /// Implemented by the object that owns the data, so the range is the only thing passed and no
    /// closure is created. Implementations must not write outside their own rows, and must not read
    /// anything another range writes.
    /// </remarks>
    internal interface IRowRange
    {
        void Execute(int startRow, int endRowExclusive);
    }

    private readonly Thread[] threads;
    private readonly SemaphoreSlim[] wake;
    private readonly SemaphoreSlim finished;

    private IRowRange? work;
    private int rangeStart;
    private int rangeEnd;
    private volatile bool disposed;
    private Exception? workerFault;

    /// <summary>Participants including the calling thread.</summary>
    public int Participants => threads.Length + 1;

    /// <param name="workerThreads">
    /// Threads to create. Zero runs everything inline on the caller, which is the single-threaded
    /// path.
    /// </param>
    public RowParallel(int workerThreads)
    {
        if (workerThreads < 0)
            throw new ArgumentOutOfRangeException(nameof(workerThreads));

        threads = new Thread[workerThreads];
        wake = new SemaphoreSlim[workerThreads];
        finished = new SemaphoreSlim(0, Math.Max(workerThreads, 1));

        for (int i = 0; i < workerThreads; i++)
        {
            wake[i] = new SemaphoreSlim(0, 1);
            int index = i;
            var thread = new Thread(() => WorkerLoop(index))
            {
                IsBackground = true,
                Name = $"MediaKernel row worker {i}",
            };
            threads[i] = thread;
            thread.Start();
        }
    }

    /// <summary>Runs <paramref name="rows"/> over [startRow, endRowExclusive) and returns once done.</summary>
    public void Run(IRowRange rows, int startRow, int endRowExclusive)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ObjectDisposedException.ThrowIf(disposed, this);

        int total = endRowExclusive - startRow;
        if (total <= 0)
            return;

        // One participant, or too few rows to be worth splitting: do it here.
        if (threads.Length == 0 || total < 2)
        {
            rows.Execute(startRow, endRowExclusive);
            return;
        }

        work = rows;
        rangeStart = startRow;
        rangeEnd = endRowExclusive;
        workerFault = null;

        for (int i = 0; i < threads.Length; i++)
            wake[i].Release();

        // The caller is the last participant, and takes the final chunk.
        try
        {
            ChunkFor(threads.Length, out int start, out int end);
            if (end > start)
                rows.Execute(start, end);
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref workerFault, ex, null);
        }

        for (int i = 0; i < threads.Length; i++)
            finished.Wait();

        work = null;

        if (workerFault is not null)
            throw new AggregateException("A row worker failed.", workerFault);
    }

    /// <summary>The contiguous chunk belonging to participant <paramref name="index"/>.</summary>
    private void ChunkFor(int index, out int start, out int end)
    {
        int total = rangeEnd - rangeStart;
        int participants = threads.Length + 1;
        int baseSize = total / participants;
        int remainder = total % participants;

        // The first `remainder` participants take one extra row, so the split is even and every
        // row belongs to exactly one participant.
        start = rangeStart + index * baseSize + Math.Min(index, remainder);
        end = start + baseSize + (index < remainder ? 1 : 0);
    }

    private void WorkerLoop(int index)
    {
        while (true)
        {
            wake[index].Wait();
            if (disposed)
                return;

            try
            {
                var rows = work;
                if (rows is not null)
                {
                    ChunkFor(index, out int start, out int end);
                    if (end > start)
                        rows.Execute(start, end);
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref workerFault, ex, null);
            }
            finally
            {
                finished.Release();
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        for (int i = 0; i < threads.Length; i++)
            wake[i].Release();

        foreach (var thread in threads)
            thread.Join(TimeSpan.FromSeconds(2));

        foreach (var semaphore in wake)
            semaphore.Dispose();

        finished.Dispose();
    }
}

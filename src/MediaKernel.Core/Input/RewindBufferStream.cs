using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MediaKernel.Core.Input;

/// <summary>
/// Gives a forward-only source a limited ability to seek backwards, by keeping the most recently
/// read bytes in a buffer.
/// </summary>
/// <remarks>
/// A live radio stream cannot seek, but format probing and demuxer start-up both need to: the
/// pipeline reads a header and rewinds, and each elementary demuxer scans for a sync word and then
/// resumes from it. Both only ever look back a few kilobytes, so buffering a window is enough to
/// make them work unchanged over a socket.
/// <para>
/// <see cref="Length"/> is 0 when the source length is unknown, which is how the demuxers already
/// spell "live" - it stops them estimating a duration from a size that does not exist.
/// </para>
/// </remarks>
public sealed class RewindBufferStream : Stream
{
    /// <summary>Default window. Comfortably covers a 16 KB probe plus a demuxer sync scan.</summary>
    public const int DefaultCapacity = 128 * 1024;

    private readonly Stream source;
    private readonly bool ownsSource;
    private readonly byte[] buffer;

    /// <summary>Stream offset of buffer[0].</summary>
    private long bufferStart;

    /// <summary>Bytes currently held in the buffer.</summary>
    private int buffered;

    /// <summary>Absolute stream offset the next read returns.</summary>
    private long position;

    /// <summary>True once the window has slid, so the earliest bytes are gone for good.</summary>
    private bool discarded;

    public RewindBufferStream(Stream source, bool ownsSource = false, int capacity = DefaultCapacity)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.ownsSource = ownsSource;
        buffer = new byte[capacity];
    }

    public override bool CanRead => true;

    /// <summary>
    /// True: callers may seek backwards within the retained window, and forwards by reading. A
    /// backward seek past the window throws rather than silently returning wrong bytes.
    /// </summary>
    public override bool CanSeek => true;

    public override bool CanWrite => false;

    /// <summary>Source length when known, otherwise 0 meaning "unknown".</summary>
    public override long Length
    {
        get
        {
            try
            {
                return source.CanSeek ? source.Length : 0;
            }
            catch (NotSupportedException)
            {
                return 0;
            }
        }
    }

    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <summary>Earliest offset still reachable by a backward seek.</summary>
    public long EarliestAvailablePosition => bufferStart;

    public override int Read(byte[] array, int offset, int count) =>
        Read(array.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        if (destination.IsEmpty)
            return 0;

        // Serve from the buffer whenever the caller has seeked back into it.
        int fromBuffer = ServeFromBuffer(destination);
        if (fromBuffer > 0)
            return fromBuffer;

        int read = source.Read(destination);
        if (read > 0)
            Append(destination[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.IsEmpty)
            return 0;

        int fromBuffer = ServeFromBuffer(destination.Span);
        if (fromBuffer > 0)
            return fromBuffer;

        int read = await source.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        if (read > 0)
            Append(destination.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] array, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(array.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>
    /// Copies out anything already buffered at the current position, or 0 when the position is at
    /// the buffer's leading edge and fresh bytes are needed.
    /// </summary>
    private int ServeFromBuffer(Span<byte> destination)
    {
        long bufferEnd = bufferStart + buffered;
        if (position >= bufferEnd)
            return 0;

        int available = (int)(bufferEnd - position);
        int take = Math.Min(available, destination.Length);
        buffer.AsSpan((int)(position - bufferStart), take).CopyTo(destination);
        position += take;
        return take;
    }

    /// <summary>Records freshly read bytes, sliding the window when it is full.</summary>
    private void Append(ReadOnlySpan<byte> data)
    {
        position += data.Length;

        if (data.Length >= buffer.Length)
        {
            // A single read larger than the window: keep only its tail.
            data[^buffer.Length..].CopyTo(buffer);
            bufferStart = position - buffer.Length;
            buffered = buffer.Length;
            discarded = true;
            return;
        }

        if (buffered + data.Length > buffer.Length)
        {
            int drop = buffered + data.Length - buffer.Length;
            Buffer.BlockCopy(buffer, drop, buffer, 0, buffered - drop);
            bufferStart += drop;
            buffered -= drop;
            discarded = true;
        }

        data.CopyTo(buffer.AsSpan(buffered));
        buffered += data.Length;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => throw new NotSupportedException(
                "Cannot seek relative to the end of a stream whose length is not known"),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target == position)
            return position;

        if (target < bufferStart)
        {
            throw new NotSupportedException(
                $"Cannot seek back to {target}: this source only retains from {bufferStart} " +
                (discarded ? "because earlier bytes have been discarded" : "because it has not been read that far"));
        }

        long bufferEnd = bufferStart + buffered;
        if (target <= bufferEnd)
        {
            position = target;
            return position;
        }

        // Forward past what has been read: the only way to get there is to read and discard.
        Span<byte> scratch = stackalloc byte[4096];
        while (position < target)
        {
            int want = (int)Math.Min(target - position, scratch.Length);
            int read = Read(scratch[..want]);
            if (read == 0)
                break;
        }
        return position;
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] array, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && ownsSource)
            source.Dispose();
        base.Dispose(disposing);
    }
}

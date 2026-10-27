using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs;

/// <summary>
/// FFmpeg's <c>put_bits.h</c> bitstream writer, big-endian (MSB first), in the 64-bit
/// accumulator form the C selects on x86-64.
/// </summary>
/// <remarks>
/// A direct translation. The accumulator holds up to 64 pending bits and is flushed eight bytes
/// at a time, which is why <see cref="BytesOutput"/> is only meaningful on a flushed writer -
/// the C asserts exactly that. Where the C logs "put_bits buffer too small" and asserts, this
/// throws: a bitstream writer that runs off its buffer has produced nothing usable, and a silent
/// truncation is the kind of fault that reaches a gate as mysteriously wrong output.
/// </remarks>
public sealed class PutBitContext
{
    private const int BufBits = 64;

    private ulong bitBuf;
    private int bitLeft;
    private byte[] buf;
    private int bufPtr;
    private int bufEnd;

    /// <summary>init_put_bits</summary>
    public PutBitContext(byte[] buffer, int bufferSize)
    {
        Reset(buffer, bufferSize);
    }

    /// <summary>init_put_bits, on an existing writer - the C re-initialises in place.</summary>
    public void Reset(byte[] buffer, int bufferSize)
    {
        if (bufferSize < 0)
        {
            bufferSize = 0;
            buffer = [];
        }

        buf = buffer;
        bufEnd = bufferSize;
        bufPtr = 0;
        bitLeft = BufBits;
        bitBuf = 0;
    }

    /// <summary>The buffer being written, so a CRC can be taken over the bytes so far.</summary>
    public byte[] Buffer => buf;

    /// <summary>put_bits_count: the total number of bits written.</summary>
    public int BitsCount => bufPtr * 8 + BufBits - bitLeft;

    /// <summary>
    /// put_bytes_output: bytes written so far. Only meaningful on a freshly initialised or
    /// flushed writer, which the C asserts and this checks.
    /// </summary>
    public int BytesOutput
    {
        get
        {
            if (bitLeft != BufBits)
                throw new InvalidOperationException("BytesOutput on an unflushed PutBitContext.");
            return bufPtr;
        }
    }

    /// <summary>put_bits_no_assert</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PutBitsNoAssert(int n, ulong value)
    {
        ulong localBuf = bitBuf;
        int localLeft = bitLeft;

        if (n < localLeft)
        {
            localBuf = (localBuf << n) | value;
            localLeft -= n;
        }
        else
        {
            localBuf <<= localLeft;
            // n == 64 would be undefined as a shift; the C never calls it with n == BUF_BITS
            // because put_bits caps at 31 and put_bits63 at 63.
            localBuf |= value >> (n - localLeft);
            WriteAccumulator(localBuf);
            localLeft += BufBits - n;
            localBuf = value;
        }

        bitBuf = localBuf;
        bitLeft = localLeft;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteAccumulator(ulong value)
    {
        if (bufEnd - bufPtr < sizeof(ulong))
            throw new InvalidOperationException("put_bits buffer too small.");

        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(bufPtr), value);
        bufPtr += sizeof(ulong);
    }

    /// <summary>put_bits: up to 31 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PutBits(int n, uint value)
    {
        if (n > 31 || (n < 32 && value >= (1u << n)))
            throw new ArgumentOutOfRangeException(nameof(value), "put_bits: value wider than n.");
        PutBitsNoAssert(n, value);
    }

    /// <summary>put_sbits: n bits of a signed value, zero-extended from its low n bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PutSBits(int n, int value)
    {
        if (n < 0 || n > 31)
            throw new ArgumentOutOfRangeException(nameof(n));
        // av_zero_extend(value, n)
        PutBits(n, (uint)value & (uint.MaxValue >> (32 - n)));
    }

    /// <summary>put_bits32: exactly 32 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PutBits32(uint value) => PutBitsNoAssert(32, value);

    /// <summary>put_bits63: up to 63 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PutBits63(int n, ulong value)
    {
        if (n >= 64 || value >= (1UL << n))
            throw new ArgumentOutOfRangeException(nameof(value), "put_bits63: value wider than n.");
        PutBitsNoAssert(n, value);
    }

    /// <summary>put_sbits63: n bits of a signed 64-bit value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PutSBits63(int n, long value)
    {
        if (n < 0 || n >= 64)
            throw new ArgumentOutOfRangeException(nameof(n));
        PutBits63(n, (ulong)value & ~(ulong.MaxValue << n));
    }

    /// <summary>flush_put_bits: pad the pending bits out to a byte boundary with zeros.</summary>
    public void Flush()
    {
        if (bitLeft < BufBits)
            bitBuf <<= bitLeft;

        while (bitLeft < BufBits)
        {
            if (bufPtr >= bufEnd)
                throw new InvalidOperationException("put_bits buffer too small.");

            buf[bufPtr++] = (byte)(bitBuf >> (BufBits - 8));
            bitBuf <<= 8;
            bitLeft += 8;
        }

        bitLeft = BufBits;
        bitBuf = 0;
    }
}

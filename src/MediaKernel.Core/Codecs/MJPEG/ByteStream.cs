// Ported from FFmpeg's libavcodec/bytestream.h - the subset JPEG's segment parsing uses.
//
// Only the unchecked readers (the "u" suffix) and the two queries that make them safe are
// here. That is deliberate and is how the C uses them: mjpeg_parse_len checks once that a
// segment's declared length is within the buffer, and every read inside that segment is then
// unchecked. Translating the checked variants as well would invite a caller to mix the two
// and lose the property that makes the unchecked ones correct.
//
// Lives beside the MJPEG decoder because that is its only caller. It belongs in a shared
// place the moment a second codec needs it.

using System;
using System.Buffers.Binary;

namespace MediaKernel.Core.Codecs.Mjpeg;

/// <summary><c>GetByteContext</c> (<c>bytestream.h</c>) - a cursor over a byte buffer.</summary>
public struct GetByteContext
{
    /// <summary><c>buffer_start</c> - the whole buffer.</summary>
    public byte[] Buffer;

    /// <summary><c>buffer</c> - how far in the cursor is.</summary>
    public int Position;

    /// <summary><c>buffer_end</c> - one past the last readable byte.</summary>
    public int End;

    /// <summary><c>bytestream2_init</c>.</summary>
    public void Init(byte[] buffer, int offset, int size)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || size < 0 || offset + size > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(size), "window is outside the buffer");

        Buffer = buffer;
        Position = offset;
        End = offset + size;
    }

    /// <summary><c>bytestream2_get_bytes_left</c>.</summary>
    public readonly int BytesLeft => End - Position;

    /// <summary><c>bytestream2_tell</c> - how far the cursor has moved from the start.</summary>
    public readonly int Tell => Position;

    /// <summary>
    /// <c>bytestream2_get_byteu</c> - one byte, unchecked.
    /// </summary>
    /// <remarks>
    /// Unchecked in the C means "the caller has already established there is room". It is not
    /// unchecked here: a C# array read throws rather than wandering into the next allocation,
    /// and a throw is the correct answer to a caller that got its arithmetic wrong. The name
    /// keeps the C's, so the call sites read the same.
    /// </remarks>
    public byte GetByteU() => Buffer[Position++];

    /// <summary><c>bytestream2_get_be16u</c> - two bytes, big-endian, unchecked.</summary>
    public ushort GetBe16U()
    {
        ushort v = BinaryPrimitives.ReadUInt16BigEndian(Buffer.AsSpan(Position));
        Position += 2;
        return v;
    }

    /// <summary><c>bytestream2_get_be32u</c>.</summary>
    public uint GetBe32U()
    {
        uint v = (uint)((Buffer[Position] << 24) | (Buffer[Position + 1] << 16)
                      | (Buffer[Position + 2] << 8) | Buffer[Position + 3]);
        Position += 4;
        return v;
    }

    /// <summary><c>bytestream2_peek_byteu</c> - the next byte, without consuming it.</summary>
    public readonly byte PeekByteU() => Buffer[Position];

    /// <summary><c>bytestream2_peek_be32u</c>.</summary>
    public readonly uint PeekBe32U() =>
        (uint)((Buffer[Position] << 24) | (Buffer[Position + 1] << 16)
             | (Buffer[Position + 2] << 8) | Buffer[Position + 3]);

    /// <summary><c>bytestream2_peek_be16</c>.</summary>
    public readonly int PeekBe16() => (Buffer[Position] << 8) | Buffer[Position + 1];

    /// <summary><c>bytestream2_skipu</c>.</summary>
    public void SkipU(int count) => Position += count;
}

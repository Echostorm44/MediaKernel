using System;

namespace MediaKernel.Core.Codecs.AAC.Sbr;

/// <summary>
/// Bit reader for the SBR extension payload.
/// </summary>
/// <remarks>
/// Separate from the AAC reader because SBR is parsed out of an extension payload whose length is
/// known up front, and the parser has to report exactly how many bits it consumed so the caller can
/// skip any remainder. Overreading is tracked rather than throwing: a corrupt payload should turn
/// SBR off for the frame, not fail the whole decode.
/// </remarks>
internal ref struct SbrBitReader
{
    private readonly ReadOnlySpan<byte> data;
    private int bitPosition;
    private bool invalid;

    public SbrBitReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        bitPosition = 0;
        invalid = false;
    }

    /// <summary>Bits consumed so far.</summary>
    public readonly int Position => bitPosition;

    public readonly int TotalBits => data.Length * 8;

    public readonly int RemainingBits => TotalBits - bitPosition;

    /// <summary>True once a read has run past the end of the payload, or a code failed to decode.</summary>
    public readonly bool IsInvalid => invalid;

    public void MarkInvalid() => invalid = true;

    public uint ReadBit()
    {
        int byteIndex = bitPosition >> 3;
        if (byteIndex >= data.Length)
        {
            invalid = true;
            bitPosition++;
            return 0;
        }

        uint bit = (uint)(data[byteIndex] >> (7 - (bitPosition & 7))) & 1u;
        bitPosition++;
        return bit;
    }

    public uint ReadBits(int count)
    {
        uint value = 0;
        for (int i = 0; i < count; i++)
            value = (value << 1) | ReadBit();
        return value;
    }

    public bool ReadFlag() => ReadBit() != 0;

    /// <summary>Reads <paramref name="count"/> single bits into consecutive entries.</summary>
    public void ReadBitVector(Span<byte> destination, int count)
    {
        for (int i = 0; i < count; i++)
            destination[i] = (byte)ReadBit();
    }

    public void Skip(int count)
    {
        bitPosition += count;
        if (bitPosition > TotalBits)
            invalid = true;
    }

    /// <summary>Peeks without consuming; used to tell real extension data from zero padding.</summary>
    public readonly uint PeekBits(int count)
    {
        var copy = this;
        return copy.ReadBits(count);
    }
}

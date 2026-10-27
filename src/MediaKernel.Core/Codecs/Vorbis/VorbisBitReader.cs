// Licensed under the MIT License.
// Vorbis bitstream reader (LSB-first bit packing).

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Vorbis;

/// <summary>
/// Bit reader for Vorbis bitstream (LSB-first packing).
/// </summary>
public ref struct VorbisBitReader
{
    private readonly ReadOnlySpan<byte> data;
    private int bytePosition;
    private int bitPosition;
    
    /// <summary>
    /// Creates a new Vorbis bit reader.
    /// </summary>
    public VorbisBitReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        bytePosition = 0;
        bitPosition = 0;
    }
    
    /// <summary>
    /// Gets the total number of bits available.
    /// </summary>
    public readonly int TotalBits => data.Length * 8;
    
    /// <summary>
    /// Gets the current bit position.
    /// </summary>
    public readonly int Position => bytePosition * 8 + bitPosition;
    
    /// <summary>
    /// Gets the number of bits remaining.
    /// </summary>
    public readonly int BitsRemaining => TotalBits - Position;
    
    /// <summary>
    /// Reads a single bit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit()
    {
        if (bytePosition >= data.Length)
            return 0;
        
        int bit = (data[bytePosition] >> bitPosition) & 1;
        bitPosition++;
        
        if (bitPosition >= 8)
        {
            bitPosition = 0;
            bytePosition++;
        }
        
        return bit;
    }
    
    /// <summary>
    /// Reads up to 32 bits (LSB first).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        if (count == 0)
            return 0;
        
        if (count > 32)
            throw new ArgumentOutOfRangeException(nameof(count));
        
        uint result = 0;
        int shift = 0;
        
        while (count > 0)
        {
            if (bytePosition >= data.Length)
                break;
            
            // Calculate how many bits we can read from current byte
            int bitsAvailable = 8 - bitPosition;
            int bitsToRead = Math.Min(bitsAvailable, count);
            
            // Extract bits from current byte
            int mask = (1 << bitsToRead) - 1;
            uint bits = (uint)((data[bytePosition] >> bitPosition) & mask);
            
            result |= bits << shift;
            shift += bitsToRead;
            count -= bitsToRead;
            bitPosition += bitsToRead;
            
            if (bitPosition >= 8)
            {
                bitPosition = 0;
                bytePosition++;
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Reads up to 64 bits (LSB first). Used for floor type 0 amplitude.
    /// </summary>
    public ulong ReadBits64(int count)
    {
        if (count <= 32)
            return ReadBits(count);
        
        ulong lo = ReadBits(32);
        ulong hi = ReadBits(count - 32);
        return lo | (hi << 32);
    }
    
    /// <summary>
    /// Reads a signed integer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSignedBits(int count)
    {
        uint value = ReadBits(count);
        
        // Sign extend
        int signBit = 1 << (count - 1);
        if ((value & signBit) != 0)
        {
            // Negative - extend sign
            value |= ~0u << count;
        }
        
        return (int)value;
    }
    
    /// <summary>
    /// Reads a boolean (1 bit).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBool() => ReadBit() != 0;
    
    /// <summary>
    /// Reads a Vorbis float32 (6-bit mantissa, 5-bit exponent, 1-bit sign).
    /// </summary>
    public float ReadFloat32()
    {
        uint packed = ReadBits(32);
        
        // Vorbis float32 format: [mantissa:21][exponent:10][sign:1]
        uint mantissa = packed & 0x1FFFFF;      // 21 bits
        uint exponent = (packed >> 21) & 0x3FF; // 10 bits
        uint sign = (packed >> 31) & 1;
        
        float value = mantissa * MathF.Pow(2, (int)exponent - 788);
        return sign != 0 ? -value : value;
    }
    
    /// <summary>
    /// Skips specified number of bits.
    /// </summary>
    public void SkipBits(int count)
    {
        int newPosition = Position + count;
        bytePosition = newPosition / 8;
        bitPosition = newPosition % 8;
    }
    
    /// <summary>
    /// Aligns to byte boundary.
    /// </summary>
    public void AlignToByte()
    {
        if (bitPosition != 0)
        {
            bitPosition = 0;
            bytePosition++;
        }
    }
    
    /// <summary>
    /// Reads bytes from current position (must be byte-aligned).
    /// </summary>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (bitPosition != 0)
            throw new InvalidOperationException("Must be byte-aligned to read bytes");
        
        if (bytePosition + count > data.Length)
            count = data.Length - bytePosition;
        
        var result = data.Slice(bytePosition, count);
        bytePosition += count;
        return result;
    }
    
    /// <summary>
    /// Decodes a value using a Huffman codebook.
    /// </summary>
    public int ReadHuffman(VorbisCodebook codebook)
    {
        if (codebook.DecodeTree.Length == 0)
            return -1;
        
        int node = 0;
        int maxDepth = 32; // No valid Vorbis codebook exceeds 32 bits
        
        while (maxDepth-- > 0)
        {
            int bit = ReadBit();
            
            // Tree is stored as [left_child, right_child] pairs
            // Negative values are leaf nodes (entry index = ~value)
            int childIndex = node * 2 + bit;
            
            if (childIndex >= codebook.DecodeTree.Length)
                return -1;
            
            node = codebook.DecodeTree[childIndex];
            
            if (node < 0)
            {
                // Leaf node - return entry index
                return ~node;
            }
        }
        
        return -1; // Safety: exceeded max depth
    }
    
    /// <summary>
    /// Decodes a value and returns the VQ vector.
    /// </summary>
    public ReadOnlySpan<float> ReadVq(VorbisCodebook codebook)
    {
        int entry = ReadHuffman(codebook);
        
        if (entry < 0 || codebook.VectorLookup == null)
            return ReadOnlySpan<float>.Empty;
        
        int offset = entry * codebook.Dimensions;
        int length = codebook.Dimensions;
        
        if (offset + length > codebook.VectorLookup.Length)
            return ReadOnlySpan<float>.Empty;
        
        return codebook.VectorLookup.AsSpan(offset, length);
    }
    
    /// <summary>
    /// Reads an ilog value (bits needed to represent n).
    /// </summary>
    public static int ILog(int value)
    {
        if (value <= 0)
            return 0;
        
        int bits = 0;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }
        return bits;
    }
}

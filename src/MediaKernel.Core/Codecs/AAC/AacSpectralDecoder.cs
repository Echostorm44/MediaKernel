using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.AAC;

/// <summary>
/// AAC spectral data decoder using Huffman codebooks.
/// Decodes quantized spectral coefficients from the bitstream.
/// </summary>
public static class AacSpectralDecoder
{

    /// <summary>
    /// Decodes spectral coefficients for a standard codebook band (codebooks 1-10).
    /// Applies gain inline during decode.
    /// </summary>
    public static void DecodeSpectralBandStandard(
        ref AacBitReader reader,
        Span<float> coefficients,
        int codebook,
        float gain)
    {
        int dimension = AacCodebooks.GetDimension(codebook);
        bool isUnsigned = AacCodebooks.IsUnsigned(codebook);
        
        int v0, v1, v2, v3;
        
        int i = 0;
        while (i < coefficients.Length)
        {
            if (reader.IsEmpty) break;
            DecodeHuffmanCodewordInline(ref reader, codebook, dimension, out v0, out v1, out v2, out v3);
            
            // Apply sign bits for unsigned codebooks
            if (isUnsigned)
            {
                if (v0 != 0 && reader.ReadBit()) v0 = -v0;
                if (v1 != 0 && reader.ReadBit()) v1 = -v1;
                if (dimension == 4)
                {
                    if (v2 != 0 && reader.ReadBit()) v2 = -v2;
                    if (v3 != 0 && reader.ReadBit()) v3 = -v3;
                }
            }
            
            // Apply inverse quantization and gain
            if (i < coefficients.Length) coefficients[i++] = InverseQuantize(v0) * gain;
            if (i < coefficients.Length) coefficients[i++] = InverseQuantize(v1) * gain;
            if (dimension == 4)
            {
                if (i < coefficients.Length) coefficients[i++] = InverseQuantize(v2) * gain;
                if (i < coefficients.Length) coefficients[i++] = InverseQuantize(v3) * gain;
            }
        }
    }

    /// <summary>
    /// Decodes spectral coefficients for the escape codebook (codebook 11).
    /// Writes IQ'd values without gain; caller applies gain via vector_fmul_scalar.
    /// Matches FFmpeg's escape path in decode_spectrum_and_dequant.
    /// </summary>
    public static void DecodeSpectralBandEscape(
        ref AacBitReader reader,
        Span<float> coefficients,
        int codebook)
    {
        int i = 0;
        while (i < coefficients.Length)
        {
            if (reader.IsEmpty) break;
            DecodeHuffmanCodewordInline(ref reader, codebook, 2, out int v0, out int v1, out _, out _);
            
            // Sign bits (escape codebook is unsigned)
            if (v0 != 0 && reader.ReadBit()) v0 = -v0;
            if (v1 != 0 && reader.ReadBit()) v1 = -v1;
            
            // Escape values
            if (Math.Abs(v0) == 16) v0 = DecodeEscapeValue(ref reader, v0);
            if (Math.Abs(v1) == 16) v1 = DecodeEscapeValue(ref reader, v1);
            
            // Write IQ'd values (no gain — caller applies)
            if (i < coefficients.Length) coefficients[i++] = InverseQuantize(v0);
            if (i < coefficients.Length) coefficients[i++] = InverseQuantize(v1);
        }
    }

    /// <summary>
    /// Decodes a Huffman codeword from the specified codebook (inline version).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeHuffmanCodewordInline(
        ref AacBitReader reader,
        int codebook,
        int dimension,
        out int v0, out int v1, out int v2, out int v3)
    {
        v0 = v1 = v2 = v3 = 0;
        
        // Use binary search tree for Huffman decoding
        var table = AacCodebooks.GetCodebook(codebook);
        if (table.IsEmpty)
            return;
        
        int index = 0;
        
        while (index < table.Length)
        {
            var entry = table[index];
            
            if (entry.IsLeaf)
            {
                // Found the codeword - extract values
                ExtractCodewordValues(entry.Value, codebook, dimension, out v0, out v1, out v2, out v3);
                return;
            }
            
            // Guard: if reader is exhausted, Huffman decode cannot succeed
            if (reader.IsEmpty)
                return;
            
            // Navigate tree based on next bit
            if (reader.ReadBit())
                index = entry.Right;
            else
                index = entry.Left;
        }
    }

    /// <summary>
    /// Extracts individual values from a packed codeword value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExtractCodewordValues(
        int packedValue, 
        int codebook, 
        int dimension,
        out int v0, out int v1, out int v2, out int v3)
    {
        int offset = AacCodebooks.GetOffset(codebook);
        int mod = AacCodebooks.GetModulus(codebook);
        
        // Unpack values based on dimension
        if (dimension == 4)
        {
            v3 = (packedValue % mod) - offset;
            packedValue /= mod;
            v2 = (packedValue % mod) - offset;
            packedValue /= mod;
            v1 = (packedValue % mod) - offset;
            packedValue /= mod;
            v0 = packedValue - offset;
        }
        else // dimension == 2
        {
            v1 = (packedValue % mod) - offset;
            v0 = (packedValue / mod) - offset;
            v2 = 0;
            v3 = 0;
        }
    }

    /// <summary>
    /// Decodes an escape-coded value (for codebook 11 when |value| == 16).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeEscapeValue(ref AacBitReader reader, int value)
    {
        // Count escape prefix (number of 1 bits before 0)
        int escapePrefix = 0;
        while (reader.ReadBit() && escapePrefix < 20)
        {
            escapePrefix++;
        }
        
        // Read escape word (escapePrefix + 4 bits)
        int escapeWord = reader.ReadBits(escapePrefix + 4);
        
        // Calculate magnitude
        int magnitude = (1 << (escapePrefix + 4)) + escapeWord;
        
        return value < 0 ? -magnitude : magnitude;
    }

    /// <summary>
    /// Applies inverse quantization: value^(4/3).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float InverseQuantize(int value)
    {
        if (value == 0)
            return 0;
        
        int absValue = Math.Abs(value);
        float result;
        
        // Use lookup table for small values
        if (absValue < AacQuantizationTables.InverseQuantTable.Length)
        {
            result = AacQuantizationTables.InverseQuantTable[absValue];
        }
        else
        {
            // Calculate for large values
            result = MathF.Pow(absValue, 4.0f / 3.0f);
        }
        
        return value < 0 ? -result : result;
    }
}

/// <summary>
/// AAC Huffman codebook definitions and lookup tables.
/// Uses correct Huffman tables from ISO/IEC 14496-3.
/// </summary>
public static class AacCodebooks
{
    // Codebook constants
    public const int ZeroHcb = 0;
    public const int FirstPairHcb = 5;
    public const int EscapeHcb = 11;
    public const int NoiseHcb = 13;
    public const int IntensityHcb2 = 14;
    public const int IntensityHcb = 15;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetDimension(int codebook) =>
        codebook < FirstPairHcb ? 4 : 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsUnsigned(int codebook) => codebook switch
    {
        1 or 2 => false,
        3 or 4 => true,
        5 or 6 => false,
        >= 7 and <= 11 => true,
        _ => false
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetLav(int codebook) => codebook switch
    {
        1 or 2 => 1,
        3 or 4 => 2,
        5 or 6 => 4,
        7 or 8 => 7,
        9 or 10 => 12,
        11 => 16,
        _ => 0
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetOffset(int codebook) => codebook switch
    {
        1 or 2 => 1,
        5 or 6 => 4,
        _ => 0
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetModulus(int codebook) => codebook switch
    {
        1 or 2 => 3,
        3 or 4 => 3,
        5 or 6 => 9,
        7 or 8 => 8,
        9 or 10 => 13,
        11 => 17,
        _ => 1
    };

    public static ReadOnlySpan<HuffmanNode> GetCodebook(int codebook) => codebook switch
    {
        1 => Codebook1,
        2 => Codebook2,
        3 => Codebook3,
        4 => Codebook4,
        5 => Codebook5,
        6 => Codebook6,
        7 => Codebook7,
        8 => Codebook8,
        9 => Codebook9,
        10 => Codebook10,
        11 => Codebook11,
        _ => ReadOnlySpan<HuffmanNode>.Empty
    };

    // Build correct trees from ISO/IEC 14496-3 tables
    public static readonly HuffmanNode[] Codebook1 = BuildTreeFromCodeBits(AacSpectralTables.Codes1, AacSpectralTables.Bits1);
    public static readonly HuffmanNode[] Codebook2 = BuildTreeFromCodeBits(AacSpectralTables.Codes2, AacSpectralTables.Bits2);
    public static readonly HuffmanNode[] Codebook3 = BuildTreeFromCodeBits(AacSpectralTables.Codes3, AacSpectralTables.Bits3);
    public static readonly HuffmanNode[] Codebook4 = BuildTreeFromCodeBits(AacSpectralTables.Codes4, AacSpectralTables.Bits4);
    public static readonly HuffmanNode[] Codebook5 = BuildTreeFromCodeBits(AacSpectralTables.Codes5, AacSpectralTables.Bits5);
    public static readonly HuffmanNode[] Codebook6 = BuildTreeFromCodeBits(AacSpectralTables.Codes6, AacSpectralTables.Bits6);
    public static readonly HuffmanNode[] Codebook7 = BuildTreeFromCodeBits(AacSpectralTables.Codes7, AacSpectralTables.Bits7);
    public static readonly HuffmanNode[] Codebook8 = BuildTreeFromCodeBits(AacSpectralTables.Codes8, AacSpectralTables.Bits8);
    public static readonly HuffmanNode[] Codebook9 = BuildTreeFromCodeBits(AacSpectralTables.Codes9, AacSpectralTables.Bits9);
    public static readonly HuffmanNode[] Codebook10 = BuildTreeFromCodeBits(AacSpectralTables.Codes10, AacSpectralTables.Bits10);
    public static readonly HuffmanNode[] Codebook11 = BuildTreeFromCodeBits(AacSpectralTables.Codes11, AacSpectralTables.Bits11);

    /// <summary>
    /// Builds a Huffman binary tree from parallel code/bits arrays.
    /// codes[i] is the bitstring for symbol i, bits[i] is its length.
    /// The leaf value stored is the symbol index i.
    /// </summary>
    private static HuffmanNode[] BuildTreeFromCodeBits(ushort[] codes, byte[] bits)
    {
        int size = codes.Length;

        // Mutable tree building: parallel lists of left/right children
        var nodeLeft = new System.Collections.Generic.List<int> { -1 };
        var nodeRight = new System.Collections.Generic.List<int> { -1 };
        var nodeIsLeaf = new System.Collections.Generic.List<bool> { false };
        var nodeValue = new System.Collections.Generic.List<int> { 0 };

        int AddBranch()
        {
            int idx = nodeLeft.Count;
            nodeLeft.Add(-1);
            nodeRight.Add(-1);
            nodeIsLeaf.Add(false);
            nodeValue.Add(0);
            return idx;
        }

        int AddLeaf(int val)
        {
            int idx = nodeLeft.Count;
            nodeLeft.Add(0);
            nodeRight.Add(0);
            nodeIsLeaf.Add(true);
            nodeValue.Add(val);
            return idx;
        }

        for (int sym = 0; sym < size; sym++)
        {
            int numBits = bits[sym];
            if (numBits == 0) continue;

            uint code = codes[sym];
            int current = 0;

            for (int bit = numBits - 1; bit >= 0; bit--)
            {
                bool isOne = ((code >> bit) & 1) != 0;

                if (bit == 0)
                {
                    // Last bit — create leaf
                    int leafIdx = AddLeaf(sym);
                    if (isOne)
                        nodeRight[current] = leafIdx;
                    else
                        nodeLeft[current] = leafIdx;
                }
                else
                {
                    // Navigate or create intermediate branch
                    if (isOne)
                    {
                        if (nodeRight[current] == -1)
                            nodeRight[current] = AddBranch();
                        current = nodeRight[current];
                    }
                    else
                    {
                        if (nodeLeft[current] == -1)
                            nodeLeft[current] = AddBranch();
                        current = nodeLeft[current];
                    }
                }
            }
        }

        // Convert to HuffmanNode[]
        var result = new HuffmanNode[nodeLeft.Count];
        for (int i = 0; i < result.Length; i++)
        {
            if (nodeIsLeaf[i])
                result[i] = new HuffmanNode(nodeValue[i]);
            else
                result[i] = new HuffmanNode(nodeLeft[i], nodeRight[i]);
        }
        return result;
    }
}

/// <summary>
/// Huffman tree node for AAC codebook decoding.
/// </summary>
public readonly struct HuffmanNode
{
    /// <summary>Left child index (if branch) or packed value (if leaf).</summary>
    public readonly short Left;
    
    /// <summary>Right child index (if branch) or -1 (if leaf).</summary>
    public readonly short Right;
    
    /// <summary>Packed value for leaf nodes.</summary>
    public readonly short Value;
    
    /// <summary>True if this is a leaf node (has a value).</summary>
    public bool IsLeaf => Right < 0;

    /// <summary>Creates a branch node.</summary>
    public HuffmanNode(int left, int right)
    {
        Left = (short)left;
        Right = (short)right;
        Value = 0;
    }

    /// <summary>Creates a leaf node with a value.</summary>
    public HuffmanNode(int value)
    {
        Left = 0;
        Right = -1;
        Value = (short)value;
    }
}

/// <summary>
/// AAC scalefactor decoder using Huffman codebook.
/// </summary>
public static class AacScalefactorDecoder
{
    /// <summary>
    /// Decodes a single scalefactor delta using the ISO/IEC 14496-3 Huffman table.
    /// Returns values from -60 to +60.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeScalefactorDelta(ref AacBitReader reader)
    {
        // Read bits and match against the scalefactor Huffman table.
        // The table has 121 entries mapping to delta values -60..+60.
        var codes = AacSpectralTables.ScalefactorCodes;
        var bits = AacSpectralTables.ScalefactorBits;

        uint codeword = 0;
        for (int numBits = 1; numBits <= 19; numBits++)
        {
            codeword = (codeword << 1) | (uint)(reader.ReadBit() ? 1 : 0);

            // Check entries with this bit length
            for (int i = 0; i < 121; i++)
            {
                if (bits[i] == numBits && codes[i] == codeword)
                    return i - 60;
            }
        }

        return 0; // Error fallback
    }
}

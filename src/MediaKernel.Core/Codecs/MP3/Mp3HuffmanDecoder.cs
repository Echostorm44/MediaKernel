// Licensed under the MIT License.
// MP3 Huffman decoder ported from FFmpeg's mpegaudiodec_common.c / mpegaudiodec_template.c.
// Uses binary trie tables built from FFmpeg's mpa_hufflens[] and mpa_huffsymbols[] data
// with FFmpeg's ff_vlc_init_from_lengths code assignment (NOT standard canonical Huffman).

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3;

/// <summary>
/// Huffman decoder for MP3 spectral data.
/// Decodes the compressed frequency coefficients from the big_values and count1 regions.
/// Tables and decode logic ported from FFmpeg (LGPL).
/// </summary>
public static class Mp3HuffmanDecoder
{
    public const int SpectralCoefficientsPerGranule = 576;

    /// <summary>
    /// Linbits for each table index (0-31). From ff_mpa_huff_data[table][1].
    /// </summary>
    private static readonly int[] TableLinbits =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 2, 3, 4, 6, 8, 10, 13, 4, 5, 6, 7, 8, 9, 11, 13
    ];

    /// <summary>
    /// Maps table index (1-31) to internal canonical table index (0-14).
    /// From ff_mpa_huff_data[table][0]. Table 0 and 4/14 are zero tables.
    /// </summary>
    private static readonly int[] TableToCanonical =
    [
     // table:  0   1   2   3   4   5   6   7   8   9  10  11  12  13  14  15
               -1,  0,  1,  2, -1,  3,  4,  5,  6,  7,  8,  9, 10, 11, -1, 12,
     //        16  17  18  19  20  21  22  23  24  25  26  27  28  29  30  31
               13, 13, 13, 13, 13, 13, 13, 13, 14, 14, 14, 14, 14, 14, 14, 14
    ];

    /// <summary>
    /// Max x/y value for each table (before linbits). xMax for canonical tables.
    /// </summary>
    private static readonly int[] TableXMax =
    [
     // table:  0   1   2   3   4   5   6   7   8   9  10  11  12  13  14  15
                0,  1,  2,  2,  0,  3,  3,  5,  5,  5,  7,  7,  7, 15,  0, 15,
     //        16  17  18  19  20  21  22  23  24  25  26  27  28  29  30  31
               15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15
    ];

    // ===== Huffman trie tables built from FFmpeg's mpa_hufflens/mpa_huffsymbols =====
    // Built using FFmpeg's ff_vlc_init_from_lengths code assignment (NOT standard canonical).
    // Each table is a binary trie: walk bit-by-bit from root until reaching a leaf.

    private static readonly HuffNode[][] HuffTrees = BuildHuffTrees();

    /// <summary>
    /// Decodes spectral data (Huffman-coded frequency coefficients) for a granule.
    /// </summary>
    public static bool DecodeSpectralData(
        ref Mp3BitReader reader,
        in Mp3GranuleInfo granuleInfo,
        Span<int> spectralData,
        int huffmanBitBudget,
        int sampleRate,
        out int bitsRead)
    {
        bitsRead = 0;

        if (spectralData.Length < SpectralCoefficientsPerGranule)
            return false;

        spectralData.Slice(0, SpectralCoefficientsPerGranule).Clear();

        int totalBits = huffmanBitBudget;
        int startBits = reader.BitsRead;
        int endBits = startBits + totalBits;

        // Region boundaries
        CalculateRegionBoundaries(in granuleInfo, sampleRate, out int region1Start, out int region2Start);

        int bigValuesEnd = Math.Min(granuleInfo.BigValues * 2, SpectralCoefficientsPerGranule);
        int index = 0;

        // Big-values regions: FFmpeg checks pos >= end_pos BEFORE each pair decode,
        // so a VLC read that crosses the boundary keeps its values. We match this by
        // only checking BitsRead < endBits in the while condition (pre-check).
        // Region 0
        int table0 = granuleInfo.TableSelect[0];
        while (index < Math.Min(region1Start, bigValuesEnd) && reader.BitsRead < endBits)
        {
            if (!DecodeBigValuePair(ref reader, table0, spectralData, index))
                break;
            index += 2;
        }

        // Region 1
        int table1 = granuleInfo.TableSelect[1];
        while (index < Math.Min(region2Start, bigValuesEnd) && reader.BitsRead < endBits)
        {
            if (!DecodeBigValuePair(ref reader, table1, spectralData, index))
                break;
            index += 2;
        }

        // Region 2
        int table2 = granuleInfo.TableSelect[2];
        while (index < bigValuesEnd && reader.BitsRead < endBits)
        {
            if (!DecodeBigValuePair(ref reader, table2, spectralData, index))
                break;
            index += 2;
        }

        // Count1 region (quadruples with tables 32/33)
        // FFmpeg handles overread by backing up the last quadruple
        int count1Table = granuleInfo.Count1TableSelect;
        int lastCount1Pos = reader.BitsRead;
        while (index + 4 <= SpectralCoefficientsPerGranule && reader.BitsRead < endBits)
        {
            lastCount1Pos = reader.BitsRead;
            if (!DecodeCount1Quadruple(ref reader, count1Table, spectralData, index))
                break;
            // If overread, undo last quadruple (like FFmpeg)
            if (reader.BitsRead > endBits)
            {
                spectralData[index] = 0;
                spectralData[index + 1] = 0;
                spectralData[index + 2] = 0;
                spectralData[index + 3] = 0;
                reader.Seek(lastCount1Pos);
                break;
            }
            index += 4;
        }

        bitsRead = reader.BitsRead - startBits;

        return true;
    }

    /// <summary>
    /// Calculates region boundaries from granule side info and scalefactor band tables.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CalculateRegionBoundaries(
        in Mp3GranuleInfo granuleInfo,
        int sampleRate,
        out int region1Start,
        out int region2Start)
    {
        if (granuleInfo.WindowSwitchingFlag && granuleInfo.BlockType == 2)
        {
            region1Start = 36;
            region2Start = SpectralCoefficientsPerGranule;
        }
        else
        {
            ReadOnlySpan<int> sfbTable = Mp3HuffmanTables.GetScalefactorBandLong(sampleRate);
            int region0Bands = granuleInfo.Region0Count + 1;
            int region1Bands = granuleInfo.Region1Count + 1;
            int region0End = Math.Min(region0Bands, sfbTable.Length - 1);
            int region1End = Math.Min(region0Bands + region1Bands, sfbTable.Length - 1);
            region1Start = sfbTable[region0End];
            region2Start = sfbTable[region1End];
        }
    }

    /// <summary>
    /// Decodes a pair of values from the big_values region using canonical Huffman tables.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool DecodeBigValuePair(
        ref Mp3BitReader reader,
        int table,
        Span<int> spectralData,
        int index)
    {
        if (table == 0 || table == 4 || table == 14)
        {
            spectralData[index] = 0;
            spectralData[index + 1] = 0;
            return true;
        }

        int canonIdx = TableToCanonical[table];
        if (canonIdx < 0 || canonIdx >= HuffTrees.Length)
        {
            spectralData[index] = 0;
            spectralData[index + 1] = 0;
            return true;
        }

        HuffNode[] tree = HuffTrees[canonIdx];
        if (!DecodeHuffTree(ref reader, tree, out int x, out int y))
            return false;

        // Handle linbits extension for max values
        int linbits = TableLinbits[table];
        int xMax = TableXMax[table];

        if (x == xMax && linbits > 0)
            x += reader.ReadBits(linbits);
        if (x != 0 && reader.ReadBits(1) == 1)
            x = -x;

        if (y == xMax && linbits > 0)
            y += reader.ReadBits(linbits);
        if (y != 0 && reader.ReadBits(1) == 1)
            y = -y;

        spectralData[index] = x;
        spectralData[index + 1] = y;
        return true;
    }

    /// <summary>
    /// Decodes a quadruple of values (v, w, x, y ∈ {-1, 0, 1}) from the count1 region.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool DecodeCount1Quadruple(
        ref Mp3BitReader reader,
        int tableSelect,
        Span<int> spectralData,
        int index)
    {
        int pattern;
        if (tableSelect != 0)
        {
            // Table B: 4-bit fixed codes, code-to-symbol mapping is reversed
            // FFmpeg's mpa_quad_codes[1] = {15,14,13,...,0} means symbol N has code (15-N)
            // So: pattern = 15 - code
            pattern = 15 - reader.ReadBits(4);
        }
        else
        {
            // Table A: Variable-length Huffman codes
            pattern = DecodeCount1TableA(ref reader);
        }

        // pattern bits: v(bit3) w(bit2) x(bit1) y(bit0)
        int v = (pattern >> 3) & 1;
        int w = (pattern >> 2) & 1;
        int x = (pattern >> 1) & 1;
        int y = pattern & 1;

        // Read sign bits for nonzero values
        if (v != 0) v = reader.ReadBits(1) == 1 ? -1 : 1;
        if (w != 0) w = reader.ReadBits(1) == 1 ? -1 : 1;
        if (x != 0) x = reader.ReadBits(1) == 1 ? -1 : 1;
        if (y != 0) y = reader.ReadBits(1) == 1 ? -1 : 1;

        spectralData[index] = v;
        spectralData[index + 1] = w;
        spectralData[index + 2] = x;
        spectralData[index + 3] = y;
        return true;
    }

    /// <summary>
    /// Decodes a symbol by walking a binary Huffman trie bit-by-bit.
    /// </summary>
    private static bool DecodeHuffTree(
        ref Mp3BitReader reader,
        HuffNode[] tree,
        out int x, out int y)
    {
        int node = 0; // root
        while (tree[node].SymbolX < 0) // while internal node
        {
            int bit = reader.ReadBits(1);
            node = bit == 0 ? tree[node].Left : tree[node].Right;
            if (node < 0)
            {
                x = 0; y = 0;
                return false;
            }
        }
        x = tree[node].SymbolX;
        y = tree[node].SymbolY;
        return true;
    }

    /// <summary>
    /// Decodes count1 table A (variable-length Huffman for quadruples).
    /// </summary>
    private static int DecodeCount1TableA(ref Mp3BitReader reader)
    {
        // Table A codes from FFmpeg: mpa_quad_codes[0] / mpa_quad_bits[0]
        // Codes sorted by bit length for canonical decode
        // Length 1: pattern 0000 (code 1)
        // Length 4: patterns with 4-bit codes
        // Length 5: patterns with 5-bit codes
        // Length 6: patterns with 6-bit codes

        // Use bit-by-bit matching against known codes
        int code = 0;
        for (int len = 1; len <= 6; len++)
        {
            code = (code << 1) | reader.ReadBits(1);
            ref readonly HuffLevel level = ref Count1TableACanonical.Levels[len];
            if (level.Count > 0 && code >= level.FirstCode && code < level.FirstCode + level.Count)
            {
                return Count1TableACanonical.SymbolsX[level.SymbolOffset + (code - level.FirstCode)];
            }
        }

        return 0;
    }

    private static readonly CanonicalHuffTable Count1TableACanonical = BuildCount1TableA();

    // ===== Table data from FFmpeg's mpegaudiodec_common.c =====

    // Bit lengths for all 15 unique Huffman tables, concatenated.
    // Tables: 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16, 24
    private static ReadOnlySpan<byte> MpaHuffLens =>
    [
// Table 1 - 4 entries
        3, 3, 2, 1,
        // Table 2 - 9 entries
        6, 6, 5, 5, 5, 3, 3, 3, 1,
        // Table 3 - 9 entries
        6, 6, 5, 5, 5, 3, 2, 2, 2,
        // Table 5 - 16 entries
        8, 8, 7, 6, 7, 7, 7, 7, 6, 6, 6, 6, 3, 3, 3, 1,
        // Table 6 - 16 entries
        7, 7, 6, 6, 6, 5, 5, 5, 5, 4, 4, 4, 3, 2, 3, 3,
        // Table 7 - 36 entries
        10, 10, 10, 10, 9, 9, 9, 9, 8, 8, 9, 9, 8, 9, 9, 8, 8, 7, 7,
        7, 8, 8, 8, 8, 7, 7, 7, 7, 6, 5, 6, 6, 4, 3, 3, 1,
        // Table 8 - 36 entries
        11, 11, 10, 9, 10, 10, 9, 9, 9, 8, 8, 9, 9, 9, 9, 8, 8, 8, 7,
        8, 8, 8, 8, 8, 8, 8, 8, 6, 6, 6, 4, 4, 2, 3, 3, 2,
        // Table 9 - 36 entries
        9, 9, 8, 8, 9, 9, 8, 8, 8, 8, 7, 7, 7, 8, 8, 7, 7, 7, 7,
        6, 6, 6, 6, 5, 5, 6, 6, 5, 5, 4, 4, 4, 3, 3, 3, 3,
        // Table 10 - 64 entries
        11, 11, 11, 11, 11, 11, 10, 10, 10, 10, 10, 10, 10, 11, 11, 10, 9, 9, 10,
        10, 9, 9, 10, 10, 9, 10, 10, 8, 8, 9, 9, 10, 10, 9, 9, 10, 10, 8,
        8, 8, 9, 9, 9, 9, 9, 9, 8, 8, 8, 8, 8, 8, 7, 7, 7, 7, 6,
        6, 6, 6, 4, 3, 3, 1,
        // Table 11 - 64 entries
        10, 10, 10, 10, 10, 10, 10, 11, 11, 10, 10, 9, 9, 9, 10, 10, 10, 10, 8,
        8, 9, 9, 7, 8, 8, 8, 8, 8, 9, 9, 9, 9, 8, 7, 8, 8, 7, 7,
        8, 8, 8, 9, 9, 8, 8, 8, 8, 8, 8, 7, 7, 6, 6, 7, 7, 6, 5,
        4, 5, 5, 3, 3, 3, 2,
        // Table 12 - 64 entries
        10, 10, 9, 9, 9, 9, 9, 9, 9, 8, 8, 9, 9, 8, 8, 8, 8, 8, 8,
        9, 9, 8, 8, 8, 8, 8, 9, 9, 7, 7, 7, 8, 8, 8, 8, 8, 8, 7,
        7, 7, 7, 8, 8, 7, 7, 7, 6, 6, 6, 6, 7, 7, 6, 5, 5, 5, 4,
        4, 5, 5, 4, 3, 3, 3,
        // Table 13 - 256 entries
        19, 19, 18, 17, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 17, 17, 15, 15, 16,
        16, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 16, 16, 15, 16, 16, 14, 14, 15,
        15, 15, 15, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 15, 15, 14, 13, 14,
        14, 13, 13, 14, 14, 13, 14, 14, 13, 14, 14, 13, 14, 14, 13, 13, 14, 14, 12,
        12, 12, 13, 13, 13, 13, 13, 13, 12, 13, 13, 12, 12, 13, 13, 13, 13, 13, 13,
        13, 13, 13, 13, 13, 13, 12, 12, 13, 13, 12, 12, 12, 12, 13, 13, 13, 13, 12,
        13, 13, 12, 11, 12, 12, 12, 12, 12, 12, 12, 12, 11, 11, 11, 11, 12, 12, 11,
        11, 12, 12, 11, 12, 12, 12, 12, 11, 11, 12, 12, 11, 12, 12, 11, 12, 12, 11,
        12, 12, 10, 10, 10, 11, 11, 11, 11, 11, 11, 11, 11, 10, 10, 10, 10, 11, 11,
        10, 11, 11, 10, 11, 11, 11, 11, 10, 10, 11, 11, 10, 10, 11, 11, 11, 11, 11,
        11, 9, 9, 10, 10, 10, 10, 10, 11, 11, 9, 9, 9, 10, 10, 9, 9, 10, 10,
        10, 10, 10, 10, 10, 10, 10, 10, 8, 9, 9, 9, 9, 9, 9, 10, 10, 9, 9,
        9, 8, 8, 9, 9, 9, 9, 9, 9, 8, 7, 8, 8, 8, 8, 7, 7, 7, 7,
        7, 6, 6, 6, 6, 4, 4, 3, 1,
        // Table 15 - 256 entries
        13, 13, 13, 13, 12, 13, 13, 13, 13, 13, 13, 12, 13, 13, 12, 12, 12, 12, 12,
        12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 13,
        13, 11, 11, 12, 12, 12, 12, 11, 11, 11, 11, 11, 11, 12, 12, 11, 11, 11, 11,
        11, 11, 11, 11, 12, 12, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11,
        11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 12, 12, 11, 11, 11, 11, 11,
        11, 10, 11, 11, 11, 11, 11, 11, 10, 10, 11, 11, 10, 10, 10, 10, 11, 11, 10,
        10, 10, 10, 10, 10, 10, 11, 11, 10, 10, 10, 10, 10, 11, 11, 9, 10, 10, 10,
        10, 10, 10, 10, 10, 10, 10, 10, 10, 9, 10, 10, 10, 10, 9, 10, 10, 9, 10,
        10, 10, 10, 10, 10, 10, 10, 9, 9, 9, 9, 9, 9, 9, 10, 10, 9, 9, 9,
        9, 9, 9, 10, 10, 9, 9, 9, 9, 9, 9, 8, 9, 9, 9, 9, 9, 9, 9,
        9, 9, 9, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9, 9, 9, 8, 8, 8, 8,
        8, 8, 9, 9, 8, 8, 8, 8, 8, 8, 8, 9, 9, 8, 7, 8, 8, 7, 7,
        7, 7, 8, 8, 7, 7, 7, 7, 7, 6, 7, 7, 6, 6, 7, 7, 6, 6, 6,
        5, 5, 5, 5, 5, 3, 4, 4, 3,
        // Table 16 - 256 entries
        11, 11, 11, 11, 11, 11, 11, 11, 10, 11, 11, 11, 11, 10, 10, 10, 10, 10, 8,
        10, 10, 9, 9, 9, 9, 10, 16, 17, 17, 15, 15, 16, 16, 14, 15, 15, 14, 14,
        15, 15, 14, 14, 15, 15, 15, 15, 14, 15, 15, 14, 13, 8, 9, 9, 8, 8, 13,
        14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 13, 13, 14, 14, 14, 14, 13, 14, 14,
        13, 13, 13, 14, 14, 14, 14, 13, 13, 14, 14, 13, 14, 14, 12, 13, 13, 13, 13,
        13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 12, 13, 13, 13, 13, 13, 13, 12, 13,
        13, 12, 12, 13, 13, 11, 12, 12, 12, 12, 12, 12, 12, 13, 13, 11, 12, 12, 12,
        12, 11, 12, 12, 12, 12, 12, 12, 12, 12, 11, 12, 12, 11, 11, 11, 11, 12, 12,
        12, 12, 12, 12, 12, 12, 11, 12, 12, 11, 12, 12, 11, 12, 12, 11, 12, 12, 11,
        10, 10, 11, 11, 11, 11, 11, 11, 10, 10, 11, 11, 10, 10, 11, 11, 11, 11, 11,
        11, 11, 11, 10, 11, 11, 10, 10, 10, 11, 11, 10, 10, 11, 11, 10, 10, 11, 11,
        10, 9, 9, 10, 10, 10, 10, 10, 10, 9, 9, 9, 10, 10, 9, 10, 10, 9, 9,
        8, 9, 9, 9, 9, 9, 9, 9, 9, 8, 8, 9, 9, 8, 8, 7, 7, 8, 8,
        7, 6, 6, 6, 6, 4, 4, 3, 1,
        // Table 24 - 256 entries
        8, 8, 8, 8, 8, 8, 8, 8, 7, 8, 8, 7, 7, 8, 8, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7, 8, 8, 9, 11, 11, 11, 11, 11, 11, 11, 11,
        11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11,
        11, 4, 11, 11, 11, 11, 12, 12, 11, 10, 11, 11, 10, 10, 10, 10, 11, 11, 10,
        10, 10, 10, 11, 11, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
        10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
        10, 10, 10, 10, 10, 10, 11, 11, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
        10, 10, 11, 11, 10, 11, 11, 10, 9, 10, 10, 10, 10, 11, 11, 10, 9, 9, 10,
        10, 9, 10, 10, 10, 10, 9, 9, 10, 10, 9, 9, 9, 9, 9, 9, 9, 9, 9,
        9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9,
        9, 9, 9, 9, 9, 9, 9, 9, 10, 10, 9, 9, 9, 10, 10, 8, 9, 9, 8,
        8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 9, 9, 8, 8, 8, 8, 8,
        8, 9, 9, 7, 8, 8, 7, 7, 7, 7, 7, 8, 8, 7, 7, 6, 6, 7, 7,
        6, 5, 5, 6, 6, 4, 4, 4, 4
    ];

    // Symbol values (packed x|y nibbles) for all 15 tables, concatenated.
    private static ReadOnlySpan<byte> MpaHuffSymbols =>
    [
// Table 1 - 4 entries
        0x11, 0x01, 0x10, 0x00,
        // Table 2 - 9 entries
        0x22, 0x02, 0x12, 0x21, 0x20, 0x11, 0x01, 0x10, 0x00,
        // Table 3 - 9 entries
        0x22, 0x02, 0x12, 0x21, 0x20, 0x10, 0x11, 0x01, 0x00,
        // Table 5 - 16 entries
        0x33, 0x23, 0x32, 0x31, 0x13, 0x03, 0x30, 0x22, 0x12, 0x21, 0x02, 0x20,
        0x11, 0x01, 0x10, 0x00,
        // Table 6 - 16 entries
        0x33, 0x03, 0x23, 0x32, 0x30, 0x13, 0x31, 0x22, 0x02, 0x12, 0x21, 0x20,
        0x01, 0x11, 0x10, 0x00,
        // Table 7 - 36 entries
        0x55, 0x45, 0x54, 0x53, 0x35, 0x44, 0x25, 0x52, 0x15, 0x51, 0x05, 0x34,
        0x50, 0x43, 0x33, 0x24, 0x42, 0x14, 0x41, 0x40, 0x04, 0x23, 0x32, 0x03,
        0x13, 0x31, 0x30, 0x22, 0x12, 0x21, 0x02, 0x20, 0x11, 0x01, 0x10, 0x00,
        // Table 8 - 36 entries
        0x55, 0x54, 0x45, 0x53, 0x35, 0x44, 0x25, 0x52, 0x05, 0x15, 0x51, 0x34,
        0x43, 0x50, 0x33, 0x24, 0x42, 0x14, 0x41, 0x04, 0x40, 0x23, 0x32, 0x13,
        0x31, 0x03, 0x30, 0x22, 0x02, 0x20, 0x12, 0x21, 0x11, 0x01, 0x10, 0x00,
        // Table 9 - 36 entries
        0x55, 0x45, 0x35, 0x53, 0x54, 0x05, 0x44, 0x25, 0x52, 0x15, 0x51, 0x34,
        0x43, 0x50, 0x04, 0x24, 0x42, 0x33, 0x40, 0x14, 0x41, 0x23, 0x32, 0x13,
        0x31, 0x03, 0x30, 0x22, 0x02, 0x12, 0x21, 0x20, 0x11, 0x01, 0x10, 0x00,
        // Table 10 - 64 entries
        0x77, 0x67, 0x76, 0x57, 0x75, 0x66, 0x47, 0x74, 0x56, 0x65, 0x37, 0x73,
        0x46, 0x55, 0x54, 0x63, 0x27, 0x72, 0x64, 0x07, 0x70, 0x62, 0x45, 0x35,
        0x06, 0x53, 0x44, 0x17, 0x71, 0x36, 0x26, 0x25, 0x52, 0x15, 0x51, 0x34,
        0x43, 0x16, 0x61, 0x60, 0x05, 0x50, 0x24, 0x42, 0x33, 0x04, 0x14, 0x41,
        0x40, 0x23, 0x32, 0x03, 0x13, 0x31, 0x30, 0x22, 0x12, 0x21, 0x02, 0x20,
        0x11, 0x01, 0x10, 0x00,
        // Table 11 - 64 entries
        0x77, 0x67, 0x76, 0x75, 0x66, 0x47, 0x74, 0x57, 0x55, 0x56, 0x65, 0x37,
        0x73, 0x46, 0x45, 0x54, 0x35, 0x53, 0x27, 0x72, 0x64, 0x07, 0x71, 0x17,
        0x70, 0x36, 0x63, 0x60, 0x44, 0x25, 0x52, 0x05, 0x15, 0x62, 0x26, 0x06,
        0x16, 0x61, 0x51, 0x34, 0x50, 0x43, 0x33, 0x24, 0x42, 0x14, 0x41, 0x04,
        0x40, 0x23, 0x32, 0x13, 0x31, 0x03, 0x30, 0x22, 0x21, 0x12, 0x02, 0x20,
        0x11, 0x01, 0x10, 0x00,
        // Table 12 - 64 entries
        0x77, 0x67, 0x76, 0x57, 0x75, 0x66, 0x47, 0x74, 0x65, 0x56, 0x37, 0x73,
        0x55, 0x27, 0x72, 0x46, 0x64, 0x17, 0x71, 0x07, 0x70, 0x36, 0x63, 0x45,
        0x54, 0x44, 0x06, 0x05, 0x26, 0x62, 0x61, 0x16, 0x60, 0x35, 0x53, 0x25,
        0x52, 0x15, 0x51, 0x34, 0x43, 0x50, 0x04, 0x24, 0x42, 0x14, 0x33, 0x41,
        0x23, 0x32, 0x40, 0x03, 0x30, 0x13, 0x31, 0x22, 0x12, 0x21, 0x02, 0x20,
        0x00, 0x11, 0x01, 0x10,
        // Table 13 - 256 entries
        0xFE, 0xFC, 0xFD, 0xED, 0xFF, 0xEF, 0xDF, 0xEE, 0xCF, 0xDE, 0xBF, 0xFB,
        0xCE, 0xDC, 0xAF, 0xE9, 0xEC, 0xDD, 0xFA, 0xCD, 0xBE, 0xEB, 0x9F, 0xF9,
        0xEA, 0xBD, 0xDB, 0x8F, 0xF8, 0xCC, 0xAE, 0x9E, 0x8E, 0x7F, 0x7E, 0xF7,
        0xDA, 0xAD, 0xBC, 0xCB, 0xF6, 0x6F, 0xE8, 0x5F, 0x9D, 0xD9, 0xF5, 0xE7,
        0xAC, 0xBB, 0x4F, 0xF4, 0xCA, 0xE6, 0xF3, 0x3F, 0x8D, 0xD8, 0x2F, 0xF2,
        0x6E, 0x9C, 0x0F, 0xC9, 0x5E, 0xAB, 0x7D, 0xD7, 0x4E, 0xC8, 0xD6, 0x3E,
        0xB9, 0x9B, 0xAA, 0x1F, 0xF1, 0xF0, 0xBA, 0xE5, 0xE4, 0x8C, 0x6D, 0xE3,
        0xE2, 0x2E, 0x0E, 0x1E, 0xE1, 0xE0, 0x5D, 0xD5, 0x7C, 0xC7, 0x4D, 0x8B,
        0xB8, 0xD4, 0x9A, 0xA9, 0x6C, 0xC6, 0x3D, 0xD3, 0x7B, 0x2D, 0xD2, 0x1D,
        0xB7, 0x5C, 0xC5, 0x99, 0x7A, 0xC3, 0xA7, 0x97, 0x4B, 0xD1, 0x0D, 0xD0,
        0x8A, 0xA8, 0x4C, 0xC4, 0x6B, 0xB6, 0x3C, 0x2C, 0xC2, 0x5B, 0xB5, 0x89,
        0x1C, 0xC1, 0x98, 0x0C, 0xC0, 0xB4, 0x6A, 0xA6, 0x79, 0x3B, 0xB3, 0x88,
        0x5A, 0x2B, 0xA5, 0x69, 0xA4, 0x78, 0x87, 0x94, 0x77, 0x76, 0xB2, 0x1B,
        0xB1, 0x0B, 0xB0, 0x96, 0x4A, 0x3A, 0xA3, 0x59, 0x95, 0x2A, 0xA2, 0x1A,
        0xA1, 0x0A, 0x68, 0xA0, 0x86, 0x49, 0x93, 0x39, 0x58, 0x85, 0x67, 0x29,
        0x92, 0x57, 0x75, 0x38, 0x83, 0x66, 0x47, 0x74, 0x56, 0x65, 0x73, 0x19,
        0x91, 0x09, 0x90, 0x48, 0x84, 0x72, 0x46, 0x64, 0x28, 0x82, 0x18, 0x37,
        0x27, 0x17, 0x71, 0x55, 0x07, 0x70, 0x36, 0x63, 0x45, 0x54, 0x26, 0x62,
        0x35, 0x81, 0x08, 0x80, 0x16, 0x61, 0x06, 0x60, 0x53, 0x44, 0x25, 0x52,
        0x05, 0x15, 0x51, 0x34, 0x43, 0x50, 0x24, 0x42, 0x33, 0x14, 0x41, 0x04,
        0x40, 0x23, 0x32, 0x13, 0x31, 0x03, 0x30, 0x22, 0x12, 0x21, 0x02, 0x20,
        0x11, 0x01, 0x10, 0x00,
        // Table 15 - 256 entries
        0xFF, 0xEF, 0xFE, 0xDF, 0xEE, 0xFD, 0xCF, 0xFC, 0xDE, 0xED, 0xBF, 0xFB,
        0xCE, 0xEC, 0xDD, 0xAF, 0xFA, 0xBE, 0xEB, 0xCD, 0xDC, 0x9F, 0xF9, 0xEA,
        0xBD, 0xDB, 0x8F, 0xF8, 0xCC, 0x9E, 0xE9, 0x7F, 0xF7, 0xAD, 0xDA, 0xBC,
        0x6F, 0xAE, 0x0F, 0xCB, 0xF6, 0x8E, 0xE8, 0x5F, 0x9D, 0xF5, 0x7E, 0xE7,
        0xAC, 0xCA, 0xBB, 0xD9, 0x8D, 0x4F, 0xF4, 0x3F, 0xF3, 0xD8, 0xE6, 0x2F,
        0xF2, 0x6E, 0xF0, 0x1F, 0xF1, 0x9C, 0xC9, 0x5E, 0xAB, 0xBA, 0xE5, 0x7D,
        0xD7, 0x4E, 0xE4, 0x8C, 0xC8, 0x3E, 0x6D, 0xD6, 0xE3, 0x9B, 0xB9, 0x2E,
        0xAA, 0xE2, 0x1E, 0xE1, 0x0E, 0xE0, 0x5D, 0xD5, 0x7C, 0xC7, 0x4D, 0x8B,
        0xD4, 0xB8, 0x9A, 0xA9, 0x6C, 0xC6, 0x3D, 0xD3, 0xD2, 0x2D, 0x0D, 0x1D,
        0x7B, 0xB7, 0xD1, 0x5C, 0xD0, 0xC5, 0x8A, 0xA8, 0x4C, 0xC4, 0x6B, 0xB6,
        0x99, 0x0C, 0x3C, 0xC3, 0x7A, 0xA7, 0xA6, 0xC0, 0x0B, 0xC2, 0x2C, 0x5B,
        0xB5, 0x1C, 0x89, 0x98, 0xC1, 0x4B, 0xB4, 0x6A, 0x3B, 0x79, 0xB3, 0x97,
        0x88, 0x2B, 0x5A, 0xB2, 0xA5, 0x1B, 0xB1, 0xB0, 0x69, 0x96, 0x4A, 0xA4,
        0x78, 0x87, 0x3A, 0xA3, 0x59, 0x95, 0x2A, 0xA2, 0x1A, 0xA1, 0x0A, 0xA0,
        0x68, 0x86, 0x49, 0x94, 0x39, 0x93, 0x77, 0x09, 0x58, 0x85, 0x29, 0x67,
        0x76, 0x92, 0x91, 0x19, 0x90, 0x48, 0x84, 0x57, 0x75, 0x38, 0x83, 0x66,
        0x47, 0x28, 0x82, 0x18, 0x81, 0x74, 0x08, 0x80, 0x56, 0x65, 0x37, 0x73,
        0x46, 0x27, 0x72, 0x64, 0x17, 0x55, 0x71, 0x07, 0x70, 0x36, 0x63, 0x45,
        0x54, 0x26, 0x62, 0x16, 0x06, 0x60, 0x35, 0x61, 0x53, 0x44, 0x25, 0x52,
        0x15, 0x51, 0x05, 0x50, 0x34, 0x43, 0x24, 0x42, 0x33, 0x41, 0x14, 0x04,
        0x23, 0x32, 0x40, 0x03, 0x13, 0x31, 0x30, 0x22, 0x12, 0x21, 0x02, 0x20,
        0x11, 0x01, 0x10, 0x00,
        // Table 16 - 256 entries
        0xEF, 0xFE, 0xDF, 0xFD, 0xCF, 0xFC, 0xBF, 0xFB, 0xAF, 0xFA, 0x9F, 0xF9,
        0xF8, 0x8F, 0x7F, 0xF7, 0x6F, 0xF6, 0xFF, 0x5F, 0xF5, 0x4F, 0xF4, 0xF3,
        0xF0, 0x3F, 0xCE, 0xEC, 0xDD, 0xDE, 0xE9, 0xEA, 0xD9, 0xEE, 0xED, 0xEB,
        0xBE, 0xCD, 0xDC, 0xDB, 0xAE, 0xCC, 0xAD, 0xDA, 0x7E, 0xAC, 0xCA, 0xC9,
        0x7D, 0x5E, 0xBD, 0xF2, 0x2F, 0x0F, 0x1F, 0xF1, 0x9E, 0xBC, 0xCB, 0x8E,
        0xE8, 0x9D, 0xE7, 0xBB, 0x8D, 0xD8, 0x6E, 0xE6, 0x9C, 0xAB, 0xBA, 0xE5,
        0xD7, 0x4E, 0xE4, 0x8C, 0xC8, 0x3E, 0x6D, 0xD6, 0x9B, 0xB9, 0xAA, 0xE1,
        0xD4, 0xB8, 0xA9, 0x7B, 0xB7, 0xD0, 0xE3, 0x0E, 0xE0, 0x5D, 0xD5, 0x7C,
        0xC7, 0x4D, 0x8B, 0x9A, 0x6C, 0xC6, 0x3D, 0x5C, 0xC5, 0x0D, 0x8A, 0xA8,
        0x99, 0x4C, 0xB6, 0x7A, 0x3C, 0x5B, 0x89, 0x1C, 0xC0, 0x98, 0x79, 0xE2,
        0x2E, 0x1E, 0xD3, 0x2D, 0xD2, 0xD1, 0x3B, 0x97, 0x88, 0x1D, 0xC4, 0x6B,
        0xC3, 0xA7, 0x2C, 0xC2, 0xB5, 0xC1, 0x0C, 0x4B, 0xB4, 0x6A, 0xA6, 0xB3,
        0x5A, 0xA5, 0x2B, 0xB2, 0x1B, 0xB1, 0x0B, 0xB0, 0x69, 0x96, 0x4A, 0xA4,
        0x78, 0x87, 0xA3, 0x3A, 0x59, 0x2A, 0x95, 0x68, 0xA1, 0x86, 0x77, 0x94,
        0x49, 0x57, 0x67, 0xA2, 0x1A, 0x0A, 0xA0, 0x39, 0x93, 0x58, 0x85, 0x29,
        0x92, 0x76, 0x09, 0x19, 0x91, 0x90, 0x48, 0x84, 0x75, 0x38, 0x83, 0x66,
        0x28, 0x82, 0x47, 0x74, 0x18, 0x81, 0x80, 0x08, 0x56, 0x37, 0x73, 0x65,
        0x46, 0x27, 0x72, 0x64, 0x55, 0x07, 0x17, 0x71, 0x70, 0x36, 0x63, 0x45,
        0x54, 0x26, 0x62, 0x16, 0x61, 0x06, 0x60, 0x53, 0x35, 0x44, 0x25, 0x52,
        0x51, 0x15, 0x05, 0x34, 0x43, 0x50, 0x24, 0x42, 0x33, 0x14, 0x41, 0x04,
        0x40, 0x23, 0x32, 0x13, 0x31, 0x03, 0x30, 0x22, 0x12, 0x21, 0x02, 0x20,
        0x11, 0x01, 0x10, 0x00,
        // Table 24 - 256 entries
        0xEF, 0xFE, 0xDF, 0xFD, 0xCF, 0xFC, 0xBF, 0xFB, 0xFA, 0xAF, 0x9F, 0xF9,
        0xF8, 0x8F, 0x7F, 0xF7, 0x6F, 0xF6, 0x5F, 0xF5, 0x4F, 0xF4, 0x3F, 0xF3,
        0x2F, 0xF2, 0xF1, 0x1F, 0xF0, 0x0F, 0xEE, 0xDE, 0xED, 0xCE, 0xEC, 0xDD,
        0xBE, 0xEB, 0xCD, 0xDC, 0xAE, 0xEA, 0xBD, 0xDB, 0xCC, 0x9E, 0xE9, 0xAD,
        0xDA, 0xBC, 0xCB, 0x8E, 0xE8, 0x9D, 0xD9, 0x7E, 0xE7, 0xAC, 0xFF, 0xCA,
        0xBB, 0x8D, 0xD8, 0x0E, 0xE0, 0x0D, 0xE6, 0x6E, 0x9C, 0xC9, 0x5E, 0xBA,
        0xE5, 0xAB, 0x7D, 0xD7, 0xE4, 0x8C, 0xC8, 0x4E, 0x2E, 0x3E, 0x6D, 0xD6,
        0xE3, 0x9B, 0xB9, 0xAA, 0xE2, 0x1E, 0xE1, 0x5D, 0xD5, 0x7C, 0xC7, 0x4D,
        0x8B, 0xB8, 0xD4, 0x9A, 0xA9, 0x6C, 0xC6, 0x3D, 0xD3, 0x2D, 0xD2, 0x1D,
        0x7B, 0xB7, 0xD1, 0x5C, 0xC5, 0x8A, 0xA8, 0x99, 0x4C, 0xC4, 0x6B, 0xB6,
        0xD0, 0x0C, 0x3C, 0xC3, 0x7A, 0xA7, 0x2C, 0xC2, 0x5B, 0xB5, 0x1C, 0x89,
        0x98, 0xC1, 0x4B, 0xC0, 0x0B, 0x3B, 0xB0, 0x0A, 0x1A, 0xB4, 0x6A, 0xA6,
        0x79, 0x97, 0xA0, 0x09, 0x90, 0xB3, 0x88, 0x2B, 0x5A, 0xB2, 0xA5, 0x1B,
        0xB1, 0x69, 0x96, 0xA4, 0x4A, 0x78, 0x87, 0x3A, 0xA3, 0x59, 0x95, 0x2A,
        0xA2, 0xA1, 0x68, 0x86, 0x77, 0x49, 0x94, 0x39, 0x93, 0x58, 0x85, 0x29,
        0x67, 0x76, 0x92, 0x19, 0x91, 0x48, 0x84, 0x57, 0x75, 0x38, 0x83, 0x66,
        0x28, 0x82, 0x18, 0x47, 0x74, 0x81, 0x08, 0x80, 0x56, 0x65, 0x17, 0x07,
        0x70, 0x73, 0x37, 0x27, 0x72, 0x46, 0x64, 0x55, 0x71, 0x36, 0x63, 0x45,
        0x54, 0x26, 0x62, 0x16, 0x61, 0x06, 0x60, 0x35, 0x53, 0x44, 0x25, 0x52,
        0x15, 0x05, 0x50, 0x51, 0x34, 0x43, 0x24, 0x42, 0x33, 0x14, 0x41, 0x04,
        0x40, 0x23, 0x32, 0x13, 0x31, 0x03, 0x30, 0x22, 0x12, 0x21, 0x02, 0x20,
        0x11, 0x01, 0x10, 0x00
    ];

    // Number of entries per canonical table (tables 1,2,3,5,6,7,8,9,10,11,12,13,15,16,24)
    private static ReadOnlySpan<int> TableSizes => [4, 9, 9, 16, 16, 36, 36, 36, 64, 64, 64, 256, 256, 256, 256];

    // ===== Huffman trie node structure =====

    /// <summary>
    /// Node in a Huffman binary trie. Internal nodes have SymbolX = -1.
    /// Leaf nodes store the decoded (x, y) pair.
    /// </summary>
    private struct HuffNode
    {
        public int Left;       // child index for bit 0 (-1 = none)
        public int Right;      // child index for bit 1 (-1 = none)
        public short SymbolX;  // -1 for internal nodes
        public short SymbolY;  // -1 for internal nodes
    }

    /// <summary>
    /// Builds all 15 Huffman trie tables from FFmpeg's mpa_hufflens/mpa_huffsymbols data.
    /// Uses FFmpeg's ff_vlc_init_from_lengths code assignment (process entries in original order,
    /// MSB-aligned code incrementing by 2^(32-len) per entry).
    /// </summary>
    private static HuffNode[][] BuildHuffTrees()
    {
        var trees = new HuffNode[15][];
        ReadOnlySpan<byte> lens = MpaHuffLens;
        ReadOnlySpan<byte> syms = MpaHuffSymbols;
        ReadOnlySpan<int> sizes = TableSizes;

        int offset = 0;
        for (int t = 0; t < 15; t++)
        {
            int n = sizes[t];
            trees[t] = BuildHuffTree(lens.Slice(offset, n), syms.Slice(offset, n));
            offset += n;
        }

        return trees;
    }

    /// <summary>
    /// Builds a single Huffman binary trie using FFmpeg's code assignment algorithm.
    /// Entries are processed in their ORIGINAL order (longest codes first) and codes are
    /// assigned as MSB-aligned values starting from 0, incrementing by 2^(32-len).
    /// </summary>
    private static HuffNode[] BuildHuffTree(ReadOnlySpan<byte> lengths, ReadOnlySpan<byte> symbols)
    {
        int n = lengths.Length;

        // Step 1: Compute actual codes using FFmpeg's ff_vlc_init_from_lengths algorithm
        ulong code = 0;
        Span<int> codeLens = stackalloc int[n];
        Span<uint> normalCodes = stackalloc uint[n];
        Span<byte> symX = stackalloc byte[n];
        Span<byte> symY = stackalloc byte[n];

        for (int i = 0; i < n; i++)
        {
            int len = lengths[i];
            codeLens[i] = len;
            normalCodes[i] = (uint)(code >> (32 - len));
            symX[i] = (byte)((symbols[i] >> 4) & 0xF);
            symY[i] = (byte)(symbols[i] & 0xF);
            code += 1UL << (32 - len);
        }

        // Step 2: Build binary trie by inserting each code
        int maxNodes = 2 * n; // worst case: n leaves + n-1 internal + root
        var tree = new HuffNode[maxNodes];
        int nodeCount = 1; // node 0 is root

        // Initialize root as internal node
        tree[0] = new HuffNode { Left = -1, Right = -1, SymbolX = -1, SymbolY = -1 };

        for (int i = 0; i < n; i++)
        {
            int curNode = 0;
            int len = codeLens[i];
            uint codeVal = normalCodes[i];

            // Walk from MSB to LSB of the code
            for (int bitPos = len - 1; bitPos >= 0; bitPos--)
            {
                int bit = (int)((codeVal >> bitPos) & 1);
                int nextNode = bit == 0 ? tree[curNode].Left : tree[curNode].Right;

                if (nextNode < 0)
                {
                    // Create new node
                    nextNode = nodeCount++;
                    if (bitPos == 0)
                    {
                        // Leaf node
                        tree[nextNode] = new HuffNode
                        {
                            Left = -1,
                            Right = -1,
                            SymbolX = (short)symX[i],
                            SymbolY = (short)symY[i]
                        };
                    }
                    else
                    {
                        // Internal node
                        tree[nextNode] = new HuffNode { Left = -1, Right = -1, SymbolX = -1, SymbolY = -1 };
                    }

                    if (bit == 0)
                        tree[curNode].Left = nextNode;
                    else
                        tree[curNode].Right = nextNode;
                }

                curNode = nextNode;
            }
        }

        // Trim to actual size
        return tree[..nodeCount];
    }

    // ===== Canonical Huffman structures for count1 table (uses explicit codes, not canonical) =====

    private struct HuffLevel
    {
        public int FirstCode;
        public int Count;
        public int SymbolOffset;
    }

    private struct CanonicalHuffTable
    {
        public int MaxCodeLen;
        public HuffLevel[] Levels;
        public byte[] SymbolsX;
        public byte[] SymbolsY;
    }

    /// <summary>
    /// Builds the count1 table A as a canonical Huffman table.
    /// </summary>
    private static CanonicalHuffTable BuildCount1TableA()
    {
        // From FFmpeg: mpa_quad_codes[0] and mpa_quad_bits[0]
        // 16 entries, each symbol is 0-15 (4-bit pattern for v,w,x,y)
        ReadOnlySpan<byte> codes = [1, 5, 4, 5, 6, 5, 4, 4, 7, 3, 6, 0, 7, 2, 3, 1];
        ReadOnlySpan<byte> bits =  [1, 4, 4, 5, 4, 6, 5, 6, 4, 5, 5, 6, 5, 6, 6, 6];

        // Build (length, code, symbol) sorted by length for canonical construction
        // But these are NOT canonical codes — they're explicit codes. We need a different approach.
        // Build a direct lookup: for each (length, code) pair, store the symbol.

        int maxLen = 0;
        for (int i = 0; i < 16; i++)
            if (bits[i] > maxLen) maxLen = bits[i];

        // Sort by (length, code) to build levels
        var sorted = new (int len, int code, int sym)[16];
        for (int i = 0; i < 16; i++)
            sorted[i] = (bits[i], codes[i], i);
        Array.Sort(sorted, (a, b) => a.len != b.len ? a.len.CompareTo(b.len) : a.code.CompareTo(b.code));

        // These are already canonical codes (sorted by len, sequential within each length)
        var levels = new HuffLevel[maxLen + 1];
        var symArr = new byte[16];
        int symIdx = 0;

        for (int i = 0; i < 16; i++)
        {
            int curLen = sorted[i].len;
            if (levels[curLen].Count == 0)
            {
                levels[curLen].FirstCode = sorted[i].code;
                levels[curLen].SymbolOffset = symIdx;
            }
            levels[curLen].Count++;
            symArr[symIdx] = (byte)sorted[i].sym;
            symIdx++;
        }

        return new CanonicalHuffTable
        {
            MaxCodeLen = maxLen,
            Levels = levels,
            SymbolsX = symArr,
            SymbolsY = [] // Not used for count1
        };
    }
}

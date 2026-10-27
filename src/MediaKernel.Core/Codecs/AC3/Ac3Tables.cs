// AC-3 Decoder Tables
// Based on ATSC A/52 specification

namespace MediaKernel.Core.Codecs.AC3;

/// <summary>
/// Lookup tables for AC-3 decoding.
/// </summary>
public static class Ac3Tables
{

    // ── Bit allocation tables, ported verbatim from FFmpeg ───────────────────
    //
    // The previous versions of these were fabricated: BandStartTable was 0,1,2,...,24 rather
    // than the real critical-band boundaries ending at 253, BandSizeTable held placeholder
    // 1/2/4 values, and HearingThresholdTable was a flat array padded with 0x0800. Bit
    // allocation computed from them produced wrong bit-allocation pointers, so mantissa
    // reading consumed the wrong number of bits and ran off the end of every frame.
    // Sources: ff_ac3_band_start_tab, ff_ac3_bin_to_band_tab, ac3_log_add_tab,
    // ac3_hearing_threshold_tab (libavcodec/ac3.c) and ff_ac3_bap_tab (libavcodec/ac3tab.c).

    public static ReadOnlySpan<byte> BandStartTable =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 31, 34, 37, 40, 43,
        46, 49, 55, 61, 67, 73, 79, 85, 97, 109, 121, 133, 157, 181, 205, 229, 253
    ];

    public static ReadOnlySpan<byte> BinToBandTable =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19,
        20, 21, 22, 23, 24, 25, 26, 27, 28, 28, 28, 29, 29, 29, 30, 30, 30, 31, 31, 31,
        32, 32, 32, 33, 33, 33, 34, 34, 34, 35, 35, 35, 35, 35, 35, 36, 36, 36, 36, 36,
        36, 37, 37, 37, 37, 37, 37, 38, 38, 38, 38, 38, 38, 39, 39, 39, 39, 39, 39, 40,
        40, 40, 40, 40, 40, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 42, 42, 42,
        42, 42, 42, 42, 42, 42, 42, 42, 42, 43, 43, 43, 43, 43, 43, 43, 43, 43, 43, 43,
        43, 44, 44, 44, 44, 44, 44, 44, 44, 44, 44, 44, 44, 45, 45, 45, 45, 45, 45, 45,
        45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 45, 46, 46, 46,
        46, 46, 46, 46, 46, 46, 46, 46, 46, 46, 46, 46, 46, 46, 46, 46, 46, 46, 46, 46,
        46, 47, 47, 47, 47, 47, 47, 47, 47, 47, 47, 47, 47, 47, 47, 47, 47, 47, 47, 47,
        47, 47, 47, 47, 47, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48, 48,
        48, 48, 48, 48, 48, 48, 48, 48, 48, 49, 49, 49, 49, 49, 49, 49, 49, 49, 49, 49,
        49, 49, 49, 49, 49, 49, 49, 49, 49, 49, 49, 49, 49
    ];

    public static ReadOnlySpan<byte> LogAddTable =>
    [
        64, 63, 62, 61, 60, 59, 58, 57, 56, 55, 54, 53, 52, 52, 51, 50, 49, 48, 47, 47,
        46, 45, 44, 44, 43, 42, 41, 41, 40, 39, 38, 38, 37, 36, 36, 35, 35, 34, 33, 33,
        32, 32, 31, 30, 30, 29, 29, 28, 28, 27, 27, 26, 26, 25, 25, 24, 24, 23, 23, 22,
        22, 21, 21, 21, 20, 20, 19, 19, 19, 18, 18, 18, 17, 17, 17, 16, 16, 16, 15, 15,
        15, 14, 14, 14, 13, 13, 13, 13, 12, 12, 12, 12, 11, 11, 11, 11, 10, 10, 10, 10,
        10, 9, 9, 9, 9, 9, 8, 8, 8, 8, 8, 8, 7, 7, 7, 7, 7, 7, 6, 6,
        6, 6, 6, 6, 6, 6, 5, 5, 5, 5, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4,
        4, 4, 4, 4, 4, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    ];

    public static ReadOnlySpan<byte> BapTable =>
    [
        0, 1, 1, 1, 1, 1, 2, 2, 3, 3, 3, 4, 4, 5, 5, 6,
        6, 6, 6, 7, 7, 7, 7, 8, 8, 8, 8, 9, 9, 9, 9, 10,
        10, 10, 10, 11, 11, 11, 11, 12, 12, 12, 12, 13, 13, 13, 13, 14,
        14, 14, 14, 14, 14, 14, 14, 15, 15, 15, 15, 15, 15, 15, 15, 15
    ];

    public static ReadOnlySpan<ushort> HearingThresholdTable =>
    [
        1232, 1264, 1408, 1232, 1264, 1408, 1088, 1120, 1200, 1024, 1040, 1104,
        992, 992, 1056, 960, 976, 1008, 944, 960, 992, 944, 944, 976,
        928, 944, 960, 928, 928, 944, 928, 928, 944, 928, 928, 944,
        928, 928, 928, 912, 928, 928, 912, 912, 928, 912, 912, 928,
        896, 912, 928, 896, 896, 928, 880, 896, 928, 880, 896, 928,
        864, 880, 912, 864, 880, 912, 848, 864, 912, 848, 864, 912,
        832, 848, 896, 832, 848, 896, 816, 832, 896, 800, 832, 880,
        784, 800, 864, 768, 784, 848, 752, 768, 832, 752, 752, 816,
        752, 752, 800, 752, 752, 784, 768, 752, 768, 784, 768, 752,
        832, 800, 752, 912, 848, 752, 992, 912, 768, 1056, 992, 784,
        1120, 1056, 816, 1168, 1104, 848, 1184, 1184, 960, 1120, 1168, 1040,
        1088, 1120, 1136, 1088, 1088, 1184, 1312, 1152, 1120, 2048, 1584, 1088,
        2112, 2112, 1104, 2112, 2112, 1248
    ];

    /// <summary>Maximum number of exponent groups.</summary>
    public const int MaxExponentGroups = 85;
    
    /// <summary>Maximum number of coefficients per channel.</summary>
    public const int MaxCoefficients = 256;
    
    /// <summary>
    /// Slow decay rate table (indexed by sdcycod).
    /// </summary>
    public static ReadOnlySpan<byte> SlowDecayTable => [0x0F, 0x11, 0x13, 0x15];
    
    /// <summary>
    /// Fast decay rate table (indexed by fdcycod).
    /// </summary>
    public static ReadOnlySpan<byte> FastDecayTable => [0x3F, 0x53, 0x67, 0x7B];
    
    /// <summary>
    /// Slow gain table (indexed by sgaincod).
    /// </summary>
    public static ReadOnlySpan<short> SlowGainTable => [0x540, 0x4D8, 0x478, 0x410];
    
    /// <summary>
    /// dB per bit table (indexed by dbpbcod).
    /// </summary>
    public static ReadOnlySpan<short> DbPerBitTable => [0x000, 0x700, 0x900, 0xB00];
    
    /// <summary>
    /// Floor table (indexed by floorcod).
    /// </summary>
    /// <summary>
    /// Floor values (FFmpeg <c>ff_ac3_floor_tab</c>). Note the final entry is negative — the
    /// previous values here were an invented arithmetic sequence, and since the floor shifts
    /// every bit-allocation pointer, they corrupted the entire allocation.
    /// </summary>
    public static ReadOnlySpan<short> FloorTable =>
    [
        0x02F0, 0x02B0, 0x0270, 0x0230,
        0x01F0, 0x0170, 0x00F0, unchecked((short)0xF800)
    ];
    
    /// <summary>
    /// Fast gain table (indexed by fgaincod).
    /// </summary>
    public static ReadOnlySpan<short> FastGainTable =>
    [
        0x080, 0x100, 0x180, 0x200,
        0x280, 0x300, 0x380, 0x400
    ];
    
    
    
    
    /// <summary>
    /// Quantization levels for mantissa decoding (indexed by bap).
    /// </summary>
    public static ReadOnlySpan<byte> QuantizationLevels => 
    [
        0,   // bap 0: not transmitted (dither)
        3,   // bap 1: 3 levels
        5,   // bap 2: 5 levels
        7,   // bap 3: 7 levels
        11,  // bap 4: 11 levels
        15,  // bap 5: 15 levels (4 bits)
        0,   // bap 6-14: linear quantization
        0, 0, 0, 0, 0, 0, 0, 0,
        0    // bap 15: 16 bits
    ];
    
    /// <summary>
    /// Bits per mantissa indexed by bap (0-15).
    /// </summary>
    public static ReadOnlySpan<byte> BitsPerMantissa =>
    [
        0,  // 0: not transmitted
        0,  // 1: 3 levels (grouped)
        0,  // 2: 5 levels (grouped)
        3,  // 3: 7 levels
        0,  // 4: 11 levels (grouped)
        4,  // 5: 15 levels
        5,  // 6
        6,  // 7
        7,  // 8
        8,  // 9
        9,  // 10
        10, // 11
        11, // 12
        12, // 13
        14, // 14
        16  // 15
    ];
    
    
    /// <summary>
    /// Number of exponent groups per strategy.
    /// </summary>
    public static int GetExponentGroupCount(Ac3ExponentStrategy strategy, int coefficients)
    {
        return strategy switch
        {
            Ac3ExponentStrategy.D15 => (coefficients + 2) / 3,
            Ac3ExponentStrategy.D25 => (coefficients + 5) / 6 + 1,
            Ac3ExponentStrategy.D45 => (coefficients + 11) / 12 + 1,
            _ => 0
        };
    }
    
    /// <summary>
    /// Unpack grouped exponents into individual exponents.
    /// </summary>
    public static void UnpackExponents(
        Ac3ExponentStrategy strategy,
        int groupCount,
        ReadOnlySpan<byte> groupedExponents,
        int absoluteExponent,
        Span<int> exponents)
    {
        if (strategy == Ac3ExponentStrategy.Reuse)
            return;
        
        int exp = absoluteExponent;
        int idx = 0;
        exponents[idx++] = exp;
        
        int groupSize = strategy switch
        {
            Ac3ExponentStrategy.D15 => 1,
            Ac3ExponentStrategy.D25 => 2,
            Ac3ExponentStrategy.D45 => 4,
            _ => 1
        };
        
        for (int g = 0; g < groupCount; g++)
        {
            byte groupValue = groupedExponents[g];
            
            // Each group encodes 3 deltas (0-4 each, with 2 as zero)
            int d0 = (groupValue / 25) - 2;
            int d1 = ((groupValue % 25) / 5) - 2;
            int d2 = (groupValue % 5) - 2;
            
            exp += d0;
            for (int i = 0; i < groupSize && idx < exponents.Length; i++)
                exponents[idx++] = exp;
            
            exp += d1;
            for (int i = 0; i < groupSize && idx < exponents.Length; i++)
                exponents[idx++] = exp;
            
            exp += d2;
            for (int i = 0; i < groupSize && idx < exponents.Length; i++)
                exponents[idx++] = exp;
        }
    }
}

/// <summary>
/// Dequantization levels for symmetric and asymmetric mantissas.
/// </summary>
public static class Ac3Dequantizer
{
    /// <summary>
    /// Symmetric mantissa dequantisation, A/52 7.3.3 / FFmpeg's SYMMETRIC_DEQUANT:
    /// <c>((code - (levels &gt;&gt; 1)) * (1 &lt;&lt; 24)) / levels</c>, yielding 24-bit signed
    /// fixed point. Integer division truncates toward zero exactly as C does, and the reference
    /// decoder's output depends on that truncation.
    /// </summary>
    public static int SymmetricDequant(int code, int levels)
        => ((code - (levels >> 1)) * (1 << 24)) / levels;

    /// <summary>
    /// Linear mantissa dequantisation for bap 6-15: sign-extend the coded value and left-align
    /// it to 24 bits (FFmpeg: <c>get_sbits(gbc, n) &lt;&lt; (24 - n)</c>).
    /// </summary>
    public static int LinearDequant(int value, int bits)
    {
        if (bits <= 0) return 0;
        int half = 1 << (bits - 1);
        int signExtended = value >= half ? value - (1 << bits) : value;
        return signExtended << (24 - bits);
    }

    /// <summary>
    /// 3-level quantization values.
    /// </summary>
    private static readonly float[] Level3 = [-2/3f, 0f, 2/3f];
    
    /// <summary>
    /// 5-level quantization values.
    /// </summary>
    private static readonly float[] Level5 = [-4/5f, -2/5f, 0f, 2/5f, 4/5f];
    
    /// <summary>
    /// 7-level quantization values.
    /// </summary>
    private static readonly float[] Level7 = [-6/7f, -4/7f, -2/7f, 0f, 2/7f, 4/7f, 6/7f];
    
    /// <summary>
    /// 11-level quantization values.
    /// </summary>
    private static readonly float[] Level11 =
    [
        -10/11f, -8/11f, -6/11f, -4/11f, -2/11f,
        0f,
        2/11f, 4/11f, 6/11f, 8/11f, 10/11f
    ];
    
    /// <summary>
    /// 15-level quantization values.
    /// </summary>
    private static readonly float[] Level15 =
    [
        -14/15f, -12/15f, -10/15f, -8/15f, -6/15f, -4/15f, -2/15f,
        0f,
        2/15f, 4/15f, 6/15f, 8/15f, 10/15f, 12/15f, 14/15f
    ];
    
    /// <summary>
    /// Gets dequantized mantissa value for grouped quantization.
    /// </summary>
    public static float DequantizeGrouped(int level, int quantIndex)
    {
        return level switch
        {
            3 => Level3[Math.Clamp(quantIndex, 0, 2)],
            5 => Level5[Math.Clamp(quantIndex, 0, 4)],
            7 => Level7[Math.Clamp(quantIndex, 0, 6)],
            11 => Level11[Math.Clamp(quantIndex, 0, 10)],
            15 => Level15[Math.Clamp(quantIndex, 0, 14)],
            _ => 0f
        };
    }
    
    /// <summary>
    /// Gets dequantized mantissa value for linear quantization.
    /// </summary>
    public static float DequantizeLinear(int value, int bits)
    {
        if (bits == 0)
            return 0f;
        
        // AC-3 linear mantissas are two's complement (FFmpeg reads them with get_sbits),
        // not offset binary. Subtracting the midpoint inverted the sign of every mantissa
        // and turned a coded zero into full negative scale.
        int half = 1 << (bits - 1);
        int signedValue = value >= half ? value - (1 << bits) : value;

        return signedValue / (float)half;
    }
}

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3;

/// <summary>
/// Huffman tables for MPEG Layer III decoding.
/// ISO/IEC 11172-3 Tables B.7 through B.15
/// </summary>
public static class Mp3HuffmanTables
{
    /// <summary>
    /// Maximum absolute value for each Huffman table.
    /// </summary>
    public static ReadOnlySpan<int> TableLinbits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 2, 3, 4, 6, 8, 10, 13, 4, 5, 6, 7, 8, 9, 11, 13
    ];
    
    /// <summary>
    /// Gets the linbits value for a Huffman table.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetLinbits(int tableIndex)
    {
        if (tableIndex < 0 || tableIndex >= 32)
            return 0;
        return TableLinbits[tableIndex];
    }
    
    /// <summary>
    /// Scalefactor band indices for long blocks (MPEG-1).
    /// </summary>
    public static ReadOnlySpan<int> ScalefactorBandLong44100 =>
    [
        0, 4, 8, 12, 16, 20, 24, 30, 36, 44, 52, 62, 74, 90, 110, 134, 162, 196, 238, 288, 342, 418, 576
    ];
    
    public static ReadOnlySpan<int> ScalefactorBandLong48000 =>
    [
        0, 4, 8, 12, 16, 20, 24, 30, 36, 42, 50, 60, 72, 88, 106, 128, 156, 190, 230, 276, 330, 384, 576
    ];
    
    public static ReadOnlySpan<int> ScalefactorBandLong32000 =>
    [
        0, 4, 8, 12, 16, 20, 24, 30, 36, 44, 54, 66, 82, 102, 126, 156, 194, 240, 296, 364, 448, 550, 576
    ];
    
    /// <summary>
    /// Scalefactor band indices for short blocks (MPEG-1).
    /// </summary>
    public static ReadOnlySpan<int> ScalefactorBandShort44100 =>
    [
        0, 4, 8, 12, 16, 22, 30, 40, 52, 66, 84, 106, 136, 192
    ];
    
    public static ReadOnlySpan<int> ScalefactorBandShort48000 =>
    [
        0, 4, 8, 12, 16, 22, 28, 38, 50, 64, 80, 100, 126, 192
    ];
    
    public static ReadOnlySpan<int> ScalefactorBandShort32000 =>
    [
        0, 4, 8, 12, 16, 22, 30, 42, 58, 78, 104, 138, 180, 192
    ];
    
    /// <summary>
    /// Scalefactor band indices for long blocks, MPEG-2 LSF (22.05, 24, 16 kHz) and MPEG-2.5
    /// (11.025, 12, 8 kHz).
    /// </summary>
    /// <remarks>
    /// Rows 3 to 8 of FFmpeg's <c>ff_band_size_long[9][22]</c>, accumulated. They are not the
    /// MPEG-1 rows halved or reused: 22.05 kHz starts with six-wide bands where 44.1 kHz starts
    /// with four-wide ones, and 8 kHz ends with five two-wide bands. Every one of the nine rows
    /// in FFmpeg's table is distinct, and this decoder used to answer the 44.1 kHz row for
    /// 22.05, 11.025 and every unknown rate, which put the coefficients of every MPEG-2 and
    /// MPEG-2.5 frame in the wrong bands - right energy, wrong spectrum.
    /// </remarks>
    
    public static ReadOnlySpan<int> ScalefactorBandLong22050 =>
    [
        0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576
    ];
    
    public static ReadOnlySpan<int> ScalefactorBandLong24000 =>
    [
        0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 114, 136, 162, 194, 232, 278, 332, 394, 464, 540, 576
    ];
    
    public static ReadOnlySpan<int> ScalefactorBandLong16000 =>
    [
        0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576
    ];
    
    public static ReadOnlySpan<int> ScalefactorBandLong11025 =>
    [
        0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576
    ];
    
    public static ReadOnlySpan<int> ScalefactorBandLong12000 =>
    [
        0, 6, 12, 18, 24, 30, 36, 44, 54, 66, 80, 96, 116, 140, 168, 200, 238, 284, 336, 396, 464, 522, 576
    ];
    
    public static ReadOnlySpan<int> ScalefactorBandLong8000 =>
    [
        0, 12, 24, 36, 48, 60, 72, 88, 108, 132, 160, 192, 232, 280, 336, 400, 476, 566, 568, 570, 572, 574, 576
    ];
        /// <summary>
    /// Scalefactor band indices for short blocks, MPEG-2 LSF and MPEG-2.5.
    /// </summary>
    /// <remarks>Rows 3 to 8 of FFmpeg's <c>ff_band_size_short[9][13]</c>, accumulated.</remarks>
    
    public static ReadOnlySpan<int> ScalefactorBandShort22050 =>
    [
        0, 4, 8, 12, 18, 24, 32, 42, 56, 74, 100, 132, 174, 192
    ];
    
    public static ReadOnlySpan<int> ScalefactorBandShort24000 =>
    [
        0, 4, 8, 12, 18, 26, 36, 48, 62, 80, 104, 136, 180, 192
    ];
    
    public static ReadOnlySpan<int> ScalefactorBandShort16000 =>
    [
        0, 4, 8, 12, 18, 26, 36, 48, 62, 80, 104, 134, 174, 192
    ];
    
    public static ReadOnlySpan<int> ScalefactorBandShort11025 =>
    [
        0, 4, 8, 12, 18, 26, 36, 48, 62, 80, 104, 134, 174, 192
    ];
    
    public static ReadOnlySpan<int> ScalefactorBandShort12000 =>
    [
        0, 4, 8, 12, 18, 26, 36, 48, 62, 80, 104, 134, 174, 192
    ];
    
    public static ReadOnlySpan<int> ScalefactorBandShort8000 =>
    [
        0, 8, 16, 24, 36, 52, 72, 96, 124, 160, 162, 164, 166, 192
    ];
        /// <summary>
    /// Gets scalefactor band offsets for long blocks based on sample rate.
    /// </summary>
    /// <summary>
    /// MPEG-1 intensity stereo weights for channel 0, indexed by channel 1's scale factor.
    /// FFmpeg's is_table[0] (mpegaudiodec_float.c). Entries 7..15 are unused: a scale factor of 7
    /// or more signals "not intensity coded" and the band is left alone.
    /// </summary>
    public static ReadOnlySpan<float> IntensityTable0 =>
    [
        0.000000000000000000e+00f, 2.113248705863952637e-01f, 3.660253882408142090e-01f,
        5.000000000000000000e-01f, 6.339746117591857910e-01f, 7.886751294136047363e-01f,
        1.000000000000000000e+00f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f
    ];

    /// <summary>MPEG-1 intensity stereo weights for channel 1. FFmpeg's is_table[1].</summary>
    public static ReadOnlySpan<float> IntensityTable1 =>
    [
        1.000000000000000000e+00f, 7.886751294136047363e-01f, 6.339746117591857910e-01f,
        5.000000000000000000e-01f, 3.660253882408142090e-01f, 2.113248705863952637e-01f,
        0.000000000000000000e+00f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f
    ];

    /// <summary>
    /// MPEG-2/2.5 (LSF) intensity stereo weights. Built exactly as FFmpeg's decode_init_static()
    /// builds is_table_lsf: for scale factor i and table j, one channel keeps unity gain and the
    /// other takes 2^(-(j+1)*((i+1)/2)/4), with which channel is which alternating on i's low bit.
    /// </summary>
    private static readonly float[][][] IntensityTablesLsf = BuildIntensityTablesLsf();

    private static float[][][] BuildIntensityTablesLsf()
    {
        var tables = new float[2][][];
        for (int j = 0; j < 2; j++)
        {
            tables[j] = [new float[16], new float[16]];
            for (int i = 0; i < 16; i++)
            {
                int exponent = -(j + 1) * ((i + 1) >> 1);
                float value = (float)Math.Pow(2.0, exponent / 4.0);
                int k = i & 1;
                tables[j][k ^ 1][i] = value;
                tables[j][k][i] = 1.0f;
            }
        }
        return tables;
    }

    /// <summary>
    /// LSF intensity stereo weights for the given table (channel 1's scalefac_compress low bit)
    /// and channel.
    /// </summary>
    public static ReadOnlySpan<float> GetIntensityTableLsf(int table, int channel) =>
        IntensityTablesLsf[table][channel];

    public static ReadOnlySpan<int> GetScalefactorBandLong(int sampleRate)
    {
        return sampleRate switch
        {
            44100 => ScalefactorBandLong44100,
            48000 => ScalefactorBandLong48000,
            32000 => ScalefactorBandLong32000,
            22050 => ScalefactorBandLong22050,
            24000 => ScalefactorBandLong24000,
            16000 => ScalefactorBandLong16000,
            11025 => ScalefactorBandLong11025,
            12000 => ScalefactorBandLong12000,
            8000 => ScalefactorBandLong8000,
            _ => throw new NotSupportedException(
                $"MP3: no scalefactor band table for {sampleRate} Hz. Layer III has nine "
              + "rates and no others; a tenth means the header was parsed wrongly."),
        };
    }
    
    /// <summary>
    /// Gets scalefactor band offsets for short blocks based on sample rate.
    /// </summary>
    public static ReadOnlySpan<int> GetScalefactorBandShort(int sampleRate)
    {
        return sampleRate switch
        {
            44100 => ScalefactorBandShort44100,
            48000 => ScalefactorBandShort48000,
            32000 => ScalefactorBandShort32000,
            22050 => ScalefactorBandShort22050,
            24000 => ScalefactorBandShort24000,
            16000 => ScalefactorBandShort16000,
            11025 => ScalefactorBandShort11025,
            12000 => ScalefactorBandShort12000,
            8000 => ScalefactorBandShort8000,
            _ => throw new NotSupportedException(
                $"MP3: no scalefactor band table for {sampleRate} Hz. Layer III has nine "
              + "rates and no others; a tenth means the header was parsed wrongly."),
        };
    }
    
    /// <summary>
    /// Scalefactor length table - slen1 values for scalefac_compress indices.
    /// </summary>
    public static ReadOnlySpan<int> ScalefactorLength1 =>
    [
        0, 0, 0, 0, 3, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4
    ];
    
    /// <summary>
    /// Scalefactor length table - slen2 values for scalefac_compress indices.
    /// </summary>
    public static ReadOnlySpan<int> ScalefactorLength2 =>
    [
        0, 1, 2, 3, 0, 1, 2, 3, 1, 2, 3, 1, 2, 3, 2, 3
    ];
    
    /// <summary>
    /// <c>ff_lsf_nsf_table[6][3][4]</c> - how many scalefactors each of the four LSF
    /// partitions holds, by <c>scalefac_compress</c> row and block shape.
    /// </summary>
    /// <remarks>
    /// Flattened; index as <c>[(tindex2 * 3 + tindex) * 4 + k]</c>. The three columns are
    /// long, short, and short with a switch point. The long column always sums to 21 and
    /// the short one to 36, but the partitions inside them are not the MPEG-1 groups of
    /// 6/5/5/5, which is what the reconstruction this replaced assumed.
    /// </remarks>
    public static ReadOnlySpan<byte> LsfNsfTable =>
    [
        6, 5, 5, 5,  9, 9, 9, 9,  6, 9, 9, 9,
        6, 5, 7, 3,  9, 9, 12, 6,  6, 9, 12, 6,
        11, 10, 0, 0,  18, 18, 0, 0,  15, 18, 0, 0,
        7, 7, 7, 0,  12, 12, 12, 0,  6, 15, 12, 0,
        6, 6, 6, 3,  12, 9, 9, 6,  6, 12, 9, 6,
        8, 8, 5, 0,  15, 12, 9, 0,  6, 18, 9, 0,
    ];
    
    /// <summary>
    /// Pretab values for scalefactor preemphasis.
    /// </summary>
    public static ReadOnlySpan<int> Pretab =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 3, 2, 0
    ];
    
    /// <summary>
    /// Power table for dequantization: 2^(x/4) for x = 0..127
    /// </summary>
    public static ReadOnlySpan<float> PowerTable => powerTableData;
    
    private static readonly float[] powerTableData = InitializePowerTable();
    
    private static float[] InitializePowerTable()
    {
        var table = new float[128];
        for (int i = 0; i < 128; i++)
        {
            table[i] = MathF.Pow(2.0f, i / 4.0f);
        }
        return table;
    }
    
    /// <summary>
    /// Inverse power table for dequantization: 2^(-x/4) for x = 0..127
    /// </summary>
    public static ReadOnlySpan<float> InversePowerTable => inversePowerTableData;
    
    private static readonly float[] inversePowerTableData = InitializeInversePowerTable();
    
    private static float[] InitializeInversePowerTable()
    {
        var table = new float[128];
        for (int i = 0; i < 128; i++)
        {
            table[i] = MathF.Pow(2.0f, -i / 4.0f);
        }
        return table;
    }
    
    /// <summary>
    /// Requantization table: x^(4/3) for x = 0..8191 (single precision, for fallback)
    /// </summary>
    public static ReadOnlySpan<float> RequantTable => requantTableData;
    
    private static readonly float[] requantTableData = InitializeRequantTable();
    
    private static float[] InitializeRequantTable()
    {
        var table = new float[8192];
        for (int i = 0; i < 8192; i++)
        {
            table[i] = (float)Math.Pow(i, 4.0 / 3.0);
        }
        return table;
    }
    
    /// <summary>
    /// Double-precision requantization table: x^(4/3) for x = 0..8191.
    /// Used to match FFmpeg's precision — FFmpeg pre-computes pow43 * gain in double,
    /// then casts to float. Using double for the multiply avoids accumulated float rounding.
    /// </summary>
    public static ReadOnlySpan<double> RequantTableDouble => requantTableDoubleData;
    
    private static readonly double[] requantTableDoubleData = InitializeRequantTableDouble();
    
    private static double[] InitializeRequantTableDouble()
    {
        var table = new double[8192];
        for (int i = 0; i < 8192; i++)
        {
            table[i] = Math.Pow(i, 4.0 / 3.0);
        }
        return table;
    }
    
    /// <summary>
    /// IMDCT window for long blocks (Type 0).
    /// </summary>
    public static ReadOnlySpan<float> ImdctWindowLong => imdctWindowLongData;
    
    private static readonly float[] imdctWindowLongData = InitializeImdctWindowLong();
    
    private static float[] InitializeImdctWindowLong()
    {
        var window = new float[36];
        for (int i = 0; i < 36; i++)
        {
            window[i] = (float)Math.Sin(Math.PI / 36.0 * (i + 0.5));
        }
        return window;
    }
    
    /// <summary>
    /// IMDCT window for start blocks (Type 1).
    /// </summary>
    public static ReadOnlySpan<float> ImdctWindowStart => imdctWindowStartData;
    
    private static readonly float[] imdctWindowStartData = InitializeImdctWindowStart();
    
    private static float[] InitializeImdctWindowStart()
    {
        var window = new float[36];
        for (int i = 0; i < 18; i++)
        {
            window[i] = (float)Math.Sin(Math.PI / 36.0 * (i + 0.5));
        }
        for (int i = 18; i < 24; i++)
        {
            window[i] = 1.0f;
        }
        for (int i = 24; i < 30; i++)
        {
            window[i] = (float)Math.Sin(Math.PI / 12.0 * (i - 18 + 0.5));
        }
        for (int i = 30; i < 36; i++)
        {
            window[i] = 0.0f;
        }
        return window;
    }
    
    /// <summary>
    /// IMDCT window for short blocks (Type 2).
    /// </summary>
    public static ReadOnlySpan<float> ImdctWindowShort => imdctWindowShortData;
    
    private static readonly float[] imdctWindowShortData = InitializeImdctWindowShort();
    
    private static float[] InitializeImdctWindowShort()
    {
        var window = new float[12];
        for (int i = 0; i < 12; i++)
        {
            window[i] = (float)Math.Sin(Math.PI / 12.0 * (i + 0.5));
        }
        return window;
    }
    
    /// <summary>
    /// IMDCT window for end blocks (Type 3).
    /// </summary>
    public static ReadOnlySpan<float> ImdctWindowEnd => imdctWindowEndData;
    
    private static readonly float[] imdctWindowEndData = InitializeImdctWindowEnd();
    
    private static float[] InitializeImdctWindowEnd()
    {
        var window = new float[36];
        for (int i = 0; i < 6; i++)
        {
            window[i] = 0.0f;
        }
        for (int i = 6; i < 12; i++)
        {
            window[i] = (float)Math.Sin(Math.PI / 12.0 * (i - 6 + 0.5));
        }
        for (int i = 12; i < 18; i++)
        {
            window[i] = 1.0f;
        }
        for (int i = 18; i < 36; i++)
        {
            window[i] = (float)Math.Sin(Math.PI / 36.0 * (i + 0.5));
        }
        return window;
    }
    
    /// <summary>
    /// Cosine table for 36-point IMDCT.
    /// </summary>
    public static ReadOnlySpan<float> ImdctCos36 => imdctCos36Data;
    
    private static readonly float[] imdctCos36Data = InitializeImdctCos36();
    
    private static float[] InitializeImdctCos36()
    {
        // cos(PI/72 * (2*n + 1 + 18) * (2*k + 1)) for n = 0..35, k = 0..17
        // Computed in double precision for maximum accuracy, stored as float.
        var table = new float[36 * 18];
        for (int n = 0; n < 36; n++)
        {
            for (int k = 0; k < 18; k++)
            {
                table[n * 18 + k] = (float)Math.Cos(Math.PI / 72.0 * (2 * n + 1 + 18) * (2 * k + 1));
            }
        }
        return table;
    }
    
    /// <summary>
    /// Cosine table for 12-point IMDCT (short blocks).
    /// </summary>
    public static ReadOnlySpan<float> ImdctCos12 => imdctCos12Data;
    
    private static readonly float[] imdctCos12Data = InitializeImdctCos12();
    
    private static float[] InitializeImdctCos12()
    {
        // cos(PI/24 * (2*n + 1 + 6) * (2*k + 1)) for n = 0..11, k = 0..5
        // Computed in double precision for maximum accuracy, stored as float.
        var table = new float[12 * 6];
        for (int n = 0; n < 12; n++)
        {
            for (int k = 0; k < 6; k++)
            {
                table[n * 6 + k] = (float)Math.Cos(Math.PI / 24.0 * (2 * n + 1 + 6) * (2 * k + 1));
            }
        }
        return table;
    }
    
    /// <summary>
    /// Polyphase synthesis filterbank coefficients.
    /// 512 coefficients organized as 32 subbands × 16 taps.
    /// ISO/IEC 11172-3 Table B.2
    /// </summary>
    public static ReadOnlySpan<float> SynthesisWindow => synthesisWindowData;
    
    /// <summary>Returns the synthesis window as a float[] for indexed access in ApplyWindow.</summary>
    internal static float[] SynthesisWindowArray => synthesisWindowData;
    
    private static readonly float[] synthesisWindowData = InitializeSynthesisWindow();
    
    private static float[] InitializeSynthesisWindow()
    {
        // ISO/IEC 11172-3 Table B.3 half-window values (257 entries).
        // These are the exact values from ffmpeg's ff_mpa_enwindow[].
        ReadOnlySpan<int> enwindow =
        [
             0,    -1,    -1,    -1,    -1,    -1,    -1,    -2,
            -2,    -2,    -2,    -3,    -3,    -4,    -4,    -5,
            -5,    -6,    -7,    -7,    -8,    -9,   -10,   -11,
           -13,   -14,   -16,   -17,   -19,   -21,   -24,   -26,
           -29,   -31,   -35,   -38,   -41,   -45,   -49,   -53,
           -58,   -63,   -68,   -73,   -79,   -85,   -91,   -97,
          -104,  -111,  -117,  -125,  -132,  -139,  -147,  -154,
          -161,  -169,  -176,  -183,  -190,  -196,  -202,  -208,
           213,   218,   222,   225,   227,   228,   228,   227,
           224,   221,   215,   208,   200,   189,   177,   163,
           146,   127,   106,    83,    57,    29,    -2,   -36,
           -72,  -111,  -153,  -197,  -244,  -294,  -347,  -401,
          -459,  -519,  -581,  -645,  -711,  -779,  -848,  -919,
          -991, -1064, -1137, -1210, -1283, -1356, -1428, -1498,
         -1567, -1634, -1698, -1759, -1817, -1870, -1919, -1962,
         -2001, -2032, -2057, -2075, -2085, -2087, -2080, -2063,
          2037,  2000,  1952,  1893,  1822,  1739,  1644,  1535,
          1414,  1280,  1131,   970,   794,   605,   402,   185,
           -45,  -288,  -545,  -814, -1095, -1388, -1692, -2006,
         -2330, -2663, -3004, -3351, -3705, -4063, -4425, -4788,
         -5153, -5517, -5879, -6237, -6589, -6935, -7271, -7597,
         -7910, -8209, -8491, -8755, -8998, -9219, -9416, -9585,
         -9727, -9838, -9916, -9959, -9966, -9935, -9863, -9750,
         -9592, -9389, -9139, -8840, -8492, -8092, -7640, -7134,
          6574,  5959,  5288,  4561,  3776,  2935,  2037,  1082,
            70,  -998, -2122, -3300, -4533, -5818, -7154, -8540,
         -9975,-11455,-12980,-14548,-16155,-17799,-19478,-21189,
        -22929,-24694,-26482,-28289,-30112,-31947,-33791,-35640,
        -37489,-39336,-41176,-43006,-44821,-46617,-48390,-50137,
        -51853,-53534,-55178,-56778,-58333,-59838,-61289,-62684,
        -64019,-65290,-66494,-67629,-68692,-69679,-70590,-71420,
        -72169,-72835,-73415,-73908,-74313,-74630,-74856,-74992,
         75038
        ];

        // Build 512-entry synthesis window from 257 half-values.
        // Matches FFmpeg's mpa_synth_init() in mpegaudiodsp_template.c.
        // FFmpeg uses 1/(1<<39) because its spectral values include the FRAC_BITS (2^23) convention.
        // Our requant produces natural-scale floats, so we only need 1/(2^WFRAC_BITS).
        const double scale = 1.0 / (1L << 16); // WFRAC_BITS = 16
        var window = new float[512];

        for (int i = 0; i < 257; i++)
        {
            float v = (float)(enwindow[i] * scale);
            window[i] = v;
            if ((i & 63) != 0) v = -v;
            if (i != 0) window[512 - i] = v;
        }

        return window;
    }
    
    /// <summary>
    /// Cosine matrix for polyphase synthesis (32 × 64).
    /// </summary>
    public static ReadOnlySpan<float> CosMatrix => cosMatrixData;
    
    private static readonly float[] cosMatrixData = InitializeCosMatrix();
    
    private static float[] InitializeCosMatrix()
    {
        var matrix = new float[32 * 64];
        for (int i = 0; i < 32; i++)
        {
            for (int j = 0; j < 64; j++)
            {
                matrix[i * 64 + j] = MathF.Cos(MathF.PI / 64.0f * (2 * i + 1) * (j + 16));
            }
        }
        return matrix;
    }
    
    /// <summary>
    /// CS (cosine) values for stereo processing.
    /// </summary>
    public static ReadOnlySpan<float> CsValues =>
    [
        0.857493f, 0.881742f, 0.949629f, 0.983315f,
        0.995518f, 0.999161f, 0.999899f, 0.999993f
    ];
    
    /// <summary>
    /// CA (cosine) values for stereo processing.
    /// </summary>
    public static ReadOnlySpan<float> CaValues =>
    [
        -0.514496f, -0.471732f, -0.313377f, -0.181913f,
        -0.094574f, -0.040966f, -0.014199f, -0.003700f
    ];
}

using System;

namespace MediaKernel.Core.Codecs.AAC;

/// <summary>
/// Huffman codebook tables for AAC spectral data decoding.
/// Based on ISO/IEC 14496-3 Table 4.A.
/// </summary>
public static class AacHuffmanTables
{
    // ========================================================================
    // Long block (1024-sample) SWB offset tables from FFmpeg aactab.c
    // Indexed by sample rate: ff_swb_offset_1024[sampleRateIndex]
    // ========================================================================

    /// <summary>SWB offsets for 1024-sample long blocks at 96000/88200 Hz (index 0-1).</summary>
    public static ReadOnlySpan<int> SwbOffset1024_96 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64,
        72, 80, 88, 96, 108, 120, 132, 144, 156, 172, 188, 212, 240, 276, 320, 384,
        448, 512, 576, 640, 704, 768, 832, 896, 960, 1024
    ];

    /// <summary>SWB offsets for 1024-sample long blocks at 64000 Hz (index 2).</summary>
    public static ReadOnlySpan<int> SwbOffset1024_64 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64,
        72, 80, 88, 100, 112, 124, 140, 156, 172, 192, 216, 240, 268, 304, 344, 384,
        424, 464, 504, 544, 584, 624, 664, 704, 744, 784, 824, 864, 904, 944, 984, 1024
    ];

    /// <summary>SWB offsets for 1024-sample long blocks at 48000/44100 Hz (index 3-4).</summary>
    public static ReadOnlySpan<int> SwbOffset1024_48 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72, 80,
        88, 96, 108, 120, 132, 144, 160, 176, 196, 216, 240, 264, 292, 320, 352, 384,
        416, 448, 480, 512, 544, 576, 608, 640, 672, 704, 736, 768, 800, 832, 864, 896,
        928, 1024
    ];

    /// <summary>SWB offsets for 1024-sample long blocks at 32000 Hz (index 5).</summary>
    public static ReadOnlySpan<int> SwbOffset1024_32 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72, 80,
        88, 96, 108, 120, 132, 144, 160, 176, 196, 216, 240, 264, 292, 320, 352, 384,
        416, 448, 480, 512, 544, 576, 608, 640, 672, 704, 736, 768, 800, 832, 864, 896,
        928, 960, 992, 1024
    ];

    /// <summary>SWB offsets for 1024-sample long blocks at 24000/22050 Hz (index 6-7).</summary>
    public static ReadOnlySpan<int> SwbOffset1024_24 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 52, 60, 68, 76,
        84, 92, 100, 108, 116, 124, 136, 148, 160, 172, 188, 204, 220, 240, 260, 284,
        308, 336, 364, 396, 432, 468, 508, 552, 600, 652, 704, 768, 832, 896, 960, 1024
    ];

    /// <summary>SWB offsets for 1024-sample long blocks at 16000/12000/11025 Hz (index 8-10).</summary>
    public static ReadOnlySpan<int> SwbOffset1024_16 =>
    [
        0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 100, 112, 124, 136,
        148, 160, 172, 184, 196, 212, 228, 244, 260, 280, 300, 320, 344, 368, 396, 424,
        456, 492, 532, 572, 616, 664, 716, 772, 832, 896, 960, 1024
    ];

    /// <summary>SWB offsets for 1024-sample long blocks at 8000/7350 Hz (index 11-12).</summary>
    public static ReadOnlySpan<int> SwbOffset1024_8 =>
    [
        0, 12, 24, 36, 48, 60, 72, 84, 96, 108, 120, 132, 144, 156, 172, 188,
        204, 220, 236, 252, 268, 288, 308, 328, 348, 372, 396, 420, 448, 476, 508, 544,
        580, 620, 664, 712, 764, 820, 880, 944, 1024
    ];

    // ========================================================================
    // Short block (128-sample) SWB offset tables from FFmpeg aactab.c
    // Indexed by sample rate: ff_swb_offset_128[sampleRateIndex]
    // ========================================================================

    /// <summary>SWB offsets for 128-sample short blocks at 96000/88200/64000 Hz (index 0-2).</summary>
    public static ReadOnlySpan<int> SwbOffset128_96 =>
    [
        0, 4, 8, 12, 16, 20, 24, 32, 40, 48, 64, 92, 128
    ];

    /// <summary>SWB offsets for 128-sample short blocks at 48000/44100/32000 Hz (index 3-5).</summary>
    public static ReadOnlySpan<int> SwbOffset128_48 =>
    [
        0, 4, 8, 12, 16, 20, 28, 36, 44, 56, 68, 80, 96, 112, 128
    ];

    /// <summary>SWB offsets for 128-sample short blocks at 24000/22050 Hz (index 6-7).</summary>
    public static ReadOnlySpan<int> SwbOffset128_24 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 36, 44, 52, 64, 76, 92, 108, 128
    ];

    /// <summary>SWB offsets for 128-sample short blocks at 16000/12000/11025 Hz (index 8-10).</summary>
    public static ReadOnlySpan<int> SwbOffset128_16 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 60, 72, 88, 108, 128
    ];

    /// <summary>SWB offsets for 128-sample short blocks at 8000/7350 Hz (index 11-12).</summary>
    public static ReadOnlySpan<int> SwbOffset128_8 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 36, 44, 52, 60, 72, 88, 108, 128
    ];

    /// <summary>
    /// Number of scalefactor bands for long blocks by sample rate index.
    /// </summary>
    public static ReadOnlySpan<int> NumSwbLong =>
    [
        41, 41, 47, 49, 49, 51, 47, 47, 43, 43, 43, 40, 40
    ];

    /// <summary>
    /// Number of scalefactor bands for short blocks by sample rate index.
    /// </summary>
    public static ReadOnlySpan<int> NumSwbShort =>
    [
        12, 12, 12, 14, 14, 14, 15, 15, 15, 15, 15, 15, 15
    ];


    // ========================================================================
    // 960-sample long block and 120-sample short block SWB offsets, from
    // ff_swb_offset_960[] and ff_swb_offset_120[] in aactab.c. These are what a stream whose
    // AudioSpecificConfig sets frameLengthFlag uses in place of the 1024/128 tables; the
    // spectral coefficients themselves still sit 128 apart per window, so only the band
    // boundaries move.
    // ========================================================================

    /// <summary>SWB offsets for 960-sample long blocks at 96000/88200 Hz (index 0-1).</summary>
    public static ReadOnlySpan<int> SwbOffset960_96 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64,
        72, 80, 88, 96, 108, 120, 132, 144, 156, 172, 188, 212, 240, 276, 320, 384,
        448, 512, 576, 640, 704, 768, 832, 896, 960
    ];

    /// <summary>SWB offsets for 960-sample long blocks at 64000 Hz (index 2).</summary>
    public static ReadOnlySpan<int> SwbOffset960_64 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64,
        72, 80, 88, 100, 112, 124, 140, 156, 172, 192, 216, 240, 268, 304, 344, 384,
        424, 464, 504, 544, 584, 624, 664, 704, 744, 784, 824, 864, 904, 944, 960
    ];

    /// <summary>SWB offsets for 960-sample long blocks at 48000/44100/32000 Hz (index 3-5).</summary>
    /// <remarks>FFmpeg defines swb_offset_960_32 as swb_offset_960_48, so 32000 Hz shares it.</remarks>
    public static ReadOnlySpan<int> SwbOffset960_48 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72, 80,
        88, 96, 108, 120, 132, 144, 160, 176, 196, 216, 240, 264, 292, 320, 352, 384,
        416, 448, 480, 512, 544, 576, 608, 640, 672, 704, 736, 768, 800, 832, 864, 896,
        928, 960
    ];

    /// <summary>SWB offsets for 960-sample long blocks at 24000/22050 Hz (index 6-7).</summary>
    public static ReadOnlySpan<int> SwbOffset960_24 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 52, 60, 68, 76,
        84, 92, 100, 108, 116, 124, 136, 148, 160, 172, 188, 204, 220, 240, 260, 284,
        308, 336, 364, 396, 432, 468, 508, 552, 600, 652, 704, 768, 832, 896, 960
    ];

    /// <summary>SWB offsets for 960-sample long blocks at 16000/12000/11025 Hz (index 8-10).</summary>
    public static ReadOnlySpan<int> SwbOffset960_16 =>
    [
        0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 100, 112, 124, 136,
        148, 160, 172, 184, 196, 212, 228, 244, 260, 280, 300, 320, 344, 368, 396, 424,
        456, 492, 532, 572, 616, 664, 716, 772, 832, 896, 960
    ];

    /// <summary>SWB offsets for 960-sample long blocks at 8000/7350 Hz (index 11-12).</summary>
    public static ReadOnlySpan<int> SwbOffset960_8 =>
    [
        0, 12, 24, 36, 48, 60, 72, 84, 96, 108, 120, 132, 144, 156, 172, 188,
        204, 220, 236, 252, 268, 288, 308, 328, 348, 372, 396, 420, 448, 476, 508, 544,
        580, 620, 664, 712, 764, 820, 880, 944, 960
    ];

    /// <summary>SWB offsets for 120-sample short blocks at 96000/88200/64000 Hz (index 0-2).</summary>
    public static ReadOnlySpan<int> SwbOffset120_96 =>
    [
        0, 4, 8, 12, 16, 20, 24, 32, 40, 48, 64, 92, 120
    ];

    /// <summary>SWB offsets for 120-sample short blocks at 48000/44100/32000 Hz (index 3-5).</summary>
    public static ReadOnlySpan<int> SwbOffset120_48 =>
    [
        0, 4, 8, 12, 16, 20, 28, 36, 44, 56, 68, 80, 96, 112, 120
    ];

    /// <summary>SWB offsets for 120-sample short blocks at 24000/22050 Hz (index 6-7).</summary>
    public static ReadOnlySpan<int> SwbOffset120_24 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 36, 44, 52, 64, 76, 92, 108, 120
    ];

    /// <summary>SWB offsets for 120-sample short blocks at 16000/12000/11025 Hz (index 8-10).</summary>
    public static ReadOnlySpan<int> SwbOffset120_16 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 60, 72, 88, 108, 120
    ];

    /// <summary>SWB offsets for 120-sample short blocks at 8000/7350 Hz (index 11-12).</summary>
    public static ReadOnlySpan<int> SwbOffset120_8 =>
    [
        0, 4, 8, 12, 16, 20, 24, 28, 36, 44, 52, 60, 72, 88, 108, 120
    ];

    /// <summary>
    /// Number of scalefactor bands for 960-sample long blocks, <c>ff_aac_num_swb_960</c>.
    /// </summary>
    public static ReadOnlySpan<int> NumSwb960 =>
    [
        40, 40, 46, 49, 49, 49, 46, 46, 42, 42, 42, 40, 40
    ];

    /// <summary>
    /// Gets the scalefactor band offsets for a given sample rate and block type.
    /// </summary>
    /// <param name="frameLengthShort">
    /// The AudioSpecificConfig's <c>frameLengthFlag</c>: 960-sample frames rather than 1024.
    /// </param>

    // ---- The low delay families: 512 and 480 samples a frame ----------------------------
    //
    // ER AAC LD and ELD run at half the frame length of LC: 512 samples, or 480 when
    // frame_length_short is set. They have no short blocks at all - LD is defined only for
    // ONLY_LONG_SEQUENCE and ELD has no window sequence field - so there is no 64/60 family to
    // go with these, which is why the pair below has no short counterpart.
    //
    // FFmpeg's ff_swb_offset_512[] and _480[] are NULL for every sampling index outside 3..7,
    // and the config parser rejects an LD stream outside that range outright. The getters
    // therefore THROW rather than falling back to a nearby rate: a silent substitution here
    // would decode at the wrong band boundaries and sound almost right, which is the failure
    // this project has been bitten by before.

    public static ReadOnlySpan<int> SwbOffset512_48 =>
    [
          0,   4,   8,  12,  16,  20,  24,  28,
         32,  36,  40,  44,  48,  52,  56,  60,
         68,  76,  84,  92, 100, 112, 124, 136,
        148, 164, 184, 208, 236, 268, 300, 332,
        364, 396, 428, 460, 512
    ];

    public static ReadOnlySpan<int> SwbOffset512_32 =>
    [
          0,   4,   8,  12,  16,  20,  24,  28,
         32,  36,  40,  44,  48,  52,  56,  64,
         72,  80,  88,  96, 108, 120, 132, 144,
        160, 176, 192, 212, 236, 260, 288, 320,
        352, 384, 416, 448, 480, 512
    ];

    public static ReadOnlySpan<int> SwbOffset512_24 =>
    [
          0,   4,   8,  12,  16,  20,  24,  28,
         32,  36,  40,  44,  52,  60,  68,  80,
         92, 104, 120, 140, 164, 192, 224, 256,
        288, 320, 352, 384, 416, 448, 480, 512
    ];

    public static ReadOnlySpan<int> SwbOffset480_48 =>
    [
          0,   4,   8,  12,  16,  20,  24,  28,
         32,  36,  40,  44,  48,  52,  56,  64,
         72,  80,  88,  96, 108, 120, 132, 144,
        156, 172, 188, 212, 240, 272, 304, 336,
        368, 400, 432, 480
    ];

    public static ReadOnlySpan<int> SwbOffset480_32 =>
    [
          0,   4,   8,  12,  16,  20,  24,  28,
         32,  36,  40,  44,  48,  52,  56,  60,
         64,  72,  80,  88,  96, 104, 112, 124,
        136, 148, 164, 180, 200, 224, 256, 288,
        320, 352, 384, 416, 448, 480
    ];

    public static ReadOnlySpan<int> SwbOffset480_24 =>
    [
          0,   4,   8,  12,  16,  20,  24,  28,
         32,  36,  40,  44,  52,  60,  68,  80,
         92, 104, 120, 140, 164, 192, 224, 256,
        288, 320, 352, 384, 416, 448, 480
    ];

    /// <summary><c>ff_aac_num_swb_512</c>.</summary>
    public static ReadOnlySpan<int> NumSwb512 =>
        [0, 0, 0, 36, 36, 37, 31, 31, 0, 0, 0, 0, 0];

    /// <summary><c>ff_aac_num_swb_480</c>.</summary>
    public static ReadOnlySpan<int> NumSwb480 =>
        [0, 0, 0, 35, 35, 37, 30, 30, 0, 0, 0, 0, 0];

    /// <summary><c>ff_tns_max_bands_512</c>.</summary>
    public static ReadOnlySpan<int> TnsMaxBands512 =>
        [0, 0, 0, 31, 32, 37, 31, 31, 0, 0, 0, 0, 0];

    /// <summary><c>ff_tns_max_bands_480</c>.</summary>
    public static ReadOnlySpan<int> TnsMaxBands480 =>
        [0, 0, 0, 31, 32, 37, 30, 30, 0, 0, 0, 0, 0];

    /// <summary>
    /// <c>ff_swb_offset_512</c> / <c>ff_swb_offset_480</c> - the band boundaries of an ER AAC
    /// LD or ELD long block.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The sampling index is outside 3..7, where FFmpeg's tables are NULL. The C treats that as
    /// AVERROR_BUG because its config parser has already rejected the stream, so reaching it
    /// means the two checks disagree.
    /// </exception>
    public static int[] GetSwbOffsetsLowDelay(int sampleRateIndex, bool frameLengthShort)
        => (sampleRateIndex, frameLengthShort) switch
        {
            (3 or 4, false) => SwbOffset512_48.ToArray(),
            (5, false) => SwbOffset512_32.ToArray(),
            (6 or 7, false) => SwbOffset512_24.ToArray(),
            (3 or 4, true) => SwbOffset480_48.ToArray(),
            (5, true) => SwbOffset480_32.ToArray(),
            (6 or 7, true) => SwbOffset480_24.ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(sampleRateIndex),
                     $"ER AAC LD and ELD are defined only for sampling indices 3 to 7; "
                   + $"index {sampleRateIndex} has no {(frameLengthShort ? 480 : 512)}-sample "
                   + "band table."),
        };

    /// <summary><c>ff_aac_num_swb_512</c> / <c>_480</c>, with the same restriction.</summary>
    public static int GetNumSwbLowDelay(int sampleRateIndex, bool frameLengthShort)
    {
        ReadOnlySpan<int> table = frameLengthShort ? NumSwb480 : NumSwb512;
        int n = (uint)sampleRateIndex < (uint)table.Length ? table[sampleRateIndex] : 0;
        if (n == 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateIndex),
                $"ER AAC LD and ELD are defined only for sampling indices 3 to 7; index "
              + $"{sampleRateIndex} has no band count.");
        return n;
    }

    /// <summary><c>ff_tns_max_bands_512</c> / <c>_480</c>, with the same restriction.</summary>
    public static int GetTnsMaxBandsLowDelay(int sampleRateIndex, bool frameLengthShort)
    {
        ReadOnlySpan<int> table = frameLengthShort ? TnsMaxBands480 : TnsMaxBands512;
        int n = (uint)sampleRateIndex < (uint)table.Length ? table[sampleRateIndex] : 0;
        if (n == 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateIndex),
                $"ER AAC LD and ELD are defined only for sampling indices 3 to 7; index "
              + $"{sampleRateIndex} has no TNS band limit.");
        return n;
    }

    public static int[] GetSwbOffsets(int sampleRateIndex, bool isShortBlock,
                                      bool frameLengthShort = false)
    {
        // Matches FFmpeg's ff_swb_offset_1024[] / ff_swb_offset_128[] indexing, and
        // ff_swb_offset_960[] / ff_swb_offset_120[] when the frame is 960 samples long.
        if (isShortBlock && frameLengthShort)
        {
            return sampleRateIndex switch
            {
                0 or 1 or 2 => SwbOffset120_96.ToArray(),
                3 or 4 or 5 => SwbOffset120_48.ToArray(),
                6 or 7 => SwbOffset120_24.ToArray(),
                8 or 9 or 10 => SwbOffset120_16.ToArray(),
                11 or 12 => SwbOffset120_8.ToArray(),
                _ => SwbOffset120_48.ToArray()
            };
        }

        if (frameLengthShort)
        {
            return sampleRateIndex switch
            {
                0 or 1 => SwbOffset960_96.ToArray(),
                2 => SwbOffset960_64.ToArray(),
                3 or 4 or 5 => SwbOffset960_48.ToArray(),
                6 or 7 => SwbOffset960_24.ToArray(),
                8 or 9 or 10 => SwbOffset960_16.ToArray(),
                11 or 12 => SwbOffset960_8.ToArray(),
                _ => SwbOffset960_48.ToArray()
            };
        }

        if (isShortBlock)
        {
            return sampleRateIndex switch
            {
                0 or 1 or 2 => SwbOffset128_96.ToArray(),
                3 or 4 or 5 => SwbOffset128_48.ToArray(),
                6 or 7 => SwbOffset128_24.ToArray(),
                8 or 9 or 10 => SwbOffset128_16.ToArray(),
                11 or 12 => SwbOffset128_8.ToArray(),
                _ => SwbOffset128_48.ToArray()
            };
        }
        else
        {
            return sampleRateIndex switch
            {
                0 or 1 => SwbOffset1024_96.ToArray(),
                2 => SwbOffset1024_64.ToArray(),
                3 or 4 => SwbOffset1024_48.ToArray(),
                5 => SwbOffset1024_32.ToArray(),
                6 or 7 => SwbOffset1024_24.ToArray(),
                8 or 9 or 10 => SwbOffset1024_16.ToArray(),
                11 or 12 => SwbOffset1024_8.ToArray(),
                _ => SwbOffset1024_48.ToArray()
            };
        }
    }

    /// <summary>
    /// Gets the number of scalefactor bands for a sample rate and block type.
    /// </summary>
    public static int GetNumSwb(int sampleRateIndex, bool isShortBlock,
                                bool frameLengthShort = false)
    {
        if (sampleRateIndex < 0 || sampleRateIndex > 12)
            return isShortBlock ? 14 : 49;

        // ff_aac_num_swb_120 is a #define for ff_aac_num_swb_128: the 120-sample short block has
        // the same band count as the 128-sample one, only different boundaries.
        if (isShortBlock) return NumSwbShort[sampleRateIndex];
        return frameLengthShort ? NumSwb960[sampleRateIndex] : NumSwbLong[sampleRateIndex];
    }

    /// <summary>
    /// Maximum number of scalefactor bands on which TNS can operate for long blocks.
    /// From FFmpeg's ff_tns_max_bands_1024[] in aactab.c, indexed by sampling rate index.
    /// </summary>
    public static ReadOnlySpan<int> TnsMaxBandsLong =>
    [
        31, 31, 34, 40, 42, 51, 46, 46, 42, 42, 42, 39, 39
    ];

    /// <summary>
    /// Maximum number of scalefactor bands on which TNS can operate for short blocks.
    /// From FFmpeg's ff_tns_max_bands_128[] in aactab.c, indexed by sampling rate index.
    /// </summary>
    public static ReadOnlySpan<int> TnsMaxBandsShort =>
    [
        9, 9, 10, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14
    ];

    /// <summary>
    /// Gets the maximum TNS bands for a sample rate and block type.
    /// </summary>
    public static int GetTnsMaxBands(int sampleRateIndex, bool isShortBlock)
    {
        if (sampleRateIndex < 0 || sampleRateIndex > 12)
            return isShortBlock ? 14 : 42;

        return isShortBlock ? TnsMaxBandsShort[sampleRateIndex] : TnsMaxBandsLong[sampleRateIndex];
    }

    #region Codebook Tables

    /// <summary>
    /// Codebook dimensions (number of spectral values per codeword).
    /// </summary>
    public static ReadOnlySpan<int> CodebookDimension =>
    [
        0, 4, 4, 4, 4, 2, 2, 2, 2, 2, 2, 2
    ];

    /// <summary>
    /// Whether codebook uses unsigned values.
    /// </summary>
    public static ReadOnlySpan<bool> CodebookUnsigned =>
    [
        false, false, false, true, true, false, false, true, true, true, true, true
    ];

    /// <summary>
    /// Maximum absolute value in codebook.
    /// </summary>
    public static ReadOnlySpan<int> CodebookLav =>
    [
        0, 1, 1, 2, 2, 4, 4, 7, 7, 12, 12, 16
    ];

    // The scalefactor Huffman codebook that used to sit here is deleted. It was a 31-entry
    // placeholder - sequential integers 0..30, with the comment "These are simplified - real
    // implementation needs full binary tree" - documented as covering -60 to +60, which would be
    // 121 entries. Nothing referenced it: this decoder reaches its scalefactors another way, and
    // is bit-exact on the conformance gate without it. The real table, ff_aac_scalefactor_code
    // and ff_aac_scalefactor_bits from aactab.c, is generated into WmaTables, because WMA is what
    // actually builds a VLC from it - wma.h declares both extern for exactly that.

    #endregion

    #region Spectral Huffman Codebooks

    /// <summary>
    /// Huffman codebook 1: 4-tuple, signed, LAV=1
    /// </summary>
    public static readonly HuffmanEntry[] Codebook1 = BuildCodebook1();

    /// <summary>
    /// Huffman codebook 2: 4-tuple, signed, LAV=1
    /// </summary>
    public static readonly HuffmanEntry[] Codebook2 = BuildCodebook2();

    /// <summary>
    /// Huffman codebook 3: 4-tuple, unsigned, LAV=2
    /// </summary>
    public static readonly HuffmanEntry[] Codebook3 = BuildCodebook3();

    /// <summary>
    /// Huffman codebook 4: 4-tuple, unsigned, LAV=2
    /// </summary>
    public static readonly HuffmanEntry[] Codebook4 = BuildCodebook4();

    /// <summary>
    /// Huffman codebook 5: 2-tuple, signed, LAV=4
    /// </summary>
    public static readonly HuffmanEntry[] Codebook5 = BuildCodebook5();

    /// <summary>
    /// Huffman codebook 6: 2-tuple, signed, LAV=4
    /// </summary>
    public static readonly HuffmanEntry[] Codebook6 = BuildCodebook6();

    /// <summary>
    /// Huffman codebook 7: 2-tuple, unsigned, LAV=7
    /// </summary>
    public static readonly HuffmanEntry[] Codebook7 = BuildCodebook7();

    /// <summary>
    /// Huffman codebook 8: 2-tuple, unsigned, LAV=7
    /// </summary>
    public static readonly HuffmanEntry[] Codebook8 = BuildCodebook8();

    /// <summary>
    /// Huffman codebook 9: 2-tuple, unsigned, LAV=12
    /// </summary>
    public static readonly HuffmanEntry[] Codebook9 = BuildCodebook9();

    /// <summary>
    /// Huffman codebook 10: 2-tuple, unsigned, LAV=12
    /// </summary>
    public static readonly HuffmanEntry[] Codebook10 = BuildCodebook10();

    /// <summary>
    /// Huffman codebook 11: 2-tuple, unsigned, LAV=16+ (escape)
    /// </summary>
    public static readonly HuffmanEntry[] Codebook11 = BuildCodebook11();

    // Simplified codebook builders - real implementation needs full ISO tables
    private static HuffmanEntry[] BuildCodebook1()
    {
        // 4-tuple codebook, values -1, 0, 1 (3^4 = 81 entries)
        return new HuffmanEntry[81];
    }

    private static HuffmanEntry[] BuildCodebook2()
    {
        return new HuffmanEntry[81];
    }

    private static HuffmanEntry[] BuildCodebook3()
    {
        // 4-tuple codebook, values 0, 1, 2 (3^4 = 81 entries)
        return new HuffmanEntry[81];
    }

    private static HuffmanEntry[] BuildCodebook4()
    {
        return new HuffmanEntry[81];
    }

    private static HuffmanEntry[] BuildCodebook5()
    {
        // 2-tuple codebook, values -4 to 4 (9^2 = 81 entries)
        return new HuffmanEntry[81];
    }

    private static HuffmanEntry[] BuildCodebook6()
    {
        return new HuffmanEntry[81];
    }

    private static HuffmanEntry[] BuildCodebook7()
    {
        // 2-tuple codebook, values 0 to 7 (8^2 = 64 entries)
        return new HuffmanEntry[64];
    }

    private static HuffmanEntry[] BuildCodebook8()
    {
        return new HuffmanEntry[64];
    }

    private static HuffmanEntry[] BuildCodebook9()
    {
        // 2-tuple codebook, values 0 to 12 (13^2 = 169 entries)
        return new HuffmanEntry[169];
    }

    private static HuffmanEntry[] BuildCodebook10()
    {
        return new HuffmanEntry[169];
    }

    private static HuffmanEntry[] BuildCodebook11()
    {
        // 2-tuple codebook with escape (17^2 = 289 entries)
        return new HuffmanEntry[289];
    }

    #endregion
}

/// <summary>
/// Huffman table entry for AAC decoding.
/// </summary>
public readonly struct HuffmanEntry
{
    /// <summary>Codeword bits.</summary>
    public readonly uint Code;
    
    /// <summary>Codeword length in bits.</summary>
    public readonly byte Length;
    
    /// <summary>Decoded values (up to 4 for 4-tuple codebooks).</summary>
    public readonly short Value1;
    public readonly short Value2;
    public readonly short Value3;
    public readonly short Value4;

    public HuffmanEntry(uint code, byte length, short v1, short v2 = 0, short v3 = 0, short v4 = 0)
    {
        Code = code;
        Length = length;
        Value1 = v1;
        Value2 = v2;
        Value3 = v3;
        Value4 = v4;
    }
}

/// <summary>
/// Window coefficient tables for IMDCT.
/// </summary>
public static class AacWindowTables
{
    /// <summary>
    /// Sine window for 2048-point IMDCT (long block).
    /// w(n) = sin(π/2048 * (n + 0.5))
    /// </summary>
    public static readonly float[] SineWindowLong = GenerateSineWindow(2048);

    /// <summary>
    /// Sine window for 256-point IMDCT (short block).
    /// w(n) = sin(π/256 * (n + 0.5))
    /// </summary>
    public static readonly float[] SineWindowShort = GenerateSineWindow(256);

    /// <summary>
    /// Kaiser-Bessel Derived (KBD) window for long block.
    /// </summary>
    public static readonly float[] KbdWindowLong = GenerateKbdWindow(2048, 4.0f);

    /// <summary>
    /// Kaiser-Bessel Derived (KBD) window for short block.
    /// </summary>
    public static readonly float[] KbdWindowShort = GenerateKbdWindow(256, 6.0f);

    /// <summary><c>sine_960</c>: the sine window a 960-sample frame's long block overlaps with.</summary>
    public static readonly float[] SineWindow960 = GenerateSineWindow(1920);

    /// <summary><c>sine_120</c>: the sine window a 960-sample frame's short blocks overlap with.</summary>
    public static readonly float[] SineWindow120 = GenerateSineWindow(240);

    /// <summary><c>aac_kbd_long_960</c>, alpha 4.0 as <c>aacdec_float.c</c> initialises it.</summary>
    public static readonly float[] KbdWindow960 = GenerateKbdWindow(1920, 4.0f);

    /// <summary><c>aac_kbd_short_120</c>, alpha 6.0.</summary>
    public static readonly float[] KbdWindow120 = GenerateKbdWindow(240, 6.0f);

    /// <summary>
    /// <c>sine_512</c>: the window an AAC LD long block overlaps with, since LD uses a sine
    /// window where regular AAC would be free to choose a Kaiser-Bessel one.
    /// </summary>
    /// <remarks>
    /// Generated at length 1024 so the first 512 entries are FFmpeg's <c>ff_sine_512</c>, which
    /// is the whole span <c>vector_fmul_window(..., 256)</c> reads. The first 512 entries of
    /// <see cref="SineWindowLong"/> are NOT these: that table's argument is scaled by its own
    /// length, so a sine window of a different size is a different window, not a prefix of a
    /// longer one.
    /// </remarks>
    public static readonly float[] SineWindow512 = GenerateSineWindow(1024);

    /// <summary>
    /// The sine window, as <c>ff_sine_window_init</c> computes it.
    /// </summary>
    /// <remarks>
    /// The formula is the obvious one; what matters is that the argument is rounded to float
    /// before the sine is taken, because the C passes a double expression to <c>sinf</c>. Taking
    /// the sine in double and rounding afterwards gives a different last bit on some entries,
    /// and the window multiplies every output sample. The table is twice the length of FFmpeg's,
    /// whose second half this decoder's filterbank reaches by symmetry; the first half is
    /// FFmpeg's table entry for entry.
    /// </remarks>
    private static float[] GenerateSineWindow(int length)
    {
        var window = new float[length];
        double scale = Math.PI / length;

        for (int n = 0; n < length; n++)
            window[n] = MathF.Sin((float)(scale * (n + 0.5)));

        return window;
    }

    /// <summary>
    /// The Kaiser-Bessel derived window, from <c>ff_kbd_window_init</c>.
    /// </summary>
    /// <remarks>
    /// This used to be the textbook form - <c>I0(alpha*pi*sqrt(1-x^2)) / I0(alpha*pi)</c> over a
    /// plain sum, with the Bessel function as an ascending series. FFmpeg sums
    /// <c>i*(n-i)*alpha2</c> under the Bessel function, normalises with a symmetric count, and
    /// evaluates the Bessel function by minimax rational approximations. Algebraically the same
    /// window; numerically a different one, well above the last bit. It is now FFmpeg's, in
    /// <see cref="Dsp.Windows"/>, with the second half mirrored for the longer table this
    /// decoder's filterbank indexes.
    /// </remarks>
    private static float[] GenerateKbdWindow(int length, float alpha)
    {
        float[] half = Dsp.Windows.KbdWindow(alpha, length / 2);
        var window = new float[length];

        half.CopyTo(window, 0);
        for (int n = 0; n < length / 2; n++)
            window[length - 1 - n] = window[n];

        return window;
    }
}

/// <summary>
/// Inverse quantization tables for AAC.
/// </summary>
public static class AacQuantizationTables
{
    /// <summary>
    /// Precomputed 4/3 power table for inverse quantization.
    /// iq(x) = x^(4/3)
    /// </summary>
    public static readonly float[] InverseQuantTable = GenerateInverseQuantTable();

    private static float[] GenerateInverseQuantTable()
    {
        var table = new float[8192];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = (float)Math.Pow(i, 4.0 / 3.0);
        }
        return table;
    }

    /// <summary>
    /// Scalefactor gain table: <c>-2^((sf - 100) / 4)</c>, which is FFmpeg's
    /// <c>-ff_aac_pow2sf_tab[sf - 100 + POW_SF2_ZERO]</c>.
    /// </summary>
    public static readonly float[] ScalefactorGainTable = GenerateScalefactorGainTable();

    /// <summary>2^(i/16) for 0 &lt;= i &lt;= 15, as FFmpeg's aac_tableinit spells them.</summary>
    private static ReadOnlySpan<float> Exp2Sixteenths =>
    [
        1.00000000000000000000f, 1.04427378242741384032f,
        1.09050773266525765921f, 1.13878863475669165370f,
        1.18920711500272106672f, 1.24185781207348404859f,
        1.29683955465100966593f, 1.35425554693689272830f,
        1.41421356237309504880f, 1.47682614593949931139f,
        1.54221082540794082361f, 1.61049033194925430818f,
        1.68179283050742908606f, 1.75625216037329948311f,
        1.83400808640934246349f, 1.91520656139714729387f,
    ];

    private static float[] GenerateScalefactorGainTable()
    {
        // FFmpeg builds ff_aac_pow2sf_tab by doubling a running float every fourth step and
        // multiplying by 2^(k/16), rather than calling pow(). The two disagree in the last bit for
        // most entries, and since the result multiplies every spectral coefficient the difference
        // shows up in the decoded samples - so the construction is reproduced rather than the
        // formula it stands for.
        //
        // The 1/32768 FFmpeg carries in its IMDCT scale used to be folded in here. It is not any
        // more, and the reason is worth keeping: folding it is exact, but it changes where the
        // factor meets the arithmetic. FFmpeg's IMDCT scale is 1/(M*32768), and its exponent
        // table carries sqrt of that - an irrational 2^-12.5 for M = 1024. Fold the 32768 into
        // the gain instead and the table carries 2^-5, a different number, so every product
        // rounds differently. Bit-exactness with av_tx requires FFmpeg's arrangement, not an
        // algebraically equal one.
        //
        // Index sf corresponds to FFmpeg's sfo + 100, so entry sf reads pow2sf_tab[sf + 100].
        var table = new float[256];
        float running = 8.8817841970012523233890533447265625e-16f; // 2^-50
        int previousStep = 0;

        for (int i = 0; i < 428; i++)
        {
            int step = 4 * (i % 4);
            if (step < previousStep)
                running *= 2;
            previousStep = step;

            int sf = i - 100;
            if ((uint)sf < (uint)table.Length)
                table[sf] = -(running * Exp2Sixteenths[step]);
        }

        return table;
    }
}

// Translated from ffmpeg-master/libavcodec/gsmdec_data.c, gsmdec_data.h and gsm.h.
//
// The data layout first, before any of the algorithm: GSMContext is 280 int16 of excitation
// history, nine filter states, two sets of eight log-area ratios and a running mean. The
// 280-element ref_buf is the one field worth reading twice - the first 120 elements are the
// TAIL of the previous frame, because the long term predictor reaches back up to 120 samples
// (the "lag"), and the current frame is built in the 160 after them. gsm_decode_block ends by
// memcpy-ing the last 120 down to the front, which is why a frame decoded on its own and the
// same frame decoded in sequence are different audio.

using System;

namespace MediaKernel.Core.Codecs.Audio.Gsm;

/// <summary>The bit rates <c>gsm.h</c> names. <c>enum GSMModes</c>.</summary>
public enum GsmMode
{
    /// <summary>Full rate GSM 06.10, and the only mode plain GSM has.</summary>
    Gsm13000 = 0,

    /// <summary>MSN 12.4 kbit/s.</summary>
    Msn12400,

    /// <summary>MSN 11.8 kbit/s.</summary>
    Msn11800,

    /// <summary>MSN 11.2 kbit/s.</summary>
    Msn11200,

    /// <summary>MSN 10.6 kbit/s.</summary>
    Msn10600,

    /// <summary>MSN 10.0 kbit/s.</summary>
    Msn10000,

    /// <summary>MSN 9.4 kbit/s.</summary>
    Msn9400,

    /// <summary>MSN 8.8 kbit/s.</summary>
    Msn8800,

    /// <summary>MSN 8.2 kbit/s.</summary>
    Msn8200,

    /// <summary><c>NUM_GSM_MODES</c>.</summary>
    NumGsmModes,
}

/// <summary>The block and frame sizes from <c>gsm.h</c>.</summary>
public static class GsmSizes
{
    /// <summary><c>GSM_BLOCK_SIZE</c> - bytes per block of plain GSM.</summary>
    public const int GsmBlockSize = 33;

    /// <summary><c>GSM_MS_BLOCK_SIZE</c> - bytes per block of the Microsoft variant.</summary>
    public const int GsmMsBlockSize = 65;

    /// <summary><c>MSN_MIN_BLOCK_SIZE</c> - the smallest block MSN's reduced rates use.</summary>
    public const int MsnMinBlockSize = 41;

    /// <summary><c>GSM_FRAME_SIZE</c> - samples per block.</summary>
    public const int GsmFrameSize = 160;
}

/// <summary>
/// <c>GSMContext</c>. The decoder's whole state, which runs the length of the stream.
/// </summary>
public sealed class GsmContext
{
    /// <summary>
    /// Contains first 120 elements from the previous frame (used by <c>long_term_synth</c>
    /// according to the "lag"), then in the following 160 elements the current frame is
    /// constructed.
    /// </summary>
    public readonly short[] RefBuf = new short[280];

    /// <summary>The short term synthesis filter's state.</summary>
    public readonly int[] V = new int[9];

    /// <summary>
    /// This block's log-area ratios and the previous block's, which the short term filter
    /// interpolates between over the first 27 samples.
    /// </summary>
    public readonly int[][] Lar = [new int[8], new int[8]];

    /// <summary>Which half of <see cref="Lar"/> is this block's.</summary>
    public int LarIdx;

    /// <summary>The de-emphasis filter's running mean. <c>msr</c>.</summary>
    public int Msr;

    /// <summary>
    /// <c>gsm_flush</c>, which is a <c>memset</c> of the whole context: the excitation
    /// history, the filter state and the log-area ratios are all the stream's history, and a
    /// seek that kept them would ring with audio that was never played.
    /// </summary>
    public void Clear()
    {
        Array.Clear(RefBuf);
        Array.Clear(V);
        Array.Clear(Lar[0]);
        Array.Clear(Lar[1]);
        LarIdx = 0;
        Msr = 0;
    }
}

/// <summary>The tables from <c>gsmdec_data.c</c>.</summary>
public static class GsmDecData
{
    /// <summary><c>ff_gsm_long_term_gain_tab</c>.</summary>
    public static ReadOnlySpan<ushort> GsmLongTermGainTab => [3277, 11469, 21299, 32767];

    /// <summary>
    /// <c>ff_gsm_requant_tab</c>, flattened to 4x8. Indexed by the number of bits a sample
    /// was coded in, it maps that sample onto one of the eight columns of
    /// <see cref="GsmDequantTab"/>, so a 1-bit sample reads only the extremes and a 3-bit
    /// one reads all eight.
    /// </summary>
    public static ReadOnlySpan<byte> GsmRequantTab =>
    [
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 7, 0, 0, 0, 0, 0, 0,
        0, 2, 5, 7, 0, 0, 0, 0,
        0, 1, 2, 3, 4, 5, 6, 7,
    ];

    /// <summary><c>ff_gsm_dequant_tab</c>, flattened to 64x8.</summary>
    public static ReadOnlySpan<short> GsmDequantTab =>
    [
          -28,    -20,    -12,     -4,      4,     12,     20,     28,
          -56,    -40,    -24,     -8,      8,     24,     40,     56,
          -84,    -60,    -36,    -12,     12,     36,     60,     84,
         -112,    -80,    -48,    -16,     16,     48,     80,    112,
         -140,   -100,    -60,    -20,     20,     60,    100,    140,
         -168,   -120,    -72,    -24,     24,     72,    120,    168,
         -196,   -140,    -84,    -28,     28,     84,    140,    196,
         -224,   -160,    -96,    -32,     32,     96,    160,    224,
         -252,   -180,   -108,    -36,     36,    108,    180,    252,
         -280,   -200,   -120,    -40,     40,    120,    200,    280,
         -308,   -220,   -132,    -44,     44,    132,    220,    308,
         -336,   -240,   -144,    -48,     48,    144,    240,    336,
         -364,   -260,   -156,    -52,     52,    156,    260,    364,
         -392,   -280,   -168,    -56,     56,    168,    280,    392,
         -420,   -300,   -180,    -60,     60,    180,    300,    420,
         -448,   -320,   -192,    -64,     64,    192,    320,    448,
         -504,   -360,   -216,    -72,     72,    216,    360,    504,
         -560,   -400,   -240,    -80,     80,    240,    400,    560,
         -616,   -440,   -264,    -88,     88,    264,    440,    616,
         -672,   -480,   -288,    -96,     96,    288,    480,    672,
         -728,   -520,   -312,   -104,    104,    312,    520,    728,
         -784,   -560,   -336,   -112,    112,    336,    560,    784,
         -840,   -600,   -360,   -120,    120,    360,    600,    840,
         -896,   -640,   -384,   -128,    128,    384,    640,    896,
        -1008,   -720,   -432,   -144,    144,    432,    720,   1008,
        -1120,   -800,   -480,   -160,    160,    480,    800,   1120,
        -1232,   -880,   -528,   -176,    176,    528,    880,   1232,
        -1344,   -960,   -576,   -192,    192,    576,    960,   1344,
        -1456,  -1040,   -624,   -208,    208,    624,   1040,   1456,
        -1568,  -1120,   -672,   -224,    224,    672,   1120,   1568,
        -1680,  -1200,   -720,   -240,    240,    720,   1200,   1680,
        -1792,  -1280,   -768,   -256,    256,    768,   1280,   1792,
        -2016,  -1440,   -864,   -288,    288,    864,   1440,   2016,
        -2240,  -1600,   -960,   -320,    320,    960,   1600,   2240,
        -2464,  -1760,  -1056,   -352,    352,   1056,   1760,   2464,
        -2688,  -1920,  -1152,   -384,    384,   1152,   1920,   2688,
        -2912,  -2080,  -1248,   -416,    416,   1248,   2080,   2912,
        -3136,  -2240,  -1344,   -448,    448,   1344,   2240,   3136,
        -3360,  -2400,  -1440,   -480,    480,   1440,   2400,   3360,
        -3584,  -2560,  -1536,   -512,    512,   1536,   2560,   3584,
        -4032,  -2880,  -1728,   -576,    576,   1728,   2880,   4032,
        -4480,  -3200,  -1920,   -640,    640,   1920,   3200,   4480,
        -4928,  -3520,  -2112,   -704,    704,   2112,   3520,   4928,
        -5376,  -3840,  -2304,   -768,    768,   2304,   3840,   5376,
        -5824,  -4160,  -2496,   -832,    832,   2496,   4160,   5824,
        -6272,  -4480,  -2688,   -896,    896,   2688,   4480,   6272,
        -6720,  -4800,  -2880,   -960,    960,   2880,   4800,   6720,
        -7168,  -5120,  -3072,  -1024,   1024,   3072,   5120,   7168,
        -8063,  -5759,  -3456,  -1152,   1152,   3456,   5760,   8064,
        -8959,  -6399,  -3840,  -1280,   1280,   3840,   6400,   8960,
        -9855,  -7039,  -4224,  -1408,   1408,   4224,   7040,   9856,
       -10751,  -7679,  -4608,  -1536,   1536,   4608,   7680,  10752,
       -11647,  -8319,  -4992,  -1664,   1664,   4992,   8320,  11648,
       -12543,  -8959,  -5376,  -1792,   1792,   5376,   8960,  12544,
       -13439,  -9599,  -5760,  -1920,   1920,   5760,   9600,  13440,
       -14335, -10239,  -6144,  -2048,   2048,   6144,  10240,  14336,
       -16127, -11519,  -6912,  -2304,   2304,   6912,  11519,  16127,
       -17919, -12799,  -7680,  -2560,   2560,   7680,  12799,  17919,
       -19711, -14079,  -8448,  -2816,   2816,   8448,  14079,  19711,
       -21503, -15359,  -9216,  -3072,   3072,   9216,  15359,  21503,
       -23295, -16639,  -9984,  -3328,   3328,   9984,  16639,  23295,
       -25087, -17919, -10752,  -3584,   3584,  10752,  17919,  25087,
       -26879, -19199, -11520,  -3840,   3840,  11520,  19199,  26879,
       -28671, -20479, -12288,  -4096,   4096,  12288,  20479,  28671,
    ];

    /// <summary>
    /// <c>apcm_bits</c> - the eleven bit allocations a subframe's thirteen samples can use.
    /// </summary>
    private static ReadOnlySpan<byte> ApcmBits =>
    [
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    /// <summary>
    /// <c>ff_gsm_apcm_bits</c> as the row of <see cref="ApcmBits"/> each mode uses for each
    /// of its four subframes. The C holds pointers into <c>apcm_bits</c>; this holds the row
    /// index, which is the same table with one indirection fewer.
    /// </summary>
    private static ReadOnlySpan<byte> GsmApcmBitsRows =>
    [
        10, 10, 10, 10,  // 13000
        10, 10, 10,  6,  // 12400
        10, 10,  7,  5,  // 11800
        10,  8,  5,  5,  // 11200
         9,  5,  5,  5,  // 10600
         5,  5,  5,  1,  // 10000
         5,  5,  2,  0,  //  9400
         5,  3,  0,  0,  //  8800
         4,  0,  0,  0,  //  8200
    ];

    /// <summary>
    /// The thirteen bit widths subframe <paramref name="subframe"/> of a block at
    /// <paramref name="mode"/> uses. <c>ff_gsm_apcm_bits[mode][subframe]</c>.
    /// </summary>
    public static ReadOnlySpan<byte> GsmApcmBits(int mode, int subframe) =>
        ApcmBits.Slice(GsmApcmBitsRows[mode * 4 + subframe] * 13, 13);
}

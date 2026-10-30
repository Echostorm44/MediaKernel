// MediaKernel - AAC encoder tables
//
// A translation of libavcodec/aacenctab.c and aacenctab.h, plus ff_aac_pow34sf_tab, which lives
// in aactab.c beside the pow2sf table the decoder already builds.
//
// The encoder needs scalefactor BAND SIZES where the decoder needs offsets: the decoder is told
// where each band starts and the encoder walks the bands in order, adding each size as it goes.
// They hold the same information and FFmpeg keeps both, so both are here.

using System;

namespace MediaKernel.Core.Codecs.AAC.Encoder;

/// <summary>
/// The static tables of FFmpeg's AAC encoder: <c>aacenctab.c</c>, <c>aacenctab.h</c>, and
/// <c>ff_aac_pow34sf_tab</c> from <c>aactab.c</c>.
/// </summary>
internal static class AacEncTables
{
    /// <summary>Total number of usable codebooks. <c>CB_TOT</c>.</summary>
    public const int CbTot = 12;

    /// <summary>Total number of codebooks, including the special ones. <c>CB_TOT_ALL</c>.</summary>
    public const int CbTotAll = 15;

    /// <summary><c>AAC_MAX_CHANNELS</c>.</summary>
    public const int AacMaxChannels = 16;

    // ── scalefactor band sizes ──────────────────────────────────────────────────

    private static ReadOnlySpan<byte> SwbSize12896 =>
    [
        4, 4, 4, 4, 4, 4, 8, 8, 8, 16, 28, 36
    ];

    // swb_size_128_64 is #defined to swb_size_128_96.

    private static ReadOnlySpan<byte> SwbSize12848 =>
    [
        4, 4, 4, 4, 4, 8, 8, 8, 12, 12, 12, 16, 16, 16
    ];

    private static ReadOnlySpan<byte> SwbSize12824 =>
    [
        4, 4, 4, 4, 4, 4, 4, 8, 8, 8, 12, 12, 16, 16, 20
    ];

    private static ReadOnlySpan<byte> SwbSize12816 =>
    [
        4, 4, 4, 4, 4, 4, 4, 4, 8, 8, 12, 12, 16, 20, 20
    ];

    private static ReadOnlySpan<byte> SwbSize1288 =>
    [
        4, 4, 4, 4, 4, 4, 4, 8, 8, 8, 8, 12, 16, 20, 20
    ];

    private static ReadOnlySpan<byte> SwbSize102496 =>
    [
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 8, 8, 8, 8, 8,
        12, 12, 12, 12, 12, 16, 16, 24, 28, 36, 44,
        64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64
    ];

    private static ReadOnlySpan<byte> SwbSize102464 =>
    [
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 8, 8, 8, 8,
        12, 12, 12, 16, 16, 16, 20, 24, 24, 28, 36,
        40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40
    ];

    private static ReadOnlySpan<byte> SwbSize102448 =>
    [
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 8, 8, 8, 8, 8, 8, 8,
        12, 12, 12, 12, 16, 16, 20, 20, 24, 24, 28, 28,
        32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32,
        96
    ];

    private static ReadOnlySpan<byte> SwbSize102432 =>
    [
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 8, 8, 8, 8, 8, 8, 8,
        12, 12, 12, 12, 16, 16, 20, 20, 24, 24, 28, 28,
        32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32
    ];

    private static ReadOnlySpan<byte> SwbSize102424 =>
    [
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
        12, 12, 12, 12, 16, 16, 16, 20, 20, 24, 24, 28, 28,
        32, 36, 36, 40, 44, 48, 52, 52, 64, 64, 64, 64, 64
    ];

    private static ReadOnlySpan<byte> SwbSize102416 =>
    [
        8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
        12, 12, 12, 12, 12, 12, 12, 12, 12, 16, 16, 16, 16, 20, 20, 20, 24, 24, 28, 28,
        32, 36, 40, 40, 44, 48, 52, 56, 60, 64, 64, 64
    ];

    private static ReadOnlySpan<byte> SwbSize10248 =>
    [
        12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12,
        16, 16, 16, 16, 16, 16, 16, 20, 20, 20, 20, 24, 24, 24, 28, 28,
        32, 36, 36, 40, 44, 48, 52, 56, 60, 64, 80
    ];

    /// <summary>
    /// <c>ff_aac_swb_size_128</c>, indexed by the sample rate index. Short windows.
    /// </summary>
    /// <remarks>
    /// C arrays of pointers become a method returning a span: the rows have different lengths,
    /// so a jagged array would allocate thirteen arrays at startup to say what thirteen spans
    /// over static data already say for free.
    /// </remarks>
    public static ReadOnlySpan<byte> SwbSize128(int sampleRateIndex) => sampleRateIndex switch
    {
        0 or 1 => SwbSize12896,
        2 => SwbSize12896,          // swb_size_128_64 is the 96 kHz table
        3 or 4 or 5 => SwbSize12848,
        6 or 7 => SwbSize12824,
        8 or 9 or 10 => SwbSize12816,
        11 or 12 => SwbSize1288,
        _ => throw new ArgumentOutOfRangeException(nameof(sampleRateIndex), sampleRateIndex,
                 "AAC has thirteen sample rate indices, 0 to 12."),
    };

    /// <summary>
    /// <c>ff_aac_swb_size_1024</c>, indexed by the sample rate index. Long windows.
    /// </summary>
    public static ReadOnlySpan<byte> SwbSize1024(int sampleRateIndex) => sampleRateIndex switch
    {
        0 or 1 => SwbSize102496,
        2 => SwbSize102464,
        3 or 4 => SwbSize102448,
        5 => SwbSize102432,
        6 or 7 => SwbSize102424,
        8 or 9 or 10 => SwbSize102416,
        11 or 12 => SwbSize10248,
        _ => throw new ArgumentOutOfRangeException(nameof(sampleRateIndex), sampleRateIndex,
                 "AAC has thirteen sample rate indices, 0 to 12."),
    };

    // ── channel configuration ───────────────────────────────────────────────────

    // The C writes TYPE_SCE and friends straight into the table; these keep it that readable.
    private const byte Sce = (byte)SyntaxElementType.SingleChannelElement;
    private const byte Cpe = (byte)SyntaxElementType.ChannelPairElement;
    private const byte Lfe = (byte)SyntaxElementType.LowFrequencyEffectsElement;

    /// <summary>
    /// <c>aac_chan_configs</c>: for each channel count, the elements the frame is built from.
    /// Row <c>n - 1</c> is <c>[count, type, type, ...]</c>; a leading zero means the count
    /// cannot be coded without a programme config element.
    /// </summary>
    public static ReadOnlySpan<byte> ChanConfigs =>
    [
        1, Sce,   0,   0,   0,   0,     // 1: single channel element
        1, Cpe,   0,   0,   0,   0,     // 2: channel pair
        2, Sce, Cpe,   0,   0,   0,     // 3: centre + stereo
        3, Sce, Cpe, Sce,   0,   0,     // 4: front centre + stereo + back centre
        3, Sce, Cpe, Cpe,   0,   0,     // 5: front centre + stereo + back stereo
        4, Sce, Cpe, Cpe, Lfe,   0,     // 6: front centre + stereo + back stereo + LFE
        0,   0,   0,   0,   0,   0,     // 7: invalid without a PCE
        5, Sce, Cpe, Cpe, Cpe, Lfe,     // 8: centre + front + side + back stereo + LFE
    ];

    /// <summary>One row of <see cref="ChanConfigs"/>, for a channel count of 1 to 8.</summary>
    public static ReadOnlySpan<byte> ChanConfig(int channels)
    {
        if (channels is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels,
                "Without a programme config element AAC codes 1 to 8 channels.");
        }
        return ChanConfigs.Slice((channels - 1) * 6, 6);
    }

    /// <summary>
    /// <c>aac_chan_maps</c>: remaps channels from this tree's order to AAC's, which puts the
    /// centre channel first. Row <c>n - 1</c>, the first <c>n</c> entries.
    /// </summary>
    public static ReadOnlySpan<byte> ChanMaps =>
    [
        0, 0, 0, 0, 0, 0, 0, 0,     // 1
        0, 1, 0, 0, 0, 0, 0, 0,     // 2
        2, 0, 1, 0, 0, 0, 0, 0,     // 3
        2, 0, 1, 3, 0, 0, 0, 0,     // 4
        2, 0, 1, 3, 4, 0, 0, 0,     // 5
        2, 0, 1, 4, 5, 3, 0, 0,     // 6
        0, 0, 0, 0, 0, 0, 0, 0,     // 7: needs a PCE
        2, 0, 1, 6, 7, 4, 5, 3,     // 8
    ];

    /// <summary>One row of <see cref="ChanMaps"/>, for a channel count of 1 to 8.</summary>
    public static ReadOnlySpan<byte> ChanMap(int channels)
    {
        if (channels is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels,
                "Without a programme config element AAC codes 1 to 8 channels.");
        }
        return ChanMaps.Slice((channels - 1) * 8, channels);
    }

    // ── run lengths, TNS, codebooks ─────────────────────────────────────────────

    /// <summary><c>run_value_bits_long</c>: bits to code a codebook run, long windows.</summary>
    public static ReadOnlySpan<byte> RunValueBitsLong =>
    [
         5,  5,  5,  5,  5,  5,  5,  5,  5,  5,  5,  5,  5,  5,  5,  5,
         5,  5,  5,  5,  5,  5,  5,  5,  5,  5,  5,  5,  5,  5,  5, 10,
        10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
        10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 15
    ];

    /// <summary><c>run_value_bits_short</c>: the same, short windows.</summary>
    public static ReadOnlySpan<byte> RunValueBitsShort =>
    [
        3, 3, 3, 3, 3, 3, 3, 6, 6, 6, 6, 6, 6, 6, 6, 9
    ];

    /// <summary>
    /// <c>run_value_bits</c>: index 0 is long, 1 is short - the C indexes it with
    /// <c>ics.num_windows == 8</c>.
    /// </summary>
    public static ReadOnlySpan<byte> RunValueBits(int shortWindows) =>
        shortWindows != 0 ? RunValueBitsShort : RunValueBitsLong;

    /// <summary><c>tns_min_sfb_short</c>: the first band TNS may filter, short windows.</summary>
    public static ReadOnlySpan<byte> TnsMinSfbShort =>
    [
        2, 2, 2, 3, 3, 4, 6, 6, 8, 10, 10, 12, 12, 12, 12, 12
    ];

    /// <summary><c>tns_min_sfb_long</c>: the same, long windows.</summary>
    public static ReadOnlySpan<byte> TnsMinSfbLong =>
    [
        12, 13, 15, 16, 17, 20, 25, 26, 24, 28, 30, 31, 31, 31, 31, 31
    ];

    /// <summary><c>tns_min_sfb</c>: index 0 is long, 1 is short.</summary>
    public static ReadOnlySpan<byte> TnsMinSfb(int shortWindows) =>
        shortWindows != 0 ? TnsMinSfbShort : TnsMinSfbLong;

    /// <summary>
    /// <c>aac_cb_out_map</c>: a band coding path index to a codebook index. 12 is missing
    /// because it is RESERVED_BT, which no band may use.
    /// </summary>
    public static ReadOnlySpan<byte> CbOutMap => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 14, 15];

    /// <summary><c>aac_cb_in_map</c>: the inverse of <see cref="CbOutMap"/>.</summary>
    public static ReadOnlySpan<byte> CbInMap => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0, 12, 13, 14];

    /// <summary><c>aac_cb_range</c>: the span of values each codebook can code.</summary>
    public static ReadOnlySpan<byte> CbRange => [0, 3, 3, 3, 3, 9, 9, 8, 8, 13, 13, 17];

    /// <summary><c>aac_cb_maxval</c>: the largest magnitude each codebook can code.</summary>
    public static ReadOnlySpan<byte> CbMaxval => [0, 1, 1, 2, 2, 4, 4, 7, 7, 12, 12, 16];

    /// <summary>
    /// <c>aac_maxval_cb</c>: the cheapest codebook that can code a given quantised maximum.
    /// Indexed by the maximum itself, which is why <c>find_min_book</c> range-checks first.
    /// </summary>
    public static ReadOnlySpan<byte> MaxvalCb => [0, 1, 3, 5, 5, 7, 7, 7, 9, 9, 9, 9, 9, 11];

    // ── codebook vectors ────────────────────────────────────────────────────────

    /// <summary>
    /// <c>ff_aac_codebook_vectors</c>: the dequantised value each codebook index stands for,
    /// indexed by codebook 1 to 11. Four values per index for codebooks 1 to 4, two for the
    /// rest.
    /// </summary>
    /// <remarks>
    /// Derived rather than transcribed. FFmpeg writes out 1,854 floats across six tables, and
    /// every one of them is <c>sign * |v|^(4/3)</c> over the codebook's value range in
    /// lexicographic order, last element varying fastest - so typing them in would be 1,854
    /// chances to make a mistake that no compiler sees, in tables that decide what every
    /// coefficient quantises to. The aacenc-tables gate parses FFmpeg's own literals out of
    /// aactab.c and compares them against what this generates, which checks the derivation
    /// and the transcription at once.
    /// <para>
    /// The exception is codebook 11's largest magnitude, which is stored as 64.0 rather than
    /// 16^(4/3). That is not a value, it is the escape marker: the band cost function tests
    /// <c>vec[j] == 64.0f</c> to decide a coefficient is coded with an escape sequence.
    /// </para>
    /// </remarks>
    public static ReadOnlySpan<float> CodebookVectors(int codebook) => codebook switch
    {
        1 or 2 => Vector0,
        3 or 4 => Vector2,
        5 or 6 => Vector4,
        7 or 8 => Vector6,
        9 or 10 => Vector8,
        11 => Vector10,
        _ => throw new ArgumentOutOfRangeException(nameof(codebook), codebook,
                 "AAC's spectral codebooks are numbered 1 to 11."),
    };

    /// <summary>Values per index for a spectral codebook: four for 1 to 4, two above.</summary>
    public static int CodebookDimension(int codebook) => codebook <= 4 ? 4 : 2;

    private static readonly float[] Vector0 = BuildCodebookVectors(4, 1, signedBook: true);
    private static readonly float[] Vector2 = BuildCodebookVectors(4, 2, signedBook: false);
    private static readonly float[] Vector4 = BuildCodebookVectors(2, 4, signedBook: true);
    private static readonly float[] Vector6 = BuildCodebookVectors(2, 7, signedBook: false);
    private static readonly float[] Vector8 = BuildCodebookVectors(2, 12, signedBook: false);
    private static readonly float[] Vector10 = BuildCodebookVectors(2, 16, signedBook: false);

    /// <summary>The escape marker codebook 11 carries in place of its largest magnitude.</summary>
    public const float EscapeValue = 64.0f;

    private static float[] BuildCodebookVectors(int dim, int lav, bool signedBook)
    {
        int range = signedBook ? 2 * lav + 1 : lav + 1;
        int count = 1;
        for (int i = 0; i < dim; i++) count *= range;

        var table = new float[count * dim];
        for (int index = 0; index < count; index++)
        {
            int rest = index;
            for (int j = dim - 1; j >= 0; j--)
            {
                int v = signedBook ? rest % range - lav : rest % range;
                rest /= range;

                float magnitude = v == 0 ? 0.0f
                    // Codebook 11 is the escape book: its top magnitude is the marker, and
                    // the real value is coded in the bitstream after the codeword.
                    : !signedBook && lav == 16 && Math.Abs(v) == lav ? EscapeValue
                    : (float)Math.Pow(Math.Abs(v), 4.0 / 3.0);

                table[index * dim + j] = v < 0 ? -magnitude : magnitude;
            }
        }
        return table;
    }

    // ── the scalefactor gain tables ─────────────────────────────────────────────

    /// <summary>
    /// <c>ff_aac_pow34sf_tab</c>: 2<sup>3(i - 200)/16</sup>, the quantiser's gain.
    /// <c>find_min_book</c> and the quantisation loops index it at
    /// <c>POW_SF2_ZERO - sf + SCALE_ONE_POS - SCALE_DIV_512</c>.
    /// </summary>
    /// <remarks>
    /// Built FFmpeg's way, by doubling a running float every sixteen-step cycle rather than
    /// calling <c>pow</c>. That is not a micro-optimisation to skip: <c>aac_tableinit</c> says
    /// in a comment that it is "much more efficient and accurate", the two disagree in the last
    /// bit, and this table multiplies every spectral coefficient before it is quantised - a
    /// one-ulp difference moves a coefficient across a rounding boundary and changes the
    /// codebook the band is coded with. The same construction already builds
    /// <see cref="AacUsacTables.Pow2SfTab"/>.
    /// </remarks>
    public static readonly float[] Pow34SfTab = GeneratePow34SfTab();

    /// <summary><c>POW_SF2_ZERO</c>: the bias that makes a scalefactor an index.</summary>
    public const int PowSf2Zero = 200;

    private static float[] GeneratePow34SfTab()
    {
        var table = new float[428];
        float running = 3.63797880709171295166015625e-12f;   // 2^-38
        int previousStep = 8;

        for (int i = 0; i < table.Length; i++)
        {
            int step = (8 + 3 * i) % 16;
            if (step < previousStep)
                running *= 2;
            previousStep = step;

            table[i] = running * Exp2Sixteenths[step];
        }

        return table;
    }

    /// <summary>2^(i/16) for 0 &lt;= i &lt;= 15, as <c>aac_tableinit</c> spells them.</summary>
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
}

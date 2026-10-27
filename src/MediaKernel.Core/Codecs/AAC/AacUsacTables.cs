// USAC's own tables from ffmpeg-master/libavcodec/aactab.c.
//
// Small enough to read, so they are transcribed rather than script-generated, and each one is
// checked against the C below. The band tables USAC shares with AAC-LC (ff_swb_offset_1024,
// ff_swb_offset_128 and their counts) are already in AacHuffmanTables and are not duplicated here.

using System;

namespace MediaKernel.Core.Codecs.AAC;

/// <summary>
/// The tables that belong to USAC alone.
/// </summary>
public static class AacUsacTables
{
    /// <summary>
    /// <c>ff_aac_usac_samplerate[32]</c>: <c>usacSamplingFrequencyIndex</c> to a rate.
    /// </summary>
    /// <remarks>
    /// Not the same table as AAC's four-bit sampling index: it is five bits wide and continues
    /// past 8 kHz into rates AAC has no index for (57600 down to 9600), with -1 in the slots the
    /// spec reserves. A -1 is a malformed stream, not a rate.
    /// </remarks>
    public static ReadOnlySpan<int> Samplerate =>
    [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025,  8000,  7350,    -1,    -1, 57600,
        51200, 40000, 38400, 34150, 28800, 25600, 20000, 19200,
        17075, 14400, 12800,  9600,    -1,    -1,    -1,    -1,
    ];

    /// <summary><c>ff_tns_max_bands_usac_1024[13]</c>.</summary>
    /// <remarks>
    /// USAC has its own TNS band limits and they are not AAC's: this one differs from
    /// <c>ff_tns_max_bands_1024</c>, and the 128 table differs from <c>ff_tns_max_bands_128</c>
    /// in the top seven entries. Using AAC's would filter the wrong number of bands at 24 kHz
    /// and below.
    /// </remarks>
    public static ReadOnlySpan<byte> TnsMaxBands1024 =>
    [
        31, 31, 34, 40, 42, 51, 47, 47, 43, 43, 43, 40, 40,
    ];

    /// <summary><c>ff_tns_max_bands_usac_128[13]</c>.</summary>
    public static ReadOnlySpan<byte> TnsMaxBands128 =>
    [
        9, 9, 10, 14, 14, 14, 15, 15, 15, 15, 15, 15, 15,
    ];

    /// <summary>
    /// <c>ff_usac_noise_fill_start_offset[2][2]</c>: the first coefficient noise filling may
    /// touch, by <c>frame_length_short</c> then by whether the window is eight-short.
    /// </summary>
    public static ReadOnlySpan<byte> NoiseFillStartOffset =>
    [
        160, 20,
        120, 15,
    ];

    /// <summary>
    /// <c>ff_aac_usac_mdst_filt_cur[4][4][7]</c>: the MDST estimation filters for complex
    /// stereo prediction, indexed by window type then by the pair of window shapes.
    /// </summary>
    /// <remarks>
    /// Flattened to one span of 4*4*7 and indexed through <see cref="MdstFilter"/>, because a
    /// jagged array of 16 seven-element arrays is 16 allocations and a span of literals is none.
    /// Each filter is antisymmetric with a zero centre tap - it estimates an imaginary part from
    /// a real one - so a transcription error shows up as a broken symmetry, which is worth
    /// knowing when reading the numbers back.
    /// </remarks>
    public static ReadOnlySpan<float> MdstFiltCur =>
    [
        // Window 0: only-long and eight-short.
         0.000000f,  0.000000f,  0.500000f, 0.000000f, -0.500000f,  0.000000f,  0.000000f,
         0.091497f,  0.000000f,  0.581427f, 0.000000f, -0.581427f,  0.000000f, -0.091497f,
         0.045748f,  0.057238f,  0.540714f, 0.000000f, -0.540714f, -0.057238f, -0.045748f,
         0.045748f, -0.057238f,  0.540714f, 0.000000f, -0.540714f,  0.057238f, -0.045748f,

        // Window 1: long-start.
         0.102658f,  0.103791f,  0.567149f, 0.000000f, -0.567149f, -0.103791f, -0.102658f,
         0.150512f,  0.047969f,  0.608574f, 0.000000f, -0.608574f, -0.047969f, -0.150512f,
         0.104763f,  0.105207f,  0.567861f, 0.000000f, -0.567861f, -0.105207f, -0.104763f,
         0.148406f,  0.046553f,  0.607863f, 0.000000f, -0.607863f, -0.046553f, -0.148406f,

        // Window 2: long-stop.
         0.102658f, -0.103791f,  0.567149f, 0.000000f, -0.567149f,  0.103791f, -0.102658f,
         0.150512f, -0.047969f,  0.608574f, 0.000000f, -0.608574f,  0.047969f, -0.150512f,
         0.148406f, -0.046553f,  0.607863f, 0.000000f, -0.607863f,  0.046553f, -0.148406f,
         0.104763f, -0.105207f,  0.567861f, 0.000000f, -0.567861f,  0.105207f, -0.104763f,

        // Window 3: stop-start.
         0.205316f,  0.000000f,  0.634298f, 0.000000f, -0.634298f,  0.000000f, -0.205316f,
         0.209526f,  0.000000f,  0.635722f, 0.000000f, -0.635722f,  0.000000f, -0.209526f,
         0.207421f,  0.001416f,  0.635010f, 0.000000f, -0.635010f, -0.001416f, -0.207421f,
         0.207421f, -0.001416f,  0.635010f, 0.000000f, -0.635010f,  0.001416f, -0.207421f,
    ];

    /// <summary>One MDST filter, as <c>ff_aac_usac_mdst_filt_cur[window][shape]</c>.</summary>
    public static ReadOnlySpan<float> MdstFilter(int window, int shape)
    {
        if ((uint)window > 3 || (uint)shape > 3)
            throw new ArgumentOutOfRangeException(
                window > 3 ? nameof(window) : nameof(shape),
                "ff_aac_usac_mdst_filt_cur is indexed by four window types and four shape "
              + "pairings; anything else means the caller derived the index wrongly.");

        return MdstFiltCur.Slice((window * 4 + shape) * 7, 7);
    }

    /// <summary>
    /// <c>ff_aac_pow2sf_tab</c>: 2<sup>(i - 200)/4</sup>, which <c>dequant_scalefactors</c>
    /// indexes at <c>sfo + POW_SF2_ZERO</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="AacHuffmanTables.ScalefactorGainTable"/> is the same numbers already negated,
    /// but it only covers <c>sfo</c> from -100 to 155, which is the range AAC's clamped
    /// scalefactors reach. USAC needs more: <c>apply_noise_fill</c> adds a noise offset to the
    /// scalefactor offset and floors the result at -200, so the index runs down to 0 here. The
    /// construction is FFmpeg's, doubling a running float every fourth step rather than calling
    /// <c>pow</c>, because the two disagree in the last bit and this multiplies every
    /// coefficient.
    /// </remarks>
    public static readonly float[] Pow2SfTab = GeneratePow2SfTab();

    /// <summary><c>POW_SF2_ZERO</c>: the bias that makes <c>sfo</c> an index.</summary>
    public const int Pow2SfZero = 200;

    private static float[] GeneratePow2SfTab()
    {
        var table = new float[428];
        float running = 8.8817841970012523233890533447265625e-16f; // 2^-50
        int previousStep = 0;

        for (int i = 0; i < table.Length; i++)
        {
            int step = 4 * (i % 4);
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

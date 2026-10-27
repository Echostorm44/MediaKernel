// WMA v1/v2 decoder state - a translation of ffmpeg-master/libavcodec/wma.h.
//
// The fields are in the C's declared order, because the C's context *is* the algorithm: which
// value is per-channel, which is per-block-size and which is per-frame is most of what there is to
// know about this codec, and reordering them loses that.

using System;
using MediaKernel.Core.Codecs.MpegVideo;

namespace MediaKernel.Core.Codecs.Audio.Wma;

/// <summary>Sizes and limits from <c>wma.h</c>.</summary>
internal static class WmaLimits
{
    /// <summary><c>BLOCK_MIN_BITS</c>: the smallest MDCT block is 1 &lt;&lt; 7.</summary>
    public const int BlockMinBits = 7;

    /// <summary><c>BLOCK_MAX_BITS</c>.</summary>
    public const int BlockMaxBits = 11;

    /// <summary><c>BLOCK_MAX_SIZE</c>.</summary>
    public const int BlockMaxSize = 1 << BlockMaxBits;

    /// <summary><c>BLOCK_NB_SIZES</c>: how many block sizes a stream may switch between.</summary>
    public const int BlockNbSizes = BlockMaxBits - BlockMinBits + 1;

    /// <summary><c>HIGH_BAND_MAX_SIZE</c>. The C's comment is "XXX: find exact max size".</summary>
    public const int HighBandMaxSize = 16;

    /// <summary><c>NB_LSP_COEFS</c>.</summary>
    public const int NbLspCoefs = 10;

    /// <summary><c>MAX_CODED_SUPERFRAME_SIZE</c>. The C's comment is "XXX: is it a suitable value ?".</summary>
    public const int MaxCodedSuperframeSize = 32768;

    /// <summary><c>MAX_CHANNELS</c>: WMA v1 and v2 are mono or stereo.</summary>
    public const int MaxChannels = 2;

    /// <summary><c>NOISE_TAB_SIZE</c>.</summary>
    public const int NoiseTabSize = 8192;

    /// <summary><c>LSP_POW_BITS</c>.</summary>
    public const int LspPowBits = 7;

    /// <summary><c>VLCBITS</c>: bits read at the first level of a coefficient VLC.</summary>
    public const int VlcBits = 9;

    /// <summary><c>VLCMAX</c>: how many levels a coefficient VLC lookup may descend.</summary>
    public const int VlcMax = (22 + VlcBits - 1) / VlcBits;

    /// <summary><c>EXPVLCBITS</c> (wmadec.c).</summary>
    public const int ExpVlcBits = 8;

    /// <summary><c>EXPMAX</c> (wmadec.c).</summary>
    public const int ExpMax = (19 + ExpVlcBits - 1) / ExpVlcBits;

    /// <summary><c>HGAINVLCBITS</c> (wmadec.c).</summary>
    public const int HGainVlcBits = 9;

    /// <summary><c>HGAINMAX</c> (wmadec.c).</summary>
    public const int HGainMax = (13 + HGainVlcBits - 1) / HGainVlcBits;
}

/// <summary>
/// <c>coef_vlcs[6]</c>: the six spectral code sets, reached by index.
/// </summary>
/// <remarks>
/// The C stores pointers in a struct. A C# struct cannot hold a <see cref="ReadOnlySpan{T}"/>, and
/// making these arrays instead would put four heap allocations behind every table for no gain, so
/// the sets are selected by a switch on the index. <c>ff_wma_init</c> picks a pair of indices and
/// nothing else ever needs them, so the indirection costs nothing at decode time.
/// </remarks>
internal static class WmaCoefVlcTables
{
    /// <summary>How many code sets there are.</summary>
    public const int Count = 6;

    public static ReadOnlySpan<uint> HuffCodes(int index) => index switch
    {
        0 => WmaTables.Coef0Huffcodes,
        1 => WmaTables.Coef1Huffcodes,
        2 => WmaTables.Coef2Huffcodes,
        3 => WmaTables.Coef3Huffcodes,
        4 => WmaTables.Coef4Huffcodes,
        5 => WmaTables.Coef5Huffcodes,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static ReadOnlySpan<byte> HuffBits(int index) => index switch
    {
        0 => WmaTables.Coef0Huffbits,
        1 => WmaTables.Coef1Huffbits,
        2 => WmaTables.Coef2Huffbits,
        3 => WmaTables.Coef3Huffbits,
        4 => WmaTables.Coef4Huffbits,
        5 => WmaTables.Coef5Huffbits,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static ReadOnlySpan<ushort> Levels(int index) => index switch
    {
        0 => WmaTables.Levels0,
        1 => WmaTables.Levels1,
        2 => WmaTables.Levels2,
        3 => WmaTables.Levels3,
        4 => WmaTables.Levels4,
        5 => WmaTables.Levels5,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}

/// <summary>
/// <c>WMACodecContext</c>: everything one WMA v1/v2 stream needs while it decodes.
/// </summary>
/// <remarks>
/// A class, not a struct: it is nearly 100 KB of buffers, lives for the length of the stream, and
/// is passed by reference everywhere in the C as well.
/// </remarks>
internal sealed class WmaContext
{
    /// <summary>1 = 0x160 (WMAV1), 2 = 0x161 (WMAV2).</summary>
    public int Version;

    public bool UseBitReservoir;
    public bool UseVariableBlockLen;

    /// <summary>Exponent coding: false = LSP, true = VLC plus delta.</summary>
    public bool UseExpVlc;

    /// <summary>True if perceptual noise is added.</summary>
    public bool UseNoiseCoding;

    public int ByteOffsetBits;

    /// <summary>The exponent VLC, built from aactab.c's scalefactor code.</summary>
    public VLCElem[]? ExpVlc;

    public readonly int[] ExponentSizes = new int[WmaLimits.BlockNbSizes];

    /// <summary><c>exponent_bands[BLOCK_NB_SIZES][25]</c>.</summary>
    public readonly int[,] ExponentBands = new int[WmaLimits.BlockNbSizes, 25];

    /// <summary>Index of the first coefficient in the high band.</summary>
    public readonly int[] HighBandStart = new int[WmaLimits.BlockNbSizes];

    /// <summary>First coded coefficient.</summary>
    public int CoefsStart;

    /// <summary>Maximum number of coded coefficients, per block size.</summary>
    public readonly int[] CoefsEnd = new int[WmaLimits.BlockNbSizes];

    public readonly int[] ExponentHighSizes = new int[WmaLimits.BlockNbSizes];

    public readonly int[,] ExponentHighBands =
        new int[WmaLimits.BlockNbSizes, WmaLimits.HighBandMaxSize];

    /// <summary>The high-band gain VLC.</summary>
    public VLCElem[]? HGainVlc;

    /// <summary>Coded values in the high bands.</summary>
    public readonly int[,] HighBandCoded =
        new int[WmaLimits.MaxChannels, WmaLimits.HighBandMaxSize];

    public readonly int[,] HighBandValues =
        new int[WmaLimits.MaxChannels, WmaLimits.HighBandMaxSize];

    // There are two possible tables for spectral coefficients.
    public readonly VLCElem[]?[] CoefVlc = new VLCElem[2][];
    public readonly ushort[]?[] RunTable = new ushort[2][];
    public readonly float[]?[] LevelTable = new float[2][];
    public readonly ushort[]?[] IntTable = new ushort[2][];

    /// <summary>Which of <see cref="WmaCoefVlcTables"/> each of the two tables came from.</summary>
    public readonly int[] CoefVlcIndex = new int[2];

    // Frame info.

    /// <summary>Frame length in samples.</summary>
    public int FrameLen;

    /// <summary><c>frame_len = 1 &lt;&lt; frame_len_bits</c>.</summary>
    public int FrameLenBits;

    /// <summary>Number of block sizes this stream may use.</summary>
    public int NbBlockSizes;

    // Block info.
    public bool ResetBlockLengths;
    public int BlockLenBits;
    public int NextBlockLenBits;
    public int PrevBlockLenBits;
    public int BlockLen;
    public int BlockNum;
    public int BlockPos;

    /// <summary>True if mid/side stereo mode.</summary>
    public bool MsStereo;

    /// <summary>True if the channel is coded.</summary>
    public readonly bool[] ChannelCoded = new bool[WmaLimits.MaxChannels];

    /// <summary>log2 of the frame-to-exponent length ratio.</summary>
    public readonly int[] ExponentsBsize = new int[WmaLimits.MaxChannels];

    public readonly float[][] Exponents = Jagged(WmaLimits.MaxChannels, WmaLimits.BlockMaxSize);

    public readonly float[] MaxExponent = new float[WmaLimits.MaxChannels];

    /// <summary><c>coefs1</c>: the decoded coefficients, before the exponents are applied.</summary>
    public readonly float[][] Coefs1 = Jagged(WmaLimits.MaxChannels, WmaLimits.BlockMaxSize);

    public readonly float[][] Coefs = Jagged(WmaLimits.MaxChannels, WmaLimits.BlockMaxSize);

    /// <summary>The IMDCT's output for one block.</summary>
    public readonly float[] Output = new float[WmaLimits.BlockMaxSize * 2];

    /// <summary>One inverse MDCT per block size.</summary>
    public readonly Dsp.Tx.TxMdct?[] Mdct = new Dsp.Tx.TxMdct[WmaLimits.BlockNbSizes];

    /// <summary>The sine window for each block size.</summary>
    public readonly float[]?[] Windows = new float[WmaLimits.BlockNbSizes][];

    /// <summary>Output buffer for one frame, and the tail the next IMDCT windows into.</summary>
    public readonly float[][] FrameOut = Jagged(WmaLimits.MaxChannels, WmaLimits.BlockMaxSize * 2);

    // Last frame info.
    public readonly byte[] LastSuperframe = new byte[WmaLimits.MaxCodedSuperframeSize + 64];
    public int LastBitoffset;
    public int LastSuperframeLen;

    public readonly bool[] ExponentsInitialized = new bool[WmaLimits.MaxChannels];

    public readonly float[] NoiseTable = new float[WmaLimits.NoiseTabSize];
    public int NoiseIndex;

    /// <summary>
    /// The C's comment is "XXX: suppress that and integrate it in the noise array".
    /// </summary>
    public float NoiseMult;

    // lsp_to_curve tables.
    public readonly float[] LspCosTable = new float[WmaLimits.BlockMaxSize];
    public readonly float[] LspPowETable = new float[256];
    public readonly float[] LspPowMTable1 = new float[1 << WmaLimits.LspPowBits];
    public readonly float[] LspPowMTable2 = new float[1 << WmaLimits.LspPowBits];

    /// <summary>Set once the stream has ended, to flush the samples still held.</summary>
    public bool EofDone;

    // Stream parameters the C reads from AVCodecContext.
    public int SampleRate;
    public int Channels;
    public long BitRate;
    public int BlockAlign;

    private static float[][] Jagged(int rows, int columns)
    {
        var result = new float[rows][];
        for (int i = 0; i < rows; i++)
            result[i] = new float[columns];
        return result;
    }
}

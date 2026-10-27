// AC-3 (Dolby Digital) Decoder Types
// Based on ATSC A/52 specification

namespace MediaKernel.Core.Codecs.AC3;

/// <summary>
/// AC-3 sync frame structure containing header and audio data.
/// </summary>
public readonly struct Ac3SyncFrame
{
    /// <summary>Sync word (0x0B77).</summary>
    public const ushort SyncWord = 0x0B77;
    
    /// <summary>Frame size in bytes (including sync word).</summary>
    public readonly int FrameSize;
    
    /// <summary>Sample rate in Hz.</summary>
    public readonly int SampleRate;
    
    /// <summary>Bitrate in bits per second.</summary>
    public readonly int Bitrate;
    
    /// <summary>Bitstream identification (bsid). AC-3 uses 0-8, E-AC-3 uses 16.</summary>
    public readonly int BitstreamId;
    
    /// <summary>Bitstream mode (bsmod) - content type.</summary>
    public readonly Ac3BitstreamMode BitstreamMode;
    
    /// <summary>Audio coding mode (acmod) - channel configuration.</summary>
    public readonly Ac3AudioCodingMode AudioCodingMode;
    
    /// <summary>LFE channel on (lfeon) - subwoofer present.</summary>
    public readonly bool LfeOn;
    
    /// <summary>Number of full bandwidth channels (1-5).</summary>
    public readonly int FullBandwidthChannels;
    
    /// <summary>Total number of channels including LFE.</summary>
    public readonly int TotalChannels;
    
    /// <summary>Frame size code (frmsizcod).</summary>
    public readonly int FrameSizeCode;
    
    /// <summary>Sample rate code (fscod).</summary>
    public readonly int SampleRateCode;
    
    /// <summary>Is this an E-AC-3 frame?</summary>
    public readonly bool IsEnhancedAc3;
    
    /// <summary>Dialog normalization level in dB.</summary>
    public readonly int DialogNormalization;
    
    public Ac3SyncFrame(
        int frameSize,
        int sampleRate,
        int bitrate,
        int bitstreamId,
        Ac3BitstreamMode bitstreamMode,
        Ac3AudioCodingMode audioCodingMode,
        bool lfeOn,
        int frameSizeCode,
        int sampleRateCode,
        int dialogNormalization)
    {
        FrameSize = frameSize;
        SampleRate = sampleRate;
        Bitrate = bitrate;
        BitstreamId = bitstreamId;
        BitstreamMode = bitstreamMode;
        AudioCodingMode = audioCodingMode;
        LfeOn = lfeOn;
        FrameSizeCode = frameSizeCode;
        SampleRateCode = sampleRateCode;
        DialogNormalization = dialogNormalization;
        IsEnhancedAc3 = bitstreamId > 10;
        FullBandwidthChannels = GetChannelCount(audioCodingMode);
        TotalChannels = FullBandwidthChannels + (lfeOn ? 1 : 0);
    }
    
    private static int GetChannelCount(Ac3AudioCodingMode mode) => mode switch
    {
        Ac3AudioCodingMode.DualMono => 2,
        Ac3AudioCodingMode.Mono => 1,
        Ac3AudioCodingMode.Stereo => 2,
        Ac3AudioCodingMode.ThreeFront => 3,
        Ac3AudioCodingMode.TwoFrontOneSurround => 3,
        Ac3AudioCodingMode.ThreeFrontOneSurround => 4,
        Ac3AudioCodingMode.TwoFrontTwoSurround => 4,
        Ac3AudioCodingMode.ThreeFrontTwoSurround => 5,
        _ => 2
    };
}

/// <summary>
/// Audio coding mode (acmod) - channel configuration.
/// </summary>
public enum Ac3AudioCodingMode
{
    /// <summary>1+1: Two mono channels.</summary>
    DualMono = 0,
    /// <summary>1/0: Center only.</summary>
    Mono = 1,
    /// <summary>2/0: Left and Right (stereo).</summary>
    Stereo = 2,
    /// <summary>3/0: Left, Center, Right.</summary>
    ThreeFront = 3,
    /// <summary>2/1: Left, Right, Surround.</summary>
    TwoFrontOneSurround = 4,
    /// <summary>3/1: Left, Center, Right, Surround.</summary>
    ThreeFrontOneSurround = 5,
    /// <summary>2/2: Left, Right, Left Surround, Right Surround.</summary>
    TwoFrontTwoSurround = 6,
    /// <summary>3/2: Left, Center, Right, Left Surround, Right Surround (5.0).</summary>
    ThreeFrontTwoSurround = 7
}

/// <summary>
/// Bitstream mode (bsmod) - content type.
/// </summary>
public enum Ac3BitstreamMode
{
    /// <summary>Complete Main (CM).</summary>
    CompleteMain = 0,
    /// <summary>Music and Effects (ME).</summary>
    MusicAndEffects = 1,
    /// <summary>Visually Impaired (VI).</summary>
    VisuallyImpaired = 2,
    /// <summary>Hearing Impaired (HI).</summary>
    HearingImpaired = 3,
    /// <summary>Dialogue (D).</summary>
    Dialogue = 4,
    /// <summary>Commentary (C).</summary>
    Commentary = 5,
    /// <summary>Emergency (E).</summary>
    Emergency = 6,
    /// <summary>Voice Over (VO) or Karaoke.</summary>
    VoiceOver = 7
}

/// <summary>
/// AC-3 audio block information.
/// </summary>
public readonly struct Ac3AudioBlock
{
    /// <summary>Block number (0-5).</summary>
    public readonly int BlockNumber;
    
    /// <summary>Block switch mode per channel (1 = short blocks, 0 = long blocks).</summary>
    public readonly int BlockSwitchFlags;
    
    /// <summary>Dither flag per channel.</summary>
    public readonly int DitherFlags;
    
    /// <summary>Dynamic range word exists.</summary>
    public readonly bool HasDynamicRange;
    
    /// <summary>Dynamic range word.</summary>
    public readonly byte DynamicRange;
    
    /// <summary>Coupling strategy exists.</summary>
    public readonly bool CouplingInUse;
    
    /// <summary>Rematrixing enabled.</summary>
    public readonly bool RematrixingInUse;
    
    public Ac3AudioBlock(
        int blockNumber,
        int blockSwitchFlags,
        int ditherFlags,
        bool hasDynamicRange,
        byte dynamicRange,
        bool couplingInUse,
        bool rematrixingInUse)
    {
        BlockNumber = blockNumber;
        BlockSwitchFlags = blockSwitchFlags;
        DitherFlags = ditherFlags;
        HasDynamicRange = hasDynamicRange;
        DynamicRange = dynamicRange;
        CouplingInUse = couplingInUse;
        RematrixingInUse = rematrixingInUse;
    }
}

/// <summary>
/// Exponent strategy for a channel.
/// </summary>
public enum Ac3ExponentStrategy
{
    /// <summary>Reuse previous exponents.</summary>
    Reuse = 0,
    /// <summary>D15 - every coefficient has exponent.</summary>
    D15 = 1,
    /// <summary>D25 - exponent for every 2 coefficients.</summary>
    D25 = 2,
    /// <summary>D45 - exponent for every 4 coefficients.</summary>
    D45 = 3
}

/// <summary>
/// Channel exponent information.
/// </summary>
public struct Ac3ChannelExponents
{
    /// <summary>Exponent strategy.</summary>
    public Ac3ExponentStrategy Strategy;
    
    /// <summary>Number of exponents.</summary>
    public int ExponentCount;
    
    /// <summary>Absolute exponent (first exponent value).</summary>
    public int AbsoluteExponent;
    
    /// <summary>Decoded exponents (256 max).</summary>
    public readonly int[] Exponents;
    
    /// <summary>Encoded grouped exponents.</summary>
    public readonly byte[] GroupedExponents;
    
    public Ac3ChannelExponents(int maxCoefficients)
    {
        Strategy = Ac3ExponentStrategy.D15;
        ExponentCount = 0;
        AbsoluteExponent = 0;
        Exponents = new int[maxCoefficients];
        GroupedExponents = new byte[maxCoefficients / 3 + 1];
    }
}

/// <summary>
/// Bit allocation parameters for a frame.
/// </summary>
public struct Ac3BitAllocation
{
    /// <summary>Slow decay code (sdcycod).</summary>
    public int SlowDecayCode;
    
    /// <summary>Fast decay code (fdcycod).</summary>
    public int FastDecayCode;
    
    /// <summary>Slow gain code (sgaincod).</summary>
    public int SlowGainCode;
    
    /// <summary>dB per bit code (dbpbcod).</summary>
    public int DbPerBitCode;
    
    /// <summary>Floor code (floorcod).</summary>
    public int FloorCode;
    
    /// <summary>Fine SNR offset for all channels.</summary>
    public int FineSnrOffset;

    /// <summary>Coarse SNR offset.</summary>
    public int CoarseSnrOffset;

    /// <summary>
    /// Per-channel SNR offset, already combined from the coarse and fine values
    /// (FFmpeg ac3dec.c: <c>snr = (csnr + get_bits(4)) &lt;&lt; 2</c>).
    /// </summary>
    /// <remarks>
    /// This used to be the single <see cref="FineSnrOffset"/> scalar above, overwritten once per
    /// channel in the parse loop, so every channel ended up using the last channel's offset.
    /// </remarks>
    public readonly int[] SnrOffsets;

    /// <summary>Per-channel end frequency (exclusive), derived from the channel bandwidth code.</summary>
    public readonly int[] EndFreq;
    
    /// <summary>Bit allocation pointer table (per coefficient).</summary>
    public readonly int[] BitAllocationPointers;
    
    /// <summary>Fast gain per channel.</summary>
    public readonly int[] FastGains;
    
    public Ac3BitAllocation(int maxChannels, int maxCoefficients)
    {
        SlowDecayCode = 0;
        FastDecayCode = 0;
        SlowGainCode = 0;
        DbPerBitCode = 0;
        FloorCode = 0;
        FineSnrOffset = 0;
        CoarseSnrOffset = 0;
        BitAllocationPointers = new int[maxCoefficients * maxChannels];
        FastGains = new int[maxChannels];
        SnrOffsets = new int[maxChannels];
        EndFreq = new int[maxChannels];
    }
}

/// <summary>
/// Mantissa values for a channel.
/// </summary>
public struct Ac3ChannelMantissas
{
    /// <summary>Decoded mantissa values.</summary>
    public readonly float[] Mantissas;
    
    /// <summary>Number of mantissas.</summary>
    public int MantissaCount;
    
    public Ac3ChannelMantissas(int maxCoefficients)
    {
        Mantissas = new float[maxCoefficients];
        MantissaCount = 0;
    }
}

/// <summary>
/// Parser for AC-3 frame headers.
/// </summary>
public static class Ac3FrameParser
{
    /// <summary>Maximum number of channels (5.1).</summary>
    public const int MaxChannels = 6;
    
    /// <summary>Number of audio blocks per frame.</summary>
    public const int BlocksPerFrame = 6;
    
    /// <summary>Samples per block.</summary>
    public const int SamplesPerBlock = 256;
    
    /// <summary>Total samples per frame.</summary>
    public const int SamplesPerFrame = BlocksPerFrame * SamplesPerBlock;
    
    /// <summary>Maximum mantissa coefficients per channel.</summary>
    public const int MaxCoefficients = 256;
    
    /// <summary>
    /// Sample rate table indexed by fscod.
    /// </summary>
    private static readonly int[] SampleRateTable = [48000, 44100, 32000];
    
    /// <summary>
    /// Bitrate table indexed by frmsizecod / 2.
    /// </summary>
    private static readonly int[] BitrateTable =
    [
        32, 40, 48, 56, 64, 80, 96, 112, 128, 160,
        192, 224, 256, 320, 384, 448, 512, 576, 640
    ];
    
    /// <summary>
    /// Frame size table indexed by [frmsizecod, fscod] in 16-bit words.
    /// </summary>
    private static readonly int[,] FrameSizeTable = new int[38, 3]
    {
        { 64, 69, 96 },
        { 64, 70, 96 },
        { 80, 87, 120 },
        { 80, 88, 120 },
        { 96, 104, 144 },
        { 96, 105, 144 },
        { 112, 121, 168 },
        { 112, 122, 168 },
        { 128, 139, 192 },
        { 128, 140, 192 },
        { 160, 174, 240 },
        { 160, 175, 240 },
        { 192, 208, 288 },
        { 192, 209, 288 },
        { 224, 243, 336 },
        { 224, 244, 336 },
        { 256, 278, 384 },
        { 256, 279, 384 },
        { 320, 348, 480 },
        { 320, 349, 480 },
        { 384, 417, 576 },
        { 384, 418, 576 },
        { 448, 487, 672 },
        { 448, 488, 672 },
        { 512, 557, 768 },
        { 512, 558, 768 },
        { 640, 696, 960 },
        { 640, 697, 960 },
        { 768, 835, 1152 },
        { 768, 836, 1152 },
        { 896, 975, 1344 },
        { 896, 976, 1344 },
        { 1024, 1114, 1536 },
        { 1024, 1115, 1536 },
        { 1152, 1253, 1728 },
        { 1152, 1254, 1728 },
        { 1280, 1393, 1920 },
        { 1280, 1394, 1920 }
    };
    
    /// <summary>
    /// Tries to parse an AC-3 sync frame header.
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out Ac3SyncFrame frame)
    {
        frame = default;
        
        if (data.Length < 7)
            return false;
        
        // Check sync word (0x0B77)
        if (data[0] != 0x0B || data[1] != 0x77)
            return false;
        
        // CRC1 (2 bytes) - skip for now
        // byte 2, 3 = crc1
        
        // Byte 4: sample rate code (2 bits) + frame size code (6 bits)
        int fscod = (data[4] >> 6) & 0x03;
        int frmsizecod = data[4] & 0x3F;
        
        if (fscod == 3 || frmsizecod > 37)
            return false;
        
        int sampleRate = SampleRateTable[fscod];
        int bitrate = BitrateTable[frmsizecod / 2] * 1000;
        int frameSize = FrameSizeTable[frmsizecod, fscod] * 2; // Convert words to bytes
        
        // Byte 5: bsid (5 bits) + bsmod (3 bits)
        int bsid = (data[5] >> 3) & 0x1F;
        int bsmod = data[5] & 0x07;
        
        // Check bsid for AC-3 vs E-AC-3
        if (bsid > 16)
            return false;
        
        // Byte 6: acmod (3 bits) + lfeon (1 bit) + ...
        int acmod = (data[6] >> 5) & 0x07;
        
        // Bit position within byte 6, measured from its most significant bit. acmod occupies the
        // first 3 bits, so the optional mix-level fields start at 3 — not 11. The extra byte made
        // lfeon read from the wrong position entirely.
        int bitOffset = 3;
        
        // cmixlev (2 bits) if 3 front channels
        if ((acmod & 0x01) != 0 && acmod != 1)
            bitOffset += 2;
        
        // surmixlev (2 bits) if surround channel exists
        if ((acmod & 0x04) != 0)
            bitOffset += 2;
        
        // dsurmod (2 bits) if stereo
        if (acmod == 2)
            bitOffset += 2;
        
        // lfeon (1 bit)
        int byteIndex = 6 + (bitOffset / 8);
        int bitIndex = 7 - (bitOffset % 8);
        bool lfeon = false;
        
        if (byteIndex < data.Length)
            lfeon = ((data[byteIndex] >> bitIndex) & 0x01) != 0;
        
        // dialnorm (5 bits) - skip calculation of exact bit position for now
        int dialnorm = -31; // Default value
        
        frame = new Ac3SyncFrame(
            frameSize,
            sampleRate,
            bitrate,
            bsid,
            (Ac3BitstreamMode)bsmod,
            (Ac3AudioCodingMode)acmod,
            lfeon,
            frmsizecod,
            fscod,
            dialnorm);
        
        return true;
    }
    
    /// <summary>
    /// Finds the next sync frame in the data.
    /// </summary>
    public static int FindSyncWord(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length - 1; i++)
        {
            if (data[i] == 0x0B && data[i + 1] == 0x77)
                return i;
        }
        return -1;
    }
    
    /// <summary>
    /// Calculates the expected frame size from header bytes.
    /// </summary>
    public static int GetFrameSize(byte byte4)
    {
        int fscod = (byte4 >> 6) & 0x03;
        int frmsizecod = byte4 & 0x3F;
        
        if (fscod == 3 || frmsizecod > 37)
            return -1;
        
        return FrameSizeTable[frmsizecod, fscod] * 2;
    }
}

/// <summary>
/// FFmpeg's lagged Fibonacci generator (libavutil/lfg.c), replicated bit-for-bit.
/// </summary>
/// <remarks>
/// AC-3 fills every zero-bit-allocation bin with pseudo-random dither, and roughly half the
/// bins in a typical frame have bap == 0. The reference decoder's dither is fully deterministic
/// — seeded once with av_lfg_init(state, 0) and advanced across the whole stream — so matching
/// its output bit-for-bit requires reproducing this generator and calling it exactly as often,
/// and in the same order, as FFmpeg does. The previous implementation used Random.Shared scaled
/// to 0.001, which was both non-reproducible between runs and about 700x too quiet.
/// </remarks>
public sealed class Ac3Lfg
{
    private readonly uint[] state = new uint[64];
    private uint index;

    public Ac3Lfg(uint seed = 0) => Init(seed);

    /// <summary>
    /// Port of av_lfg_init. Note that entries 0..7 are deliberately left zero — FFmpeg's loop
    /// starts at 8 and the surrounding context is zero-allocated.
    /// </summary>
    public void Init(uint seed)
    {
        Array.Clear(state);

        // tmp is zeroed once, outside the loop, and then hashed in place. Each iteration only
        // rewrites bytes 0..4, so bytes 5..15 carry the previous digest forward — the state blocks
        // are chained, not independent. FFmpeg relies on this; clearing tmp per iteration produces
        // a different stream and the dither noise then diverges from the reference decoder.
        Span<byte> tmp = stackalloc byte[16];
        tmp.Clear();

        for (int i = 8; i < 64; i += 4)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tmp, seed);
            tmp[4] = (byte)i;
            System.Security.Cryptography.MD5.HashData(tmp, tmp);

            state[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tmp);
            state[i + 1] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tmp[4..]);
            state[i + 2] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tmp[8..]);
            state[i + 3] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tmp[12..]);
        }
        index = 0;
    }

    /// <summary>
    /// Port of av_lfg_init_from_data: runs a CRC-32 across 64 segments of the supplied buffer and
    /// stores each running value as one state slot. FFmpeg uses this when its
    /// <c>cons_noisegen</c> option is set, which reseeds the dither generator from every frame's
    /// own bytes so decoding is reproducible without cross-frame state.
    /// </summary>
    public void InitFromData(ReadOnlySpan<byte> data)
    {
        index = 0;
        uint crc = 1;
        int beg = 0;
        for (int segment = 0; segment < 64; segment++)
        {
            int end = (int)(((long)(segment + 1) * data.Length) / 64);
            crc = Crc32Ieee(crc, data[beg..end]);
            state[segment] = crc;
            beg = end;
        }
    }

    /// <summary>
    /// FFmpeg's AV_CRC_32_IEEE. The table is built big-endian from polynomial 0x04C11DB7 and then
    /// byte-swapped (crc.c av_crc_init with le = 0), and consumed with a reflected-style update.
    /// Ported verbatim rather than assumed equal to the usual zlib CRC-32.
    /// </summary>
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int j = 0; j < 8; j++)
                c = (c << 1) ^ (0x04C11DB7u & (uint)((int)c >> 31));
            table[i] = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(c);
        }
        return table;
    }

    private static uint Crc32Ieee(uint crc, ReadOnlySpan<byte> buffer)
    {
        foreach (byte b in buffer)
            crc = Crc32Table[(byte)crc ^ b] ^ (crc >> 8);
        return crc;
    }

    /// <summary>Port of av_lfg_get: state[i] = state[i-24] + state[i-55], with 32-bit wraparound.</summary>
    public uint Next()
    {
        uint a = state[index & 63] = state[(index - 24) & 63] + state[(index - 55) & 63];
        index += 1u;
        return a;
    }

    /// <summary>
    /// One AC-3 dither mantissa as normalised float, matching FFmpeg's
    /// <c>(((av_lfg_get() &gt;&gt; 8) * 181) &gt;&gt; 8) - 5931008</c> over a 24-bit full scale.
    /// </summary>
    public int NextDitherMantissa()
        => (int)(((Next() >> 8) * 181u) >> 8) - 5931008;
}

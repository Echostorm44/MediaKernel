// AAC Audio Decoder Types and Enumerations
// Based on ISO/IEC 14496-3 (MPEG-4 Audio) and ISO/IEC 13818-7 (MPEG-2 AAC)

using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.AAC;

/// <summary>
/// MPEG-4 Audio Object Types as defined in ISO/IEC 14496-3 Table 1.17.
/// </summary>
public enum AudioObjectType : byte
{
    Null = 0,
    
    /// <summary>AAC Main profile.</summary>
    AacMain = 1,
    
    /// <summary>AAC Low Complexity - the most common AAC profile.</summary>
    AacLowComplexity = 2,
    
    /// <summary>AAC Scalable Sample Rate.</summary>
    AacScalableSampleRate = 3,
    
    /// <summary>AAC Long Term Prediction.</summary>
    AacLongTermPrediction = 4,
    
    /// <summary>Spectral Band Replication (used with AAC-HE v1).</summary>
    SpectralBandReplication = 5,
    
    /// <summary>AAC Scalable.</summary>
    AacScalable = 6,
    
    /// <summary>TwinVQ.</summary>
    TwinVq = 7,
    
    /// <summary>Code Excited Linear Prediction.</summary>
    Celp = 8,
    
    /// <summary>Harmonic Vector eXcitation Coding.</summary>
    Hvxc = 9,
    
    // Reserved 10-11
    
    /// <summary>Text-To-Speech Interface.</summary>
    TextToSpeechInterface = 12,
    
    /// <summary>Main Synthesis.</summary>
    MainSynthesis = 13,
    
    /// <summary>Wavetable Synthesis.</summary>
    WavetableSynthesis = 14,
    
    /// <summary>General MIDI.</summary>
    GeneralMidi = 15,
    
    /// <summary>Algorithmic Synthesis and Audio FX.</summary>
    AlgorithmicSynthesis = 16,
    
    /// <summary>Error Resilient AAC-LC.</summary>
    ErrorResilientAacLc = 17,
    
    // Reserved 18
    
    /// <summary>Error Resilient AAC-LTP.</summary>
    ErrorResilientAacLtp = 19,
    
    /// <summary>Error Resilient AAC Scalable.</summary>
    ErrorResilientAacScalable = 20,
    
    /// <summary>Error Resilient TwinVQ.</summary>
    ErrorResilientTwinVq = 21,
    
    /// <summary>Error Resilient BSAC.</summary>
    ErrorResilientBsac = 22,
    
    /// <summary>Error Resilient AAC-LD (Low Delay).</summary>
    ErrorResilientAacLd = 23,
    
    /// <summary>Error Resilient CELP.</summary>
    ErrorResilientCelp = 24,
    
    /// <summary>Error Resilient HVXC.</summary>
    ErrorResilientHvxc = 25,
    
    /// <summary>Error Resilient HILN.</summary>
    ErrorResilientHiln = 26,
    
    /// <summary>Error Resilient Parametric.</summary>
    ErrorResilientParametric = 27,
    
    /// <summary>Synthetic Audio - General.</summary>
    SyntheticAudioGeneral = 28,
    
    /// <summary>Parametric Stereo (used with AAC-HE v2).</summary>
    ParametricStereo = 29,
    
    /// <summary>MPEG Surround.</summary>
    MpegSurround = 30,
    
    // Escape value = 31
    
    /// <summary>Layer-1.</summary>
    Layer1 = 32,
    
    /// <summary>Layer-2.</summary>
    Layer2 = 33,
    
    /// <summary>Layer-3 (MP3).</summary>
    Layer3 = 34,
    
    /// <summary>DST (Direct Stream Transfer).</summary>
    Dst = 35,
    
    /// <summary>ALS (Audio Lossless Coding).</summary>
    Als = 36,
    
    /// <summary>SLS (Scalable Lossless Coding).</summary>
    Sls = 37,
    
    /// <summary>SLS Non-core.</summary>
    SlsNonCore = 38,
    
    /// <summary>Error Resilient AAC-ELD (Enhanced Low Delay).</summary>
    ErrorResilientAacEld = 39,
    
    /// <summary>SMR Simple.</summary>
    SmrSimple = 40,
    
    /// <summary>SMR Main.</summary>
    SmrMain = 41,
    
    /// <summary>USAC (Unified Speech and Audio Coding).</summary>
    Usac = 42,
    
    /// <summary>SAOC (Spatial Audio Object Coding).</summary>
    Saoc = 43,
    
    /// <summary>LD MPEG Surround.</summary>
    LdMpegSurround = 44,
    
    /// <summary>USAC No SBR.</summary>
    UsacNoSbr = 45,
}

/// <summary>
/// AAC window sequence types for IMDCT.
/// </summary>
public enum WindowSequence : byte
{
    /// <summary>Only long windows (1024 samples).</summary>
    OnlyLongSequence = 0,
    
    /// <summary>Transition from long to short windows.</summary>
    LongStartSequence = 1,
    
    /// <summary>8 short windows (128 samples each).</summary>
    EightShortSequence = 2,
    
    /// <summary>Transition from short to long windows.</summary>
    LongStopSequence = 3,
}

/// <summary>
/// AAC channel configuration as defined in ISO/IEC 14496-3 Table 1.19.
/// </summary>
public enum ChannelConfiguration : byte
{
    /// <summary>Channel configuration defined in AOT specific config.</summary>
    AotSpecificConfig = 0,
    
    /// <summary>1 channel: Center.</summary>
    Mono = 1,
    
    /// <summary>2 channels: Left, Right.</summary>
    Stereo = 2,
    
    /// <summary>3 channels: Center, Left, Right.</summary>
    ThreeChannels = 3,
    
    /// <summary>4 channels: Center, Left, Right, Rear Center.</summary>
    FourChannels = 4,
    
    /// <summary>5 channels: Center, Left, Right, Rear Left, Rear Right.</summary>
    FiveChannels = 5,
    
    /// <summary>5.1 channels: Center, Left, Right, Rear Left, Rear Right, LFE.</summary>
    FivePointOne = 6,
    
    /// <summary>7.1 channels: Center, Left, Right, Side Left, Side Right, Rear Left, Rear Right, LFE.</summary>
    SevenPointOne = 7,
}

/// <summary>
/// Sample rate index to frequency mapping.
/// </summary>
public static class AacSampleRates
{
    /// <summary>
    /// Standard AAC sample rate table (index 0-12, 13-15 are reserved/escape).
    /// </summary>
    public static ReadOnlySpan<int> Table =>
    [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025,  8000,  7350,     0,     0,     0
    ];
    
    /// <summary>
    /// Gets the sample rate for a given index.
    /// </summary>
    public static int FromIndex(int index)
    {
        if (index < 0 || index >= 16)
            return 0;
        return Table[index];
    }
    
    /// <summary>
    /// Gets the index for a given sample rate.
    /// </summary>
    public static int ToIndex(int sampleRate)
    {
        return sampleRate switch
        {
            96000 => 0,
            88200 => 1,
            64000 => 2,
            48000 => 3,
            44100 => 4,
            32000 => 5,
            24000 => 6,
            22050 => 7,
            16000 => 8,
            12000 => 9,
            11025 => 10,
            8000 => 11,
            7350 => 12,
            _ => 15  // Escape value
        };
    }
}

/// <summary>
/// AAC Audio Specific Config (parsed from ESDS or ADTS header).
/// </summary>
public class AudioSpecificConfig
{
    /// <summary>Audio object type (profile).</summary>
    public AudioObjectType ObjectType { get; set; }
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; set; }
    
    /// <summary>Sample rate index (0-15).</summary>
    public int SampleRateIndex { get; set; }
    
    /// <summary>Channel configuration.</summary>
    public ChannelConfiguration ChannelConfiguration { get; set; }
    
    /// <summary>Number of audio channels. Returns 0 if channel config is AOT-specific (must be determined from bitstream).</summary>
    public int ChannelCount => ChannelConfiguration switch
    {
        ChannelConfiguration.AotSpecificConfig => 0,
        ChannelConfiguration.Mono => 1,
        ChannelConfiguration.Stereo => 2,
        ChannelConfiguration.ThreeChannels => 3,
        ChannelConfiguration.FourChannels => 4,
        ChannelConfiguration.FiveChannels => 5,
        ChannelConfiguration.FivePointOne => 6,
        ChannelConfiguration.SevenPointOne => 8,
        _ => 0  // Unknown configuration
    };
    
    /// <summary>Frame length (1024 for standard, 960 for low-delay).</summary>
    public int FrameLength { get; set; } = 1024;
    
    /// <summary>Depends on core coder flag.</summary>
    public bool DependsOnCoreCoder { get; set; }
    
    /// <summary>Extension audio object type (for SBR/PS).</summary>
    public AudioObjectType ExtensionObjectType { get; set; }
    
    /// <summary>Extension sample rate (for SBR, typically 2x base rate).</summary>
    public int ExtensionSampleRate { get; set; }

    /// <summary>
    /// The AudioSpecificConfig this was parsed from, kept whole for the object types whose
    /// specific configuration is read by the decoder rather than the parser.
    /// </summary>
    /// <remarks>
    /// USAC is the one that needs it. Its UsacConfig is substantial - a rate index of its own, a
    /// channel configuration, a list of elements each with their own configuration, and a chain
    /// of configuration extensions - and it belongs with the rest of the USAC port rather than
    /// in the ASC parser, exactly as FFmpeg puts it in aacdec_usac.c and not in mpeg4audio.c.
    /// So the parser stops after the thirteen-bit prefix and records where it stopped.
    /// </remarks>
    public byte[]? RawConfig { get; set; }

    /// <summary>The bit offset in <see cref="RawConfig"/> where the specific configuration starts.</summary>
    public int SpecificConfigBitOffset { get; set; }
    
    /// <summary>Extension channel configuration.</summary>
    public ChannelConfiguration ExtensionChannelConfiguration { get; set; }
    
    /// <summary>Whether SBR (Spectral Band Replication) is present.</summary>
    public bool HasSbr { get; set; }
    
    /// <summary>Whether PS (Parametric Stereo) is present.</summary>
    public bool HasParametricStereo { get; set; }

    /// <summary>
    /// <c>m4ac.sbr</c>: -1 not signalled either way, 0 signalled absent, 1 present. The
    /// three-way distinction matters because a stream may carry SBR without announcing it, and
    /// "not announced" has to stay different from "announced absent" - the second makes an SBR
    /// payload an error.
    /// </summary>
    public int SbrMode { get; set; } = -1;

    /// <summary>
    /// <c>m4ac.ps</c>: -1 not signalled, 0 absent, 1 present. A mono stream left at -1 that then
    /// carries SBR is treated as HE-AACv2 and decoded to two channels, which is what FFmpeg
    /// does and what the reference decoder's output for those streams assumes.
    /// </summary>
    public int PsMode { get; set; } = -1;
    
    /// <summary>
    /// Gets the effective sample rate (considering SBR).
    /// </summary>
    public int EffectiveSampleRate => HasSbr && ExtensionSampleRate > 0 
        ? ExtensionSampleRate 
        : SampleRate;
    
    /// <summary>
    /// Gets the effective channel count (considering PS).
    /// </summary>
    public int EffectiveChannelCount => HasParametricStereo && ChannelCount == 1 
        ? 2 
        : ChannelCount;

    /// <summary>
    /// Program Config Element parsed from GASpecificConfig (for channelConfig=0).
    /// Null if channelConfiguration is not 0.
    /// </summary>
    public ProgramConfigElement? Pce { get; set; }

    /// <summary>
    /// Gets the total channel count including PCE-derived channels.
    /// </summary>
    public int TotalChannelCount
    {
        get
        {
            if (ChannelConfiguration != ChannelConfiguration.AotSpecificConfig || Pce == null)
                return ChannelCount;
            return Pce.TotalChannels;
        }
    }
}

/// <summary>
/// Element descriptor from a Program Config Element: type (SCE/CPE/LFE) and instance tag.
/// </summary>
public readonly struct PceElementDescriptor
{
    public readonly SyntaxElementType Type;
    public readonly int InstanceTag;

    public PceElementDescriptor(SyntaxElementType type, int instanceTag)
    {
        Type = type;
        InstanceTag = instanceTag;
    }
}

/// <summary>
/// Program Config Element parsed from GASpecificConfig or in-band PCE.
/// Defines the channel layout for channelConfig=0 streams.
/// </summary>
public class ProgramConfigElement
{
    public int NumFront { get; init; }
    public int NumSide { get; init; }
    public int NumBack { get; init; }
    public int NumLfe { get; init; }
    public int NumAssocData { get; init; }
    public int NumCc { get; init; }

    /// <summary>
    /// Ordered list of audio elements (front, side, back, LFE) defining the channel layout.
    /// Does NOT include CC elements.
    /// </summary>
    public PceElementDescriptor[] Elements { get; init; } = Array.Empty<PceElementDescriptor>();

    /// <summary>
    /// Total number of audio output channels.
    /// </summary>
    public int TotalChannels
    {
        get
        {
            int count = 0;
            foreach (var elem in Elements)
            {
                count += elem.Type == SyntaxElementType.ChannelPairElement ? 2 : 1;
            }
            return count;
        }
    }

    /// <summary>
    /// Number of audio elements (SCE/CPE/LFE) expected per frame.
    /// </summary>
    public int AudioElementCount => Elements.Length;
}

/// <summary>
/// ADTS (Audio Data Transport Stream) frame header.
/// </summary>
public struct AdtsHeader
{
    /// <summary>MPEG version: false = MPEG-4, true = MPEG-2.</summary>
    public bool IsMpeg2 { get; set; }
    
    /// <summary>Audio object type (profile + 1 for ADTS).</summary>
    public AudioObjectType Profile { get; set; }
    
    /// <summary>Sample rate index (0-15).</summary>
    public int SampleRateIndex { get; set; }
    
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate => AacSampleRates.FromIndex(SampleRateIndex);
    
    /// <summary>Channel configuration.</summary>
    public ChannelConfiguration ChannelConfiguration { get; set; }
    
    /// <summary>Frame length including header and CRC.</summary>
    public int FrameLength { get; set; }
    
    /// <summary>Buffer fullness.</summary>
    public int BufferFullness { get; set; }
    
    /// <summary>Number of raw data blocks in frame (0 = 1 block).</summary>
    public int RawDataBlocksInFrame { get; set; }
    
    /// <summary>Whether CRC is present.</summary>
    public bool HasCrc { get; set; }
    
    /// <summary>Header size in bytes (7 without CRC, 9 with CRC).</summary>
    public int HeaderSize => HasCrc ? 9 : 7;
    
    /// <summary>Payload size in bytes.</summary>
    public int PayloadSize => FrameLength - HeaderSize;
    
    /// <summary>Whether this is a valid header.</summary>
    public bool IsValid => FrameLength >= HeaderSize && SampleRate > 0;
}

/// <summary>
/// Individual Channel Stream (ICS) info for AAC decoding.
/// </summary>
public struct IcsInfo
{
    /// <summary>Default constructor.</summary>
    public IcsInfo()
    {
        SwbOffset = Array.Empty<int>();
    }

    /// <summary>Window sequence type.</summary>
    public WindowSequence WindowSequence { get; set; }
    
    /// <summary>Window shape (0 = sine, 1 = KBD).</summary>
    public int WindowShape { get; set; }
    
    /// <summary>Previous window shape for overlap.</summary>
    public int PreviousWindowShape { get; set; }
    
    /// <summary>Maximum scalefactor band.</summary>
    public int MaxScalefactorBand { get; set; }
    
    /// <summary>Maximum scalefactor band (alias for MaxScalefactorBand).</summary>
    public int MaxSfb { get => MaxScalefactorBand; set => MaxScalefactorBand = value; }
    
    /// <summary>Scale factor grouping (for short blocks).</summary>
    public int ScalefactorGrouping { get; set; }
    
    /// <summary>Predictor data present flag.</summary>
    public bool PredictorDataPresent { get; set; }
    
    /// <summary>AAC-Main predictor reset group number (1-30, or 0 = no reset).</summary>
    public int PredictorResetGroup { get; set; }
    
    /// <summary>Per-SFB prediction enabled flags (AAC-Main profile, max 41 SFBs).</summary>
    public PredictionUsedFlags PredictionUsed;
    
    /// <summary>Whether predictor_present flag was set in the ICS info.</summary>
    public bool PredictorPresent { get; set; }

    /// <summary>Long term prediction, for the AAC-LTP object type.</summary>
    public AacLtp Ltp;
    
    /// <summary>Number of window groups.</summary>
    public int NumWindowGroups { get; set; }
    
    /// <summary>Number of windows in each group.</summary>
    /// <remarks>
    /// A fixed buffer and not a <c>byte[]</c>, for the same reason
    /// <see cref="PredictionUsedFlags"/> is one: an <see cref="IcsInfo"/> is built fresh for every
    /// channel of every frame, so an array field here is an allocation per channel per frame. It
    /// measured 32 bytes a channel - 64 on a stereo stream, 160 on 5.1 - which is what put AAC
    /// into DecoderAllocationTests. It is also the closer translation: the C's field is a
    /// <c>uint8_t group_len[8]</c> inside the struct, so copying an IcsInfo copies the lengths
    /// rather than aliasing them, which is what a struct copy now does here too.
    /// </remarks>
    public WindowGroupLengths WindowGroupLength;
    
    /// <summary>Number of scalefactor bands.</summary>
    public int NumSwb { get; set; }
    
    /// <summary>Scalefactor band offsets.</summary>
    public int[] SwbOffset { get; set; }
}

/// <summary>
/// <c>LongTermPrediction</c>: the lag, gain and per-band flags of AAC-LTP's predictor.
/// </summary>
/// <remarks>
/// <c>used</c> is <c>MAX_LTP_LONG_SFB</c> = 40 entries, and is a fixed buffer for the same reason
/// <see cref="PredictionUsedFlags"/> is: <see cref="IcsInfo"/> is a struct and must not drag a
/// heap allocation per frame behind it.
/// </remarks>
public unsafe struct AacLtp
{
    /// <summary>The number of scalefactor bands LTP can cover, <c>MAX_LTP_LONG_SFB</c>.</summary>
    public const int MaxLongSfb = 40;

    /// <summary><c>ltp.present</c>: this channel's ICS info carried LTP data.</summary>
    public bool Present;

    /// <summary><c>lag</c>: how far back in the reconstructed signal the predictor reaches.</summary>
    public int Lag;

    /// <summary><c>coef</c>: the gain, one of the eight values of <c>ff_ltp_coef</c>.</summary>
    public float Coef;

    private fixed byte used[MaxLongSfb];

    /// <summary><c>used[sfb]</c>: whether this band takes the prediction.</summary>
    public bool this[int index]
    {
        get => used[index] != 0;
        set => used[index] = value ? (byte)1 : (byte)0;
    }

    public void Clear()
    {
        Present = false;
        Lag = 0;
        Coef = 0;
        for (int i = 0; i < MaxLongSfb; i++)
            used[i] = 0;
    }
}

/// <summary>
/// Fixed-size buffer for per-SFB prediction flags (max 41 for AAC-Main).
/// Avoids heap allocation in the IcsInfo struct.
/// </summary>
/// <summary>
/// FFmpeg's <c>group_len[8]</c>: how many windows are in each window group.
/// </summary>
public unsafe struct WindowGroupLengths
{
    /// <summary>The eight groups a short-block sequence can be split into.</summary>
    public const int MaxGroups = 8;

    private fixed byte lengths[MaxGroups];

    public byte this[int index]
    {
        get => lengths[index];
        set => lengths[index] = value;
    }

    public void Clear()
    {
        for (int i = 0; i < MaxGroups; i++)
            lengths[i] = 0;
    }
}

public unsafe struct PredictionUsedFlags
{
    private fixed byte flags[41];
    
    public bool this[int index]
    {
        get => flags[index] != 0;
        set => flags[index] = value ? (byte)1 : (byte)0;
    }
    
    public void Clear()
    {
        for (int i = 0; i < 41; i++)
            flags[i] = 0;
    }
}

/// <summary>
/// Section data for AAC Huffman decoding.
/// </summary>
public struct SectionData
{
    /// <summary>Codebook index for this section.</summary>
    public int Codebook { get; set; }
    
    /// <summary>Start scalefactor band.</summary>
    public int Start { get; set; }
    
    /// <summary>End scalefactor band.</summary>
    public int End { get; set; }
}

// TnsData is now defined in AacTnsDecoder.cs with zero-allocation inline arrays

/// <summary>
/// Pulse data for AAC decoding.
/// </summary>
public struct PulseData
{
    /// <summary>The four pulses a frame may carry.</summary>
    public const int MaxPulses = 4;

    /// <summary>Whether pulse data is present.</summary>
    public bool Present { get; set; }
    
    /// <summary>Number of pulses.</summary>
    public int NumPulse { get; set; }
    
    /// <summary>Pulse start scalefactor band.</summary>
    public int PulseStartSfb { get; set; }
    
    /// <summary>Pulse offset (relative position within band).</summary>
    /// <remarks>
    /// Inline, as the C's <c>int pos[4]</c> is. These were two <c>int[4]</c> properties, which
    /// meant eighty bytes allocated on every frame that carries pulse data - al04_44 and
    /// al04sf_48 carry it on 2124 frames out of 2130, and nothing measured AAC's allocation
    /// until now.
    /// </remarks>
    public PulseArray PulseOffset;

    /// <summary>Pulse amplitude, the C's <c>int amp[4]</c>.</summary>
    public PulseArray PulseAmp;
}

/// <summary>Four ints: the C's <c>pos[4]</c> and <c>amp[4]</c> inside <see cref="PulseData"/>.</summary>
[InlineArray(PulseData.MaxPulses)]
public struct PulseArray
{
    private int element;
}

/// <summary>
/// Constants for AAC decoding.
/// </summary>
public static class AacConstants
{
    /// <summary>Maximum number of scalefactor bands for long blocks.</summary>
    public const int MaxSfbLong = 51;
    
    /// <summary>Maximum number of scalefactor bands for short blocks.</summary>
    public const int MaxSfbShort = 15;
    
    /// <summary>Maximum spectral coefficients (long block).</summary>
    public const int MaxSpecLong = 1024;
    
    /// <summary>Maximum spectral coefficients (short block).</summary>
    public const int MaxSpecShort = 128;
    
    /// <summary>Number of short windows in a short block sequence.</summary>
    public const int ShortWindowsPerFrame = 8;
    
    /// <summary>Long block size.</summary>
    public const int LongBlockSize = 1024;
    
    /// <summary>Short block size.</summary>
    public const int ShortBlockSize = 128;
    
    /// <summary>Overlap size for long blocks.</summary>
    public const int LongOverlapSize = 512;
    
    /// <summary>Overlap size for short blocks.</summary>
    public const int ShortOverlapSize = 64;
    
    /// <summary>Maximum number of channels.</summary>
    public const int MaxChannels = 8;
    
    /// <summary>Number of scalefactor codebook entries.</summary>
    public const int NumScalefactorHuffmanCodes = 121;
    
    /// <summary>ADTS sync word (0xFFF in upper 12 bits).</summary>
    public const int AdtsSyncWord = 0xFFF;
}

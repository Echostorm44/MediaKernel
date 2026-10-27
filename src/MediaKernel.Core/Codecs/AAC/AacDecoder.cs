using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using MediaKernel.Core.Codecs.AAC.Sbr;

namespace MediaKernel.Core.Codecs.AAC;

/// <summary>
/// Per-element MDCT overlap state. In FFmpeg, each (type, instanceTag) element
/// has its own SingleChannelElement with independent saved[] buffer.
/// </summary>
internal sealed class ElementOverlapState
{
    /// <summary>
    /// <c>saved[1536]</c>: the overlap carried into the next frame.
    /// </summary>
    /// <remarks>
    /// 1,536 and not 1,024 because of ELD, whose overlap runs four frames deep rather than two:
    /// <c>imdct_and_windowing_eld</c> shifts 2n samples up by n and refills the bottom n every
    /// frame, and reads as far as <c>saved[3n - 1]</c>. FFmpeg declares the same 1,536 for every
    /// profile for the same reason, so this is its size and not a margin.
    /// </remarks>
    public readonly float[] PreviousSamples = new float[1536];

    /// <summary>
    /// <c>ltp_state[3072]</c>: the time signal long term prediction reaches back into. The three
    /// thousand samples are the previous frame's windowed output, this frame's, and the overlap
    /// that has not been emitted yet - which is why it is updated after the IMDCT and not before.
    /// </summary>
    public readonly float[] LtpState = new float[3072];
    public WindowSequence PrevWindowSequence;
    public int PrevWindowShape;
    
    /// <summary>
    /// AAC-Main backward-adaptive predictor state. One per spectral coefficient (672 max).
    /// Port of FFmpeg's PredictorState predictor_state[MAX_PREDICTORS].
    /// </summary>
    public readonly AacPredictorState[] PredictorStates = new AacPredictorState[672];
    
    /// <summary>
    /// Whether predictor state has been initialized (first frame resets all).
    /// </summary>
    public bool PredictorInitialized;
    
    /// <summary>
    /// Persistent predictor reset group. In FFmpeg, ics.predictor_reset_group persists
    /// across frames because IndividualChannelStream is reused. We replicate this by
    /// storing the last-set value here. When predictor_reset bit is 0, the old value persists.
    /// </summary>
    public int PredictorResetGroup;
}

/// <summary>
/// State for AAC-Main backward-adaptive predictor (per spectral coefficient).
/// Port of FFmpeg's PredictorState from aac_defines.h.
/// </summary>
public struct AacPredictorState
{
    public float Cor0, Cor1;
    public float Var0, Var1;
    public float R0, R1;
}

/// <summary>
/// AAC-LC (Low Complexity) software decoder.
/// Implements ISO/IEC 14496-3 (MPEG-4 Audio) AAC decoding.
/// </summary>
public sealed class AacDecoder : IDisposable
{
    private readonly AacFilterbank filterbank;
    private readonly float[] spectralCoefficients;
    private readonly float[] spectralCoefficientsRight;  // Second buffer for CPE right channel
    private readonly float[] imdctBuffer;    // half-IMDCT temporary buffer (1024 samples)
    private readonly int[] scalefactors;
    
    // Band type and section data, indexed as [group * maxSfb + sfb]
    // Matches FFmpeg's sce->band_type[] flat 2D indexing.
    private readonly int[] bandType;
    private readonly int[] bandTypeLeft;     // Saved left-channel band types for MS stereo
    private readonly bool[] msMask;          // MS stereo mask [group * maxSfb + sfb]
    
    // Per-element overlap state: [elementType * 16 + instanceTag][channelIndex]
    // Matches FFmpeg's che[type][id]->ch[ch].saved[] architecture.
    // 4 element types × 16 instances × 2 channels = 128 possible slots (lazily allocated).
    private readonly ElementOverlapState?[] elementOverlap = new ElementOverlapState?[4 * 16 * 2];
    
    // Scratch output buffer for non-primary elements (e.g., LFE in a stereo stream)
    private readonly float[] scratchOutput = new float[1024];
    private int[] swbOffsets;
    private int[] swbOffsetsShort;
    private int numSwbLong;
    private int numSwbShort;
    private int tnsMaxBandsLong;
    private int tnsMaxBandsShort;
    
    private AudioSpecificConfig? config;
    private int frameCount;
    private bool isDisposed;
    private int lastFrameChannels;
    
    /// <summary>
    /// Debug flag: when true, skip TNS application. For diagnostic use only.
    /// </summary>
    public bool DebugSkipTns;
    
    /// <summary>
    /// Debug flag: when true, skip MS stereo. For diagnostic use only.
    /// </summary>
    public bool DebugSkipMs;
    
    /// <summary>
    /// Debug flag: when true, skip intensity stereo. For diagnostic use only.
    /// </summary>
    public bool DebugSkipIs;
    
    /// <summary>
    /// Debug: skip prediction output (but keep state update) for CPE with this instanceTag. -1 = disabled.
    /// </summary>
    public int DebugSkipPredForCpeTag = -1;
    
    /// <summary>
    /// Debug: skip independent coupling for CPE with this instanceTag. -1 = disabled.
    /// </summary>
    public int DebugSkipCouplingForCpeTag = -1;
    
    // Cached routing for channelConfig=0 (detected from first multi-channel frame)
    private int[][]? detectedRouting;

    /// <summary>The USAC decoder, non-null only for an xHE-AAC stream.</summary>
    private AacUsacDecoder? usacDecoder;
    
    // CCE (Coupling Channel Element) state — up to 16 CCE instances per MAX_ELEM_ID
    private readonly float[] cceSpectral = new float[1024];
    private readonly float[] cceOutput = new float[1024];
    private readonly CouplingChannelState[] cceStates = new CouplingChannelState[16];
    private int cceCount;  // Number of CCEs decoded in current frame
    
    // Element tracking for CCE target resolution
    private readonly DecodedElementInfo[] decodedElements = new DecodedElementInfo[16];
    private int decodedElementCount;
    
    // Two-pass processing: per-channel deferred spectral data for SpectralToSample.
    // Phase 1 decodes all elements to spectral data. Phase 2 applies coupling → TNS → IMDCT.
    private const int MaxPendingChannels = 8;
    private readonly float[][] pendingSpectral;
    private readonly PendingChannel[] pendingChannels = new PendingChannel[MaxPendingChannels];
    private int pendingChannelCount;
    private bool deferTnsImdct;  // Set by DecodeFrameMultiChannel; element decode methods skip TNS/IMDCT

    /// <summary>
    /// Maximum prediction SFB count per sampling rate index (AAC-Main profile).
    /// From FFmpeg's ff_aac_pred_sfb_max[] in aactab.c.
    /// </summary>
    private static readonly int[] PredSfbMax = { 33, 33, 38, 40, 40, 40, 41, 41, 37, 37, 37, 34, 34 };

    /// <summary>
    /// Current audio configuration.
    /// </summary>
    public AudioSpecificConfig? Configuration => config;

    /// <summary>
    /// Number of frames decoded.
    /// </summary>
    public int FrameCount => frameCount;

    /// <summary>
    /// Sample rate of decoded audio.
    /// </summary>
    /// <summary>
    /// Output sample rate. With SBR running this is twice the AAC core's, because that is what the
    /// decoder now produces; reporting the core rate here is what made an HE-AAC stream play at
    /// half speed.
    /// </summary>
    public int SampleRate =>
        EnableSbr && (sbrSignalled || sbr.Ready) && sbr.OutputSampleRate > 0
            ? sbr.OutputSampleRate
            : (config?.SampleRate ?? 0);

    /// <summary>The AAC core's own rate, before SBR doubles it.</summary>
    public int CoreSampleRate => config?.SampleRate ?? 0;

    /// <summary>
    /// Number of channels from ASC. Returns 0 if channelConfig is AOT-specific (determined from bitstream).
    /// </summary>
    public int ChannelCount => config?.ChannelCount ?? 0;

    /// <summary>
    /// Actual channel count from the last decoded frame (1=SCE/mono, 2=CPE/stereo).
    /// Updated after each successful DecodeFrame call.
    /// </summary>
    public int LastFrameChannels => lastFrameChannels;

    /// <summary>
    /// Samples per frame (1024 for standard AAC, 960 for AAC-LD).
    /// </summary>
    public int SamplesPerFrame =>
        config is null ? 1024
        : IsLowDelayObjectType ? config.FrameLength >> 1
        : config.FrameLength;

    /// <summary>
    /// Whether the configured object type is one of the two low-delay ones, which share a frame
    /// length, a band table set and a windowing shape distinct from every other profile.
    /// </summary>
    private bool IsLowDelayObjectType =>
        config is not null &&
        (config.ObjectType == AudioObjectType.ErrorResilientAacLd ||
         config.ObjectType == AudioObjectType.ErrorResilientAacEld);

    /// <summary><c>eld_syntax</c>: ELD alone drops several fields from the element syntax.</summary>
    private bool IsEldSyntax =>
        config is not null && config.ObjectType == AudioObjectType.ErrorResilientAacEld;

    /// <summary>
    /// <c>er_syntax</c>: the error-resilient object types, which move TNS to after the gain
    /// control data.
    /// </summary>
    private bool IsErSyntax =>
        config is not null &&
        (config.ObjectType == AudioObjectType.ErrorResilientAacLc ||
         config.ObjectType == AudioObjectType.ErrorResilientAacLtp ||
         config.ObjectType == AudioObjectType.ErrorResilientAacLd ||
         config.ObjectType == AudioObjectType.ErrorResilientAacEld);

    public AacDecoder()
    {
        filterbank = new AacFilterbank();
        spectralCoefficients = new float[1024];
        spectralCoefficientsRight = new float[1024];
        imdctBuffer = new float[1024];
        scalefactors = new int[8 * 64];  // Max: 8 window groups × 64 SFBs
        bandType = new int[8 * 64];       // Codebook per (group, sfb), matching FFmpeg
        bandTypeLeft = new int[8 * 64];   // Saved left-channel band types for MS stereo
        msMask = new bool[8 * 64];        // MS mask per (group, sfb)
        swbOffsets = Array.Empty<int>();
        swbOffsetsShort = Array.Empty<int>();
        
        pendingSpectral = new float[MaxPendingChannels][];
        for (int i = 0; i < MaxPendingChannels; i++)
            pendingSpectral[i] = new float[1024];
    }
    
    /// <summary>
    /// Gets or lazily creates per-element overlap state.
    /// Matches FFmpeg's che[type][id]->ch[ch].saved[].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ElementOverlapState GetOverlap(int elementType, int instanceTag, int channel = 0)
    {
        int idx = (elementType * 16 + instanceTag) * 2 + channel;
        return elementOverlap[idx] ??= new ElementOverlapState();
    }

    /// <summary>
    /// Initializes the decoder with AudioSpecificConfig.
    /// </summary>
    /// <param name="audioConfig">Audio specific configuration.</param>
    public void Initialize(AudioSpecificConfig audioConfig)
    {
        config = audioConfig ?? throw new ArgumentNullException(nameof(audioConfig));

        if (!IsSupportedObjectType(config.ObjectType))
            throw new NotSupportedException(
                $"AAC object type {(int)config.ObjectType} ({config.ObjectType}) is not "
                + "implemented. Implemented: Main, LC, LTP, and the SBR and PS extensions.");

        frameCount = 0;
        detectedRouting = null;

        // Clear all per-element overlap state
        Array.Clear(elementOverlap);

        // USAC's frame syntax has nothing in common with a raw_data_block, so it gets its own
        // decoder rather than a branch through this one. Its configuration is read here because
        // the ASC parser deliberately stopped at the start of it.
        if (config.ObjectType == AudioObjectType.Usac)
        {
            if (config.RawConfig is null)
                throw new InvalidOperationException(
                    "A USAC configuration needs the AudioSpecificConfig bytes it was parsed "
                  + "from, and this one carries none.");

            usacDecoder = new AacUsacDecoder();
            var configReader = new AacBitReader(config.RawConfig);
            configReader.SkipBits(config.SpecificConfigBitOffset);
            usacDecoder.ConfigDecode(ref configReader, (int)config.ChannelConfiguration);
            return;
        }

        usacDecoder = null;
        
        // The configuration owns m4ac.ps and m4ac.sbr; SBR reads the first when a PS payload
        // arrives and when it decides how many channels to produce, and the second decides
        // whether SBR runs at all. Configure here rather than at the first payload, because a
        // signalled stream is upsampled from its first frame even before a header arrives.
        // Where each program-config element's channels come out. Worked out once, here.
        BuildPceChannelOrder();

        sbr.PsMode = audioConfig.PsMode;
        sbrSignalled = audioConfig.SbrMode > 0;
        if (sbrSignalled)
            sbr.Configure(audioConfig.SampleRate, audioConfig.ExtensionSampleRate);

        // 960-sample framing changes three things and nothing else: the band boundaries, the
        // transform lengths, and how many samples a frame produces. The spectral coefficients
        // still sit 1024 to a frame and 128 to a window, so every buffer here stays as it is.
        bool frameLengthShort = audioConfig.FrameLength == 960;

        // Not for the low-delay types: a 960-sample LD or ELD frame transforms at 480 through
        // their own paths, not through the 960-point filterbank, so configuring it would build
        // two transforms and four windows that are never read.
        filterbank.Configure(frameLengthShort &&
                             audioConfig.ObjectType != AudioObjectType.ErrorResilientAacLd &&
                             audioConfig.ObjectType != AudioObjectType.ErrorResilientAacEld);

        // Initialize SWB offsets for this sample rate
        if (audioConfig.ObjectType == AudioObjectType.ErrorResilientAacLd ||
            audioConfig.ObjectType == AudioObjectType.ErrorResilientAacEld)
        {
            // A low-delay frame is 512 coefficients, or 480 at 960 frame length, and has its own
            // band layout for each - not a prefix of the 1024-point one. Both are defined only
            // for sampling indices 3..7, which the configuration parser has already enforced,
            // and the accessors throw rather than return null outside that range.
            bool shortFrame = audioConfig.FrameLength == 960;
            swbOffsets = AacHuffmanTables.GetSwbOffsetsLowDelay(
                audioConfig.SampleRateIndex, shortFrame);
            numSwbLong = AacHuffmanTables.GetNumSwbLowDelay(
                audioConfig.SampleRateIndex, shortFrame);
            tnsMaxBandsLong = AacHuffmanTables.GetTnsMaxBandsLowDelay(
                audioConfig.SampleRateIndex, shortFrame);

            // LD and ELD are defined only for ONLY_LONG_SEQUENCE, so there is no short-block
            // table to choose. Leaving the long one here would let a malformed stream that
            // somehow reached the eight-short path read the wrong table silently; an empty one
            // makes it fail where it happens.
            swbOffsetsShort = Array.Empty<int>();
            numSwbShort = 0;
            tnsMaxBandsShort = 0;
        }
        else
        {
            swbOffsets = AacHuffmanTables.GetSwbOffsets(audioConfig.SampleRateIndex, isShortBlock: false,
                                                        frameLengthShort);
            swbOffsetsShort = AacHuffmanTables.GetSwbOffsets(audioConfig.SampleRateIndex, isShortBlock: true,
                                                             frameLengthShort);
            numSwbLong = AacHuffmanTables.GetNumSwb(audioConfig.SampleRateIndex, isShortBlock: false,
                                                    frameLengthShort);
            numSwbShort = AacHuffmanTables.GetNumSwb(audioConfig.SampleRateIndex, isShortBlock: true,
                                                     frameLengthShort);
            tnsMaxBandsLong = AacHuffmanTables.GetTnsMaxBands(audioConfig.SampleRateIndex, isShortBlock: false);
            tnsMaxBandsShort = AacHuffmanTables.GetTnsMaxBands(audioConfig.SampleRateIndex, isShortBlock: true);
        }
        
        // Build channel routing from the PCE if there is one (channelConfig = 0).
        //
        // Only as a fallback, and only when the PCE's own layout could not be worked out. Picking
        // a routing table by channel count alone assumes the elements arrive in the order the
        // standard layout lists them, and a PCE is exactly the case where they need not: the ISO
        // 5.1 conformance streams declare three front elements and no back elements, so
        // "6 channels => Routing6" sends the front pair to the back pair's outputs. The PCE's
        // position groups are the authority, and BuildPceChannelOrder has already read them.
        if (audioConfig.Pce != null && pceOutputChannel is null)
        {
            int pceChannels = audioConfig.Pce.TotalChannels;
            detectedRouting = pceChannels switch
            {
                3 => Routing3,
                4 => Routing4,
                5 => Routing5,
                6 => Routing6,
                8 => Routing7,
                _ => null
            };
        }
    }

    /// <summary>
    /// Initializes the decoder from ADTS header.
    /// </summary>
    public void InitializeFromAdts(in AdtsHeader header)
    {
        var audioConfig = AdtsParser.FromAdtsHeader(header);
        Initialize(audioConfig);
    }

    /// <summary>
    /// Probes an AAC frame to determine if it contains more than 2 channels.
    /// Scans element type headers (3-bit tags) to count SCE/CPE/LFE elements.
    /// Since element data cannot be skipped without decoding, we only examine
    /// the first element type — if it's a CPE and no prior SCE, it's likely stereo.
    /// </summary>
    /// <param name="frameData">Raw AAC frame data (without ADTS header).</param>
    /// <returns>True if the stream appears to have more than 2 channels.</returns>
    public static bool ProbeIsMultiChannel(ReadOnlySpan<byte> frameData)
    {
        if (frameData.Length < 1) return false;
        
        // Read first 3 bits = first element type
        int firstElementType = (frameData[0] >> 5) & 0x7;
        
        // If first element is CPE, likely stereo (standard mono/stereo layout)
        // If first element is SCE, could be mono OR multi-channel (SCE + CPE + ...)
        // For channelConfig=0 with SCE first, assume multi-channel
        return firstElementType == (int)SyntaxElementType.SingleChannelElement;
    }

    /// <summary>
    /// <summary>
    /// Decodes an AAC frame to multiple output channels.
    /// Each SCE adds 1 channel, each CPE adds 2 channels, each LFE adds 1 channel.
    /// Channel order matches FFmpeg output (FL, FR, FC, LFE, BL, BR for standard configs).
    /// </summary>
    /// <param name="frameData">Raw AAC frame data (without ADTS header).</param>
    /// <param name="channelBuffers">Array of per-channel output buffers (each at least SamplesPerFrame).</param>
    /// <param name="channelCount">Number of channels actually decoded.</param>
    /// <returns>Number of samples decoded per channel.</returns>
    public int DecodeFrameMultiChannel(ReadOnlySpan<byte> frameData, float[][] channelBuffers, out int channelCount)
    {
        if (config == null)
            throw new InvalidOperationException("Decoder not initialized");

        // The low-delay object types do not carry a raw_data_block, so there is no element loop
        // to run; their frame layout is the channel configuration.
        if (IsLowDelayObjectType)
            return DecodeErFrame(frameData, channelBuffers, out channelCount);

        if (usacDecoder is not null)
        {
            var usacReader = new AacBitReader(frameData);
            int usacSamples = usacDecoder.DecodeFrame(ref usacReader, channelBuffers);
            channelCount = usacDecoder.LastFrameChannels;
            lastFrameChannels = channelCount;
            frameCount++;
            return usacSamples;
        }

        var reader = new AacBitReader(frameData);
        int samplesDecoded = 0;
        int elementIndex = 0;
        int totalChannels = 0;
        
        // Get channel routing — prefer explicit config, fall back to detected
        var routing = GetChannelRouting(config.ChannelConfiguration) ?? detectedRouting;
        
        // PCE-guided element count: how many audio elements to expect per frame
        var pce = config.Pce;
        int expectedAudioElements = pce?.AudioElementCount ?? int.MaxValue;
        int audioElementsDecoded = 0;
        
        // Track element types for auto-detecting routing (channelConfig=0, no PCE)
        bool needAutoDetect = routing == null && config.ChannelConfiguration == ChannelConfiguration.AotSpecificConfig && pce == null;
        int sceCount = 0, cpeCount = 0, lfeCount = 0;
        
        // Reset CCE state for this frame
        cceCount = 0;
        decodedElementCount = 0;
        
        // Enable two-pass processing: spectral decode → coupling → TNS → IMDCT
        deferTnsImdct = true;
        pendingChannelCount = 0;
        
        try
        {
            while (reader.BitsRemaining >= 3)
            {
                // If PCE defines the element count and we've decoded all audio elements,
                // continue parsing for CCE, DSE, FIL, END (don't skip CCE!)
                if (audioElementsDecoded >= expectedAudioElements)
                {
                    ParseRemainingElements(ref reader);
                    break;
                }
                
                int elementType = reader.ReadBits(3);
                
                switch ((SyntaxElementType)elementType)
                {
                    case SyntaxElementType.SingleChannelElement:
                        lastSbrElement = SbrElementType.SingleChannel;
                    {
                        int sceTag = reader.PeekBits(4);
                        lastSbrSyntaxElement = SyntaxElementType.SingleChannelElement;
                        lastSbrElementTag = sceTag;
                        int sceIndex = PceElementIndex(
                            SyntaxElementType.SingleChannelElement, sceTag);
                        int outCh = sceIndex >= 0
                            ? GetRoutedChannel(routing, sceIndex, 0, PceChannelOffset(sceIndex))
                            : GetRoutedChannel(routing, elementIndex, 0, totalChannels);
                        var target = outCh < channelBuffers.Length
                            ? channelBuffers[outCh].AsSpan()
                            : scratchOutput.AsSpan();
                        int pendingBefore = pendingChannelCount;
                        DecodeSceSingleChannel(ref reader, target);
                        // Set output buffer index on the pending channel (if deferred)
                        if (pendingChannelCount > pendingBefore)
                            pendingChannels[pendingBefore].OutputBufferIdx = outCh;
                        // Track for CCE target resolution
                        if (decodedElementCount < decodedElements.Length)
                            decodedElements[decodedElementCount++] = new DecodedElementInfo
                            {
                                ElementType = (int)SyntaxElementType.SingleChannelElement,
                                InstanceTag = sceTag,
                                LeftBufferIdx = outCh,
                                RightBufferIdx = -1
                            };
                        samplesDecoded = SamplesPerFrame;
                        elementIndex++;
                        totalChannels++;
                        audioElementsDecoded++;
                        if (needAutoDetect) sceCount++;
                        break;
                    }
                    
                    case SyntaxElementType.ChannelPairElement:
                        lastSbrElement = SbrElementType.ChannelPair;
                    {
                        int cpeTag = reader.PeekBits(4);
                        lastSbrSyntaxElement = SyntaxElementType.ChannelPairElement;
                        lastSbrElementTag = cpeTag;
                        int cpeIndex = PceElementIndex(
                            SyntaxElementType.ChannelPairElement, cpeTag);
                        int cpeBase = cpeIndex >= 0 ? PceChannelOffset(cpeIndex) : totalChannels;
                        int routeIndex = cpeIndex >= 0 ? cpeIndex : elementIndex;
                        int outChL = GetRoutedChannel(routing, routeIndex, 0, cpeBase);
                        int outChR = GetRoutedChannel(routing, routeIndex, 1, cpeBase + 1);
                        var left = outChL < channelBuffers.Length
                            ? channelBuffers[outChL].AsSpan()
                            : scratchOutput.AsSpan();
                        var right = outChR < channelBuffers.Length
                            ? channelBuffers[outChR].AsSpan()
                            : scratchOutput.AsSpan();
                        int pendingBefore = pendingChannelCount;
                        DecodeCpeChannelPair(ref reader, left, right);
                        // Set output buffer indices on the pending channels (if deferred)
                        if (pendingChannelCount > pendingBefore)
                        {
                            pendingChannels[pendingBefore].OutputBufferIdx = outChL;
                            pendingChannels[pendingBefore + 1].OutputBufferIdx = outChR;
                        }
                        // Track for CCE target resolution
                        if (decodedElementCount < decodedElements.Length)
                            decodedElements[decodedElementCount++] = new DecodedElementInfo
                            {
                                ElementType = (int)SyntaxElementType.ChannelPairElement,
                                InstanceTag = cpeTag,
                                LeftBufferIdx = outChL,
                                RightBufferIdx = outChR
                            };
                        samplesDecoded = SamplesPerFrame;
                        elementIndex++;
                        totalChannels += 2;
                        audioElementsDecoded++;
                        if (needAutoDetect) cpeCount++;
                        break;
                    }
                    
                    case SyntaxElementType.CouplingChannelElement:
                        DecodeCce(ref reader);
                        break;
                    
                    case SyntaxElementType.LowFrequencyEffectsElement:
                    {
                        int lfeTag = reader.PeekBits(4);
                        // An LFE never carries SBR, but it still gets its own identity: FFmpeg
                        // allocates a context per element of every type, and a payload arriving
                        // after one must not land in another element's state.
                        lastSbrElement = SbrElementType.SingleChannel;
                        lastSbrSyntaxElement = SyntaxElementType.LowFrequencyEffectsElement;
                        lastSbrElementTag = lfeTag;
                        int lfeIndex = PceElementIndex(
                            SyntaxElementType.LowFrequencyEffectsElement, lfeTag);
                        int outCh = lfeIndex >= 0
                            ? GetRoutedChannel(routing, lfeIndex, 0, PceChannelOffset(lfeIndex))
                            : GetRoutedChannel(routing, elementIndex, 0, totalChannels);
                        var target = outCh < channelBuffers.Length
                            ? channelBuffers[outCh].AsSpan()
                            : scratchOutput.AsSpan();
                        int pendingBefore = pendingChannelCount;
                        DecodeLfe(ref reader, target);
                        // Set output buffer index on the pending channel (if deferred)
                        if (pendingChannelCount > pendingBefore)
                            pendingChannels[pendingBefore].OutputBufferIdx = outCh;
                        // Track for CCE target resolution
                        if (decodedElementCount < decodedElements.Length)
                            decodedElements[decodedElementCount++] = new DecodedElementInfo
                            {
                                ElementType = (int)SyntaxElementType.LowFrequencyEffectsElement,
                                InstanceTag = lfeTag,
                                LeftBufferIdx = outCh,
                                RightBufferIdx = -1
                            };
                        samplesDecoded = SamplesPerFrame;
                        elementIndex++;
                        totalChannels++;
                        audioElementsDecoded++;
                        if (needAutoDetect) lfeCount++;
                        break;
                    }
                    
                    case SyntaxElementType.DataStreamElement:
                        SkipDse(ref reader);
                        break;
                    
                    case SyntaxElementType.ProgramConfigElement:
                        SkipPce(ref reader);
                        break;
                    
                    case SyntaxElementType.FillElement:
                        SkipFil(ref reader, lastSbrElement);
                        break;
                    
                    case SyntaxElementType.End:
                        goto EndOfFrame;
                    
                    default:
                        throw new InvalidOperationException($"Unknown element type: {elementType}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AAC] Frame {frameCount} decode error: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[AAC] Frame {frameCount} decode error: {ex.GetType().Name}: {ex.Message}");
            if (frameCount < 3) // Stack trace for first few errors only
                Console.WriteLine($"  Stack: {ex.StackTrace}");
            // Clear all output channels (with routing, channels are non-sequential)
            int expectedChannels = config?.ChannelCount ?? totalChannels;
            if (expectedChannels <= 0) expectedChannels = totalChannels;
            for (int ch = 0; ch < Math.Min(expectedChannels, channelBuffers.Length); ch++)
                channelBuffers[ch].AsSpan(0, SamplesPerFrame).Clear();
            channelCount = Math.Max(totalChannels, expectedChannels);
            return SamplesPerFrame;
        }
        
        EndOfFrame:
        // Two-pass phase 2: CCE TNS → dependent coupling → TNS → IMDCT → independent coupling
        deferTnsImdct = false;
        SpectralToSample(channelBuffers);
        
        // Auto-detect channel routing for channelConfig=0 from element layout
        if (needAutoDetect && totalChannels >= 3)
        {
            detectedRouting = DetectRoutingFromElements(sceCount, cpeCount, lfeCount);
            
            if (detectedRouting != null)
            {
                // First frame was decoded in bitstream order — reorder buffer data
                // by swapping channel content to match the detected routing.
                ReorderFirstFrame(channelBuffers, totalChannels, sceCount, cpeCount, lfeCount);
            }
        }
        
        channelCount = totalChannels;
        if (totalChannels > 0)
            lastFrameChannels = totalChannels;
        frameCount++;

        // Spectral Band Replication rebuilds the upper half of the spectrum and doubles the sample
        // rate. It needs 2048 samples of room per channel; a caller that supplied less gets the
        // core only, which is what every caller got before SBR existed.
        if (CoreTrace is not null)
        {
            for (int ch = 0; ch < totalChannels && ch < channelBuffers.Length; ch++)
            {
                double weighted = 0, magnitude = 0;
                var buffer = channelBuffers[ch];
                for (int n = 0; n < 1024 && n < buffer.Length; n++)
                {
                    weighted += buffer[n] * (n + 1);
                    magnitude += Math.Abs(buffer[n]);
                }

                CoreTrace.Add($"MKCORE f={frameCount - 1} ch={ch} sum={weighted:F4} " +
                              $"abs={magnitude:F4} first={buffer[0]:F6}");
            }
        }

        // FFmpeg's condition is m4ac.sbr > 0, not "a header has been read": a signalled stream
        // runs the filterbanks from its first frame, upsampling with an empty high band until a
        // header arrives.
        if (EnableSbr && (sbrSignalled || sbr.Ready) && HasRoomForSbr(channelBuffers, totalChannels))
        {
            int expanded = ApplySbrPerElement(channelBuffers, totalChannels, ref channelCount);
            if (expanded > 0)
                return expanded;
        }

        return samplesDecoded;
    }

    /// <summary>
    /// Runs SBR over every channel element, each with its own state, and reports how many samples
    /// per channel came out.
    /// </summary>
    /// <remarks>
    /// This used to be one <c>sbr.Apply(channelBuffers, totalChannels)</c> for the whole frame.
    /// A decoder handles at most a channel pair, so on a 5.1 stream that upsampled channels 0 and
    /// 1 and left the other four holding 1024 core samples in a buffer the caller was then told
    /// held 2048 - while also running the first element's envelope history over the second and
    /// third elements' audio. FFmpeg calls <c>ff_aac_sbr_apply</c> once per element with that
    /// element's own context and its own one or two channels, which is what happens here.
    /// </remarks>
    private int ApplySbrPerElement(float[][] channelBuffers, int totalChannels,
                                   ref int channelCount)
    {
        int expanded = 0;
        bool anyApplied = false;

        for (int e = 0; e < decodedElementCount; e++)
        {
            ref DecodedElementInfo info = ref decodedElements[e];
            var type = (SyntaxElementType)info.ElementType;

            // Coupling channel elements are not audio elements with their own output; their
            // contribution has already been mixed into the elements they target.
            if (type != SyntaxElementType.SingleChannelElement &&
                type != SyntaxElementType.ChannelPairElement &&
                type != SyntaxElementType.LowFrequencyEffectsElement)
                continue;

            SbrDecoder element = SbrForElement(type, info.InstanceTag);

            int present = 0;
            if (info.LeftBufferIdx >= 0 && info.LeftBufferIdx < channelBuffers.Length)
                sbrElementChannels[present++] = channelBuffers[info.LeftBufferIdx];
            if (info.RightBufferIdx >= 0 && info.RightBufferIdx < channelBuffers.Length)
                sbrElementChannels[present++] = channelBuffers[info.RightBufferIdx];
            if (present == 0)
                continue;

            // Parametric Stereo makes a mono element produce two channels, and SBR synthesises
            // the second one into the slot above. Both slots must hold a real buffer either way:
            // SBR checks their lengths before writing, and a null would fault instead.
            if (present == 1)
            {
                int second = info.LeftBufferIdx + 1;
                sbrElementChannels[1] = second < channelBuffers.Length
                    ? channelBuffers[second]
                    : sbrElementChannels[0];
            }

            int produced = element.Apply(sbrElementChannels, present);
            if (produced <= 0)
                continue;

            anyApplied = true;
            if (produced > expanded)
                expanded = produced;

            // Parametric Stereo turns one coded channel into two. It only applies to a mono
            // element, and a stream that uses it has exactly that one element, so widening the
            // frame's channel count is safe here and meaningless anywhere else.
            if (element.OutputChannels > present && element.OutputChannels > totalChannels)
            {
                channelCount = element.OutputChannels;
                lastFrameChannels = element.OutputChannels;
            }
        }

        return anyApplied ? expanded : 0;
    }

    /// <summary>
    /// The one or two channel buffers handed to a single element's SBR decoder. Reused so the
    /// per-frame path allocates nothing.
    /// </summary>
    private readonly float[][] sbrElementChannels = new float[2][];

    private static bool HasRoomForSbr(float[][] channelBuffers, int channelCount)
    {
        for (int ch = 0; ch < channelCount && ch < channelBuffers.Length; ch++)
        {
            if (channelBuffers[ch].Length < 2048)
                return false;
        }
        return channelCount > 0;
    }

    /// <summary>
    /// Decodes an AAC frame.
    /// </summary>
    /// <param name="frameData">Raw AAC frame data (without ADTS header).</param>
    /// <param name="outputLeft">Output buffer for left channel (must be at least SamplesPerFrame).</param>
    /// <param name="outputRight">Output buffer for right channel (can be empty for mono).</param>
    /// <returns>Number of samples decoded per channel.</returns>
    public int DecodeFrame(ReadOnlySpan<byte> frameData, Span<float> outputLeft, Span<float> outputRight)
    {
        if (config == null)
            throw new InvalidOperationException("Decoder not initialized");
        
        if (outputLeft.Length < SamplesPerFrame)
            throw new ArgumentException($"Output buffer too small, need {SamplesPerFrame} samples");
        
        var reader = new AacBitReader(frameData);
        int samplesDecoded = 0;
        int decodedChannels = 0;
        
        try
        {
            // Decode channel elements
            while (reader.BitsRemaining >= 3)
            {
                int elementType = reader.ReadBits(3);
                
                switch ((SyntaxElementType)elementType)
                {
                    case SyntaxElementType.SingleChannelElement:
                        lastSbrElement = SbrElementType.SingleChannel;
                    {
                        // SCE output: if no CPE decoded yet, write to outputLeft; otherwise discard
                        var target = decodedChannels < 2 ? outputLeft : scratchOutput.AsSpan();
                        DecodeSceSingleChannel(ref reader, target);
                        samplesDecoded = SamplesPerFrame;
                        decodedChannels = Math.Max(decodedChannels, 1);
                        break;
                    }
                    
                    case SyntaxElementType.ChannelPairElement:
                        lastSbrElement = SbrElementType.ChannelPair;
                        DecodeCpeChannelPair(ref reader, outputLeft, outputRight);
                        samplesDecoded = SamplesPerFrame;
                        decodedChannels = Math.Max(decodedChannels, 2);
                        break;
                    
                    case SyntaxElementType.CouplingChannelElement:
                        DecodeCce(ref reader);
                        break;
                    
                    case SyntaxElementType.LowFrequencyEffectsElement:
                    {
                        // LFE output: if no CPE decoded yet, write to outputLeft; otherwise discard
                        var target = decodedChannels < 2 ? outputLeft : scratchOutput.AsSpan();
                        DecodeLfe(ref reader, target);
                        if (decodedChannels < 1)
                        {
                            samplesDecoded = SamplesPerFrame;
                            decodedChannels = Math.Max(decodedChannels, 1);
                        }
                        break;
                    }
                    
                    case SyntaxElementType.DataStreamElement:
                        SkipDse(ref reader);
                        break;
                    
                    case SyntaxElementType.ProgramConfigElement:
                        SkipPce(ref reader);
                        break;
                    
                    case SyntaxElementType.FillElement:
                        SkipFil(ref reader, lastSbrElement);
                        break;
                    
                    case SyntaxElementType.End:
                        goto EndOfFrame;
                    
                    default:
                        throw new InvalidOperationException($"Unknown element type: {elementType}");
                }
            }
        }
        catch (Exception ex)
        {
            // Return silence on decode error — per-element overlap state is preserved
            // since each element uses its own state via GetOverlap()
            System.Diagnostics.Debug.WriteLine($"[AAC] Frame {frameCount} decode error: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[AAC] Frame {frameCount} decode error: {ex.GetType().Name}: {ex.Message}");
            var stackLines = ex.StackTrace?.Split('\n');
            if (stackLines != null && stackLines.Length > 0)
                Console.WriteLine($"  at: {stackLines[0].Trim()}");
            outputLeft.Slice(0, SamplesPerFrame).Clear();
            if (!outputRight.IsEmpty)
                outputRight.Slice(0, SamplesPerFrame).Clear();
            return SamplesPerFrame;
        }
        
        EndOfFrame:
        if (decodedChannels > 0)
            lastFrameChannels = decodedChannels;
        frameCount++;
        return samplesDecoded;
    }

    /// <summary>
    /// Decodes a complete ADTS frame including header.
    /// </summary>
    public int DecodeAdtsFrame(ReadOnlySpan<byte> adtsData, Span<float> outputLeft, Span<float> outputRight)
    {
        if (!AdtsParser.TryParseHeader(adtsData, out var header))
            throw new InvalidOperationException("Invalid ADTS header");
        
        if (config == null)
            InitializeFromAdts(header);
        
        var payload = adtsData.Slice(header.HeaderSize, header.PayloadSize);
        return DecodeFrame(payload, outputLeft, outputRight);
    }

    #region Syntax Element Decoding

    /// <summary>
    /// Decodes Single Channel Element (SCE).
    /// </summary>
    private void DecodeSceSingleChannel(ref AacBitReader reader, Span<float> output)
    {
        int instanceTag = ReadElementInstanceTag(ref reader);
        CoreTrace?.Add($"MKELEM sce tag={instanceTag}");
        
        var result = DecodeIcs(ref reader, spectralCoefficients, 0, false, false);
        
        // Per-element overlap state: SCE type = 0
        var overlap = GetOverlap(0, instanceTag);
        
        // AAC-Main prediction: applied after dequant, before TNS (SCE is never common_window)
        if (config!.ObjectType == AudioObjectType.AacMain)
            ApplyPrediction(spectralCoefficients, in result.IcsInfo, overlap);
        
        // Two-pass mode: store spectral data for SpectralToSample, skip TNS/IMDCT
        if (deferTnsImdct && pendingChannelCount < MaxPendingChannels)
        {
            Array.Copy(spectralCoefficients, 0, pendingSpectral[pendingChannelCount], 0, 1024);
            pendingChannels[pendingChannelCount].IcsResult = result;
            pendingChannels[pendingChannelCount].InstanceTag = instanceTag;
            pendingChannels[pendingChannelCount].ElementType = (int)SyntaxElementType.SingleChannelElement;
            pendingChannels[pendingChannelCount].ChannelInPair = 0;
            pendingChannelCount++;
            return;
        }
        
        // Apply TNS (for SCE, immediately after decode/prediction — no MS stereo needed)
        ApplyTns(spectralCoefficients, in result);
        
        filterbank.ImdctAndWindowing(
            spectralCoefficients,
            output,
            overlap.PreviousSamples.AsSpan(),
            result.IcsInfo.WindowSequence,
            overlap.PrevWindowSequence,
            result.IcsInfo.WindowShape,
            overlap.PrevWindowShape,
            imdctBuffer);
        
        overlap.PrevWindowSequence = result.IcsInfo.WindowSequence;
        overlap.PrevWindowShape = result.IcsInfo.WindowShape;
    }

    /// <summary>
    /// Decodes Channel Pair Element (CPE).
    /// </summary>
    private void DecodeCpeChannelPair(ref AacBitReader reader, Span<float> left, Span<float> right)
    {
        // element_instance_tag (4 bits)
        int instanceTag = ReadElementInstanceTag(ref reader);

        // common_window (1 bit). ELD does not send it: a channel pair always shares its window,
        // because ELD has only one window, so the bit would carry no information and is absent
        // from the syntax rather than present and always set.
        bool commonWindow = IsEldSyntax || reader.ReadBit();
        CoreTrace?.Add($"MKELEM cpe tag={instanceTag} common={(commonWindow ? 1 : 0)}");
        
        // Diagnostic: log CPE properties for first few frames
        IcsInfo sharedIcsInfo = default;
        int msMaskPresent = 0;
        int maxSfbSte = 0;
        
        if (commonWindow)
        {
            sharedIcsInfo = DecodeIcsInfo(ref reader);
            maxSfbSte = sharedIcsInfo.MaxScalefactorBand;

            // With a common window the second channel copies the first channel's ICS info, but
            // its long term prediction is its OWN and is transmitted here, between the shared
            // ICS info and ms_mask_present:
            //   if (cpe->ch[1].ics.predictor_present && object_type != AOT_AAC_MAIN)
            //       if ((cpe->ch[1].ics.ltp.present = get_bits1(gb)))
            //           decode_ltp(...);
            // Not reading it does not merely lose the second channel's prediction - every bit
            // after it in the frame is then misread, which is why adding LTP to the ICS info
            // alone changed nothing measurable on ap05_48.
            rightLtp.Clear();
            if (sharedIcsInfo.PredictorPresent &&
                config!.ObjectType != AudioObjectType.AacMain)
            {
                if (reader.ReadBit())
                    DecodeLtp(ref reader, ref rightLtp, sharedIcsInfo.MaxScalefactorBand);
            }

            // ms_mask_present (2 bits)
            msMaskPresent = reader.ReadBits(2);
            
            if (msMaskPresent == 1)
            {
                // Read individual MS mask bits (1 bit per SFB per window group)
                int numWindowGroups = Math.Max(sharedIcsInfo.NumWindowGroups, 1);
                for (int g = 0; g < numWindowGroups; g++)
                {
                    for (int sfb = 0; sfb < maxSfbSte; sfb++)
                    {
                        msMask[g * maxSfbSte + sfb] = reader.ReadBit();
                    }
                }
            }
            else if (msMaskPresent == 2)
            {
                // All SFBs use MS
                int numWindowGroups = Math.Max(sharedIcsInfo.NumWindowGroups, 1);
                for (int g = 0; g < numWindowGroups; g++)
                    for (int sfb = 0; sfb < maxSfbSte; sfb++)
                        msMask[g * maxSfbSte + sfb] = true;
            }
            else
            {
                // msMaskPresent == 0: no MS
                Array.Clear(msMask);
            }
            
        }
        
        // Decode left channel (spectrum + pulse, no TNS yet)
        var leftResult = DecodeIcs(ref reader, spectralCoefficients, 0, commonWindow, false, sharedIcsInfo);
        
        // For CPE non-common-window AAC-Main: prediction applied per-channel after ICS decode
        // (FFmpeg: aacdec.c line 1776-1777 — inside ff_aac_decode_ics when !common_window)
        if (!commonWindow && config!.ObjectType == AudioObjectType.AacMain)
        {
            var overlapL = GetOverlap(1, instanceTag, 0);
            ApplyPrediction(spectralCoefficients, in leftResult.IcsInfo, overlapL);
        }
        
        // Save left channel's band types for MS stereo check
        Array.Copy(bandType, bandTypeLeft, bandType.Length);
        
        // Decode right channel into separate buffer
        var rightResult = DecodeIcs(ref reader, spectralCoefficientsRight, 0, commonWindow, false, sharedIcsInfo);
        // bandType now contains right channel's band types
        
        // For CPE non-common-window AAC-Main: prediction on right channel
        if (!commonWindow && config!.ObjectType == AudioObjectType.AacMain)
        {
            var overlapR = GetOverlap(1, instanceTag, 1);
            ApplyPrediction(spectralCoefficientsRight, in rightResult.IcsInfo, overlapR);
        }
        
        // Apply MS stereo (BEFORE prediction, intensity stereo and TNS, matching FFmpeg's processing order)
        if (commonWindow && msMaskPresent != 0 && !DebugSkipMs)
        {
            ApplyMidSideStereo(in leftResult.IcsInfo, maxSfbSte);
        }
        
        // AAC-Main prediction: applied after MS, before IS and TNS (common_window path only)
        // Port of FFmpeg's decode_cpe() lines 1822-1825.
        if (commonWindow && config!.ObjectType == AudioObjectType.AacMain)
        {
            var overlapL = GetOverlap(1, instanceTag, 0);
            var overlapR = GetOverlap(1, instanceTag, 1);
            
            if (DebugSkipPredForCpeTag == instanceTag)
            {
                // Still update predictor state, but save/restore coefficients to skip output
                Span<float> savedL = stackalloc float[1024];
                Span<float> savedR = stackalloc float[1024];
                spectralCoefficients.CopyTo(savedL);
                spectralCoefficientsRight.AsSpan().CopyTo(savedR);
                ApplyPrediction(spectralCoefficients, in leftResult.IcsInfo, overlapL);
                ApplyPrediction(spectralCoefficientsRight, in rightResult.IcsInfo, overlapR);
                savedL.CopyTo(spectralCoefficients);
                savedR.CopyTo(spectralCoefficientsRight.AsSpan());
            }
            else
            {
                ApplyPrediction(spectralCoefficients, in leftResult.IcsInfo, overlapL);
                ApplyPrediction(spectralCoefficientsRight, in rightResult.IcsInfo, overlapR);
            }
        }
        
        // Apply intensity stereo (after MS+prediction, before TNS — matching FFmpeg aacdec.c:1828)
        // bandType[] still has right channel's values, scalefactors[] has right channel's IS positions
        if (!DebugSkipIs)
            ApplyIntensityStereo(in leftResult.IcsInfo, msMaskPresent);
        
        // Two-pass mode: store spectral data for SpectralToSample, skip TNS/IMDCT
        if (deferTnsImdct && pendingChannelCount + 1 < MaxPendingChannels)
        {
            // Left channel
            Array.Copy(spectralCoefficients, 0, pendingSpectral[pendingChannelCount], 0, 1024);
            pendingChannels[pendingChannelCount].IcsResult = leftResult;
            pendingChannels[pendingChannelCount].InstanceTag = instanceTag;
            pendingChannels[pendingChannelCount].ElementType = (int)SyntaxElementType.ChannelPairElement;
            pendingChannels[pendingChannelCount].ChannelInPair = 0;
            pendingChannelCount++;
            
            // Right channel. Its ICS info is the pair's shared one, so the long term prediction
            // read separately for this channel has to be put back on it.
            Array.Copy(spectralCoefficientsRight, 0, pendingSpectral[pendingChannelCount], 0, 1024);
            if (commonWindow)
                rightResult.IcsInfo.Ltp = rightLtp;
            pendingChannels[pendingChannelCount].IcsResult = rightResult;
            pendingChannels[pendingChannelCount].InstanceTag = instanceTag;
            pendingChannels[pendingChannelCount].ElementType = (int)SyntaxElementType.ChannelPairElement;
            pendingChannels[pendingChannelCount].ChannelInPair = 1;
            pendingChannelCount++;
            return;
        }
        
        // Apply TNS to each channel (AFTER MS and intensity stereo)
        ApplyTns(spectralCoefficients, in leftResult);
        ApplyTns(spectralCoefficientsRight, in rightResult);
        
        // Per-element overlap state: CPE type = 1, ch0 = left, ch1 = right
        var overlapLeft = GetOverlap(1, instanceTag, 0);
        var overlapRight = GetOverlap(1, instanceTag, 1);
        
        if (CoreTrace is not null)
        {
            CoreTrace.Add(DescribeSpectrum(spectralCoefficients, in leftResult.IcsInfo, overlapLeft));
            CoreTrace.Add(DescribeSpectrum(spectralCoefficientsRight, in rightResult.IcsInfo, overlapRight));
        }

        // IMDCT + windowing + overlap-add for left channel
        filterbank.ImdctAndWindowing(
            spectralCoefficients,
            left,
            overlapLeft.PreviousSamples.AsSpan(),
            leftResult.IcsInfo.WindowSequence,
            overlapLeft.PrevWindowSequence,
            leftResult.IcsInfo.WindowShape,
            overlapLeft.PrevWindowShape,
            imdctBuffer);
        
        overlapLeft.PrevWindowSequence = leftResult.IcsInfo.WindowSequence;
        overlapLeft.PrevWindowShape = leftResult.IcsInfo.WindowShape;
        
        // IMDCT + windowing + overlap-add for right channel
        filterbank.ImdctAndWindowing(
            spectralCoefficientsRight,
            right,
            overlapRight.PreviousSamples.AsSpan(),
            rightResult.IcsInfo.WindowSequence,
            overlapRight.PrevWindowSequence,
            rightResult.IcsInfo.WindowShape,
            overlapRight.PrevWindowShape,
            imdctBuffer);
        
        overlapRight.PrevWindowSequence = rightResult.IcsInfo.WindowSequence;
        overlapRight.PrevWindowShape = rightResult.IcsInfo.WindowShape;
    }
    
    /// <summary>
    /// Applies Mid/Side stereo decoding.
    /// Port of FFmpeg's apply_mid_side_stereo() from aacdec_dsp_template.c.
    /// Butterfly: L = M + S, R = M - S (in-place).
    /// </summary>
    private void ApplyMidSideStereo(in IcsInfo icsInfo, int maxSfbSte)
    {
        ReadOnlySpan<int> offsets = icsInfo.WindowSequence == WindowSequence.EightShortSequence
            ? swbOffsetsShort
            : swbOffsets;
        
        int ch0Offset = 0;
        int ch1Offset = 0;
        int msAppliedCount = 0;
        
        for (int g = 0; g < icsInfo.NumWindowGroups; g++)
        {
            for (int sfb = 0; sfb < maxSfbSte; sfb++)
            {
                int idx = g * maxSfbSte + sfb;
                
                // Only apply if MS is enabled and neither channel uses noise codebook (13)
                if (msMask[idx] && bandTypeLeft[idx] < 13 && bandType[idx] < 13)
                {
                    msAppliedCount++;
                    int start = offsets[sfb];
                    int end = offsets[sfb + 1];
                    int len = end - start;
                    
                    for (int group = 0; group < icsInfo.WindowGroupLength[g]; group++)
                    {
                        int baseOffset = ch0Offset + group * 128 + start;
                        
                        for (int k = 0; k < len; k++)
                        {
                            float mid = spectralCoefficients[baseOffset + k];
                            float side = spectralCoefficientsRight[baseOffset + k];
                            spectralCoefficients[baseOffset + k] = mid + side;
                            spectralCoefficientsRight[baseOffset + k] = mid - side;
                        }
                    }
                }
            }
            
            ch0Offset += icsInfo.WindowGroupLength[g] * 128;
            ch1Offset += icsInfo.WindowGroupLength[g] * 128;
        }
    }

    /// <summary>
    /// Applies intensity stereo decoding; reference: ISO 14496-3 4.6.8.2.3.
    /// For SFBs where the right channel has INTENSITY_BT (15) or INTENSITY_BT2 (14),
    /// copies the left channel's spectral data to the right channel with scaling.
    /// Port of FFmpeg's apply_intensity_stereo() from aacdec_dsp_template.c:120-156.
    /// </summary>
    private void ApplyIntensityStereo(in IcsInfo icsInfo, int msMaskPresent)
    {
        int maxSfb = icsInfo.MaxScalefactorBand;
        ReadOnlySpan<int> offsets = icsInfo.WindowSequence == WindowSequence.EightShortSequence
            ? swbOffsetsShort
            : swbOffsets;
        
        int coefOffset = 0;
        
        for (int g = 0; g < icsInfo.NumWindowGroups; g++)
        {
            for (int sfb = 0; sfb < maxSfb; sfb++)
            {
                int idx = g * maxSfb + sfb;
                int bt = bandType[idx]; // right channel's band type
                
                if (bt == AacCodebooks.IntensityHcb2 || bt == AacCodebooks.IntensityHcb)
                {
                    // Direction: INTENSITY_BT2 (14) → -1, INTENSITY_BT (15) → +1
                    int c = -1 + 2 * (bt - 14);
                    
                    // Modify direction by MS mask if present
                    if (msMaskPresent != 0)
                        c *= 1 - 2 * (msMask[idx] ? 1 : 0);
                    
                    // Intensity gain: pow(2, -isPosition / 4.0)
                    // scalefactors[idx] stores the isAccum value for intensity SFBs
                    float intensityGain = (float)Math.Pow(2.0, -scalefactors[idx] / 4.0);
                    float scale = c * intensityGain;
                    
                    int start = offsets[sfb];
                    int len = offsets[sfb + 1] - start;
                    
                    for (int group = 0; group < icsInfo.WindowGroupLength[g]; group++)
                    {
                        int baseOffset = coefOffset + group * 128 + start;
                        
                        for (int k = 0; k < len; k++)
                        {
                            spectralCoefficientsRight[baseOffset + k] =
                                spectralCoefficients[baseOffset + k] * scale;
                        }
                    }
                }
            }
            
            coefOffset += icsInfo.WindowGroupLength[g] * 128;
        }
    }

    /// <summary>
    /// Decodes Individual Channel Stream.
    /// Returns the ICS info and TNS data. TNS is NOT applied here — caller applies it
    /// after MS stereo (for CPE) or immediately (for SCE).
    /// </summary>
    private IcsDecodeResult DecodeIcs(
        ref AacBitReader reader,
        Span<float> spectral,
        int group,
        bool commonWindow,
        bool scaleFlag,
        IcsInfo sharedIcsInfo = default)
    {
        // global_gain (8 bits)
        int globalGain = reader.ReadBits(8);
        
        IcsInfo icsInfo;
        if (!commonWindow && !scaleFlag)
        {
            icsInfo = DecodeIcsInfo(ref reader);
        }
        else
        {
            // Use the shared IcsInfo from CPE common_window
            icsInfo = sharedIcsInfo;
        }
        
        bool isShort = icsInfo.WindowSequence == WindowSequence.EightShortSequence;
        var offsets = isShort ? swbOffsetsShort : swbOffsets;
        
        // section_data — populates bandType[] for all (group, sfb) pairs
        DecodeSectionData(ref reader, in icsInfo);
        
        // scale_factor_data — populates scalefactors[] using bandType[]
        DecodeScaleFactorData(ref reader, globalGain, in icsInfo);
        
        // pulse_data (optional — only for long blocks per spec). Absent from ELD's syntax.
        bool eldSyntax = IsEldSyntax;
        bool erSyntax = IsErSyntax;
        bool pulsePresent = !eldSyntax && reader.ReadBit();
        PulseData pulseData = default;
        if (pulsePresent)
        {
            pulseData = DecodePulseData(ref reader);
        }

        // tns_data (optional)
        bool tnsPresent = reader.ReadBit();
        TnsData tnsData = default;
        if (tnsPresent && !erSyntax)
        {
            AacTnsDecoder.DecodeTnsData(ref reader, in icsInfo, out tnsData);
        }

        // gain_control_data (optional, SSR profile only). Absent from ELD's syntax.
        bool gainControlPresent = !eldSyntax && reader.ReadBit();
        if (gainControlPresent)
        {
            SkipGainControlData(ref reader, icsInfo.WindowSequence);
        }

        // The error-resilient object types carry the TNS data HERE, after the gain control,
        // rather than where the presence flag was read. FFmpeg's comment on this is worth
        // keeping: it sees no textual basis in the specification for it, but both the reference
        // decoder and real implementations do it, so a conformant decoder must too.
        if (tnsPresent && erSyntax)
        {
            AacTnsDecoder.DecodeTnsData(ref reader, in icsInfo, out tnsData);
        }

// spectral_data — uses bandType[], scalefactors[], correct SWB offsets
        DecodeSpectralData(ref reader, spectral, in icsInfo, offsets);
        
        // Apply pulse data if present (only for long blocks)
        if (pulsePresent && !isShort)
        {
            ApplyPulseData(spectral, in pulseData, in icsInfo, offsets);
        }
        
        return new IcsDecodeResult
        {
            IcsInfo = icsInfo,
            TnsData = tnsData,
            TnsPresent = tnsPresent
        };
    }
    
    /// <summary>
    /// Applies TNS filter to spectral coefficients for a decoded ICS.
    /// </summary>
    /// <summary>
    /// <c>apply_ltp</c>: predict this frame's spectrum from the reconstructed signal one lag ago,
    /// and add it into the bands that asked for it.
    /// </summary>
    /// <remarks>
    /// The prediction is formed in the time domain, windowed and transformed forward, and only
    /// then added to the coefficients - so it runs before TNS and before the IMDCT, which is
    /// where the call sits in FFmpeg's element loop. The C reuses the channel's own output and
    /// coefficient buffers as scratch; this port keeps two buffers of its own instead, because
    /// our output span is one frame of 1024 samples and the prediction needs 2048.
    /// </remarks>
    private void ApplyLtp(Span<float> spectral, in IcsInfo ics, ElementOverlapState overlap,
                          in IcsDecodeResult result)
    {
        if (ics.WindowSequence == WindowSequence.EightShortSequence)
            return;

        ref readonly AacLtp ltp = ref ics.Ltp;
        float[] ltpState = overlap.LtpState;

        // Everything from the lag to the end of the window, then silence.
        int samples = ltp.Lag < 1024 ? ltp.Lag + 1024 : 2048;
        for (int i = 0; i < samples; i++)
            ltpPredictedTime[i] = ltpState[i + 2048 - ltp.Lag] * ltp.Coef;
        Array.Clear(ltpPredictedTime, samples, 2048 - samples);

        filterbank.WindowingAndMdctLtp(ltpPredictedFreq, ltpPredictedTime,
                                       ics.WindowSequence, ics.WindowShape,
                                       overlap.PrevWindowShape);

        // The prediction goes through TNS as well, but with the tool's other filter: the C calls
        // apply_tns(predFreq, ..., decode = 0), which is the moving-average form rather than the
        // autoregressive one the residual gets.
        if (result.TnsPresent)
            ApplyTns(ltpPredictedFreq.AsSpan(0, 1024), in result, decode: false);

        int[] offsets = ics.SwbOffset;
        int limit = Math.Min(ics.MaxScalefactorBand, AacLtp.MaxLongSfb);
        for (int sfb = 0; sfb < limit; sfb++)
        {
            if (!ltp[sfb]) continue;
            int to = Math.Min(offsets[sfb + 1], 1024);
            for (int i = offsets[sfb]; i < to; i++)
                spectral[i] += ltpPredictedFreq[i];
        }
    }

    /// <summary>
    /// The second channel of a common-window pair has its own long term prediction, transmitted
    /// separately from the shared ICS info. Held here between the parse and the pending pass.
    /// </summary>
    private AacLtp rightLtp;

    /// <summary>The 2048 predicted time samples, and the 1024 coefficients they transform to.</summary>
    private readonly float[] ltpPredictedTime = new float[2048];
    private readonly float[] ltpPredictedFreq = new float[1024];

    /// <summary>Scratch for <c>update_ltp</c>'s half-built history row.</summary>
    private readonly float[] ltpSaved = new float[1024];

    /// <summary>
    /// The object types this decoder actually implements.
    /// </summary>
    /// <remarks>
    /// An explicit list, and everything else throws, because the alternative is what this decoder
    /// used to do: an ER AAC LD, ER AAC ELD or USAC configuration was accepted, every frame then
    /// threw an IndexOutOfRangeException deep in the core, the per-frame catch swallowed it and
    /// cleared the buffers, and the result was a stream of noise roughly the right length. A
    /// player cannot tell that from audio it should be playing, and the house rule is that unknown
    /// input fails loudly. These are the types the conformance gate covers: Main, LC and LTP, plus
    /// the two extension types a configuration uses to signal SBR and PS over an LC core.
    /// </remarks>
    private static bool IsSupportedObjectType(AudioObjectType type) => type switch
    {
        AudioObjectType.AacMain => true,
        AudioObjectType.AacLowComplexity => true,
        AudioObjectType.AacLongTermPrediction => true,
        AudioObjectType.SpectralBandReplication => true,
        AudioObjectType.ParametricStereo => true,
        AudioObjectType.ErrorResilientAacLd => true,
        AudioObjectType.ErrorResilientAacEld => true,
        AudioObjectType.Usac => true,
        _ => false,
    };

    /// <summary>True when the configuration's object type predicts long-term.</summary>
    private bool IsLtpObjectType =>
        config is not null &&
        (config.ObjectType == AudioObjectType.AacLongTermPrediction ||
         config.ObjectType == AudioObjectType.ErrorResilientAacLtp);

    /// <summary><c>ff_ltp_coef</c>: the eight gains LTP's three-bit index selects.</summary>
    private static readonly float[] LtpCoef =
    [
        0.570829f, 0.696616f, 0.813004f, 0.911304f,
        0.984900f, 1.067894f, 1.194601f, 1.369533f,
    ];

    /// <summary><c>decode_ltp</c>: the lag, the gain index and one flag per scalefactor band.</summary>
    private static void DecodeLtp(ref AacBitReader reader, ref AacLtp ltp, int maxSfb)
    {
        ltp.Present = true;
        ltp.Lag = reader.ReadBits(11);
        ltp.Coef = LtpCoef[reader.ReadBits(3)];

        int limit = Math.Min(maxSfb, AacLtp.MaxLongSfb);
        for (int sfb = 0; sfb < limit; sfb++)
            ltp[sfb] = reader.ReadBit();
    }

    private void ApplyTns(Span<float> spectral, in IcsDecodeResult result, bool decode = true)
    {
        if (!result.TnsPresent || DebugSkipTns)
            return;
        
        bool isShort = result.IcsInfo.WindowSequence == WindowSequence.EightShortSequence;
        var offsets = isShort ? swbOffsetsShort : swbOffsets;
        int numSwb = isShort ? numSwbShort : numSwbLong;
        int tnsMaxBands = isShort ? tnsMaxBandsShort : tnsMaxBandsLong;
        AacTnsDecoder.ApplyTnsFilter(spectral, in result.TnsData, in result.IcsInfo, offsets,
                                     numSwb, tnsMaxBands, decode);
    }

    #region AAC-Main Prediction
    
    /// <summary>
    /// Truncates a float to 16-bit mantissa precision.
    /// Port of FFmpeg's flt16_trunc() from aacdec_float_prediction.h.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Flt16Trunc(float pf)
    {
        uint bits = BitConverter.SingleToUInt32Bits(pf);
        bits &= 0xFFFF0000U;
        return BitConverter.UInt32BitsToSingle(bits);
    }
    
    /// <summary>
    /// Rounds a float to 16-bit mantissa precision (round half up).
    /// Port of FFmpeg's flt16_round() from aacdec_float_prediction.h.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Flt16Round(float pf)
    {
        uint bits = BitConverter.SingleToUInt32Bits(pf);
        bits = (bits + 0x00008000U) & 0xFFFF0000U;
        return BitConverter.UInt32BitsToSingle(bits);
    }
    
    /// <summary>
    /// Rounds a float to 16-bit mantissa precision (round half to even).
    /// Port of FFmpeg's flt16_even() from aacdec_float_prediction.h.
    /// NOTE: FFmpeg's C code is `tmp.i & 0x00010000U >> 16` — due to C precedence
    /// (>> binds tighter than &), this evaluates as `tmp.i & 1` (bit 0), not bit 16.
    /// We must match FFmpeg exactly for FATE conformance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Flt16Even(float pf)
    {
        uint bits = BitConverter.SingleToUInt32Bits(pf);
        bits = (bits + 0x00007FFFU + (bits & 1U)) & 0xFFFF0000U;
        return BitConverter.UInt32BitsToSingle(bits);
    }
    
    /// <summary>
    /// Resets a single predictor state to initial values.
    /// Port of FFmpeg's reset_predict_state().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ResetPredictState(ref AacPredictorState ps)
    {
        ps.R0 = 0.0f;
        ps.R1 = 0.0f;
        ps.Cor0 = 0.0f;
        ps.Cor1 = 0.0f;
        ps.Var0 = 1.0f;
        ps.Var1 = 1.0f;
    }
    
    /// <summary>
    /// Resets all 672 predictor states.
    /// Port of FFmpeg's reset_all_predictors().
    /// </summary>
    private static void ResetAllPredictors(AacPredictorState[] ps)
    {
        for (int i = 0; i < 672; i++)
            ResetPredictState(ref ps[i]);
    }
    
    /// <summary>
    /// Resets predictor states for a given group number (1-30).
    /// Group i resets states at positions i-1, i-1+30, i-1+60, ...
    /// Port of FFmpeg's reset_predictor_group().
    /// </summary>
    private static void ResetPredictorGroup(AacPredictorState[] ps, int groupNum)
    {
        for (int i = groupNum - 1; i < 672; i += 30)
            ResetPredictState(ref ps[i]);
    }
    
    /// <summary>
    /// Applies the backward-adaptive prediction for one spectral coefficient.
    /// Port of FFmpeg's predict() from aacdec_float_prediction.h.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Predict(ref AacPredictorState ps, ref float coef, bool outputEnable)
    {
        const float a = 0.953125f;    // 61.0 / 64
        const float alpha = 0.90625f; // 29.0 / 32
        
        float r0 = ps.R0, r1 = ps.R1;
        float cor0 = ps.Cor0, cor1 = ps.Cor1;
        float var0 = ps.Var0, var1 = ps.Var1;
        
        float k1 = var0 > 1 ? cor0 * Flt16Even(a / var0) : 0;
        float k2 = var1 > 1 ? cor1 * Flt16Even(a / var1) : 0;
        
        float pv = Flt16Round(k1 * r0 + k2 * r1);
        if (outputEnable)
            coef += pv;
        
        float e0 = coef;
        float e1 = e0 - k1 * r0;
        
        ps.Cor1 = Flt16Trunc(alpha * cor1 + r1 * e1);
        ps.Var1 = Flt16Trunc(alpha * var1 + 0.5f * (r1 * r1 + e1 * e1));
        ps.Cor0 = Flt16Trunc(alpha * cor0 + r0 * e0);
        ps.Var0 = Flt16Trunc(alpha * var0 + 0.5f * (r0 * r0 + e0 * e0));
        
        ps.R1 = Flt16Trunc(a * (r0 - k1 * e0));
        ps.R0 = Flt16Trunc(a * e0);
    }
    
    /// <summary>
    /// Applies AAC-Main frequency domain prediction to spectral coefficients.
    /// Port of FFmpeg's apply_prediction() from aacdec_dsp_template.c.
    /// Called after dequantization (and after MS for CPE), before TNS and IMDCT.
    /// </summary>
    private void ApplyPrediction(Span<float> spectral, in IcsInfo icsInfo, ElementOverlapState overlap)
    {
        if (config == null) return;
        
        // First-time initialization
        if (!overlap.PredictorInitialized)
        {
            ResetAllPredictors(overlap.PredictorStates);
            overlap.PredictorInitialized = true;
        }
        
        if (icsInfo.WindowSequence != WindowSequence.EightShortSequence)
        {
            int predSfbMax = PredSfbMax[config.SampleRateIndex];
            var offsets = swbOffsets;
            
            // Our scalefactor gain table bakes in a factor of -1/32768 relative to
            // FFmpeg's: our_gain = -(pow(2,(sf-100)/4))/32768, FFmpeg uses pow(2,(sf-100)/4).
            // This makes our spectral coefficients 32768× smaller (and negated).
            // The prediction algorithm has scale-dependent behavior:
            //   - var0 > 1 threshold check
            //   - a / var0 ratio (controls prediction aggressiveness)
            // At our small scale, prediction behaves completely differently than FFmpeg's
            // (e.g., k1 ≈ 0.95 vs k1 ≈ 0.0001 for the same audio signal).
            // Fix: scale to FFmpeg's domain before prediction, scale back after.
            // Since 32768 = 2^15, these multiplications are exact in IEEE 754 float.
            const float scaleToFfmpeg = -32768.0f;
            const float scaleFromFfmpeg = -1.0f / 32768.0f;
            
            for (int sfb = 0; sfb < predSfbMax; sfb++)
            {
                int start = offsets[sfb];
                int end = offsets[sfb + 1];
                bool used = icsInfo.PredictorPresent && icsInfo.PredictionUsed[sfb];
                
                for (int k = start; k < end; k++)
                {
                    spectral[k] *= scaleToFfmpeg;
                    Predict(ref overlap.PredictorStates[k], ref spectral[k], used);
                    spectral[k] *= scaleFromFfmpeg;
                }
            }
            
            if (icsInfo.PredictorResetGroup > 0)
                ResetPredictorGroup(overlap.PredictorStates, icsInfo.PredictorResetGroup);
        }
        else
        {
            // Short blocks: reset all predictors
            ResetAllPredictors(overlap.PredictorStates);
        }
    }
    
    #endregion

    private IcsInfo DecodeIcsInfo(ref AacBitReader reader)
    {
        var info = new IcsInfo();

        // ELD reads none of this. Not "reads it and ignores it" - the three fields are absent
        // from the syntax, so reading them would consume three bits of the band data. The window
        // sequence is always ONLY_LONG_SEQUENCE (0, the default) and the shape bit never changes,
        // because ELD has one window and it is neither of the two a shape bit selects between.
        if (!IsEldSyntax)
        {
            // ics_reserved_bit (1 bit)
            reader.ReadBit();

            // window_sequence (2 bits)
            info.WindowSequence = (WindowSequence)reader.ReadBits(2);

            // window_shape (1 bit)
            info.WindowShape = reader.ReadBit() ? 1 : 0;

            // AAC LD is defined only for ONLY_LONG_SEQUENCE. A stream that says otherwise is
            // malformed, and continuing would index a short-block band table this profile does
            // not have.
            if (config!.ObjectType == AudioObjectType.ErrorResilientAacLd &&
                info.WindowSequence != WindowSequence.OnlyLongSequence)
                throw new InvalidOperationException(
                    "AAC LD is only defined for ONLY_LONG_SEQUENCE, but window sequence "
                  + $"{info.WindowSequence} was signalled.");
        }

if (info.WindowSequence == WindowSequence.EightShortSequence)
        {
            // max_sfb (4 bits)
            info.MaxScalefactorBand = reader.ReadBits(4);
            info.SwbOffset = swbOffsetsShort;
            
            // scale_factor_grouping (7 bits) — compute window groups
            int grouping = reader.ReadBits(7);
            info.ScalefactorGrouping = grouping;
            info.NumWindowGroups = 1;
            info.WindowGroupLength[0] = 1;
            for (int i = 6; i >= 0; i--)
            {
                if ((grouping & (1 << i)) != 0)
                {
                    info.WindowGroupLength[info.NumWindowGroups - 1]++;
                }
                else
                {
                    info.NumWindowGroups++;
                    info.WindowGroupLength[info.NumWindowGroups - 1] = 1;
                }
            }
        }
        else
        {
            // max_sfb (6 bits)
            info.MaxScalefactorBand = reader.ReadBits(6);
            info.SwbOffset = swbOffsets;
            info.NumWindowGroups = 1;
            info.WindowGroupLength[0] = 1;

            // ELD has no predictor_data_present bit either, and no prediction tool to switch on.
            // LD has the bit, but FFmpeg refuses LTP in LD rather than decode it.
            if (IsEldSyntax)
                return info;

            // predictor_data_present (1 bit)
            bool predictorPresent = reader.ReadBit();
            info.PredictorPresent = predictorPresent;
            if (predictorPresent)
            {
                if (config!.ObjectType == AudioObjectType.AacMain)
                {
                    // AAC-Main prediction: predictor_reset + per-SFB prediction flags
                    // Port of FFmpeg's decode_prediction()
                    info.PredictionUsed.Clear();
                    if (reader.ReadBit()) // predictor_reset
                    {
                        info.PredictorResetGroup = reader.ReadBits(5);
                    }
                    else
                    {
                        info.PredictorResetGroup = 0;
                    }
                    int predSfbMax = PredSfbMax[config.SampleRateIndex];
                    int limit = Math.Min(info.MaxScalefactorBand, predSfbMax);
                    for (int sfb = 0; sfb < limit; sfb++)
                    {
                        info.PredictionUsed[sfb] = reader.ReadBit();
                    }
                }
                else if (config!.ObjectType == AudioObjectType.AacLongTermPrediction ||
                         config.ObjectType == AudioObjectType.ErrorResilientAacLtp)
                {
                    // ltp_data_present, then ltp_data. The C reads these inside the same
                    // predictor_present branch that AAC-Main uses for its backward predictor;
                    // leaving them unread does not just lose the prediction, it desynchronises
                    // every bit after it in the frame.
                    info.Ltp.Clear();
                    if (reader.ReadBit())
                        DecodeLtp(ref reader, ref info.Ltp, info.MaxScalefactorBand);
                }
                // For LC profile, predictor_present should not be set (spec violation)
                // We just ignore it to be resilient
            }
        }
        
        return info;
    }

    /// <summary>
    /// Decodes section (band type) data for all window groups.
    /// Port of FFmpeg's decode_band_types().
    /// Populates bandType[g * maxSfb + sfb] for all (group, sfb) pairs.
    /// </summary>
    private void DecodeSectionData(ref AacBitReader reader, in IcsInfo icsInfo)
    {
        int maxSfb = icsInfo.MaxScalefactorBand;
        bool isShortBlock = icsInfo.WindowSequence == WindowSequence.EightShortSequence;
        int sectionBits = isShortBlock ? 3 : 5;
        int sectionEscapeValue = (1 << sectionBits) - 1;
        
        // Clear band type array
        Array.Clear(bandType, 0, icsInfo.NumWindowGroups * maxSfb);
        
        for (int g = 0; g < icsInfo.NumWindowGroups; g++)
        {
            int k = 0;
            while (k < maxSfb)
            {
                if (reader.IsEmpty) return;
                
                int sectBandType = reader.ReadBits(4);
                
                // Section length
                int sectEnd = k;
                int sectLenIncr;
                do
                {
                    sectLenIncr = reader.ReadBits(sectionBits);
                    sectEnd += sectLenIncr;
                } while (sectLenIncr == sectionEscapeValue && !reader.IsEmpty);
                
                if (sectEnd > maxSfb)
                    sectEnd = maxSfb;
                
                // Prevent infinite loop if section length is 0 (corrupt data)
                if (sectEnd <= k)
                {
                    k++;
                    continue;
                }
                
                // Fill band_type for this section
                for (int sfb = k; sfb < sectEnd; sfb++)
                    bandType[g * maxSfb + sfb] = sectBandType;
                
                k = sectEnd;
            }
        }
    }

    /// <summary>
    /// Decodes scalefactor data for all window groups.
    /// Port of FFmpeg's decode_scalefactors().
    /// Uses separate accumulators for normal/noise/intensity scalefactors.
    /// Populates scalefactors[g * maxSfb + sfb] for all (group, sfb) pairs.
    /// </summary>
    private void DecodeScaleFactorData(ref AacBitReader reader, int globalGain, in IcsInfo icsInfo)
    {
        int maxSfb = icsInfo.MaxScalefactorBand;
        
        // Three separate accumulators matching FFmpeg:
        // offset[0] = normal scalefactor (starts at globalGain)
        // offset[1] = noise energy (starts at globalGain - NOISE_OFFSET(90))
        // offset[2] = intensity stereo position (starts at 0)
        int sfAccum = globalGain;
        int noiseAccum = globalGain - 90; // NOISE_OFFSET = 90
        int isAccum = 0;
        bool noiseFlag = true;
        
        for (int g = 0; g < icsInfo.NumWindowGroups; g++)
        {
            for (int sfb = 0; sfb < maxSfb; sfb++)
            {
                int idx = g * maxSfb + sfb;
                int bt = bandType[idx];
                
                switch (bt)
                {
                    case AacCodebooks.ZeroHcb:
                        scalefactors[idx] = 0;
                        break;
                        
                    case AacCodebooks.IntensityHcb:
                    case AacCodebooks.IntensityHcb2:
                        isAccum += AacScalefactorDecoder.DecodeScalefactorDelta(ref reader);
                        scalefactors[idx] = Math.Clamp(isAccum, -155, 100);
                        break;
                        
                    case AacCodebooks.NoiseHcb:
                        if (noiseFlag)
                        {
                            noiseAccum += reader.ReadBits(9) - 256; // NOISE_PRE_BITS=9, NOISE_PRE=256
                            noiseFlag = false;
                        }
                        else
                        {
                            noiseAccum += AacScalefactorDecoder.DecodeScalefactorDelta(ref reader);
                        }
                        scalefactors[idx] = Math.Clamp(noiseAccum, -100, 155);
                        break;
                        
                    default:
                        // Normal scalefactor
                        sfAccum += AacScalefactorDecoder.DecodeScalefactorDelta(ref reader);
                        scalefactors[idx] = Math.Clamp(sfAccum, 0, 255);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Decodes spectral data for all window groups.
    /// Port of FFmpeg's decode_spectrum_and_dequant() outer loop structure.
    /// Iterates over (group, sfb, window) with correct coefficient layout.
    /// </summary>
    private void DecodeSpectralData(
        ref AacBitReader reader,
        Span<float> spectral,
        in IcsInfo icsInfo,
        ReadOnlySpan<int> offsets)
    {
        int maxSfb = icsInfo.MaxScalefactorBand;
        bool isShort = icsInfo.WindowSequence == WindowSequence.EightShortSequence;
        int windowSize = isShort ? 128 : 1024;
        int numWindows = isShort ? 8 : 1;
        
        // Clear entire spectral buffer
        spectral.Slice(0, 1024).Clear();
        
        // Clear above max_sfb for each window (matching FFmpeg)
        if (maxSfb < offsets.Length - 1)
        {
            int clearFrom = offsets[maxSfb];
            int clearLen = windowSize - clearFrom;
            if (clearLen > 0)
            {
                for (int w = 0; w < numWindows; w++)
                    spectral.Slice(w * 128 + clearFrom, clearLen).Clear();
            }
        }
        
        int coefBase = 0;
        int idx = 0;
        
        for (int g = 0; g < icsInfo.NumWindowGroups; g++)
        {
            int gLen = icsInfo.WindowGroupLength[g];
            
            for (int sfb = 0; sfb < maxSfb; sfb++, idx++)
            {
                int bt = bandType[idx];
                int startCoeff = offsets[sfb];
                int offLen = offsets[sfb + 1] - startCoeff;
                
                // Skip zero, intensity, and noise bands
                if (bt == AacCodebooks.ZeroHcb)
                {
                    // Already cleared — skip bitstream too
                    continue;
                }
                if (bt == AacCodebooks.NoiseHcb)
                {
                    // Perceptual noise substitution: the band carries only an energy, and the
                    // coefficients are filled from the decoder's running LCG. The generator is
                    // shared by every channel and every frame, so the order bands are visited in
                    // has to match FFmpeg's or the sequence drifts apart.
                    int noiseSf = Math.Clamp(scalefactors[idx] + 100, 0, 255);
                    float noiseGain = AacQuantizationTables.ScalefactorGainTable[noiseSf];

                    for (int w = 0; w < gLen; w++)
                    {
                        var band = spectral.Slice(coefBase + w * 128 + startCoeff, offLen);
                        for (int k = 0; k < offLen; k++)
                        {
                            randomState = unchecked(randomState * 1664525 + 1013904223);
                            band[k] = randomState;
                        }

                        // Accumulated in float, as FFmpeg's scalarproduct_float does - summing in
                        // double here would give a different last bit.
                        float bandEnergy = 0;
                        for (int k = 0; k < offLen; k++)
                            bandEnergy += band[k] * band[k];

                        float scale = noiseGain / MathF.Sqrt(bandEnergy);
                        for (int k = 0; k < offLen; k++)
                            band[k] *= scale;
                    }
                    continue;
                }

                if (bt >= AacCodebooks.IntensityHcb2)
                {
                    // Intensity stereo carries no coefficients of its own; the band is filled in
                    // later from the other channel.
                    for (int w = 0; w < gLen; w++)
                    {
                        spectral.Slice(coefBase + w * 128 + startCoeff, offLen).Clear();
                    }
                    continue;
                }
                
                // Get gain for this band from scalefactors
                int sf = scalefactors[idx];
                float gain = AacQuantizationTables.ScalefactorGainTable[Math.Clamp(sf, 0, 255)];
                
                // Decode spectral coefficients for each window in the group
                for (int w = 0; w < gLen; w++)
                {
                    int coefOffset = coefBase + w * 128 + startCoeff;
                    
                    if (bt == AacCodebooks.EscapeHcb)
                    {
                        // Escape codebook — decode then scale by gain after
                        AacSpectralDecoder.DecodeSpectralBandEscape(
                            ref reader,
                            spectral.Slice(coefOffset, offLen),
                            bt);
                        
                        // Apply gain via vector_fmul_scalar (matching FFmpeg)
                        var slice = spectral.Slice(coefOffset, offLen);
                        for (int k = 0; k < offLen; k++)
                            slice[k] *= gain;
                    }
                    else
                    {
                        // Standard codebooks — decode with inline gain
                        AacSpectralDecoder.DecodeSpectralBandStandard(
                            ref reader,
                            spectral.Slice(coefOffset, offLen),
                            bt,
                            gain);
                    }
                }
            }
            
            coefBase += gLen * 128;
        }
    }

    private PulseData DecodePulseData(ref AacBitReader reader)
    {
        var pulse = new PulseData
        {
            Present = true,
            NumPulse = reader.ReadBits(2),
            PulseStartSfb = reader.ReadBits(6),
        };
        
        for (int i = 0; i <= pulse.NumPulse; i++)
        {
            pulse.PulseOffset[i] = reader.ReadBits(5);
            pulse.PulseAmp[i] = reader.ReadBits(4);
        }
        
        return pulse;
    }

    /// <summary>
    /// Applies pulse data to spectral coefficients.
    /// Port of FFmpeg's float pulse logic from aacdec_proc_template.c:304-326.
    /// Pulses are added in the integer quantization domain: reverse IQ → add → re-IQ.
    /// </summary>
    private void ApplyPulseData(Span<float> spectral, in PulseData pulse, in IcsInfo icsInfo, ReadOnlySpan<int> offsets)
    {
        if (!pulse.Present || offsets.Length == 0)
            return;
        
        int pulseStartSfb = pulse.PulseStartSfb;
        if (pulseStartSfb >= offsets.Length - 1)
            return;
        
        int k = offsets[pulseStartSfb];
        int sfbIdx = 0;  // will be advanced to match pulse position
        
        for (int i = 0; i <= pulse.NumPulse; i++)
        {
            k += pulse.PulseOffset[i];
            if (k >= spectral.Length)
                break;
            
            // Find which SFB this position falls in (FFmpeg: while (offsets[idx+1] <= pulse->pos[i]))
            while (sfbIdx + 1 < offsets.Length - 1 && offsets[sfbIdx + 1] <= k)
                sfbIdx++;
            
            // Skip noise and zero bands (FFmpeg: band_type[idx] != NOISE_BT && sf[idx])
            int bt = bandType[sfbIdx];
            if (bt == AacCodebooks.NoiseHcb || bt == AacCodebooks.ZeroHcb)
                continue;
            if (bt >= AacCodebooks.IntensityHcb2)
                continue;
            
            int sf = scalefactors[sfbIdx];
            float gain = AacQuantizationTables.ScalefactorGainTable[Math.Clamp(sf, 0, 255)];
            if (gain == 0)
                continue;
            
            float co = spectral[k];
            float ico = -(float)pulse.PulseAmp[i];
            
            if (co != 0)
            {
                // FFmpeg: co /= sf[idx]; (undo gain → signed IQ value)
                co /= gain;
                // FFmpeg: ico = co / sqrtf(sqrtf(fabsf(co))) + (co > 0 ? -ico : ico)
                // This reverses IQ: co / |co|^(1/4) = sign * |co|^(3/4) = sign * n
                // Then adds pulse amplitude in integer domain
                ico = co / MathF.Sqrt(MathF.Sqrt(MathF.Abs(co))) + (co > 0 ? -ico : ico);
            }
            
            // FFmpeg: coef_base[pos] = cbrtf(fabsf(ico)) * ico * sf[idx]
            // Re-IQ: cbrtf(|ico|) * ico = sign * |ico|^(4/3) (integer → IQ value)
            // Then re-apply gain
            spectral[k] = MathF.Cbrt(MathF.Abs(ico)) * ico * gain;
        }
    }

    private void SkipPulseData(ref AacBitReader reader)
    {
        int numPulse = reader.ReadBits(2);
        reader.ReadBits(6);  // pulse_start_sfb
        
        for (int i = 0; i < numPulse + 1; i++)
        {
            reader.ReadBits(5);  // pulse_offset
            reader.ReadBits(4);  // pulse_amp
        }
    }

    /// <summary>
    /// CCE scale bases indexed by 2-bit code from bitstream.
    /// Port of FFmpeg's cce_scale[] from aacdec_float.c.
    /// Values: 2^(1/8), 2^(1/4), sqrt(2), 2.
    /// </summary>
    private static readonly float[] CceScale =
    {
        1.09050773266525765921f, // 2^(1/8)
        1.18920711500272106672f, // 2^(1/4)
        1.41421356237309504880f, // sqrt(2)
        2.0f
    };

    /// <summary>
    /// Decodes a Coupling Channel Element (CCE).
    /// Port of FFmpeg's decode_cce() from aacdec_proc_template.c:357.
    /// The CCE carries shared spectral data that gets scaled and added
    /// to target channels at a specific point in the decode pipeline.
    /// </summary>
    private void DecodeCce(ref AacBitReader reader)
    {
        int instanceTag = reader.ReadBits(4);
        
        // Allocate or reuse CCE state
        ref var cce = ref cceStates[cceCount];
        cce.InstanceTag = instanceTag;
        cce.Targets ??= new CouplingTarget[8];
        cce.Gains ??= new float[16, 120];
        cce.Spectral ??= new float[1024];
        cce.Output ??= new float[1024];
        
        // coupling_point determination (FFmpeg: coup->coupling_point = 2 * get_bits1(gb))
        int couplingPointBit1 = reader.ReadBit() ? 1 : 0;
        cce.CouplingPoint = (CouplingPoint)(2 * couplingPointBit1);
        
        // num_coupled (3 bits) — number of target elements minus 1
        cce.NumCoupled = reader.ReadBits(3);
        
        int numGain = 0;
        for (int c = 0; c <= cce.NumCoupled; c++)
        {
            numGain++;
            bool isCpe = reader.ReadBit();
            cce.Targets[c].TargetType = isCpe ? (int)SyntaxElementType.ChannelPairElement 
                                               : (int)SyntaxElementType.SingleChannelElement;
            cce.Targets[c].TargetId = reader.ReadBits(4);
            
            if (isCpe)
            {
                cce.Targets[c].ChSelect = reader.ReadBits(2);
                if (cce.Targets[c].ChSelect == 3)
                    numGain++; // Separate gains for L and R
            }
            else
            {
                cce.Targets[c].ChSelect = 2; // SCE: left only (only channel)
            }
        }
        
        // Second coupling_point bit: coup->coupling_point += get_bits1(gb) || (coup->coupling_point >> 1)
        int couplingPointBit2 = reader.ReadBit() ? 1 : 0;
        int orResult = (couplingPointBit2 != 0 || ((int)cce.CouplingPoint >> 1) != 0) ? 1 : 0;
        cce.CouplingPoint = (CouplingPoint)((int)cce.CouplingPoint + orResult);
        
        // sign (1 bit) and scale (2 bits)
        int sign = reader.ReadBits(1);
        float scale = CceScale[reader.ReadBits(2)];
        
        // Decode ICS for the CCE channel (commonWindow=false, scaleFlag=false)
        cce.IcsResult = DecodeIcs(ref reader, cce.Spectral, 0, false, false);
        
        // Save CCE's band_type for dependent coupling (apply_dependent_coupling checks for ZERO_BT)
        cce.BandType ??= new int[8 * 64];
        int btLen = cce.IcsResult.IcsInfo.NumWindowGroups * cce.IcsResult.IcsInfo.MaxSfb;
        Array.Copy(bandType, cce.BandType, Math.Min(btLen, cce.BandType.Length));
        
        // Parse per-target gains
        // For independent coupling (AFTER_IMDCT): one gain per target → gain[c][0]
        // For dependent coupling: per-SFB gains → gain[c][sfb]
        for (int c = 0; c < numGain; c++)
        {
            int idx = 0;
            int cge = 1;
            int gain = 0;
            float gainCache = 1.0f; // FIXR10(1.) = 1.0f
            
            if (c > 0)
            {
                cge = cce.CouplingPoint == CouplingPoint.AfterImdct ? 1 : (reader.ReadBit() ? 1 : 0);
                int vlcDelta = cge != 0 ? AacScalefactorDecoder.DecodeScalefactorDelta(ref reader) : 0;
                gain += vlcDelta;
                gainCache = MathF.Pow(scale, -gain); // GET_GAIN(scale, gain)
            }
            
            if (cce.CouplingPoint == CouplingPoint.AfterImdct)
            {
                // Independent coupling: single gain per target
                cce.Gains[c, 0] = gainCache;
            }
            else
            {
                // Dependent coupling: per-SFB gains
                var ics = cce.IcsResult.IcsInfo;
                for (int g = 0; g < ics.NumWindowGroups; g++)
                {
                    for (int sfb = 0; sfb < ics.MaxScalefactorBand; sfb++, idx++)
                    {
                        if (bandType[idx] != 0) // != ZERO_BT
                        {
                            if (cge == 0)
                            {
                                int t = AacScalefactorDecoder.DecodeScalefactorDelta(ref reader);
                                if (t != 0)
                                {
                                    int s = 1;
                                    gain += t;
                                    int g2 = gain;
                                    if (sign != 0)
                                    {
                                        s -= 2 * (g2 & 1);
                                        g2 >>= 1;
                                    }
                                    gainCache = MathF.Pow(scale, -g2) * s;
                                }
                            }
                            cce.Gains[c, idx] = gainCache;
                        }
                    }
                }
            }
        }
        
        cce.NumGain = numGain;
        cceCount++;
        
    }
    
    /// <summary>
    /// Processes all decoded CCEs: applies TNS, IMDCT, then applies coupling to target channels.
    /// Called after all audio elements have been decoded and IMDCT'd.
    /// Port of coupling application from FFmpeg's spectral_to_sample() in aacdec.c.
    /// </summary>
    private void ProcessCouplingChannels(float[][] channelBuffers, DecodedElementInfo[] decodedElements, int elementCount)
    {
        for (int i = 0; i < cceCount; i++)
        {
            ref var cce = ref cceStates[i];
            
            // Apply TNS to CCE spectral data
            ApplyTns(cce.Spectral, in cce.IcsResult);
            
            // Apply IMDCT to get CCE time-domain output
            var overlap = GetOverlap(2, cce.InstanceTag); // elementType=2 = CCE
            filterbank.ImdctAndWindowing(
                cce.Spectral,
                cce.Output,
                overlap.PreviousSamples.AsSpan(),
                cce.IcsResult.IcsInfo.WindowSequence,
                overlap.PrevWindowSequence,
                cce.IcsResult.IcsInfo.WindowShape,
                overlap.PrevWindowShape,
                imdctBuffer);
            
            overlap.PrevWindowSequence = cce.IcsResult.IcsInfo.WindowSequence;
            overlap.PrevWindowShape = cce.IcsResult.IcsInfo.WindowShape;
            
            // Apply coupling to target channels
            if (cce.CouplingPoint == CouplingPoint.AfterImdct)
                ApplyIndependentCouplingToTargets(ref cce, channelBuffers, decodedElements, elementCount);
        }
    }
    
    /// <summary>
    /// Applies independent coupling (time-domain, after IMDCT) to all target channels.
    /// Port of FFmpeg's apply_independent_coupling() + apply_channel_coupling() routing.
    /// dest[i] += gain * cce_output[i] for 1024 samples.
    /// </summary>
    private void ApplyIndependentCouplingToTargets(
        ref CouplingChannelState cce, float[][] channelBuffers,
        DecodedElementInfo[] decodedElements, int elementCount)
    {
        int index = 0;
        
        for (int c = 0; c <= cce.NumCoupled; c++)
        {
            int targetType = cce.Targets[c].TargetType;
            int targetId = cce.Targets[c].TargetId;
            int chSelect = cce.Targets[c].ChSelect;
            
            // Find the decoded element matching this target
            int elemIdx = -1;
            for (int e = 0; e < elementCount; e++)
            {
                if (decodedElements[e].ElementType == targetType && decodedElements[e].InstanceTag == targetId)
                {
                    elemIdx = e;
                    break;
                }
            }
            
            if (elemIdx < 0)
            {
                // Target element not found — advance gain index
                index += 1 + (chSelect == 3 ? 1 : 0);
                continue;
            }
            
            ref var elem = ref decodedElements[elemIdx];
            
            // Debug: skip coupling for specific CPE tag
            bool skipCoupling = DebugSkipCouplingForCpeTag >= 0 
                && targetType == (int)SyntaxElementType.ChannelPairElement 
                && targetId == DebugSkipCouplingForCpeTag;
            
            // Apply to left channel (ch[0]) if ch_select is not "right only" (1)
            if (chSelect != 1)
            {
                float gain = cce.Gains[index, 0];
                if (!skipCoupling && elem.LeftBufferIdx >= 0 && elem.LeftBufferIdx < channelBuffers.Length)
                    ApplyIndependentCoupling(channelBuffers[elem.LeftBufferIdx].AsSpan(), cce.Output, gain);
                if (chSelect != 0)
                    index++;
            }
            
            // Apply to right channel (ch[1]) if ch_select is not "left only" (2)
            if (chSelect != 2)
            {
                float gain = cce.Gains[index, 0];
                if (!skipCoupling && elem.RightBufferIdx >= 0 && elem.RightBufferIdx < channelBuffers.Length)
                    ApplyIndependentCoupling(channelBuffers[elem.RightBufferIdx].AsSpan(), cce.Output, gain);
                index++;
            }
        }
    }
    
    /// <summary>
    /// Applies independent coupling: dest[i] += gain * src[i] for 1024 samples.
    /// Port of FFmpeg's vector_fmac_scalar() used in apply_independent_coupling().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyIndependentCoupling(Span<float> dest, float[] src, float gain)
    {
        for (int i = 0; i < 1024; i++)
            dest[i] += gain * src[i];
    }
    
    /// <summary>
    /// Two-pass phase 2: converts spectral data to time-domain samples.
    /// Processes CCE TNS first, then applies coupling and IMDCT per channel.
    /// Port of FFmpeg's spectral_to_sample() from aacdec.c:2040.
    /// Processing order: type 3(LFE) → 2(CCE) → 1(CPE) → 0(SCE).
    /// For each non-CCE element: BEFORE_TNS coupling → TNS → BETWEEN_TNS coupling → IMDCT → AFTER_IMDCT coupling.
    /// </summary>
    private void SpectralToSample(float[][] channelBuffers)
    {
        // Phase 2a: Apply TNS to all CCE spectral data (CCEs need TNS before their
        // spectral data is used for dependent coupling on target channels)
        for (int i = 0; i < cceCount; i++)
        {
            ref var cce = ref cceStates[i];
            ApplyTns(cce.Spectral, in cce.IcsResult);
        }
        
        // Phase 2b: Process each pending channel in order
        for (int p = 0; p < pendingChannelCount; p++)
        {
            ref var pending = ref pendingChannels[p];
            var spectral = pendingSpectral[p];
            
            // Only SCE/CPE/LFE get coupling applied (type <= TYPE_CPE in FFmpeg)
            if (pending.ElementType <= (int)SyntaxElementType.ChannelPairElement)
            {
                ApplyDependentCouplingToChannel(spectral, pending.ElementType,
                    pending.InstanceTag, pending.ChannelInPair, CouplingPoint.BeforeTns);
            }
            
            // Long term prediction, before TNS and before the IMDCT, as in FFmpeg's loop.
            var ltpOverlap = GetOverlap(pending.ElementType, pending.InstanceTag,
                                        pending.ChannelInPair);
            if (IsLtpObjectType &&
                pending.IcsResult.IcsInfo.PredictorPresent &&
                pending.IcsResult.IcsInfo.Ltp.Present)
            {
                ApplyLtp(spectral, in pending.IcsResult.IcsInfo, ltpOverlap, in pending.IcsResult);
            }

            // Apply TNS
            ApplyTns(spectral, in pending.IcsResult);
            
            if (pending.ElementType <= (int)SyntaxElementType.ChannelPairElement)
            {
                ApplyDependentCouplingToChannel(spectral, pending.ElementType,
                    pending.InstanceTag, pending.ChannelInPair, CouplingPoint.BetweenTnsAndImdct);
            }
            
            // IMDCT + windowing
            int overlapType = pending.ElementType;
            var overlap = GetOverlap(overlapType, pending.InstanceTag, pending.ChannelInPair);
            int outIdx = pending.OutputBufferIdx;
            var output = outIdx >= 0 && outIdx < channelBuffers.Length
                ? channelBuffers[outIdx].AsSpan()
                : scratchOutput.AsSpan();
            
            if (CoreTrace is not null)
                CoreTrace.Add(DescribeSpectrum(spectral, in pending.IcsResult.IcsInfo, overlap));

            switch (config!.ObjectType)
            {
                case AudioObjectType.ErrorResilientAacLd:
                    filterbank.ImdctAndWindowingLd(
                        spectral,
                        output,
                        overlap.PreviousSamples.AsSpan(),
                        overlap.PrevWindowShape != 0,
                        imdctBuffer);
                    break;

                case AudioObjectType.ErrorResilientAacEld:
                    // Destructive in its first argument, as FFmpeg's is: the coefficient
                    // reversal is done in place on sce->coeffs. Nothing reads them afterwards.
                    filterbank.ImdctAndWindowingEld(
                        spectral,
                        output,
                        overlap.PreviousSamples.AsSpan(),
                        config.FrameLength >> 1,
                        imdctBuffer);
                    break;

                default:
                    filterbank.ImdctAndWindowing(
                        spectral,
                        output,
                        overlap.PreviousSamples.AsSpan(),
                        pending.IcsResult.IcsInfo.WindowSequence,
                        overlap.PrevWindowSequence,
                        pending.IcsResult.IcsInfo.WindowShape,
                        overlap.PrevWindowShape,
                        imdctBuffer);
                    break;
            }
            
            // The LTP history is rolled after the IMDCT, because the row it appends is built
            // from this frame's raw transform output and the overlap that has not been emitted.
            // Unconditional for an LTP stream: the next frame can predict from this one whether
            // or not this one predicted from its predecessor.
            if (IsLtpObjectType)
            {
                filterbank.UpdateLtp(overlap.LtpState, ltpSaved,
                                     overlap.PreviousSamples, output, imdctBuffer,
                                     pending.IcsResult.IcsInfo.WindowSequence,
                                     pending.IcsResult.IcsInfo.WindowShape);
            }

            overlap.PrevWindowSequence = pending.IcsResult.IcsInfo.WindowSequence;
            overlap.PrevWindowShape = pending.IcsResult.IcsInfo.WindowShape;
        }
        
        // Phase 2c: Independent coupling (AFTER_IMDCT) — applied to time-domain output
        // Process CCE IMDCT first (only for AfterImdct CCEs)
        for (int i = 0; i < cceCount; i++)
        {
            ref var cce = ref cceStates[i];
            
            if (cce.CouplingPoint == CouplingPoint.AfterImdct)
            {
                // IMDCT the CCE itself
                var overlap = GetOverlap(2, cce.InstanceTag);
                filterbank.ImdctAndWindowing(
                    cce.Spectral,
                    cce.Output,
                    overlap.PreviousSamples.AsSpan(),
                    cce.IcsResult.IcsInfo.WindowSequence,
                    overlap.PrevWindowSequence,
                    cce.IcsResult.IcsInfo.WindowShape,
                    overlap.PrevWindowShape,
                    imdctBuffer);
                
                overlap.PrevWindowSequence = cce.IcsResult.IcsInfo.WindowSequence;
                overlap.PrevWindowShape = cce.IcsResult.IcsInfo.WindowShape;
                
                // Apply independent coupling to all targets
                ApplyIndependentCouplingToTargets(ref cce, channelBuffers, decodedElements, decodedElementCount);
            }
            else
            {
                // For dependent CCEs, still need to maintain overlap state (no IMDCT output used)
                var overlap = GetOverlap(2, cce.InstanceTag);
                overlap.PrevWindowSequence = cce.IcsResult.IcsInfo.WindowSequence;
                overlap.PrevWindowShape = cce.IcsResult.IcsInfo.WindowShape;
            }
        }
    }
    
    /// <summary>
    /// Applies dependent coupling from all matching CCEs to a single channel's spectral data.
    /// Routes coupling based on element type, instance tag, and channel position.
    /// Port of FFmpeg's apply_channel_coupling() with apply_dependent_coupling callback.
    /// The index tracking must exactly mirror FFmpeg's logic for both channels.
    /// </summary>
    private void ApplyDependentCouplingToChannel(
        float[] destSpectral, int elemType, int instanceTag, int channelInPair, CouplingPoint point)
    {
        for (int i = 0; i < cceCount; i++)
        {
            ref var cce = ref cceStates[i];
            if (cce.CouplingPoint != point)
                continue;
            
            int index = 0;
            for (int c = 0; c <= cce.NumCoupled; c++)
            {
                int targetType = cce.Targets[c].TargetType;
                int targetId = cce.Targets[c].TargetId;
                int chSelect = cce.Targets[c].ChSelect;
                
                if (targetType == elemType && targetId == instanceTag)
                {
                    // ch_select: 0=shared gain both, 1=right only, 2=left only, 3=separate gains
                    // Apply to ch[0] (left/mono) if ch_select is not "right only" (1)
                    if (chSelect != 1)
                    {
                        if (channelInPair == 0)
                            ApplyDependentCoupling(destSpectral, ref cce, index);
                        if (chSelect != 0)
                            index++;
                    }
                    // Apply to ch[1] (right) if ch_select is not "left only" (2)
                    if (chSelect != 2)
                    {
                        if (channelInPair == 1)
                            ApplyDependentCoupling(destSpectral, ref cce, index);
                        index++;
                    }
                }
                else
                {
                    // Non-matching target — advance gain index
                    index += 1 + (chSelect == 3 ? 1 : 0);
                }
            }
        }
    }
    
    /// <summary>
    /// Applies dependent coupling to spectral coefficients: dest[k] += gain[sfb] * src[k].
    /// Uses CCE's ICS (not target's) for SFB iteration.
    /// Port of FFmpeg's apply_dependent_coupling() from aacdec_float_coupling.h:42-71.
    /// </summary>
    private static void ApplyDependentCoupling(float[] dest, ref CouplingChannelState cce, int gainIndex)
    {
        ref readonly var ics = ref cce.IcsResult.IcsInfo;
        var offsets = ics.SwbOffset;
        var src = cce.Spectral;
        int idx = 0;
        int destOffset = 0;
        int srcOffset = 0;
        
        for (int g = 0; g < ics.NumWindowGroups; g++)
        {
            for (int i = 0; i < ics.MaxSfb; i++, idx++)
            {
                if (cce.BandType[idx] != 0) // 0 = ZERO_BT
                {
                    float gain = cce.Gains[gainIndex, idx];
                    for (int group = 0; group < ics.WindowGroupLength[g]; group++)
                    {
                        for (int k = offsets[i]; k < offsets[i + 1]; k++)
                        {
                            dest[destOffset + group * 128 + k] += gain * src[srcOffset + group * 128 + k];
                        }
                    }
                }
            }
            destOffset += ics.WindowGroupLength[g] * 128;
            srcOffset += ics.WindowGroupLength[g] * 128;
        }
    }
    private static void SkipGainControlData(ref AacBitReader reader, WindowSequence windowSequence)
    {
        // FFmpeg's gain_mode table: [wd_num, wd_test, aloc_size]
        int wdNum, wdTest, alocSize;
        switch (windowSequence)
        {
            case WindowSequence.OnlyLongSequence:  wdNum = 1; wdTest = 0; alocSize = 5; break;
            case WindowSequence.LongStartSequence: wdNum = 2; wdTest = 1; alocSize = 2; break;
            case WindowSequence.EightShortSequence: wdNum = 8; wdTest = 0; alocSize = 2; break;
            case WindowSequence.LongStopSequence:  wdNum = 2; wdTest = 1; alocSize = 5; break;
            default: return;
        }
        
        int maxBand = reader.ReadBits(2);
        for (int bd = 0; bd < maxBand; bd++)
        {
            for (int wd = 0; wd < wdNum; wd++)
            {
                int adjustNum = reader.ReadBits(3);
                for (int ad = 0; ad < adjustNum; ad++)
                {
                    int skipBits = 4 + ((wd == 0 && wdTest != 0) ? 4 : alocSize);
                    reader.ReadBits(skipBits);
                }
            }
        }
    }

    private void DecodeLfe(ref AacBitReader reader, Span<float> output)
    {
        int instanceTag = ReadElementInstanceTag(ref reader);
        var result = DecodeIcs(ref reader, spectralCoefficients, 0, false, false);
        
        // Per-element overlap state: LFE type = 3
        var overlap = GetOverlap(3, instanceTag);
        
        // AAC-Main prediction (LFE is always non-common-window)
        if (config!.ObjectType == AudioObjectType.AacMain)
            ApplyPrediction(spectralCoefficients, in result.IcsInfo, overlap);
        
        // Two-pass mode: store spectral data for SpectralToSample, skip TNS/IMDCT
        if (deferTnsImdct && pendingChannelCount < MaxPendingChannels)
        {
            Array.Copy(spectralCoefficients, 0, pendingSpectral[pendingChannelCount], 0, 1024);
            pendingChannels[pendingChannelCount].IcsResult = result;
            pendingChannels[pendingChannelCount].InstanceTag = instanceTag;
            pendingChannels[pendingChannelCount].ElementType = (int)SyntaxElementType.LowFrequencyEffectsElement;
            pendingChannels[pendingChannelCount].ChannelInPair = 0;
            pendingChannelCount++;
            return;
        }
        
        // Apply TNS (LFE has no MS stereo)
        ApplyTns(spectralCoefficients, in result);
        
        filterbank.ImdctAndWindowing(
            spectralCoefficients,
            output,
            overlap.PreviousSamples.AsSpan(),
            result.IcsInfo.WindowSequence,
            overlap.PrevWindowSequence,
            result.IcsInfo.WindowShape,
            overlap.PrevWindowShape,
            imdctBuffer);
        
        overlap.PrevWindowSequence = result.IcsInfo.WindowSequence;
        overlap.PrevWindowShape = result.IcsInfo.WindowShape;
    }

    private void SkipDse(ref AacBitReader reader)
    {
        int instanceTag = reader.ReadBits(4);
        bool dataByteAlign = reader.ReadBit();
        int count = reader.ReadBits(8);
        if (count == 255)
            count += reader.ReadBits(8);
        
        if (dataByteAlign)
            reader.AlignToByte();
        
        // Skip count bytes from current bit position (no forced alignment)
        reader.SkipBits(count * 8);
    }

    private void SkipPce(ref AacBitReader reader)
    {
        // Program config element (ISO 14496-3 Table 4.2)
        // Port of FFmpeg's decode_pce() — we parse enough to skip correctly
        // Note: element_instance_tag (4 bits) is the first field we read here
        // (element_type was already read by the caller's switch)
        reader.ReadBits(4);  // element_instance_tag
        reader.ReadBits(2);  // object_type
        reader.ReadBits(4);  // sampling_frequency_index
        int numFront = reader.ReadBits(4);
        int numSide = reader.ReadBits(4);
        int numBack = reader.ReadBits(4);
        int numLfe = reader.ReadBits(2);
        int numAssocData = reader.ReadBits(3);
        int numCcElements = reader.ReadBits(4);
        
        // Mono mixdown
        if (reader.ReadBit())
            reader.ReadBits(4); // mono_mixdown_tag
        
        // Stereo mixdown
        if (reader.ReadBit())
            reader.ReadBits(4); // stereo_mixdown_tag
        
        // Matrix mixdown
        if (reader.ReadBit())
            reader.ReadBits(3); // mixdown_coeff_index + pseudo_surround
        
        // Channel element descriptors: 5 bits each (is_cpe/is_ind_sw + tag)
        // Front, side, back channels: 1 bit (CPE flag) + 4 bits (tag) = 5 bits each
        for (int i = 0; i < numFront + numSide + numBack; i++)
            reader.ReadBits(5);
        
        // LFE elements: 4 bits each (tag only, no CPE flag)
        for (int i = 0; i < numLfe; i++)
            reader.ReadBits(4);
        
        // Associated data elements: 4 bits each
        for (int i = 0; i < numAssocData; i++)
            reader.ReadBits(4);
        
        // CC elements: 5 bits each (is_ind_sw + tag)
        for (int i = 0; i < numCcElements; i++)
            reader.ReadBits(5);
        
        // Byte alignment relative to frame start (reference position = 0)
        // FFmpeg: relative_align_get_bits(gb, payload_alignment) where payload_alignment = 0
        // n = (0 - bits_count) & 7
        int bitsRead = reader.BitsRead;
        int alignment = (8 - (bitsRead & 7)) & 7;
        if (alignment > 0)
            reader.ReadBits(alignment);
        
        // Comment field
        int commentBytes = reader.ReadBits(8);
        reader.SkipBytes(commentBytes);
    }

    /// <summary>
    /// Reads a fill element. Most carry nothing of interest, but HE-AAC hides its Spectral Band
    /// Replication payload here - skipping it wholesale is what leaves an HE-AAC stream decoding as
    /// its core only, at half the sample rate.
    /// </summary>
    /// <summary>
    /// The element an SBR fill payload belongs to - the one immediately before it. SBR describes a
    /// single channel or a channel pair, and the payload carries no indication of which.
    /// </summary>
    private SbrElementType lastSbrElement = SbrElementType.SingleChannel;

    /// <summary>
    /// The identity of that element: FFmpeg reaches an element's SBR state through
    /// <c>che[syntax element type][instance tag]</c>, so both halves are needed.
    /// </summary>
    private SyntaxElementType lastSbrSyntaxElement = SyntaxElementType.SingleChannelElement;
    private int lastSbrElementTag;

    /// <summary>
    /// The first element's SBR decoder, and the one every diagnostic accessor reports.
    /// </summary>
    private readonly SbrDecoder sbr = new();

    /// <summary>
    /// One SBR decoder per channel element, because SBR state is per element and not per stream.
    /// FFmpeg allocates a <c>SpectralBandReplication</c> inside each <c>ChannelElement</c>
    /// (<c>ExtChannelElement</c>) and reaches it through <c>che[type][instance tag]</c>, so a 5.1
    /// stream carries three independent SBR states with their own headers, envelope history and
    /// filterbank overlap. Sharing one across elements is wrong twice over: the later elements
    /// read the first one's tables, and - because a decoder handles at most a channel pair - the
    /// channels past the second were never upsampled at all.
    /// </summary>
    private readonly SbrDecoder?[] sbrByElement = new SbrDecoder?[MaxSbrElements];

    /// <summary><c>(type &lt;&lt; 8) | instance tag</c> for each entry in <see cref="sbrByElement"/>.</summary>
    private readonly int[] sbrElementKeys = new int[MaxSbrElements];
    private int sbrElementCount;

    /// <summary>
    /// Every <c>(syntax element type, instance tag)</c> an AAC frame can name: the type is three
    /// bits and the tag four, and only the three audio element types reach here.
    /// </summary>
    private const int MaxSbrElements = 8 * 16;

    /// <summary>
    /// The SBR decoder belonging to one channel element, created the first time that element is
    /// seen. Nothing is allocated per frame: the pool only grows when a new element appears.
    /// </summary>
    private SbrDecoder SbrForElement(SyntaxElementType type, int instanceTag)
    {
        int key = ((int)type << 8) | (instanceTag & 0xFF);

        for (int i = 0; i < sbrElementCount; i++)
            if (sbrElementKeys[i] == key)
                return sbrByElement[i]!;

        // The key space is bounded - a 3-bit syntax element type and a 4-bit instance tag - so
        // the pool is sized to hold all of it and this is unreachable for a well-formed stream.
        // A malformed one must not throw here: it is ordinary bad input, and the decoder's answer
        // to that is to carry on and let the gate measure the damage.
        if (sbrElementCount >= MaxSbrElements)
            return sbr;

        // The first element reuses the decoder the diagnostics point at, so a mono or stereo
        // stream behaves exactly as it did before there was a pool.
        SbrDecoder created = sbrElementCount == 0 ? sbr : new SbrDecoder();
        created.ElementKey = key;

        // Configure it on sight, not on its first payload. FFmpeg initialises every element's
        // context when the element is created, and then runs ff_aac_sbr_apply on every element
        // whenever the configuration signals SBR - so an element that never carries an SBR
        // payload at all, an LFE above all, still goes through the filterbanks in pure
        // upsampling mode and still comes out at twice the core rate. Leaving it unconfigured
        // makes Apply decline, and that channel alone stays at the core rate while the rest of
        // the frame is upsampled around it.
        if (config is not null)
            created.Configure(CoreSampleRate, config.ExtensionSampleRate);
        sbrElementKeys[sbrElementCount] = key;
        sbrByElement[sbrElementCount++] = created;
        return created;
    }

    /// <summary><c>m4ac.sbr &gt; 0</c>: the configuration says this stream carries SBR.</summary>
    private bool sbrSignalled;

    /// <summary>
    /// How the SBR payload parser is faring on this stream: payloads seen, payloads that parsed
    /// cleanly, and payloads whose consumed length matched the length the fill element declared.
    /// The third is the strong check - it only comes out right if every field was read in the
    /// right order and with the right width.
    /// </summary>
    public (int Seen, int Parsed, int ExactLength) SbrPayloadStats =>
        (sbr.PayloadsSeen, sbr.PayloadsParsed, sbr.PayloadsExactLength);

    /// <summary>
    /// Payloads that arrived before any SBR header, which a live stream joined mid-broadcast always
    /// produces. They are skipped, not failures.
    /// </summary>
    public int SbrPayloadsBeforeHeader => sbr.PayloadsBeforeHeader;

    /// <summary>Why SBR was turned off, or null if it is still running.</summary>
    public string? SbrError => sbr.LastError;

    /// <summary>Payloads whose consumed length did not match the declared one.</summary>
    public System.Collections.Generic.IReadOnlyList<(int Index, int Declared, int Consumed, bool Started)>
        SbrLengthMismatches => sbr.LengthMismatches;

    /// <summary>
    /// Whether to apply Spectral Band Replication. On by default: an HE-AAC stream decoded without
    /// it is band-limited and runs at half the rate.
    /// </summary>
    /// <remarks>
    /// Checked whole-file against ffmpeg on six HE-AAC streams: sample counts identical, and
    /// between 90.6% and 99.9% of output samples bit-exact (63 to 109 dB SNR). What differs is not
    /// a reconstruction error - the spectral coefficients entering the IMDCT agree with FFmpeg, and
    /// so does the synthesis input. It is float rounding in the IMDCT, which SBR's inverse filter
    /// amplifies; FFmpeg warns in that routine that it is not numerically stable, and closing the
    /// gap means matching av_tx's FFT rounding rather than fixing anything in SBR.
    /// </remarks>
    public bool EnableSbr { get; set; } = true;

    /// <summary>True when a usable SBR header has been seen.</summary>
    public bool SbrReady => sbr.Ready;

    /// <summary>
    /// True once a Parametric Stereo payload has been seen in this stream. PS is not implemented,
    /// so such a stream decodes as its mono core rather than the stereo pair FFmpeg produces.
    /// </summary>
    public bool SawParametricStereo => sbr.SawParametricStereo;

    /// <summary>The Parametric Stereo parameters of the most recent payload, for diagnosis.</summary>
    public string PsState => sbr.DescribePs();

    /// <summary>Bit set of SBR extension ids seen, for diagnosis.</summary>
    public uint SbrExtensionsSeen => sbr.ExtensionsSeen;

    /// <summary>The derived SBR band layout, for diagnosis.</summary>
    public string SbrTables => sbr.DescribeTables();

    /// <summary>
    /// Where to write the per-frame SBR stage dump that is diffed against a patched FFmpeg, or
    /// null for no dump. See <see cref="Sbr.SbrDecoder.StageDump"/>.
    /// </summary>
    public static System.IO.TextWriter? SbrStageDump
    {
        get => Sbr.SbrDecoder.StageDump;
        set => Sbr.SbrDecoder.StageDump = value;
    }

    /// <summary>
    /// Collects per-frame SBR fingerprints in the same form the instrumented FFmpeg build emits.
    /// Set before decoding to enable; see scripts/reference.
    /// </summary>
    public System.Collections.Generic.List<string>? SbrFrameTrace
    {
        get => sbr.FrameTrace;
        set => sbr.FrameTrace = value;
    }

    /// <summary>
    /// State of the linear congruential generator that fills perceptual-noise-substitution bands.
    /// One generator for the whole decoder, seeded once, exactly as FFmpeg does.
    /// </summary>
    private int randomState = 0x1f2e3d4c;

    private int traceSpectrumIndex;

    /// <summary>Fingerprints the spectral coefficients entering the IMDCT, and the window state.</summary>
    private string DescribeSpectrum(ReadOnlySpan<float> spectral, in IcsInfo icsInfo, ElementOverlapState overlap)
    {
        double sum = 0, magnitude = 0;
        for (int q = 0; q < 1024 && q < spectral.Length; q++)
        {
            sum += spectral[q] * (q + 1);
            magnitude += Math.Abs(spectral[q]);
        }

        return $"MKIMDCT n={traceSpectrumIndex++} seq={(int)icsInfo.WindowSequence} " +
               $"shape={icsInfo.WindowShape} prevshape={overlap.PrevWindowShape} " +
               $"sum={sum:F5} mag={magnitude:F5}";
    }

    /// <summary>
    /// Collects a fingerprint of the core decoder's output for each frame, in the form the
    /// instrumented FFmpeg build emits, so the two can be compared before SBR touches them.
    /// </summary>
    public System.Collections.Generic.List<string>? CoreTrace { get; set; }

    /// <summary>Reconstructed sample rate: twice the core's, once SBR is running.</summary>
    public int SbrSampleRate => sbr.SampleRate;

    private void SkipFil(ref AacBitReader reader, SbrElementType element)
    {
        int count = reader.ReadBits(4);
        if (count == 15)
            count += reader.ReadBits(8) - 1;

        if (count <= 0)
            return;

        int extensionType = reader.PeekBits(4);
        if (extensionType is SbrDecoder.ExtensionSbrData or SbrDecoder.ExtensionSbrDataCrc)
        {
            reader.SkipBits(4); // bs_extension_type, already peeked

            int bitPosition = reader.BitsRead;
            var payload = reader.Data[(bitPosition >> 3)..];
            var sbrReader = new SbrBitReader(payload);
            sbrReader.Skip(bitPosition & 7);

            // Implicit signalling: a mono stream whose configuration never mentioned PS, but
            // which carries SBR, is HE-AACv2 and decodes to two channels. FFmpeg makes this
            // decision here, at the first SBR payload, and not on seeing PS data - a mono
            // HE-AAC stream with no PS payload still produces two channels, by copying.
            // FFmpeg tests avctx->ch_layout.nb_channels, which is the channel count of the
            // applied configuration - so for channelConfig 0 it is the program config element's
            // count, not the zero the channel configuration field carries.
            int configuredChannels = ChannelCount > 0
                ? ChannelCount
                : config!.Pce?.TotalChannels ?? 0;
            // The payload belongs to the element immediately before it, and that element's SBR
            // state is its own.
            SbrDecoder target = SbrForElement(lastSbrSyntaxElement, lastSbrElementTag);

            if (target.PsMode == -1 && configuredChannels == 1)
                target.PsMode = 1;

            // Configure from the core rate: SampleRate now reports the doubled one once SBR runs.
            // A stream whose configuration never mentioned SBR still gets configured here, which
            // is FFmpeg's implicit path.
            target.Configure(CoreSampleRate, config!.ExtensionSampleRate);
            sbrSignalled = true;
            // The return value is only a diagnostic now; the decoder records its own mismatches
            // and turns itself off if it over-read.
            target.DecodeExtension(
                ref sbrReader, count, extensionType == SbrDecoder.ExtensionSbrDataCrc, element);

            // The element loop's position comes from the declared length and nothing else. The C
            // does this before it parses anything - "skip_bits_long(gb_host, cnt*8 - 4)" on the
            // host reader, with the SBR parse running on a copy - and returns cnt unconditionally.
            // Deriving the skip from what the SBR parser consumed instead lets an over-read walk
            // the element loop off the end of the payload: on a 5.1 HE-AAC frame that invented a
            // phantom SCE with instance tag 3, which then got an SBR context of its own. The four
            // bits of the extension type are already gone.
            reader.SkipBits(count * 8 - 4);
            return;
        }

        // Anything else is skipped whole, without byte alignment, as FFmpeg does.
        reader.SkipBits(count * 8);
    }

    /// <summary>
    /// Parses remaining non-audio elements after all audio elements have been decoded.
    /// Unlike SkipToEnd, this properly decodes CCE elements instead of skipping them.
    /// </summary>
    private void ParseRemainingElements(ref AacBitReader reader)
    {
        while (reader.BitsRemaining >= 3)
        {
            int elementType = reader.ReadBits(3);
            switch ((SyntaxElementType)elementType)
            {
                case SyntaxElementType.End:
                    return;
                case SyntaxElementType.CouplingChannelElement:
                    DecodeCce(ref reader);
                    break;
                case SyntaxElementType.DataStreamElement:
                    SkipDse(ref reader);
                    break;
                case SyntaxElementType.FillElement:
                    SkipFil(ref reader, lastSbrElement);
                    break;
                case SyntaxElementType.ProgramConfigElement:
                    SkipPce(ref reader);
                    break;
                default:
                    // Unexpected audio element after expected count reached — stop
                    return;
            }
        }
    }

    /// <summary>
    /// Skips remaining non-audio elements (DSE, FIL, PCE, CCE) until END or exhaustion.
    /// Used when PCE defines the exact element count and all audio elements have been decoded.
    /// </summary>
    private void SkipToEnd(ref AacBitReader reader)
    {
        while (reader.BitsRemaining >= 3)
        {
            int elementType = reader.ReadBits(3);
            switch ((SyntaxElementType)elementType)
            {
                case SyntaxElementType.End:
                    return;
                case SyntaxElementType.DataStreamElement:
                    SkipDse(ref reader);
                    break;
                case SyntaxElementType.FillElement:
                    SkipFil(ref reader, lastSbrElement);
                    break;
                case SyntaxElementType.ProgramConfigElement:
                    SkipPce(ref reader);
                    break;
                default:
                    // Unexpected audio element after expected count reached — stop
                    return;
            }
        }
    }

    #endregion

    #region Channel Routing
    
    /// <summary>
    /// Channel routing tables matching FFmpeg's sniff_channel_order output.
    /// Each entry maps an element index to output channel indices.
    /// For CPE elements, two consecutive indices are given (left, right).
    /// For SCE/LFE elements, a single index is given.
    /// 
    /// FFmpeg sorts channels by AV_CHAN position: FL(0), FR(1), FC(2), LFE(3), BL(4), BR(5).
    /// This reorders from bitstream element order to FFmpeg's output order.
    /// </summary>
    /// <remarks>
    /// Standard element orders (from ISO 14496-3 Table 1.19):
    ///   config=3: SCE, CPE                → FFmpeg output: CPE.L(FL), CPE.R(FR), SCE(FC)
    ///   config=4: SCE, CPE, SCE           → FFmpeg output: CPE.L(FL), CPE.R(FR), SCE0(FC), SCE1(BC)
    ///   config=5: SCE, CPE, CPE           → FFmpeg output: CPE0.L(FL), CPE0.R(FR), SCE(FC), CPE1.L(BL), CPE1.R(BR)
    ///   config=6: SCE, CPE, CPE, LFE      → FFmpeg output: CPE0.L(FL), CPE0.R(FR), SCE(FC), LFE, CPE1.L(BL), CPE1.R(BR)
    ///   config=7: SCE, CPE, CPE, CPE, LFE → FFmpeg output: CPE0.L(FL), CPE0.R(FR), SCE(FC), LFE, CPE1.L(FLc), CPE1.R(FRc), CPE2.L(BL), CPE2.R(BR)
    /// </remarks>
    private static readonly int[][] Routing3 = { new[]{2}, new[]{0, 1} };
    private static readonly int[][] Routing4 = { new[]{2}, new[]{0, 1}, new[]{3} };
    private static readonly int[][] Routing5 = { new[]{2}, new[]{0, 1}, new[]{3, 4} };
    private static readonly int[][] Routing6 = { new[]{2}, new[]{0, 1}, new[]{4, 5}, new[]{3} };
    private static readonly int[][] Routing7 = { new[]{2}, new[]{0, 1}, new[]{4, 5}, new[]{6, 7}, new[]{3} };
    
    /// <summary>
    /// Gets channel routing table for a channel configuration.
    /// Returns null for configs that don't need reordering (mono, stereo, unknown).
    /// </summary>
    /// <summary>
    /// The element's identity: read from the bitstream in a normal frame, taken from the channel
    /// configuration in an error-resilient one.
    /// </summary>
    /// <remarks>
    /// An ER frame has no element headers to find elements by, so the identity cannot come from
    /// the bitstream - <c>aac_decode_er_frame</c> walks the channel configuration table and
    /// <c>skip_bits(gb, 4)</c>s the tag field without using it, and ELD does not even send those
    /// four bits. Using the transmitted value as the identity would work on a well-formed stream
    /// and silently give two elements the same overlap state on one that disagreed with its own
    /// configuration.
    /// </remarks>
    private int ReadElementInstanceTag(ref AacBitReader reader)
    {
        if (erElementTag < 0)
            return reader.ReadBits(4);

        if (!IsEldSyntax)
            reader.ReadBits(4);   // present, and deliberately discarded

        return erElementTag;
    }

    /// <summary>
    /// The element identity for the element now being decoded in an error-resilient frame, or -1
    /// when this is an ordinary frame whose elements carry their own tags.
    /// </summary>
    private int erElementTag = -1;

    /// <summary>
    /// <c>ff_tags_per_config</c>: how many elements an error-resilient frame of each channel
    /// configuration carries. Zero means the configuration names no fixed element list, which
    /// for an ER frame means it cannot be decoded.
    /// </summary>
    private static ReadOnlySpan<byte> ErTagsPerConfig =>
        [0, 1, 1, 2, 3, 3, 4, 5, 0, 0, 0, 5, 5, 16, 5, 0];

    /// <summary>
    /// <c>ff_aac_channel_layout_map</c>, as the (element type, element id) pairs an
    /// error-resilient frame carries, for the configurations an ER frame may use.
    /// </summary>
    /// <remarks>
    /// An ER frame has no per-element headers: the configuration IS the element list, in order,
    /// and a decoder that guessed from what it found would have nothing to guess from. Only the
    /// entries FFmpeg accepts for an ER frame are here - it refuses configurations 8, 9, 10 and
    /// 13 and above - and the absent ones are an exception rather than a silently empty list.
    /// </remarks>
    private static readonly (SyntaxElementType Type, int Id)[][] ErChannelLayoutMap =
    [
        /*  1 */ [(SyntaxElementType.SingleChannelElement, 0)],
        /*  2 */ [(SyntaxElementType.ChannelPairElement, 0)],
        /*  3 */ [(SyntaxElementType.SingleChannelElement, 0),
                  (SyntaxElementType.ChannelPairElement, 0)],
        /*  4 */ [(SyntaxElementType.SingleChannelElement, 0),
                  (SyntaxElementType.ChannelPairElement, 0),
                  (SyntaxElementType.SingleChannelElement, 1)],
        /*  5 */ [(SyntaxElementType.SingleChannelElement, 0),
                  (SyntaxElementType.ChannelPairElement, 0),
                  (SyntaxElementType.ChannelPairElement, 1)],
        /*  6 */ [(SyntaxElementType.SingleChannelElement, 0),
                  (SyntaxElementType.ChannelPairElement, 0),
                  (SyntaxElementType.ChannelPairElement, 1),
                  (SyntaxElementType.LowFrequencyEffectsElement, 0)],
        /*  7 */ [(SyntaxElementType.SingleChannelElement, 0),
                  (SyntaxElementType.ChannelPairElement, 0),
                  (SyntaxElementType.ChannelPairElement, 1),
                  (SyntaxElementType.ChannelPairElement, 2),
                  (SyntaxElementType.LowFrequencyEffectsElement, 0)],
        /* 11 */ [(SyntaxElementType.SingleChannelElement, 0),
                  (SyntaxElementType.ChannelPairElement, 0),
                  (SyntaxElementType.ChannelPairElement, 1),
                  (SyntaxElementType.SingleChannelElement, 1),
                  (SyntaxElementType.LowFrequencyEffectsElement, 0)],
        /* 12 */ [(SyntaxElementType.SingleChannelElement, 0),
                  (SyntaxElementType.ChannelPairElement, 0),
                  (SyntaxElementType.ChannelPairElement, 1),
                  (SyntaxElementType.ChannelPairElement, 2),
                  (SyntaxElementType.LowFrequencyEffectsElement, 0)],
    ];

    private static (SyntaxElementType Type, int Id)[] ErLayoutFor(int channelConfig) =>
        channelConfig switch
        {
            >= 1 and <= 7 => ErChannelLayoutMap[channelConfig - 1],
            11 => ErChannelLayoutMap[7],
            12 => ErChannelLayoutMap[8],
            _ => throw new InvalidOperationException(
                $"Channel configuration {channelConfig} has no error-resilient element layout. "
              + "FFmpeg refuses 0, 8, 9, 10 and 13 upwards for an ER frame, and so does this."),
        };

    /// <summary>
    /// <c>aac_decode_er_frame</c>: one error-resilient frame, for the low-delay object types.
    /// </summary>
    /// <remarks>
    /// Nothing like the ordinary frame loop, and deliberately so. An error-resilient frame has
    /// no three-bit element type codes, no instance tags to match on, no fill or data stream
    /// elements and no TYPE_END - the whole point of the format is that a decoder which loses
    /// synchronisation can still tell where it is, which it can only do if the element list is
    /// known in advance. So the list comes from the channel configuration and the frame ends
    /// when that list is exhausted, with whatever bits remain discarded.
    /// </remarks>
    private int DecodeErFrame(ReadOnlySpan<byte> frameData, float[][] channelBuffers,
                              out int channelCount)
    {
        var reader = new AacBitReader(frameData);
        var layout = ErLayoutFor((int)config!.ChannelConfiguration);
        int tags = ErTagsPerConfig[(int)config.ChannelConfiguration];
        var routing = GetChannelRouting(config.ChannelConfiguration);

        cceCount = 0;
        decodedElementCount = 0;
        deferTnsImdct = true;
        pendingChannelCount = 0;

        int totalChannels = 0;

        try
        {
            for (int i = 0; i < tags && i < layout.Length; i++)
            {
                (SyntaxElementType type, int id) = layout[i];
                erElementTag = id;

                switch (type)
                {
                    case SyntaxElementType.SingleChannelElement:
                    case SyntaxElementType.LowFrequencyEffectsElement:
                    {
                        int outCh = GetRoutedChannel(routing, i, 0, totalChannels);
                        var target = outCh < channelBuffers.Length
                            ? channelBuffers[outCh].AsSpan()
                            : scratchOutput.AsSpan();
                        int pendingBefore = pendingChannelCount;

                        if (type == SyntaxElementType.SingleChannelElement)
                            DecodeSceSingleChannel(ref reader, target);
                        else
                            DecodeLfe(ref reader, target);

                        if (pendingChannelCount > pendingBefore)
                            pendingChannels[pendingBefore].OutputBufferIdx = outCh;
                        totalChannels++;
                        break;
                    }

                    case SyntaxElementType.ChannelPairElement:
                    {
                        int outChL = GetRoutedChannel(routing, i, 0, totalChannels);
                        int outChR = GetRoutedChannel(routing, i, 1, totalChannels + 1);
                        var left = outChL < channelBuffers.Length
                            ? channelBuffers[outChL].AsSpan()
                            : scratchOutput.AsSpan();
                        var right = outChR < channelBuffers.Length
                            ? channelBuffers[outChR].AsSpan()
                            : scratchOutput.AsSpan();
                        int pendingBefore = pendingChannelCount;

                        DecodeCpeChannelPair(ref reader, left, right);

                        if (pendingChannelCount > pendingBefore)
                            pendingChannels[pendingBefore].OutputBufferIdx = outChL;
                        if (pendingChannelCount > pendingBefore + 1)
                            pendingChannels[pendingBefore + 1].OutputBufferIdx = outChR;
                        totalChannels += 2;
                        break;
                    }

                    default:
                        throw new InvalidOperationException(
                            $"Element type {type} cannot appear in an error-resilient frame.");
                }
            }
        }
        finally
        {
            erElementTag = -1;
            deferTnsImdct = false;
        }

        SpectralToSample(channelBuffers);

        channelCount = totalChannels;
        if (totalChannels > 0)
            lastFrameChannels = totalChannels;
        frameCount++;

        return SamplesPerFrame;
    }

    private static int[][]? GetChannelRouting(ChannelConfiguration channelConfig) => channelConfig switch
    {
        ChannelConfiguration.ThreeChannels => Routing3,
        ChannelConfiguration.FourChannels => Routing4,
        ChannelConfiguration.FiveChannels => Routing5,
        ChannelConfiguration.FivePointOne => Routing6,
        ChannelConfiguration.SevenPointOne => Routing7,
        _ => null // Mono, stereo, and unknown use sequential order
    };
    
    /// <summary>
    /// Gets the output channel index for a given element, using routing table if available.
    /// Falls back to sequential assignment when no routing is defined.
    /// </summary>
    /// <param name="routing">Channel routing table (null = sequential).</param>
    /// <param name="elementIndex">Index of the current element (SCE/CPE/LFE, not counting DSE/FIL/PCE).</param>
    /// <param name="subChannel">0 for SCE/LFE/CPE-left, 1 for CPE-right.</param>
    /// <param name="sequentialChannel">Fallback sequential channel index.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// <summary>
    /// Where this element sits in the program config element's list, or -1 when there is no PCE
    /// or the element is not in it.
    /// </summary>
    /// <remarks>
    /// For a channelConfig of 0 the configuration is the PCE, and FFmpeg maps elements "solely
    /// based on tags": <c>ff_aac_get_che</c> returns <c>tag_che_map[type][elem_id]</c>, a map
    /// built from the PCE's element list in PCE order. The bitstream is free to send the
    /// elements in another order, and conformance streams do - al17_44 carries SCE 15 before
    /// SCE 0 while its PCE lists SCE 0 first. Routing by order of appearance puts those two
    /// channels the wrong way round, which is inaudible whenever the two channels happen to
    /// carry similar content and unmistakable when they do not.
    /// </remarks>
    private int PceElementIndex(SyntaxElementType type, int instanceTag)
    {
        var elements = config?.Pce?.Elements;
        if (elements is null) return -1;

        for (int i = 0; i < elements.Length; i++)
            if (elements[i].Type == type && elements[i].InstanceTag == instanceTag)
                return i;

        return -1;
    }

    /// <summary>
    /// The first output channel of the PCE's element <paramref name="pceIndex"/>, counting the
    /// channels of the elements before it. This is what <c>ff_aac_output_configure</c> does when
    /// it walks the layout map and hands each element the next free output buffers.
    /// </summary>
    /// <summary>
    /// The first output channel the program config element's <paramref name="pceIndex"/>'th
    /// element feeds.
    /// </summary>
    /// <remarks>
    /// This used to be the running channel count in PCE order, which assumes the elements come
    /// out in the order they were declared. They do not: FFmpeg assigns each element an AV channel
    /// position from its position group and then sorts by it, so a 5.1 stream declaring three
    /// front elements emits its second channel pair before its first. See
    /// <see cref="AacChannelOrder"/>. The order is worked out once, when the configuration is
    /// applied, and nothing is computed per frame.
    /// </remarks>
    private int PceChannelOffset(int pceIndex)
    {
        if (pceOutputChannel is not null)
            return pceIndex < pceOutputChannel.Length ? pceOutputChannel[pceIndex] : 0;

        var elements = config?.Pce?.Elements;
        if (elements is null) return 0;

        int channels = 0;
        for (int i = 0; i < pceIndex && i < elements.Length; i++)
            channels += elements[i].Type == SyntaxElementType.ChannelPairElement ? 2 : 1;

        return channels;
    }

    /// <summary>
    /// First output channel per program-config element index, or null when there is no PCE or its
    /// layout could not be worked out and the declared order is the best available answer.
    /// </summary>
    private int[]? pceOutputChannel;

    /// <summary>
    /// Works out the output channel order from the program config element, once per configuration.
    /// </summary>
    private void BuildPceChannelOrder()
    {
        pceOutputChannel = null;
        var pce = config?.Pce;
        if (pce is null || pce.Elements.Length == 0) return;

        // The PCE lists its elements front, then side, then back, then LFE; the group is what
        // decides an element's position, so it has to be carried alongside the element.
        var map = new AacChannelOrder.Entry[pce.Elements.Length];
        int at = 0;
        for (int i = 0; i < pce.Elements.Length; i++)
        {
            AacChannelOrder.Position group;
            if (i < pce.NumFront) group = AacChannelOrder.Position.Front;
            else if (i < pce.NumFront + pce.NumSide) group = AacChannelOrder.Position.Side;
            else if (i < pce.NumFront + pce.NumSide + pce.NumBack) group = AacChannelOrder.Position.Back;
            else group = AacChannelOrder.Position.Lfe;

            map[at++] = new AacChannelOrder.Entry
            {
                Type = pce.Elements[i].Type,
                InstanceTag = pce.Elements[i].InstanceTag,
                Group = group,
            };
        }

        AacChannelOrder.Entry[]? order = AacChannelOrder.SniffChannelOrder(map, at);
        if (order is null) return;

        // Walk the sorted order handing out consecutive output channels, then record where each
        // element landed against its index in the PCE as declared.
        var offsets = new int[pce.Elements.Length];
        int channel = 0;
        foreach (AacChannelOrder.Entry entry in order)
        {
            for (int i = 0; i < pce.Elements.Length; i++)
                if (pce.Elements[i].Type == entry.Type &&
                    pce.Elements[i].InstanceTag == entry.InstanceTag)
                {
                    offsets[i] = channel;
                    break;
                }

            channel += entry.Type == SyntaxElementType.ChannelPairElement ? 2 : 1;
        }

        pceOutputChannel = offsets;
    }

    private static int GetRoutedChannel(int[][]? routing, int elementIndex, int subChannel, int sequentialChannel)
    {
        if (routing != null && elementIndex < routing.Length)
        {
            var elementRouting = routing[elementIndex];
            return subChannel < elementRouting.Length ? elementRouting[subChannel] : sequentialChannel;
        }
        return sequentialChannel;
    }
    
    /// <summary>
    /// Detects channel routing from element layout for channelConfig=0 streams.
    /// Matches FFmpeg's sniff_channel_order which sorts: front pairs first, then center, then LFE, then back.
    /// </summary>
    private static int[][]? DetectRoutingFromElements(int sceCount, int cpeCount, int lfeCount)
    {
        int totalCh = sceCount + cpeCount * 2 + lfeCount;
        
        // Match against standard layouts (element order: SCE, CPE, [CPE], [LFE])
        return (sceCount, cpeCount, lfeCount) switch
        {
            (1, 1, 0) when totalCh == 3 => Routing3,  // SCE + CPE = 3.0
            (2, 1, 0) when totalCh == 4 => Routing4,  // SCE + CPE + SCE = 4.0
            (1, 2, 0) when totalCh == 5 => Routing5,  // SCE + CPE + CPE = 5.0
            (1, 2, 1) when totalCh == 6 => Routing6,  // SCE + CPE + CPE + LFE = 5.1
            (1, 3, 1) when totalCh == 8 => Routing7,  // SCE + CPE + CPE + CPE + LFE = 7.1
            _ => null  // Unknown layout, use bitstream order
        };
    }
    
    /// <summary>
    /// Reorders the first frame's channel data from bitstream order to FFmpeg output order.
    /// Called once when routing is first detected for channelConfig=0 streams.
    /// Uses a temp buffer to swap without overwriting.
    /// </summary>
    private void ReorderFirstFrame(float[][] channelBuffers, int totalChannels, int sceCount, int cpeCount, int lfeCount)
    {
        // Build the reorder map: reorder[outputCh] = bitstreamCh
        int[] reorder;
        if (sceCount == 1 && cpeCount == 1 && lfeCount == 0 && totalChannels == 3)
            reorder = new[] { 1, 2, 0 };  // SCE,CPE.L,CPE.R → CPE.L,CPE.R,SCE
        else if (sceCount == 1 && cpeCount == 2 && lfeCount == 1 && totalChannels == 6)
            reorder = new[] { 1, 2, 0, 5, 3, 4 };  // SCE,CPE0.L,CPE0.R,CPE1.L,CPE1.R,LFE → CPE0.L,CPE0.R,SCE,LFE,CPE1.L,CPE1.R
        else if (sceCount == 1 && cpeCount == 2 && lfeCount == 0 && totalChannels == 5)
            reorder = new[] { 1, 2, 0, 3, 4 };  // SCE,CPE0.L,CPE0.R,CPE1.L,CPE1.R → CPE0.L,CPE0.R,SCE,CPE1.L,CPE1.R
        else if (sceCount == 2 && cpeCount == 1 && lfeCount == 0 && totalChannels == 4)
            reorder = new[] { 1, 2, 0, 3 };  // SCE0,CPE.L,CPE.R,SCE1 → CPE.L,CPE.R,SCE0,SCE1
        else
            return;  // Unknown layout, skip reorder
        
        if (reorder.Length > channelBuffers.Length) return;
        
        // Check if reorder is identity
        bool isIdentity = true;
        for (int i = 0; i < reorder.Length; i++)
            if (reorder[i] != i) { isIdentity = false; break; }
        if (isIdentity) return;
        
        // Cycle-based in-place reorder using temp buffer (avoids extra allocation per channel)
        var temp = new float[SamplesPerFrame];
        var visited = new bool[reorder.Length];
        
        for (int start = 0; start < reorder.Length; start++)
        {
            if (visited[start] || reorder[start] == start) 
            {
                visited[start] = true;
                continue;
            }
            
            // Follow the cycle: temp = buf[start], buf[start] = buf[reorder[start]], etc.
            Array.Copy(channelBuffers[start], temp, SamplesPerFrame);
            int current = start;
            while (true)
            {
                int source = reorder[current];
                visited[current] = true;
                if (source == start)
                {
                    Array.Copy(temp, channelBuffers[current], SamplesPerFrame);
                    break;
                }
                Array.Copy(channelBuffers[source], channelBuffers[current], SamplesPerFrame);
                current = source;
            }
        }
    }
    
    #endregion

    public void Dispose()
    {
        if (!isDisposed)
        {
            isDisposed = true;
        }
    }
}

/// <summary>
/// Result of decoding an Individual Channel Stream.
/// Contains ICS info and TNS data — TNS is deferred so CPE can apply MS stereo first.
/// </summary>
public struct IcsDecodeResult
{
    public IcsInfo IcsInfo;
    public TnsData TnsData;
    public bool TnsPresent;
}

/// <summary>
/// When coupling is applied during the decode pipeline.
/// Port of FFmpeg's CouplingPoint enum from aacdec.h.
/// </summary>
internal enum CouplingPoint
{
    BeforeTns = 0,
    BetweenTnsAndImdct = 1,
    AfterImdct = 3
}

/// <summary>
/// Per-target coupling info within a CCE.
/// </summary>
internal struct CouplingTarget
{
    public int TargetType;   // SyntaxElementType value (0=SCE, 1=CPE)
    public int TargetId;     // Instance tag of target element
    public int ChSelect;     // 0=shared, 1=right only, 2=left only, 3=both separate
}

/// <summary>
/// Decoded state for a Coupling Channel Element (CCE).
/// Port of FFmpeg's ChannelCoupling + SingleChannelElement for CCE.
/// </summary>
internal struct CouplingChannelState
{
    public CouplingPoint CouplingPoint;
    public int NumCoupled;
    public int NumGain;
    public CouplingTarget[] Targets;
    public float[,] Gains;    // [gain_index, sfb] for dependent; [gain_index, 0] for independent
    public int InstanceTag;
    public IcsDecodeResult IcsResult;
    public float[] Spectral;  // Decoded spectral coefficients (1024)
    public float[] Output;     // Time-domain output after IMDCT (1024)
    public int[] BandType;     // Per-SFB band type from CCE decode (needed for dependent coupling)
}

/// <summary>
/// Tracks which (type, instanceTag) element was decoded to which channel buffer indices.
/// Used to resolve CCE coupling targets to actual output buffers.
/// </summary>
internal struct DecodedElementInfo
{
    public int ElementType;     // SyntaxElementType value (0=SCE, 1=CPE, 3=LFE)
    public int InstanceTag;
    public int LeftBufferIdx;   // Index into channelBuffers[] for left/mono channel
    public int RightBufferIdx;  // Index into channelBuffers[] for right channel (-1 for SCE/LFE)
}

/// <summary>
/// Per-channel deferred state for two-pass processing.
/// Phase 1 stores spectral data here; Phase 2 (SpectralToSample) applies coupling → TNS → IMDCT.
/// </summary>
internal struct PendingChannel
{
    public int ElementType;       // SyntaxElementType value (0=SCE, 1=CPE, 3=LFE)
    public int InstanceTag;       // Element instance tag
    public int ChannelInPair;     // 0=left/mono, 1=right (for CPE)
    public int OutputBufferIdx;   // Index into channelBuffers[] for output
    public IcsDecodeResult IcsResult;
    // pendingSpectral[pendingIndex] holds the spectral data
}

/// <summary>
/// AAC syntax element types.
/// </summary>
public enum SyntaxElementType
{
    SingleChannelElement = 0,
    ChannelPairElement = 1,
    CouplingChannelElement = 2,
    LowFrequencyEffectsElement = 3,
    DataStreamElement = 4,
    ProgramConfigElement = 5,
    FillElement = 6,
    End = 7
}

/// <summary>
/// Bit reader for AAC bitstream parsing.
/// </summary>
public ref struct AacBitReader
{
    private ReadOnlySpan<byte> data;
    private int bytePosition;
    private int bitPosition;

    public AacBitReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        bytePosition = 0;
        bitPosition = 0;
    }

    /// <summary>
    /// The frame bytes. An extension payload (SBR) is parsed by its own reader over a slice of
    /// this, rather than by threading a second syntax through this one.
    /// </summary>
    internal readonly ReadOnlySpan<byte> Data => data;

    public bool IsEmpty => bytePosition >= data.Length;

    public int BitsRemaining => (data.Length - bytePosition) * 8 - bitPosition;

    public int BitsRead => bytePosition * 8 + bitPosition;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBits(int count)
    {
        int value = 0;

        while (count > 0)
        {
            int bitsAvailable = 8 - bitPosition;
            int bitsToRead = Math.Min(count, bitsAvailable);

            // Past the end of the frame the bits are zeros and the position still advances, which
            // is FFmpeg's convention rather than a convenience: its callers hand get_bits a buffer
            // padded with AV_INPUT_BUFFER_PADDING_SIZE zero bytes, and get_bits_count keeps
            // counting past size_in_bits, so a decoder that over-reads the last frame by a few
            // bits sees zeros and carries on.
            //
            // This used to return 0 for the whole call and leave the position where it was, which
            // is wrong twice over: it discarded the bits that WERE available, and it froze the bit
            // count. USAC's last frame is where that showed - the arithmetic coder primes itself
            // with sixteen bits when fourteen remain, and a frozen reader fed the escape ladder an
            // endless run of zeros, so a frame FFmpeg decodes threw instead.
            int bits = 0;
            if (bytePosition < data.Length)
            {
                int mask = (1 << bitsToRead) - 1;
                int shift = bitsAvailable - bitsToRead;
                bits = (data[bytePosition] >> shift) & mask;
            }

            value = (value << bitsToRead) | bits;

            bitPosition += bitsToRead;
            if (bitPosition >= 8)
            {
                bitPosition = 0;
                bytePosition++;
            }

            count -= bitsToRead;
        }

        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBit()
    {
        return ReadBits(1) != 0;
    }

    /// <summary>
    /// Peeks at the next N bits without advancing the read position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekBits(int count)
    {
        int savedByte = bytePosition;
        int savedBit = bitPosition;
        int value = ReadBits(count);
        bytePosition = savedByte;
        bitPosition = savedBit;
        return value;
    }

    public void AlignToByte()
    {
        if (bitPosition != 0)
        {
            bitPosition = 0;
            bytePosition++;
        }
    }

    public void SkipBytes(int count)
    {
        AlignToByte();
        bytePosition += count;
    }
    
    /// <summary>
    /// Skip exactly <paramref name="count"/> bits without byte-alignment.
    /// Matches FFmpeg's skip_bits_long behavior.
    /// </summary>
    public void SkipBits(int count)
    {
        int totalBits = bytePosition * 8 + bitPosition + count;
        bytePosition = totalBits >> 3;
        bitPosition = totalBits & 7;
    }
}

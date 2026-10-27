// AC-3 (Dolby Digital) Decoder
// Based on ATSC A/52 specification

using MediaKernel.Core.Codecs.H264;

using System.Linq;

namespace MediaKernel.Core.Codecs.AC3;

/// <summary>
/// AC-3 (Dolby Digital) audio decoder.
/// Decodes 5.1 channel audio from AC-3 bitstreams.
/// </summary>
public sealed class Ac3Decoder
{
    /// <summary>Output sample rate (always 48kHz, 44.1kHz, or 32kHz).</summary>
    public int SampleRate { get; private set; }
    
    /// <summary>Number of output channels.</summary>
    public int ChannelCount { get; private set; }
    
    /// <summary>Samples per frame (always 1536).</summary>
    public const int SamplesPerFrame = Ac3FrameParser.SamplesPerFrame;
    
    /// <summary>Maximum number of channels (5.1).</summary>
    public const int MaxChannels = 6;
    
    /// <summary>
    /// Slot used for the coupling channel's own exponents, bit allocation and mantissas.
    /// AC-3 codes coupled channels as a shared channel that carries its own everything; it was
    /// not modelled at all, so its fields were left in the bitstream and every block after the
    /// first coupled one was read at the wrong offset.
    /// </summary>
    private const int CouplingChannel = MaxChannels;

    private const int MaxCoefficients = 256;
    private const int BlocksPerFrame = 6;
    private const int SamplesPerBlock = 256;
    
    // Decoder state
    private Ac3SyncFrame currentFrame;
    private readonly Ac3ChannelState[] channelStates;
    private Ac3BitAllocation bitAllocation;
    
    // Output buffers
    private readonly float[] outputBuffer;
    private readonly float[] imdctWindow;
    private readonly float[] imdctWorkBuffer;
    
    // Mantissa grouping state (for grouped quantization)
    private int mantissa3Count;
    private int mantissa3Value;
    private int mantissa5Count;
    private int mantissa5Value;
    private int mantissa11Count;
    private int mantissa11Value;
    
    /// <summary>
    /// Creates a new AC-3 decoder.
    /// </summary>
    public Ac3Decoder()
    {
        channelStates = new Ac3ChannelState[MaxChannels + 1];
        for (int i = 0; i <= MaxChannels; i++)
        {
            channelStates[i] = new Ac3ChannelState();
        }
        
        bitAllocation = new Ac3BitAllocation(MaxChannels + 1, MaxCoefficients);
        outputBuffer = new float[SamplesPerFrame * MaxChannels];
        imdctWindow = GenerateKbdWindow(512);
        imdctWorkBuffer = new float[512];
    }
    
    /// <summary>
    /// Resets the decoder state.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i <= MaxChannels; i++)
        {
            channelStates[i].Reset();
        }
        
        Array.Clear(outputBuffer);
        ResetMantissaGrouping();
    }
    
    /// <summary>
    /// Decodes an AC-3 frame.
    /// </summary>
    /// <param name="frameData">Complete AC-3 frame data including sync word.</param>
    /// <param name="output">Output buffer for decoded samples (interleaved).</param>
    /// <param name="samplesDecoded">Number of samples decoded per channel.</param>
    /// <returns>True if decoding succeeded.</returns>
    public bool DecodeFrame(ReadOnlySpan<byte> frameData, Span<float> output, out int samplesDecoded)
    {
        samplesDecoded = 0;
        
        if (!Ac3FrameParser.TryParseHeader(frameData, out currentFrame))
            return false;
        
        SampleRate = currentFrame.SampleRate;
        ChannelCount = currentFrame.TotalChannels;
        
        if (output.Length < SamplesPerFrame * ChannelCount)
            return false;
        
        if (ConsistentNoiseGeneration)
            dither.InitFromData(frameData[..Math.Min(frameData.Length, 32768)]);

        var reader = new BitstreamReader(frameData);

        // syncinfo (syncword 16 + crc1 16 + fscod 2 + frmsizecod 6 = 40 bits), then the first
        // three BSI fields that TryParseHeader already extracted from its own view of the data:
        // bsid (5) + bsmod (3) + acmod (3). Skipping only the 40-bit syncinfo left the reader
        // 11 bits short of the mix-level fields, desynchronising every frame from the start.
        reader.SkipBits(40 + 5 + 3 + 3);

        Mark("after acmod", reader.BitPosition);
        // Skip to audio blocks
        if (!SkipBsiFields(ref reader, currentFrame))
            return false;
        
        Mark("end of BSI", reader.BitPosition);

        // Decode each of the 6 audio blocks
        for (int block = 0; block < BlocksPerFrame; block++)
        {
            if (!DecodeAudioBlock(ref reader, block))
                return false;
            
            // FFmpeg initialises downmixed = 1 unconditionally (ac3dec.c:132), so the first block
            // that mixes long and short transforms runs ac3_upmix_delay even when nothing was ever
            // downmixed. That rearranges the delay buffers, and reproducing it is required for
            // bit-exactness — without it the first such block starts from the wrong overlap.
            if (currentBlockDifferentTransforms && delayIsDownmixed)
            {
                delayIsDownmixed = false;
                UpmixDelay();
            }

            // Apply IMDCT and overlap-add for each channel
            for (int ch = 0; ch < currentFrame.TotalChannels; ch++)
            {
                ApplyImdct(ch, block);
            }

        }
        
        // Interleave output
        InterleaveOutput(output, ChannelCount);
        samplesDecoded = SamplesPerFrame;
        
        return true;
    }
    
    /// <summary>
    /// Skips BSI (Bit Stream Information) fields.
    /// </summary>
    private bool SkipBsiFields(ref BitstreamReader reader, Ac3SyncFrame frame)
    {
        // bsid + bsmod already read in header
        // Skip remaining BSI fields
        
        // cmixlev (2 bits) if center channel exists
        if (((int)frame.AudioCodingMode & 0x01) != 0 && frame.AudioCodingMode != Ac3AudioCodingMode.Mono)
            reader.SkipBits(2);
        
        // surmixlev (2 bits) if surround exists
        if (((int)frame.AudioCodingMode & 0x04) != 0)
            reader.SkipBits(2);
        
        // dsurmod (2 bits) if stereo
        if (frame.AudioCodingMode == Ac3AudioCodingMode.Stereo)
            reader.SkipBits(2);
        
        // lfeon (1 bit)
        reader.SkipBits(1);
        
        // dialnorm (5 bits)
        reader.SkipBits(5);
        
        // compre + compr (1 + 8 bits)
        if (reader.ReadBit() == 1)
            reader.SkipBits(8);
        
        // langcode + langcod (1 + 8 bits)
        if (reader.ReadBit() == 1)
            reader.SkipBits(8);
        
        // audprodie + mixlevel + roomtyp (1 + 5 + 2 bits)
        if (reader.ReadBit() == 1)
            reader.SkipBits(7);
        
        // Additional fields for dual mono
        if (frame.AudioCodingMode == Ac3AudioCodingMode.DualMono)
        {
            reader.SkipBits(5); // dialnorm2
            if (reader.ReadBit() == 1)
                reader.SkipBits(8); // compr2
            if (reader.ReadBit() == 1)
                reader.SkipBits(8); // langcod2
            if (reader.ReadBit() == 1)
                reader.SkipBits(7); // audprodi2e
        }
        
        // copyrightb + origbs (1 + 1 bits)
        reader.SkipBits(2);
        
        // timecod1e/timecod2e
        if (reader.ReadBit() == 1)
            reader.SkipBits(14);
        if (reader.ReadBit() == 1)
            reader.SkipBits(14);
        
        // addbsie (1 bit) + addbsil + addbsi
        if (reader.ReadBit() == 1)
        {
            int addbsil = (int)reader.ReadBits(6);
            reader.SkipBits((addbsil + 1) * 8);
        }
        
        return true;
    }
    
    /// <summary>
    /// Decodes one audio block.
    /// </summary>
    /// <summary>Which full-bandwidth channels are coupled, and the coupling frequency range.</summary>
    private readonly bool[] channelInCoupling = new bool[MaxChannels];
    private readonly bool[] rematrixingFlags = new bool[4];
    private int rematrixingBands;
    private bool couplingInUse;
    private int couplingStartFreq;
    private int couplingEndFreq;
    private int couplingBandCount;
    private bool phaseFlagsInUse;
    private float dynamicRangeGain = 1f;

    /// <summary>
    /// Dither generator. FFmpeg seeds this once (av_lfg_init(&s->dith_state, 0)) and lets it run
    /// for the whole stream, so it must not be reset per frame.
    /// </summary>
    private readonly Ac3Lfg dither = new(0);

    /// <summary>Per-channel dither flags for the current block.</summary>
    private readonly bool[] ditherEnabled = new bool[MaxChannels + 1];

    /// <summary>Per-channel blksw bits for the block being decoded; bit <c>ch</c> set = short transform.</summary>
    private int currentBlockSwitch;

    /// <summary>True when this block's channels do not all use the same transform length.</summary>
    private bool currentBlockDifferentTransforms;

    /// <summary>Mirrors FFmpeg's <c>downmixed</c>, which starts set even when no downmix happens.</summary>
    private bool delayIsDownmixed = true;


    /// <summary>
    /// A/52 7.7.1 dynamic range codes to linear gain, matching FFmpeg's dynamic_range_tab:
    /// 2^((i&gt;&gt;5) - ((i&gt;&gt;7)&lt;&lt;3) - 5) * ((i &amp; 0x1F) | 0x20).
    /// </summary>
    private static readonly float[] DynamicRangeTable = BuildDynamicRangeTable();

    private static float[] BuildDynamicRangeTable()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            int v = (i >> 5) - ((i >> 7) << 3) - 5;
            table[i] = MathF.Pow(2f, v) * ((i & 0x1F) | 0x20);
        }
        return table;
    }

    /// <summary>Per-channel, per-band coupling coordinates and their band sizes.</summary>
    private readonly int[,] couplingCoords = new int[MaxChannels, 32];
    private readonly int[] couplingBandSizes = new int[32];
    private readonly bool[] couplingPhaseFlags = new bool[32];
    private int couplingFastLeak;
    private int couplingSlowLeak;

    /// <summary>
    /// Parses the AC-3 coupling strategy (FFmpeg <c>coupling_strategy</c>).
    /// </summary>
    private bool ParseCouplingStrategy(ref BitstreamReader reader, int fullBandChannels)
    {
        // Coupling is not permitted in mono or dual-mono.
        if (currentFrame.AudioCodingMode < Ac3AudioCodingMode.Stereo)
            return false;

        for (int ch = 0; ch < fullBandChannels; ch++)
            channelInCoupling[ch] = reader.ReadBit() == 1;

        if (currentFrame.AudioCodingMode == Ac3AudioCodingMode.Stereo)
            phaseFlagsInUse = reader.ReadBit() == 1;

        int startSubband = (int)reader.ReadBits(4);
        int endSubband = (int)reader.ReadBits(4) + 3;
        if (startSubband >= endSubband)
            return false;

        couplingStartFreq = startSubband * 12 + 37;
        couplingEndFreq = endSubband * 12 + 37;

        // Band structure: for plain AC-3 the flags are always present, one per subband gap.
        int subbandCount = endSubband - startSubband;
        Span<bool> joined = stackalloc bool[32];
        int bandCount = subbandCount;
        for (int i = 0; i < subbandCount - 1; i++)
        {
            joined[i] = reader.ReadBit() == 1;
            if (joined[i])
                bandCount--;
        }
        couplingBandCount = bandCount;

        // Band sizes: each subband spans 12 bins; a set flag merges it into the previous band.
        Array.Clear(couplingBandSizes, 0, couplingBandSizes.Length);
        int band = 0;
        couplingBandSizes[0] = 12;
        for (int sub = 1; sub < subbandCount; sub++)
        {
            if (joined[sub - 1]) couplingBandSizes[band] += 12;
            else couplingBandSizes[++band] = 12;
        }
        return true;
    }

    /// <summary>
    /// Parses the per-channel coupling coordinates (FFmpeg <c>coupling_coordinates</c>).
    /// Values are consumed for bitstream alignment; coupling reconstruction is not applied yet.
    /// </summary>
    private bool ParseCouplingCoordinates(ref BitstreamReader reader, int fullBandChannels, int blockNumber)
    {
        bool coordsExist = false;
        for (int ch = 0; ch < fullBandChannels; ch++)
        {
            if (!channelInCoupling[ch])
                continue;

            if (reader.ReadBit() == 1) // cplcoe
            {
                coordsExist = true;
                int master = 3 * (int)reader.ReadBits(2); // mstrcplco
                for (int bnd = 0; bnd < couplingBandCount; bnd++)
                {
                    int exp = (int)reader.ReadBits(4);  // cplcoexp
                    int mant = (int)reader.ReadBits(4); // cplcomant
                    // A/52 7.4.3: a 15 exponent means no implicit leading one. Kept in FFmpeg's
                    // integer form — the shift truncates, and computing the equivalent in float
                    // keeps bits FFmpeg has already discarded.
                    int coord = exp == 15 ? mant << 22 : (mant + 16) << 21;
                    couplingCoords[ch, bnd] = coord >> (exp + master);
                }
            }
            else if (blockNumber == 0)
            {
                return false; // coordinates are mandatory in block 0
            }
        }

        if (currentFrame.AudioCodingMode == Ac3AudioCodingMode.Stereo && coordsExist && phaseFlagsInUse)
        {
            for (int bnd = 0; bnd < couplingBandCount; bnd++)
                couplingPhaseFlags[bnd] = reader.ReadBit() == 1;
        }

        return true;
    }

    /// <summary>
    /// Why the most recent <see cref="DecodeFrame"/> returned false, or null if it succeeded.
    /// A silent false return is impossible to diagnose from a test log.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Reseed the dither generator from each frame's own bytes instead of letting it run across
    /// the stream, matching FFmpeg's <c>-cons_noisegen</c>. Off by default, as in FFmpeg.
    /// </summary>
    public bool ConsistentNoiseGeneration { get; set; }




    /// <summary>Diagnostic bit-position trace; set to a list to capture, null to disable.</summary>
    public System.Collections.Generic.List<(string Name, int Bit)>? Trace;

    private void Mark(string name, int bit) => Trace?.Add((name, bit));

    private bool DecodeAudioBlock(ref BitstreamReader reader, int blockNumber)
    {
        int fullBandChannels = currentFrame.FullBandwidthChannels;
        
        // Block switch flags (1 bit per channel)
        int blockSwitchFlags = 0;
        for (int ch = 0; ch < fullBandChannels; ch++)
        {
            if (reader.ReadBit() == 1)
                blockSwitchFlags |= 1 << ch;
        }
        // Reconstruction needs these: a switched channel uses two half-length transforms for
        // this block instead of one long one. They used to be parsed and thrown away.
        currentBlockSwitch = blockSwitchFlags;
        currentBlockDifferentTransforms = fullBandChannels > 1 && blockSwitchFlags != 0 &&
                                          blockSwitchFlags != (1 << fullBandChannels) - 1;
        
        // Dither flags (1 bit per channel)
        int ditherFlags = 0;
        for (int ch = 0; ch < fullBandChannels; ch++)
        {
            bool on = reader.ReadBit() == 1;
            ditherEnabled[ch] = on;
            if (on) ditherFlags |= 1 << ch;
        }
        // FFmpeg: dither = (ch == CPL_CH) || dither_flag[ch] — the coupling channel always dithers.
        ditherEnabled[CouplingChannel] = true;
        if (currentFrame.LfeOn) ditherEnabled[fullBandChannels] = false;
        
        Mark($"b{blockNumber} after dith blksw={blockSwitchFlags} dith={ditherFlags}", reader.BitPosition);
        // Dynamic range control. The gain byte was previously read and thrown away; FFmpeg
        // applies it to every coefficient (ac3dec.c: gain = s->dynamic_range[audio_channel]),
        // and it changes per block, which showed up as a few percent of level drift per frame.
        if (reader.ReadBit() == 1)
            dynamicRangeGain = DynamicRangeTable[(int)reader.ReadBits(8)];
        else if (blockNumber == 0)
            dynamicRangeGain = 1f;
        
        // Coupling strategy. Spec 5.4.2.4 / FFmpeg coupling_strategy(). The previous
        // SkipCouplingParams read the wrong fields in the wrong order, so any stream using
        // coupling — which is most real AC-3 at moderate bitrates — desynchronised here.
        Mark($"b{blockNumber} after dynrng", reader.BitPosition);
        // cplstre is present in EVERY block; block 0 merely requires it to be 1. Treating it as
        // absent in block 0 consumed one bit too few and shifted the rest of the block.
        if (reader.ReadBit() == 1) // cplstre
        {
            couplingInUse = reader.ReadBit() == 1;
            if (couplingInUse)
            {
                if (!ParseCouplingStrategy(ref reader, fullBandChannels))
                { LastError = $"blk{blockNumber}: coupling strategy"; return false; }
            }
            if (!couplingInUse)
            {
                Array.Clear(channelInCoupling, 0, channelInCoupling.Length);
                phaseFlagsInUse = false;
            }
        }
        else if (blockNumber == 0)
        { LastError = "blk0: no coupling strategy"; return false; }

        Mark($"b{blockNumber} after cplstrat cplinu={(couplingInUse?1:0)}", reader.BitPosition);
        // Coupling coordinates
        if (couplingInUse && !ParseCouplingCoordinates(ref reader, fullBandChannels, blockNumber))
        { LastError = $"blk{blockNumber}: coupling coords"; return false; }

        Mark($"b{blockNumber} after cplcoords", reader.BitPosition);
        // Rematrixing (stereo only)
        if (currentFrame.AudioCodingMode == Ac3AudioCodingMode.Stereo)
        {
            if (reader.ReadBit() == 1) // rematstr — present in every block
            {
                // FFmpeg ac3dec.c: 4 bands, reduced when coupling starts low enough.
                rematrixingBands = 4;
                if (couplingInUse && couplingStartFreq <= 61)
                    rematrixingBands -= 1 + (couplingStartFreq == 37 ? 1 : 0);
                for (int b = 0; b < rematrixingBands; b++)
                    rematrixingFlags[b] = reader.ReadBit() == 1;
            }
        }
        
        Mark($"b{blockNumber} after remat", reader.BitPosition);
        // Exponent strategy
        var expStrategies = new Ac3ExponentStrategy[MaxChannels + 1];
        if (couplingInUse)
            expStrategies[CouplingChannel] = (Ac3ExponentStrategy)reader.ReadBits(2); // cplexpstr
        for (int ch = 0; ch < fullBandChannels; ch++)
        {
            expStrategies[ch] = (Ac3ExponentStrategy)reader.ReadBits(2);
        }

        if (currentFrame.LfeOn)
        {
            expStrategies[fullBandChannels] = (Ac3ExponentStrategy)reader.ReadBits(1);
        }

        Mark($"b{blockNumber} after expstr", reader.BitPosition);
        // Channel bandwidth (chbwcod). Spec 5.4.2.9 / FFmpeg ac3dec.c: present for every
        // full-bandwidth channel whose exponent strategy is not reuse and which is not coupled.
        // This field was never read, which desynchronised the bitstream immediately — and the
        // resulting end frequency was hardcoded to the 253 maximum, so far too many exponent
        // groups were consumed and the reader ran off the end of the frame on block 0.
        for (int ch = 0; ch < fullBandChannels; ch++)
        {
            if (expStrategies[ch] == Ac3ExponentStrategy.Reuse)
                continue;

            if (couplingInUse && channelInCoupling[ch])
            {
                bitAllocation.EndFreq[ch] = couplingStartFreq;
            }
            else
            {
                int bandwidthCode = (int)reader.ReadBits(6);
                if (bandwidthCode > 60)
                { LastError = $"blk{blockNumber}: bwcod={bandwidthCode} ch={ch}"; return false; }
                bitAllocation.EndFreq[ch] = bandwidthCode * 3 + 73;
            }
        }

        Mark($"b{blockNumber} after chbwcod", reader.BitPosition);

        if (currentFrame.LfeOn)
            bitAllocation.EndFreq[fullBandChannels] = 7;

        if (couplingInUse)
            bitAllocation.EndFreq[CouplingChannel] = couplingEndFreq;

        // Decode exponents. The coupling channel goes first and spans
        // [couplingStartFreq, couplingEndFreq); it carries no gainrng field.
        if (couplingInUse && expStrategies[CouplingChannel] != Ac3ExponentStrategy.Reuse)
        {
            DecodeCouplingExponents(ref reader, expStrategies[CouplingChannel]);
        }

        for (int ch = 0; ch < currentFrame.TotalChannels; ch++)
        {
            if (expStrategies[ch] != Ac3ExponentStrategy.Reuse)
            {
                DecodeExponents(ref reader, ch, expStrategies[ch], ch < fullBandChannels);

                // gainrng — 2 bits per full-bandwidth channel, immediately after its exponents.
                // Also previously unread, compounding the desync.
                if (ch < fullBandChannels)
                    reader.SkipBits(2);
            }
        }
        
        Mark($"b{blockNumber} after exponents", reader.BitPosition);
        // Bit allocation parameters
        if (reader.ReadBit() == 1) // baie — present in every block
        {
            bitAllocation.SlowDecayCode = (int)reader.ReadBits(2);
            bitAllocation.FastDecayCode = (int)reader.ReadBits(2);
            bitAllocation.SlowGainCode = (int)reader.ReadBits(2);
            bitAllocation.DbPerBitCode = (int)reader.ReadBits(2);
            bitAllocation.FloorCode = (int)reader.ReadBits(3);
        }
        
        Mark($"b{blockNumber} after baie", reader.BitPosition);
        // SNR offset. FFmpeg ac3dec.c: csnr = (get_bits(6) - 15) << 4, then per channel
        // snr = (csnr + get_bits(4)) << 2. The offset is per-channel — writing it to one shared
        // field left every channel using the last channel's value.
        if (reader.ReadBit() == 1) // snroffste — present in every block
        {
            bitAllocation.CoarseSnrOffset = (int)reader.ReadBits(6);
            int coarse = (bitAllocation.CoarseSnrOffset - 15) << 4;

            if (couplingInUse)
            {
                bitAllocation.SnrOffsets[CouplingChannel] = (coarse + (int)reader.ReadBits(4)) << 2;
                bitAllocation.FastGains[CouplingChannel] = (int)reader.ReadBits(3);
            }

            for (int ch = 0; ch < currentFrame.TotalChannels; ch++)
            {
                int fine = (int)reader.ReadBits(4);
                bitAllocation.SnrOffsets[ch] = (coarse + fine) << 2;
                bitAllocation.FastGains[ch] = (int)reader.ReadBits(3);
            }
        }
        
        Mark($"b{blockNumber} after snroffst", reader.BitPosition);
        // Coupling leak information. FFmpeg gates the unconditional read on first_cpl_leak,
        // which it sets to s->eac3 — so for plain AC-3 the cplleake flag is always present.
        if (couplingInUse && reader.ReadBit() == 1) // cplleake
        {
            couplingFastLeak = (int)reader.ReadBits(3);
            couplingSlowLeak = (int)reader.ReadBits(3);
        }

        Mark($"b{blockNumber} after cplleak", reader.BitPosition);
        // Calculate bit allocation
        CalculateBitAllocation();
        
        // Skip delta bit allocation if present
        if (reader.ReadBit() == 1) // deltbaie
        {
            // Two passes, matching FFmpeg: every channel's mode first, then every channel's
            // segments. Reading each channel's segments immediately after its own mode
            // interleaved the fields and desynchronised the rest of the block.
            Span<int> dbaMode = stackalloc int[MaxChannels + 1];
            if (couplingInUse)
                dbaMode[CouplingChannel] = (int)reader.ReadBits(2);
            for (int ch = 0; ch < fullBandChannels; ch++)
                dbaMode[ch] = (int)reader.ReadBits(2);

            if (couplingInUse && dbaMode[CouplingChannel] == 1)
            {
                int cplSegments = (int)reader.ReadBits(3) + 1;
                for (int seg = 0; seg < cplSegments; seg++)
                    reader.SkipBits(5 + 4 + 3);
            }

            for (int ch = 0; ch < fullBandChannels; ch++)
            {
                if (dbaMode[ch] != 1) // only DBA_NEW carries segments
                    continue;
                int segments = (int)reader.ReadBits(3) + 1;
                for (int seg = 0; seg < segments; seg++)
                    reader.SkipBits(5 + 4 + 3); // deltoffst + deltlen + deltba
            }
        }
        
        Mark($"b{blockNumber} after deltba", reader.BitPosition);
        // skiple — present in every block, not just block 0.
        if (reader.ReadBit() == 1)
        {
            int skipl = (int)reader.ReadBits(9);
            reader.SkipBits(skipl * 8);
        }
        
        Mark($"b{blockNumber} after skipfld (mantissas start)", reader.BitPosition);
        // Reset mantissa grouping
        ResetMantissaGrouping();
        
        // Decode mantissas. AC-3 emits the shared coupling channel's mantissas inline, once,
        // at the first channel that is coupled — not as a separate trailing block.
        bool couplingMantissasRead = false;
        for (int ch = 0; ch < currentFrame.TotalChannels; ch++)
        {
            DecodeMantissas(ref reader, ch, 0, bitAllocation.EndFreq[ch]);

            if (couplingInUse && ch < fullBandChannels && channelInCoupling[ch] && !couplingMantissasRead)
            {
                DecodeMantissas(ref reader, CouplingChannel, couplingStartFreq, couplingEndFreq);
                couplingMantissasRead = true;
            }
        }


        // Reconstruction must happen after every channel has been read: DecodeMantissas zeroes
        // each channel's coefficients above its own end frequency, which would wipe the coupling
        // contribution for any channel processed after the coupling channel was decoded.
        if (couplingInUse && couplingMantissasRead)
            ApplyCouplingCoordinates(fullBandChannels);

        ApplyRematrixing();

        // Convert to float exactly once, as FFmpeg does with int32_to_float_fmul_scalar.
        float coefficientScale = dynamicRangeGain / 4194304f;
        for (int ch = 0; ch < currentFrame.TotalChannels; ch++)
        {
            var fixedCoefficients = channelStates[ch].FixedCoefficients;
            var coefficients = channelStates[ch].Coefficients;
            for (int bin = 0; bin < MaxCoefficients; bin++)
                coefficients[bin] = fixedCoefficients[bin] * coefficientScale;
        }

        
        return true;
    }
    
    private void SkipCouplingParams(ref BitstreamReader reader, int channels)
    {
        // Simplified coupling parameter skipping
        for (int ch = 0; ch < channels; ch++)
        {
            reader.SkipBits(1); // cplcoe
        }
        
        reader.SkipBits(4); // cplbegf
        reader.SkipBits(4); // cplendf
        
        // More coupling params would go here
    }
    
    /// <summary>
    /// Decodes the coupling channel's exponents, which span the coupling frequency range rather
    /// than starting at bin 0, and whose absolute exponent is scaled by 2 (FFmpeg's
    /// <c>get_bits(4) &lt;&lt; !ch</c> for the CPL channel).
    /// </summary>
    private void DecodeCouplingExponents(ref BitstreamReader reader, Ac3ExponentStrategy strategy)
    {
        var state = channelStates[CouplingChannel];
        int span = couplingEndFreq - couplingStartFreq;
        if (span <= 0)
            return;

        state.AbsoluteExponent = (int)reader.ReadBits(4) << 1;

        int groupSize = 3 << ((int)strategy - 1);
        int groupCount = span / groupSize;

        for (int g = 0; g < groupCount && g < state.GroupedExponents.Length; g++)
            state.GroupedExponents[g] = (byte)reader.ReadBits(7);

        // UnpackExponents fills from index 0; the coupling channel's coefficients live at
        // [couplingStartFreq, couplingEndFreq), and both bit allocation and mantissa reading
        // index by absolute bin, so shift them into place.
        Span<int> relative = stackalloc int[MaxCoefficients];
        Ac3Tables.UnpackExponents(
            strategy, groupCount, state.GroupedExponents, state.AbsoluteExponent, relative);

        // UnpackExponents stores the seed at index 0 and the expanded values from index 1, which
        // is what FFmpeg does for normal channels (dexps[ch][0] = absexp, expansion at
        // dexps[ch][start + !!ch]). For the coupling channel !!ch is 0, so its expanded values
        // begin exactly at couplingStartFreq — skip the seed when shifting them into place.
        Array.Clear(state.Exponents, 0, state.Exponents.Length);
        for (int i = 0; i < span && couplingStartFreq + i < state.Exponents.Length; i++)
            state.Exponents[couplingStartFreq + i] = relative[i + 1];

        state.ExponentCount = couplingEndFreq;
    }

    /// <summary>
    /// Decodes exponents for a channel.
    /// </summary>
    private void DecodeExponents(ref BitstreamReader reader, int channel, Ac3ExponentStrategy strategy, bool fullBandwidth)
    {
        var state = channelStates[channel];

        // End frequency comes from the channel bandwidth code (or the coupling start frequency
        // for coupled channels). It was hardcoded to the 253 maximum, which made every channel
        // decode the largest possible number of exponent groups regardless of the stream.
        int coeffCount = fullBandwidth ? bitAllocation.EndFreq[channel] : 7;
        if (coeffCount <= 0)
            coeffCount = fullBandwidth ? 253 : 7;

        // Read absolute exponent
        state.AbsoluteExponent = (int)reader.ReadBits(4);

        // FFmpeg ac3dec.c: group_size = 3 << (exp_strategy - 1);
        //                  num_exp_groups = (end_freq + group_size - 4) / group_size;
        int groupSize = 3 << ((int)strategy - 1);
        int groupCount = (coeffCount + groupSize - 4) / groupSize;
        
        // Read grouped exponents
        for (int g = 0; g < groupCount && g < state.GroupedExponents.Length; g++)
        {
            state.GroupedExponents[g] = (byte)reader.ReadBits(7);
        }
        
        // Unpack exponents
        Ac3Tables.UnpackExponents(
            strategy,
            groupCount,
            state.GroupedExponents,
            state.AbsoluteExponent,
            state.Exponents);
        
        state.ExponentCount = coeffCount;
    }
    
    /// <summary>
    /// Calculates bit allocation for all channels.
    /// </summary>
    private void CalculateBitAllocation()
    {
        // FFmpeg applies the sample-rate shift to the decay values; sr_shift is 1 for 32 kHz
        // (fscod 2) and 0 otherwise.
        int srShift = currentFrame.SampleRateCode == 2 ? 1 : 0;

        var p = new Ac3BitAllocParameters
        {
            SlowDecay = Ac3Tables.SlowDecayTable[bitAllocation.SlowDecayCode] >> srShift,
            FastDecay = Ac3Tables.FastDecayTable[bitAllocation.FastDecayCode] >> srShift,
            SlowGain = Ac3Tables.SlowGainTable[bitAllocation.SlowGainCode],
            DbPerBit = Ac3Tables.DbPerBitTable[bitAllocation.DbPerBitCode],
            Floor = Ac3Tables.FloorTable[bitAllocation.FloorCode],
            CouplingFastLeak = couplingFastLeak,
            CouplingSlowLeak = couplingSlowLeak,
            SampleRateCode = currentFrame.SampleRateCode,
            SampleRateShift = srShift,
        };

        Span<int> psd = stackalloc int[Ac3BitAllocator.MaxCoefficients];
        Span<int> bandPsd = stackalloc int[Ac3BitAllocator.CriticalBands];
        Span<int> mask = stackalloc int[Ac3BitAllocator.CriticalBands];

        int lfeChannel = currentFrame.LfeOn ? currentFrame.FullBandwidthChannels : -1;

        for (int ch = 0; ch <= MaxChannels; ch++)
        {
            bool isCoupling = ch == CouplingChannel;
            if (isCoupling && !couplingInUse) continue;
            if (!isCoupling && ch >= currentFrame.TotalChannels) continue;

            var state = channelStates[ch];
            int start = isCoupling ? couplingStartFreq : 0;
            int end = isCoupling ? couplingEndFreq : bitAllocation.EndFreq[ch];
            if (end <= start) continue;

            psd.Clear();
            bandPsd.Clear();
            mask.Clear();

            Ac3BitAllocator.CalculatePsd(state.Exponents, start, end, psd, bandPsd);

            if (!Ac3BitAllocator.CalculateMask(
                    in p, bandPsd, start, end,
                    Ac3Tables.FastGainTable[bitAllocation.FastGains[ch]],
                    isLfe: ch == lfeChannel,
                    dbaMode: 2, // DBA_NONE — delta bit allocation is parsed but not applied yet
                    dbaSegments: 0,
                    ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty,
                    mask))
            {
                continue;
            }

            Ac3BitAllocator.CalculateBap(
                mask, psd, start, end,
                bitAllocation.SnrOffsets[ch], p.Floor, state.BitAllocationPointers);
        }
    }
    
    /// <summary>
    /// Decodes mantissas for a channel.
    /// </summary>
    /// <summary>
    /// Decodes one channel's mantissas into coefficients.
    /// </summary>
    /// <remarks>
    /// Mantissas are kept as FFmpeg does — 24-bit signed fixed point — and the exponent is
    /// applied as an arithmetic right shift, exactly matching
    /// <c>coeffs[freq] = mantissa &gt;&gt; exps[freq]</c> in ac3dec.c. Doing this in floating point
    /// as <c>mantissa * 2^-exponent</c> looks equivalent but is not: for quiet bins the shifted
    /// result is a very small integer, so the truncation the reference performs is a large part
    /// of the value. That difference is what left the all-dither surround channel uncorrelated
    /// with the reference even when every mantissa and exponent matched.
    /// </remarks>
    private void DecodeMantissas(ref BitstreamReader reader, int channel, int startBin, int endBin)
    {
        var state = channelStates[channel];

        // FFmpeg converts fixed_coeffs to float with gain / 2^22.
        float coefficientScale = dynamicRangeGain / 4194304f;

        for (int bin = startBin; bin < endBin; bin++)
        {
            int bap = state.BitAllocationPointers[bin];
            int mantissa;

            switch (bap)
            {
                case 0:
                    // No bits transmitted. FFmpeg draws one LFG value per dithered bin and
                    // leaves the generator untouched when the channel's dither flag is clear,
                    // so the call pattern itself is part of matching its output.
                    mantissa = ditherEnabled[channel] ? dither.NextDitherMantissa() : 0;
                    break;

                case 1: // 3 levels, three mantissas in 5 bits
                    mantissa = DecodeGrouped3(ref reader);
                    break;

                case 2: // 5 levels, three mantissas in 7 bits
                    mantissa = DecodeGrouped5(ref reader);
                    break;

                case 3: // 7 levels, 3 bits
                    mantissa = Ac3Dequantizer.SymmetricDequant((int)reader.ReadBits(3), 7);
                    break;

                case 4: // 11 levels, two mantissas in 7 bits
                    mantissa = DecodeGrouped11(ref reader);
                    break;

                case 5: // 15 levels, 4 bits
                    mantissa = Ac3Dequantizer.SymmetricDequant((int)reader.ReadBits(4), 15);
                    break;

                default: // 6-15: linear quantisation, sign-extended and left-aligned to 24 bits
                    int bits = Ac3Tables.BitsPerMantissa[bap];
                    mantissa = Ac3Dequantizer.LinearDequant((int)reader.ReadBits(bits), bits);
                    break;
            }

            state.FixedCoefficients[bin] = mantissa >> state.Exponents[bin];
        }

        // Zero coefficients outside the coded range
        for (int bin = 0; bin < startBin; bin++)
            state.FixedCoefficients[bin] = 0;
        for (int bin = endBin; bin < MaxCoefficients; bin++)
            state.FixedCoefficients[bin] = 0;
    }
    
    /// <summary>
    /// Rebuilds each coupled channel's high-frequency coefficients from the shared coupling
    /// channel, scaled by that channel's per-band coupling coordinate (A/52 7.4.3).
    /// </summary>
    private void ApplyCouplingCoordinates(int fullBandChannels)
    {
        var cpl = channelStates[CouplingChannel];
        int bin = couplingStartFreq;

        for (int band = 0; band < couplingBandCount; band++)
        {
            int bandStart = bin;
            int bandEnd = Math.Min(bandStart + couplingBandSizes[band], couplingEndFreq);

            for (int ch = 0; ch < fullBandChannels; ch++)
            {
                if (!channelInCoupling[ch])
                    continue;

                // FFmpeg: MULH(fixed_coeffs[CPL][bin] * (1 << 4), cpl_coords[ch][band] << 5).
                int coord = couplingCoords[ch, band] << 5;
                var dst = channelStates[ch].FixedCoefficients;
                for (int b = bandStart; b < bandEnd; b++)
                    dst[b] = MulH(cpl.FixedCoefficients[b] * (1 << 4), coord);

                // The right channel is inverted where the phase flag is set.
                if (ch == 1 && couplingPhaseFlags[band])
                {
                    for (int b = bandStart; b < bandEnd; b++)
                        dst[b] = -dst[b];
                }
            }
            bin = bandEnd;
        }
    }

    /// <summary>Stereo rematrixing (A/52 7.5.4): undoes the encoder's sum/difference coding.</summary>
    private void ApplyRematrixing()
    {
        if (currentFrame.AudioCodingMode != Ac3AudioCodingMode.Stereo || rematrixingBands == 0)
            return;

        ReadOnlySpan<byte> bandTab = [13, 25, 37, 61, 253];
        var left = channelStates[0].FixedCoefficients;
        var right = channelStates[1].FixedCoefficients;
        // FFmpeg's do_rematrixing stops at min(end_freq[1], end_freq[2]) and nothing more. For a
        // coupled channel that end frequency is the coupling start, so rematrixing never reaches
        // into the coupling range. Extending it to the coupling end corrupted every coupled band
        // of a stereo stream.
        int end = Math.Min(bitAllocation.EndFreq[0], bitAllocation.EndFreq[1]);

        for (int bnd = 0; bnd < rematrixingBands; bnd++)
        {
            if (!rematrixingFlags[bnd])
                continue;

            int bandEnd = Math.Min(end, bandTab[bnd + 1]);
            for (int i = bandTab[bnd]; i < bandEnd; i++)
            {
                int tmp = left[i];
                left[i] = tmp + right[i];
                right[i] = tmp - right[i];
            }
        }
    }

    private void ResetMantissaGrouping()
    {
        mantissa3Count = 0;
        mantissa5Count = 0;
        mantissa11Count = 0;
    }
    
    // Grouped mantissas pack their values most-significant first: a 3-level group of three is
    // coded as 9a + 3b + c, so a is v/9 and c is v%3. Serving v%3 first returned the group in
    // reverse order, scrambling every bap 1/2/4 coefficient.
    private readonly int[] groupedValues = new int[3];

    private int DecodeGrouped3(ref BitstreamReader reader)
    {
        if (mantissa3Count == 0)
        {
            int v = (int)reader.ReadBits(5);
            groupedValues[0] = v / 9;
            groupedValues[1] = (v / 3) % 3;
            groupedValues[2] = v % 3;
            mantissa3Value = 0;
            mantissa3Count = 3;
        }

        int quantIndex = groupedValues[mantissa3Value++];
        mantissa3Count--;
        return Ac3Dequantizer.SymmetricDequant(quantIndex, 3);
    }
    
    private readonly int[] grouped5Values = new int[3];

    private int DecodeGrouped5(ref BitstreamReader reader)
    {
        if (mantissa5Count == 0)
        {
            int v = (int)reader.ReadBits(7);
            grouped5Values[0] = v / 25;
            grouped5Values[1] = (v / 5) % 5;
            grouped5Values[2] = v % 5;
            mantissa5Value = 0;
            mantissa5Count = 3;
        }

        int quantIndex = grouped5Values[mantissa5Value++];
        mantissa5Count--;
        return Ac3Dequantizer.SymmetricDequant(quantIndex, 5);
    }
    
    private readonly int[] grouped11Values = new int[2];

    private int DecodeGrouped11(ref BitstreamReader reader)
    {
        if (mantissa11Count == 0)
        {
            int v = (int)reader.ReadBits(7);
            grouped11Values[0] = v / 11;
            grouped11Values[1] = v % 11;
            mantissa11Value = 0;
            mantissa11Count = 2;
        }

        int quantIndex = grouped11Values[mantissa11Value++];
        mantissa11Count--;
        return Ac3Dequantizer.SymmetricDequant(quantIndex, 11);
    }
    
    
    /// <summary>
    /// Applies IMDCT to transform coefficients to time domain.
    /// </summary>
    private void ApplyImdct(int channel, int block)
    {
        var state = channelStates[channel];
        int outputOffset = block * SamplesPerBlock + channel * SamplesPerFrame;


        // Only the full-bandwidth channels carry a blksw bit; the coupling channel and the LFE
        // always use the long transform.
        if (channel < currentFrame.FullBandwidthChannels && (currentBlockSwitch & (1 << channel)) != 0)
        {
            ApplyShortImdct(state, outputOffset);
            return;
        }

        Imdct256(state.Coefficients, state.ImdctOutput);

        for (int i = 0; i < SamplesPerBlock; i++)
        {
            // Window and overlap with previous
            float sample = state.ImdctOutput[i] * imdctWindow[i] + 
                          state.OverlapBuffer[i];
            
            outputBuffer[outputOffset + i] = sample;
            
            // Store overlap for next block
            state.OverlapBuffer[i] = state.ImdctOutput[i + SamplesPerBlock] * 
                                     imdctWindow[SamplesPerBlock + i];
        }
    }

    /// <summary>
    /// Port of FFmpeg's ac3_upmix_delay. Rearranges the per-channel overlap buffers as though they
    /// held a stereo downmix that now has to be spread back over the full layout. Our overlap
    /// entries are FFmpeg's delay samples scaled by the window, so a copy or a clear carries over
    /// unchanged.
    /// </summary>
    private void UpmixDelay()
    {
        switch (currentFrame.AudioCodingMode)
        {
            case Ac3AudioCodingMode.DualMono:
            case Ac3AudioCodingMode.Stereo:
                Array.Copy(channelStates[0].OverlapBuffer, channelStates[1].OverlapBuffer, SamplesPerBlock);
                break;

            case Ac3AudioCodingMode.TwoFrontTwoSurround:
                Array.Clear(channelStates[3].OverlapBuffer, 0, SamplesPerBlock);
                goto case Ac3AudioCodingMode.TwoFrontOneSurround;

            case Ac3AudioCodingMode.TwoFrontOneSurround:
                Array.Clear(channelStates[2].OverlapBuffer, 0, SamplesPerBlock);
                break;

            case Ac3AudioCodingMode.ThreeFrontTwoSurround:
                Array.Clear(channelStates[4].OverlapBuffer, 0, SamplesPerBlock);
                goto case Ac3AudioCodingMode.ThreeFrontOneSurround;

            case Ac3AudioCodingMode.ThreeFrontOneSurround:
                Array.Clear(channelStates[3].OverlapBuffer, 0, SamplesPerBlock);
                goto case Ac3AudioCodingMode.ThreeFront;

            case Ac3AudioCodingMode.ThreeFront:
                Array.Copy(channelStates[1].OverlapBuffer, channelStates[2].OverlapBuffer, SamplesPerBlock);
                Array.Clear(channelStates[1].OverlapBuffer, 0, SamplesPerBlock);
                break;
        }
    }

    private readonly float[] shortImdctEven = new float[SamplesPerBlock];
    private readonly float[] shortImdctOdd = new float[SamplesPerBlock];

    /// <summary>
    /// Reconstructs one block of a channel whose blksw bit is set. The 256 coefficients are
    /// de-interleaved into two 128-coefficient halves; the even half overlaps with the previous
    /// block and the odd half becomes the overlap carried into the next one (A/52 7.9.4.2).
    /// </summary>
    /// <remarks>
    /// FFmpeg reaches the same result through av_tx, whose inverse MDCT of length L emits only
    /// samples L/2 .. 3L/2-1 of the full 2L-point transform and recovers the rest inside
    /// vector_fmul_window. Here the full 2L points are computed and indexed directly, which is
    /// why the offsets below are 64-based rather than 0-based.
    /// </remarks>
    private void ApplyShortImdct(Ac3ChannelState state, int outputOffset)
    {
        Imdct128(state.Coefficients, 0, shortImdctEven);
        Imdct128(state.Coefficients, 1, shortImdctOdd);

        const int half = SamplesPerBlock / 2; // 128

        for (int u = 0; u < half; u++)
        {
            float even = shortImdctEven[191 - u];
            outputBuffer[outputOffset + u] =
                state.OverlapBuffer[u] - even * imdctWindow[u];
            outputBuffer[outputOffset + SamplesPerBlock - 1 - u] =
                state.OverlapBuffer[SamplesPerBlock - 1 - u] + even * imdctWindow[SamplesPerBlock - 1 - u];
        }

        for (int u = 0; u < half; u++)
        {
            float odd = shortImdctOdd[half / 2 + u];
            state.OverlapBuffer[u] = odd * imdctWindow[SamplesPerBlock - 1 - u];
            state.OverlapBuffer[SamplesPerBlock - 1 - u] = odd * imdctWindow[u];
        }
    }

    /// <summary>
    /// AC-3 short-block inverse MDCT: the 128 coefficients at <paramref name="parity"/> (every
    /// other bin) to 256 time samples. Same definition as <see cref="Imdct256"/> at half length.
    /// </summary>
    /// <summary>Port of FFmpeg's MULH: the high 32 bits of a 32x32 signed product.</summary>
    private static int MulH(int a, int b) => (int)(((long)a * b) >> 32);

    private static void Imdct128(float[] input, int parity, float[] output)
    {
        const int n = 256;
        const int coefficients = n / 2;   // 128
        const int quarter = n / 4;
        const float scale = -1.0f;

        for (int k = 0; k < n; k++)
        {
            double sum = 0;
            double phase = Math.PI / coefficients * (k + 0.5 + quarter);
            for (int m = 0; m < coefficients; m++)
                sum += input[2 * m + parity] * Math.Cos(phase * (m + 0.5));
            output[k] = (float)sum * scale;
        }
    }
    
    /// <summary>
    /// 256-point IMDCT.
    /// </summary>
    /// <summary>
    /// AC-3 long-block inverse MDCT: 256 frequency coefficients to 512 time samples.
    /// </summary>
    /// <remarks>
    /// The previous version summed only the first 128 coefficients and wrote only 256 outputs,
    /// so it discarded every coefficient at or above bin 128 — including most of the coupling
    /// range — and left the upper half of the overlap buffer untouched.
    /// </remarks>
    private void Imdct256(float[] input, float[] output)
    {
        const int n = 512;      // transform length
        const int half = n / 2; // 256 coefficients
        const int quarter = n / 4;

        // FFmpeg initialises its inverse MDCT with scale 1.0 (av_tx_init(..., &scale) where
        // scale == 1.0f), i.e. no 1/N normalisation, and the coefficients carry the level.
        // The sign is negative per A/52 7.9.4.1. Calibrated against ffmpeg's own output:
        // with dynamic range applied the residual gain was exactly 2^8, which is the 1/256
        // this used to divide by.
        const float scale = -1.0f;
        for (int k = 0; k < n; k++)
        {
            double sum = 0;
            double phase = Math.PI / half * (k + 0.5 + quarter);
            for (int m = 0; m < half; m++)
                sum += input[m] * Math.Cos(phase * (m + 0.5));
            output[k] = (float)sum * scale;
        }
    }
    
    /// <summary>
    /// Generates Kaiser-Bessel derived window.
    /// </summary>
    /// <summary>
    /// Builds AC-3's 512-point transform window: a Kaiser-Bessel-derived window of length 256
    /// (alpha 5.0, matching FFmpeg's ff_kbd_window_init) mirrored into the second half.
    /// </summary>
    /// <remarks>
    /// The previous generator used a different parameterisation entirely
    /// (x = 4n^2/h^2 - 1) and produced the wrong shape.
    /// </remarks>
    private static float[] GenerateKbdWindow(int length)
    {
        const int n = 256;
        const double alpha = 5.0;

        double alpha2 = 4 * (alpha * Math.PI / n) * (alpha * Math.PI / n);
        var temp = new double[n / 2 + 1];
        double scale = 0.0;

        for (int i = 0; i <= n / 2; i++)
        {
            double tmp = (double)i * (n - i) * alpha2;
            temp[i] = Bessel0(Math.Sqrt(tmp));
            scale += temp[i] * (1 + ((i != 0 && i < n / 2) ? 1 : 0));
        }
        scale = 1.0 / (scale + 1);

        var half = new float[n];
        double sum = 0.0;
        int k = 0;
        for (; k <= n / 2; k++)
        {
            sum += temp[k];
            half[k] = (float)Math.Sqrt(sum * scale);
        }
        for (; k < n; k++)
        {
            sum += temp[n - k];
            half[k] = (float)Math.Sqrt(sum * scale);
        }

        // Full 512-point symmetric window.
        var window = new float[length];
        for (int i = 0; i < n && i < length; i++)
        {
            window[i] = half[i];
            if (n + i < length)
                window[n + i] = half[n - 1 - i];
        }
        return window;
    }
    
    /// <summary>
    /// Modified Bessel function of the first kind, order 0.
    /// </summary>
    /// <summary>
    /// Modified Bessel function of the first kind, order 0, used to build the KBD window.
    /// </summary>
    /// <remarks>
    /// Runs in double precision and iterates until the terms stop contributing. The previous
    /// version summed a fixed 15 terms in float; for alpha 5 the argument reaches about 15.7,
    /// where the series is nowhere near converged, so the window it produced was noticeably off.
    /// </remarks>
    private static double Bessel0(double x)
    {
        double sum = 1.0;
        double term = 1.0;
        double x2 = x * x * 0.25;

        for (int k = 1; k < 60; k++)
        {
            term *= x2 / ((double)k * k);
            sum += term;
            if (term < sum * 1e-17)
                break;
        }
        return sum;
    }
    
    /// <summary>
    /// Interleaves channel buffers to output.
    /// </summary>
    /// <summary>
    /// AC-3 codes channels in acmod order (L, C, R, Ls, Rs) with LFE last; decoders emit them in
    /// WAV order (L, R, C, LFE, Ls, Rs). This is FFmpeg's ff_ac3_dec_channel_map, indexed
    /// [acmod][lfeOn], giving the decoded channel for each output position.
    /// </summary>
    private static ReadOnlySpan<byte> ChannelMap(int acmod, bool lfeOn) => (acmod, lfeOn) switch
    {
        (0, false) => [0, 1],          (0, true) => [0, 1, 2],
        (1, false) => [0],             (1, true) => [0, 1],
        (2, false) => [0, 1],          (2, true) => [0, 1, 2],
        (3, false) => [0, 2, 1],       (3, true) => [0, 2, 1, 3],
        (4, false) => [0, 1, 2],       (4, true) => [0, 1, 3, 2],
        (5, false) => [0, 2, 1, 3],    (5, true) => [0, 2, 1, 4, 3],
        (6, false) => [0, 1, 2, 3],    (6, true) => [0, 1, 4, 2, 3],
        (7, false) => [0, 2, 1, 3, 4], (7, true) => [0, 2, 1, 5, 3, 4],
        _ => [0, 1],
    };

    private void InterleaveOutput(Span<float> output, int channels)
    {
        ReadOnlySpan<byte> map = ChannelMap((int)currentFrame.AudioCodingMode, currentFrame.LfeOn);

        for (int s = 0; s < SamplesPerFrame; s++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                int src = ch < map.Length ? map[ch] : ch;
                output[s * channels + ch] = outputBuffer[src * SamplesPerFrame + s];
            }
        }
    }
    
    /// <summary>
    /// Per-channel decoder state.
    /// </summary>
    private sealed class Ac3ChannelState
    {
        public int AbsoluteExponent;
        public int ExponentCount;
        public readonly byte[] GroupedExponents = new byte[Ac3Tables.MaxExponentGroups];
        public readonly int[] Exponents = new int[MaxCoefficients];
        public readonly int[] BitAllocationPointers = new int[MaxCoefficients];
        /// <summary>
        /// Transform coefficients before scaling, as integers. FFmpeg keeps these fixed-point all
        /// the way through coupling and rematrixing and converts to float exactly once, so doing
        /// that arithmetic in float instead loses the low bits and shifts samples by up to an LSB.
        /// </summary>
        public readonly int[] FixedCoefficients = new int[MaxCoefficients];
        public readonly float[] Coefficients = new float[MaxCoefficients];
        public readonly float[] ImdctOutput = new float[512];
        public readonly float[] OverlapBuffer = new float[SamplesPerBlock];
        
        public void Reset()
        {
            AbsoluteExponent = 0;
            ExponentCount = 0;
            Array.Clear(GroupedExponents);
            Array.Clear(Exponents);
            Array.Clear(BitAllocationPointers);
            Array.Clear(FixedCoefficients);
            Array.Clear(Coefficients);
            Array.Clear(ImdctOutput);
            Array.Clear(OverlapBuffer);
        }
    }
}

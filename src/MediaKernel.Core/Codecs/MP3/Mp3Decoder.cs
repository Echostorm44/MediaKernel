// Licensed under the MIT License.
// Based on ISO/IEC 11172-3 (MPEG-1 Audio Layer III) specification.

using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3;

/// <summary>
/// Complete MPEG-1/2/2.5 Layer III (MP3) audio decoder.
/// Decodes compressed MP3 frames to PCM audio samples.
/// </summary>
public class Mp3Decoder
{
    /// <summary>
    /// Number of spectral coefficients per granule.
    /// </summary>
    public const int SpectralCoefficientsPerGranule = 576;
    
    /// <summary>
    /// Number of subbands.
    /// </summary>
    public const int Subbands = 32;
    
    /// <summary>
    /// Number of samples per subband.
    /// </summary>
    public const int SamplesPerSubband = 18;
    
    /// <summary>
    /// Maximum channels.
    /// </summary>
    public const int MaxChannels = 2;
    
    /// <summary>
    /// Maximum granules for MPEG-1.
    /// </summary>
    public const int MaxGranules = 2;
    
    // Decoder state
    private readonly float[] overlapBuffer;
    private readonly Mp3Filterbank[] filterbanks;
    private readonly int[] spectralCoefficients;
    // Per-channel scalefactor arrays — SCFSI requires granule 0 values to persist
    // into granule 1 decode, so each channel needs its own array.
    private readonly int[][] scalefactorsLongPerChannel;
    private readonly int[][] scalefactorsShortPerChannel;
    private readonly float[] requantizedSamples;
    private readonly float[] pcmOutput;
    
    // Bit reservoir for main_data
    // FFmpeg limits the combined reader buffer to LAST_BUF_SIZE = 2*BACKSTEP_SIZE + EXTRABYTES = 1048 bytes.
    // Bytes beyond this limit read as zeros (AV_INPUT_BUFFER_PADDING). We replicate this
    // to match FFmpeg's overread behavior — see mpegaudiodec_template.c lines 1305-1306.
    private const int BackstepSize = 512;
    private const int ExtraBytes = 24;
    private const int LastBufSize = 2 * BackstepSize + ExtraBytes; // 1048
    
    private readonly byte[] mainDataBuffer;
    private int mainDataBufferIndex;
    private int mainDataBegin;
    
    private bool isInitialized;
    private Mp3FrameHeader currentHeader;
    private int sampleRate;
    private int channelCount;
    
    /// <summary>
    /// Gets the current sample rate.
    /// </summary>
    public int SampleRate => sampleRate;
    
    /// <summary>
    /// Gets the current channel count.
    /// </summary>
    public int ChannelCount => channelCount;
    
    /// <summary>
    /// Gets the samples per frame based on MPEG version.
    /// </summary>
    public int SamplesPerFrame => currentHeader.Version == MpegVersion.Mpeg1
        ? SpectralCoefficientsPerGranule * 2
        : SpectralCoefficientsPerGranule;
    
    public Mp3Decoder()
    {
        // Allocate state buffers
        overlapBuffer = new float[MaxChannels * SpectralCoefficientsPerGranule];
        filterbanks = new Mp3Filterbank[MaxChannels];
        for (int ch = 0; ch < MaxChannels; ch++)
            filterbanks[ch] = new Mp3Filterbank();
        spectralCoefficients = new int[SpectralCoefficientsPerGranule];
        scalefactorsLongPerChannel = new int[MaxChannels][];
        scalefactorsShortPerChannel = new int[MaxChannels][];
        for (int ch = 0; ch < MaxChannels; ch++)
        {
            scalefactorsLongPerChannel[ch] = new int[22];
            scalefactorsShortPerChannel[ch] = new int[13 * 3]; // 13 bands × 3 windows
        }
        requantizedSamples = new float[MaxChannels * SpectralCoefficientsPerGranule];
        pcmOutput = new float[MaxChannels * SpectralCoefficientsPerGranule * MaxGranules];
        
        // Bit reservoir buffer (maximum 511 bytes back-reference + current frame)
        mainDataBuffer = new byte[2048];
        mainDataBufferIndex = 0;
    }
    
    /// <summary>
    /// Decodes an MP3 frame to PCM audio samples.
    /// </summary>
    /// <param name="frameData">Complete MP3 frame data including header.</param>
    /// <param name="pcmSamples">Output buffer for PCM samples (must be at least SamplesPerFrame × channels).</param>
    /// <param name="samplesDecoded">Number of samples decoded per channel.</param>
    /// <returns>True if decoding succeeded.</returns>
    public bool DecodeFrame(ReadOnlySpan<byte> frameData, Span<float> pcmSamples, out int samplesDecoded)
    {
        samplesDecoded = 0;
        
        if (frameData.Length < 4)
            return false;
        
        // Parse frame header
        if (!Mp3FrameParser.TryParseHeader(frameData, out currentHeader))
            return false;
        
        // Validate layer III
        if (currentHeader.Layer != MpegLayer.Layer3)
            return false;
        
        sampleRate = currentHeader.SampleRate;
        channelCount = currentHeader.ChannelMode == Mp3ChannelMode.SingleChannel ? 1 : 2;
        isInitialized = true;
        
        int headerSize = 4;
        if (currentHeader.HasCrc)
            headerSize += 2;
        
        // Parse side information
        ReadOnlySpan<byte> afterHeader = frameData.Slice(headerSize);
        var sideInfoReader = new Mp3BitReader(afterHeader);
        
        if (!Mp3SideInfoDecoder.DecodeSideInfo(
            ref sideInfoReader, 
            currentHeader, 
            out Mp3SideInfo sideInfo))
        {
            return false;
        }
        
        int sideInfoSize = currentHeader.Version == MpegVersion.Mpeg1
            ? (channelCount == 1 ? 17 : 32)
            : (channelCount == 1 ? 9 : 17);
        
        // Get main data start position
        mainDataBegin = sideInfo.MainDataBegin;
        
        // Copy current frame's main data to buffer
        ReadOnlySpan<byte> mainData = afterHeader.Slice(sideInfoSize);
        int mainDataLength = frameData.Length - headerSize - sideInfoSize;
        
        // Handle bit reservoir
        int bufferOffset = mainDataBufferIndex - mainDataBegin;
        if (bufferOffset < 0)
        {
            // Not enough data in reservoir — start from beginning of available data.
            // This matches FFmpeg's behavior: min(mainDataBegin, lastBufSize).
            bufferOffset = 0;
        }
        
        // Copy main data to buffer
        if (mainDataLength > 0)
        {
            mainData.Slice(0, mainDataLength).CopyTo(mainDataBuffer.AsSpan(mainDataBufferIndex));
            
            // DEBUG: Print bit reservoir info for all frames
            
            // Reader spans the reservoir plus the whole of this frame's main data, matching what
            // FFmpeg's two-buffer mechanism (last_buf then in_gb, via switch_buffer) makes
            // available. It used to cap the current frame's contribution at
            // LastBufSize - reservoirSize, so a frame that both reached far back into the
            // reservoir and carried a lot of its own data ran off the end of the reader partway
            // through - and Mp3BitReader zero-pads an overread rather than failing, so the second
            // granule silently decoded from zeroes. The bytes are already in the buffer; there is
            // no reason to hide them.
            int reservoirSize = mainDataBufferIndex - bufferOffset;
            int readerTotalBytes = reservoirSize + mainDataLength;
            var mainDataReader = new Mp3BitReader(mainDataBuffer.AsSpan(bufferOffset, readerTotalBytes));
            
            // Decode granules
            int granulesPerFrame = currentHeader.Version == MpegVersion.Mpeg1 ? 2 : 1;
            int outputIndex = 0;
            int startGranule = 0;
            
            // FFmpeg's granule-skip logic: when insufficient reservoir data is available,
            // skip granules whose data falls in the missing region. Output silence for them
            // but still run IMDCT+synthesis to keep overlap buffers in sync.
            // See mpegaudiodec_template.c lines 1316-1332.
            int availableReservoirBytes = mainDataBufferIndex - bufferOffset;
            if (availableReservoirBytes < mainDataBegin)
            {
                int accumulatedBits = availableReservoirBytes * 8;
                
                while (startGranule < granulesPerFrame && (accumulatedBits >> 3) < mainDataBegin)
                {
                    for (int ch = 0; ch < channelCount; ch++)
                    {
                        ref readonly Mp3GranuleInfo gi = ref Mp3SideInfo.GetGranule(
                            ref sideInfo.Granules, startGranule, ch);
                        accumulatedBits += gi.Part23Length;
                        
                        // Zero spectral data and run IMDCT+synthesis (silence output, overlap update)
                        Span<float> channelReq = requantizedSamples.AsSpan(
                            ch * SpectralCoefficientsPerGranule, SpectralCoefficientsPerGranule);
                        channelReq.Clear();
                        
                        Span<float> channelOverlap = overlapBuffer.AsSpan(
                            ch * SpectralCoefficientsPerGranule, SpectralCoefficientsPerGranule);
                        
                        Span<float> channelOutput = pcmOutput.AsSpan(
                            outputIndex, SpectralCoefficientsPerGranule);
                        
                        filterbanks[ch].ProcessGranuleWithSynthesis(
                            channelReq, in gi, channelOverlap, channelOutput);
                        
                        outputIndex += SpectralCoefficientsPerGranule;
                    }
                    startGranule++;
                }
                
                // Advance reader past the portion of frame data consumed by skipped granules
                int skipBits = accumulatedBits - mainDataBegin * 8;
                if (skipBits > 0)
                    mainDataReader.Skip(skipBits);
            }
            
            for (int granule = startGranule; granule < granulesPerFrame; granule++)
            {
                // Phase 1: Decode both channels (scalefactors + Huffman + requantize)
                for (int channel = 0; channel < channelCount; channel++)
                {
                    // Get granule info
                    ref readonly Mp3GranuleInfo granuleInfo = ref Mp3SideInfo.GetGranule(ref sideInfo.Granules, granule, channel);
                    
                    // Record bit position before this granule — part2_3_length covers
                    // BOTH scale factors AND Huffman data
                    int granuleBitsStart = mainDataReader.BitsRead;
                    
                    // Get per-channel scalefactor arrays
                    int[] scalefactorsLong = scalefactorsLongPerChannel[channel];
                    int[] scalefactorsShort = scalefactorsShortPerChannel[channel];
                    
                    // Only clear scalefactors on granule 0 — SCFSI in granule 1 needs
                    // granule 0's values to persist for bands where scfsi=1.
                    if (granule == 0)
                    {
                        Array.Clear(scalefactorsLong);
                        Array.Clear(scalefactorsShort);
                    }
                    
                    // Get scfsi values for this channel
                    int scfsi0 = sideInfo.Scfsi[channel * 4 + 0];
                    int scfsi1 = sideInfo.Scfsi[channel * 4 + 1];
                    int scfsi2 = sideInfo.Scfsi[channel * 4 + 2];
                    int scfsi3 = sideInfo.Scfsi[channel * 4 + 3];
                    
                    int sfBitsRead;
                    bool lsfPreflag = false;
                    if (currentHeader.Version == MpegVersion.Mpeg1)
                    {
                        Mp3ScalefactorDecoder.DecodeMpeg1Scalefactors(
                            ref mainDataReader,
                            in granuleInfo,
                            granule,
                            scfsi0, scfsi1, scfsi2, scfsi3,
                            scalefactorsLong,
                            scalefactorsShort,
                            out sfBitsRead);
                    }
                    else
                    {
                        // MODE_EXT_I_STEREO is bit 0 and MODE_EXT_MS_STEREO is bit 1, so these
                        // are tested with & and not with >=; mode_ext 2 is M/S alone, and the
                        // old >= 1 reported intensity stereo for it.
                        bool intensityStereo = (currentHeader.ModeExtension & 1) != 0;
                        
                        Mp3ScalefactorDecoder.DecodeMpeg2Scalefactors(
                            ref mainDataReader,
                            in granuleInfo,
                            intensityStereo && channel == 1,
                            SampleRateIndexOf(currentHeader.SampleRate),
                            scalefactorsLong,
                            scalefactorsShort,
                            out lsfPreflag,
                            out sfBitsRead);
                    }
                    
                    // Huffman bit budget = part2_3_length minus scale factor bits
                    int huffmanBits = granuleInfo.Part23Length - sfBitsRead;
                    if (huffmanBits < 0)
                        huffmanBits = 0; // Gracefully handle overread (e.g., extra_overread test)
                    
                    // Decode spectral data (Huffman)
                    Array.Clear(spectralCoefficients);
                    
                    Mp3HuffmanDecoder.DecodeSpectralData(
                        ref mainDataReader,
                        in granuleInfo,
                        spectralCoefficients,
                        huffmanBits,
                        sampleRate,
                        out _);
                    
                    // Advance reader to exact end of this granule's data
                    int granuleBitsEnd = granuleBitsStart + granuleInfo.Part23Length;
                    mainDataReader.Seek(granuleBitsEnd);
                    
                    // Requantize into per-channel buffer
                    Span<float> channelReq = requantizedSamples.AsSpan(
                        channel * SpectralCoefficientsPerGranule,
                        SpectralCoefficientsPerGranule);
                    
                    // FFmpeg adjusts global_gain -= 2 for MS-stereo-only to pre-incorporate
                    // the 1/sqrt(2) normalization factor (2^(-2*0.25) = 2^(-0.5) = 1/sqrt(2))
                    int gainAdjust = 0;
                    if (channelCount == 2 && currentHeader.ChannelMode == Mp3ChannelMode.JointStereo
                        && (currentHeader.ModeExtension & (2 | 1)) == 2) // MS only, no IS
                    {
                        gainAdjust = -2;
                    }
                    
                    Requantize(
                        spectralCoefficients,
                        in granuleInfo,
                        scalefactorsLong,
                        scalefactorsShort,
                        gainAdjust,
                        lsfPreflag,
                        channelReq);
                }
                
                // Phase 2: Apply stereo processing (MS stereo / intensity stereo)
                if (channelCount == 2 && currentHeader.ChannelMode == Mp3ChannelMode.JointStereo)
                {
                    Span<float> ch0 = requantizedSamples.AsSpan(0, SpectralCoefficientsPerGranule);
                    Span<float> ch1 = requantizedSamples.AsSpan(SpectralCoefficientsPerGranule, SpectralCoefficientsPerGranule);
                    ApplyJointStereo(
                        ch0, ch1,
                        in Mp3SideInfo.GetGranule(ref sideInfo.Granules, granule, 1),
                        scalefactorsLongPerChannel[1],
                        scalefactorsShortPerChannel[1]);
                }
                
                // Phase 3: Post-processing and synthesis for each channel
                for (int channel = 0; channel < channelCount; channel++)
                {
                    ref readonly Mp3GranuleInfo granuleInfo = ref Mp3SideInfo.GetGranule(ref sideInfo.Granules, granule, channel);
                    
                    Span<float> channelReq = requantizedSamples.AsSpan(
                        channel * SpectralCoefficientsPerGranule,
                        SpectralCoefficientsPerGranule);
                    
                    // Reorder short blocks (if needed)
                    if (granuleInfo.WindowSwitchingFlag && granuleInfo.BlockType == 2)
                    {
                        ReorderShortBlocks(channelReq, granuleInfo.MixedBlockFlag);
                    }
                    
                    // Anti-aliasing butterfly (between requantization and IMDCT)
                    ApplyAntialias(channelReq, in granuleInfo);
                    
                    // Get overlap buffer for this channel
                    Span<float> channelOverlap = overlapBuffer.AsSpan(
                        channel * SpectralCoefficientsPerGranule,
                        SpectralCoefficientsPerGranule);
                    
                    // Apply IMDCT, frequency inversion, and polyphase synthesis via filterbank
                    Span<float> channelOutput = pcmOutput.AsSpan(
                        outputIndex,
                        SpectralCoefficientsPerGranule);
                    
                    filterbanks[channel].ProcessGranuleWithSynthesis(
                        channelReq,
                        in granuleInfo,
                        channelOverlap,
                        channelOutput);
                    
                    outputIndex += SpectralCoefficientsPerGranule;
                }
            }
            
            // Update buffer index for next frame
            mainDataBufferIndex += mainDataLength;
            if (mainDataBufferIndex > mainDataBuffer.Length - 1024)
            {
                // Shift buffer to prevent overflow
                int keepBytes = Math.Min(512, mainDataBufferIndex);
                Buffer.BlockCopy(
                    mainDataBuffer, mainDataBufferIndex - keepBytes,
                    mainDataBuffer, 0, keepBytes);
                mainDataBufferIndex = keepBytes;
            }
            
            // Interleave output channels
            samplesDecoded = SpectralCoefficientsPerGranule * granulesPerFrame;
            int totalSamples = samplesDecoded * channelCount;
            
            if (pcmSamples.Length >= totalSamples)
            {
                if (channelCount == 1)
                {
                    // Mono: direct copy
                    pcmOutput.AsSpan(0, samplesDecoded).CopyTo(pcmSamples);
                }
                else
                {
                    // Stereo: interleave per-granule
                    // pcmOutput layout: [G0_ch0(576), G0_ch1(576), G1_ch0(576), G1_ch1(576)]
                    // Output layout: [G0_L, G0_R, G0_L, G0_R, ..., G1_L, G1_R, ...]
                    int outIdx = 0;
                    for (int gr = 0; gr < granulesPerFrame; gr++)
                    {
                        int ch0Base = (gr * channelCount) * SpectralCoefficientsPerGranule;
                        int ch1Base = ch0Base + SpectralCoefficientsPerGranule;
                        for (int i = 0; i < SpectralCoefficientsPerGranule; i++)
                        {
                            pcmSamples[outIdx++] = pcmOutput[ch0Base + i];
                            pcmSamples[outIdx++] = pcmOutput[ch1Base + i];
                        }
                    }
                }
            }
            
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// FFmpeg's <c>sample_rate_index</c>, 0-8: the three MPEG-1 rates, then MPEG-2's three,
    /// then MPEG-2.5's three, in the order <c>ff_band_size_long</c> and
    /// <c>ff_mpa_freq_tab</c> use.
    /// </summary>
    private static int SampleRateIndexOf(int sampleRate) => sampleRate switch
    {
        44100 => 0,
        48000 => 1,
        32000 => 2,
        22050 => 3,
        24000 => 4,
        16000 => 5,
        11025 => 6,
        12000 => 7,
        8000 => 8,
        _ => throw new NotSupportedException(
            $"MP3: {sampleRate} Hz is not one of Layer III's nine sample rates."),
    };
    
    /// <summary>
    /// Requantizes spectral coefficients to floating-point values.
    /// </summary>
    private void Requantize(
        ReadOnlySpan<int> spectral,
        in Mp3GranuleInfo granuleInfo,
        ReadOnlySpan<int> scalefactorsLong,
        ReadOnlySpan<int> scalefactorsShort,
        int gainAdjust,
        bool lsfPreflag,
        Span<float> output)
    {
        int globalGain = granuleInfo.GlobalGain + gainAdjust;
        // MPEG-1 carries preflag as a side-information bit; LSF has no such bit and decides
        // it from scalefac_compress instead, in DecodeMpeg2Scalefactors.
        int preflag = granuleInfo.PreFlag != 0 || lsfPreflag ? 1 : 0;
        int scalefactorScale = granuleInfo.ScalefactorScale;
        
        // scalefac_multiplier = 1 + scalefac_scale (ISO 11172-3 section 2.4.3.4)
        int scalefacMultiplier = 1 + scalefactorScale;
        
        // Base gain integer: matches FFmpeg's exponents_from_scale_factors() convention
        // gain = global_gain - 210 (integer, combined with scalefactors per-band)
        int baseGain = globalGain - 210;
        
        if (granuleInfo.WindowSwitchingFlag && granuleInfo.BlockType == 2)
        {
            // Short blocks or mixed blocks
            RequantizeShortBlocks(
                spectral, granuleInfo, scalefactorsLong, scalefactorsShort,
                baseGain, scalefacMultiplier, preflag, output);
        }
        else
        {
            // Long blocks
            RequantizeLongBlocks(
                spectral, scalefactorsLong, baseGain, scalefacMultiplier, preflag, output);
        }
    }
    
    /// <summary>
    /// Requantizes long block samples using double-precision gain computation.
    /// FFmpeg pre-computes pow43*gain in double and stores as float (expval_table).
    /// We match this by computing gain in double, multiplying with double pow43, then casting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RequantizeLongBlocks(
        ReadOnlySpan<int> spectral,
        ReadOnlySpan<int> scalefactors,
        int baseGain,
        int scalefacMultiplier,
        int preflag,
        Span<float> output)
    {
        ReadOnlySpan<int> sfbOffsets = Mp3HuffmanTables.GetScalefactorBandLong(sampleRate);
        ReadOnlySpan<int> pretab = Mp3HuffmanTables.Pretab;
        ReadOnlySpan<double> requantTableD = Mp3HuffmanTables.RequantTableDouble;
        
        for (int sfb = 0; sfb < sfbOffsets.Length - 1 && sfb < scalefactors.Length; sfb++)
        {
            int sfbStart = sfbOffsets[sfb];
            int sfbEnd = sfbOffsets[sfb + 1];
            
            // Compute gain in double precision to match FFmpeg's table-based approach.
            // FFmpeg: exponent = gain - ((sf+pretab) << shift) + 400; table lookup gives 2^(exp/4)
            // We compute the same in double, then cast to float at the final multiply.
            int sf = scalefactors[sfb];
            int pre = preflag != 0 && sfb < pretab.Length ? pretab[sfb] : 0;
            int intExponent = baseGain - ((sf + pre) << scalefacMultiplier);
            double gain = Math.Pow(2.0, intExponent * 0.25);
            
            for (int i = sfbStart; i < sfbEnd && i < spectral.Length; i++)
            {
                int is_value = spectral[i];
                if (is_value == 0)
                {
                    output[i] = 0.0f;
                }
                else
                {
                    int absValue = Math.Abs(is_value);
                    // Double-precision multiply, single cast to float at the end
                    double reqVal = absValue < requantTableD.Length
                        ? requantTableD[absValue]
                        : Math.Pow(absValue, 4.0 / 3.0);
                    
                    output[i] = (float)(is_value < 0 ? -gain * reqVal : gain * reqVal);
                }
            }
        }
    }
    
    /// <summary>
    /// Requantizes short block samples using double-precision gain computation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RequantizeShortBlocks(
        ReadOnlySpan<int> spectral,
        in Mp3GranuleInfo granuleInfo,
        ReadOnlySpan<int> scalefactorsLong,
        ReadOnlySpan<int> scalefactorsShort,
        int baseGain,
        int scalefacMultiplier,
        int preflag,
        Span<float> output)
    {
        ReadOnlySpan<int> sfbOffsetsLong = Mp3HuffmanTables.GetScalefactorBandLong(sampleRate);
        ReadOnlySpan<int> sfbOffsetsShort = Mp3HuffmanTables.GetScalefactorBandShort(sampleRate);
        ReadOnlySpan<int> pretab = Mp3HuffmanTables.Pretab;
        ReadOnlySpan<double> requantTableD = Mp3HuffmanTables.RequantTableDouble;
        
        if (granuleInfo.MixedBlockFlag)
        {
            // First 8 bands are long block
            for (int sfb = 0; sfb < 8 && sfb < sfbOffsetsLong.Length - 1; sfb++)
            {
                int sfbStart = sfbOffsetsLong[sfb];
                int sfbEnd = sfbOffsetsLong[sfb + 1];
                
                int sf = sfb < scalefactorsLong.Length ? scalefactorsLong[sfb] : 0;
                int pre = preflag != 0 && sfb < pretab.Length ? pretab[sfb] : 0;
                int intExponent = baseGain - ((sf + pre) << scalefacMultiplier);
                double gain = Math.Pow(2.0, intExponent * 0.25);
                
                for (int i = sfbStart; i < sfbEnd && i < spectral.Length; i++)
                {
                    int is_value = spectral[i];
                    if (is_value == 0)
                    {
                        output[i] = 0.0f;
                    }
                    else
                    {
                        int absValue = Math.Abs(is_value);
                        double reqVal = absValue < requantTableD.Length
                            ? requantTableD[absValue]
                            : Math.Pow(absValue, 4.0 / 3.0);
                        
                        output[i] = (float)(is_value < 0 ? -gain * reqVal : gain * reqVal);
                    }
                }
            }
        }
        
        // Remaining bands are short blocks
        // FFmpeg: gains[w] = gain - (subblock_gain[w] << 3)
        // Then per-band: v0 = gains[w] - (sf << shift) + 400
        int startSfb = granuleInfo.MixedBlockFlag ? 3 : 0;
        for (int sfb = startSfb; sfb < sfbOffsetsShort.Length - 1; sfb++)
        {
            int sfbWidth = sfbOffsetsShort[sfb + 1] - sfbOffsetsShort[sfb];
            
            for (int window = 0; window < 3; window++)
            {
                int sfIdx = sfb * 3 + window;
                int sf = sfIdx < scalefactorsShort.Length ? scalefactorsShort[sfIdx] : 0;
                int subblockGain = granuleInfo.SubblockGain[window];
                
                // FFmpeg: gains[w] = gain - (subblock_gain[w] << 3)
                // Then v0 = gains[w] - (sf << shift) + 400
                // Combined: baseGain - subblockGain*8 - (sf << shift)
                int intExponent = baseGain - (subblockGain << 3) - (sf << scalefacMultiplier);
                double gain = Math.Pow(2.0, intExponent * 0.25);
                
                int offset = sfbOffsetsShort[sfb] * 3 + window * sfbWidth;
                
                for (int i = 0; i < sfbWidth && offset + i < spectral.Length; i++)
                {
                    int idx = offset + i;
                    int is_value = spectral[idx];
                    
                    if (is_value == 0)
                    {
                        output[idx] = 0.0f;
                    }
                    else
                    {
                        int absValue = Math.Abs(is_value);
                        double reqVal = absValue < requantTableD.Length
                            ? requantTableD[absValue]
                            : Math.Pow(absValue, 4.0 / 3.0);
                        
                        output[idx] = (float)(is_value < 0 ? -gain * reqVal : gain * reqVal);
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Joint-stereo reconstruction: intensity stereo, mid/side, or both. Port of FFmpeg's
    /// compute_stereo() in mpegaudiodec_template.c.
    /// </summary>
    /// <remarks>
    /// Intensity stereo was previously not implemented at all - the flag was read and discarded,
    /// and mid/side was applied across the whole spectrum whenever its bit was set. That is wrong
    /// twice over when both are in use: the intensity region has to be rebuilt from channel 0
    /// scaled by a position-dependent pair of weights, and mid/side then applies only *below* that
    /// region. Real encoders switch mode_extension per frame, so the damage was intermittent -
    /// 1023 of 6532 frames of an ordinary file - which is why the conformance streams, which do
    /// not exercise it, still passed.
    /// </remarks>
    private void ApplyJointStereo(
        Span<float> ch0,
        Span<float> ch1,
        in Mp3GranuleInfo granule1,
        int[] scalefactorsLong1,
        int[] scalefactorsShort1)
    {
        bool intensityStereo = (currentHeader.ModeExtension & 1) != 0;
        bool msStereo = (currentHeader.ModeExtension & 2) != 0;

        if (!intensityStereo)
        {
            // Mid/side alone. No 1/sqrt(2) here: in that case the normalisation is folded into the
            // global gain (see gainAdjust in DecodeFrame), which is what FFmpeg's float path does.
            if (msStereo)
                ApplyMsStereo(ch0, ch1);
            return;
        }

        bool isLsf = currentHeader.Version != MpegVersion.Mpeg1;
        int scalefactorMax = isLsf ? 16 : 7;
        ReadOnlySpan<float> weights0, weights1;
        if (isLsf)
        {
            int table = granule1.ScalefactorCompress & 1;
            weights0 = Mp3HuffmanTables.GetIntensityTableLsf(table, 0);
            weights1 = Mp3HuffmanTables.GetIntensityTableLsf(table, 1);
        }
        else
        {
            weights0 = Mp3HuffmanTables.IntensityTable0;
            weights1 = Mp3HuffmanTables.IntensityTable1;
        }

        ComputeBandIndexes(in granule1, out int longEnd, out int shortStart);

        ReadOnlySpan<int> sfbOffsetsLong = Mp3HuffmanTables.GetScalefactorBandLong(sampleRate);
        ReadOnlySpan<int> sfbOffsetsShort = Mp3HuffmanTables.GetScalefactorBandShort(sampleRate);

        // Walk down from the top of the spectrum. Intensity coding applies only while channel 1 is
        // still silent; its first non-zero value ends it for that window, and everything below is
        // ordinary (optionally mid/side) stereo.
        int position = SpectralCoefficientsPerGranule;
        Span<bool> nonZeroFoundShort = stackalloc bool[3];

        for (int band = 12; band >= shortStart; band--)
        {
            int width = sfbOffsetsShort[band + 1] - sfbOffsetsShort[band];
            // The topmost band reuses the previous band's scale factor, as the spec requires.
            int scalefactorBand = Math.Min(band, 11);

            for (int window = 2; window >= 0; window--)
            {
                position -= width;
                bool applyIntensity = !nonZeroFoundShort[window];

                if (applyIntensity)
                {
                    for (int i = 0; i < width; i++)
                    {
                        if (ch1[position + i] != 0)
                        {
                            nonZeroFoundShort[window] = true;
                            applyIntensity = false;
                            break;
                        }
                    }
                }

                if (applyIntensity)
                {
                    int scalefactor = scalefactorsShort1[scalefactorBand * 3 + window];
                    if (scalefactor >= scalefactorMax)
                        applyIntensity = false;
                    else
                        ApplyIntensityBand(ch0, ch1, position, width,
                            weights0[scalefactor], weights1[scalefactor]);
                }

                if (!applyIntensity && msStereo)
                    ApplyMsStereoBand(ch0, ch1, position, width);
            }
        }

        bool nonZeroFound = nonZeroFoundShort[0] || nonZeroFoundShort[1] || nonZeroFoundShort[2];

        for (int band = longEnd - 1; band >= 0; band--)
        {
            int width = sfbOffsetsLong[band + 1] - sfbOffsetsLong[band];
            position -= width;

            bool applyIntensity = !nonZeroFound;
            if (applyIntensity)
            {
                for (int i = 0; i < width; i++)
                {
                    if (ch1[position + i] != 0)
                    {
                        nonZeroFound = true;
                        applyIntensity = false;
                        break;
                    }
                }
            }

            if (applyIntensity)
            {
                // The last band reuses the previous band's scale factor.
                int scalefactor = scalefactorsLong1[Math.Min(band, 20)];
                if (scalefactor >= scalefactorMax)
                    applyIntensity = false;
                else
                    ApplyIntensityBand(ch0, ch1, position, width,
                        weights0[scalefactor], weights1[scalefactor]);
            }

            if (!applyIntensity && msStereo)
                ApplyMsStereoBand(ch0, ch1, position, width);
        }
    }

    /// <summary>
    /// Both channels of an intensity-coded band are rebuilt from channel 0 alone, weighted by the
    /// stereo position that channel 1's scale factor encodes.
    /// </summary>
    private static void ApplyIntensityBand(
        Span<float> ch0, Span<float> ch1, int position, int width, float weight0, float weight1)
    {
        for (int i = 0; i < width; i++)
        {
            float value = ch0[position + i];
            ch0[position + i] = value * weight0;
            ch1[position + i] = value * weight1;
        }
    }

    /// <summary>
    /// Mid/side over a single band. Unlike the whole-spectrum path this carries an explicit
    /// 1/sqrt(2), because when intensity stereo is also in use the global gain has not absorbed it.
    /// </summary>
    private static void ApplyMsStereoBand(Span<float> ch0, Span<float> ch1, int position, int width)
    {
        const float InverseSqrt2 = 0.70710678118654752440f;
        for (int i = 0; i < width; i++)
        {
            float mid = ch0[position + i];
            float side = ch1[position + i];
            ch0[position + i] = (mid + side) * InverseSqrt2;
            ch1[position + i] = (mid - side) * InverseSqrt2;
        }
    }

    /// <summary>
    /// Where the long bands stop and the short bands start for a granule. Port of FFmpeg's
    /// compute_band_indexes().
    /// </summary>
    private void ComputeBandIndexes(in Mp3GranuleInfo granule, out int longEnd, out int shortStart)
    {
        if (granule.WindowSwitchingFlag && granule.BlockType == 2)
        {
            if (granule.MixedBlockFlag)
            {
                // A switched granule decodes its first 36 samples as long blocks.
                longEnd = sampleRate <= 24000 ? 8 : 6;
                shortStart = 3;
            }
            else
            {
                longEnd = 0;
                shortStart = 0;
            }
        }
        else
        {
            shortStart = 13;
            longEnd = 22;
        }
    }

    /// <summary>
    /// Applies Mid/Side stereo decoding.
    /// ch0 (mid) and ch1 (side) are transformed to left and right channels.
    /// Ported from FFmpeg's compute_stereo() MS-only path (butterflies_float).
    /// Note: FFmpeg's float path does NOT include 1/sqrt(2) — the normalization is in global gain.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyMsStereo(Span<float> ch0, Span<float> ch1)
    {
        for (int i = 0; i < SpectralCoefficientsPerGranule; i++)
        {
            float mid = ch0[i];
            float side = ch1[i];
            ch0[i] = mid + side;
            ch1[i] = mid - side;
        }
    }
    
    /// <summary>
    /// Anti-aliasing butterfly between adjacent subbands.
    /// Applied after requantization and before IMDCT.
    /// Ported from FFmpeg's compute_antialias() in mpegaudiodec_template.c.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyAntialias(Span<float> samples, in Mp3GranuleInfo granuleInfo)
    {
        // For pure short blocks without switch_point, no anti-aliasing
        if (granuleInfo.WindowSwitchingFlag && granuleInfo.BlockType == 2)
        {
            if (!granuleInfo.MixedBlockFlag)
                return;
            // Mixed blocks: only 1 subband boundary (between sb0 and sb1)
            ApplyAntialiasButterflies(samples, 18, 1);
        }
        else
        {
            // Long blocks: 31 subband boundaries
            ApplyAntialiasButterflies(samples, 18, 31);
        }
    }
    
    /// <summary>
    /// Performs the 8-point butterfly rotation at each subband boundary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyAntialiasButterflies(Span<float> samples, int startOffset, int count)
    {
        // csa_table[j][0] = cs, csa_table[j][1] = ca
        // cs = 1/sqrt(1 + ci^2), ca = ci/sqrt(1 + ci^2)
        // ci values from ISO 11172-3 Table B.9
        ReadOnlySpan<float> cs =
        [
            8.574929237365722656e-01f,
            8.817420005798339844e-01f,
            9.496286511421203613e-01f,
            9.833145737648010254e-01f,
            9.955177903175354004e-01f,
            9.991605877876281738e-01f,
            9.998992085456848145e-01f,
            9.999931454658508301e-01f
        ];
        ReadOnlySpan<float> ca =
        [
            -5.144957900047302246e-01f,
            -4.717319905757904053e-01f,
            -3.133774697780609131e-01f,
            -1.819131970405578613e-01f,
            -9.457419067621231079e-02f,
            -4.096558317542076111e-02f,
            -1.419856864959001541e-02f,
            -3.699974622577428818e-03f
        ];
        
        int ptr = startOffset;
        for (int i = 0; i < count; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                float tmp0 = samples[ptr - 1 - j];
                float tmp1 = samples[ptr + j];
                samples[ptr - 1 - j] = tmp0 * cs[j] - tmp1 * ca[j];
                samples[ptr + j]     = tmp0 * ca[j] + tmp1 * cs[j];
            }
            ptr += 18;
        }
    }
    
    /// <summary>
    /// Reorders short block samples from scalefactor band order to frequency order.
    /// </summary>
    private void ReorderShortBlocks(Span<float> samples, bool mixedBlocks)
    {
        Span<float> temp = stackalloc float[SpectralCoefficientsPerGranule];
        samples.CopyTo(temp);
        
        ReadOnlySpan<int> sfbOffsetsShort = Mp3HuffmanTables.GetScalefactorBandShort(sampleRate);
        
        int startSfb = mixedBlocks ? 3 : 0;
        int startSample = mixedBlocks ? 36 : 0;
        
        int destIdx = startSample;
        
        for (int sfb = startSfb; sfb < sfbOffsetsShort.Length - 1; sfb++)
        {
            int sfbWidth = sfbOffsetsShort[sfb + 1] - sfbOffsetsShort[sfb];
            
            for (int sample = 0; sample < sfbWidth; sample++)
            {
                for (int window = 0; window < 3; window++)
                {
                    int srcIdx = sfbOffsetsShort[sfb] * 3 + window * sfbWidth + sample;
                    if (srcIdx < temp.Length && destIdx < samples.Length)
                    {
                        samples[destIdx++] = temp[srcIdx];
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Resets the decoder state.
    /// </summary>
    public void Reset()
    {
        Array.Clear(overlapBuffer);
        foreach (var fb in filterbanks)
            fb.Reset();
        Array.Clear(mainDataBuffer);
        mainDataBufferIndex = 0;
        isInitialized = false;
    }
}

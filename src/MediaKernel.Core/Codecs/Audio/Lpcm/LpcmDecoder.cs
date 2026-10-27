// Linear PCM decoder for DVD, Blu-ray, and DVD-Audio formats
// Reference: VLC lpcm.c, DVD specifications, Blu-ray specifications

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.Audio.Lpcm;

/// <summary>
/// LPCM variant types.
/// </summary>
public enum LpcmVariant : byte
{
    /// <summary>DVD Video LPCM (VOB)</summary>
    DvdVideo = 0,
    /// <summary>DVD-Audio LPCM (AOB)</summary>
    DvdAudio = 1,
    /// <summary>Blu-ray LPCM</summary>
    BluRay = 2,
    /// <summary>WIDI/IEEE 1394 LPCM</summary>
    Widi = 3
}

/// <summary>
/// LPCM frame header information.
/// </summary>
public readonly struct LpcmFrameHeader
{
    public readonly LpcmVariant Variant;
    public readonly int SampleRate;
    public readonly int ChannelCount;
    public readonly int BitsPerSample;
    public readonly int PayloadOffset;
    public readonly bool Emphasis;
    public readonly bool Mute;
    public readonly byte DynamicRange;
    
    public LpcmFrameHeader(LpcmVariant variant, int sampleRate, int channelCount,
        int bitsPerSample, int payloadOffset, bool emphasis = false, bool mute = false,
        byte dynamicRange = 0x80)
    {
        Variant = variant;
        SampleRate = sampleRate;
        ChannelCount = channelCount;
        BitsPerSample = bitsPerSample;
        PayloadOffset = payloadOffset;
        Emphasis = emphasis;
        Mute = mute;
        DynamicRange = dynamicRange;
    }
}

/// <summary>
/// Decodes Linear PCM audio from DVD, Blu-ray, and DVD-Audio streams.
/// </summary>
/// <remarks>
/// DVD-Video LPCM: 16/20/24-bit, 48/96 kHz, 1-8 channels (6-byte header)
/// Blu-ray LPCM: 16/20/24-bit, 48/96/192 kHz, 1-8 channels (4-byte header)
/// DVD-Audio LPCM: 16/20/24-bit, 44.1-192 kHz, 1-6 channels per group (11-byte header)
/// </remarks>
public sealed class LpcmDecoder
{
    private const int DvdVideoHeaderLength = 6;
    private const int BluRayHeaderLength = 4;
    private const int DvdAudioHeaderLength = 11;
    private const int WidiHeaderLength = 4;
    
    private readonly LpcmVariant _variant;
    private long _samplesDecoded;
    
    /// <summary>Total samples decoded since creation or reset.</summary>
    public long SamplesDecoded => _samplesDecoded;
    
    public LpcmDecoder(LpcmVariant variant)
    {
        _variant = variant;
    }
    
    /// <summary>
    /// Parses the LPCM header from the frame data.
    /// </summary>
    public bool TryParseHeader(ReadOnlySpan<byte> frameData, out LpcmFrameHeader header)
    {
        header = default;
        
        int requiredLength = _variant switch
        {
            LpcmVariant.DvdVideo => DvdVideoHeaderLength,
            LpcmVariant.BluRay => BluRayHeaderLength,
            LpcmVariant.DvdAudio => DvdAudioHeaderLength,
            LpcmVariant.Widi => WidiHeaderLength,
            _ => 0
        };
        
        if (frameData.Length < requiredLength)
            return false;
        
        return _variant switch
        {
            LpcmVariant.DvdVideo => TryParseDvdVideoHeader(frameData, out header),
            LpcmVariant.BluRay => TryParseBluRayHeader(frameData, out header),
            LpcmVariant.DvdAudio => TryParseDvdAudioHeader(frameData, out header),
            LpcmVariant.Widi => TryParseWidiHeader(frameData, out header),
            _ => false
        };
    }
    
    /// <summary>
    /// Decodes LPCM audio data to normalized float samples.
    /// </summary>
    /// <returns>Number of samples decoded per channel.</returns>
    public int Decode(ReadOnlySpan<byte> frameData, Span<float> outputSamples, out LpcmFrameHeader header)
    {
        if (!TryParseHeader(frameData, out header))
            return 0;
        
        var payload = frameData.Slice(header.PayloadOffset);
        int bytesPerSample = header.BitsPerSample switch
        {
            16 => 2,
            20 => 3, // Packed 20-bit uses 2.5 bytes, rounded up
            24 => 3,
            _ => 2
        };
        
        int samplesPerChannel = CalculateSampleCount(payload.Length, header.ChannelCount, header.BitsPerSample);
        int totalSamples = samplesPerChannel * header.ChannelCount;
        
        if (outputSamples.Length < totalSamples)
            return 0;
        
        int decoded = header.BitsPerSample switch
        {
            16 => Decode16Bit(payload, outputSamples, header.ChannelCount, samplesPerChannel),
            20 => Decode20Bit(payload, outputSamples, header.ChannelCount, samplesPerChannel),
            24 => Decode24Bit(payload, outputSamples, header.ChannelCount, samplesPerChannel),
            _ => 0
        };
        
        _samplesDecoded += samplesPerChannel;
        return samplesPerChannel;
    }
    
    /// <summary>
    /// Decodes LPCM audio to 32-bit integer samples (useful for 24-bit preservation).
    /// </summary>
    public int DecodeToInt32(ReadOnlySpan<byte> frameData, Span<int> outputSamples, out LpcmFrameHeader header)
    {
        if (!TryParseHeader(frameData, out header))
            return 0;
        
        var payload = frameData.Slice(header.PayloadOffset);
        int samplesPerChannel = CalculateSampleCount(payload.Length, header.ChannelCount, header.BitsPerSample);
        int totalSamples = samplesPerChannel * header.ChannelCount;
        
        if (outputSamples.Length < totalSamples)
            return 0;
        
        int decoded = header.BitsPerSample switch
        {
            16 => Decode16BitToInt32(payload, outputSamples, header.ChannelCount, samplesPerChannel),
            20 => Decode20BitToInt32(payload, outputSamples, header.ChannelCount, samplesPerChannel),
            24 => Decode24BitToInt32(payload, outputSamples, header.ChannelCount, samplesPerChannel),
            _ => 0
        };
        
        _samplesDecoded += samplesPerChannel;
        return samplesPerChannel;
    }
    
    /// <summary>Resets decoder state.</summary>
    public void Reset()
    {
        _samplesDecoded = 0;
    }
    
    #region Header Parsing
    
    private static bool TryParseDvdVideoHeader(ReadOnlySpan<byte> data, out LpcmFrameHeader header)
    {
        // DVD-Video LPCM header (6 bytes):
        // [0]: Number of frames in packet
        // [1-2]: First access unit pointer (usually 0x0003)
        // [3]: bits 7=emphasis, 6=mute, 5=reserved, 4-0=frame number
        // [4]: bits 7-6=quantization, 5-4=sample rate, 3=reserved, 2-0=channels-1
        // [5]: Dynamic range control (0x80 = neutral)
        
        byte flags = data[3];
        bool emphasis = (flags & 0x80) != 0;
        bool mute = (flags & 0x40) != 0;
        
        byte audioInfo = data[4];
        int quantization = (audioInfo >> 6) & 0x03;
        int sampleRateCode = (audioInfo >> 4) & 0x03;
        int channelCount = (audioInfo & 0x07) + 1;
        
        byte dynamicRange = data[5];
        
        int bitsPerSample = quantization switch
        {
            0 => 16,
            1 => 20,
            2 => 24,
            _ => 16 // 3 is illegal, default to 16
        };
        
        int sampleRate = sampleRateCode switch
        {
            0 => 48000,
            1 => 96000,
            2 => 44100,
            3 => 32000,
            _ => 48000
        };
        
        header = new LpcmFrameHeader(
            LpcmVariant.DvdVideo,
            sampleRate,
            channelCount,
            bitsPerSample,
            DvdVideoHeaderLength,
            emphasis,
            mute,
            dynamicRange);
        
        return true;
    }
    
    private static bool TryParseBluRayHeader(ReadOnlySpan<byte> data, out LpcmFrameHeader header)
    {
        // Blu-ray LPCM header (4 bytes):
        // [0-1]: Unknown (usually 0)
        // [2]: bits 7-4=channel count code, 3-0=sample rate code
        // [3]: bits 7-6=bits per sample code, 5-0=unknown
        
        byte channelRateInfo = data[2];
        int channelCode = (channelRateInfo >> 4) & 0x0F;
        int sampleRateCode = channelRateInfo & 0x0F;
        
        byte bitsInfo = data[3];
        int bitsCode = (bitsInfo >> 6) & 0x03;
        
        int channelCount = channelCode switch
        {
            1 => 1,  // Mono
            3 => 2,  // Stereo
            4 => 3,  // 3.0
            5 => 4,  // 4.0
            6 => 5,  // 5.0
            7 => 6,  // 5.1
            8 => 7,  // 7.0
            9 => 8,  // 7.1
            _ => 2   // Default stereo
        };
        
        int sampleRate = sampleRateCode switch
        {
            1 => 48000,
            4 => 96000,
            5 => 192000,
            _ => 48000
        };
        
        int bitsPerSample = bitsCode switch
        {
            1 => 16,
            2 => 20,
            3 => 24,
            _ => 16
        };
        
        header = new LpcmFrameHeader(
            LpcmVariant.BluRay,
            sampleRate,
            channelCount,
            bitsPerSample,
            BluRayHeaderLength);
        
        return true;
    }
    
    private static bool TryParseDvdAudioHeader(ReadOnlySpan<byte> data, out LpcmFrameHeader header)
    {
        // DVD-Audio LPCM header (11 bytes):
        // [0]: Continuity counter
        // [1-2]: Header size
        // [3-4]: Byte pointer to first audio frame
        // [5]: Audio info (0x10=stereo, 0x00=surround)
        // [6]: Sample size group 1 (bits 7-4) + group 2 (bits 3-0)
        // [7]: Sample rate group 1 (bits 7-4) + group 2 (bits 3-0)
        // [8]: Unknown
        // [9]: Group assignment
        // [10]: Unknown
        
        int headerSize = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(1));
        int firstAudioByte = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(3));
        
        byte sizeInfo = data[6];
        int sizeCode1 = (sizeInfo >> 4) & 0x0F;
        
        byte rateInfo = data[7];
        int rateCode1 = (rateInfo >> 4) & 0x0F;
        
        byte groupAssignment = data[9];
        
        int bitsPerSample = sizeCode1 switch
        {
            0 => 16,
            1 => 20,
            2 => 24,
            _ => 16
        };
        
        int sampleRate = rateCode1 switch
        {
            0 => 48000,
            1 => 96000,
            2 => 192000,
            8 => 44100,
            9 => 88200,
            10 => 176400,
            _ => 48000
        };
        
        // Group assignment determines channel count
        int channelCount = GetDvdAudioChannelCount(groupAssignment);
        
        header = new LpcmFrameHeader(
            LpcmVariant.DvdAudio,
            sampleRate,
            channelCount,
            bitsPerSample,
            Math.Max(firstAudioByte, DvdAudioHeaderLength));
        
        return true;
    }
    
    private static bool TryParseWidiHeader(ReadOnlySpan<byte> data, out LpcmFrameHeader header)
    {
        // WIDI/IEEE 1394 LPCM header (4 bytes):
        // [0]: Sub stream ID (0xA0)
        // [1]: Frame header count (0x06)
        // [2]: bits 7-1=0, 0=audio emphasis
        // [3]: bits 7-6=quantization, 5-3=sample rate, 2-0=channels
        
        bool emphasis = (data[2] & 0x01) != 0;
        
        byte audioInfo = data[3];
        int quantization = (audioInfo >> 6) & 0x03;
        int sampleRateCode = (audioInfo >> 3) & 0x07;
        int channelCode = audioInfo & 0x07;
        
        int bitsPerSample = quantization switch
        {
            0 => 16,
            1 => 20,
            2 => 24,
            _ => 16
        };
        
        int sampleRate = sampleRateCode switch
        {
            1 => 44100,
            2 => 48000,
            _ => 48000
        };
        
        int channelCount = channelCode switch
        {
            0 => 2, // Dual mono
            1 => 2, // Stereo
            _ => 2
        };
        
        header = new LpcmFrameHeader(
            LpcmVariant.Widi,
            sampleRate,
            channelCount,
            bitsPerSample,
            WidiHeaderLength,
            emphasis);
        
        return true;
    }
    
    private static int GetDvdAudioChannelCount(byte groupAssignment)
    {
        // Group assignment byte determines channel layout
        // Common values: 0=mono, 1=stereo, 4=4ch, 21=6ch (5.1)
        return groupAssignment switch
        {
            0 => 1,
            1 => 2,
            2 => 3,
            3 => 4,
            4 => 4,
            5 => 5,
            6 => 6,
            20 => 5,
            21 => 6,
            _ => 2
        };
    }
    
    #endregion
    
    #region Sample Decoding
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CalculateSampleCount(int payloadBytes, int channelCount, int bitsPerSample)
    {
        if (channelCount == 0) return 0;
        
        int bytesPerSample = bitsPerSample switch
        {
            16 => 2,
            20 => 0, // 20-bit is packed, calculated below
            24 => 3,
            _ => 2
        };
        
        if (bitsPerSample == 20)
        {
            // 20-bit: 2 samples take 5 bytes (2.5 bytes each)
            int samplePairs = payloadBytes / (5 * channelCount);
            return samplePairs * 2;
        }
        
        return payloadBytes / (bytesPerSample * channelCount);
    }
    
    private static int Decode16Bit(ReadOnlySpan<byte> payload, Span<float> output, 
        int channelCount, int samplesPerChannel)
    {
        const float scale = 1.0f / 32768.0f;
        int totalSamples = samplesPerChannel * channelCount;
        int offset = 0;
        
        // LPCM is stored in big-endian, interleaved
        for (int i = 0; i < totalSamples && offset + 1 < payload.Length; i++)
        {
            short sample = BinaryPrimitives.ReadInt16BigEndian(payload.Slice(offset));
            output[i] = sample * scale;
            offset += 2;
        }
        
        return samplesPerChannel;
    }
    
    private static int Decode20Bit(ReadOnlySpan<byte> payload, Span<float> output,
        int channelCount, int samplesPerChannel)
    {
        // 20-bit samples are packed: 2 samples = 5 bytes
        // Sample 1: bytes 0-1 + high nibble of byte 4
        // Sample 2: bytes 2-3 + low nibble of byte 4
        const float scale = 1.0f / 524288.0f; // 2^19
        
        int samplePairs = samplesPerChannel / 2;
        int outputIndex = 0;
        int offset = 0;
        
        for (int pair = 0; pair < samplePairs * channelCount; pair++)
        {
            if (offset + 4 >= payload.Length) break;
            
            // First sample: big-endian 16-bit + 4 high bits
            int sample1 = (payload[offset] << 12) | (payload[offset + 1] << 4) | 
                          ((payload[offset + 4] >> 4) & 0x0F);
            if ((sample1 & 0x80000) != 0) sample1 |= unchecked((int)0xFFF00000); // Sign extend
            
            // Second sample: big-endian 16-bit + 4 low bits  
            int sample2 = (payload[offset + 2] << 12) | (payload[offset + 3] << 4) |
                          (payload[offset + 4] & 0x0F);
            if ((sample2 & 0x80000) != 0) sample2 |= unchecked((int)0xFFF00000);
            
            output[outputIndex++] = sample1 * scale;
            output[outputIndex++] = sample2 * scale;
            offset += 5;
        }
        
        return samplesPerChannel;
    }
    
    private static int Decode24Bit(ReadOnlySpan<byte> payload, Span<float> output,
        int channelCount, int samplesPerChannel)
    {
        const float scale = 1.0f / 8388608.0f; // 2^23
        int totalSamples = samplesPerChannel * channelCount;
        int offset = 0;
        
        for (int i = 0; i < totalSamples && offset + 2 < payload.Length; i++)
        {
            // Big-endian 24-bit, sign extend to 32-bit
            int sample = (payload[offset] << 16) | (payload[offset + 1] << 8) | payload[offset + 2];
            if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
            
            output[i] = sample * scale;
            offset += 3;
        }
        
        return samplesPerChannel;
    }
    
    private static int Decode16BitToInt32(ReadOnlySpan<byte> payload, Span<int> output,
        int channelCount, int samplesPerChannel)
    {
        int totalSamples = samplesPerChannel * channelCount;
        int offset = 0;
        
        for (int i = 0; i < totalSamples && offset + 1 < payload.Length; i++)
        {
            short sample = BinaryPrimitives.ReadInt16BigEndian(payload.Slice(offset));
            output[i] = sample << 16; // Scale to 32-bit
            offset += 2;
        }
        
        return samplesPerChannel;
    }
    
    private static int Decode20BitToInt32(ReadOnlySpan<byte> payload, Span<int> output,
        int channelCount, int samplesPerChannel)
    {
        int samplePairs = samplesPerChannel / 2;
        int outputIndex = 0;
        int offset = 0;
        
        for (int pair = 0; pair < samplePairs * channelCount; pair++)
        {
            if (offset + 4 >= payload.Length) break;
            
            int sample1 = (payload[offset] << 12) | (payload[offset + 1] << 4) |
                          ((payload[offset + 4] >> 4) & 0x0F);
            if ((sample1 & 0x80000) != 0) sample1 |= unchecked((int)0xFFF00000);
            
            int sample2 = (payload[offset + 2] << 12) | (payload[offset + 3] << 4) |
                          (payload[offset + 4] & 0x0F);
            if ((sample2 & 0x80000) != 0) sample2 |= unchecked((int)0xFFF00000);
            
            output[outputIndex++] = sample1 << 12; // Scale to 32-bit
            output[outputIndex++] = sample2 << 12;
            offset += 5;
        }
        
        return samplesPerChannel;
    }
    
    private static int Decode24BitToInt32(ReadOnlySpan<byte> payload, Span<int> output,
        int channelCount, int samplesPerChannel)
    {
        int totalSamples = samplesPerChannel * channelCount;
        int offset = 0;
        
        for (int i = 0; i < totalSamples && offset + 2 < payload.Length; i++)
        {
            int sample = (payload[offset] << 16) | (payload[offset + 1] << 8) | payload[offset + 2];
            if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
            
            output[i] = sample << 8; // Scale to 32-bit
            offset += 3;
        }
        
        return samplesPerChannel;
    }
    
    #endregion
}

/// <summary>
/// LPCM encoder for creating DVD-compatible LPCM streams.
/// </summary>
public sealed class LpcmEncoder
{
    private readonly int _sampleRate;
    private readonly int _channelCount;
    private readonly int _bitsPerSample;
    private int _frameNumber;
    
    public LpcmEncoder(int sampleRate, int channelCount, int bitsPerSample = 16)
    {
        if (sampleRate != 48000 && sampleRate != 96000 && sampleRate != 44100 && sampleRate != 32000)
            throw new ArgumentException("DVD LPCM supports only 48000, 96000, 44100, or 32000 Hz", nameof(sampleRate));
        
        if (channelCount < 1 || channelCount > 8)
            throw new ArgumentException("DVD LPCM supports 1-8 channels", nameof(channelCount));
        
        if (bitsPerSample != 16 && bitsPerSample != 20 && bitsPerSample != 24)
            throw new ArgumentException("DVD LPCM supports 16, 20, or 24 bits per sample", nameof(bitsPerSample));
        
        _sampleRate = sampleRate;
        _channelCount = channelCount;
        _bitsPerSample = bitsPerSample;
    }
    
    /// <summary>
    /// Encodes PCM samples to DVD-Video LPCM format.
    /// </summary>
    /// <param name="samples">Input float samples, interleaved if multi-channel.</param>
    /// <param name="output">Output buffer for encoded LPCM data.</param>
    /// <returns>Number of bytes written to output.</returns>
    public int Encode(ReadOnlySpan<float> samples, Span<byte> output)
    {
        int samplesPerChannel = samples.Length / _channelCount;
        int payloadBytes = CalculatePayloadSize(samplesPerChannel);
        int totalBytes = DvdVideoHeaderLength + payloadBytes;
        
        if (output.Length < totalBytes)
            return 0;
        
        // Write DVD-Video LPCM header
        WriteDvdVideoHeader(output, samplesPerChannel);
        
        // Encode samples
        var payload = output.Slice(DvdVideoHeaderLength);
        
        if (_bitsPerSample == 16)
            Encode16Bit(samples, payload);
        else if (_bitsPerSample == 24)
            Encode24Bit(samples, payload);
        
        _frameNumber++;
        return totalBytes;
    }
    
    private const int DvdVideoHeaderLength = 6;
    
    private void WriteDvdVideoHeader(Span<byte> output, int samplesPerChannel)
    {
        // Frame count in packet
        output[0] = 1;
        // First access unit pointer
        output[1] = 0x00;
        output[2] = 0x03;
        // Flags (no emphasis, no mute) + frame number
        output[3] = (byte)(_frameNumber & 0x1F);
        
        // Audio info: quantization (bits 7-6), sample rate (bits 5-4), channels-1 (bits 2-0)
        int quantCode = _bitsPerSample switch { 20 => 1, 24 => 2, _ => 0 };
        int rateCode = _sampleRate switch { 96000 => 1, 44100 => 2, 32000 => 3, _ => 0 };
        output[4] = (byte)((quantCode << 6) | (rateCode << 4) | (_channelCount - 1));
        
        // Dynamic range (neutral)
        output[5] = 0x80;
    }
    
    private int CalculatePayloadSize(int samplesPerChannel)
    {
        int bytesPerSample = _bitsPerSample switch { 24 => 3, 20 => 3, _ => 2 };
        return samplesPerChannel * _channelCount * bytesPerSample;
    }
    
    private void Encode16Bit(ReadOnlySpan<float> samples, Span<byte> output)
    {
        int offset = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            int sample = (int)(samples[i] * 32767.0f);
            sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16BigEndian(output.Slice(offset), (short)sample);
            offset += 2;
        }
    }
    
    private void Encode24Bit(ReadOnlySpan<float> samples, Span<byte> output)
    {
        int offset = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            int sample = (int)(samples[i] * 8388607.0f);
            sample = Math.Clamp(sample, -8388608, 8388607);
            
            // Big-endian 24-bit
            output[offset] = (byte)((sample >> 16) & 0xFF);
            output[offset + 1] = (byte)((sample >> 8) & 0xFF);
            output[offset + 2] = (byte)(sample & 0xFF);
            offset += 3;
        }
    }
}

// MediaKernel - Opus Audio Packetizer
// Parses Opus audio packets and header for codec configuration.
// Based on RFC 6716 (Opus codec), RFC 7845 (Ogg encapsulation)

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// Opus channel mapping family.
/// </summary>
public enum OpusChannelMapping : byte
{
    /// <summary>Mono or stereo (1-2 channels).</summary>
    MonoStereo = 0,
    /// <summary>Vorbis channel ordering (1-8 channels).</summary>
    Vorbis = 1,
    /// <summary>Ambisonics (projection format, 1-255 channels).</summary>
    Ambisonics = 2,
    /// <summary>Ambisonics with demixing matrix (1-255 channels).</summary>
    AmbisonicsWithMatrix = 3
}

/// <summary>
/// Opus packet configuration from TOC byte.
/// </summary>
public enum OpusPacketMode : byte
{
    /// <summary>SILK-only narrowband/mediumband.</summary>
    Silk = 0,
    /// <summary>Hybrid (SILK + CELT).</summary>
    Hybrid = 1,
    /// <summary>CELT-only wideband.</summary>
    Celt = 2
}

/// <summary>
/// Opus header information from OpusHead packet.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct OpusHeader
{
    /// <summary>Opus header magic: "OpusHead".</summary>
    public static ReadOnlySpan<byte> Magic => "OpusHead"u8;
    
    /// <summary>Version number (0 or 1).</summary>
    public readonly byte Version;
    
    /// <summary>Number of output channels (1-255).</summary>
    public readonly byte ChannelCount;
    
    /// <summary>Pre-skip samples (typically 3840 = 80ms).</summary>
    public readonly ushort PreSkip;
    
    /// <summary>Original input sample rate (informational only).</summary>
    public readonly int InputSampleRate;
    
    /// <summary>Output gain in Q7.8 dB (typically 0).</summary>
    public readonly short OutputGain;
    
    /// <summary>Channel mapping family.</summary>
    public readonly OpusChannelMapping ChannelMapping;
    
    /// <summary>Number of streams (for multichannel).</summary>
    public readonly byte StreamCount;
    
    /// <summary>Number of coupled (stereo) streams.</summary>
    public readonly byte CoupledCount;
    
    /// <summary>Channel-to-stream mapping (for multichannel).</summary>
    public readonly byte[] StreamMap;
    
    /// <summary>Whether this header is valid.</summary>
    public readonly bool IsValid;

    public OpusHeader(
        byte version, byte channelCount, ushort preSkip,
        int inputSampleRate, short outputGain, OpusChannelMapping channelMapping,
        byte streamCount, byte coupledCount, byte[]? streamMap)
    {
        Version = version;
        ChannelCount = channelCount;
        PreSkip = preSkip;
        InputSampleRate = inputSampleRate;
        OutputGain = outputGain;
        ChannelMapping = channelMapping;
        StreamCount = streamCount;
        CoupledCount = coupledCount;
        StreamMap = streamMap ?? [];
        IsValid = true;
    }
}

/// <summary>
/// Parsed information from an Opus audio packet TOC byte.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct OpusPacketInfo
{
    /// <summary>Packet mode (SILK, Hybrid, or CELT).</summary>
    public readonly OpusPacketMode Mode;
    
    /// <summary>Bandwidth (0=NB, 1=MB, 2=WB, 3=SWB, 4=FB).</summary>
    public readonly byte Bandwidth;
    
    /// <summary>Number of samples per frame.</summary>
    public readonly int SamplesPerFrame;
    
    /// <summary>Number of frames in this packet.</summary>
    public readonly int FrameCount;
    
    /// <summary>Total samples in this packet.</summary>
    public int TotalSamples => SamplesPerFrame * FrameCount;
    
    /// <summary>Duration in microseconds at 48kHz.</summary>
    public int DurationUs => TotalSamples * 1000000 / 48000;
    
    /// <summary>Is this a stereo packet.</summary>
    public readonly bool IsStereo;
    
    /// <summary>Whether this packet info is valid.</summary>
    public readonly bool IsValid;

    public OpusPacketInfo(
        OpusPacketMode mode, byte bandwidth, int samplesPerFrame,
        int frameCount, bool isStereo)
    {
        Mode = mode;
        Bandwidth = bandwidth;
        SamplesPerFrame = samplesPerFrame;
        FrameCount = frameCount;
        IsStereo = isStereo;
        IsValid = true;
    }
}

/// <summary>
/// Opus audio packetizer.
/// Handles Opus header parsing and packet duration calculation.
/// </summary>
public sealed class OpusPacketizer
{
    /// <summary>Internal sample rate (always 48kHz for Opus).</summary>
    public const int SampleRate = 48000;
    
    /// <summary>Default pre-skip samples (80ms).</summary>
    public const int DefaultPreSkip = 3840;
    
    /// <summary>Minimum valid samples per packet (2.5ms).</summary>
    public const int MinSamplesPerPacket = 120;
    
    /// <summary>Maximum valid samples per packet (120ms).</summary>
    public const int MaxSamplesPerPacket = 5760;
    
    /// <summary>Standard frame size (20ms at 48kHz).</summary>
    public const int StandardFrameSize = 960;
    
    // Samples per frame at 48kHz output rate
    // SILK: 10/20/40/60ms durations
    private static readonly int[] SilkFrameSizes = [480, 960, 1920, 2880]; // 10/20/40/60ms at 48kHz
    // Hybrid: 10/20ms durations
    private static readonly int[] HybridFrameSizes = [480, 960]; // 10/20ms at 48kHz
    // CELT: 2.5/5/10/20ms durations
    private static readonly int[] CeltFrameSizes = [120, 240, 480, 960]; // 2.5/5/10/20ms at 48kHz
    
    private OpusHeader _header;
    
    /// <summary>
    /// Gets the parsed Opus header if available.
    /// </summary>
    public OpusHeader Header => _header;
    
    /// <summary>
    /// Parses an OpusHead header packet.
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out OpusHeader header)
    {
        header = default;
        
        // Minimum size: 19 bytes for mono/stereo
        if (data.Length < 19)
            return false;
        
        // Check magic
        if (!data[..8].SequenceEqual(OpusHeader.Magic))
            return false;
        
        byte version = data[8];
        if (version > 1)
            return false;
        
        byte channels = data[9];
        if (channels == 0)
            return false;
        
        ushort preSkip = (ushort)(data[10] | (data[11] << 8));
        int inputSampleRate = data[12] | (data[13] << 8) | (data[14] << 16) | (data[15] << 24);
        short outputGain = (short)(data[16] | (data[17] << 8));
        var channelMapping = (OpusChannelMapping)data[18];
        
        byte streamCount = 1;
        byte coupledCount = (byte)(channels > 1 ? 1 : 0);
        byte[]? streamMap = null;
        
        // Extended mapping for multichannel
        if (channelMapping != OpusChannelMapping.MonoStereo)
        {
            if (data.Length < 21 + channels)
                return false;
            
            streamCount = data[19];
            coupledCount = data[20];
            
            if (streamCount == 0)
                return false;
            
            streamMap = data.Slice(21, channels).ToArray();
        }
        
        header = new OpusHeader(
            version, channels, preSkip,
            inputSampleRate, outputGain, channelMapping,
            streamCount, coupledCount, streamMap);
        
        return true;
    }
    
    /// <summary>
    /// Parses packet information from the TOC byte of an Opus packet.
    /// </summary>
    public static bool TryParsePacketInfo(ReadOnlySpan<byte> data, out OpusPacketInfo info)
    {
        info = default;
        
        if (data.Length < 1)
            return false;
        
        byte toc = data[0];
        
        // TOC byte structure:
        // Bits 0-1: Frame count code
        // Bit 2: Stereo flag
        // Bits 3-4: Bandwidth/duration encoding
        // Bits 5-7: Configuration (mode + bandwidth)
        
        int config = toc >> 3;           // Bits 3-7
        bool stereo = (toc & 0x04) != 0; // Bit 2
        int frameCountCode = toc & 0x03; // Bits 0-1
        
        // Determine mode and bandwidth from config
        OpusPacketMode mode;
        byte bandwidth;
        int samplesPerFrame;
        
        if (config < 12)
        {
            // SILK-only modes (configs 0-11)
            mode = OpusPacketMode.Silk;
            bandwidth = (byte)(config / 4); // 0=NB, 1=MB, 2=WB
            int durationIndex = config % 4;
            samplesPerFrame = SilkFrameSizes[durationIndex];
        }
        else if (config < 16)
        {
            // Hybrid modes (configs 12-15)
            mode = OpusPacketMode.Hybrid;
            bandwidth = (byte)(3 + (config - 12) / 2); // 3=SWB, 4=FB
            int durationIndex = (config - 12) % 2;
            samplesPerFrame = HybridFrameSizes[durationIndex];
        }
        else
        {
            // CELT-only modes (configs 16-31)
            mode = OpusPacketMode.Celt;
            bandwidth = (byte)((config - 16) / 4); // 0-3 for NB/WB/SWB/FB
            int durationIndex = (config - 16) % 4;
            samplesPerFrame = CeltFrameSizes[durationIndex];
        }
        
        // Determine frame count from code
        int frameCount = frameCountCode switch
        {
            0 => 1, // Single frame
            1 => 2, // Two equal-size frames
            2 => 2, // Two variable-size frames
            3 => ParseFrameCountCode3(data), // Signaled in following byte
            _ => 1
        };
        
        if (frameCount < 1)
            return false;
        
        // Validate total samples
        int totalSamples = samplesPerFrame * frameCount;
        if (totalSamples < MinSamplesPerPacket || totalSamples > MaxSamplesPerPacket)
            return false;
        
        info = new OpusPacketInfo(mode, bandwidth, samplesPerFrame, frameCount, stereo);
        return true;
    }
    
    /// <summary>
    /// Gets the number of samples in an Opus packet.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPacketSamples(ReadOnlySpan<byte> data)
    {
        if (!TryParsePacketInfo(data, out var info))
            return 0;
        return info.TotalSamples;
    }
    
    /// <summary>
    /// Gets the duration of an Opus packet in microseconds.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPacketDurationUs(ReadOnlySpan<byte> data)
    {
        if (!TryParsePacketInfo(data, out var info))
            return 0;
        return info.DurationUs;
    }
    
    /// <summary>
    /// Sets the header for this packetizer.
    /// </summary>
    public void SetHeader(OpusHeader header)
    {
        _header = header;
    }
    
    /// <summary>
    /// Validates an Opus packet.
    /// </summary>
    public static bool IsValidPacket(ReadOnlySpan<byte> data)
    {
        return TryParsePacketInfo(data, out _);
    }
    
    private static int ParseFrameCountCode3(ReadOnlySpan<byte> data)
    {
        // Code 3 means frame count is in byte 1, bits 0-5
        if (data.Length < 2)
            return -1;
        
        return data[1] & 0x3F;
    }
}

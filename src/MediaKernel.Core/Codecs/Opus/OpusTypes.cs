// Licensed under the MIT License.
// Based on RFC 6716 (Opus Audio Codec) and RFC 7845 (Ogg Opus).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Codecs.Opus;

/// <summary>
/// Opus audio modes.
/// </summary>
public enum OpusMode
{
    /// <summary>SILK-only mode (voice).</summary>
    SilkOnly = 0,
    
    /// <summary>Hybrid mode (SILK + CELT).</summary>
    Hybrid = 1,
    
    /// <summary>CELT-only mode (music/general audio).</summary>
    CeltOnly = 2
}

/// <summary>
/// Opus bandwidth values.
/// </summary>
public enum OpusBandwidth
{
    /// <summary>Narrowband (4kHz audio bandwidth, 8kHz sample rate).</summary>
    Narrowband = 0,
    
    /// <summary>Mediumband (6kHz audio bandwidth, 12kHz sample rate).</summary>
    Mediumband = 1,
    
    /// <summary>Wideband (8kHz audio bandwidth, 16kHz sample rate).</summary>
    Wideband = 2,
    
    /// <summary>Super-wideband (12kHz audio bandwidth, 24kHz sample rate).</summary>
    SuperWideband = 3,
    
    /// <summary>Fullband (20kHz audio bandwidth, 48kHz sample rate).</summary>
    Fullband = 4
}

/// <summary>
/// Opus frame duration.
/// </summary>
public enum OpusFrameDuration
{
    /// <summary>2.5ms frame.</summary>
    Ms2_5 = 0,
    
    /// <summary>5ms frame.</summary>
    Ms5 = 1,
    
    /// <summary>10ms frame.</summary>
    Ms10 = 2,
    
    /// <summary>20ms frame.</summary>
    Ms20 = 3,
    
    /// <summary>40ms frame.</summary>
    Ms40 = 4,
    
    /// <summary>60ms frame.</summary>
    Ms60 = 5
}

/// <summary>
/// Opus packet Table of Contents (TOC) byte.
/// Contains configuration, stereo flag, and frame count mode.
/// </summary>
public readonly struct OpusToc
{
    private readonly byte value;
    
    /// <summary>
    /// Creates a TOC from a byte value.
    /// </summary>
    public OpusToc(byte value)
    {
        this.value = value;
    }
    
    /// <summary>
    /// Raw TOC byte value.
    /// </summary>
    public byte Value => value;
    
    /// <summary>
    /// Configuration index (0-31).
    /// </summary>
    public int Configuration => value >> 3;
    
    /// <summary>
    /// Whether the packet is stereo.
    /// </summary>
    public bool IsStereo => (value & 0x04) != 0;
    
    /// <summary>
    /// Frame count code (0-3).
    /// 0 = 1 frame, 1 = 2 frames equal size, 2 = 2 frames different size, 3 = arbitrary frames.
    /// </summary>
    public int FrameCountCode => value & 0x03;
    
    /// <summary>
    /// Gets the mode from the configuration.
    /// </summary>
    public OpusMode Mode
    {
        get
        {
            int config = Configuration;
            if (config < 12)
                return OpusMode.SilkOnly;
            else if (config < 16)
                return OpusMode.Hybrid;
            else
                return OpusMode.CeltOnly;
        }
    }
    
    /// <summary>
    /// Gets the bandwidth from the configuration.
    /// </summary>
    public OpusBandwidth Bandwidth
    {
        get
        {
            int config = Configuration;
            
            if (config < 12)
            {
                // SILK-only: configs 0-11
                return (OpusBandwidth)(config / 4);
            }
            else if (config < 16)
            {
                // Hybrid: configs 12-15
                return config < 14 ? OpusBandwidth.SuperWideband : OpusBandwidth.Fullband;
            }
            else
            {
                // CELT-only: configs 16-31
                // CELT bandwidths skip Mediumband: NB(16-19), WB(20-23), SWB(24-27), FB(28-31)
                return ((config - 16) / 4) switch
                {
                    0 => OpusBandwidth.Narrowband,
                    1 => OpusBandwidth.Wideband,
                    2 => OpusBandwidth.SuperWideband,
                    3 => OpusBandwidth.Fullband,
                    _ => OpusBandwidth.Fullband
                };
            }
        }
    }
    
    /// <summary>
    /// Gets the frame duration from the configuration.
    /// </summary>
    public OpusFrameDuration FrameDuration
    {
        get
        {
            int config = Configuration;
            
            if (config < 12)
            {
                // SILK-only: 10, 20, 40, 60ms
                int durIndex = config % 4;
                return durIndex switch
                {
                    0 => OpusFrameDuration.Ms10,
                    1 => OpusFrameDuration.Ms20,
                    2 => OpusFrameDuration.Ms40,
                    _ => OpusFrameDuration.Ms60
                };
            }
            else if (config < 16)
            {
                // Hybrid: 10, 20ms
                return (config & 1) == 0 ? OpusFrameDuration.Ms10 : OpusFrameDuration.Ms20;
            }
            else
            {
                // CELT-only: 2.5, 5, 10, 20ms
                int durIndex = (config - 16) % 4;
                return durIndex switch
                {
                    0 => OpusFrameDuration.Ms2_5,
                    1 => OpusFrameDuration.Ms5,
                    2 => OpusFrameDuration.Ms10,
                    _ => OpusFrameDuration.Ms20
                };
            }
        }
    }
    
    /// <summary>
    /// Gets the frame duration in samples at 48kHz.
    /// </summary>
    public int FrameSamples => FrameDuration switch
    {
        OpusFrameDuration.Ms2_5 => 120,
        OpusFrameDuration.Ms5 => 240,
        OpusFrameDuration.Ms10 => 480,
        OpusFrameDuration.Ms20 => 960,
        OpusFrameDuration.Ms40 => 1920,
        OpusFrameDuration.Ms60 => 2880,
        _ => 960
    };
    
    /// <summary>
    /// Gets the sample rate for the bandwidth.
    /// </summary>
    public int SampleRate => Bandwidth switch
    {
        OpusBandwidth.Narrowband => 8000,
        OpusBandwidth.Mediumband => 12000,
        OpusBandwidth.Wideband => 16000,
        OpusBandwidth.SuperWideband => 24000,
        OpusBandwidth.Fullband => 48000,
        _ => 48000
    };
}

/// <summary>
/// Opus stream header (OpusHead) from Ogg container.
/// </summary>
public struct OpusHeader
{
    /// <summary>Version number (major in upper 4 bits).</summary>
    public byte Version;
    
    /// <summary>Number of output channels.</summary>
    public byte ChannelCount;
    
    /// <summary>Pre-skip samples at 48kHz.</summary>
    public ushort PreSkip;
    
    /// <summary>Original input sample rate (informational).</summary>
    public uint InputSampleRate;
    
    /// <summary>Output gain in dB (Q7.8 format).</summary>
    public short OutputGain;
    
    /// <summary>Channel mapping family (0, 1, 2, or 255).</summary>
    public byte ChannelMappingFamily;
    
    /// <summary>Number of streams (if mapping family > 0).</summary>
    public byte StreamCount;
    
    /// <summary>Number of coupled (stereo) streams.</summary>
    public byte CoupledStreamCount;
    
    /// <summary>Channel mapping table.</summary>
    public OpusChannelMapping ChannelMapping;
    
    /// <summary>
    /// Gets the output gain as a linear multiplier.
    /// </summary>
    public float OutputGainLinear => MathF.Pow(10.0f, OutputGain / (256.0f * 20.0f));
}

/// <summary>
/// Channel mapping table for Opus multistream.
/// </summary>
[InlineArray(255)]
public struct OpusChannelMapping
{
    private byte firstElement;
}

/// <summary>
/// Parsed Opus packet information.
/// </summary>
public struct OpusPacketInfo
{
    /// <summary>TOC byte.</summary>
    public OpusToc Toc;
    
    /// <summary>Number of frames in the packet.</summary>
    public int FrameCount;
    
    /// <summary>Frame sizes in bytes.</summary>
    public OpusFrameSizes FrameSizes;
    
    /// <summary>Offset to frame data in packet.</summary>
    public int PayloadOffset;
    
    /// <summary>Whether packet uses VBR (variable frame sizes).</summary>
    public bool IsVariableBitRate;
    
    /// <summary>Whether this is a padding packet.</summary>
    public bool IsPadding;
}

/// <summary>
/// Frame sizes for Opus packet.
/// </summary>
[InlineArray(48)]
public struct OpusFrameSizes
{
    private int firstElement;
}

/// <summary>
/// Parser for Opus packet structure and headers.
/// </summary>
public static class OpusPacketParser
{
    /// <summary>
    /// Opus header magic bytes "OpusHead".
    /// </summary>
    public static ReadOnlySpan<byte> HeaderMagic => "OpusHead"u8;
    
    /// <summary>
    /// Opus tags magic bytes "OpusTags".
    /// </summary>
    public static ReadOnlySpan<byte> TagsMagic => "OpusTags"u8;
    
    /// <summary>
    /// Minimum Opus header size (19 bytes for channel mapping 0).
    /// </summary>
    public const int MinHeaderSize = 19;
    
    /// <summary>
    /// Maximum supported channels.
    /// </summary>
    public const int MaxChannels = 255;
    
    /// <summary>
    /// Parses an Opus header (OpusHead) from the data.
    /// </summary>
    /// <param name="data">Raw header data.</param>
    /// <param name="header">Parsed header.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out OpusHeader header)
    {
        header = default;
        
        if (data.Length < MinHeaderSize)
            return false;
        
        // Check magic bytes
        if (!data.Slice(0, 8).SequenceEqual(HeaderMagic))
            return false;
        
        // Version (only major version 0 supported)
        header.Version = data[8];
        if ((header.Version & 0xF0) != 0)
            return false;
        
        // Channel count
        header.ChannelCount = data[9];
        if (header.ChannelCount == 0)
            return false;
        
        // Pre-skip (little-endian)
        header.PreSkip = (ushort)(data[10] | (data[11] << 8));
        
        // Input sample rate (little-endian)
        header.InputSampleRate = (uint)(data[12] | (data[13] << 8) | (data[14] << 16) | (data[15] << 24));
        
        // Output gain (little-endian, signed)
        header.OutputGain = (short)(data[16] | (data[17] << 8));
        
        // Channel mapping family
        header.ChannelMappingFamily = data[18];
        
        if (header.ChannelMappingFamily == 0)
        {
            // Mono or stereo, no mapping table
            if (header.ChannelCount > 2)
                return false;
            
            header.StreamCount = 1;
            header.CoupledStreamCount = (byte)(header.ChannelCount > 1 ? 1 : 0);
            header.ChannelMapping[0] = 0;
            if (header.ChannelCount > 1)
                header.ChannelMapping[1] = 1;
        }
        else
        {
            // Extended mapping table
            if (data.Length < MinHeaderSize + 2 + header.ChannelCount)
                return false;
            
            header.StreamCount = data[19];
            if (header.StreamCount == 0)
                return false;
            
            header.CoupledStreamCount = data[20];
            if (header.CoupledStreamCount > header.StreamCount)
                return false;
            
            // Read channel mapping
            for (int i = 0; i < header.ChannelCount; i++)
            {
                header.ChannelMapping[i] = data[21 + i];
            }
        }
        
        return true;
    }
    
    /// <summary>
    /// Parses an Opus packet to extract frame information.
    /// </summary>
    /// <param name="packet">Raw packet data.</param>
    /// <param name="info">Parsed packet info.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParsePacket(ReadOnlySpan<byte> packet, out OpusPacketInfo info)
    {
        info = default;
        
        if (packet.Length < 1)
            return false;
        
        info.Toc = new OpusToc(packet[0]);
        int offset = 1;
        
        switch (info.Toc.FrameCountCode)
        {
            case 0:
                // 1 frame
                info.FrameCount = 1;
                info.FrameSizes[0] = packet.Length - offset;
                break;
                
            case 1:
                // 2 frames, equal size, CBR
                if ((packet.Length - offset) % 2 != 0)
                    return false;
                    
                info.FrameCount = 2;
                int halfSize = (packet.Length - offset) / 2;
                info.FrameSizes[0] = halfSize;
                info.FrameSizes[1] = halfSize;
                break;
                
            case 2:
                // 2 frames, different sizes, VBR
                info.IsVariableBitRate = true;
                info.FrameCount = 2;
                
                if (!TryParseFrameLength(packet, ref offset, out int firstFrameSize))
                    return false;
                    
                info.FrameSizes[0] = firstFrameSize;
                info.FrameSizes[1] = packet.Length - offset - firstFrameSize;
                
                if (info.FrameSizes[1] < 0)
                    return false;
                break;
                
            case 3:
                // Arbitrary number of frames
                if (packet.Length < 2)
                    return false;
                
                byte frameCountByte = packet[offset++];
                info.FrameCount = frameCountByte & 0x3F;
                bool isVbr = (frameCountByte & 0x80) != 0;
                info.IsPadding = (frameCountByte & 0x40) != 0;
                info.IsVariableBitRate = isVbr;
                
                if (info.FrameCount == 0)
                    return false;
                
                // Parse padding length (RFC 6716 §3.2.1): padding data is at end of packet
                // Each 255-byte indicator contributes 254 to the count (matches xiph_lacing_full)
                int paddingBytes = 0;
                if (info.IsPadding)
                {
                    while (offset < packet.Length)
                    {
                        byte padByte = packet[offset++];
                        paddingBytes += padByte;
                        if (padByte < 255)
                            break;
                        paddingBytes--; // 255-byte indicators contribute 254, not 255
                    }
                }
                
                if (isVbr)
                {
                    // Variable bit rate: each frame except last has size encoded
                    int totalFrameBytes = 0;
                    for (int i = 0; i < info.FrameCount - 1; i++)
                    {
                        if (!TryParseFrameLength(packet, ref offset, out int frameSize))
                            return false;
                        info.FrameSizes[i] = frameSize;
                        totalFrameBytes += frameSize;
                    }
                    
                    // Last frame gets remaining bytes (minus padding at end)
                    info.FrameSizes[info.FrameCount - 1] = packet.Length - offset - totalFrameBytes - paddingBytes;
                }
                else
                {
                    // Constant bit rate: all frames same size (minus padding at end)
                    int remainingBytes = packet.Length - offset - paddingBytes;
                    if (remainingBytes < 0 || remainingBytes % info.FrameCount != 0)
                        return false;
                        
                    int frameSize = remainingBytes / info.FrameCount;
                    for (int i = 0; i < info.FrameCount; i++)
                    {
                        info.FrameSizes[i] = frameSize;
                    }
                }
                break;
        }
        
        info.PayloadOffset = offset;
        return true;
    }
    
    /// <summary>
    /// Parses a variable-length frame size.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseFrameLength(ReadOnlySpan<byte> packet, ref int offset, out int length)
    {
        length = 0;
        
        if (offset >= packet.Length)
            return false;
        
        byte firstByte = packet[offset++];
        
        if (firstByte < 252)
        {
            length = firstByte;
            return true;
        }
        else
        {
            // Two-byte encoding
            if (offset >= packet.Length)
                return false;
                
            byte secondByte = packet[offset++];
            length = 4 * secondByte + firstByte;
            return true;
        }
    }
    
    /// <summary>
    /// Gets the total duration of a packet in samples at 48kHz.
    /// </summary>
    public static int GetPacketDuration(in OpusPacketInfo info)
    {
        return info.FrameCount * info.Toc.FrameSamples;
    }
    
    /// <summary>
    /// Gets the bandwidth in Hz for the packet.
    /// </summary>
    public static int GetBandwidthHz(OpusBandwidth bandwidth)
    {
        return bandwidth switch
        {
            OpusBandwidth.Narrowband => 4000,
            OpusBandwidth.Mediumband => 6000,
            OpusBandwidth.Wideband => 8000,
            OpusBandwidth.SuperWideband => 12000,
            OpusBandwidth.Fullband => 20000,
            _ => 20000
        };
    }
}

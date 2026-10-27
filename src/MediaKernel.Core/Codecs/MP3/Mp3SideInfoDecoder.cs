using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3;

/// <summary>
/// MP3 side information parser for Layer III.
/// ISO/IEC 11172-3 Section 2.4.1.7
/// </summary>
public static class Mp3SideInfoDecoder
{
    /// <summary>
    /// Side info size in bytes for MPEG-1 stereo.
    /// </summary>
    public const int SideInfoSizeMpeg1Stereo = 32;
    
    /// <summary>
    /// Side info size in bytes for MPEG-1 mono.
    /// </summary>
    public const int SideInfoSizeMpeg1Mono = 17;
    
    /// <summary>
    /// Side info size in bytes for MPEG-2/2.5 stereo.
    /// </summary>
    public const int SideInfoSizeMpeg2Stereo = 17;
    
    /// <summary>
    /// Side info size in bytes for MPEG-2/2.5 mono.
    /// </summary>
    public const int SideInfoSizeMpeg2Mono = 9;
    
    /// <summary>
    /// Gets the side information size in bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSideInfoSize(MpegVersion version, int channelCount)
    {
        if (version == MpegVersion.Mpeg1)
        {
            return channelCount == 1 ? SideInfoSizeMpeg1Mono : SideInfoSizeMpeg1Stereo;
        }
        return channelCount == 1 ? SideInfoSizeMpeg2Mono : SideInfoSizeMpeg2Stereo;
    }
    
    /// <summary>
    /// Decodes side information for any MPEG version.
    /// </summary>
    /// <param name="reader">Bit reader positioned at side info start.</param>
    /// <param name="header">MP3 frame header.</param>
    /// <param name="sideInfo">Output side information.</param>
    /// <returns>True if decoding succeeded.</returns>
    public static bool DecodeSideInfo(ref Mp3BitReader reader, in Mp3FrameHeader header, out Mp3SideInfo sideInfo)
    {
        int channelCount = header.ChannelMode == Mp3ChannelMode.SingleChannel ? 1 : 2;
        
        if (header.Version == MpegVersion.Mpeg1)
        {
            DecodeMpeg1SideInfo(ref reader, channelCount, out sideInfo);
        }
        else
        {
            DecodeMpeg2SideInfo(ref reader, channelCount, out sideInfo);
        }
        
        return true;
    }
    
    /// <summary>
    /// Decodes side information for MPEG-1.
    /// </summary>
    /// <param name="reader">Bit reader positioned at side info start.</param>
    /// <param name="channelCount">Number of channels (1 or 2).</param>
    /// <param name="sideInfo">Output side information.</param>
    public static void DecodeMpeg1SideInfo(ref Mp3BitReader reader, int channelCount, out Mp3SideInfo sideInfo)
    {
        sideInfo = new Mp3SideInfo();
        
        // main_data_begin (9 bits)
        sideInfo.MainDataBegin = reader.ReadBits(9);
        
        // private_bits (5 bits for mono, 3 bits for stereo) — ISO 11172-3 Table 4.11
        sideInfo.PrivateBits = reader.ReadBits(channelCount == 1 ? 5 : 3);
        
        // scfsi (scalefactor selection information) - 4 bits per channel
        for (int channel = 0; channel < channelCount; channel++)
        {
            for (int band = 0; band < 4; band++)
            {
                sideInfo.Scfsi[channel * 4 + band] = reader.ReadBit() ? 1 : 0;
            }
        }
        
        // Two granules for MPEG-1
        for (int granule = 0; granule < 2; granule++)
        {
            for (int channel = 0; channel < channelCount; channel++)
            {
                int index = granule * 2 + channel;
                DecodeGranuleInfo(ref reader, ref sideInfo.Granules[index], isMpeg1: true);
            }
        }
    }
    
    /// <summary>
    /// Decodes side information for MPEG-2/2.5.
    /// </summary>
    public static void DecodeMpeg2SideInfo(ref Mp3BitReader reader, int channelCount, out Mp3SideInfo sideInfo)
    {
        sideInfo = new Mp3SideInfo();
        
        // main_data_begin (8 bits for MPEG-2)
        sideInfo.MainDataBegin = reader.ReadBits(8);
        
        // private_bits (2 bits for stereo, 1 bit for mono)
        sideInfo.PrivateBits = reader.ReadBits(channelCount == 1 ? 1 : 2);
        
        // One granule for MPEG-2/2.5
        for (int channel = 0; channel < channelCount; channel++)
        {
            DecodeGranuleInfo(ref reader, ref sideInfo.Granules[channel], isMpeg1: false);
        }
    }
    
    /// <summary>
    /// Decodes granule-specific information.
    /// </summary>
    private static void DecodeGranuleInfo(ref Mp3BitReader reader, ref Mp3GranuleInfo granule, bool isMpeg1)
    {
        // part2_3_length (12 bits)
        granule.Part23Length = reader.ReadBits(12);
        
        // big_values (9 bits)
        granule.BigValues = reader.ReadBits(9);
        
        // global_gain (8 bits)
        granule.GlobalGain = reader.ReadBits(8);
        
        // scalefac_compress (4 bits MPEG-1, 9 bits MPEG-2)
        granule.ScalefactorCompress = reader.ReadBits(isMpeg1 ? 4 : 9);
        
        // window_switching_flag (1 bit)
        granule.WindowSwitchingFlag = reader.ReadBit();
        
        if (granule.WindowSwitchingFlag)
        {
            // block_type (2 bits)
            granule.BlockType = reader.ReadBits(2);
            
            // mixed_block_flag (1 bit)
            granule.MixedBlockFlag = reader.ReadBit();
            
            // table_select (5 bits × 2 regions)
            for (int region = 0; region < 2; region++)
            {
                granule.TableSelect[region] = reader.ReadBits(5);
            }
            
            // subblock_gain (3 bits × 3 windows)
            for (int window = 0; window < 3; window++)
            {
                granule.SubblockGain[window] = reader.ReadBits(3);
            }
            
            // Implied region counts for window switching
            if (granule.BlockType == 2 && !granule.MixedBlockFlag)
            {
                granule.Region0Count = 8;
            }
            else
            {
                granule.Region0Count = 7;
            }
            granule.Region1Count = 20 - granule.Region0Count;
        }
        else
        {
            // Normal (long) block
            granule.BlockType = 0;
            granule.MixedBlockFlag = false;
            
            // table_select (5 bits × 3 regions)
            for (int region = 0; region < 3; region++)
            {
                granule.TableSelect[region] = reader.ReadBits(5);
            }
            
            // region0_count (4 bits)
            granule.Region0Count = reader.ReadBits(4);
            
            // region1_count (3 bits)
            granule.Region1Count = reader.ReadBits(3);
        }
        
        // MPEG-1 only: preflag (1 bit)
        if (isMpeg1)
        {
            granule.PreFlag = reader.ReadBit() ? 1 : 0;
        }
        
        // scalefac_scale (1 bit)
        granule.ScalefactorScale = reader.ReadBit() ? 1 : 0;
        
        // count1table_select (1 bit)
        granule.Count1TableSelect = reader.ReadBit() ? 1 : 0;
    }
}

/// <summary>
/// Bit reader for MP3 decoding.
/// </summary>
public ref struct Mp3BitReader
{
    private readonly ReadOnlySpan<byte> data;
    private int bitPosition;
    
    /// <summary>
    /// Creates a new bit reader.
    /// </summary>
    public Mp3BitReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        this.bitPosition = 0;
    }
    
    /// <summary>
    /// Current bit position.
    /// </summary>
    public int BitPosition => bitPosition;
    
    /// <summary>
    /// Number of bits read so far.
    /// </summary>
    public int BitsRead => bitPosition;
    
    /// <summary>
    /// Number of bits remaining.
    /// </summary>
    public int BitsRemaining => data.Length * 8 - bitPosition;
    
    /// <summary>
    /// Whether the reader has reached the end.
    /// </summary>
    public bool IsEmpty => bitPosition >= data.Length * 8;
    
    /// <summary>
    /// Reads a single bit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBit()
    {
        int byteIndex = bitPosition >> 3;
        int bitIndex = 7 - (bitPosition & 7);
        bitPosition++;
        if (byteIndex >= data.Length) return false; // zero-padded overread (matches FFmpeg AV_INPUT_BUFFER_PADDING)
        return ((data[byteIndex] >> bitIndex) & 1) != 0;
    }
    
    /// <summary>
    /// Reads up to 25 bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBits(int count)
    {
        if (count == 0)
            return 0;
        
        int result = 0;
        
        // Read bit by bit for simplicity (can optimize later)
        for (int i = 0; i < count; i++)
        {
            result = (result << 1) | (ReadBit() ? 1 : 0);
        }
        
        return result;
    }
    
    /// <summary>
    /// Skips the specified number of bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int count)
    {
        bitPosition += count;
    }
    
    /// <summary>
    /// Seeks to the specified bit position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Seek(int position)
    {
        bitPosition = position;
    }
}

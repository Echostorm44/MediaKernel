using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.MP3;

/// <summary>
/// MPEG Audio frame header.
/// ISO/IEC 11172-3 (MPEG-1) and ISO/IEC 13818-3 (MPEG-2).
/// </summary>
public readonly struct Mp3FrameHeader
{
    /// <summary>MPEG version (1, 2, or 2.5).</summary>
    public readonly MpegVersion Version;
    
    /// <summary>Audio layer (1, 2, or 3).</summary>
    public readonly MpegLayer Layer;
    
    /// <summary>Whether CRC protection is present.</summary>
    public readonly bool HasCrc;
    
    /// <summary>Bit rate in kbps.</summary>
    public readonly int BitRate;
    
    /// <summary>Sample rate in Hz.</summary>
    public readonly int SampleRate;
    
    /// <summary>Whether frame has padding byte.</summary>
    public readonly bool HasPadding;
    
    /// <summary>Channel mode.</summary>
    public readonly Mp3ChannelMode ChannelMode;
    
    /// <summary>Mode extension (for Joint Stereo).</summary>
    public readonly int ModeExtension;
    
    /// <summary>Emphasis is applied.</summary>
    public readonly Mp3Emphasis Emphasis;
    
    /// <summary>Total frame size in bytes.</summary>
    public readonly int FrameSize;
    
    /// <summary>Header size (4 bytes normally, 6 with CRC).</summary>
    public readonly int HeaderSize;
    
    /// <summary>Number of samples per frame.</summary>
    public readonly int SamplesPerFrame;
    
    /// <summary>Number of channels.</summary>
    public readonly int ChannelCount;
    
    /// <summary>Whether the header is valid.</summary>
    public readonly bool IsValid;
    
    public Mp3FrameHeader(
        MpegVersion version,
        MpegLayer layer,
        bool hasCrc,
        int bitRate,
        int sampleRate,
        bool hasPadding,
        Mp3ChannelMode channelMode,
        int modeExtension,
        Mp3Emphasis emphasis)
    {
        Version = version;
        Layer = layer;
        HasCrc = hasCrc;
        BitRate = bitRate;
        SampleRate = sampleRate;
        HasPadding = hasPadding;
        ChannelMode = channelMode;
        ModeExtension = modeExtension;
        Emphasis = emphasis;
        
        ChannelCount = channelMode == Mp3ChannelMode.SingleChannel ? 1 : 2;
        
        // Calculate samples per frame
        SamplesPerFrame = layer switch
        {
            MpegLayer.Layer1 => 384,
            MpegLayer.Layer2 => 1152,
            MpegLayer.Layer3 => version == MpegVersion.Mpeg1 ? 1152 : 576,
            _ => 0
        };
        
        // Calculate frame size
        if (sampleRate > 0 && bitRate > 0)
        {
            int slotSize = layer == MpegLayer.Layer1 ? 4 : 1;
            int padding = hasPadding ? slotSize : 0;
            
            FrameSize = layer switch
            {
                MpegLayer.Layer1 => (12 * bitRate * 1000 / sampleRate + padding) * 4,
                _ => 144 * bitRate * 1000 / sampleRate + padding
            };
            
            // MPEG-2/2.5 Layer 3 uses half the bit rate formula
            if (layer == MpegLayer.Layer3 && version != MpegVersion.Mpeg1)
            {
                FrameSize = 72 * bitRate * 1000 / sampleRate + padding;
            }
        }
        else
        {
            FrameSize = 0;
        }
        
        HeaderSize = hasCrc ? 6 : 4;
        IsValid = FrameSize > HeaderSize && SamplesPerFrame > 0;
    }
}

/// <summary>
/// MPEG audio version.
/// </summary>
public enum MpegVersion
{
    /// <summary>Reserved/invalid.</summary>
    Reserved = 0,
    
    /// <summary>MPEG Version 1.</summary>
    Mpeg1 = 1,
    
    /// <summary>MPEG Version 2 (ISO/IEC 13818-3).</summary>
    Mpeg2 = 2,
    
    /// <summary>MPEG Version 2.5 (unofficial extension).</summary>
    Mpeg25 = 3
}

/// <summary>
/// MPEG audio layer.
/// </summary>
public enum MpegLayer
{
    /// <summary>Reserved/invalid.</summary>
    Reserved = 0,
    
    /// <summary>Layer I.</summary>
    Layer1 = 1,
    
    /// <summary>Layer II.</summary>
    Layer2 = 2,
    
    /// <summary>Layer III (MP3).</summary>
    Layer3 = 3
}

/// <summary>
/// MP3 channel mode.
/// </summary>
public enum Mp3ChannelMode
{
    /// <summary>Stereo.</summary>
    Stereo = 0,
    
    /// <summary>Joint stereo (Mid/Side or intensity).</summary>
    JointStereo = 1,
    
    /// <summary>Dual channel (independent channels).</summary>
    DualChannel = 2,
    
    /// <summary>Mono.</summary>
    SingleChannel = 3
}

/// <summary>
/// MP3 emphasis type.
/// </summary>
public enum Mp3Emphasis
{
    /// <summary>No emphasis.</summary>
    None = 0,
    
    /// <summary>50/15 microseconds emphasis.</summary>
    Emphasis5015 = 1,
    
    /// <summary>Reserved.</summary>
    Reserved = 2,
    
    /// <summary>CCIT J.17 emphasis.</summary>
    CcitJ17 = 3
}

/// <summary>
/// MP3 block type for Layer III.
/// </summary>
public enum Mp3BlockType
{
    /// <summary>Normal (long) blocks - 18 MDCT coefficients.</summary>
    Normal = 0,
    
    /// <summary>Start block - transition from long to short.</summary>
    Start = 1,
    
    /// <summary>Three short blocks - 6 MDCT coefficients each.</summary>
    Short = 2,
    
    /// <summary>End block - transition from short to long.</summary>
    End = 3
}

/// <summary>
/// MPEG Audio frame parser.
/// </summary>
public static class Mp3FrameParser
{
    /// <summary>
    /// Sync word for MPEG audio frames (11 bits of 1s).
    /// </summary>
    public const ushort SyncWord = 0xFFE0;
    
    /// <summary>
    /// Tries to parse an MP3 frame header.
    /// </summary>
    /// <param name="data">Data containing frame header.</param>
    /// <param name="header">Parsed header if successful.</param>
    /// <returns>True if a valid header was parsed.</returns>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out Mp3FrameHeader header)
    {
        header = default;
        
        if (data.Length < 4)
            return false;
        
        // Check sync word (11 bits of 1s)
        if (data[0] != 0xFF || (data[1] & 0xE0) != 0xE0)
            return false;
        
        // Parse header bits
        int versionBits = (data[1] >> 3) & 0x03;
        int layerBits = (data[1] >> 1) & 0x03;
        bool protectionBit = (data[1] & 0x01) == 0;  // 0 = protected
        
        int bitRateIndex = (data[2] >> 4) & 0x0F;
        int sampleRateIndex = (data[2] >> 2) & 0x03;
        bool paddingBit = ((data[2] >> 1) & 0x01) != 0;
        
        int channelMode = (data[3] >> 6) & 0x03;
        int modeExtension = (data[3] >> 4) & 0x03;
        int emphasis = data[3] & 0x03;
        
        // Decode version
        var version = versionBits switch
        {
            0 => MpegVersion.Mpeg25,
            1 => MpegVersion.Reserved,
            2 => MpegVersion.Mpeg2,
            3 => MpegVersion.Mpeg1,
            _ => MpegVersion.Reserved
        };
        
        if (version == MpegVersion.Reserved)
            return false;
        
        // Decode layer
        var layer = layerBits switch
        {
            0 => MpegLayer.Reserved,
            1 => MpegLayer.Layer3,
            2 => MpegLayer.Layer2,
            3 => MpegLayer.Layer1,
            _ => MpegLayer.Reserved
        };
        
        if (layer == MpegLayer.Reserved)
            return false;
        
        // Get bit rate
        int bitRate = GetBitRate(version, layer, bitRateIndex);
        if (bitRate <= 0)
            return false;
        
        // Get sample rate
        int sampleRate = GetSampleRate(version, sampleRateIndex);
        if (sampleRate <= 0)
            return false;
        
        header = new Mp3FrameHeader(
            version,
            layer,
            protectionBit,
            bitRate,
            sampleRate,
            paddingBit,
            (Mp3ChannelMode)channelMode,
            modeExtension,
            (Mp3Emphasis)emphasis);
        
        return header.IsValid;
    }
    
    /// <summary>
    /// Finds the next sync word in the data.
    /// </summary>
    /// <param name="data">Data to search.</param>
    /// <returns>Offset to sync word, or -1 if not found.</returns>
    public static int FindSyncWord(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length - 1; i++)
        {
            if (data[i] == 0xFF && (data[i + 1] & 0xE0) == 0xE0)
            {
                // Try to parse to validate
                if (TryParseHeader(data.Slice(i), out _))
                    return i;
            }
        }
        return -1;
    }
    
    /// <summary>
    /// Gets bit rate in kbps from index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetBitRate(MpegVersion version, MpegLayer layer, int index)
    {
        if (index == 0 || index == 15)
            return -1;  // Free format or invalid
        
        // MPEG-1 bit rates
        if (version == MpegVersion.Mpeg1)
        {
            return layer switch
            {
                MpegLayer.Layer1 => Mpeg1Layer1BitRates[index - 1],
                MpegLayer.Layer2 => Mpeg1Layer2BitRates[index - 1],
                MpegLayer.Layer3 => Mpeg1Layer3BitRates[index - 1],
                _ => -1
            };
        }
        
        // MPEG-2/2.5 bit rates
        return layer switch
        {
            MpegLayer.Layer1 => Mpeg2Layer1BitRates[index - 1],
            _ => Mpeg2Layer23BitRates[index - 1]
        };
    }
    
    /// <summary>
    /// Gets sample rate in Hz from index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetSampleRate(MpegVersion version, int index)
    {
        if (index >= 3)
            return -1;
        
        return version switch
        {
            MpegVersion.Mpeg1 => Mpeg1SampleRates[index],
            MpegVersion.Mpeg2 => Mpeg2SampleRates[index],
            MpegVersion.Mpeg25 => Mpeg25SampleRates[index],
            _ => -1
        };
    }
    
    // MPEG-1 Layer 1 bit rates (kbps): index 1-14
    private static ReadOnlySpan<int> Mpeg1Layer1BitRates => 
        [32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
    
    // MPEG-1 Layer 2 bit rates (kbps): index 1-14
    private static ReadOnlySpan<int> Mpeg1Layer2BitRates => 
        [32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    
    // MPEG-1 Layer 3 bit rates (kbps): index 1-14
    private static ReadOnlySpan<int> Mpeg1Layer3BitRates => 
        [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    
    // MPEG-2/2.5 Layer 1 bit rates (kbps): index 1-14
    private static ReadOnlySpan<int> Mpeg2Layer1BitRates => 
        [32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
    
    // MPEG-2/2.5 Layer 2/3 bit rates (kbps): index 1-14
    private static ReadOnlySpan<int> Mpeg2Layer23BitRates => 
        [8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    
    // Sample rates (Hz)
    private static ReadOnlySpan<int> Mpeg1SampleRates => [44100, 48000, 32000];
    private static ReadOnlySpan<int> Mpeg2SampleRates => [22050, 24000, 16000];
    private static ReadOnlySpan<int> Mpeg25SampleRates => [11025, 12000, 8000];
}

/// <summary>
/// MP3 Layer III side information for a single channel/granule.
/// </summary>
public struct Mp3GranuleInfo
{
    /// <summary>Main data partition length in bits.</summary>
    public int Part23Length;
    
    /// <summary>Big values region size (pairs of values).</summary>
    public int BigValues;
    
    /// <summary>Global gain for the granule.</summary>
    public int GlobalGain;
    
    /// <summary>Scalefactor compression index.</summary>
    public int ScalefactorCompress;
    
    /// <summary>Whether window switching is used.</summary>
    public bool WindowSwitchingFlag;
    
    /// <summary>Block type (0=normal, 1=start, 2=short, 3=end). Cast to int for comparisons.</summary>
    public int BlockType;
    
    /// <summary>Whether mixed block mode is used.</summary>
    public bool MixedBlockFlag;
    
    /// <summary>Table selection for regions.</summary>
    public Mp3TableSelectArray TableSelect;
    
    /// <summary>Subblock gains for short blocks.</summary>
    public Mp3SubblockGainArray SubblockGain;
    
    /// <summary>Region 0 count.</summary>
    public int Region0Count;
    
    /// <summary>Region 1 count.</summary>
    public int Region1Count;
    
    /// <summary>Preflag for scalefactor preemphasis (0 or 1).</summary>
    public int PreFlag;
    
    /// <summary>Scalefactor scale (0 or 1).</summary>
    public int ScalefactorScale;
    
    /// <summary>Count1 table selection (0 or 1).</summary>
    public int Count1TableSelect;
}

/// <summary>Fixed-size array for table selection (3 regions).</summary>
[InlineArray(3)]
public struct Mp3TableSelectArray
{
    private int firstElement;
}

/// <summary>Fixed-size array for subblock gains (3 windows).</summary>
[InlineArray(3)]
public struct Mp3SubblockGainArray
{
    private int firstElement;
}

/// <summary>
/// MP3 Layer III side information.
/// </summary>
public struct Mp3SideInfo
{
    /// <summary>Main data begin pointer (negative offset from first byte after side info).</summary>
    public int MainDataBegin;
    
    /// <summary>Private bits.</summary>
    public int PrivateBits;
    
    /// <summary>Scalefactor selection info for each channel (4 bands × 2 channels).</summary>
    public Mp3ScfsiArray Scfsi;
    
    /// <summary>Granule info (2 granules × 2 channels = 4 max).</summary>
    public Mp3GranuleInfoArray Granules;
    
    /// <summary>Gets a reference to granule info at the specified position.</summary>
    /// <param name="granules">The granule array to access.</param>
    /// <param name="granule">Granule index (0 or 1).</param>
    /// <param name="channel">Channel index (0 or 1).</param>
    public static ref readonly Mp3GranuleInfo GetGranule(ref Mp3GranuleInfoArray granules, int granule, int channel)
    {
        return ref granules[granule * 2 + channel];
    }
}

/// <summary>Fixed-size array for SCFSI (4 bands × 2 channels).</summary>
[InlineArray(8)]
public struct Mp3ScfsiArray
{
    private int firstElement;
}

/// <summary>Fixed-size array for granule info (2 granules × 2 channels).</summary>
[InlineArray(4)]
public struct Mp3GranuleInfoArray
{
    private Mp3GranuleInfo firstElement;
}

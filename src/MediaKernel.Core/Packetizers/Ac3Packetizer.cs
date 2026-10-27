// MediaKernel - AC-3/E-AC-3 (Dolby Digital/Dolby Digital Plus) Packetizer
// Parses A/52 audio sync frames for frame assembly and codec configuration.
// Based on ATSC A/52 specification and VLC's a52.c/a52.h

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// AC-3 audio coding mode (channel configuration).
/// </summary>
public enum Ac3AudioCodingMode : byte
{
    DualMono = 0,     // 1+1 (Ch1, Ch2)
    Mono = 1,         // 1/0 (C)
    Stereo = 2,       // 2/0 (L, R)
    ThreeFront = 3,   // 3/0 (L, C, R)
    TwoFrontOneSurround = 4,  // 2/1 (L, R, S)
    ThreeFrontOneSurround = 5, // 3/1 (L, C, R, S)
    TwoFrontTwoSurround = 6,   // 2/2 (L, R, SL, SR)
    ThreeFrontTwoSurround = 7  // 3/2 (L, C, R, SL, SR)
}

/// <summary>
/// E-AC-3 stream type.
/// </summary>
public enum Eac3StreamType : byte
{
    Independent = 0,
    Dependent = 1,
    Ac3Convert = 2,
    Reserved = 3
}

/// <summary>
/// Parsed AC-3/E-AC-3 frame header information.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Ac3FrameHeader
{
    /// <summary>Whether this is E-AC-3 (Dolby Digital Plus) format.</summary>
    public readonly bool IsEac3;
    
    /// <summary>Sample rate code (0=48kHz, 1=44.1kHz, 2=32kHz).</summary>
    public readonly byte SampleRateCode;
    
    /// <summary>Sample rate in Hz.</summary>
    public readonly int SampleRate;
    
    /// <summary>Frame size code (AC-3 only).</summary>
    public readonly byte FrameSizeCode;
    
    /// <summary>Bit stream identification (version).</summary>
    public readonly byte BitstreamId;
    
    /// <summary>Bit stream mode.</summary>
    public readonly byte BitstreamMode;
    
    /// <summary>Audio coding mode (channel configuration).</summary>
    public readonly Ac3AudioCodingMode AudioCodingMode;
    
    /// <summary>LFE channel present.</summary>
    public readonly bool LfeOn;
    
    /// <summary>Number of audio channels (including LFE if present).</summary>
    public readonly int ChannelCount;
    
    /// <summary>Frame size in bytes.</summary>
    public readonly int FrameSize;
    
    /// <summary>Number of audio samples per frame (256 * blocks).</summary>
    public readonly int SampleCount;
    
    /// <summary>Bitrate in kbps.</summary>
    public readonly int Bitrate;
    
    /// <summary>E-AC-3 stream type.</summary>
    public readonly Eac3StreamType StreamType;
    
    /// <summary>E-AC-3 substream ID.</summary>
    public readonly byte SubstreamId;
    
    /// <summary>Number of audio blocks per sync frame (1-6).</summary>
    public readonly byte BlocksPerFrame;
    
    public Ac3FrameHeader(bool isEac3, byte sampleRateCode, int sampleRate, byte frameSizeCode,
        byte bitstreamId, byte bitstreamMode, Ac3AudioCodingMode audioCodingMode,
        bool lfeOn, int channelCount, int frameSize, int sampleCount, int bitrate,
        Eac3StreamType streamType, byte substreamId, byte blocksPerFrame)
    {
        IsEac3 = isEac3;
        SampleRateCode = sampleRateCode;
        SampleRate = sampleRate;
        FrameSizeCode = frameSizeCode;
        BitstreamId = bitstreamId;
        BitstreamMode = bitstreamMode;
        AudioCodingMode = audioCodingMode;
        LfeOn = lfeOn;
        ChannelCount = channelCount;
        FrameSize = frameSize;
        SampleCount = sampleCount;
        Bitrate = bitrate;
        StreamType = streamType;
        SubstreamId = substreamId;
        BlocksPerFrame = blocksPerFrame;
    }
}

/// <summary>
/// AC-3/E-AC-3 packetizer for parsing Dolby Digital/Digital Plus audio frames.
/// Handles sync word detection, frame header parsing, and frame boundary detection.
/// </summary>
public sealed class Ac3Packetizer
{
    // AC-3 sync word: 0x0B77
    private const ushort SyncWord = 0x0B77;
    
    // Minimum header size needed for parsing
    private const int MinHeaderSize = 8;
    private const int Eac3HeaderSize = 68; // Full E-AC-3 BSI
    
    // Sample rates by fscod
    private static readonly int[] SampleRates = [48000, 44100, 32000];
    
    // Sample rates for fscod2 (E-AC-3 only)
    private static readonly int[] SampleRates2 = [24000, 22050, 16000];
    
    // Frame sizes by frmsizcod and fscod (in 16-bit words)
    // Index: [frmsizcod][fscod] -> size in words
    private static readonly ushort[,] FrameSizeTable = new ushort[38, 3]
    {
        // 32kHz, 44.1kHz, 48kHz
        { 96, 69, 64 },     // 0
        { 96, 70, 64 },     // 1
        { 120, 87, 80 },    // 2
        { 120, 88, 80 },    // 3
        { 144, 104, 96 },   // 4
        { 144, 105, 96 },   // 5
        { 168, 121, 112 },  // 6
        { 168, 122, 112 },  // 7
        { 192, 139, 128 },  // 8
        { 192, 140, 128 },  // 9
        { 240, 174, 160 },  // 10
        { 240, 175, 160 },  // 11
        { 288, 208, 192 },  // 12
        { 288, 209, 192 },  // 13
        { 336, 243, 224 },  // 14
        { 336, 244, 224 },  // 15
        { 384, 278, 256 },  // 16
        { 384, 279, 256 },  // 17
        { 480, 348, 320 },  // 18
        { 480, 349, 320 },  // 19
        { 576, 417, 384 },  // 20
        { 576, 418, 384 },  // 21
        { 672, 487, 448 },  // 22
        { 672, 488, 448 },  // 23
        { 768, 557, 512 },  // 24
        { 768, 558, 512 },  // 25
        { 960, 696, 640 },  // 26
        { 960, 697, 640 },  // 27
        { 1152, 835, 768 }, // 28
        { 1152, 836, 768 }, // 29
        { 1344, 975, 896 }, // 30
        { 1344, 976, 896 }, // 31
        { 1536, 1114, 1024 }, // 32
        { 1536, 1115, 1024 }, // 33
        { 1728, 1253, 1152 }, // 34
        { 1728, 1254, 1152 }, // 35
        { 1920, 1393, 1280 }, // 36
        { 1920, 1394, 1280 }, // 37
    };
    
    // Bitrates in kbps by frmsizcod/2
    private static readonly int[] Bitrates =
    [
        32, 40, 48, 56, 64, 80, 96, 112, 128, 160,
        192, 224, 256, 320, 384, 448, 512, 576, 640
    ];
    
    // Channel counts by acmod
    private static readonly int[] ChannelCounts = [2, 1, 2, 3, 3, 4, 4, 5];
    
    private readonly List<byte> pendingData = new();
    
    /// <summary>
    /// Check if data starts with AC-3 sync word.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasSyncWord(ReadOnlySpan<byte> data)
    {
        return data.Length >= 2 && data[0] == 0x0B && data[1] == 0x77;
    }
    
    /// <summary>
    /// Find the next sync word in the data.
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
    /// Determine if frame is AC-3 or E-AC-3 based on bsid.
    /// AC-3: bsid 0-8, E-AC-3: bsid 11-16
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEac3(byte bitstreamId)
    {
        return bitstreamId > 10;
    }
    
    /// <summary>
    /// Parse AC-3 frame header.
    /// </summary>
    public static bool TryParseAc3Header(ReadOnlySpan<byte> data, out Ac3FrameHeader header)
    {
        header = default;
        
        if (data.Length < MinHeaderSize || !HasSyncWord(data))
            return false;
        
        // Skip sync word and CRC
        // Byte 4: fscod (2 bits) + frmsizcod (6 bits)
        byte fscod = (byte)((data[4] >> 6) & 0x03);
        byte frmsizcod = (byte)(data[4] & 0x3F);
        
        if (fscod >= 3 || frmsizcod >= 38)
            return false;
        
        // Byte 5: bsid (5 bits) + bsmod (3 bits)
        byte bsid = (byte)((data[5] >> 3) & 0x1F);
        byte bsmod = (byte)(data[5] & 0x07);
        
        // Check if this is actually E-AC-3
        if (IsEac3(bsid))
            return TryParseEac3Header(data, out header);
        
        // Byte 6: acmod (3 bits) + cmixlev/surmixlev (variable) + lfeon (1 bit)
        byte acmod = (byte)((data[6] >> 5) & 0x07);
        
        // Skip cmixlev and surmixlev based on acmod
        int bitPos = 3; // Starting after acmod in byte 6
        if ((acmod & 0x01) != 0 && acmod != 0x01) // 3 front channels
            bitPos += 2; // cmixlev
        if ((acmod & 0x04) != 0) // surround channel exists
            bitPos += 2; // surmixlev
        if (acmod == 0x02) // 2/0 mode
            bitPos += 2; // dsurmod
        
        // Extract lfeon
        int lfeByteOffset = 6 + (bitPos / 8);
        int lfeBitOffset = bitPos % 8;
        bool lfeon = false;
        if (lfeByteOffset < data.Length)
        {
            int shift = 7 - lfeBitOffset;
            lfeon = ((data[lfeByteOffset] >> shift) & 0x01) != 0;
        }
        
        int sampleRate = SampleRates[fscod];
        int frameSize = FrameSizeTable[frmsizcod, fscod] * 2; // words to bytes
        int bitrate = Bitrates[frmsizcod / 2];
        int channelCount = ChannelCounts[acmod] + (lfeon ? 1 : 0);
        
        header = new Ac3FrameHeader(
            isEac3: false,
            sampleRateCode: fscod,
            sampleRate: sampleRate,
            frameSizeCode: frmsizcod,
            bitstreamId: bsid,
            bitstreamMode: bsmod,
            audioCodingMode: (Ac3AudioCodingMode)acmod,
            lfeOn: lfeon,
            channelCount: channelCount,
            frameSize: frameSize,
            sampleCount: 256 * 6, // 6 blocks × 256 samples
            bitrate: bitrate,
            streamType: Eac3StreamType.Independent,
            substreamId: 0,
            blocksPerFrame: 6
        );
        
        return true;
    }
    
    /// <summary>
    /// Parse E-AC-3 frame header.
    /// </summary>
    public static bool TryParseEac3Header(ReadOnlySpan<byte> data, out Ac3FrameHeader header)
    {
        header = default;
        
        if (data.Length < MinHeaderSize || !HasSyncWord(data))
            return false;
        
        // E-AC-3 header format differs from AC-3
        // Bytes 2-3: strmtyp (2) + substreamid (3) + frmsiz (11)
        var strmtyp = (Eac3StreamType)((data[2] >> 6) & 0x03);
        byte substreamid = (byte)((data[2] >> 3) & 0x07);
        int frmsiz = ((data[2] & 0x07) << 8) | data[3];
        int frameSize = (frmsiz + 1) * 2; // in bytes
        
        // Byte 4: fscod (2) + fscod2/numblkscod (2) + acmod (3) + lfeon (1)
        byte fscod = (byte)((data[4] >> 6) & 0x03);
        int sampleRate;
        byte numblkscod;
        
        if (fscod == 3)
        {
            // Use fscod2
            byte fscod2 = (byte)((data[4] >> 4) & 0x03);
            if (fscod2 >= 3)
                return false;
            sampleRate = SampleRates2[fscod2];
            numblkscod = 3; // 6 blocks
        }
        else
        {
            sampleRate = SampleRates[fscod];
            numblkscod = (byte)((data[4] >> 4) & 0x03);
        }
        
        byte acmod = (byte)((data[4] >> 1) & 0x07);
        bool lfeon = (data[4] & 0x01) != 0;
        
        // Byte 5: bsid (5) + ...
        byte bsid = (byte)((data[5] >> 3) & 0x1F);
        
        int blocksPerFrame = numblkscod switch
        {
            0 => 1,
            1 => 2,
            2 => 3,
            _ => 6
        };
        
        int sampleCount = 256 * blocksPerFrame;
        int channelCount = ChannelCounts[acmod] + (lfeon ? 1 : 0);
        
        // Approximate bitrate from frame size and sample rate
        int bitrate = (frameSize * 8 * sampleRate) / (sampleCount * 1000);
        
        header = new Ac3FrameHeader(
            isEac3: true,
            sampleRateCode: fscod,
            sampleRate: sampleRate,
            frameSizeCode: 0,
            bitstreamId: bsid,
            bitstreamMode: 0,
            audioCodingMode: (Ac3AudioCodingMode)acmod,
            lfeOn: lfeon,
            channelCount: channelCount,
            frameSize: frameSize,
            sampleCount: sampleCount,
            bitrate: bitrate,
            streamType: strmtyp,
            substreamId: substreamid,
            blocksPerFrame: (byte)blocksPerFrame
        );
        
        return true;
    }
    
    /// <summary>
    /// Parse frame header (auto-detects AC-3 vs E-AC-3).
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out Ac3FrameHeader header)
    {
        header = default;
        
        if (data.Length < MinHeaderSize || !HasSyncWord(data))
            return false;
        
        // Check bsid to determine format
        byte bsid = (byte)((data[5] >> 3) & 0x1F);
        
        if (IsEac3(bsid))
            return TryParseEac3Header(data, out header);
        else
            return TryParseAc3Header(data, out header);
    }
    
    /// <summary>
    /// Feed raw data to the packetizer.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        pendingData.AddRange(data.ToArray());
    }
    
    /// <summary>
    /// Try to read the next complete AC-3/E-AC-3 frame.
    /// </summary>
    public bool TryReadFrame(out ReadOnlyMemory<byte> frameData, out Ac3FrameHeader header)
    {
        frameData = default;
        header = default;
        
        if (pendingData.Count < MinHeaderSize)
            return false;
        
        var data = pendingData.ToArray().AsSpan();
        
        // Find sync word
        int syncPos = FindSyncWord(data);
        if (syncPos < 0)
        {
            // No sync found, keep last byte in case it's start of sync
            if (pendingData.Count > 1)
                pendingData.RemoveRange(0, pendingData.Count - 1);
            return false;
        }
        
        // Skip garbage before sync
        if (syncPos > 0)
            pendingData.RemoveRange(0, syncPos);
        
        data = pendingData.ToArray().AsSpan();
        
        if (!TryParseHeader(data, out header))
            return false;
        
        // Check if we have the complete frame
        if (data.Length < header.FrameSize)
            return false;
        
        // Extract frame
        var frame = new byte[header.FrameSize];
        data.Slice(0, header.FrameSize).CopyTo(frame);
        frameData = frame;
        
        // Remove consumed data
        pendingData.RemoveRange(0, header.FrameSize);
        
        return true;
    }
    
    /// <summary>
    /// Reset packetizer state.
    /// </summary>
    public void Reset()
    {
        pendingData.Clear();
    }
}

// MediaKernel - DTS (Digital Theater Systems) Packetizer
// Parses DTS Core and DTS-HD sync frames for frame assembly and codec configuration.
// Based on ETSI TS 102 114 and VLC's dts_header.c/h

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// DTS sync word type indicating the frame format.
/// </summary>
public enum DtsSyncType : byte
{
    /// <summary>Unknown or no sync found.</summary>
    Unknown = 0,
    /// <summary>16-bit big-endian core frame.</summary>
    Core16BE = 1,
    /// <summary>16-bit little-endian core frame.</summary>
    Core16LE = 2,
    /// <summary>14-bit big-endian core frame.</summary>
    Core14BE = 3,
    /// <summary>14-bit little-endian core frame.</summary>
    Core14LE = 4,
    /// <summary>DTS-HD substream.</summary>
    Substream = 5,
    /// <summary>DTS Express (LBR) substream.</summary>
    SubstreamLbr = 6
}

/// <summary>
/// DTS profile/variant type.
/// </summary>
public enum DtsProfile : byte
{
    /// <summary>Standard DTS or DTS-ES.</summary>
    Dts = 0,
    /// <summary>DTS-HD Master Audio or High Resolution Audio.</summary>
    DtsHd = 1,
    /// <summary>DTS Express (Low Bit Rate).</summary>
    DtsExpress = 2
}

/// <summary>
/// DTS audio mode (speaker/channel configuration).
/// </summary>
public enum DtsAudioMode : byte
{
    Mono = 0,
    DualMono = 1,
    Stereo_LR = 2,
    Stereo_SumDiff = 3,
    Stereo_LtRt = 4,
    ThreeFront = 5,           // C, L, R
    TwoFrontOneSurround = 6,  // L, R, S
    ThreeFrontOneSurround = 7, // C, L, R, S
    TwoFrontTwoSurround = 8,   // L, R, SL, SR
    ThreeFrontTwoSurround = 9, // C, L, R, SL, SR (5.0)
    FourFrontTwoSurround = 10, // CL, CR, L, R, SL, SR
    ThreeFrontThreeSurround = 11, // C, L, R, SL, SR, SC
    FourFrontThreeSurround = 12,  // C, CL, CR, L, R, SL, SR
    FiveOneVariant = 13,      // 5.1 variant
    SixFront = 14,            // 6/0 front channels
    SevenOne = 15             // 7.1 configuration
}

/// <summary>
/// Parsed DTS frame header information.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct DtsFrameHeader
{
    /// <summary>Type of sync word detected.</summary>
    public readonly DtsSyncType SyncType;
    
    /// <summary>Profile/variant (DTS, DTS-HD, DTS Express).</summary>
    public readonly DtsProfile Profile;
    
    /// <summary>Audio mode (speaker configuration).</summary>
    public readonly DtsAudioMode AudioMode;
    
    /// <summary>Sample rate in Hz.</summary>
    public readonly int SampleRate;
    
    /// <summary>Bitrate in bits per second (0 for lossless).</summary>
    public readonly int Bitrate;
    
    /// <summary>Frame size in bytes (including sync word).</summary>
    public readonly int FrameSize;
    
    /// <summary>Number of audio samples in this frame.</summary>
    public readonly int SampleCount;
    
    /// <summary>Number of PCM sample blocks minus 1 (NBLKS field).</summary>
    public readonly byte BlockCount;
    
    /// <summary>Number of audio channels (excluding LFE).</summary>
    public readonly int ChannelCount;
    
    /// <summary>LFE channel present (0=none, 1=128 sample, 2=64 sample).</summary>
    public readonly byte LfePresent;
    
    /// <summary>Total channels including LFE.</summary>
    public int TotalChannels => ChannelCount + (LfePresent > 0 ? 1 : 0);
    
    /// <summary>CRC present in frame.</summary>
    public readonly bool CrcPresent;
    
    /// <summary>Frame is valid.</summary>
    public readonly bool IsValid;

    public DtsFrameHeader(
        DtsSyncType syncType, DtsProfile profile, DtsAudioMode audioMode,
        int sampleRate, int bitrate, int frameSize, int sampleCount,
        byte blockCount, int channelCount, byte lfePresent, bool crcPresent)
    {
        SyncType = syncType;
        Profile = profile;
        AudioMode = audioMode;
        SampleRate = sampleRate;
        Bitrate = bitrate;
        FrameSize = frameSize;
        SampleCount = sampleCount;
        BlockCount = blockCount;
        ChannelCount = channelCount;
        LfePresent = lfePresent;
        CrcPresent = crcPresent;
        IsValid = true;
    }
}

/// <summary>
/// DTS (Digital Theater Systems) audio frame packetizer.
/// Supports DTS Core, DTS-HD MA, DTS-HD HRA, and DTS Express.
/// </summary>
public sealed class DtsPacketizer
{
    private const int MinHeaderSize = 14;  // VLC_DTS_HEADER_SIZE
    private const int MaxFrameSize = 16384; // Reasonable max
    
    // Sync words
    private const uint SyncCore16BE = 0x7FFE8001;
    private const uint SyncCore16LE = 0xFE7F0180;
    private const uint SyncCore14BE = 0x1FFFE800;
    private const uint SyncCore14LE = 0xFF1F00E8;
    private const uint SyncSubstream = 0x64582025;
    private const uint SyncSubstreamLbr = 0x0A801921;
    
    // Sample rate lookup table (index 0-15)
    private static readonly int[] SampleRates =
    [
        0, 8000, 16000, 32000, 0, 0, 11025, 22050,
        44100, 0, 0, 12000, 24000, 48000, 96000, 192000
    ];
    
    // Bitrate lookup table in bps (index 0-31)
    private static readonly int[] Bitrates =
    [
        32000, 56000, 64000, 96000, 112000, 128000, 192000, 224000,
        256000, 320000, 384000, 448000, 512000, 576000, 640000, 768000,
        896000, 1024000, 1152000, 1280000, 1344000, 1408000, 1411200, 1472000,
        1536000, 1920000, 2048000, 3072000, 3840000, 0, 0, 0  // Last 3 reserved/lossless
    ];
    
    // Channel count by audio mode (excluding LFE)
    private static readonly int[] ChannelCounts =
    [
        1, 2, 2, 2, 2, 3, 3, 4, 4, 5, 6, 6, 7, 6, 6, 8
    ];
    
    private byte[] _buffer = new byte[65536];
    private int _bufferLength;
    
    /// <summary>
    /// Checks if data starts with a valid DTS sync word.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasSyncWord(ReadOnlySpan<byte> data)
    {
        return DetectSyncType(data) != DtsSyncType.Unknown;
    }
    
    /// <summary>
    /// Detects the sync word type at the start of data.
    /// </summary>
    public static DtsSyncType DetectSyncType(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
            return DtsSyncType.Unknown;
        
        // Check using byte patterns directly (matching VLC implementation)
        // 16-bit big-endian core: 0x7F 0xFE 0x80 0x01
        if (data[0] == 0x7F && data[1] == 0xFE && data[2] == 0x80 && data[3] == 0x01)
            return DtsSyncType.Core16BE;
        
        // 16-bit little-endian core: 0xFE 0x7F 0x01 0x80
        if (data[0] == 0xFE && data[1] == 0x7F && data[2] == 0x01 && data[3] == 0x80)
            return DtsSyncType.Core16LE;
        
        // DTS-HD substream: 0x64 0x58 0x20 0x25
        if (data[0] == 0x64 && data[1] == 0x58 && data[2] == 0x20 && data[3] == 0x25)
            return DtsSyncType.Substream;
        
        // DTS Express LBR substream: 0x0A 0x80 0x19 0x21
        if (data[0] == 0x0A && data[1] == 0x80 && data[2] == 0x19 && data[3] == 0x21)
            return DtsSyncType.SubstreamLbr;
        
        // 14-bit big-endian: 0x1F 0xFF 0xE8 0x00 + byte[4]=0x07 + (byte[5]&0xF0)=0xF0
        if (data[0] == 0x1F && data[1] == 0xFF && data[2] == 0xE8 && data[3] == 0x00 &&
            data[4] == 0x07 && (data[5] & 0xF0) == 0xF0)
            return DtsSyncType.Core14BE;
        
        // 14-bit little-endian: 0xFF 0x1F 0x00 0xE8 + (byte[4]&0xF0)=0xF0 + byte[5]=0x07
        if (data[0] == 0xFF && data[1] == 0x1F && data[2] == 0x00 && data[3] == 0xE8 &&
            (data[4] & 0xF0) == 0xF0 && data[5] == 0x07)
            return DtsSyncType.Core14LE;
        
        return DtsSyncType.Unknown;
    }
    
    /// <summary>
    /// Finds the first DTS sync word in the buffer.
    /// </summary>
    /// <returns>Offset to sync word, or -1 if not found.</returns>
    public static int FindSyncWord(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
            return -1;
        
        for (int i = 0; i <= data.Length - 6; i++)
        {
            if (DetectSyncType(data[i..]) != DtsSyncType.Unknown)
                return i;
        }
        
        return -1;
    }
    
    /// <summary>
    /// Attempts to parse a DTS core frame header (16-bit format).
    /// </summary>
    public static bool TryParseCore16Header(ReadOnlySpan<byte> data, bool bigEndian, out DtsFrameHeader header)
    {
        header = default;
        
        if (data.Length < MinHeaderSize)
            return false;
        
        // Convert to big-endian if needed for parsing
        Span<byte> headerBytes = stackalloc byte[MinHeaderSize];
        if (bigEndian)
        {
            data[..MinHeaderSize].CopyTo(headerBytes);
        }
        else
        {
            // Swap bytes for little-endian
            for (int i = 0; i < MinHeaderSize; i += 2)
            {
                headerBytes[i] = data[i + 1];
                headerBytes[i + 1] = data[i];
            }
        }
        
        // Parse bit fields after sync word (starting at byte 4)
        // Bits 0-0: FTYPE, 1-5: SHORT, 6: CPF, 7-13: NBLKS, 14-27: FSIZE
        // Bits 28-33: AMODE, 34-37: SFREQ, 38-42: RATE, etc.
        
        // Pack bytes 4-9 for easier bit extraction
        ulong bits = ((ulong)headerBytes[4] << 56) |
                     ((ulong)headerBytes[5] << 48) |
                     ((ulong)headerBytes[6] << 40) |
                     ((ulong)headerBytes[7] << 32) |
                     ((ulong)headerBytes[8] << 24) |
                     ((ulong)headerBytes[9] << 16) |
                     ((ulong)headerBytes[10] << 8) |
                     headerBytes[11];
        
        // Extract fields (bit positions from MSB)
        // bool frameType = ((bits >> 63) & 1) == 1;
        // int shortFrame = (int)((bits >> 58) & 0x1F);
        bool crcPresent = ((bits >> 57) & 1) == 1;
        int nblks = (int)((bits >> 50) & 0x7F);        // 7 bits: blocks - 1
        int fsize = (int)((bits >> 36) & 0x3FFF);      // 14 bits: frame size - 1
        int amode = (int)((bits >> 30) & 0x3F);        // 6 bits: audio mode
        int sfreq = (int)((bits >> 26) & 0x0F);        // 4 bits: sample rate index
        int rate = (int)((bits >> 21) & 0x1F);         // 5 bits: bitrate index
        // Skip fixed bit (should be 1)
        // Bits 43-50 contain dynf, timef, auxf, hdcd, ext_audio_id, ext_audio
        int lff = (int)((bits >> 8) & 0x03);           // 2 bits: LFE flag
        
        // Validate indices
        if (amode > 15) amode = 9; // Default to 5.0
        if (sfreq >= SampleRates.Length || SampleRates[sfreq] == 0)
            return false;
        
        int sampleRate = SampleRates[sfreq];
        int bitrate = rate < Bitrates.Length ? Bitrates[rate] : 0;
        int frameSize = fsize + 1;
        int sampleCount = (nblks + 1) * 32;
        int channelCount = amode < ChannelCounts.Length ? ChannelCounts[amode] : 2;
        
        var syncType = bigEndian ? DtsSyncType.Core16BE : DtsSyncType.Core16LE;
        
        header = new DtsFrameHeader(
            syncType,
            DtsProfile.Dts,
            (DtsAudioMode)Math.Min(amode, 15),
            sampleRate,
            bitrate,
            frameSize,
            sampleCount,
            (byte)nblks,
            channelCount,
            (byte)lff,
            crcPresent
        );
        
        return true;
    }
    
    /// <summary>
    /// Attempts to parse a DTS core frame header (14-bit format).
    /// 14-bit frames pack 14 bits per 16-bit word.
    /// </summary>
    public static bool TryParseCore14Header(ReadOnlySpan<byte> data, bool bigEndian, out DtsFrameHeader header)
    {
        header = default;
        
        // 14-bit mode requires conversion - need more bytes
        if (data.Length < 16)
            return false;
        
        // Convert 14-bit to 16-bit for parsing
        // Each 16-bit word has 14 bits of data + 2 marker bits
        Span<byte> converted = stackalloc byte[MinHeaderSize];
        
        int srcIdx = 4; // Skip sync word
        int dstIdx = 4;
        int bitBuf = 0;
        int bitCount = 0;
        
        // Copy sync word as-is (already validated)
        if (bigEndian)
        {
            converted[0] = 0x7F; converted[1] = 0xFE;
            converted[2] = 0x80; converted[3] = 0x01;
        }
        else
        {
            converted[0] = 0xFE; converted[1] = 0x7F;
            converted[2] = 0x01; converted[3] = 0x80;
        }
        
        // Convert remaining header (simplified - just parse as 16-bit after approximation)
        // This is a simplified approach; full conversion would be more complex
        while (dstIdx < MinHeaderSize && srcIdx + 1 < data.Length)
        {
            int word = bigEndian
                ? (data[srcIdx] << 8) | data[srcIdx + 1]
                : (data[srcIdx + 1] << 8) | data[srcIdx];
            
            bitBuf = (bitBuf << 14) | (word & 0x3FFF);
            bitCount += 14;
            srcIdx += 2;
            
            while (bitCount >= 8 && dstIdx < MinHeaderSize)
            {
                bitCount -= 8;
                converted[dstIdx++] = (byte)(bitBuf >> bitCount);
            }
        }
        
        // Parse as 16-bit big-endian
        if (!TryParseCore16Header(converted, true, out header))
            return false;
        
        // Adjust frame size for 14-bit mode: size in bytes = (fsize * 16) / 14
        var syncType = bigEndian ? DtsSyncType.Core14BE : DtsSyncType.Core14LE;
        int adjustedFrameSize = (header.FrameSize * 16 + 13) / 14;
        
        header = new DtsFrameHeader(
            syncType,
            header.Profile,
            header.AudioMode,
            header.SampleRate,
            header.Bitrate,
            adjustedFrameSize,
            header.SampleCount,
            header.BlockCount,
            header.ChannelCount,
            header.LfePresent,
            header.CrcPresent
        );
        
        return true;
    }
    
    /// <summary>
    /// Attempts to parse a DTS-HD substream header.
    /// </summary>
    public static bool TryParseSubstreamHeader(ReadOnlySpan<byte> data, out DtsFrameHeader header)
    {
        header = default;
        
        if (data.Length < 12)
            return false;
        
        // Substream header structure varies; this is simplified
        // The actual frame size is in the header
        
        // Check sync
        uint sync = BinaryPrimitives.ReadUInt32BigEndian(data);
        bool isLbr = sync == SyncSubstreamLbr;
        
        if (sync != SyncSubstream && !isLbr)
            return false;
        
        var profile = isLbr ? DtsProfile.DtsExpress : DtsProfile.DtsHd;
        
        // Parse basic substream info (simplified)
        // Actual DTS-HD parsing is complex with multiple extension types
        int headerSize = isLbr ? 8 : 12;
        int frameSize = headerSize; // Would be parsed from actual header
        
        // For substreams, we'd typically find and parse the core first
        // This is a placeholder for substream detection
        header = new DtsFrameHeader(
            isLbr ? DtsSyncType.SubstreamLbr : DtsSyncType.Substream,
            profile,
            DtsAudioMode.ThreeFrontTwoSurround, // Default 5.0
            48000,  // Default
            0,      // Lossless/variable
            frameSize,
            0,      // Varies
            0,
            5,      // Default 5.0
            1,      // Assume LFE
            false
        );
        
        return true;
    }
    
    /// <summary>
    /// Attempts to parse any DTS frame header, auto-detecting the format.
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out DtsFrameHeader header)
    {
        header = default;
        
        var syncType = DetectSyncType(data);
        
        return syncType switch
        {
            DtsSyncType.Core16BE => TryParseCore16Header(data, bigEndian: true, out header),
            DtsSyncType.Core16LE => TryParseCore16Header(data, bigEndian: false, out header),
            DtsSyncType.Core14BE => TryParseCore14Header(data, bigEndian: true, out header),
            DtsSyncType.Core14LE => TryParseCore14Header(data, bigEndian: false, out header),
            DtsSyncType.Substream or DtsSyncType.SubstreamLbr => TryParseSubstreamHeader(data, out header),
            _ => false
        };
    }
    
    /// <summary>
    /// Feeds data into the packetizer buffer.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(data.Length);
        data.CopyTo(_buffer.AsSpan(_bufferLength));
        _bufferLength += data.Length;
    }
    
    /// <summary>
    /// Attempts to read a complete DTS frame from the buffer.
    /// </summary>
    /// <param name="header">The parsed frame header.</param>
    /// <param name="frameData">The complete frame data including header.</param>
    /// <returns>True if a complete frame was read.</returns>
    public bool TryReadFrame(out DtsFrameHeader header, out ReadOnlySpan<byte> frameData)
    {
        header = default;
        frameData = default;
        
        if (_bufferLength < MinHeaderSize)
            return false;
        
        // Find sync word
        var buffer = _buffer.AsSpan(0, _bufferLength);
        int syncOffset = FindSyncWord(buffer);
        
        if (syncOffset < 0)
        {
            // No sync found, keep last 5 bytes in case sync straddles
            int keep = Math.Min(5, _bufferLength);
            if (_bufferLength > keep)
            {
                Buffer.BlockCopy(_buffer, _bufferLength - keep, _buffer, 0, keep);
                _bufferLength = keep;
            }
            return false;
        }
        
        // Skip bytes before sync
        if (syncOffset > 0)
        {
            Buffer.BlockCopy(_buffer, syncOffset, _buffer, 0, _bufferLength - syncOffset);
            _bufferLength -= syncOffset;
            buffer = _buffer.AsSpan(0, _bufferLength);
        }
        
        // Try to parse header
        if (!TryParseHeader(buffer, out header))
            return false;
        
        // Check if we have the complete frame
        if (_bufferLength < header.FrameSize)
            return false;
        
        // Validate frame size is reasonable
        if (header.FrameSize < MinHeaderSize || header.FrameSize > MaxFrameSize)
        {
            // Invalid frame, skip sync word and try again
            Buffer.BlockCopy(_buffer, 4, _buffer, 0, _bufferLength - 4);
            _bufferLength -= 4;
            return false;
        }
        
        frameData = buffer[..header.FrameSize];
        
        // Remove frame from buffer
        Buffer.BlockCopy(_buffer, header.FrameSize, _buffer, 0, _bufferLength - header.FrameSize);
        _bufferLength -= header.FrameSize;
        
        return true;
    }
    
    /// <summary>
    /// Gets the number of bytes currently buffered.
    /// </summary>
    public int BufferedBytes => _bufferLength;
    
    /// <summary>
    /// Resets the packetizer, clearing all buffered data.
    /// </summary>
    public void Reset()
    {
        _bufferLength = 0;
    }
    
    private void EnsureCapacity(int additionalBytes)
    {
        int required = _bufferLength + additionalBytes;
        if (required > _buffer.Length)
        {
            int newSize = Math.Max(_buffer.Length * 2, required);
            Array.Resize(ref _buffer, newSize);
        }
    }
}

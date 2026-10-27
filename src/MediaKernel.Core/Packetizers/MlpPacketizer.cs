// MediaKernel - TrueHD/MLP (Meridian Lossless Packing) Packetizer
// Parses Dolby TrueHD and MLP lossless audio frames.
// Based on MLP/TrueHD specification and VLC's mlp.c

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// MLP/TrueHD stream type.
/// </summary>
public enum MlpStreamType : byte
{
    /// <summary>Unknown stream type.</summary>
    Unknown = 0,
    /// <summary>MLP (Meridian Lossless Packing) - DVD-Audio.</summary>
    Mlp = 0xBB,
    /// <summary>TrueHD (Dolby TrueHD) - Blu-ray.</summary>
    TrueHd = 0xBA
}

/// <summary>
/// Parsed MLP/TrueHD frame header information.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct MlpFrameHeader
{
    /// <summary>Stream type (MLP or TrueHD).</summary>
    public readonly MlpStreamType StreamType;
    
    /// <summary>Whether this is a major sync frame (contains full header).</summary>
    public readonly bool IsMajorSync;
    
    /// <summary>Frame size in bytes.</summary>
    public readonly int FrameSize;
    
    /// <summary>Sample rate in Hz.</summary>
    public readonly int SampleRate;
    
    /// <summary>Number of audio channels.</summary>
    public readonly int ChannelCount;
    
    /// <summary>Number of samples per frame.</summary>
    public readonly int SampleCount;
    
    /// <summary>Bitrate in bits per second (0 for VBR).</summary>
    public readonly int Bitrate;
    
    /// <summary>Number of substreams.</summary>
    public readonly byte SubstreamCount;
    
    /// <summary>Variable bitrate flag.</summary>
    public readonly bool IsVbr;
    
    /// <summary>Whether this header is valid.</summary>
    public readonly bool IsValid;

    public MlpFrameHeader(
        MlpStreamType streamType, bool isMajorSync, int frameSize,
        int sampleRate, int channelCount, int sampleCount,
        int bitrate, byte substreamCount, bool isVbr)
    {
        StreamType = streamType;
        IsMajorSync = isMajorSync;
        FrameSize = frameSize;
        SampleRate = sampleRate;
        ChannelCount = channelCount;
        SampleCount = sampleCount;
        Bitrate = bitrate;
        SubstreamCount = substreamCount;
        IsVbr = isVbr;
        IsValid = true;
    }
}

/// <summary>
/// TrueHD/MLP lossless audio packetizer.
/// </summary>
public sealed class MlpPacketizer
{
    private const int MinHeaderSize = 8;
    private const int MajorSyncHeaderSize = 28;
    private const int MaxFrameSize = 65536;
    
    // MLP channel count lookup (5-bit index)
    private static readonly int[] MlpChannelCounts =
    [
        1, 2, 3, 4, 3, 4, 5, 3, 4, 5, 4, 5, 6, 4, 5, 4,
        5, 6, 5, 5, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    ];
    
    // TrueHD channel count per bit (13-bit mask)
    private static readonly int[] TrueHdChannelPerBit =
    [
        2, 1, 1, 2, 2, 2, 2, 1, 1, 2, 2, 1, 1
    ];
    
    private byte[] _buffer = new byte[131072];
    private int _bufferLength;
    private MlpFrameHeader _lastMajorSync;
    
    /// <summary>
    /// Major sync pattern: 0xF8 0x72 0x6F at offset 4.
    /// </summary>
    public static ReadOnlySpan<byte> MajorSyncPattern => [0xF8, 0x72, 0x6F];
    
    /// <summary>
    /// Checks if data contains a major sync at the expected position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasMajorSync(ReadOnlySpan<byte> data)
    {
        return data.Length >= 7 &&
               data[4] == 0xF8 &&
               data[5] == 0x72 &&
               data[6] == 0x6F;
    }
    
    /// <summary>
    /// Finds the first major sync in the buffer.
    /// </summary>
    /// <returns>Offset to frame start (4 bytes before sync), or -1 if not found.</returns>
    public static int FindMajorSync(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7)
            return -1;
        
        for (int i = 0; i <= data.Length - 7; i++)
        {
            if (data[i + 4] == 0xF8 &&
                data[i + 5] == 0x72 &&
                data[i + 6] == 0x6F)
                return i;
        }
        
        return -1;
    }
    
    /// <summary>
    /// Gets the frame size from the first two bytes of a frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetFrameSize(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return 0;
        
        // 12-bit length field in words, multiply by 2 for bytes
        int words = ((data[0] << 8) | data[1]) & 0xFFF;
        return words * 2;
    }
    
    /// <summary>
    /// Attempts to parse a MLP/TrueHD frame header.
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out MlpFrameHeader header)
    {
        header = default;
        
        if (data.Length < MinHeaderSize)
            return false;
        
        // Get frame size from first two bytes
        int frameSize = GetFrameSize(data);
        if (frameSize < MinHeaderSize || frameSize > MaxFrameSize)
            return false;
        
        // Check for major sync
        bool isMajorSync = HasMajorSync(data);
        
        if (isMajorSync)
        {
            return TryParseMajorSyncHeader(data, out header);
        }
        else
        {
            // Minor sync - just frame size is available
            header = new MlpFrameHeader(
                MlpStreamType.Unknown,
                isMajorSync: false,
                frameSize,
                sampleRate: 0,
                channelCount: 0,
                sampleCount: 0,
                bitrate: 0,
                substreamCount: 0,
                isVbr: false);
            return true;
        }
    }
    
    /// <summary>
    /// Parses a major sync frame header (contains full codec info).
    /// </summary>
    public static bool TryParseMajorSyncHeader(ReadOnlySpan<byte> data, out MlpFrameHeader header)
    {
        header = default;
        
        if (data.Length < MajorSyncHeaderSize)
            return false;
        
        // Verify major sync pattern
        if (!HasMajorSync(data))
            return false;
        
        int frameSize = GetFrameSize(data);
        if (frameSize < MajorSyncHeaderSize)
            return false;
        
        // Byte 7: stream type
        var streamType = (MlpStreamType)data[7];
        if (streamType != MlpStreamType.Mlp && streamType != MlpStreamType.TrueHd)
            return false;
        
        // Parse rate index from header
        // Bytes 8-11 contain rate/channel info (layout varies between MLP and TrueHD)
        int rateIdx = (data[8] >> 4) & 0x0F;
        
        // Calculate sample rate
        int sampleRate = CalculateSampleRate(rateIdx);
        if (sampleRate == 0)
            return false;
        
        // Calculate samples per frame: 40 << (rate_idx & 7)
        int sampleCount = 40 << (rateIdx & 0x07);
        
        // Parse channel configuration
        int channelCount;
        if (streamType == MlpStreamType.Mlp)
        {
            // MLP uses 5-bit channel index in byte 11
            int channelIdx = data[11] & 0x1F;
            channelCount = channelIdx < MlpChannelCounts.Length ? MlpChannelCounts[channelIdx] : 0;
        }
        else
        {
            // TrueHD uses 13-bit channel mask
            int channelMask = ((data[9] & 0x1F) << 8) | data[10];
            channelCount = CountTrueHdChannels(channelMask);
        }
        
        if (channelCount == 0)
            return false;
        
        // Parse VBR flag and bitrate
        bool isVbr = (data[12] & 0x80) != 0;
        int bitrate = 0;
        if (!isVbr)
        {
            int bitrateField = ((data[13] & 0x7F) << 8) | data[14];
            bitrate = (bitrateField * sampleRate + 8) / 16;
        }
        
        // Substream count (4 bits)
        byte substreamCount = (byte)((data[15] >> 4) & 0x0F);
        
        header = new MlpFrameHeader(
            streamType,
            isMajorSync: true,
            frameSize,
            sampleRate,
            channelCount,
            sampleCount,
            bitrate,
            substreamCount,
            isVbr);
        
        return true;
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
    /// Attempts to read a complete MLP/TrueHD frame from the buffer.
    /// </summary>
    public bool TryReadFrame(out MlpFrameHeader header, out ReadOnlySpan<byte> frameData)
    {
        header = default;
        frameData = default;
        
        if (_bufferLength < MinHeaderSize)
            return false;
        
        var buffer = _buffer.AsSpan(0, _bufferLength);
        
        // If we don't have major sync info yet, find one
        if (!_lastMajorSync.IsValid)
        {
            int syncOffset = FindMajorSync(buffer);
            if (syncOffset < 0)
            {
                // Keep last few bytes in case sync straddles
                int keep = Math.Min(6, _bufferLength);
                if (_bufferLength > keep)
                {
                    Buffer.BlockCopy(_buffer, _bufferLength - keep, _buffer, 0, keep);
                    _bufferLength = keep;
                }
                return false;
            }
            
            // Skip to major sync
            if (syncOffset > 0)
            {
                Buffer.BlockCopy(_buffer, syncOffset, _buffer, 0, _bufferLength - syncOffset);
                _bufferLength -= syncOffset;
                buffer = _buffer.AsSpan(0, _bufferLength);
            }
        }
        
        // Parse header
        if (!TryParseHeader(buffer, out header))
        {
            // Invalid header, skip some bytes
            Buffer.BlockCopy(_buffer, 2, _buffer, 0, _bufferLength - 2);
            _bufferLength -= 2;
            return false;
        }
        
        // Update major sync info if this is one
        if (header.IsMajorSync)
            _lastMajorSync = header;
        
        // Apply cached major sync info to minor sync frames
        if (!header.IsMajorSync && _lastMajorSync.IsValid)
        {
            header = new MlpFrameHeader(
                _lastMajorSync.StreamType,
                isMajorSync: false,
                header.FrameSize,
                _lastMajorSync.SampleRate,
                _lastMajorSync.ChannelCount,
                _lastMajorSync.SampleCount,
                _lastMajorSync.Bitrate,
                _lastMajorSync.SubstreamCount,
                _lastMajorSync.IsVbr);
        }
        
        // Check if we have the complete frame
        if (_bufferLength < header.FrameSize)
            return false;
        
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
    /// Resets the packetizer, clearing all buffered data and cached state.
    /// </summary>
    public void Reset()
    {
        _bufferLength = 0;
        _lastMajorSync = default;
    }
    
    private static int CalculateSampleRate(int rateIdx)
    {
        if (rateIdx == 0x0F)
            return 0; // Reserved
        
        // Bit 3: base rate (0=48kHz, 1=44.1kHz)
        // Bits 0-2: multiplier (shift amount)
        int baseRate = (rateIdx & 0x08) != 0 ? 44100 : 48000;
        int shift = rateIdx & 0x07;
        
        return baseRate << shift;
    }
    
    private static int CountTrueHdChannels(int mask)
    {
        int count = 0;
        for (int i = 0; i < 13 && i < TrueHdChannelPerBit.Length; i++)
        {
            if ((mask & (1 << i)) != 0)
                count += TrueHdChannelPerBit[i];
        }
        return count;
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

// MediaKernel - MPEG Audio (MP3/MP2/MP1) Packetizer
// Parses MPEG audio frames for frame assembly and codec configuration.
// Based on ISO/IEC 11172-3 and ISO/IEC 13818-3

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// MPEG audio layer (MP1, MP2, MP3).
/// </summary>
public enum MpegAudioLayer : byte
{
    /// <summary>Reserved (invalid).</summary>
    Reserved = 0,
    /// <summary>Layer III (MP3).</summary>
    Layer3 = 1,
    /// <summary>Layer II (MP2).</summary>
    Layer2 = 2,
    /// <summary>Layer I (MP1).</summary>
    Layer1 = 3
}

/// <summary>
/// MPEG audio version.
/// </summary>
public enum MpegAudioVersion : byte
{
    /// <summary>MPEG Version 2.5 (unofficial extension).</summary>
    Mpeg25 = 0,
    /// <summary>Reserved (invalid).</summary>
    Reserved = 1,
    /// <summary>MPEG Version 2 (ISO/IEC 13818-3).</summary>
    Mpeg2 = 2,
    /// <summary>MPEG Version 1 (ISO/IEC 11172-3).</summary>
    Mpeg1 = 3
}

/// <summary>
/// MPEG audio channel mode.
/// </summary>
public enum MpegAudioChannelMode : byte
{
    /// <summary>Stereo.</summary>
    Stereo = 0,
    /// <summary>Joint stereo (MS stereo and/or intensity stereo).</summary>
    JointStereo = 1,
    /// <summary>Dual channel (two independent mono channels).</summary>
    DualChannel = 2,
    /// <summary>Single channel (mono).</summary>
    Mono = 3
}

/// <summary>
/// Parsed MPEG audio frame header information.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct MpegAudioFrameHeader
{
    /// <summary>MPEG audio version.</summary>
    public readonly MpegAudioVersion Version;
    
    /// <summary>MPEG audio layer.</summary>
    public readonly MpegAudioLayer Layer;
    
    /// <summary>Whether CRC protection is present.</summary>
    public readonly bool HasCrc;
    
    /// <summary>Bitrate in kbps.</summary>
    public readonly int Bitrate;
    
    /// <summary>Sample rate in Hz.</summary>
    public readonly int SampleRate;
    
    /// <summary>Whether padding bit is set.</summary>
    public readonly bool HasPadding;
    
    /// <summary>Channel mode.</summary>
    public readonly MpegAudioChannelMode ChannelMode;
    
    /// <summary>Number of audio channels.</summary>
    public readonly int ChannelCount;
    
    /// <summary>Frame size in bytes (including header).</summary>
    public readonly int FrameSize;
    
    /// <summary>Maximum frame size in bytes (for VBR).</summary>
    public readonly int MaxFrameSize;
    
    /// <summary>Number of samples per frame.</summary>
    public readonly int SamplesPerFrame;
    
    /// <summary>Whether this header is valid.</summary>
    public readonly bool IsValid;

    public MpegAudioFrameHeader(
        MpegAudioVersion version, MpegAudioLayer layer, bool hasCrc,
        int bitrate, int sampleRate, bool hasPadding,
        MpegAudioChannelMode channelMode, int channelCount,
        int frameSize, int maxFrameSize, int samplesPerFrame)
    {
        Version = version;
        Layer = layer;
        HasCrc = hasCrc;
        Bitrate = bitrate;
        SampleRate = sampleRate;
        HasPadding = hasPadding;
        ChannelMode = channelMode;
        ChannelCount = channelCount;
        FrameSize = frameSize;
        MaxFrameSize = maxFrameSize;
        SamplesPerFrame = samplesPerFrame;
        IsValid = true;
    }
}

/// <summary>
/// MPEG Audio (MP3/MP2/MP1) frame packetizer.
/// </summary>
public sealed class MpegAudioPacketizer
{
    private const int MinHeaderSize = 4;
    private const int MaxFrameSize = 4096; // Reasonable max
    
    // Bitrate tables [version_index][layer-1][bitrate_index]
    // Version index: 0 = MPEG1, 1 = MPEG2/2.5
    private static readonly int[,] BitratesMpeg1 = new int[3, 16]
    {
        // Layer 1
        { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0 },
        // Layer 2
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0 },
        // Layer 3
        { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 }
    };
    
    private static readonly int[,] BitratesMpeg2 = new int[3, 16]
    {
        // Layer 1
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0 },
        // Layer 2
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 },
        // Layer 3
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 }
    };
    
    // Sample rate tables [version_index][samplerate_index]
    private static readonly int[,] SampleRates = new int[4, 4]
    {
        // MPEG 2.5
        { 11025, 12000, 8000, 0 },
        // Reserved
        { 0, 0, 0, 0 },
        // MPEG 2
        { 22050, 24000, 16000, 0 },
        // MPEG 1
        { 44100, 48000, 32000, 0 }
    };
    
    private byte[] _buffer = new byte[65536];
    private int _bufferLength;
    
    /// <summary>
    /// Checks if data starts with a valid MPEG audio sync word.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasSyncWord(ReadOnlySpan<byte> data)
    {
        // Sync word: 11 bits set (0xFF, upper 3 bits of next byte)
        return data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0;
    }
    
    /// <summary>
    /// Finds the first MPEG audio sync word in the buffer.
    /// </summary>
    /// <returns>Offset to sync word, or -1 if not found.</returns>
    public static int FindSyncWord(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return -1;
        
        for (int i = 0; i <= data.Length - 2; i++)
        {
            if (data[i] == 0xFF && (data[i + 1] & 0xE0) == 0xE0)
                return i;
        }
        
        return -1;
    }
    
    /// <summary>
    /// Attempts to parse an MPEG audio frame header.
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out MpegAudioFrameHeader header)
    {
        header = default;
        
        if (data.Length < MinHeaderSize)
            return false;
        
        // Check sync word
        if (!HasSyncWord(data))
            return false;
        
        // Parse header bytes as 32-bit value (big-endian)
        uint headerBits = ((uint)data[0] << 24) | ((uint)data[1] << 16) |
                         ((uint)data[2] << 8) | data[3];
        
        // Extract fields
        bool isMpeg25 = (headerBits & 0x100000) == 0;
        int versionBit = (int)((headerBits >> 19) & 0x01);
        var version = isMpeg25 ? MpegAudioVersion.Mpeg25 :
                     (versionBit == 0 ? MpegAudioVersion.Mpeg2 : MpegAudioVersion.Mpeg1);
        
        int layerBits = (int)((headerBits >> 17) & 0x03);
        var layer = (MpegAudioLayer)layerBits;
        
        if (layer == MpegAudioLayer.Reserved)
            return false;
        
        bool hasCrc = ((headerBits >> 16) & 0x01) == 0;
        int bitrateIndex = (int)((headerBits >> 12) & 0x0F);
        int sampleRateIndex = (int)((headerBits >> 10) & 0x03);
        bool hasPadding = ((headerBits >> 9) & 0x01) == 1;
        int modeBits = (int)((headerBits >> 6) & 0x03);
        int emphasis = (int)(headerBits & 0x03);
        
        // Validate indices
        if (bitrateIndex == 0x0F || sampleRateIndex == 0x03 || emphasis == 0x02)
            return false;
        
        // Get actual values
        int versionIndex = version == MpegAudioVersion.Mpeg1 ? 0 : 1;
        int layerIndex = 3 - (int)layer; // Layer1=0, Layer2=1, Layer3=2
        
        int bitrate = versionIndex == 0
            ? BitratesMpeg1[layerIndex, bitrateIndex]
            : BitratesMpeg2[layerIndex, bitrateIndex];
        
        int sampleRate = SampleRates[(int)version, sampleRateIndex];
        if (sampleRate == 0)
            return false;
        
        var channelMode = (MpegAudioChannelMode)modeBits;
        int channelCount = channelMode == MpegAudioChannelMode.Mono ? 1 : 2;
        
        // Calculate frame size and samples per frame
        int samplesPerFrame;
        int frameSize;
        int maxFrameSize;
        int maxBitrate = versionIndex == 0
            ? BitratesMpeg1[layerIndex, 14]
            : BitratesMpeg2[layerIndex, 14];
        
        int padding = hasPadding ? 1 : 0;
        
        switch (layer)
        {
            case MpegAudioLayer.Layer1:
                samplesPerFrame = 384;
                frameSize = (12 * bitrate * 1000 / sampleRate + padding) * 4;
                maxFrameSize = (12 * maxBitrate * 1000 / sampleRate + 1) * 4;
                break;
            
            case MpegAudioLayer.Layer2:
                samplesPerFrame = 1152;
                frameSize = 144 * bitrate * 1000 / sampleRate + padding;
                maxFrameSize = 144 * maxBitrate * 1000 / sampleRate + 1;
                break;
            
            case MpegAudioLayer.Layer3:
                samplesPerFrame = versionIndex == 1 ? 576 : 1152;
                int multiplier = versionIndex == 1 ? 72 : 144;
                frameSize = multiplier * bitrate * 1000 / sampleRate + padding;
                maxFrameSize = multiplier * maxBitrate * 1000 / sampleRate + 1;
                break;
            
            default:
                return false;
        }
        
        // Free bitrate mode (bitrate=0) can have larger frames
        if (bitrate == 0)
            maxFrameSize *= 2;
        
        header = new MpegAudioFrameHeader(
            version, layer, hasCrc,
            bitrate, sampleRate, hasPadding,
            channelMode, channelCount,
            frameSize, maxFrameSize, samplesPerFrame);
        
        return frameSize > 0;
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
    /// Attempts to read a complete MPEG audio frame from the buffer.
    /// </summary>
    public bool TryReadFrame(out MpegAudioFrameHeader header, out ReadOnlySpan<byte> frameData)
    {
        header = default;
        frameData = default;
        
        if (_bufferLength < MinHeaderSize)
            return false;
        
        var buffer = _buffer.AsSpan(0, _bufferLength);
        int syncOffset = FindSyncWord(buffer);
        
        if (syncOffset < 0)
        {
            // Keep last byte in case sync straddles
            if (_bufferLength > 1)
            {
                _buffer[0] = _buffer[_bufferLength - 1];
                _bufferLength = 1;
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
        
        // Parse header
        if (!TryParseHeader(buffer, out header))
        {
            // Invalid header, skip sync and try again
            Buffer.BlockCopy(_buffer, 2, _buffer, 0, _bufferLength - 2);
            _bufferLength -= 2;
            return false;
        }
        
        // Check if we have the complete frame
        if (_bufferLength < header.FrameSize)
            return false;
        
        // Validate frame size
        if (header.FrameSize < MinHeaderSize || header.FrameSize > MaxFrameSize)
        {
            // Invalid, skip sync
            Buffer.BlockCopy(_buffer, 2, _buffer, 0, _bufferLength - 2);
            _bufferLength -= 2;
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

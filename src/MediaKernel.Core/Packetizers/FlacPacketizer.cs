// MediaKernel - FLAC (Free Lossless Audio Codec) Packetizer
// Parses FLAC audio frames for frame assembly and codec configuration.
// Based on FLAC format specification and VLC's flac.c/h

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Packetizers;

/// <summary>
/// FLAC stream metadata block types.
/// </summary>
public enum FlacMetadataType : byte
{
    StreamInfo = 0,
    Padding = 1,
    Application = 2,
    SeekTable = 3,
    VorbisComment = 4,
    CueSheet = 5,
    Picture = 6
}

/// <summary>
/// FLAC stream info from the mandatory STREAMINFO metadata block.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct FlacStreamInfo
{
    /// <summary>Minimum block size (samples per block).</summary>
    public readonly ushort MinBlockSize;
    
    /// <summary>Maximum block size (samples per block).</summary>
    public readonly ushort MaxBlockSize;
    
    /// <summary>Minimum frame size in bytes (0 = unknown).</summary>
    public readonly int MinFrameSize;
    
    /// <summary>Maximum frame size in bytes (0 = unknown).</summary>
    public readonly int MaxFrameSize;
    
    /// <summary>Sample rate in Hz.</summary>
    public readonly int SampleRate;
    
    /// <summary>Number of audio channels (1-8).</summary>
    public readonly byte ChannelCount;
    
    /// <summary>Bits per sample (4-32).</summary>
    public readonly byte BitsPerSample;
    
    /// <summary>Total samples in stream (0 = unknown).</summary>
    public readonly ulong TotalSamples;
    
    /// <summary>MD5 signature of the unencoded audio data.</summary>
    public readonly byte[] Md5Signature;
    
    /// <summary>Whether this stream info is valid.</summary>
    public readonly bool IsValid;

    public FlacStreamInfo(
        ushort minBlockSize, ushort maxBlockSize,
        int minFrameSize, int maxFrameSize,
        int sampleRate, byte channelCount, byte bitsPerSample,
        ulong totalSamples, byte[] md5Signature)
    {
        MinBlockSize = minBlockSize;
        MaxBlockSize = maxBlockSize;
        MinFrameSize = minFrameSize;
        MaxFrameSize = maxFrameSize;
        SampleRate = sampleRate;
        ChannelCount = channelCount;
        BitsPerSample = bitsPerSample;
        TotalSamples = totalSamples;
        Md5Signature = md5Signature;
        IsValid = true;
    }
}

/// <summary>
/// Parsed FLAC frame header information.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct FlacFrameHeader
{
    /// <summary>Whether this frame uses variable block size encoding.</summary>
    public readonly bool IsVariableBlockSize;
    
    /// <summary>Block size in samples.</summary>
    public readonly int BlockSize;
    
    /// <summary>Sample rate in Hz (0 = get from STREAMINFO).</summary>
    public readonly int SampleRate;
    
    /// <summary>Number of audio channels.</summary>
    public readonly byte ChannelCount;
    
    /// <summary>Bits per sample (0 = get from STREAMINFO).</summary>
    public readonly byte BitsPerSample;
    
    /// <summary>Frame/sample number depending on blocking strategy.</summary>
    public readonly ulong FrameOrSampleNumber;
    
    /// <summary>Size of the frame header in bytes.</summary>
    public readonly int HeaderSize;
    
    /// <summary>CRC-8 of the frame header.</summary>
    public readonly byte HeaderCrc8;
    
    /// <summary>Whether this header is valid.</summary>
    public readonly bool IsValid;

    public FlacFrameHeader(
        bool isVariableBlockSize, int blockSize, int sampleRate,
        byte channelCount, byte bitsPerSample, ulong frameOrSampleNumber,
        int headerSize, byte headerCrc8)
    {
        IsVariableBlockSize = isVariableBlockSize;
        BlockSize = blockSize;
        SampleRate = sampleRate;
        ChannelCount = channelCount;
        BitsPerSample = bitsPerSample;
        FrameOrSampleNumber = frameOrSampleNumber;
        HeaderSize = headerSize;
        HeaderCrc8 = headerCrc8;
        IsValid = true;
    }
}

/// <summary>
/// FLAC audio frame packetizer.
/// </summary>
public sealed class FlacPacketizer
{
    private const int MinHeaderSize = 9;    // FLAC_HEADER_SIZE_MIN
    private const int MaxHeaderSize = 16;   // FLAC_HEADER_SIZE_MAX
    private const int MaxFrameSize = 65535 * 2; // Max reasonable frame size
    
    // Sample rate lookup table (index 0-11, multiply by 10)
    private static readonly int[] SampleRates =
    [
        0, 88200, 176400, 192000, 8000, 16000, 22050, 24000,
        32000, 44100, 48000, 96000
    ];
    
    // Bits per sample lookup table (index 0-7)
    private static readonly int[] BitsPerSampleTable =
    [
        0, 8, 12, -1, 16, 20, 24, -1
    ];
    
    // CRC-8 lookup table for FLAC (polynomial x^8 + x^2 + x + 1)
    private static readonly byte[] Crc8Table = BuildCrc8Table();
    
    private byte[] _buffer = new byte[131072];
    private int _bufferLength;
    private FlacStreamInfo _streamInfo;
    
    /// <summary>
    /// Checks if data starts with a FLAC frame sync code.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasSyncWord(ReadOnlySpan<byte> data)
    {
        // FLAC sync: first byte 0xFF, second byte & 0xFE == 0xF8
        return data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xFE) == 0xF8;
    }
    
    /// <summary>
    /// Finds the first FLAC frame sync word in the buffer.
    /// </summary>
    /// <returns>Offset to sync word, or -1 if not found.</returns>
    public static int FindSyncWord(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return -1;
        
        for (int i = 0; i <= data.Length - 2; i++)
        {
            if (data[i] == 0xFF && (data[i + 1] & 0xFE) == 0xF8)
                return i;
        }
        
        return -1;
    }
    
    /// <summary>
    /// Parses the STREAMINFO metadata block.
    /// </summary>
    public static bool TryParseStreamInfo(ReadOnlySpan<byte> data, out FlacStreamInfo info)
    {
        info = default;
        
        // STREAMINFO is 34 bytes
        if (data.Length < 34)
            return false;
        
        // Parse fields (all big-endian)
        ushort minBlockSize = (ushort)((data[0] << 8) | data[1]);
        ushort maxBlockSize = (ushort)((data[2] << 8) | data[3]);
        int minFrameSize = (data[4] << 16) | (data[5] << 8) | data[6];
        int maxFrameSize = (data[7] << 16) | (data[8] << 8) | data[9];
        
        // Sample rate: 20 bits
        int sampleRate = (data[10] << 12) | (data[11] << 4) | (data[12] >> 4);
        
        // Channels: 3 bits (stored as channels - 1)
        byte channels = (byte)(((data[12] >> 1) & 0x07) + 1);
        
        // Bits per sample: 5 bits (stored as bits - 1)
        byte bitsPerSample = (byte)((((data[12] & 0x01) << 4) | (data[13] >> 4)) + 1);
        
        // Total samples: 36 bits
        ulong totalSamples = ((ulong)(data[13] & 0x0F) << 32) |
                            ((ulong)data[14] << 24) |
                            ((ulong)data[15] << 16) |
                            ((ulong)data[16] << 8) |
                            data[17];
        
        // MD5 signature: 16 bytes
        byte[] md5 = data.Slice(18, 16).ToArray();
        
        info = new FlacStreamInfo(
            minBlockSize, maxBlockSize,
            minFrameSize, maxFrameSize,
            sampleRate, channels, bitsPerSample,
            totalSamples, md5);
        
        return true;
    }
    
    /// <summary>
    /// Attempts to parse a FLAC frame header.
    /// </summary>
    public static bool TryParseFrameHeader(ReadOnlySpan<byte> data, out FlacFrameHeader header)
    {
        header = default;
        
        if (data.Length < MinHeaderSize)
            return false;
        
        // Check sync code
        if (!HasSyncWord(data))
            return false;
        
        // Byte 1, bit 0: blocking strategy (0 = fixed, 1 = variable)
        bool variableBlockSize = (data[1] & 0x01) != 0;
        
        // Byte 2, upper 4 bits: block size encoding
        int blockSizeCode = data[2] >> 4;
        
        // Byte 2, lower 4 bits: sample rate encoding
        int sampleRateCode = data[2] & 0x0F;
        
        // Byte 3, upper 4 bits: channel assignment
        int channelAssignment = data[3] >> 4;
        
        // Byte 3, bits 1-3: sample size encoding
        int sampleSizeCode = (data[3] >> 1) & 0x07;
        
        // Byte 3, bit 0: reserved (must be 0)
        if ((data[3] & 0x01) != 0)
            return false;
        
        // Decode block size
        int blockSize = blockSizeCode switch
        {
            0 => 0, // Reserved
            1 => 192,
            2 => 576,
            3 => 1152,
            4 => 2304,
            5 => 4608,
            6 => 0, // 8-bit (block size - 1) follows in header
            7 => 0, // 16-bit (block size - 1) follows in header
            _ => 256 << (blockSizeCode - 8) // 8-15: 256, 512, 1024, etc.
        };
        
        // Decode sample rate
        int sampleRate = 0;
        if (sampleRateCode < 12)
            sampleRate = SampleRates[sampleRateCode];
        // else: 12-14 = get from header extension, 15 = invalid
        
        if (sampleRateCode == 15)
            return false;
        
        // Decode channels
        int channels;
        if (channelAssignment < 8)
            channels = channelAssignment + 1;
        else if (channelAssignment <= 10)
            channels = 2; // Stereo modes (left/side, right/side, mid/side)
        else
            return false; // Reserved
        
        // Decode bits per sample
        int bitsPerSample = BitsPerSampleTable[sampleSizeCode];
        if (bitsPerSample < 0)
            return false;
        
        // Parse variable-length frame/sample number (UTF-8 style)
        int headerPos = 4;
        ulong frameOrSampleNumber = ParseUtf8Number(data, ref headerPos);
        
        // Parse extended block size if needed
        if (blockSizeCode == 6)
        {
            if (headerPos >= data.Length)
                return false;
            blockSize = data[headerPos++] + 1;
        }
        else if (blockSizeCode == 7)
        {
            if (headerPos + 1 >= data.Length)
                return false;
            blockSize = ((data[headerPos] << 8) | data[headerPos + 1]) + 1;
            headerPos += 2;
        }
        
        // Parse extended sample rate if needed
        if (sampleRateCode == 12)
        {
            if (headerPos >= data.Length)
                return false;
            sampleRate = data[headerPos++] * 1000;
        }
        else if (sampleRateCode == 13)
        {
            if (headerPos + 1 >= data.Length)
                return false;
            sampleRate = (data[headerPos] << 8) | data[headerPos + 1];
            headerPos += 2;
        }
        else if (sampleRateCode == 14)
        {
            if (headerPos + 1 >= data.Length)
                return false;
            sampleRate = ((data[headerPos] << 8) | data[headerPos + 1]) * 10;
            headerPos += 2;
        }
        
        // CRC-8 is the last byte of the header
        if (headerPos >= data.Length)
            return false;
        
        byte crc8 = data[headerPos++];
        
        // Verify CRC-8
        byte computedCrc = ComputeCrc8(data[..headerPos]);
        // Note: CRC should be 0 when including the CRC byte itself
        
        header = new FlacFrameHeader(
            variableBlockSize, blockSize, sampleRate,
            (byte)channels, (byte)bitsPerSample, frameOrSampleNumber,
            headerPos, crc8);
        
        return blockSize > 0;
    }
    
    /// <summary>
    /// Sets the stream info for this packetizer (from STREAMINFO block).
    /// </summary>
    public void SetStreamInfo(FlacStreamInfo info)
    {
        _streamInfo = info;
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
    /// Attempts to read a complete FLAC frame from the buffer.
    /// </summary>
    public bool TryReadFrame(out FlacFrameHeader header, out ReadOnlySpan<byte> frameData)
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
        
        // Parse frame header
        if (!TryParseFrameHeader(buffer, out header))
        {
            // Invalid header, skip sync and try again
            Buffer.BlockCopy(_buffer, 2, _buffer, 0, _bufferLength - 2);
            _bufferLength -= 2;
            return false;
        }
        
        // Find next frame sync to determine frame size
        int frameSize = FindNextFrameSize(buffer, header.HeaderSize);
        
        if (frameSize < 0)
            return false; // Not enough data
        
        if (frameSize > MaxFrameSize)
        {
            // Invalid, skip sync
            Buffer.BlockCopy(_buffer, 2, _buffer, 0, _bufferLength - 2);
            _bufferLength -= 2;
            return false;
        }
        
        frameData = buffer[..frameSize];
        
        // Remove frame from buffer
        Buffer.BlockCopy(_buffer, frameSize, _buffer, 0, _bufferLength - frameSize);
        _bufferLength -= frameSize;
        
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
    
    private int FindNextFrameSize(ReadOnlySpan<byte> data, int startOffset)
    {
        // Look for the next sync word after the current header
        for (int i = startOffset; i <= data.Length - 2; i++)
        {
            if (data[i] == 0xFF && (data[i + 1] & 0xFE) == 0xF8)
            {
                // Verify it's a valid frame header
                if (TryParseFrameHeader(data[i..], out _))
                    return i;
            }
        }
        
        // If we have stream info, use max frame size as hint
        if (_streamInfo.IsValid && _streamInfo.MaxFrameSize > 0)
        {
            if (data.Length >= startOffset + _streamInfo.MaxFrameSize + 2)
                return _streamInfo.MaxFrameSize;
        }
        
        return -1; // Need more data
    }
    
    private static ulong ParseUtf8Number(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos >= data.Length)
            return 0;
        
        byte first = data[pos++];
        
        // Count leading 1 bits
        int count = 0;
        byte mask = 0x80;
        while ((first & mask) != 0)
        {
            count++;
            mask >>= 1;
        }
        
        if (count == 0)
            return first; // Single byte
        
        if (count == 1 || count > 6)
            return 0; // Invalid
        
        // Extract initial bits
        ulong value = (ulong)(first & ((1 << (7 - count)) - 1));
        
        // Read continuation bytes
        for (int i = 1; i < count; i++)
        {
            if (pos >= data.Length)
                return 0;
            
            byte b = data[pos++];
            if ((b & 0xC0) != 0x80)
                return 0; // Invalid continuation
            
            value = (value << 6) | (ulong)(b & 0x3F);
        }
        
        return value;
    }
    
    private static byte ComputeCrc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte b in data)
            crc = Crc8Table[crc ^ b];
        return crc;
    }
    
    private static byte[] BuildCrc8Table()
    {
        // FLAC CRC-8: polynomial x^8 + x^2 + x + 1 = 0x07
        const byte poly = 0x07;
        byte[] table = new byte[256];
        
        for (int i = 0; i < 256; i++)
        {
            byte crc = (byte)i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x80) != 0)
                    crc = (byte)((crc << 1) ^ poly);
                else
                    crc <<= 1;
            }
            table[i] = crc;
        }
        
        return table;
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

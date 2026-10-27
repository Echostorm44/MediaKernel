// MediaKernel - AES3/SMPTE 302M Audio Decoder
// Professional broadcast audio format used in MXF and MPEG-TS
// Reference: SMPTE 302M-2007

using System.Buffers.Binary;

namespace MediaKernel.Core.Codecs.Audio.Aes3;

/// <summary>
/// AES3/SMPTE 302M bit depth options.
/// </summary>
public enum Aes3BitDepth
{
    /// <summary>16-bit audio samples.</summary>
    Bits16 = 16,
    
    /// <summary>20-bit audio samples.</summary>
    Bits20 = 20,
    
    /// <summary>24-bit audio samples.</summary>
    Bits24 = 24
}

/// <summary>
/// Parsed AES3 frame header.
/// </summary>
public readonly struct Aes3FrameHeader
{
    /// <summary>Whether the header is valid.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Audio data size in bytes (excluding header).</summary>
    public int AudioDataSize { get; init; }
    
    /// <summary>Number of audio channels (2, 4, 6, or 8).</summary>
    public int ChannelCount { get; init; }
    
    /// <summary>Bits per sample (16, 20, or 24).</summary>
    public Aes3BitDepth BitDepth { get; init; }
    
    /// <summary>Channel assignment code.</summary>
    public int ChannelAssignment { get; init; }
    
    /// <summary>Number of sample pairs in this frame.</summary>
    public int SamplePairCount { get; init; }
    
    /// <summary>Total header size in bytes.</summary>
    public const int HeaderSize = 4;
}

/// <summary>
/// Decoded AES3 audio frame.
/// </summary>
public sealed class Aes3DecodedFrame
{
    /// <summary>PCM audio samples (interleaved, 16-bit or 32-bit depending on source).</summary>
    public byte[] Samples { get; init; } = [];
    
    /// <summary>Number of channels.</summary>
    public int ChannelCount { get; init; }
    
    /// <summary>Sample rate (always 48000 Hz for AES3/SMPTE 302M).</summary>
    public int SampleRate { get; init; } = 48000;
    
    /// <summary>Bits per sample in output (16 or 32).</summary>
    public int BitsPerSample { get; init; }
    
    /// <summary>Number of sample frames (samples per channel).</summary>
    public int SampleFrameCount { get; init; }
}

/// <summary>
/// Audio channel layout configurations.
/// </summary>
public enum Aes3ChannelLayout
{
    /// <summary>Unknown layout.</summary>
    Unknown,
    
    /// <summary>Stereo (L, R).</summary>
    Stereo,
    
    /// <summary>Quad (L, R, Ls, Rs).</summary>
    Quad,
    
    /// <summary>5.1 surround (L, R, C, LFE, Ls, Rs).</summary>
    FivePointOne,
    
    /// <summary>7.1 surround (L, R, C, LFE, Ls, Rs, Lss, Rss).</summary>
    SevenPointOne
}

/// <summary>
/// Decoder for AES3/SMPTE 302M professional broadcast audio.
/// AES3 is used in MXF containers and MPEG-TS for broadcast applications.
/// The format always uses 48 kHz sample rate with 2-8 channels at 16/20/24-bit depth.
/// </summary>
public sealed class Aes3Decoder
{
    /// <summary>AES3/SMPTE 302M always uses 48 kHz sample rate.</summary>
    public const int SampleRate = 48000;
    
    // Bit reversal lookup table for AES3 byte ordering
    private static readonly byte[] BitReversalTable = CreateBitReversalTable();
    
    private static byte[] CreateBitReversalTable()
    {
        byte[] table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int v = i;
            int r = 0;
            for (int j = 0; j < 8; j++)
            {
                r = (r << 1) | (v & 1);
                v >>= 1;
            }
            table[i] = (byte)r;
        }
        return table;
    }
    
    /// <summary>
    /// Parse an AES3 frame header.
    /// </summary>
    /// <param name="data">Buffer containing at least 4 bytes of header data.</param>
    /// <returns>Parsed header information.</returns>
    public static Aes3FrameHeader ParseHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < Aes3FrameHeader.HeaderSize)
            return new Aes3FrameHeader { IsValid = false };
        
        // AES3 header (4 bytes, big-endian):
        // - bits 31-16: audio data size
        // - bits 15-14: number of channel pairs (0=2, 1=4, 2=6, 3=8)
        // - bits 13-6:  channel assignment
        // - bits 5-4:   bits per sample (0=20, 1=24, 2=reserved, 3=16)
        // - bits 3-0:   alignment (reserved)
        
        uint header = BinaryPrimitives.ReadUInt32BigEndian(data);
        
        int audioDataSize = (int)((header >> 16) & 0xFFFF);
        int channelPairs = (int)((header >> 14) & 0x03);
        int channelAssignment = (int)((header >> 6) & 0xFF);
        int bitsCode = (int)((header >> 4) & 0x03);
        
        int channelCount = 2 + (channelPairs * 2);
        
        var bitDepth = bitsCode switch
        {
            0 => Aes3BitDepth.Bits20,
            1 => Aes3BitDepth.Bits24,
            3 => Aes3BitDepth.Bits16,
            _ => Aes3BitDepth.Bits16 // Reserved, treat as 16-bit
        };
        
        // Validate
        if ((int)bitDepth > 24)
            return new Aes3FrameHeader { IsValid = false };
        
        // Calculate sample pair count based on bit depth and data size
        int bytesPerSamplePair = bitDepth switch
        {
            Aes3BitDepth.Bits16 => 5,  // 2 * 20 bits packed
            Aes3BitDepth.Bits20 => 6,  // 2 * 24 bits packed (20 data + 4 aux)
            Aes3BitDepth.Bits24 => 7,  // 2 * 28 bits packed (24 data + 4 aux)
            _ => 5
        };
        
        int samplePairCount = audioDataSize / bytesPerSamplePair / (channelCount / 2);
        
        return new Aes3FrameHeader
        {
            IsValid = true,
            AudioDataSize = audioDataSize,
            ChannelCount = channelCount,
            BitDepth = bitDepth,
            ChannelAssignment = channelAssignment,
            SamplePairCount = samplePairCount
        };
    }
    
    /// <summary>
    /// Decode an AES3 frame to PCM audio.
    /// </summary>
    /// <param name="data">Complete AES3 frame including header.</param>
    /// <returns>Decoded PCM audio, or null if invalid.</returns>
    public Aes3DecodedFrame? Decode(ReadOnlySpan<byte> data)
    {
        var header = ParseHeader(data);
        if (!header.IsValid)
            return null;
        
        if (data.Length < Aes3FrameHeader.HeaderSize + header.AudioDataSize)
            return null;
        
        var audioData = data.Slice(Aes3FrameHeader.HeaderSize, header.AudioDataSize);
        
        return header.BitDepth switch
        {
            Aes3BitDepth.Bits16 => Decode16Bit(audioData, header),
            Aes3BitDepth.Bits20 => Decode20Bit(audioData, header),
            Aes3BitDepth.Bits24 => Decode24Bit(audioData, header),
            _ => null
        };
    }
    
    private static Aes3DecodedFrame Decode16Bit(ReadOnlySpan<byte> audioData, Aes3FrameHeader header)
    {
        // 16-bit: 5 bytes per sample pair
        // Each 5 bytes contains 2 x 20-bit values (16 data + 4 aux bits each)
        int bytesPerPair = 5;
        int channelPairs = header.ChannelCount / 2;
        int sampleFrames = audioData.Length / (bytesPerPair * channelPairs);
        
        // Output: 16-bit PCM, interleaved
        byte[] output = new byte[sampleFrames * header.ChannelCount * 2];
        int outOffset = 0;
        int inOffset = 0;
        
        for (int frame = 0; frame < sampleFrames; frame++)
        {
            for (int pair = 0; pair < channelPairs; pair++)
            {
                if (inOffset + 5 > audioData.Length)
                    break;
                
                // Unpack 2 samples from 5 bytes
                // AES3 uses bit-reversed byte ordering
                byte b0 = BitReversalTable[audioData[inOffset]];
                byte b1 = BitReversalTable[audioData[inOffset + 1]];
                byte b2 = BitReversalTable[audioData[inOffset + 2]];
                byte b3 = BitReversalTable[audioData[inOffset + 3]];
                byte b4 = BitReversalTable[audioData[inOffset + 4]];
                
                // Sample 1: bits 0-15 from bytes 0-1
                short sample1 = (short)(b0 | (b1 << 8));
                
                // Sample 2: bits 20-35 from bytes 2-4 (shift right 4)
                short sample2 = (short)((b2 >> 4) | (b3 << 4) | (b4 << 12));
                
                // Write to output (little-endian)
                output[outOffset++] = (byte)(sample1 & 0xFF);
                output[outOffset++] = (byte)((sample1 >> 8) & 0xFF);
                output[outOffset++] = (byte)(sample2 & 0xFF);
                output[outOffset++] = (byte)((sample2 >> 8) & 0xFF);
                
                inOffset += 5;
            }
        }
        
        return new Aes3DecodedFrame
        {
            Samples = output,
            ChannelCount = header.ChannelCount,
            BitsPerSample = 16,
            SampleFrameCount = sampleFrames
        };
    }
    
    private static Aes3DecodedFrame Decode20Bit(ReadOnlySpan<byte> audioData, Aes3FrameHeader header)
    {
        // 20-bit: 6 bytes per sample pair
        int bytesPerPair = 6;
        int channelPairs = header.ChannelCount / 2;
        int sampleFrames = audioData.Length / (bytesPerPair * channelPairs);
        
        // Output: 32-bit PCM (20-bit samples in upper bits)
        byte[] output = new byte[sampleFrames * header.ChannelCount * 4];
        int outOffset = 0;
        int inOffset = 0;
        
        for (int frame = 0; frame < sampleFrames; frame++)
        {
            for (int pair = 0; pair < channelPairs; pair++)
            {
                if (inOffset + 6 > audioData.Length)
                    break;
                
                byte b0 = BitReversalTable[audioData[inOffset]];
                byte b1 = BitReversalTable[audioData[inOffset + 1]];
                byte b2 = BitReversalTable[audioData[inOffset + 2]];
                byte b3 = BitReversalTable[audioData[inOffset + 3]];
                byte b4 = BitReversalTable[audioData[inOffset + 4]];
                byte b5 = BitReversalTable[audioData[inOffset + 5]];
                
                // Sample 1: 20 bits in upper part of 32-bit value
                int sample1 = (b0 << 12) | (b1 << 20) | (b2 << 28);
                
                // Sample 2: 20 bits
                int sample2 = (b3 << 12) | (b4 << 20) | (b5 << 28);
                
                // Write 32-bit samples (little-endian)
                output[outOffset++] = (byte)(sample1 & 0xFF);
                output[outOffset++] = (byte)((sample1 >> 8) & 0xFF);
                output[outOffset++] = (byte)((sample1 >> 16) & 0xFF);
                output[outOffset++] = (byte)((sample1 >> 24) & 0xFF);
                
                output[outOffset++] = (byte)(sample2 & 0xFF);
                output[outOffset++] = (byte)((sample2 >> 8) & 0xFF);
                output[outOffset++] = (byte)((sample2 >> 16) & 0xFF);
                output[outOffset++] = (byte)((sample2 >> 24) & 0xFF);
                
                inOffset += 6;
            }
        }
        
        return new Aes3DecodedFrame
        {
            Samples = output,
            ChannelCount = header.ChannelCount,
            BitsPerSample = 32,
            SampleFrameCount = sampleFrames
        };
    }
    
    private static Aes3DecodedFrame Decode24Bit(ReadOnlySpan<byte> audioData, Aes3FrameHeader header)
    {
        // 24-bit: 7 bytes per sample pair
        int bytesPerPair = 7;
        int channelPairs = header.ChannelCount / 2;
        int sampleFrames = audioData.Length / (bytesPerPair * channelPairs);
        
        // Output: 32-bit PCM (24-bit samples in upper bits)
        byte[] output = new byte[sampleFrames * header.ChannelCount * 4];
        int outOffset = 0;
        int inOffset = 0;
        
        for (int frame = 0; frame < sampleFrames; frame++)
        {
            for (int pair = 0; pair < channelPairs; pair++)
            {
                if (inOffset + 7 > audioData.Length)
                    break;
                
                byte b0 = BitReversalTable[audioData[inOffset]];
                byte b1 = BitReversalTable[audioData[inOffset + 1]];
                byte b2 = BitReversalTable[audioData[inOffset + 2]];
                byte b3 = BitReversalTable[audioData[inOffset + 3]];
                byte b4 = BitReversalTable[audioData[inOffset + 4]];
                byte b5 = BitReversalTable[audioData[inOffset + 5]];
                byte b6 = BitReversalTable[audioData[inOffset + 6]];
                
                // Sample 1: 24 bits in upper part of 32-bit value
                int sample1 = (b0 << 8) | (b1 << 16) | (b2 << 24);
                
                // Sample 2: 24 bits (shifted by 4 bits due to aux data)
                int sample2 = ((b3 << 4) | (b4 << 12) | (b5 << 20) | (b6 << 28)) & unchecked((int)0xFFFFFF00);
                
                // Write 32-bit samples (little-endian)
                output[outOffset++] = (byte)(sample1 & 0xFF);
                output[outOffset++] = (byte)((sample1 >> 8) & 0xFF);
                output[outOffset++] = (byte)((sample1 >> 16) & 0xFF);
                output[outOffset++] = (byte)((sample1 >> 24) & 0xFF);
                
                output[outOffset++] = (byte)(sample2 & 0xFF);
                output[outOffset++] = (byte)((sample2 >> 8) & 0xFF);
                output[outOffset++] = (byte)((sample2 >> 16) & 0xFF);
                output[outOffset++] = (byte)((sample2 >> 24) & 0xFF);
                
                inOffset += 7;
            }
        }
        
        return new Aes3DecodedFrame
        {
            Samples = output,
            ChannelCount = header.ChannelCount,
            BitsPerSample = 32,
            SampleFrameCount = sampleFrames
        };
    }
    
    /// <summary>
    /// Get the channel configuration for a given channel count.
    /// </summary>
    public static Aes3ChannelLayout GetChannelLayout(int channelCount)
    {
        return channelCount switch
        {
            2 => Aes3ChannelLayout.Stereo,
            4 => Aes3ChannelLayout.Quad,
            6 => Aes3ChannelLayout.FivePointOne,
            8 => Aes3ChannelLayout.SevenPointOne,
            _ => Aes3ChannelLayout.Unknown
        };
    }
    
    /// <summary>
    /// Probe buffer for AES3/SMPTE 302M content.
    /// </summary>
    public static bool Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < Aes3FrameHeader.HeaderSize)
            return false;
        
        var header = ParseHeader(data);
        if (!header.IsValid)
            return false;
        
        // Validate that the data size makes sense
        if (header.AudioDataSize < 5 || header.AudioDataSize > 65535)
            return false;
        
        // Validate channel count
        if (header.ChannelCount < 2 || header.ChannelCount > 8 || header.ChannelCount % 2 != 0)
            return false;
        
        return true;
    }
}

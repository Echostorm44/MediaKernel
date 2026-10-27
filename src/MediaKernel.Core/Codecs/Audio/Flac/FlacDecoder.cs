// FLAC (Free Lossless Audio Codec) decoder implementation
// Reference: RFC 9639, Xiph.org FLAC format specification

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Codecs.Audio.Flac;

/// <summary>
/// FLAC subframe types.
/// </summary>
public enum FlacSubframeType : byte
{
    Constant = 0,
    Verbatim = 1,
    Fixed = 2,    // Fixed predictor, order 0-4
    Lpc = 3       // Linear Predictive Coding
}

/// <summary>
/// FLAC channel assignment types.
/// </summary>
public enum FlacChannelAssignment : byte
{
    Independent = 0,    // Channels are independent
    LeftSide = 1,       // Left + Side (Left - Right)
    RightSide = 2,      // Side + Right
    MidSide = 3         // Mid + Side
}

/// <summary>
/// FLAC metadata block types.
/// </summary>
public enum FlacMetadataType : byte
{
    StreamInfo = 0,
    Padding = 1,
    Application = 2,
    SeekTable = 3,
    VorbisComment = 4,
    CueSheet = 5,
    Picture = 6,
    Reserved127 = 127   // Invalid, used to mark last block
}

/// <summary>
/// FLAC stream info from STREAMINFO metadata block.
/// </summary>
public readonly struct FlacStreamInfo
{
    public readonly ushort MinBlockSize;      // 16-65535 samples
    public readonly ushort MaxBlockSize;      // 16-65535 samples
    public readonly uint MinFrameSize;        // In bytes, 0 = unknown
    public readonly uint MaxFrameSize;        // In bytes, 0 = unknown
    public readonly uint SampleRate;          // 1-655350 Hz
    public readonly byte ChannelCount;        // 1-8
    public readonly byte BitsPerSample;       // 4-32
    public readonly ulong TotalSamples;       // 0 = unknown
    public readonly byte[] Md5Signature;      // 128-bit MD5 of unencoded audio

    public FlacStreamInfo(ushort minBlockSize, ushort maxBlockSize, uint minFrameSize, 
        uint maxFrameSize, uint sampleRate, byte channelCount, byte bitsPerSample,
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
    }
}

/// <summary>
/// FLAC frame header information.
/// </summary>
public readonly struct FlacFrameHeader
{
    public readonly bool VariableBlockSize;   // Fixed vs variable blocksize
    public readonly uint BlockSize;           // Samples in this frame
    public readonly uint SampleRate;          // Hz
    public readonly byte ChannelCount;        // 1-8
    public readonly FlacChannelAssignment ChannelAssignment;
    public readonly byte BitsPerSample;       // 4-32
    public readonly ulong FrameOrSampleNumber;
    public readonly byte Crc8;                // CRC-8 of header

    public FlacFrameHeader(bool variableBlockSize, uint blockSize, uint sampleRate,
        byte channelCount, FlacChannelAssignment channelAssignment, byte bitsPerSample,
        ulong frameOrSampleNumber, byte crc8)
    {
        VariableBlockSize = variableBlockSize;
        BlockSize = blockSize;
        SampleRate = sampleRate;
        ChannelCount = channelCount;
        ChannelAssignment = channelAssignment;
        BitsPerSample = bitsPerSample;
        FrameOrSampleNumber = frameOrSampleNumber;
        Crc8 = crc8;
    }
}

/// <summary>
/// Full FLAC decoder supporting all subframe types.
/// </summary>
public sealed class FlacDecoder
{
    // CRC-8 polynomial: x^8 + x^2 + x^1 + x^0
    private static readonly byte[] Crc8Table = GenerateCrc8Table();
    
    // CRC-16 polynomial: x^16 + x^15 + x^2 + x^0 (CRC-16-IBM)
    private static readonly ushort[] Crc16Table = GenerateCrc16Table();

    // Fixed predictor coefficients for orders 0-4
    private static readonly int[][] FixedCoefficients =
    [
        [],                         // Order 0: residual only
        [1],                        // Order 1: s(n) = s(n-1)
        [2, -1],                    // Order 2: s(n) = 2*s(n-1) - s(n-2)
        [3, -3, 1],                 // Order 3: s(n) = 3*s(n-1) - 3*s(n-2) + s(n-3)
        [4, -6, 4, -1]              // Order 4: s(n) = 4*s(n-1) - 6*s(n-2) + 4*s(n-3) - s(n-4)
    ];

    // Sample rate lookup table (4-bit code to Hz)
    private static readonly uint[] SampleRateLookup =
    [
        0,      // From STREAMINFO
        88200,
        176400,
        192000,
        8000,
        16000,
        22050,
        24000,
        32000,
        44100,
        48000,
        96000,
        0,      // Get 8-bit from end of header in kHz
        0,      // Get 16-bit from end of header in Hz
        0,      // Get 16-bit from end of header in 10s of Hz
        0       // Invalid
    ];

    // Block size lookup table (4-bit code to samples)
    private static readonly uint[] BlockSizeLookup =
    [
        0,      // Reserved
        192,
        576,
        1152,
        2304,
        4608,
        0,      // Get 8-bit from end of header
        0,      // Get 16-bit from end of header
        256,
        512,
        1024,
        2048,
        4096,
        8192,
        16384,
        32768
    ];

    // Bits per sample lookup (3-bit code)
    private static readonly byte[] BitsPerSampleLookup = [0, 8, 12, 0, 16, 20, 24, 32];

    private FlacStreamInfo? streamInfo;
    private readonly int[][] sampleBuffers;
    private readonly int[] lpcResidual;
    private int currentBlockSize;

    /// <summary>
    /// Stream info from metadata, or null if not yet parsed.
    /// </summary>
    public FlacStreamInfo? StreamInfo => streamInfo;

    /// <summary>
    /// Creates a new FLAC decoder.
    /// </summary>
    public FlacDecoder()
    {
        // Allocate for max block size (65535 samples, 8 channels)
        sampleBuffers = new int[8][];
        for (int i = 0; i < 8; i++)
            sampleBuffers[i] = new int[65536];
        
        lpcResidual = new int[65536];
    }

    /// <summary>
    /// Parses the FLAC stream header ("fLaC" magic + STREAMINFO).
    /// </summary>
    public bool ParseStreamHeader(ReadOnlySpan<byte> data, out int bytesConsumed)
    {
        bytesConsumed = 0;
        
        if (data.Length < 42) // 4 magic + 4 block header + 34 streaminfo
            return false;

        // Check magic
        if (data[0] != 'f' || data[1] != 'L' || data[2] != 'a' || data[3] != 'C')
            return false;

        bytesConsumed = 4;
        
        // Parse metadata blocks until we find STREAMINFO
        return ParseMetadataBlocks(data[4..], ref bytesConsumed);
    }

    /// <summary>
    /// Parses metadata blocks.
    /// </summary>
    private bool ParseMetadataBlocks(ReadOnlySpan<byte> data, ref int bytesConsumed)
    {
        int offset = 0;
        
        while (offset + 4 <= data.Length)
        {
            byte blockHeader = data[offset];
            bool isLast = (blockHeader & 0x80) != 0;
            var blockType = (FlacMetadataType)(blockHeader & 0x7F);
            
            int blockLength = (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            offset += 4;
            
            if (offset + blockLength > data.Length)
                return false;

            if (blockType == FlacMetadataType.StreamInfo)
            {
                if (!ParseStreamInfo(data.Slice(offset, blockLength)))
                    return false;
            }
            
            offset += blockLength;
            bytesConsumed += 4 + blockLength;
            
            if (isLast)
                break;
        }
        
        return streamInfo.HasValue;
    }

    /// <summary>
    /// Parses STREAMINFO metadata block.
    /// </summary>
    private bool ParseStreamInfo(ReadOnlySpan<byte> data)
    {
        if (data.Length < 34)
            return false;

        ushort minBlockSize = (ushort)((data[0] << 8) | data[1]);
        ushort maxBlockSize = (ushort)((data[2] << 8) | data[3]);
        uint minFrameSize = (uint)((data[4] << 16) | (data[5] << 8) | data[6]);
        uint maxFrameSize = (uint)((data[7] << 16) | (data[8] << 8) | data[9]);

        // Sample rate (20 bits) + channels (3 bits) + bits per sample (5 bits) + total samples (36 bits)
        // = 64 bits = 8 bytes at offset 10
        uint sampleRate = (uint)((data[10] << 12) | (data[11] << 4) | (data[12] >> 4));
        byte channelCount = (byte)(((data[12] >> 1) & 0x07) + 1);
        byte bitsPerSample = (byte)((((data[12] & 0x01) << 4) | (data[13] >> 4)) + 1);
        
        ulong totalSamples = ((ulong)(data[13] & 0x0F) << 32) |
                             ((ulong)data[14] << 24) |
                             ((ulong)data[15] << 16) |
                             ((ulong)data[16] << 8) |
                             data[17];

        byte[] md5 = data.Slice(18, 16).ToArray();

        // Everything in this decoder - the residuals, the LPC accumulator, the side channel -
        // is int, which is what FFmpeg's ordinary paths use too. Above 24 bits it needs the
        // wide and 33bps variants (decode_subframe_fixed_wide, decode_subframe_lpc_33bps,
        // decorrelate_33bps), which are not ported: a stereo side channel at 32 bits per sample
        // needs 33 bits and would overflow silently, so the output would be noise rather than an
        // error. There is no stream to gate it with either - ffmpeg's own FLAC encoder caps at
        // 24 bits, reporting "s32 (24 bit)" - so this fails loudly instead of guessing.
        if (bitsPerSample > 24)
        {
            throw new NotSupportedException(
                $"FLAC: {bitsPerSample} bits per sample needs the 33-bit subframe paths, which "
              + "are not implemented. Up to 24 bits is supported.");
        }

        streamInfo = new FlacStreamInfo(minBlockSize, maxBlockSize, minFrameSize, maxFrameSize,
            sampleRate, channelCount, bitsPerSample, totalSamples, md5);

        return true;
    }

    /// <summary>
    /// Decodes a FLAC frame and returns interleaved PCM samples.
    /// </summary>
    public bool DecodeFrame(ReadOnlySpan<byte> data, Span<int> output, out int samplesDecoded, out int bytesConsumed)
    {
        samplesDecoded = 0;
        bytesConsumed = 0;

        if (!ParseFrameHeader(data, out var frameHeader, out int headerSize))
            return false;

        // Create bitstream reader starting after header
        var reader = new FlacBitReader(data[headerSize..]);
        currentBlockSize = (int)frameHeader.BlockSize;

        // Decode each subframe
        int subframeBits = frameHeader.BitsPerSample;
        
        for (int ch = 0; ch < frameHeader.ChannelCount; ch++)
        {
            // Adjust bits for side channel in stereo decorrelation
            int channelBits = subframeBits;
            if (frameHeader.ChannelAssignment == FlacChannelAssignment.LeftSide && ch == 1)
                channelBits++; // Side needs extra bit
            else if (frameHeader.ChannelAssignment == FlacChannelAssignment.RightSide && ch == 0)
                channelBits++; // Side needs extra bit
            else if (frameHeader.ChannelAssignment == FlacChannelAssignment.MidSide && ch == 1)
                channelBits++; // Side needs extra bit

            if (!DecodeSubframe(ref reader, sampleBuffers[ch], currentBlockSize, channelBits))
                return false;
        }

        // Apply channel decorrelation
        ApplyChannelDecorrelation(frameHeader.ChannelAssignment, frameHeader.ChannelCount, currentBlockSize);

        // Align to byte boundary
        reader.AlignToByte();

        // Skip CRC-16 footer
        reader.SkipBits(16);

        // Interleave output
        int outputIndex = 0;
        for (int s = 0; s < currentBlockSize; s++)
        {
            for (int ch = 0; ch < frameHeader.ChannelCount; ch++)
            {
                output[outputIndex++] = sampleBuffers[ch][s];
            }
        }

        samplesDecoded = currentBlockSize;
        bytesConsumed = headerSize + (reader.BitsRead + 7) / 8;
        
        return true;
    }

    /// <summary>
    /// Parses a FLAC frame header.
    /// </summary>
    /// <summary>
    /// <c>ff_flac_decode_frame_header</c>: the frame header, validated.
    /// </summary>
    /// <remarks>
    /// Every rejection here is one the C makes, and they are what make a header *detectable*
    /// rather than merely parseable. This used to compute the CRC-8 and discard the result
    /// ("CRC mismatch - but we'll continue for now"), and to accept the three reserved codes by
    /// substituting a value from STREAMINFO. On a valid stream none of that shows, which is why
    /// nine bit-exact conformance streams never caught it; but a frame header is found by trying
    /// every 0xFF 0xF8 byte pair in the stream and asking whether it parses, and FLAC frame data
    /// is full of such pairs. Without the CRC and the reserved-code checks, almost every one of
    /// them answers yes.
    /// <para>
    /// A sample rate, block size or sample size of zero is a different thing from a reserved
    /// code: the C's tables define 0 as "take it from STREAMINFO", so those stay substitutions.
    /// </para>
    /// </remarks>
    public bool ParseFrameHeader(ReadOnlySpan<byte> data, out FlacFrameHeader header, out int headerSize)
        => ParseFrameHeader(data, streamInfo, out header, out headerSize);

    /// <summary>
    /// The same, for a caller that holds the STREAMINFO but not a decoder.
    /// </summary>
    /// <remarks>
    /// <see cref="FlacParser"/> needs this and nothing else from the decoder, and a
    /// <see cref="FlacDecoder"/> carries 2.4 MB of sample buffers it would never use. In the C the
    /// split is the same: <c>ff_flac_decode_frame_header</c> is a free function in flac.c, and the
    /// "zero means take it from STREAMINFO" substitutions belong to the decoder in flacdec.c, so
    /// they are applied here only when a STREAMINFO is supplied.
    /// </remarks>
    public static bool ParseFrameHeader(ReadOnlySpan<byte> data, FlacStreamInfo? streamInfo,
                                       out FlacFrameHeader header, out int headerSize)
    {
        header = default;
        headerSize = 0;

        if (data.Length < 5)
            return false;

        // Sync code: the top fifteen bits are 0x7FFC.
        if (data[0] != 0xFF || (data[1] & 0xFC) != 0xF8)
            return false;

        bool variableBlockSize = (data[1] & 0x01) != 0;

        int blockSizeCode = data[2] >> 4;
        int sampleRateCode = data[2] & 0x0F;
        int channelCode = data[3] >> 4;
        int bitsPerSampleCode = (data[3] >> 1) & 0x07;

        // Reserved codes. The C logs and returns AVERROR_INVALIDDATA for each of these.
        if (blockSizeCode == 0)        // reserved blocksize code
            return false;
        if (sampleRateCode == 0x0F)    // illegal sample rate code
            return false;
        if (bitsPerSampleCode == 3)    // invalid sample size code
            return false;
        if ((data[3] & 0x01) != 0)     // broken stream, invalid padding
            return false;

        int offset = 4;

        // Frame or sample number, GET_UTF8. The C's macro validates as it goes: a leading byte
        // that is itself a continuation byte or >= 0xFE is an error, and every following byte
        // must be 10xxxxxx.
        if (!TryReadUtf8Number(data, ref offset, out ulong frameOrSampleNumber))
            return false;

        uint blockSize = BlockSizeLookup[blockSizeCode];
        if (blockSizeCode == 6)
        {
            if (offset + 1 > data.Length) return false;
            blockSize = (uint)(data[offset++] + 1);
        }
        else if (blockSizeCode == 7)
        {
            if (offset + 2 > data.Length) return false;
            blockSize = (uint)((data[offset] << 8) | data[offset + 1]) + 1;
            offset += 2;
        }

        uint sampleRate = SampleRateLookup[sampleRateCode];
        if (sampleRateCode == 12)
        {
            if (offset + 1 > data.Length) return false;
            sampleRate = (uint)(data[offset++] * 1000);
        }
        else if (sampleRateCode == 13)
        {
            if (offset + 2 > data.Length) return false;
            sampleRate = (uint)((data[offset] << 8) | data[offset + 1]);
            offset += 2;
        }
        else if (sampleRateCode == 14)
        {
            if (offset + 2 > data.Length) return false;
            sampleRate = (uint)(((data[offset] << 8) | data[offset + 1]) * 10);
            offset += 2;
        }
        else if (sampleRate == 0 && streamInfo.HasValue)
        {
            sampleRate = streamInfo.Value.SampleRate;
        }

        if (blockSize == 0 && streamInfo.HasValue)
            blockSize = streamInfo.Value.MaxBlockSize;

        byte channelCount;
        FlacChannelAssignment channelAssignment;

        if (channelCode < 8)
        {
            channelCount = (byte)(channelCode + 1);
            channelAssignment = FlacChannelAssignment.Independent;
        }
        else if (channelCode == 8)
        {
            channelCount = 2;
            channelAssignment = FlacChannelAssignment.LeftSide;
        }
        else if (channelCode == 9)
        {
            channelCount = 2;
            channelAssignment = FlacChannelAssignment.RightSide;
        }
        else if (channelCode == 10)
        {
            channelCount = 2;
            channelAssignment = FlacChannelAssignment.MidSide;
        }
        else
        {
            return false; // invalid channel mode
        }

        byte bitsPerSample = BitsPerSampleLookup[bitsPerSampleCode];
        if (bitsPerSample == 0 && streamInfo.HasValue)
            bitsPerSample = streamInfo.Value.BitsPerSample;

        if (offset + 1 > data.Length)
            return false;
        byte crc8 = data[offset++];

        // The CRC-8 runs over the whole header including its own byte, so a good header gives 0.
        if (CalculateCrc8(data[..offset]) != 0)
            return false;

        header = new FlacFrameHeader(variableBlockSize, blockSize, sampleRate,
            channelCount, channelAssignment, bitsPerSample, frameOrSampleNumber, crc8);
        headerSize = offset;

        return true;
    }

    /// <summary>
    /// Decodes a single subframe.
    /// </summary>
    private bool DecodeSubframe(ref FlacBitReader reader, int[] output, int blockSize, int bitsPerSample)
    {
        // Zero-padding bit (must be 0)
        if (reader.ReadBits(1) != 0)
            return false;

        // Subframe type (6 bits)
        int typeCode = (int)reader.ReadBits(6);

        // Wasted bits per sample flag
        bool hasWastedBits = reader.ReadBits(1) == 1;
        int wastedBits = 0;
        if (hasWastedBits)
        {
            // Read unary-coded wasted bits count
            while (reader.ReadBits(1) == 0)
                wastedBits++;
            wastedBits++;
        }

        int effectiveBits = bitsPerSample - wastedBits;

        // Determine subframe type and decode
        if (typeCode == 0)
        {
            // Constant subframe
            int value = reader.ReadSignedBits(effectiveBits);
            for (int i = 0; i < blockSize; i++)
                output[i] = value << wastedBits;
        }
        else if (typeCode == 1)
        {
            // Verbatim subframe
            for (int i = 0; i < blockSize; i++)
                output[i] = reader.ReadSignedBits(effectiveBits) << wastedBits;
        }
        else if (typeCode >= 8 && typeCode <= 12)
        {
            // Fixed predictor (order 0-4)
            int order = typeCode - 8;
            if (!DecodeFixedSubframe(ref reader, output, blockSize, effectiveBits, order))
                return false;
            
            if (wastedBits > 0)
            {
                for (int i = 0; i < blockSize; i++)
                    output[i] <<= wastedBits;
            }
        }
        else if (typeCode >= 32 && typeCode <= 63)
        {
            // LPC predictor (order 1-32)
            int order = typeCode - 31;
            if (!DecodeLpcSubframe(ref reader, output, blockSize, effectiveBits, order))
                return false;
            
            if (wastedBits > 0)
            {
                for (int i = 0; i < blockSize; i++)
                    output[i] <<= wastedBits;
            }
        }
        else
        {
            return false; // Reserved or invalid
        }

        return true;
    }

    /// <summary>
    /// Decodes a fixed predictor subframe.
    /// </summary>
    private bool DecodeFixedSubframe(ref FlacBitReader reader, int[] output, int blockSize, int bitsPerSample, int order)
    {
        // Read warmup samples
        for (int i = 0; i < order; i++)
            output[i] = reader.ReadSignedBits(bitsPerSample);

        // Decode residual
        if (!DecodeResidual(ref reader, lpcResidual, blockSize, order))
            return false;

        // Apply fixed prediction
        var coefficients = FixedCoefficients[order];
        
        for (int i = order; i < blockSize; i++)
        {
            long prediction = 0;
            for (int j = 0; j < order; j++)
                prediction += (long)coefficients[j] * output[i - 1 - j];
            
            output[i] = (int)prediction + lpcResidual[i - order];
        }

        return true;
    }

    /// <summary>
    /// Decodes an LPC predictor subframe.
    /// </summary>
    private bool DecodeLpcSubframe(ref FlacBitReader reader, int[] output, int blockSize, int bitsPerSample, int order)
    {
        // Read warmup samples
        for (int i = 0; i < order; i++)
            output[i] = reader.ReadSignedBits(bitsPerSample);

        // Read LPC parameters
        int precision = (int)reader.ReadBits(4) + 1;
        if (precision == 16) // All 1s = invalid
            return false;

        int shift = reader.ReadSignedBits(5);

        // Read quantized LPC coefficients
        Span<int> lpcCoefficients = stackalloc int[order];
        for (int i = 0; i < order; i++)
            lpcCoefficients[i] = reader.ReadSignedBits(precision);

        // Decode residual
        if (!DecodeResidual(ref reader, lpcResidual, blockSize, order))
            return false;

        // Apply LPC prediction
        for (int i = order; i < blockSize; i++)
        {
            long prediction = 0;
            for (int j = 0; j < order; j++)
                prediction += (long)lpcCoefficients[j] * output[i - 1 - j];
            
            output[i] = (int)(prediction >> shift) + lpcResidual[i - order];
        }

        return true;
    }

    /// <summary>
    /// Decodes residual using Rice coding.
    /// Matches FFmpeg's decode_residuals() in flacdec.c.
    /// </summary>
    private bool DecodeResidual(ref FlacBitReader reader, int[] residual, int blockSize, int predictorOrder)
    {
        int method = (int)reader.ReadBits(2);
        if (method > 1)
            return false;

        int riceParameterBits = (method == 0) ? 4 : 5;
        int escapeCode = (1 << riceParameterBits) - 1;

        int partitionOrder = (int)reader.ReadBits(4);
        int partitionCount = 1 << partitionOrder;
        int samplesPerPartition = blockSize >> partitionOrder;

        // Validate: blockSize must be divisible by partitionCount
        if (samplesPerPartition << partitionOrder != blockSize)
            return false;
        if (predictorOrder > samplesPerPartition)
            return false;

        int residualIndex = 0;
        // First partition: skip warmup samples (already decoded as part of subframe)
        int sampleIndex = predictorOrder;

        for (int partition = 0; partition < partitionCount; partition++)
        {
            int riceParameter = (int)reader.ReadBits(riceParameterBits);
            int samplesToRead = samplesPerPartition - sampleIndex;

            if (riceParameter == escapeCode)
            {
                int rawBits = (int)reader.ReadBits(5);
                for (int i = 0; i < samplesToRead; i++)
                {
                    if (!reader.HasData) return false;
                    residual[residualIndex++] = (rawBits > 0) ? reader.ReadSignedBits(rawBits) : 0;
                }
            }
            else
            {
                for (int i = 0; i < samplesToRead; i++)
                {
                    if (!reader.HasData) return false;

                    // Read unary quotient
                    int quotient = 0;
                    while (reader.HasData && reader.ReadBits(1) == 0)
                        quotient++;

                    // Read binary remainder
                    int remainder = (riceParameter > 0) ? (int)reader.ReadBits(riceParameter) : 0;

                    // Reconstruct and convert from unsigned zig-zag to signed
                    int unsignedValue = (quotient << riceParameter) | remainder;
                    residual[residualIndex++] = (unsignedValue >> 1) ^ (-(unsignedValue & 1));
                }
            }

            sampleIndex = 0; // Subsequent partitions start at 0
        }

        return true;
    }

    /// <summary>
    /// Applies channel decorrelation for stereo modes.
    /// </summary>
    private void ApplyChannelDecorrelation(FlacChannelAssignment assignment, int channelCount, int blockSize)
    {
        if (channelCount != 2 || assignment == FlacChannelAssignment.Independent)
            return;

        int[] left = sampleBuffers[0];
        int[] right = sampleBuffers[1];

        switch (assignment)
        {
            case FlacChannelAssignment.LeftSide:
                // Right = Left - Side
                for (int i = 0; i < blockSize; i++)
                    right[i] = left[i] - right[i];
                break;

            case FlacChannelAssignment.RightSide:
                // Left = Side + Right
                for (int i = 0; i < blockSize; i++)
                    left[i] = left[i] + right[i];
                break;

            case FlacChannelAssignment.MidSide:
                // FFmpeg: a = (unsigned)mid; b = side; a -= b>>1; left = a+b; right = a;
                for (int i = 0; i < blockSize; i++)
                {
                    uint a = (uint)left[i];
                    int b = right[i];
                    a -= (uint)(b >> 1);
                    left[i] = (int)(a + (uint)b);
                    right[i] = (int)a;
                }
                break;
        }
    }

    /// <summary>
    /// Reads a UTF-8 style variable-length integer.
    /// </summary>
    /// <summary>
    /// <c>GET_UTF8</c>: the frame or sample number, in FLAC's extended UTF-8 coding.
    /// </summary>
    /// <remarks>
    /// This is the C macro's own algorithm rather than a chain of mask tests, because the macro
    /// validates as it walks and the chain did not: the previous version fell back to "treat as
    /// a single byte" for a malformed leading byte and masked every continuation byte with 0x3F
    /// without checking that it was a continuation byte at all. Both turn an invalid header into
    /// a plausible one, which is exactly what a frame-header search must not accept. <c>top</c>
    /// starts at bit 6 and walks up, so the loop runs once per leading one-bit.
    /// </remarks>
    private static bool TryReadUtf8Number(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        if (offset >= data.Length)
            return false;

        ulong val = data[offset++];
        uint top = ((uint)val & 128) >> 1;

        // A continuation byte cannot lead, and 0xFE/0xFF are not valid leading bytes.
        if ((val & 0xC0) == 0x80 || val >= 0xFE)
            return false;

        while ((val & top) != 0)
        {
            if (offset >= data.Length)
                return false;
            uint tmp = (uint)(data[offset++] - 128);
            if ((tmp >> 6) != 0)
                return false;
            val = (val << 6) + tmp;
            top <<= 5;
        }

        value = val & (((ulong)top << 1) - 1);
        return true;
    }

    /// <summary>
    /// Calculates CRC-8 for frame header verification.
    /// </summary>
    public static byte CalculateCrc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte b in data)
            crc = Crc8Table[crc ^ b];
        return crc;
    }

    /// <summary>
    /// Calculates CRC-16 for frame verification.
    /// </summary>
    public static ushort CalculateCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
            crc = (ushort)((crc << 8) ^ Crc16Table[(crc >> 8) ^ b]);
        return crc;
    }

    /// <summary>
    /// Generates CRC-8 lookup table.
    /// </summary>
    private static byte[] GenerateCrc8Table()
    {
        const byte polynomial = 0x07; // CRC-8 polynomial
        byte[] table = new byte[256];
        
        for (int i = 0; i < 256; i++)
        {
            byte crc = (byte)i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x80) != 0)
                    crc = (byte)((crc << 1) ^ polynomial);
                else
                    crc <<= 1;
            }
            table[i] = crc;
        }
        
        return table;
    }

    /// <summary>
    /// Generates CRC-16 lookup table.
    /// </summary>
    private static ushort[] GenerateCrc16Table()
    {
        const ushort polynomial = 0x8005; // CRC-16-IBM polynomial
        ushort[] table = new ushort[256];
        
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)(i << 8);
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ polynomial);
                else
                    crc <<= 1;
            }
            table[i] = crc;
        }
        
        return table;
    }

    /// <summary>
    /// Finds the next FLAC frame sync in the data.
    /// </summary>
    public static int FindFrameSync(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length - 1; i++)
        {
            if (data[i] == 0xFF && (data[i + 1] & 0xFC) == 0xF8)
                return i;
        }
        return -1;
    }
}

/// <summary>
/// Bitstream reader for FLAC decoding.
/// </summary>
internal ref struct FlacBitReader
{
    private readonly ReadOnlySpan<byte> data;
    private int bytePosition;
    private int bitPosition;

    public int BitsRead => bytePosition * 8 + bitPosition;

    public bool HasData => bytePosition < data.Length;

    public FlacBitReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        bytePosition = 0;
        bitPosition = 0;
    }

    /// <summary>
    /// Reads up to 32 bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        if (count == 0)
            return 0;

        uint result = 0;
        int remaining = count;

        while (remaining > 0)
        {
            if (bytePosition >= data.Length)
                return result;

            int bitsInCurrentByte = 8 - bitPosition;
            int bitsToRead = Math.Min(bitsInCurrentByte, remaining);
            
            int shift = bitsInCurrentByte - bitsToRead;
            uint mask = (uint)((1 << bitsToRead) - 1);
            uint bits = (uint)((data[bytePosition] >> shift) & mask);
            
            result = (result << bitsToRead) | bits;
            remaining -= bitsToRead;
            
            bitPosition += bitsToRead;
            if (bitPosition >= 8)
            {
                bitPosition = 0;
                bytePosition++;
            }
        }

        return result;
    }

    /// <summary>
    /// Reads signed bits using two's complement.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSignedBits(int count)
    {
        uint value = ReadBits(count);
        // Sign extension
        if ((value & (1u << (count - 1))) != 0)
            value |= uint.MaxValue << count;
        return (int)value;
    }

    /// <summary>
    /// Skips the specified number of bits.
    /// </summary>
    public void SkipBits(int count)
    {
        int totalBits = bitPosition + count;
        bytePosition += totalBits / 8;
        bitPosition = totalBits % 8;
    }

    /// <summary>
    /// Aligns to the next byte boundary.
    /// </summary>
    public void AlignToByte()
    {
        if (bitPosition != 0)
        {
            bitPosition = 0;
            bytePosition++;
        }
    }
}

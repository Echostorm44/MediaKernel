using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Codecs.H264
{
    /// <summary>
    /// Represents a NAL unit with its type and payload data.
    /// </summary>
    public readonly struct NalUnit
    {
        /// <summary>The NAL unit type (lower 5 bits of first byte).</summary>
        public readonly NalUnitType Type;

        /// <summary>NAL reference IDC (bits 5-6 of first byte). Higher values indicate more important frames.</summary>
        public readonly byte ReferenceIdc;

        /// <summary>The NAL unit payload excluding the type byte but before RBSP conversion.</summary>
        public readonly ReadOnlyMemory<byte> Payload;

        /// <summary>The complete NAL unit data including the type byte.</summary>
        public readonly ReadOnlyMemory<byte> RawData;

        /// <summary>Position of this NAL unit in the source stream.</summary>
        public readonly long StreamPosition;

        public NalUnit(NalUnitType type, byte referenceIdc, ReadOnlyMemory<byte> payload, 
                       ReadOnlyMemory<byte> rawData, long streamPosition)
        {
            Type = type;
            ReferenceIdc = referenceIdc;
            Payload = payload;
            RawData = rawData;
            StreamPosition = streamPosition;
        }

        /// <summary>Returns true if this is a key frame (IDR slice).</summary>
        public bool IsKeyFrame => Type == NalUnitType.CodedSliceIdr;

        /// <summary>Returns true if this is a video coding layer NAL unit.</summary>
        public bool IsVclNalUnit => Type >= NalUnitType.CodedSliceNonIdr && 
                                    Type <= NalUnitType.CodedSliceIdr;
    }

    /// <summary>
    /// High-performance parser for H.264 NAL units in AnnexB format.
    /// Uses SIMD intrinsics for start code detection.
    /// </summary>
    public static class NalUnitParser
    {
        private const int StartCodeLength3 = 3;  // 0x00 0x00 0x01
        private const int StartCodeLength4 = 4;  // 0x00 0x00 0x00 0x01

        /// <summary>
        /// Parses all NAL units from an AnnexB formatted byte stream.
        /// </summary>
        /// <param name="data">The input data containing NAL units with start code prefixes.</param>
        /// <param name="streamOffset">Base stream offset for position calculation.</param>
        /// <returns>Array of parsed NAL units.</returns>
        public static NalUnit[] ParseAnnexB(ReadOnlySpan<byte> data, long streamOffset = 0)
        {
            if (data.Length < 4)
                return Array.Empty<NalUnit>();

            // First pass: count NAL units for efficient allocation
            int nalCount = CountStartCodes(data);
            if (nalCount == 0)
                return Array.Empty<NalUnit>();

            var nalUnits = new NalUnit[nalCount];
            int nalIndex = 0;

            int position = 0;
            int nalStart = -1;
            int startCodeLen = 0;

            // Find first start code
            int firstStart = FindNextStartCode(data, 0, out startCodeLen);
            if (firstStart < 0)
                return Array.Empty<NalUnit>();

            nalStart = firstStart + startCodeLen;
            long currentOffset = streamOffset + nalStart;

            // Parse remaining NAL units
            position = nalStart;
            while (position < data.Length)
            {
                int nextStart = FindNextStartCode(data, position, out int nextCodeLen);

                if (nextStart < 0)
                {
                    // Last NAL unit extends to end
                    if (nalStart < data.Length && nalIndex < nalUnits.Length)
                    {
                        var rawData = data.Slice(nalStart, data.Length - nalStart);
                        nalUnits[nalIndex++] = CreateNalUnit(rawData, currentOffset);
                    }
                    break;
                }

                // Extract current NAL unit
                int nalEnd = nextStart;
                // Remove trailing zeros (part of next start code)
                while (nalEnd > nalStart && data[nalEnd - 1] == 0)
                    nalEnd--;

                if (nalEnd > nalStart && nalIndex < nalUnits.Length)
                {
                    var rawData = data.Slice(nalStart, nalEnd - nalStart);
                    nalUnits[nalIndex++] = CreateNalUnit(rawData, currentOffset);
                }

                nalStart = nextStart + nextCodeLen;
                currentOffset = streamOffset + nalStart;
                position = nalStart;
            }

            // Trim array if we got fewer NAL units than expected
            if (nalIndex < nalUnits.Length)
                Array.Resize(ref nalUnits, nalIndex);

            return nalUnits;
        }

        /// <summary>
        /// Creates a NalUnit from raw NAL data (without start code).
        /// </summary>
        /// <summary>
        /// Creates a NalUnit whose payload and raw data slice <paramref name="rawData"/> rather than
        /// copying it.
        /// </summary>
        /// <remarks>
        /// The span version has to copy - twice, once for the payload and once for the raw data -
        /// because a span cannot be stored. That is two NAL-sized allocations per NAL per frame. A
        /// decoder that holds the access unit in a buffer it owns can slice it instead.
        /// </remarks>
        private static NalUnit CreateNalUnit(ReadOnlyMemory<byte> rawData, long streamPosition)
        {
            if (rawData.Length == 0)
                return default;

            byte firstByte = rawData.Span[0];
            var type = (NalUnitType)(firstByte & 0x1F);
            byte refIdc = (byte)((firstByte >> 5) & 0x03);

            var payload = rawData.Length > 1 ? rawData[1..] : ReadOnlyMemory<byte>.Empty;
            return new NalUnit(type, refIdc, payload, rawData, streamPosition);
        }

        /// <summary>
        /// Splits length-prefixed NAL units into <paramref name="units"/>, growing it if needed, with
        /// payloads slicing <paramref name="data"/> rather than copying it.
        /// </summary>
        public static void ParseLengthPrefixedInto(
            ReadOnlyMemory<byte> data, int lengthSize, ref NalUnit[]? units, out int count)
        {
            units ??= new NalUnit[16];
            count = 0;

            var span = data.Span;
            int position = 0;

            while (position + lengthSize <= span.Length)
            {
                int nalLength = 0;
                for (int i = 0; i < lengthSize; i++)
                    nalLength = (nalLength << 8) | span[position + i];

                position += lengthSize;
                if (nalLength <= 0 || position + nalLength > span.Length)
                    break;

                if (count == units.Length)
                {
                    var larger = new NalUnit[units.Length * 2];
                    Array.Copy(units, larger, count);
                    units = larger;
                }

                units[count++] = CreateNalUnit(data.Slice(position, nalLength), position);
                position += nalLength;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static NalUnit CreateNalUnit(ReadOnlySpan<byte> rawData, long streamPosition)
        {
            if (rawData.Length == 0)
                return default;

            byte firstByte = rawData[0];
            var type = (NalUnitType)(firstByte & 0x1F);
            byte refIdc = (byte)((firstByte >> 5) & 0x03);

            // Payload is everything after the type byte
            var payload = rawData.Length > 1 
                ? rawData.Slice(1).ToArray() 
                : Array.Empty<byte>();

            return new NalUnit(type, refIdc, payload, rawData.ToArray(), streamPosition);
        }

        /// <summary>
        /// Finds the next AnnexB start code (0x000001 or 0x00000001) in the data.
        /// Uses SIMD acceleration when available.
        /// </summary>
        /// <param name="data">The data to search.</param>
        /// <param name="offset">Starting offset for search.</param>
        /// <param name="startCodeLength">Output: length of found start code (3 or 4).</param>
        /// <returns>Position of start code, or -1 if not found.</returns>
        public static int FindNextStartCode(ReadOnlySpan<byte> data, int offset, out int startCodeLength)
        {
            startCodeLength = 0;

            if (offset >= data.Length - 2)
                return -1;

            // Use SIMD for larger buffers
            if (Avx2.IsSupported && data.Length - offset >= 32)
            {
                int simdResult = FindStartCodeAvx2(data, offset, out startCodeLength);
                if (simdResult >= 0)
                    return simdResult;
            }
            else if (Sse2.IsSupported && data.Length - offset >= 16)
            {
                int simdResult = FindStartCodeSse2(data, offset, out startCodeLength);
                if (simdResult >= 0)
                    return simdResult;
            }

            // Scalar fallback
            return FindStartCodeScalar(data, offset, out startCodeLength);
        }

        /// <summary>
        /// AVX2-accelerated start code detection.
        /// Searches for consecutive zeros which indicate potential start codes.
        /// </summary>
        private static int FindStartCodeAvx2(ReadOnlySpan<byte> data, int offset, out int startCodeLength)
        {
            startCodeLength = 0;
            int length = data.Length;
            int position = offset;

            var zeroVector = Vector256<byte>.Zero;

            // Process 32 bytes at a time, looking for zeros
            while (position + 32 <= length - 2)
            {
                var chunk = Vector256.Create(data.Slice(position, 32));
                var compareResult = Avx2.CompareEqual(chunk, zeroVector);
                int mask = Avx2.MoveMask(compareResult);

                if (mask != 0)
                {
                    // Found at least one zero, check for start code pattern
                    while (mask != 0)
                    {
                        int zeroIndex = System.Numerics.BitOperations.TrailingZeroCount((uint)mask);
                        int absolutePos = position + zeroIndex;

                        // Check if this could be a start code
                        if (absolutePos + 3 < length)
                        {
                            // Check for 0x00 0x00 0x01
                            if (data[absolutePos] == 0 && 
                                data[absolutePos + 1] == 0 && 
                                data[absolutePos + 2] == 1)
                            {
                                // Check for 4-byte start code (0x00 0x00 0x00 0x01)
                                if (absolutePos > 0 && data[absolutePos - 1] == 0)
                                {
                                    startCodeLength = StartCodeLength4;
                                    return absolutePos - 1;
                                }
                                startCodeLength = StartCodeLength3;
                                return absolutePos;
                            }
                        }

                        // Clear this bit and continue
                        mask &= mask - 1;
                    }
                }

                position += 32;
            }

            // Handle remaining bytes with scalar code
            if (position < length - 2)
            {
                return FindStartCodeScalar(data, position, out startCodeLength);
            }

            return -1;
        }

        /// <summary>
        /// SSE2-accelerated start code detection for smaller buffers.
        /// </summary>
        private static int FindStartCodeSse2(ReadOnlySpan<byte> data, int offset, out int startCodeLength)
        {
            startCodeLength = 0;
            int length = data.Length;
            int position = offset;

            var zeroVector = Vector128<byte>.Zero;

            while (position + 16 <= length - 2)
            {
                var chunk = Vector128.Create(data.Slice(position, 16));
                var compareResult = Sse2.CompareEqual(chunk, zeroVector);
                int mask = Sse2.MoveMask(compareResult);

                if (mask != 0)
                {
                    while (mask != 0)
                    {
                        int zeroIndex = System.Numerics.BitOperations.TrailingZeroCount((uint)mask);
                        int absolutePos = position + zeroIndex;

                        if (absolutePos + 3 < length)
                        {
                            if (data[absolutePos] == 0 && 
                                data[absolutePos + 1] == 0 && 
                                data[absolutePos + 2] == 1)
                            {
                                if (absolutePos > 0 && data[absolutePos - 1] == 0)
                                {
                                    startCodeLength = StartCodeLength4;
                                    return absolutePos - 1;
                                }
                                startCodeLength = StartCodeLength3;
                                return absolutePos;
                            }
                        }

                        mask &= mask - 1;
                    }
                }

                position += 16;
            }

            if (position < length - 2)
            {
                return FindStartCodeScalar(data, position, out startCodeLength);
            }

            return -1;
        }

        /// <summary>
        /// Scalar fallback for start code detection.
        /// Uses the bitflow technique from VLC for efficiency.
        /// </summary>
        private static int FindStartCodeScalar(ReadOnlySpan<byte> data, int offset, out int startCodeLength)
        {
            startCodeLength = 0;
            int length = data.Length;

            // Bitflow technique: track trailing zeros
            // bitflow bit is set if byte was 0, shifted left each iteration
            uint bitflow = 0;

            for (int i = offset; i < length; i++)
            {
                byte current = data[i];
                bitflow = (bitflow << 1) | (current == 0 ? 1u : 0u);

                // Check for pattern: at least two zeros followed by 0x01
                if (current == 1)
                {
                    // (bitflow & 0x06) == 0x06 means bits 1 and 2 are set
                    // which indicates two consecutive zeros before this byte
                    if ((bitflow & 0x06) == 0x06)
                    {
                        // Found 0x00 0x00 0x01 pattern
                        int startPos = i - 2;

                        // Check for extra leading zero (4-byte start code)
                        if ((bitflow & 0x0E) == 0x0E)
                        {
                            startPos = i - 3;
                            startCodeLength = StartCodeLength4;
                        }
                        else
                        {
                            startCodeLength = StartCodeLength3;
                        }

                        return startPos;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// Counts the number of start codes in the data.
        /// Used for pre-allocation.
        /// </summary>
        private static int CountStartCodes(ReadOnlySpan<byte> data)
        {
            int count = 0;
            int position = 0;

            while (position < data.Length - 2)
            {
                int nextStart = FindNextStartCode(data, position, out int codeLen);
                if (nextStart < 0)
                    break;

                count++;
                position = nextStart + codeLen;
            }

            return count;
        }

        /// <summary>
        /// Converts RBSP (Raw Byte Sequence Payload) to SODB (String of Data Bits)
        /// by removing emulation prevention bytes (0x03 after 0x00 0x00).
        /// </summary>
        /// <param name="rbsp">The RBSP data.</param>
        /// <returns>The decoded SODB data.</returns>
        /// <summary>
        /// Unescapes a NAL into a caller-owned buffer, growing it only when it is too small.
        /// </summary>
        /// <remarks>
        /// The allocating overload copies the result into an exact-size array, which for a slice is
        /// kilobytes every frame. A decoder unescapes the same slice on a fixed path, so it can own
        /// the buffer and reuse it: after the first frame this allocates nothing. Only the first
        /// <paramref name="rbspLength"/> bytes are meaningful.
        /// </remarks>
        public static void RemoveEmulationPreventionBytesInto(
            ReadOnlySpan<byte> nalPayload,
            ref byte[]? rbsp, out int rbspLength,
            ref int[]? removedPositions, out int removedCount)
        {
            // Grown geometrically, not to exactly this slice's length. Slice sizes drift upwards
            // across a stream, so growing to fit bought a fresh slice-sized buffer for every slice
            // a little larger than the largest so far - which kept happening all through a file
            // rather than settling. Doubling reaches the largest slice in a few steps and stops.
            if (rbsp is null || rbsp.Length < nalPayload.Length)
                rbsp = new byte[Math.Max(nalPayload.Length, Math.Max(4096, rbsp is null ? 0 : rbsp.Length * 2))];
            removedPositions ??= new int[64];

            rbspLength = 0;
            removedCount = 0;
            int i = 0;

            while (i < nalPayload.Length)
            {
                // Emulation prevention sequence: 0x00 0x00 0x03. Same rule as the allocating
                // overload, so the two produce identical output.
                if (i + 2 < nalPayload.Length &&
                    nalPayload[i] == 0x00 &&
                    nalPayload[i + 1] == 0x00 &&
                    nalPayload[i + 2] == 0x03)
                {
                    rbsp[rbspLength++] = 0x00;
                    rbsp[rbspLength++] = 0x00;

                    if (removedCount == removedPositions.Length)
                    {
                        var larger = new int[removedPositions.Length * 2];
                        Array.Copy(removedPositions, larger, removedCount);
                        removedPositions = larger;
                    }

                    removedPositions[removedCount++] = i + 2;
                    i += 3;
                }
                else
                {
                    rbsp[rbspLength++] = nalPayload[i++];
                }
            }
        }

        public static byte[] RemoveEmulationPreventionBytes(ReadOnlySpan<byte> rbsp)
        {
            if (rbsp.Length == 0)
                return Array.Empty<byte>();

            // Worst case: no emulation bytes, output = input size
            byte[] output = ArrayPool<byte>.Shared.Rent(rbsp.Length);
            try
            {
                int outputIndex = 0;
                int i = 0;

                while (i < rbsp.Length)
                {
                    // Check for emulation prevention sequence: 0x00 0x00 0x03
                    if (i + 2 < rbsp.Length &&
                        rbsp[i] == 0x00 &&
                        rbsp[i + 1] == 0x00 &&
                        rbsp[i + 2] == 0x03)
                    {
                        // Copy the two zeros
                        output[outputIndex++] = 0x00;
                        output[outputIndex++] = 0x00;
                        // Skip the 0x03 emulation prevention byte
                        i += 3;
                    }
                    else
                    {
                        output[outputIndex++] = rbsp[i++];
                    }
                }

                // Copy to exact-size array
                byte[] result = new byte[outputIndex];
                Array.Copy(output, result, outputIndex);
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(output);
            }
        }

        /// <summary>
        /// Parses NAL units from length-prefixed format (AVC sample format).
        /// Used in MP4/MOV containers where NAL units are prefixed with their length.
        /// </summary>
        /// <param name="data">The input data.</param>
        /// <param name="lengthSize">Size of the length prefix (1, 2, 3, or 4 bytes).</param>
        /// <param name="streamOffset">Base stream offset for position calculation.</param>
        /// <returns>Array of parsed NAL units.</returns>
        public static NalUnit[] ParseLengthPrefixed(ReadOnlySpan<byte> data, int lengthSize, long streamOffset = 0)
        {
            return ParseLengthPrefixed(data, lengthSize, out _, streamOffset);
        }

        /// <summary>
        /// Parses length-prefixed NAL units, also reporting how many bytes were consumed.
        /// </summary>
        public static NalUnit[] ParseLengthPrefixed(ReadOnlySpan<byte> data, int lengthSize, out int bytesConsumed, long streamOffset = 0)
        {
            bytesConsumed = 0;
            if (data.Length < lengthSize)
                return Array.Empty<NalUnit>();

            // First pass: count NAL units
            int count = 0;
            int pos = 0;
            while (pos + lengthSize <= data.Length)
            {
                uint nalLength = ReadLength(data.Slice(pos), lengthSize);
                if (nalLength == 0 || pos + lengthSize + nalLength > data.Length)
                    break;
                count++;
                pos += lengthSize + (int)nalLength;
            }

            if (count == 0)
                return Array.Empty<NalUnit>();

            // Second pass: extract NAL units
            var nalUnits = new NalUnit[count];
            pos = 0;
            int idx = 0;

            while (pos + lengthSize <= data.Length && idx < count)
            {
                uint nalLength = ReadLength(data.Slice(pos), lengthSize);
                if (nalLength == 0 || pos + lengthSize + nalLength > data.Length)
                    break;

                int dataStart = pos + lengthSize;
                var rawData = data.Slice(dataStart, (int)nalLength);
                nalUnits[idx++] = CreateNalUnit(rawData, streamOffset + dataStart);
                pos = dataStart + (int)nalLength;
            }

            bytesConsumed = pos;
            return nalUnits;
        }

        /// <summary>
        /// Parses length-prefixed NAL units with auto-detection of the actual length prefix size.
        /// Some muxers (especially MKV) incorrectly declare nalLengthSize in avcC but use a different
        /// size in the actual data. This method tries the declared size first, then falls back to
        /// other sizes. Also handles packets with mixed length prefix sizes (e.g., 3-byte for SEI
        /// followed by 4-byte for IDR in the same access unit).
        /// </summary>
        /// <param name="data">The input data.</param>
        /// <param name="declaredLengthSize">The length prefix size declared in avcC/hvcC.</param>
        /// <param name="detectedLengthSize">The actual length size that worked, for caching.</param>
        /// <param name="streamOffset">Base stream offset for position calculation.</param>
        /// <returns>Array of parsed NAL units.</returns>
        public static NalUnit[] ParseLengthPrefixedWithAutoDetect(
            ReadOnlySpan<byte> data, int declaredLengthSize, ref int detectedLengthSize, long streamOffset = 0)
        {
            if (data.Length < 4)
                return Array.Empty<NalUnit>();

            // If we've previously detected a working size, try it first
            int primarySize = detectedLengthSize > 0 ? detectedLengthSize : declaredLengthSize;
            var result = ParseLengthPrefixed(data, primarySize, out int consumed, streamOffset);
            if (result.Length > 0 && consumed >= data.Length)
            {
                // Fully consumed — single prefix size works
                detectedLengthSize = primarySize;
                return result;
            }

            // Either nothing parsed, or there's unconsumed data remaining.
            // Try all valid sizes and pick the one that consumes the most data
            // while producing valid NAL types. Also handle mixed-prefix packets
            // where the first NAL uses one size and subsequent NALs use another.
            int[] allSizes = [4, 3, 2, 1];
            NalUnit[] bestResult = result;
            int bestConsumed = consumed;
            int bestSize = result.Length > 0 ? primarySize : 0;

            foreach (int trySize in allSizes)
            {
                if (trySize == primarySize && result.Length > 0)
                    continue; // Already tried

                var tryResult = ParseLengthPrefixed(data, trySize, out int tryConsumed, streamOffset);
                if (tryResult.Length > bestResult.Length ||
                    (tryResult.Length == bestResult.Length && tryConsumed > bestConsumed))
                {
                    // Validate: at least one NAL should have a recognizable type
                    bool hasValidNal = false;
                    foreach (var nal in tryResult)
                    {
                        int nalType = (int)nal.Type;
                        if (nalType >= 1 && nalType <= 12)
                        {
                            hasValidNal = true;
                            break;
                        }
                    }

                    if (hasValidNal)
                    {
                        bestResult = tryResult;
                        bestConsumed = tryConsumed;
                        bestSize = trySize;
                    }
                }
            }

            // If the best single-size parse left unconsumed data (>= 5 bytes: min length prefix + NAL header),
            // try parsing the remainder with other prefix sizes to handle mixed-prefix packets.
            if (bestResult.Length > 0 && bestConsumed < data.Length - 4)
            {
                var remainingData = data.Slice(bestConsumed);
                long remainingOffset = streamOffset + bestConsumed;

                foreach (int trySize in allSizes)
                {
                    var extraNals = ParseLengthPrefixed(remainingData, trySize, out int extraConsumed, remainingOffset);
                    if (extraNals.Length > 0)
                    {
                        bool hasValidExtra = false;
                        foreach (var nal in extraNals)
                        {
                            int nalType = (int)nal.Type;
                            if (nalType >= 1 && nalType <= 12)
                            {
                                hasValidExtra = true;
                                break;
                            }
                        }

                        if (hasValidExtra)
                        {
                            // Combine: original NALs + extra NALs from remaining data
                            var combined = new NalUnit[bestResult.Length + extraNals.Length];
                            bestResult.CopyTo(combined, 0);
                            extraNals.CopyTo(combined, bestResult.Length);
                            bestResult = combined;
                            bestConsumed += extraConsumed;
                            break;
                        }
                    }
                }
            }

            if (bestResult.Length > 0 && bestSize > 0)
            {
                detectedLengthSize = bestSize;
                return bestResult;
            }

            // All length-prefixed approaches failed — try Annex B as last resort
            // Some MKV muxers store Annex B data despite having avcC codec private
            result = ParseAnnexB(data);
            if (result.Length > 0)
            {
                detectedLengthSize = -1; // Signal Annex B mode
                return result;
            }

            return Array.Empty<NalUnit>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ReadLength(ReadOnlySpan<byte> data, int lengthSize)
        {
            return lengthSize switch
            {
                1 => data[0],
                2 => (uint)((data[0] << 8) | data[1]),
                3 => (uint)((data[0] << 16) | (data[1] << 8) | data[2]),
                4 => (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]),
                _ => throw new ArgumentException("Invalid length size", nameof(lengthSize))
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.H264
{
    /// <summary>
    /// Single CABAC operation recorded by the trace log.
    /// Type: 0=regular bin, 1=bypass bin, 2=terminate bin, 3=PCM reinit.
    /// </summary>
    public struct CabacTraceEntry
    {
        public byte Type;
        public short CtxIdx;
        public byte Result;
        public int PcmByteCount;  // only meaningful for Type=3
        public int BytePosition;  // byte position in CABAC data at time of this operation
    }

    /// <summary>
    /// CABAC (Context-Adaptive Binary Arithmetic Coding) decoder for H.264/AVC.
    /// This is a ref struct to avoid heap allocations during decoding.
    /// </summary>
    /// <remarks>
    /// Based on ITU-T H.264/AVC Annex E.
    /// The arithmetic decoder maintains two state variables:
    /// - codIRange: the current range (9 bits, 256-510)
    /// - codIOffset: the current offset within the range (9 bits)
    /// </remarks>
    public ref struct CabacDecoder
    {
        // Shadow trace: when non-null, every bin/bypass/terminate/pcm-reinit is recorded.
        // Set before decoding a frame and read back after. Null = zero overhead.
        public static List<CabacTraceEntry>? TraceLog;
        public static byte[]? TraceInitData;
        public static byte[]? TraceInitContextStates;
        public static int TraceMaxEntries = 50_000;  // Stop recording after this many entries
        public static int TraceSliceCapture = -1;    // Which slice index to capture (-1 = first, 0+ = specific)
        public static int TraceSliceBinStart;        // Bin index where captured slice starts
        public static int TraceSliceCounter;         // Counter for slice init calls (reset before decode)
        private readonly ReadOnlySpan<byte> data;
        private int bytePosition;
        // FFmpeg's representation, not the spec's. `low` carries the offset shifted left by
        // seventeen with sixteen lookahead bits beneath it, so renormalisation is a shift and
        // nothing else, and the bitstream is touched two bytes at a time when those bits drain
        // rather than one bit at a time on every bin. See cabac_functions.h.
        private int codIRange;      // Current range, 9 bits
        private int low;            // Offset << 17 | 16 lookahead bits

        /// <summary>
        /// Context states: probability state (0-63) and MPS value (0 or 1).
        /// Stored as: (pStateIdx << 1) | valMps
        /// </summary>
        private readonly Span<byte> contextStates;
        
        // Diagnostic: total bins decoded (regular + bypass + terminate)
        public int TotalBinsDecoded;
        
        /// <summary>Current byte position in the bitstream data.</summary>
        public readonly int BytePosition => bytePosition;
        
        /// <summary>Bits remaining in the current byte (8 = at byte boundary).</summary>
        public readonly int BitsRemaining => 8;
        
        // Diagnostic: snapshot of CABAC state for tracing
        public readonly string DiagState => $"R={codIRange} L={low} bp={bytePosition} bins={TotalBinsDecoded}";
        
        // Diagnostic: get context state for a given index
        public readonly string GetContextState(int ctxIdx)
        {
            byte s = contextStates[ctxIdx];
            return $"ctx{ctxIdx}:pS={s >> 1},mps={s & 1}";
        }

        /// <summary>Export all context states to a byte array (for shadow comparison).</summary>
        public readonly byte[] ExportContextStates()
        {
            var result = new byte[CabacTables.ContextInit.TotalContexts];
            contextStates.Slice(0, result.Length).CopyTo(result);
            return result;
        }
        
        /// <summary>Compute a quick hash of all context states for divergence detection.</summary>
        public readonly uint ContextStateHash()
        {
            uint hash = 2166136261u; // FNV-1a offset basis
            for (int i = 0; i < CabacTables.ContextInit.TotalContexts; i++)
            {
                hash ^= contextStates[i];
                hash *= 16777619u; // FNV prime
            }
            return hash;
        }
        
        /// <summary>Get raw pStateIdx for a context (0-63).</summary>
        public readonly int GetPState(int ctxIdx) => contextStates[ctxIdx] >> 1;
        
        /// <summary>Get raw valMPS for a context (0 or 1).</summary>
        public readonly int GetValMps(int ctxIdx) => contextStates[ctxIdx] & 1;

        /// <summary>
        /// Gets whether the decoder has reached the end of data.
        /// </summary>
        public readonly bool IsEndOfStream => ConsumedBytes >= data.Length;

        /// <summary>
        /// Creates a new CABAC decoder.
        /// </summary>
        /// <param name="bitstreamData">The CABAC-encoded bitstream data (RBSP).</param>
        /// <param name="contextStateStorage">Pre-allocated storage for context states (460 bytes minimum).</param>
        /// <param name="sliceQp">Slice quantization parameter for context initialization.</param>
        /// <param name="cabacInitIdc">CABAC init IDC from slice header (0-2 for P/B, ignored for I).</param>
        /// <param name="isISlice">True if this is an I-slice.</param>
        public CabacDecoder(ReadOnlySpan<byte> bitstreamData, Span<byte> contextStateStorage,
            int sliceQp, int cabacInitIdc, bool isISlice)
        {
            if (contextStateStorage.Length < CabacTables.ContextInit.TotalContexts)
                throw new ArgumentException(
                    $"Context storage must have at least {CabacTables.ContextInit.TotalContexts} bytes",
                    nameof(contextStateStorage));

            data = bitstreamData;
            contextStates = contextStateStorage;

            StartArithmeticDecoder(0);

            // Initialize all context states
            InitializeContexts(sliceQp, cabacInitIdc, isISlice);

            // Capture init data for shadow replay.
            // When TraceSliceCapture >= 0, capture only that slice index.
            // When negative, capture first slice (legacy behavior).
            if (TraceLog != null)
            {
                if (TraceSliceCapture < 0 && TraceInitData == null)
                {
                    TraceInitData = bitstreamData.ToArray();
                    TraceInitContextStates = contextStateStorage.Slice(0, CabacTables.ContextInit.TotalContexts).ToArray();
                }
                else if (TraceSliceCapture >= 0)
                {
                    if (TraceSliceCounter == TraceSliceCapture)
                    {
                        TraceInitData = bitstreamData.ToArray();
                        TraceInitContextStates = contextStateStorage.Slice(0, CabacTables.ContextInit.TotalContexts).ToArray();
                        TraceLog.Clear();  // Clear prior slices' bins
                        TraceSliceBinStart = 0;
                    }
                    TraceSliceCounter++;
                }
            }
        }

        /// <summary>
        /// Initializes all context states based on slice QP and initialization table.
        /// </summary>
        private void InitializeContexts(int sliceQp, int cabacInitIdc, bool isISlice)
        {
            for (int ctxIdx = 0; ctxIdx < CabacTables.ContextInit.TotalContexts; ctxIdx++)
            {
                CabacTables.ContextInit.GetInitialState(sliceQp, cabacInitIdc, isISlice, ctxIdx,
                    out int pStateIdx, out int valMps);

                // Pack state and MPS into single byte
                contextStates[ctxIdx] = (byte)((pStateIdx << 1) | valMps);
            }
        }

        /// <summary>
        /// Decodes a single binary decision using the specified context.
        /// </summary>
        /// <param name="contextIndex">The context index to use (0-459).</param>
        /// <returns>The decoded binary value (0 or 1).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int DecodeBin(int contextIndex)
        {
            // Branchless, the way FFmpeg's get_cabac_inline is. A CABAC bin is close to
            // unpredictable by construction - that is the entire point of the arithmetic coder -
            // so the outcome test was a branch the predictor cannot learn, taken ninety thousand
            // times a frame. Turning it into a mask costs a shift and keeps the pipeline full.
            byte state = contextStates[contextIndex];
            int rangeIndex = (codIRange >> 6) & 3;
            int codIRangeLps = CabacTables.GetLpsRange(state >> 1, rangeIndex);

            codIRange -= codIRangeLps;

            // All ones when the offset landed in the least-probable sub-range, zero otherwise.
            int lpsMask = ((codIRange << 17) - low) >> 31;

            low -= (codIRange << 17) & lpsMask;
            codIRange += (codIRangeLps - codIRange) & lpsMask;

            // The successor state and the bin value both fall out of the same mask: the table's
            // upper half holds the least-probable successors, and the bin is the state's
            // most-probable flag, inverted exactly when that is the branch taken.
            int binVal = (state & 1) ^ (lpsMask & 1);
            contextStates[contextIndex] = CabacTables.NextState[state + (lpsMask & 128)];

            // Renormalization
            Renormalize();

            TotalBinsDecoded++;

            if (TraceLog != null && TraceLog.Count < TraceMaxEntries)
                TraceLog.Add(new CabacTraceEntry { Type = 0, CtxIdx = (short)contextIndex, Result = (byte)binVal, BytePosition = bytePosition });

            return binVal;
        }

        /// <summary>
        /// Decodes a binary decision in bypass mode (equiprobable).
        /// Used for certain syntax elements where context adaptation provides no benefit.
        /// </summary>
        /// <returns>The decoded binary value (0 or 1).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int DecodeBypass()
        {
            // One more bit of the offset, taken from the lookahead already in `low`.
            low += low;
            if ((low & 0xFFFF) == 0)
                Refill();

            int scaledRange = codIRange << 17;
            int val;
            if (low < scaledRange)
            {
                val = 0;
            }
            else
            {
                low -= scaledRange;
                val = 1;
            }
            
            TotalBinsDecoded++;

            if (TraceLog != null && TraceLog.Count < TraceMaxEntries)
                TraceLog.Add(new CabacTraceEntry { Type = 1, CtxIdx = -1, Result = (byte)val, BytePosition = bytePosition });
            
            return val;
        }

        /// <summary>
        /// Decodes a terminate bin, used at the end of slice data.
        /// </summary>
        /// <returns>1 if this is the terminating bin, 0 otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int DecodeTerminate()
        {
            codIRange -= 2;

            if (low >= (codIRange << 17))
            {
                if (TraceLog != null && TraceLog.Count < TraceMaxEntries)
                    TraceLog.Add(new CabacTraceEntry { Type = 2, CtxIdx = -1, Result = 1, BytePosition = bytePosition });
                return 1;  // End of slice
            }

            Renormalize();
            if (TraceLog != null && TraceLog.Count < TraceMaxEntries)
                TraceLog.Add(new CabacTraceEntry { Type = 2, CtxIdx = -1, Result = 0, BytePosition = bytePosition });
            return 0;
        }

        /// <summary>
        /// Decodes an unsigned exp-golomb coded value using CABAC bypass bins.
        /// Uses CAVLC convention (leading zeros). For CABAC residual suffixes,
        /// use DecodeUeg0Bypass instead.
        /// </summary>
        /// <returns>The decoded unsigned value.</returns>
        public uint DecodeExpGolombUnsigned()
        {
            int leadingZeros = 0;

            // Count leading zeros (each is a bypass bin)
            while (DecodeBypass() == 0)
            {
                leadingZeros++;
                if (leadingZeros > 32)
                    throw new InvalidOperationException("Invalid exp-golomb code in CABAC stream");
            }

            if (leadingZeros == 0)
                return 0;

            // Read suffix bits
            uint suffix = 0;
            for (int i = 0; i < leadingZeros; i++)
            {
                suffix = (suffix << 1) | (uint)DecodeBypass();
            }

            return (1u << leadingZeros) - 1 + suffix;
        }

        /// <summary>
        /// Decodes a 0th-order unsigned exp-golomb (UEG0) value using CABAC bypass bins.
        /// H.264 CABAC convention: count leading ONES until a ZERO, then read that many suffix bits.
        /// Used for coeff_abs_level_minus1 suffix when prefix reaches maximum (14).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint DecodeUeg0Bypass()
        {
            int leadingOnes = 0;

            while (DecodeBypass() != 0)
            {
                leadingOnes++;
                if (leadingOnes > 24)
                    break;
            }

            if (leadingOnes == 0)
                return 0;

            uint suffix = 0;
            for (int i = 0; i < leadingOnes; i++)
            {
                suffix = (suffix << 1) | (uint)DecodeBypass();
            }

            return (1u << leadingOnes) - 1 + suffix;
        }

        /// <summary>
        /// Decodes a signed exp-golomb coded value using CABAC bypass bins.
        /// Maps 0, 1, 2, 3, 4... to 0, 1, -1, 2, -2...
        /// </summary>
        /// <returns>The decoded signed value.</returns>
        public int DecodeExpGolombSigned()
        {
            uint codeNum = DecodeExpGolombUnsigned();
            int value = (int)((codeNum + 1) >> 1);
            return ((codeNum & 1) == 0) ? -value : value;
        }

        /// <summary>
        /// Decodes an unsigned value using truncated unary followed by exp-golomb.
        /// Common pattern in H.264 for MVD and coefficient levels.
        /// </summary>
        /// <param name="maxUnary">Maximum value for unary part (cMax).</param>
        /// <param name="contextOffset">Base context index for unary bins.</param>
        /// <param name="contextIncrement">Context increment per bin.</param>
        /// <returns>The decoded unsigned value.</returns>
        public uint DecodeTruncatedUnaryExpGolomb(int maxUnary, int contextOffset, int contextIncrement)
        {
            // Decode unary prefix
            uint value = 0;
            while (value < maxUnary && DecodeBin(contextOffset + Math.Min((int)value, contextIncrement)) != 0)
            {
                value++;
            }

            // If reached max, decode exp-golomb suffix
            if (value >= maxUnary)
            {
                value += DecodeExpGolombUnsigned();
            }

            return value;
        }

        /// <summary>
        /// Decodes a fixed number of bypass bins as an unsigned integer.
        /// </summary>
        /// <param name="numberOfBits">Number of bits to decode.</param>
        /// <returns>The decoded unsigned value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint DecodeBypassBits(int numberOfBits)
        {
            uint value = 0;
            for (int i = 0; i < numberOfBits; i++)
            {
                value = (value << 1) | (uint)DecodeBypass();
            }
            return value;
        }

        /// <summary>
        /// Decodes a signed value from bypass bins.
        /// </summary>
        /// <param name="absoluteValue">The absolute value already decoded.</param>
        /// <returns>The signed value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int DecodeSign(uint absoluteValue)
        {
            if (absoluteValue == 0)
                return 0;

            return DecodeBypass() != 0 ? -(int)absoluteValue : (int)absoluteValue;
        }

        /// <summary>Restores the invariant that the range is at least 256.</summary>
        /// <remarks>
        /// The whole renormalisation is one shift of both registers. The bitstream is not touched
        /// at all until the sixteen lookahead bits in <c>low</c> drain, and then two bytes arrive
        /// at once. This is what the spec's bit-at-a-time loop costs: it ran up to seven times per
        /// bin, ninety thousand bins a frame, each iteration a bounds test, shift, mask and byte
        /// advance.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Renormalize()
        {
            int shift = System.Numerics.BitOperations.LeadingZeroCount((uint)codIRange) - 23;
            codIRange <<= shift;
            low <<= shift;

            if ((low & 0xFFFF) == 0)
                Refill2();
        }

        /// <summary>Brings in two more bytes when the lookahead has run out.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Refill()
        {
            low += NextTwoBytes();
            low -= 0xFFFF;

            // FFmpeg stops advancing at the end of the buffer rather than running past it,
            // so the last bytes are re-read instead of zeros arriving.
            if (bytePosition < data.Length)
                bytePosition += 2;
        }

        /// <summary>
        /// Brings in two more bytes, placed where the drained bits actually ran out.
        /// </summary>
        /// <remarks>
        /// Renormalisation can shift by more than one, so the lookahead does not always empty
        /// exactly at the boundary; the trailing zero count says how far the new bits belong.
        /// </remarks>
        private void Refill2()
        {
            int i = System.Numerics.BitOperations.TrailingZeroCount(low) - 16;
            int x = NextTwoBytes() - 0xFFFF;

            low += x << i;

            if (bytePosition < data.Length)
                bytePosition += 2;
        }

        /// <summary>The next two bitstream bytes, positioned as the arithmetic decoder wants them.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly int NextTwoBytes()
        {
            int first = bytePosition < data.Length ? data[bytePosition] : 0;
            int second = bytePosition + 1 < data.Length ? data[bytePosition + 1] : 0;
            return (first << 9) + (second << 1);
        }

        /// <summary>Starts the arithmetic decoder at a byte position, per ff_init_cabac_decoder.</summary>
        private void StartArithmeticDecoder(int at)
        {
            bytePosition = at;
            codIRange = 0x1FE;
            low = NextTwoBytes() << 9;
            bytePosition += 2;
            low += 1 << 9;
        }

        /// <summary>
        /// Handles I_PCM macroblock: skips past raw PCM sample bytes in the bitstream
        /// and re-initializes the arithmetic decoder and all context states.
        /// Called after DecodeTerminate returns 1 for I_PCM (not end-of-slice).
        /// </summary>
        /// <param name="pcmByteCount">Number of raw PCM bytes (e.g. 384 for 4:2:0 at 8-bit).</param>
        /// <param name="pcmData">Destination span for the raw PCM sample bytes.</param>
        /// <param name="sliceQp">Slice QP for context re-initialization.</param>
        /// <param name="cabacInitIdc">CABAC init IDC for context re-initialization.</param>
        /// <param name="isISlice">Whether current slice is I-slice.</param>
        public void SkipPcmAndReinitialize(int pcmByteCount, Span<byte> pcmData)
        {
            // Where the bitstream really stands, given `low` holds lookahead the reader has
            // consumed but the decoder has not used. This is h264_cabac.c's calculation.
            // Where the next byte-aligned data begins, which is not the same as how much has
            // been consumed - this is h264_cabac.c's calculation, and only I_PCM wants it.
            int pcmStart = bytePosition;
            if ((low & 0x1) != 0) pcmStart--;
            if ((low & 0x1FF) != 0) pcmStart--;

            // Copy raw PCM samples
            if (pcmStart + pcmByteCount <= data.Length)
                data.Slice(pcmStart, pcmByteCount).CopyTo(pcmData);

            // Advance past PCM data
            int afterPcm = pcmStart + pcmByteCount;

            if (TraceLog != null && TraceLog.Count < TraceMaxEntries)
                TraceLog.Add(new CabacTraceEntry { Type = 3, CtxIdx = -1, PcmByteCount = pcmByteCount, BytePosition = bytePosition });

            // Re-initialize ONLY the arithmetic decoding engine (H.264 spec 9.3.3.5).
            // Context variables are NOT re-initialized — they persist from prior decoding.
            // This matches FFmpeg's ff_init_cabac_decoder (no context reinit).
            StartArithmeticDecoder(afterPcm);
        }

        /// <summary>
        /// Gets the current bit position in the stream for debugging.
        /// </summary>
        public readonly int CurrentBitPosition => ConsumedBytes * 8;

        /// <summary>
        /// Gets the remaining bytes in the stream.
        /// </summary>
        /// <summary>
        /// How far the bitstream has actually been consumed, as opposed to read.
        /// </summary>
        /// <remarks>
        /// <c>low</c> holds up to sixteen bits the reader has taken but the decoder has not
        /// used, so <c>bytePosition</c> sits up to two bytes ahead of the truth and every test
        /// on stream position has to correct for it or a slice ends early.
        /// <para>
        /// This is deliberately the conservative bound rather than h264_cabac.c's
        /// <c>(low &amp; 1)</c> / <c>(low &amp; 0x1FF)</c> correction. That one answers a
        /// different question - where the next byte-aligned data begins, which is what I_PCM
        /// needs - and it runs ahead of what has actually been consumed. Using it here ended
        /// slices early and corrupted every frame from the fourth on, while the arithmetic
        /// decoder itself agreed bin for bin with the engine it replaced.
        /// </para>
        /// </remarks>
        public readonly int ConsumedBytes
        {
            get
            {
                return bytePosition - 2;
            }
        }

        public readonly int RemainingBytes => data.Length - ConsumedBytes;
        
        // Temporary debug properties
        public readonly uint DebugRange => (uint)codIRange;
        public readonly uint DebugOffset => (uint)low;
        
        /// <summary>Dump raw bytes around current position for external verification.</summary>
        public readonly string DumpBytes(int before = 4, int after = 8)
        {
            int start = Math.Max(0, bytePosition - before);
            int end = Math.Min(data.Length, bytePosition + after);
            var sb = new System.Text.StringBuilder();
            sb.Append($"bp={bytePosition} low={low} bytes[{start}..{end}]=");
            for (int i = start; i < end; i++)
            {
                if (i == bytePosition) sb.Append('[');
                sb.Append($"{data[i]:X2}");
                if (i == bytePosition) sb.Append(']');
                sb.Append(' ');
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Context index definitions for H.264 CABAC decoding.
    /// Each syntax element has assigned context indices from the spec.
    /// </summary>
    public static class CabacContextIndex
    {
        // Table 9-11 - Context index offsets

        // MB type contexts (I slice)
        public const int MbTypeI = 3;
        public const int MbTypeSI = 0;

        // MB type contexts (P/B slice) 
        public const int MbSkipFlag = 11;
        public const int MbTypeP = 14;
        public const int MbTypeB = 27;

        // Sub-macroblock type
        public const int SubMbType = 21;
        public const int SubMbTypeB = 36;

        // Motion vector difference
        public const int MvdX = 40;
        public const int MvdY = 47;

        // Reference frame index
        public const int RefIdx = 54;

        // MB QP delta
        public const int MbQpDelta = 60;

        // Intra prediction modes
        public const int IntraChromaPredMode = 64;
        public const int PrevIntra4x4PredModeFlag = 68;
        public const int RemIntra4x4PredMode = 69;

        // Coded block pattern
        public const int CbpLuma = 73;
        public const int CbpChroma = 77;

        // Coded block flag
        public const int CodedBlockFlag = 85;

        // Significant coefficient flag
        public const int SignificantCoeffFlag = 105;
        public const int LastSignificantCoeffFlag = 166;
        public const int CoeffAbsLevelMinus1 = 227;

        // Transform size flag
        public const int TransformSizeFlag = 399;

        // End of slice flag
        public const int EndOfSliceFlag = 276;
    }
}

// H.264/AVC reconstruction pipeline
// Partial class: macroblock reconstruction, intra prediction, inter prediction, chroma processing

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.H264;

public sealed partial class H264Decoder
{
    // Temporary diagnostic: set to (mbX, mbY, frameNum) to dump one MB's reconstruction data
    public static (int mbX, int mbY, int frame)? DiagnosticTargetMb = null;
    public static TextWriter? DiagnosticOutput = null;
    // Per-slice temporal direct trace counter (reset per-slice, used to limit output)
    private int diagTdTraceCount;
    // (DisableDeblockingForTest removed — use instance property SkipDeblocking)

    /// <summary>Loads a block's sixteen coefficients, which the parser left in raster order.</summary>
    /// <remarks>
    /// Both parsers now store each coefficient at its raster position as they decode it, the way
    /// FFmpeg's <c>decode_cabac_residual</c> does and the way our own eight-by-eight path always
    /// has. What used to happen here instead was a clear of sixteen entries followed by sixteen
    /// indirect writes through the scan table, for every coded block in the picture whether it
    /// held one coefficient or sixteen.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadBlockCoefficients(ReadOnlySpan<short> residual, int at, Span<short> block)
        => residual.Slice(at, 16).CopyTo(block);

    /// <inheritdoc cref="LoadBlockCoefficients(ReadOnlySpan{short}, int, Span{short})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadBlockCoefficients(ReadOnlySpan<short> residual, int at, Span<int> block)
    {
        for (int k = 0; k < 16; k++)
            block[k] = residual[at + k];
    }

    /// <summary>
    /// Loads the fifteen alternating-current coefficients of a block, leaving position zero for
    /// the direct-current term the caller holds separately.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadAcCoefficients(ReadOnlySpan<short> residual, int at, Span<short> block)
        => residual.Slice(at + 1, 15).CopyTo(block.Slice(1));

    /// <inheritdoc cref="LoadAcCoefficients(ReadOnlySpan{short}, int, Span{short})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadAcCoefficients(ReadOnlySpan<short> residual, int at, Span<int> block)
    {
        for (int k = 1; k < 16; k++)
            block[k] = residual[at + k];
    }

    // (DisableWeightedPredForTest removed — use instance property SkipWeightedPred)
    // Diagnostic: dump summary of ALL MBs for a specific frame (by frame_num)
    public static int DiagnosticDumpAllMbsForFrame = -1;
    // (SkipInter8x8ResidualForTest removed — use instance property SkipInter8x8Residual)
    // Diagnostic: dump per-8x8 MV detail for all MBs at a specific POC
    public static int DiagnosticDumpAllMbsForPoc = -1;
    // Diagnostic: trace temporal direct mode details for specific POC
    public static int DiagTemporalDirectTracePoc = -1;
    // Diagnostic: trace MC stride details for specific MB position (mbX,mbY)
    public static (int mbX, int mbY)? DiagMcStrideMb = null;
    /// <summary>Expose per-MB type array for diagnostics. -1=Skip, 0=B_Direct_16x16, etc.</summary>
    public int[]? DiagMbTypes => cabacMbTypes;
    /// <summary>Expose per-4x4-block L0 MV X for diagnostics.</summary>
    public short[]? DiagMvL0X => deblockMvL0;
    /// <summary>Expose per-4x4-block L0 MV Y for diagnostics.</summary>
    public short[]? DiagMvL0Y => deblockMvL0;
    /// <summary>Expose per-4x4-block L1 MV X for diagnostics.</summary>
    public short[]? DiagMvL1X => deblockMvL1;
    /// <summary>Expose per-4x4-block L1 MV Y for diagnostics.</summary>
    public short[]? DiagMvL1Y => deblockMvL1;
    /// <summary>Expose per-4x4-block L0 ref index for diagnostics.</summary>
    public sbyte[]? DiagRefL0 => deblockRefL0;
    /// <summary>Expose per-4x4-block L1 ref index for diagnostics.</summary>
    public sbyte[]? DiagRefL1 => deblockRefL1;
    // Diagnostic: trace CABAC skip/mb_type decisions for first N MBs at specific POC
    public static int DiagCabacTracePoc = -1;
    public static int DiagCabacTraceMbCount = 20;
    public static int DiagMbGridPoc = -1;
    /// <summary>Pre-deblocking luma snapshot buffer (set externally). Filled at PreDeblockDecodeIndex.</summary>
    public static byte[]? PreDeblockLumaSnapshot;
    /// <summary>Which decode count (1-indexed) to save pre-deblocking luma for (-1 = disabled).</summary>
    public static int PreDeblockDecodeIndex = -1;
    // Diagnostic: dump per-4x4 MV detail for a specific MB address 
    public static int DiagDumpDetailMbAddr = -1;
    // Diagnostic: enable per-bin CABAC state trace for B-slice mb_type 
    public static bool DiagCabacBinTrace = false;
    // Diagnostic: watch a specific buffer index and log all writes to it
    public static int DiagWatchBufferIndex = -1;
    // Diagnostic: zero all residual for a specific POC (prediction-only output)
    public static int DiagZeroResidualForPoc = -1;
    /// <summary>Snapshot of mbFieldDecodingFlag captured at DiagCabacTracePoc frame end.</summary>
    public static bool[]? DiagFieldFlagSnapshot;

    /// <summary>
    /// Resets ALL mutable static diagnostic state to defaults.
    /// Call between independent decode sessions to prevent cross-contamination.
    /// </summary>
    public static void ResetDiagnosticState()
    {
        // H264Decoder statics
        DiagnosticTargetMb = null;
        DiagnosticOutput = null;
        DiagnosticDumpAllMbsForFrame = -1;
        DiagnosticDumpAllMbsForPoc = -1;
        DiagTemporalDirectTracePoc = -1;
        DiagCabacTracePoc = -1;
        DiagCabacTraceMbCount = 20;
        DiagMbGridPoc = -1;
        PreDeblockLumaSnapshot = null;
        PreDeblockDecodeIndex = -1;
        DiagDumpDetailMbAddr = -1;
        DiagFieldFlagSnapshot = null;
        DiagCabacBinTrace = false;
        DiagWatchBufferIndex = -1;
        DiagZeroResidualForPoc = -1;
        DiagDirectTracePoc = -1;
        DiagDirectTarget = null;
        // DeblockingFilter statics
        DeblockingFilter.DeblockCallCount = 0;
        DeblockingFilter.CurrentDeblockPoc = -1;
        DeblockingFilter.DiagMbAddr = -1;
        DeblockingFilter.DiagMbAddrSet = null;
        DeblockingFilter.DiagPoc = -1;
        DeblockingFilter.DiagDecodeCount = -1;
        DeblockingFilter.DiagOutput = null;
        DeblockingFilter.DiagPreDeblockLuma = null;
        DeblockingFilter.DiagPreDeblockStride = 0;
        DeblockingFilter.DiagPreDeblockMbX = 0;
        DeblockingFilter.DiagPreDeblockMbY = 0;
        // CabacDecoder statics
        CabacDecoder.TraceLog = null;
        CabacDecoder.TraceInitData = null;
        CabacDecoder.TraceInitContextStates = null;
        CabacDecoder.TraceMaxEntries = 50_000;
        // MotionCompensation statics
        MotionCompensation.McDiagnostic = null;
        MotionCompensation.McDiagTarget = null;
    }

    private static void ParseMbType(uint mbType, SliceType sliceType, ref Macroblock mb)
    {
        if (sliceType == SliceType.ISlice || sliceType == SliceType.SiSlice)
        {
            mb.IsIntra = true;
            if (mbType == 0)
                mb.IntraType = IntraMacroblockType.Intra4x4;
            else if (mbType <= 24)
                mb.IntraType = (IntraMacroblockType)mbType;
            else
                mb.IntraType = IntraMacroblockType.IntraPcm;
        }
        else
        {
            uint intraOffset = sliceType == SliceType.PSlice ? 5u : 23u;
            if (mbType >= intraOffset)
            {
                mb.IsIntra = true;
                uint intraType = mbType - intraOffset;
                mb.IntraType = intraType <= 24 ? (IntraMacroblockType)intraType : IntraMacroblockType.IntraPcm;
            }
            else
            {
                mb.IsIntra = false;
            }
        }
        
        // Parse I16x16 embedded CBP
        if (mb.IntraType >= IntraMacroblockType.Intra16x16_0_0_0 && mb.IntraType <= IntraMacroblockType.Intra16x16_3_2_1)
        {
            int i16x16Type = (int)mb.IntraType - 1;
            mb.Intra16x16PredMode = (Intra16x16PredictionMode)(i16x16Type % 4);
            mb.CodedBlockPatternChroma = (i16x16Type / 4) % 3;
            mb.CodedBlockPatternLuma = (i16x16Type / 12) > 0 ? 15 : 0;
        }
    }

    // Note: Full CABAC residual decoding requires DecodeResidualBlock with proper context management.
    // This is a placeholder that produces zero residuals.
    // See ITU-T H.264 Section 9.3 for full CABAC coefficient decoding specification.

    private bool isDiagnosticMb;
    private bool diagMvdBinTrace;

    /// <summary>
    /// Computes MB-level neighbor availability for intra prediction, accounting for both
    /// picture boundaries and slice boundaries. Matches FFmpeg's non-FMO behavior where
    /// cross-slice neighbors are blocked only when the topleft neighbor is also cross-slice.
    /// </summary>
    private void GetSliceAwareNeighborAvailability<TShape>(int mbX, int mbY, int mbWidth,
        out bool topAvailable, out bool leftAvailable, out bool topLeftAvailable)
        where TShape : struct, IPictureShape
    {
        int mbAddr = mbY * mbWidth + mbX;

        // MBAFF field-coded bottom MBs: the physical "above" pixel row accessed by
        // buffer[offset - stride] with doubled stride is 2 MB-rows up (same-parity
        // field from the previous pair), NOT 1 MB-row up (top of the current pair).
        // Use the physical row for slice boundary checks so intra prediction correctly
        // treats the previous pair's data as unavailable when cross-slice.
        bool isFieldBottom = TShape.Mbaff && mbFieldDecodingFlag != null
            && mbAddr < mbFieldDecodingFlag.Length && mbFieldDecodingFlag[mbAddr]
            && (mbY & 1) != 0;
        int aboveRow = isFieldBottom ? mbY - 2 : mbY - 1;

        // Picture boundary defaults
        topAvailable = aboveRow >= 0;
        leftAvailable = mbX > 0;
        topLeftAvailable = mbX > 0 && aboveRow >= 0;

        // Use per-MB slice index for cross-slice checks. This correctly handles MBAFF
        // where first_mb_in_slice is in pair units but MB addresses are in raster units.
        // Falls back to first_mb comparison for non-MBAFF (mbSliceIndex should always
        // be allocated, but guard defensively).
        if (mbSliceIndex != null)
        {
            int mySlice = mbSliceIndex[mbAddr];

            // FFmpeg non-FMO logic: top/left are blocked only if topleft is also cross-slice
            int topleftAddr = topLeftAvailable ? aboveRow * mbWidth + (mbX - 1) : -1;
            bool topleftCrossSlice = topleftAddr < 0 || mbSliceIndex[topleftAddr] != mySlice;

            if (topleftCrossSlice)
            {
                topLeftAvailable = false;
                if (topAvailable)
                {
                    int topAddr = aboveRow * mbWidth + mbX;
                    if (mbSliceIndex[topAddr] != mySlice)
                        topAvailable = false;
                }
                if (leftAvailable)
                {
                    int leftAddr = mbY * mbWidth + mbX - 1;
                    if (mbSliceIndex[leftAddr] != mySlice)
                        leftAvailable = false;
                }
            }
        }
        else
        {
            // Fallback: use first_mb_in_slice address comparison
            int firstMb = currentSliceHeader != null ? (int)currentSliceHeader.FirstMacroblockAddress : 0;
            int topleftAddr = topLeftAvailable ? aboveRow * mbWidth + (mbX - 1) : -1;
            bool topleftCrossSlice = topleftAddr < 0 || topleftAddr < firstMb;

            if (topleftCrossSlice)
            {
                topLeftAvailable = false;
                if (topAvailable)
                {
                    int topAddr = aboveRow * mbWidth + mbX;
                    if (topAddr < firstMb)
                        topAvailable = false;
                }
                if (leftAvailable)
                {
                    int leftAddr = mbY * mbWidth + mbX - 1;
                    if (leftAddr < firstMb)
                        leftAvailable = false;
                }
            }
        }
    }

    /// <summary>
    /// Picks the sample width and hands the macroblock to a body the JIT has already folded it
    /// into. One predictable test per macroblock buys the deletion of every wide-sample path
    /// from the eight-bit instantiation, and of every eight-bit path from the wide one.
    /// </summary>
    private void ReconstructMacroblock<TShape>(ref Macroblock mb, int mbX, int mbY, SequenceParameterSet sps, Span<short> residualLuma, Span<short> residualChroma)
        where TShape : struct, IPictureShape
    {
        if (pixelShift == 0)
            ReconstructMacroblockAtDepth<TShape, EightBitDepth>(ref mb, mbX, mbY, sps, residualLuma, residualChroma);
        else
            ReconstructMacroblockAtDepth<TShape, WideBitDepth>(ref mb, mbX, mbY, sps, residualLuma, residualChroma);
    }

    private void ReconstructMacroblockAtDepth<TShape, TDepth>(ref Macroblock mb, int mbX, int mbY, SequenceParameterSet sps, Span<short> residualLuma, Span<short> residualChroma)
        where TShape : struct, IPictureShape
        where TDepth : struct, IPixelDepth
    {
        if (currentFrameBuffer == null)
        {
            if (DiagnosticOutput != null && mb.Address == 0)
                DiagnosticOutput.WriteLine($"RECON-NULL-BUF: poc={currentPoc} mbAddr={mb.Address}");
            return;
        }
        if (DiagnosticOutput != null && mb.Address == 0)
            DiagnosticOutput.WriteLine($"RECON-ENTER: poc={currentPoc} dumpPoc={DiagnosticDumpAllMbsForPoc}");

        isDiagnosticMb = DiagnosticTargetMb.HasValue && DiagnosticTargetMb.Value.mbX == mbX && DiagnosticTargetMb.Value.mbY == mbY
            && (DiagnosticTargetMb.Value.frame < 0 || DiagnosticTargetMb.Value.frame == currentFrameNum)
            && (DiagCabacTracePoc < 0 || currentPoc == DiagCabacTracePoc);
        if (isDiagnosticMb && DiagnosticOutput != null)
        {
            DiagnosticOutput.WriteLine($"=== MB({mbX},{mbY}) frame={currentFrameNum} isIntra={mb.IsIntra} isSkip={mb.IsSkipped} type={mb.IntraType} t8x8={mb.Transform8x8} qp={mb.QuantizationParameter} cbpL={mb.CodedBlockPatternLuma} cbpC={mb.CodedBlockPatternChroma} ===");
            var sps2 = context.ActiveSps!;
            DiagnosticOutput.WriteLine($"  SPS: frameMbsOnly={sps2.FrameMacroblockOnly} mbAFF={sps2.MbAdaptiveFrameField} profile={sps2.Profile}");
            DiagnosticOutput.WriteLine($"  PIC: fieldPic={currentFieldPicture} pictStruct={currentPictureStructure} width={context.CodedWidth}");
        }
        
        if (DiagnosticDumpAllMbsForFrame == currentFrameNum && DiagnosticOutput != null)
        {
            string tag = mb.IsSkipped ? "SKIP" : mb.IsIntra ? $"I({mb.IntraType})" : "INTER";
            int scan8Pos = Scan8[0];
            short mvX = mvCacheL0X[scan8Pos], mvY = mvCacheL0Y[scan8Pos];
            short mvL1X = mvCacheL1X[scan8Pos], mvL1Y = mvCacheL1Y[scan8Pos];
            sbyte refIdx = mbRefIdxL0 != null ? mbRefIdxL0[mb.Address * 4] : (sbyte)-1;
            sbyte refIdxL1 = mbRefIdxL1 != null ? mbRefIdxL1[mb.Address * 4] : (sbyte)-1;
            int rawMbType = cabacMbTypes != null ? cabacMbTypes[mb.Address] : -999;
            DiagnosticOutput.WriteLine($"MB({mbX},{mbY}) {tag} rawType={rawMbType} t8x8={mb.Transform8x8} cbpL={mb.CodedBlockPatternLuma} qp={mb.QuantizationParameter} mvL0=({mvX},{mvY}) mvL1=({mvL1X},{mvL1Y}) refL0={refIdx} refL1={refIdxL1}");
        }

        // Detailed per-8x8 MV dump for a specific POC
        if (DiagnosticDumpAllMbsForPoc >= 0 && DiagnosticOutput != null && mb.Address == 0)
            DiagnosticOutput.WriteLine($"POC-CHECK: target={DiagnosticDumpAllMbsForPoc} current={currentPoc} match={DiagnosticDumpAllMbsForPoc == currentPoc}");
        if (DiagnosticDumpAllMbsForPoc == currentPoc && DiagnosticOutput != null)
        {
            string tag = mb.IsSkipped ? "SKIP" : mb.IsIntra ? $"INTRA({mb.IntraType})" : "INTER";
            int rawMbType = cabacMbTypes != null ? cabacMbTypes[mb.Address] : -999;
            bool isFieldMb = TShape.Mbaff && mbFieldDecodingFlag != null && mb.Address < mbFieldDecodingFlag.Length && mbFieldDecodingFlag[mb.Address];
            // Compute residual checksum (sum of absolute values) for coefficient comparison
            long residSum = 0;
            for (int ri = 0; ri < residualLuma.Length; ri++)
                residSum += Math.Abs(residualLuma[ri]);
            long residChromaSum = 0;
            for (int ri = 0; ri < residualChroma.Length; ri++)
                residChromaSum += Math.Abs(residualChroma[ri]);
            var sb = new System.Text.StringBuilder();
            // Compute weighted residual checksum to catch sign/position errors
            long residWSum = 0;
            for (int ri = 0; ri < residualLuma.Length; ri++)
                residWSum += residualLuma[ri] * (ri + 1);
            sb.Append($"MB({mbX},{mbY}) {tag} rawType={rawMbType} t8x8={mb.Transform8x8} cbpL={mb.CodedBlockPatternLuma} cbpC={mb.CodedBlockPatternChroma} fld={isFieldMb} qp={mb.QuantizationParameter} part={mb.InterPartitionMode} resL={residSum} resW={residWSum} resC={residChromaSum}");
            if (!mb.IsIntra && !mb.IsSkipped)
            {
                unsafe
                {
                    sb.Append($" sub=[{mb.InterSubMbTypes[0]},{mb.InterSubMbTypes[1]},{mb.InterSubMbTypes[2]},{mb.InterSubMbTypes[3]}]");
                }
            }
            if (!mb.IsIntra)
            {
                for (int i8 = 0; i8 < 4; i8++)
                {
                    int subPos = Scan8[i8 * 4];
                    short l0x = mvCacheL0X[subPos], l0y = mvCacheL0Y[subPos];
                    short l1x = mvCacheL1X[subPos], l1y = mvCacheL1Y[subPos];
                    sbyte rL0 = mbRefIdxL0 != null ? mbRefIdxL0[mb.Address * 4 + i8] : (sbyte)-1;
                    sbyte rL1 = mbRefIdxL1 != null ? mbRefIdxL1[mb.Address * 4 + i8] : (sbyte)-1;
                    sb.Append($" [{i8}]L0({l0x},{l0y})r{rL0} L1({l1x},{l1y})r{rL1}");
                }
                // Per-4x4 detail for B_8x8 (rawType=22) at the target MB
                if (rawMbType == 22 && mb.Address == DiagDumpDetailMbAddr)
                {
                    sb.AppendLine();
                    sb.Append("  4x4 detail:");
                    for (int blk = 0; blk < 16; blk++)
                    {
                        int s8 = Scan8[blk];
                        sb.Append($" [{blk}]L0({mvCacheL0X[s8]},{mvCacheL0Y[s8]}) L1({mvCacheL1X[s8]},{mvCacheL1Y[s8]})");
                    }
                }
                // Dump reference buffer pixels at (0,0) for MB 0 to verify correct reference
                if (mb.Address == 0)
                {
                    int lumaW = context.CodedWidth;
                    sbyte r0 = mbRefIdxL0 != null ? mbRefIdxL0[0] : (sbyte)-1;
                    sbyte r1 = mbRefIdxL1 != null ? mbRefIdxL1[0] : (sbyte)-1;
                    sb.AppendLine();
                    if (r0 >= 0 && r0 < refPicListL0Count)
                    {
                        var refBuf = refPicListL0[r0];
                        if (refBuf != null)
                            sb.Append($"  L0[{r0}] poc={refPicListL0Poc[r0]} first8pix: {refBuf[0]} {refBuf[1]} {refBuf[2]} {refBuf[3]} {refBuf[4]} {refBuf[5]} {refBuf[6]} {refBuf[7]}");
                    }
                    if (r1 >= 0 && r1 < refPicListL1Count)
                    {
                        var refBuf = refPicListL1[r1];
                        if (refBuf != null)
                            sb.Append($"  L1[{r1}] poc={refPicListL1Poc[r1]} first8pix: {refBuf[0]} {refBuf[1]} {refBuf[2]} {refBuf[3]} {refBuf[4]} {refBuf[5]} {refBuf[6]} {refBuf[7]}");
                    }
                }
            }
            DiagnosticOutput.WriteLine(sb.ToString());
        }

        // I_PCM: raw samples are in residualLuma/residualChroma for both CABAC and CAVLC paths.
        if (mb.IntraType == IntraMacroblockType.IntraPcm)
        {
            ReconstructPcmMacroblock<TDepth>(residualLuma, residualChroma, mbX, mbY);
            return;
        }

        int width = context.CodedWidth;
        int height = context.CodedHeight;
        int lumaStride = width;
        int chromaStride = width / 2;
        
        // Field pictures: double stride to write into alternating rows of the frame buffer.
        int fieldBaseOffsetLuma = 0;
        int fieldBaseOffsetChroma = 0;
        if (currentFieldPicture)
        {
            if (currentPictureStructure == ReferenceFrame.PictBottomField)
            {
                fieldBaseOffsetLuma = lumaStride;
                fieldBaseOffsetChroma = chromaStride;
            }
            lumaStride *= 2;
            chromaStride *= 2;
        }

        // MBAFF field MBs: double stride, adjust base for bottom field of pair.
        // Maps to FFmpeg h264_mb_template.c:65-96.
        // Field MB pair: top field writes even rows (stride*2), bottom field writes odd rows.
        // Offset computation uses original width for Y position, not doubled stride.
        bool isMbaffFieldMb = TShape.Mbaff && mbFieldDecodingFlag != null && mbFieldDecodingFlag[mb.Address];
        if (DiagWatchBufferIndex >= 0 && DiagnosticOutput != null && (mbY & 1) != 0 && mbY < 2)
        {
            DiagnosticOutput.WriteLine($"  FLAG-CHECK: mb={mb.Address} ({mbX},{mbY}) isMbaff={TShape.Mbaff} flagArr={mbFieldDecodingFlag != null} flagVal={mbFieldDecodingFlag?[mb.Address]} result={isMbaffFieldMb}");
        }
        int lumaYBase;
        int chromaYBase;
        if (isMbaffFieldMb)
        {
            lumaStride = width * 2;
            chromaStride = (width / 2) * 2;
            int pairTopRow = mbY & ~1;
            lumaYBase = pairTopRow * 16 * width;
            chromaYBase = pairTopRow * (16 / subHeightC) * (width / 2);
            if ((mbY & 1) != 0)
            {
                // Bottom field: one original-stride line down from pair start
                lumaYBase += width;
                chromaYBase += width / 2;
            }
        }
        else
        {
            lumaYBase = fieldBaseOffsetLuma + mbY * 16 * lumaStride;
            chromaYBase = fieldBaseOffsetChroma + mbY * (16 / subHeightC) * chromaStride;
        }

        var buffer = currentFrameBuffer.AsSpan();
        int lumaOffset = lumaYBase + mbX * 16;
        int chromaPlaneSize = (width / 2) * (height / subHeightC);
        int uOffset = width * height + chromaYBase + mbX * 8;
        int vOffset = uOffset + chromaPlaneSize;

        // MC stride diagnostic: print stride, field flags, and per-ref strides for target MB
        if (DiagMcStrideMb.HasValue && mbX == DiagMcStrideMb.Value.mbX && mbY == DiagMcStrideMb.Value.mbY && DiagnosticOutput != null)
        {
            DiagnosticOutput.WriteLine($"MC-STRIDE: poc={currentPoc} MB({mbX},{mbY}) addr={mb.Address} isIntra={mb.IsIntra} isSkip={mb.IsSkipped} " +
                $"fieldPic={currentFieldPicture} mbaff={TShape.Mbaff} mbaffField={isMbaffFieldMb} " +
                $"lumaStride={lumaStride} chromaStride={chromaStride} lumaYBase={lumaYBase} lumaOff={lumaOffset} " +
                $"sliceType={(currentSliceHeader?.IsBSlice == true ? "B" : currentSliceHeader?.IsPSlice == true ? "P" : "I")}");
            int s8 = Scan8[0];
            sbyte r0 = mbRefIdxL0?[mb.Address * 4] ?? -1;
            sbyte r1 = mbRefIdxL1?[mb.Address * 4] ?? -1;
            DiagnosticOutput.WriteLine($"  MV: L0=({mvCacheL0X[s8]},{mvCacheL0Y[s8]}) L1=({mvCacheL1X[s8]},{mvCacheL1Y[s8]}) refL0={r0} refL1={r1}");
            for (int ri = 0; ri < Math.Min(10, refPicListL0Count); ri++)
                DiagnosticOutput.WriteLine($"  L0[{ri}]: poc={refPicListL0Poc[ri]} stride={refPicListL0RefStride[ri]} offset={refPicListL0RefOffset[ri]} top={refPicListL0IsTopField[ri]}");
            for (int ri = 0; ri < Math.Min(10, refPicListL1Count); ri++)
                DiagnosticOutput.WriteLine($"  L1[{ri}]: poc={refPicListL1Poc[ri]} stride={refPicListL1RefStride[ri]} offset={refPicListL1RefOffset[ri]} top={refPicListL1IsTopField[ri]}");
        }

        // Field B-frame diagnostic: trace output setup for first 2 MBs
        if (DiagnosticOutput != null && DiagnosticDumpAllMbsForPoc > 0 
            && (currentPoc == DiagnosticDumpAllMbsForPoc || currentPoc == DiagnosticDumpAllMbsForPoc + 1)
            && currentFieldPicture && mb.Address < 2)
        {
            DiagnosticOutput.WriteLine($"FIELD-RECON: poc={currentPoc} struct={currentPictureStructure} mb={mb.Address} ({mbX},{mbY}) fieldBaseOff={fieldBaseOffsetLuma} lumaStride={lumaStride} lumaOff={lumaOffset} isMbaff={TShape.Mbaff} buf[0]={buffer[0]} buf[{width}]={buffer[width]} skip={mb.IsSkipped} intra={mb.IsIntra}");
            // Dump ref list L0 first 3 entries with offset/stride
            for (int ri = 0; ri < Math.Min(3, refPicListL0Count); ri++)
                DiagnosticOutput.WriteLine($"  REF-L0[{ri}]: poc={refPicListL0Poc[ri]} stride={refPicListL0RefStride[ri]} offset={refPicListL0RefOffset[ri]} isTop={refPicListL0IsTopField[ri]}");
            // Also dump L1 refs for B-slice diagnosis
            for (int ri = 0; ri < Math.Min(3, refPicListL1Count); ri++)
                DiagnosticOutput.WriteLine($"  REF-L1[{ri}]: poc={refPicListL1Poc[ri]} stride={refPicListL1RefStride[ri]} offset={refPicListL1RefOffset[ri]} isTop={refPicListL1IsTopField[ri]}");
            // Show MVs for skip/inter MBs
            if (mb.IsSkipped || !mb.IsIntra)
            {
                int s8 = Scan8[0];
                sbyte r0 = mbRefIdxL0?[mb.Address * 4] ?? -1;
                sbyte r1 = mbRefIdxL1?[mb.Address * 4] ?? -1;
                DiagnosticOutput.WriteLine($"  MV: L0=({mvCacheL0X[s8]},{mvCacheL0Y[s8]}) L1=({mvCacheL1X[s8]},{mvCacheL1Y[s8]}) refIdxL0={r0} refIdxL1={r1}");
                // Show reference pixel at MV position for skip MB verification
                if (mb.IsSkipped && r0 >= 0 && r0 < refPicListL0Count)
                {
                    int refOff0 = GetRefLumaOffset(r0, 0);
                    var refBuf0 = GetReferenceBuffer(r0, 0);
                    if (refBuf0 != null)
                    {
                        int srcY = mbY * 16 + (mvCacheL0Y[s8] >> 2);
                        int srcX = mbX * 16 + (mvCacheL0X[s8] >> 2);
                        srcY = Math.Clamp(srcY, 0, (currentFieldPicture ? height / 2 : height) - 1);
                        srcX = Math.Clamp(srcX, 0, width - 1);
                        int refPixOff = refOff0 + srcY * lumaStride + srcX;
                        if (refPixOff >= 0 && refPixOff + 3 < refBuf0.Length)
                            DiagnosticOutput.WriteLine($"  REFPIX-L0: offset={refOff0} srcPos=({srcX},{srcY}) pix=[{refBuf0[refPixOff]},{refBuf0[refPixOff+1]},{refBuf0[refPixOff+2]},{refBuf0[refPixOff+3]}]");
                    }
                }
                if (mb.IsSkipped && r1 >= 0 && r1 < refPicListL1Count)
                {
                    int refOff1 = GetRefLumaOffset(r1, 1);
                    var refBuf1 = GetReferenceBuffer(r1, 1);
                    if (refBuf1 != null)
                    {
                        int srcY = mbY * 16 + (mvCacheL1Y[s8] >> 2);
                        int srcX = mbX * 16 + (mvCacheL1X[s8] >> 2);
                        srcY = Math.Clamp(srcY, 0, (currentFieldPicture ? height / 2 : height) - 1);
                        int srcXc = Math.Clamp(srcX, 0, width - 1);
                        int refPixOff = refOff1 + srcY * lumaStride + srcXc;
                        if (refPixOff >= 0 && refPixOff + 3 < refBuf1.Length)
                            DiagnosticOutput.WriteLine($"  REFPIX-L1: offset={refOff1} srcPos=({srcXc},{srcY}) pix=[{refBuf1[refPixOff]},{refBuf1[refPixOff+1]},{refBuf1[refPixOff+2]},{refBuf1[refPixOff+3]}]");
                    }
                }
            }
        }

        // Trace stride for specific MBs to debug MBAFF stride/field mismatch
        if (DiagDumpDetailMbAddr >= 0 && DiagnosticOutput != null && DiagnosticDumpAllMbsForPoc == currentPoc
            && (mb.Address == DiagDumpDetailMbAddr || mb.Address == DiagDumpDetailMbAddr + context.WidthInMacroblocks))
        {
            DiagnosticOutput.WriteLine($"STRIDE-TRACE: mb={mb.Address} ({mbX},{mbY}) poc={currentPoc} isMbaffFieldMb={isMbaffFieldMb} isMbaff={TShape.Mbaff} lumaStride={lumaStride} width={width} lumaOff={lumaOffset} skip={mb.IsSkipped} intra={mb.IsIntra}");
        }

        if (isDiagnosticMb && DiagnosticOutput != null)
        {
            DiagnosticOutput.WriteLine($"  RECON: mb={mb.Address} ({mbX},{mbY}) lumaOff={lumaOffset} lumaStride={lumaStride} width={width} height={height} fieldPic={currentFieldPicture} mbaffField={isMbaffFieldMb} isMbaff={TShape.Mbaff} transform8x8={mb.Transform8x8} intraType={mb.IntraType}");
        }

        // Temporary MBAFF diagnostic: dump type/MV/ref for all MBs at a specific POC
        if (DiagnosticOutput != null && DiagMbGridPoc >= 0 || DiagMbGridPoc == -2)
        {
            bool matchesPoc = DiagMbGridPoc == -2 || currentPoc == DiagMbGridPoc;
            if (mb.Address == 0 && DiagnosticOutput != null)
                DiagnosticOutput.WriteLine($"MBGRID-CHECK: poc={currentPoc} target={DiagMbGridPoc} isMbaff={TShape.Mbaff} fieldPic={currentFieldPicture} match={matchesPoc}");
            if (matchesPoc && DiagnosticOutput != null)
            {
                string kind = mb.IsIntra ? "I" : mb.IsSkipped ? "S" : "P";
                int s8 = Scan8[0];
                short mvx = mvCacheL0X[s8], mvy = mvCacheL0Y[s8];
                int ref0 = mbRefIdxL0 != null ? mbRefIdxL0[mb.Address * 4] : -99;
                int refL1_0 = mbRefIdxL1 != null ? mbRefIdxL1[mb.Address * 4] : -99;
                short mvL1x = mvCacheL1X[s8], mvL1y = mvCacheL1Y[s8];
                DiagnosticOutput.WriteLine($"MBGRID ({mbX,2},{mbY,2}) addr={mb.Address} poc={currentPoc} {kind} field={isMbaffFieldMb} fldPic={currentFieldPicture} refL0={ref0} mvL0=({mvx},{mvy}) refL1={refL1_0} mvL1=({mvL1x},{mvL1y}) lumaOff={lumaOffset} stride={lumaStride} part={mb.InterPartitionMode}");
                // Dump all 16 scan8 MVs for inter MBs to see partition-level detail
                if (!mb.IsIntra)
                {
                    var mvSb = new System.Text.StringBuilder("  MVL0: ");
                    for (int b = 0; b < 16; b++)
                    {
                        int sp = Scan8[b];
                        mvSb.Append($"[{b}]=({mvCacheL0X[sp]},{mvCacheL0Y[sp]}) ");
                    }
                    DiagnosticOutput.WriteLine(mvSb.ToString());
                    if (mbRefIdxL0 != null)
                    {
                        int addr4 = mb.Address * 4;
                        DiagnosticOutput.WriteLine($"  REFL0: [{mbRefIdxL0[addr4]},{mbRefIdxL0[addr4+1]},{mbRefIdxL0[addr4+2]},{mbRefIdxL0[addr4+3]}]");
                    }
                    if (mbRefIdxL1 != null)
                    {
                        int addr4 = mb.Address * 4;
                        DiagnosticOutput.WriteLine($"  REFL1: [{mbRefIdxL1[addr4]},{mbRefIdxL1[addr4+1]},{mbRefIdxL1[addr4+2]},{mbRefIdxL1[addr4+3]}]");
                    }
                }
            }
        }

#if MK_STAGE_TIMING
        long _sub = Stopwatch.GetTimestamp();
#endif
        if (mb.IsIntra)
        {
            ReconstructIntraMacroblock<TShape, TDepth>(ref mb, buffer, lumaOffset, lumaStride, uOffset, vOffset, chromaStride, chromaYBase, residualLuma, residualChroma);
#if MK_STAGE_TIMING
            StageIntraTicks += Stopwatch.GetTimestamp() - _sub; StageIntraCount++;
#endif
        }
        else if (mb.IsSkipped)
        {
            ReconstructSkippedMacroblock<TShape, TDepth>(buffer, lumaOffset, lumaStride, uOffset, vOffset, chromaStride, mbX, mbY);
#if MK_STAGE_TIMING
            StageSkipTicks += Stopwatch.GetTimestamp() - _sub; StageSkipCount++;
#endif
        }
        else
        {
            ReconstructInterMacroblock<TShape, TDepth>(ref mb, buffer, lumaOffset, lumaStride, uOffset, vOffset, chromaStride, residualLuma, residualChroma);
#if MK_STAGE_TIMING
            StageInterTicks += Stopwatch.GetTimestamp() - _sub; StageInterCount++;
#endif
        }
        
        // Targeted post-write verification: check buffer[22896] for FREXT4 debugging
        if ((DiagnosticDumpAllMbsForPoc == currentPoc || DiagnosticDumpAllMbsForPoc == currentPoc - 1) && DiagnosticOutput != null)
        {
            int checkPos = 22896; // frame row 65, col 16
            if (checkPos < buffer.Length)
                DiagnosticOutput.WriteLine($"  VERIFY poc={currentPoc} MB({mbX},{mbY}) addr={mb.Address}: buf[{checkPos}]={buffer[checkPos]} skip={mb.IsSkipped} intra={mb.IsIntra} fld={isMbaffFieldMb} fieldPic={currentFieldPicture}");}

        // Post-reconstruction trace: verify what was written for field B-frame MB(0,0)
        if (DiagnosticOutput != null && DiagnosticDumpAllMbsForPoc > 0
            && (currentPoc == DiagnosticDumpAllMbsForPoc || currentPoc == DiagnosticDumpAllMbsForPoc + 1)
            && currentFieldPicture && mb.Address == 0)
        {
            // Show first 8 pixels of frame rows 0 and 1 (even=top field, odd=bottom field)
            var sb = new System.Text.StringBuilder("  POST-RECON: ");
            sb.Append($"row0=[");
            for (int i = 0; i < 8; i++) sb.Append($"{buffer[i]},");
            sb.Append($"] row1=[");
            for (int i = 0; i < 8; i++) sb.Append($"{buffer[width + i]},");
            sb.Append($"] lumaOff={lumaOffset} fieldBaseOff={fieldBaseOffsetLuma}");
            DiagnosticOutput.WriteLine(sb.ToString());
        }

        // Buffer watchpoint: log value after each MB reconstruction
        if (DiagWatchBufferIndex >= 0 && DiagnosticOutput != null && buffer.Length > DiagWatchBufferIndex)
        {
            DiagnosticOutput.WriteLine($"  WATCH: after mb={mb.Address} ({mbX},{mbY}) field={isMbaffFieldMb} lumaOff={lumaOffset} stride={lumaStride} buffer[{DiagWatchBufferIndex}]={buffer[DiagWatchBufferIndex]}");
        }
    }

    /// <summary>
    /// Writes I_PCM raw samples from CAVLC residual buffers directly to the frame buffer.
    /// residualLuma contains 256 raw luma samples, residualChroma contains 64 Cb + 64 Cr samples.
    /// </summary>
    private void ReconstructPcmMacroblock<TDepth>(Span<short> residualLuma, Span<short> residualChroma, int mbX, int mbY)
        where TDepth : struct, IPixelDepth
    {
        if (currentFrameBuffer == null) return;

        // Diagnostic: verify PCM data is in the buffer
        if (DiagnosticOutput != null)
        {
            DiagnosticOutput.Write($"[PCM_RECON] MB({mbX},{mbY}) first16:");
            for (int i = 0; i < 16; i++)
                DiagnosticOutput.Write($" {residualLuma[i]}");
            DiagnosticOutput.WriteLine();
        }

        int width = context.CodedWidth;
        int height = context.CodedHeight;
        int lumaStride = width;
        int chromaStride = width / 2;
        int fieldBaseOffsetLuma = 0;
        int fieldBaseOffsetChroma = 0;
        if (currentFieldPicture)
        {
            if (currentPictureStructure == ReferenceFrame.PictBottomField)
            {
                fieldBaseOffsetLuma = lumaStride;
                fieldBaseOffsetChroma = chromaStride;
            }
            lumaStride *= 2;
            chromaStride *= 2;
        }
        int lumaOffset = fieldBaseOffsetLuma + mbY * 16 * lumaStride + mbX * 16;
        int chromaPlaneSize = (width / 2) * (height / subHeightC);
        int chromaHeightPerMb = 16 / subHeightC; // 8 for 4:2:0, 16 for 4:2:2
        int uOffset = width * height + fieldBaseOffsetChroma + mbY * chromaHeightPerMb * chromaStride + mbX * 8;
        int vOffset = uOffset + chromaPlaneSize;

        var buffer = currentFrameBuffer.AsSpan();

        // Write 16x16 luma samples
        for (int row = 0; row < 16; row++)
        {
            int srcOffset = row * 16;
            int dstOffset = lumaOffset + row * lumaStride;
            for (int col = 0; col < 16; col++)
                WritePixel<TDepth>(buffer, dstOffset + col, residualLuma[srcOffset + col]);
        }

        // Verify write
        if (DiagnosticOutput != null && mbX == 0 && mbY == 0)
        {
            DiagnosticOutput.Write($"[PCM_VERIFY] buffer[0..15]:");
            for (int i = 0; i < 16; i++)
                DiagnosticOutput.Write($" {buffer[i]}");
            DiagnosticOutput.WriteLine();
        }

        // Write 8x8 Cb and Cr samples (skip for monochrome — chroma stays at 128)
        if (context.ActiveSps!.ChromaFormat != ChromaFormat.Monochrome)
        {
            for (int row = 0; row < 8; row++)
            {
                int cbSrc = row * 8;
                int crSrc = 64 + row * 8;
                int cbDst = uOffset + row * chromaStride;
                int crDst = vOffset + row * chromaStride;
                for (int col = 0; col < 8; col++)
                {
                    WritePixel<TDepth>(buffer, cbDst + col, residualChroma[cbSrc + col]);
                    WritePixel<TDepth>(buffer, crDst + col, residualChroma[crSrc + col]);
                }
            }
        }
    }

    private void ReconstructIntraMacroblock<TShape, TDepth>(ref Macroblock mb, Span<byte> buffer, int lumaOffset, int lumaStride, int uOffset, int vOffset, int chromaStride, int chromaYBase, Span<short> residualLuma, Span<short> residualChroma)
        where TShape : struct, IPictureShape

        where TDepth : struct, IPixelDepth    {
        // Compute slice-boundary-aware neighbor availability (matches FFmpeg behavior)
        int mbWidth = context.CodedWidth / 16;
        int pixelWidth = context.CodedWidth;
        GetSliceAwareNeighborAvailability<TShape>(mb.PositionX, mb.PositionY, mbWidth,
            out bool sliceTopAvail, out bool sliceLeftAvail, out bool sliceTopLeftAvail);

        // H.264 spec §8.3.1.2: when constrained_intra_pred_flag=1, samples from
        // inter-coded neighbor MBs are treated as unavailable for intra prediction.
        // For MBAFF cross-type pairs (field MB next to frame-coded pair or vice versa),
        // the left column's 16 rows span TWO MBs in the left pair. Each half has
        // independent CIP availability: rows 0-7 from the left pair's top MB,
        // rows 8-15 from the bottom MB.
        bool cipLeftTopHalfUnavail = false;
        bool cipLeftBottomHalfUnavail = false;
        int cipAboveRightRow = -2; // -2 = not computed (non-CIP default)
        if (constrainedIntraPred && mbIsIntra != null)
        {
            int mbAddr = mb.PositionY * mbWidth + mb.PositionX;

            if (sliceLeftAvail && mb.PositionX > 0)
            {
                if (TShape.Mbaff && mbFieldDecodingFlag != null)
                {
                    bool currentIsField = mbFieldDecodingFlag[mb.Address];
                    int leftPairTopRow = mb.PositionY & ~1;
                    int leftTopAddr = leftPairTopRow * mbWidth + (mb.PositionX - 1);
                    int leftBotAddr = leftTopAddr + mbWidth;
                    bool leftIsField = mbFieldDecodingFlag[leftTopAddr];

                    if (currentIsField != leftIsField)
                    {
                        bool leftTopIntra = mbIsIntra[leftTopAddr];
                        bool leftBotIntra = mbIsIntra[leftBotAddr];

                        if (currentIsField)
                        {
                            // Field MB → frame pair: per-half CIP.
                            // Left rows 0-7 from left pair top MB, rows 8-15 from bottom MB.
                            if (!leftTopIntra && !leftBotIntra)
                            {
                                sliceLeftAvail = false;
                            }
                            else
                            {
                                if (!leftTopIntra) cipLeftTopHalfUnavail = true;
                                if (!leftBotIntra) cipLeftBottomHalfUnavail = true;
                            }
                        }
                        else
                        {
                            // Frame MB → field pair: ALL-or-NOTHING.
                            // Left pixels interleave per-row between field MBs (even rows
                            // from top field, odd rows from bottom field). Can't split cleanly.
                            // FFmpeg: if EITHER field MB is inter → all left unavailable.
                            if (!leftTopIntra || !leftBotIntra)
                                sliceLeftAvail = false;
                        }
                    }
                    else
                    {
                        // Same type: only same-row left MB contributes
                        if (!mbIsIntra[mbAddr - 1])
                            sliceLeftAvail = false;
                    }
                }
                else
                {
                    if (!mbIsIntra[mbAddr - 1])
                        sliceLeftAvail = false;
                }
            }

            // FFmpeg's fill_decode_neighbors: top_xy = mb_xy - (mb_stride << MB_FIELD)
            // For field-coded top-of-pair MBs, FFmpeg independently adjusts top_xy,
            // topleft_xy, and topright_xy based on whether the above pair at EACH
            // column is field-coded or frame-coded. Different pairs can have different
            // coding, so each above-neighbor column needs its own CIP row.
            int cipAboveRow;
            int cipAboveLeftRow;
            if (TShape.Mbaff && mbFieldDecodingFlag != null && mbFieldDecodingFlag[mb.Address])
            {
                if ((mb.PositionY & 1) != 0)
                {
                    // Bottom field: above is always y-2 for all directions
                    cipAboveRow = mb.PositionY - 2;
                    cipAboveLeftRow = mb.PositionY - 2;
                    cipAboveRightRow = mb.PositionY - 2;
                }
                else
                {
                    // Top field: each above-neighbor uses the above pair at its column.
                    // If that pair is frame-coded → y-1, if field-coded → y-2.
                    int abovePairTopY = mb.PositionY - 2;

                    // Top (same column)
                    if (abovePairTopY >= 0)
                    {
                        bool aboveIsField = mbFieldDecodingFlag[abovePairTopY * mbWidth + mb.PositionX];
                        cipAboveRow = aboveIsField ? mb.PositionY - 2 : mb.PositionY - 1;
                    }
                    else
                        cipAboveRow = -1;

                    // TopLeft (column - 1)
                    if (abovePairTopY >= 0 && mb.PositionX > 0)
                    {
                        bool aboveLeftIsField = mbFieldDecodingFlag[abovePairTopY * mbWidth + mb.PositionX - 1];
                        cipAboveLeftRow = aboveLeftIsField ? mb.PositionY - 2 : mb.PositionY - 1;
                    }
                    else
                        cipAboveLeftRow = -1;

                    // TopRight (column + 1)
                    if (abovePairTopY >= 0 && mb.PositionX < mbWidth - 1)
                    {
                        bool aboveRightIsField = mbFieldDecodingFlag[abovePairTopY * mbWidth + mb.PositionX + 1];
                        cipAboveRightRow = aboveRightIsField ? mb.PositionY - 2 : mb.PositionY - 1;
                    }
                    else
                        cipAboveRightRow = -1;
                }
            }
            else
            {
                cipAboveRow = mb.PositionY - 1;
                cipAboveLeftRow = mb.PositionY - 1;
                cipAboveRightRow = mb.PositionY - 1;
            }

            if (sliceTopAvail && cipAboveRow >= 0 && !mbIsIntra[cipAboveRow * mbWidth + mb.PositionX])
                sliceTopAvail = false;
            if (sliceTopLeftAvail && mb.PositionX > 0 && cipAboveLeftRow >= 0 && !mbIsIntra[cipAboveLeftRow * mbWidth + mb.PositionX - 1])
                sliceTopLeftAvail = false;
        }

        // Get neighbor pixels for intra prediction (using slice-aware availability)
        Scratch.Of17<int> topStorage = default;
        Span<int> top = topStorage;
        Scratch.Of16<int> leftStorage = default;
        Span<int> left = leftStorage;
        GetLumaNeighbors<TDepth>(buffer, lumaOffset, lumaStride, pixelWidth, top, left, sliceTopAvail, sliceLeftAvail, sliceTopLeftAvail);

        // Luma reconstruction
        bool isTraceMb = DiagTraceMbX == mb.PositionX && DiagTraceMbY == mb.PositionY && (DiagTraceFrame < 0 || DeblockingFilter.DeblockCallCount == DiagTraceFrame);
        if (isTraceMb)
            TraceIntraMb(mb.PositionX, mb.PositionY, mb.IntraType, mb.QuantizationParameter,
                TDepth.PixelShift, mb.Transform8x8, lumaOffset, lumaStride, top, left);

        // Outlined for the same reason as TraceIntra8x8 further down: this one runs once per
        // intra macroblock, and there are 891,015 of those in the 1080p clip.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void TraceIntraMb(int mbX, int mbY, IntraMacroblockType intraType, int qp,
            int pixelShift, bool transform8x8, int lumaOffset, int lumaStride,
            ReadOnlySpan<int> top, ReadOnlySpan<int> left)
        {
            Console.WriteLine($"\n=== RECON TRACE MB({mbX},{mbY}) frame={DeblockingFilter.DeblockCallCount} ===");
            Console.WriteLine($"  IntraType={intraType} QP={qp} TDepth.PixelShift={pixelShift} Transform8x8={transform8x8}");
            Console.WriteLine($"  lumaOffset={lumaOffset} stride={lumaStride}");
            Console.Write($"  Top[0..4]:");
            for (int ti = 0; ti <= 4; ti++) Console.Write($" {top[ti]}");
            Console.Write($"\n  Left[0..3]:");
            for (int ti = 0; ti < 4; ti++) Console.Write($" {left[ti]}");
            Console.WriteLine();
        }
        if (mb.IntraType == IntraMacroblockType.Intra4x4)
        {
            if (mb.Transform8x8)
                ReconstructIntra8x8Luma<TShape, TDepth>(ref mb, buffer, lumaOffset, lumaStride, pixelWidth, top, left, residualLuma, sliceTopAvail, sliceLeftAvail, sliceTopLeftAvail, cipLeftTopHalfUnavail, cipLeftBottomHalfUnavail, cipAboveRightRow);
            else
                ReconstructIntra4x4Luma<TShape, TDepth>(ref mb, buffer, lumaOffset, lumaStride, pixelWidth, top, left, residualLuma, sliceTopAvail, sliceLeftAvail, sliceTopLeftAvail, cipLeftTopHalfUnavail, cipLeftBottomHalfUnavail, cipAboveRightRow);
        }
        else if (mb.IntraType >= IntraMacroblockType.Intra16x16_0_0_0 && mb.IntraType <= IntraMacroblockType.Intra16x16_3_2_1)
        {
            // I16x16 uses single left availability flag (FFmpeg bit 15+7 check).
            // If either CIP half is unavailable, treat left as fully unavailable.
            bool i16x16LeftAvail = sliceLeftAvail && !cipLeftTopHalfUnavail && !cipLeftBottomHalfUnavail;
            ReconstructIntra16x16Luma<TDepth>(ref mb, buffer, lumaOffset, lumaStride, top, left, residualLuma, sliceTopAvail, i16x16LeftAvail);
        }

        // Chroma reconstruction with proper intra prediction and dequantization
        // For monochrome (ChromaArrayType == 0), chroma planes stay at 128 (pre-initialized)
        if (context.ActiveSps!.ChromaFormat != ChromaFormat.Monochrome)
            ReconstructChroma<TDepth>(ref mb, buffer, uOffset, vOffset, chromaStride, chromaYBase, residualChroma, sliceTopAvail, sliceLeftAvail, cipLeftTopHalfUnavail, cipLeftBottomHalfUnavail);
    }

    private void GetLumaNeighbors<TDepth>(Span<byte> buffer, int lumaOffset, int stride, int pixelWidth, Span<int> top, Span<int> left,
        bool sliceTopAvail, bool sliceLeftAvail, bool sliceTopLeftAvail)
        where TDepth : struct, IPixelDepth
    {
        bool hasTop = lumaOffset >= stride && sliceTopAvail;
        bool hasLeft = (lumaOffset % pixelWidth) > 0 && sliceLeftAvail;
        bool hasTopLeft = lumaOffset > stride && (lumaOffset % pixelWidth) > 0 && sliceTopLeftAvail;

        // Top-left corner
        top[0] = hasTopLeft ? ReadPixel<TDepth>(buffer, lumaOffset - stride - 1) : neutralChroma;

        // Top row
        if (hasTop)
        {
            for (int i = 0; i < 16; i++)
            {
                top[i + 1] = ReadPixel<TDepth>(buffer, lumaOffset - stride + i);
            }
        }
        else
        {
            for (int i = 1; i <= 16; i++) top[i] = neutralChroma;
        }

        // Left column
        for (int i = 0; i < 16; i++)
        {
            left[i] = hasLeft ? ReadPixel<TDepth>(buffer, lumaOffset - 1 + i * stride) : neutralChroma;
        }
    }

    private unsafe void ReconstructIntra4x4Luma<TShape, TDepth>(ref Macroblock mb, Span<byte> buffer, int lumaOffset, int stride, int pixelWidth, Span<int> mbTop, Span<int> mbLeft, Span<short> residual,
        bool sliceTopAvail, bool sliceLeftAvail, bool sliceTopLeftAvail, bool cipLeftTopHalfUnavail = false, bool cipLeftBottomHalfUnavail = false, int cipAboveRightRow = -2)
        where TShape : struct, IPictureShape

        where TDepth : struct, IPixelDepth    {
        Scratch.Of16<int> predStorage = default;
        Span<int> pred = predStorage;
        Scratch.Of9<int> blockTopStorage = default;
        Span<int> blockTop = blockTopStorage;  // Top-left + 4 top + 4 top-right
        Scratch.Of4<int> blockLeftStorage = default;
        Span<int> blockLeft = blockLeftStorage;
        
        bool mbTopAvailable = lumaOffset >= stride && sliceTopAvail;
        bool mbLeftAvailable = (lumaOffset % pixelWidth) > 0 && sliceLeftAvail;

        // Pre-allocate transform buffers outside the loop based on bit depth
        Scratch.Of16<int> coeffsWideStorage = default;
        Span<int> coeffsWide = TDepth.PixelShift > 0 ? (Span<int>)coeffsWideStorage : Span<int>.Empty;
        Scratch.Of16<int> transformedWideStorage = default;
        Span<int> transformedWide = TDepth.PixelShift > 0 ? (Span<int>)transformedWideStorage : Span<int>.Empty;
        Scratch.Of16<short> coeffs8Storage = default;
        Span<short> coeffs8 = TDepth.PixelShift == 0 ? (Span<short>)coeffs8Storage : Span<short>.Empty;
        Scratch.Of16<short> transformed8Storage = default;
        Span<short> transformed8 = TDepth.PixelShift == 0 ? (Span<short>)transformed8Storage : Span<short>.Empty;
        bool isTraceMb = DiagTraceMbX == mb.PositionX && DiagTraceMbY == mb.PositionY && (DiagTraceFrame < 0 || DeblockingFilter.DeblockCallCount == DiagTraceFrame);

        // Diagnostic scratch, hoisted: a stackalloc inside the loop is allocated per iteration
        // and none of them come back until the method returns.
        Scratch.Of16<short> dequantCoeffsStorage = default;
        Span<short> dequantCoeffs = dequantCoeffsStorage;

        for (int blkIdx = 0; blkIdx < 16; blkIdx++)
        {
            // Map z-scan block index to spatial position
            int blkX = (((blkIdx >> 2) & 1) * 2 + (blkIdx & 1)) * 4;
            int blkY = ((blkIdx >> 3) * 2 + ((blkIdx >> 1) & 1)) * 4;
            int predMode = mb.Intra4x4PredModes[blkIdx];

            // Get local neighbors for this 4x4 block
            GetBlock4x4Neighbors<TDepth>(buffer, lumaOffset, stride, pixelWidth, blkX, blkY, mbTop, mbLeft, blockTop, blockLeft);

            // H.264 §8.3.1.2: constrained intra prediction — when the top-right
            // neighbor MB is inter-coded, its samples are treated as unavailable.
            if (constrainedIntraPred && mbIsIntra != null && blkY == 0 && blkX + 4 >= 16)
            {
                int mbX = mb.PositionX;
                int mbW = context.CodedWidth / 16;

                int trRow = cipAboveRightRow;
                if (trRow == -2)
                    trRow = mb.PositionY - 1;

                if (mbX < mbW - 1 && trRow >= 0 && !mbIsIntra[(trRow * mbW) + mbX + 1])
                {
                    for (int i = 0; i < 4; i++)
                        blockTop[i + 5] = blockTop[4];
                }
            }

            bool isFrameCodedBottomOfPair = TShape.Mbaff && (mb.PositionY & 1) != 0
                && mbFieldDecodingFlag != null && !mbFieldDecodingFlag[mb.Address];
            if (isFrameCodedBottomOfPair && blkY == 0 && blkX + 4 >= 16)
            {
                for (int i = 0; i < 4; i++)
                    blockTop[i + 5] = blockTop[4];
            }

            bool blockTopAvail = blkY > 0 || mbTopAvailable;
            bool blockLeftAvail = blkX > 0 || mbLeftAvailable;

            if (blkX == 0 && blockLeftAvail)
            {
                if (cipLeftTopHalfUnavail && blkY < 8)
                    blockLeftAvail = false;
                if (cipLeftBottomHalfUnavail && blkY >= 8)
                    blockLeftAvail = false;
            }

            Intra4x4Predict(pred, predMode, blockTop, blockLeft, blockTopAvail, blockLeftAvail);

            int blockOffset = lumaOffset + blkY * stride + blkX;

            // Same reasoning as the inter path: dequantising zeros gives zeros, the inverse
            // transform of zeros is zeros, and adding zero leaves the prediction. FFmpeg reads
            // this out of non_zero_count_cache and calls no idct at all (h264_mb.c:697).
            if (!residual.Slice(blkIdx * 16, 16).ContainsAnyExcept((short)0))
            {
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                        WritePixel<TDepth>(buffer, blockOffset + y * stride + x, pred[y * 4 + x]);

                continue;
            }

            // Loaded per bit-depth arm below, straight out of the residual array.
            var pps4x4 = context.ActivePps!;

            if (TDepth.PixelShift > 0)
            {
                // 10-bit path: int32 coefficient pipeline
                LoadBlockCoefficients(residual, blkIdx * 16, coeffsWide);

                H264Transform.Dequantize4x4WithMatrix(coeffsWide, pps4x4.ScalingMatrix4[0], mb.QuantizationParameter);
                H264Transform.InverseTransform4x4(coeffsWide, transformedWide);

                if (isTraceMb && blkIdx == 0)
                {
                    Console.WriteLine($"  Intra4x4 blk0: predMode={predMode} QP={mb.QuantizationParameter}");
                    Console.Write($"    pred[col0]:");
                    for (int r = 0; r < 4; r++) Console.Write($" {pred[r * 4]}");
                    Console.Write($"\n    dequant[0..3]:");
                    for (int k = 0; k < 4; k++) Console.Write($" {coeffsWide[k]}");
                    Console.Write($"\n    idct[col0]:");
                    for (int r = 0; r < 4; r++) Console.Write($" {transformedWide[r * 4]}");
                    Console.Write($"\n    final[col0]:");
                    for (int r = 0; r < 4; r++) Console.Write($" {ClipPixel<TDepth>(pred[r * 4] + transformedWide[r * 4])}");
                    Console.WriteLine();
                }

                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int val = pred[y * 4 + x] + transformedWide[y * 4 + x];
                        WritePixel<TDepth>(buffer, blockOffset + y * stride + x, ClipPixel<TDepth>(val));
                    }
                }
            }
            else
            {
                // 8-bit path: original short pipeline
                LoadBlockCoefficients(residual, blkIdx * 16, coeffs8);

                // Already dequantised at the parser's store, against list 0 for an intra
                // macroblock. The wide path above still does it here - see the inter path.

                if (isDiagnosticMb) coeffs8.CopyTo(dequantCoeffs);

                H264Transform.InverseTransform4x4(coeffs8, transformed8);

                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int val = pred[y * 4 + x] + transformed8[y * 4 + x];
                        WritePixel<TDepth>(buffer, blockOffset + y * stride + x, ClipPixel<TDepth>(val));
                    }
                }

                if (isDiagnosticMb && DiagnosticOutput != null)
                    TraceIntra4x4(DiagnosticOutput, blkIdx, blkX, blkY, predMode, mb.QuantizationParameter,
                        blockTop, blockLeft, residual.Slice(blkIdx * 16, 16), dequantCoeffs, pred,
                        transformed8, buffer, blockOffset, stride);

                // Outlined for the same reason as TraceIntra8x8 below.
                [MethodImpl(MethodImplOptions.NoInlining)]
                static void TraceIntra4x4(System.IO.TextWriter output, int blkIdx, int blkX, int blkY,
                    int predMode, int qp, ReadOnlySpan<int> blockTop, ReadOnlySpan<int> blockLeft,
                    ReadOnlySpan<short> scanCoeffs, ReadOnlySpan<short> dequantCoeffs,
                    ReadOnlySpan<int> pred, ReadOnlySpan<short> transformed8,
                    ReadOnlySpan<byte> buffer, int blockOffset, int stride)
                {
                    output.Write($"  blk{blkIdx} ({blkX},{blkY}) mode={predMode} qp={qp} top=[");
                    for (int i = 0; i < 9; i++) output.Write($"{blockTop[i]}{(i<8?",":"")}");
                    output.Write($"] left=[");
                    for (int i = 0; i < 4; i++) output.Write($"{blockLeft[i]}{(i<3?",":"")}");
                    output.WriteLine("]");
                    output.Write($"    scanCoeffs=[");
                    for (int i = 0; i < 16; i++) output.Write($"{scanCoeffs[i]}{(i<15?",":"")}");
                    output.WriteLine("]");
                    output.Write($"    dequant=[");
                    for (int i = 0; i < 16; i++) output.Write($"{dequantCoeffs[i]}{(i<15?",":"")}");
                    output.WriteLine("]");
                    output.Write($"    pred=[");
                    for (int i = 0; i < 16; i++) output.Write($"{pred[i]}{(i<15?",":"")}");
                    output.WriteLine("]");
                    output.Write($"    idct=[");
                    for (int i = 0; i < 16; i++) output.Write($"{transformed8[i]}{(i<15?",":"")}");
                    output.WriteLine("]");
                    output.Write($"    recon=[");
                    for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) output.Write($"{buffer[blockOffset + y * stride + x]}{(y*4+x<15?",":"")}");
                    output.WriteLine("]");
                }
            }
        }
    }

    /// <summary>
    /// Reconstructs luma for intra 8x8 prediction mode (High Profile).
    /// Same 9 modes as intra 4x4, but applied on 8x8 blocks with filtered reference samples.
    /// </summary>
    private unsafe void ReconstructIntra8x8Luma<TShape, TDepth>(ref Macroblock mb, Span<byte> buffer, int lumaOffset, int stride, int pixelWidth, Span<int> mbTop, Span<int> mbLeft, Span<short> residual,
        bool sliceTopAvail, bool sliceLeftAvail, bool sliceTopLeftAvail, bool cipLeftTopHalfUnavail = false, bool cipLeftBottomHalfUnavail = false, int cipAboveRightRow = -2)
        where TShape : struct, IPictureShape

        where TDepth : struct, IPixelDepth    {
        Scratch.Of64<int> pred8x8Storage = default;
        Span<int> pred8x8 = pred8x8Storage;
        Scratch.Of25<int> blockTopStorage = default;
        Span<int> blockTop = blockTopStorage; // top-left + 8 top + 8 top-right + padding
        Scratch.Of8<int> blockLeftStorage = default;
        Span<int> blockLeft = blockLeftStorage;

        // Pre-allocate transform buffers based on bit depth
        Scratch.Of64<int> coeffsWideStorage = default;
        Span<int> coeffsWide = TDepth.PixelShift > 0 ? (Span<int>)coeffsWideStorage : Span<int>.Empty;
        Scratch.Of64<int> transformedWideStorage = default;
        Span<int> transformedWide = TDepth.PixelShift > 0 ? (Span<int>)transformedWideStorage : Span<int>.Empty;
        Scratch.Of64<short> coeffs8x8Storage = default;
        Span<short> coeffs8x8 = TDepth.PixelShift == 0 ? (Span<short>)coeffs8x8Storage : Span<short>.Empty;
        Scratch.Of64<short> transformed8x8Storage = default;
        Span<short> transformed8x8 = TDepth.PixelShift == 0 ? (Span<short>)transformed8x8Storage : Span<short>.Empty;

        int mbPixelX = lumaOffset % pixelWidth;
        bool mbTopAvailable = lumaOffset >= stride && sliceTopAvail;
        bool mbLeftAvailable = mbPixelX > 0 && sliceLeftAvail;

        for (int i8x8 = 0; i8x8 < 4; i8x8++)
        {
            int blk8x = (i8x8 & 1) * 8;
            int blk8y = (i8x8 >> 1) * 8;
            int predMode = mb.Intra4x4PredModes[i8x8 * 4];
            int blockOffset = lumaOffset + blk8y * stride + blk8x;

            // Gather reference samples for this 8x8 block
            bool blockTopAvail = blk8y > 0 || mbTopAvailable;
            bool blockLeftAvail = blk8x > 0 || mbLeftAvailable;

            // CIP cross-type: per-half left column availability for 8x8 blocks
            if (blk8x == 0 && blockLeftAvail)
            {
                if (cipLeftTopHalfUnavail && blk8y < 8)
                    blockLeftAvail = false;
                if (cipLeftBottomHalfUnavail && blk8y >= 8)
                    blockLeftAvail = false;
            }

            // Top-left
            if (blk8x == 0 && blk8y == 0)
                blockTop[0] = mbTop[0];
            else if (blk8y == 0)
                blockTop[0] = mbTop[blk8x];
            else if (blk8x == 0)
                blockTop[0] = mbLeft[blk8y - 1];
            else
                blockTop[0] = ReadPixel<TDepth>(buffer, blockOffset - stride - 1);

            // Top 8 pixels
            if (blk8y == 0)
            {
                for (int i = 0; i < 8; i++)
                    blockTop[i + 1] = mbTop[blk8x + i + 1];
            }
            else
            {
                for (int i = 0; i < 8; i++)
                    blockTop[i + 1] = ReadPixel<TDepth>(buffer, blockOffset - stride + i);
            }

            // Top-right 8 pixels: must check absolute pixel position for frame boundary
            int absoluteTopRight = mbPixelX + blk8x + 8;
            bool topRightAvail = (blk8x + 8 < 16 && (blk8y > 0 || mbTopAvailable)) ||
                                 (blk8y == 0 && absoluteTopRight + 7 < pixelWidth && mbTopAvailable);
            
            // H.264 §8.3.1.2: constrained intra prediction — top-right MB check
            if (constrainedIntraPred && mbIsIntra != null && blk8y == 0 && blk8x + 8 >= 16 && topRightAvail)
            {
                int mbX = mb.PositionX;
                int mbW = context.CodedWidth / 16;
                int trRow = cipAboveRightRow;
                if (trRow == -2)
                    trRow = mb.PositionY - 1;
                if (mbX < mbW - 1 && trRow >= 0 && !mbIsIntra[(trRow * mbW) + mbX + 1])
                    topRightAvail = false;
            }

            // MBAFF: frame-coded bottom-of-pair MBs' rightmost 8x8 blocks can't access the
            // next pair's top MB (not yet decoded in pair-based processing order).
            // Field-coded bottom MBs read from previous pair row (already decoded).
            bool isFrameCodedBottomOfPair8x8 = TShape.Mbaff && (mb.PositionY & 1) != 0
                && mbFieldDecodingFlag != null && !mbFieldDecodingFlag[mb.Address];
            if (isFrameCodedBottomOfPair8x8 && blk8y == 0 && blk8x + 8 >= 16)
                topRightAvail = false;
            if (topRightAvail && blk8y == 0)
            {
                for (int i = 0; i < 8; i++)
                {
                    if (blk8x + 8 + i < 16)
                        blockTop[9 + i] = mbTop[blk8x + 9 + i];
                    else
                        blockTop[9 + i] = ReadPixel<TDepth>(buffer, lumaOffset - stride + blk8x + 8 + i);
                }
            }
            else if (topRightAvail)
            {
                for (int i = 0; i < 8; i++)
                    blockTop[9 + i] = ReadPixel<TDepth>(buffer, blockOffset - stride + 8 + i);
            }
            else
            {
                // Replicate rightmost top pixel
                for (int i = 0; i < 8; i++)
                    blockTop[9 + i] = blockTop[8];
            }

            // Left 8 pixels
            if (blk8x == 0)
            {
                for (int i = 0; i < 8; i++)
                    blockLeft[i] = mbLeft[blk8y + i];
            }
            else
            {
                for (int i = 0; i < 8; i++)
                    blockLeft[i] = ReadPixel<TDepth>(buffer, blockOffset - 1 + i * stride);
            }

            // H.264 spec 8.3.2.2.2: Substitute unavailable boundary samples
            // Scan order: p[-1,7]..p[-1,0], p[-1,-1], p[0,-1]..p[15,-1]
            // Replace each unavailable sample with the nearest available sample in scan order.
            bool hasTopLeft;
            if (blk8x == 0 && blk8y == 0)
                hasTopLeft = mbTopAvailable && mbLeftAvailable && sliceTopLeftAvail;
            else if (blk8y == 0) // blk8x > 0
                hasTopLeft = mbTopAvailable;
            else if (blk8x == 0) // blk8y > 0
                hasTopLeft = mbLeftAvailable;
            else
                hasTopLeft = true;

            if (!blockTopAvail && !blockLeftAvail)
            {
                // All unavailable: 128 (already set by GetLumaNeighbors)
            }
            else if (!blockLeftAvail && blockTopAvail)
            {
                // Left and top-left unavailable, top available:
                // First available sample in scan is p[0,-1] = blockTop[1]
                blockTop[0] = blockTop[1];
                for (int i = 0; i < 8; i++)
                    blockLeft[i] = blockTop[1];
            }
            else if (blockLeftAvail && !blockTopAvail)
            {
                // Top and top-left unavailable, left available:
                // First available in scan is p[-1,7] = blockLeft[7]
                // All unavailable positions get the last-seen available value
                blockTop[0] = blockLeft[0]; // topLeft substituted with left[0]
                for (int i = 1; i <= 16; i++)
                    blockTop[i] = blockLeft[0]; // top and topRight substituted
            }
            else if (!hasTopLeft)
            {
                // Both left and top available but top-left corner unavailable (cross-slice):
                // Read actual corner pixel from the buffer (it was decoded by the adjacent slice).
                // FFmpeg's pred8x8l functions always read SRC(-1,-1) from the buffer regardless
                // of has_topleft — the flag only affects the l0/t0 filter formulas.
                blockTop[0] = ReadPixel<TDepth>(buffer, blockOffset - stride - 1);
            }

            // Intra 8x8 prediction (same modes as 4x4 but on 8x8 grid)
            Intra8x8Predict(pred8x8, predMode, blockTop, blockLeft, blockTopAvail, blockLeftAvail, hasTopLeft);

            // Dequant + inverse transform (residual is already in raster order from decode)
            int resOffset = i8x8 * 64;
            var pps = context.ActivePps!;

            // Dequantising zeros gives zeros, the inverse transform of zeros is zeros, and
            // adding zero leaves the prediction - so a block with no coefficients wants the
            // prediction stored and nothing else. FFmpeg reads this out of
            // non_zero_count_cache and calls no idct at all (h264_mb.c:697). It is the common
            // case rather than a corner: 54% of the intra 8x8 blocks in the 1080p clip.
            //
            // The coded block pattern is the test, not a scan of the coefficients: it is the
            // same bit the inter path already gates its 8x8 residual on, so a clear bit means
            // the parser wrote nothing into this block and a scan would only confirm it.
            if ((mb.CodedBlockPatternLuma & (1 << i8x8)) == 0)
            {
                if (TDepth.PixelShift == 0)
                {
                    Dsp.ReconstructDsp.StorePrediction8Bit(pred8x8, buffer, blockOffset, stride, 8);
                }
                else
                {
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                            WritePixel<TDepth>(buffer, blockOffset + y * stride + x, pred8x8[y * 8 + x]);
                }

                continue;
            }

            if (TDepth.PixelShift > 0)
            {
                // 10-bit path: int32 coefficient pipeline
                for (int k = 0; k < 64; k++)
                    coeffsWide[k] = residual[resOffset + k];

                if (isDiagnosticMb && DiagnosticOutput != null)
                {
                    DiagnosticOutput.Write($"  i8x8[{i8x8}] ({blk8x},{blk8y}) mode={predMode} QP={mb.QuantizationParameter} topAvail={blockTopAvail} leftAvail={blockLeftAvail}");
                    DiagnosticOutput.Write($" top=[");
                    for (int i = 0; i < 17; i++) DiagnosticOutput.Write($"{blockTop[i]}{(i<16?",":"")}");
                    DiagnosticOutput.Write($"] left=[");
                    for (int i = 0; i < 8; i++) DiagnosticOutput.Write($"{blockLeft[i]}{(i<7?",":"")}");
                    DiagnosticOutput.WriteLine("]");
                    DiagnosticOutput.Write($"    raw_coeffs=[");
                    for (int i = 0; i < 64; i++) DiagnosticOutput.Write($"{coeffsWide[i]}{(i<63?",":"")}");
                    DiagnosticOutput.WriteLine("]");
                    DiagnosticOutput.Write($"    pred=[");
                    for (int i = 0; i < 64; i++) DiagnosticOutput.Write($"{pred8x8[i]}{(i<63?",":"")}");
                    DiagnosticOutput.WriteLine("]");
                }

                H264Transform.Dequantize8x8WithMatrix(coeffsWide, pps.ScalingMatrix8[0], mb.QuantizationParameter);

                bool isTrace8x8 = DiagTraceMbX == mb.PositionX && DiagTraceMbY == mb.PositionY && (DiagTraceFrame < 0 || DeblockingFilter.DeblockCallCount == DiagTraceFrame) && i8x8 == 0;
                Scratch.Of64<int> dequantSnapshotStorage = default;
                Span<int> dequantSnapshot = isTrace8x8 ? (Span<int>)dequantSnapshotStorage : Span<int>.Empty;
                if (isTrace8x8)
                    coeffsWide.CopyTo(dequantSnapshot);

                H264Transform.InverseTransform8x8(coeffsWide, transformedWide);

                if (isTrace8x8)
                {
                    Console.WriteLine($"  Intra8x8 blk0: predMode={predMode} QP={mb.QuantizationParameter} blk8({blk8x},{blk8y})");
                    Console.Write($"    rawCoeffs(non-zero):");
                    for (int k = 0; k < 64; k++)
                        if (residual[resOffset + k] != 0) Console.Write($" [{k}]={residual[resOffset + k]}");
                    Console.Write($"\n    dequant(non-zero):");
                    for (int k = 0; k < 64; k++)
                        if (dequantSnapshot[k] != 0) Console.Write($" [{k}]={dequantSnapshot[k]}");
                    Console.Write($"\n    idct[row0]:");
                    for (int k = 0; k < 8; k++) Console.Write($" {transformedWide[k]}");
                    Console.Write($"\n    idct[row1]:");
                    for (int k = 8; k < 16; k++) Console.Write($" {transformedWide[k]}");
                    Console.Write($"\n    pred[col0]:");
                    for (int r = 0; r < 8; r++) Console.Write($" {pred8x8[r * 8]}");
                    Console.Write($"\n    idct[col0]:");
                    for (int r = 0; r < 8; r++) Console.Write($" {transformedWide[r * 8]}");
                    Console.Write($"\n    final[col0]:");
                    for (int r = 0; r < 8; r++) Console.Write($" {ClipPixel<TDepth>(pred8x8[r * 8] + transformedWide[r * 8])}");
                    Console.WriteLine();
                }

                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        int val = pred8x8[y * 8 + x] + transformedWide[y * 8 + x];
                        WritePixel<TDepth>(buffer, blockOffset + y * stride + x, ClipPixel<TDepth>(val));
                    }
                }
            }
            else
            {
                // 8-bit path: original short pipeline
                residual.Slice(resOffset, 64).CopyTo(coeffs8x8);

                Scratch.Of64<short> rawCoeffsSnapshotStorage = default;
                Span<short> rawCoeffsSnapshot = isDiagnosticMb ? (Span<short>)rawCoeffsSnapshotStorage : default;
                if (isDiagnosticMb)
                    coeffs8x8.Slice(0, 64).CopyTo(rawCoeffsSnapshot);

                // Already dequantised at the parser's store, against the 8x8 intra list.
                // The wide path above still does it here, where the coefficients are int.

                Scratch.Of64<short> dequantSnapshotStorage = default;
                Span<short> dequantSnapshot = isDiagnosticMb ? (Span<short>)dequantSnapshotStorage : default;
                if (isDiagnosticMb)
                    coeffs8x8.Slice(0, 64).CopyTo(dequantSnapshot);

                H264Transform.InverseTransform8x8(coeffs8x8, transformed8x8);

                // Sixty-four samples that were an int add, a clamp and a byte store each, with
                // WritePixel and ClipPixel both re-reading the bit depth off the decoder every
                // time. The pack does the clamp.
                Dsp.ReconstructDsp.AddResidual8Bit(pred8x8, transformed8x8, buffer, blockOffset, stride, 8);

                if (isDiagnosticMb && DiagnosticOutput != null)
                    TraceIntra8x8(DiagnosticOutput, i8x8, blk8x, blk8y, predMode, mb.QuantizationParameter,
                        blockTopAvail, blockLeftAvail, blockTop, blockLeft, rawCoeffsSnapshot,
                        pps.ScalingMatrix8[0], dequantSnapshot, pred8x8, transformed8x8,
                        buffer, blockOffset, stride);

                // A static local function, not an inline block: two hundred characters of
                // interpolation sitting in a per-block loop changes how the JIT lays out the
                // rest of the method even when it never runs. Static so that nothing is
                // captured - a capturing local function would take the address of the loop's
                // own counters and push them out of registers.
                [MethodImpl(MethodImplOptions.NoInlining)]
                static void TraceIntra8x8(System.IO.TextWriter output, int i8x8, int blk8x, int blk8y,
                    int predMode, int qp, bool blockTopAvail, bool blockLeftAvail,
                    ReadOnlySpan<int> blockTop, ReadOnlySpan<int> blockLeft,
                    ReadOnlySpan<short> rawCoeffsSnapshot, byte[] scalingMatrix,
                    ReadOnlySpan<short> dequantSnapshot, ReadOnlySpan<int> pred8x8,
                    ReadOnlySpan<short> transformed8x8, ReadOnlySpan<byte> buffer,
                    int blockOffset, int stride)
                {
                    output.Write($"  i8x8[{i8x8}] ({blk8x},{blk8y}) mode={predMode} QP={qp} topAvail={blockTopAvail} leftAvail={blockLeftAvail}");
                    output.Write($" top=[");
                    for (int i = 0; i < 17; i++) output.Write($"{blockTop[i]}{(i<16?",":"")}");
                    output.Write($"] left=[");
                    for (int i = 0; i < 8; i++) output.Write($"{blockLeft[i]}{(i<7?",":"")}");
                    output.WriteLine("]");
                    output.Write($"    raw_coeffs=[");
                    for (int i = 0; i < 64; i++) output.Write($"{rawCoeffsSnapshot[i]}{(i<63?",":"")}");
                    output.WriteLine("]");
                    output.Write($"    scaling_mat=[");
                    for (int i = 0; i < 64; i++) output.Write($"{scalingMatrix[i]}{(i<63?",":"")}");
                    output.WriteLine("]");
                    output.Write($"    dequant=[");
                    for (int i = 0; i < 64; i++) output.Write($"{dequantSnapshot[i]}{(i<63?",":"")}");
                    output.WriteLine("]");
                    output.Write($"    pred=[");
                    for (int i = 0; i < 64; i++) output.Write($"{pred8x8[i]}{(i<63?",":"")}");
                    output.WriteLine("]");
                    output.Write($"    residual=[");
                    for (int i = 0; i < 64; i++) output.Write($"{transformed8x8[i]}{(i<63?",":"")}");
                    output.WriteLine("]");
                    output.Write($"    recon=[");
                    for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) output.Write($"{buffer[blockOffset + y * stride + x]}{(y*8+x<63?",":"")}");
                    output.WriteLine("]");
                }
            }
        }
    }

    /// <summary>
    /// Intra 8x8 prediction: same 9 modes as intra 4x4 but on 8x8 grid.
    /// Reference samples: blockTop[0]=top-left, blockTop[1..8]=top, blockTop[9..16]=top-right.
    /// blockLeft[0..7]=left column.
    /// Per H.264 spec 8.3.2 with reference sample filtering.
    /// </summary>
    private void Intra8x8Predict(Span<int> pred, int mode, ReadOnlySpan<int> top,
        ReadOnlySpan<int> left, bool topAvail, bool leftAvail, bool hasTopLeft)
    {
        // Apply low-pass filter to reference samples per H.264 spec 8.3.2.2.3
        // All modes including DC use filtered samples (matches ffmpeg pred8x8l_*)
        Scratch.Of17<int> filtTopStorage = default;
        Span<int> filtTop = filtTopStorage; // [0]=top-left, [1..8]=top, [9..16]=top-right
        Scratch.Of8<int> filtLeftStorage = default;
        Span<int> filtLeft = filtLeftStorage;

        // Always apply 1-2-1 low-pass filter per H.264 spec 8.3.2.2.3
        // All modes including DC use filtered samples (matches ffmpeg pred8x8l_*)
        {
            // Filter top-left: (left[0] + 2*top[0] + top[1] + 2) >> 2
            if (topAvail && leftAvail)
                filtTop[0] = (left[0] + 2 * top[0] + top[1] + 2) >> 2;
            else
                filtTop[0] = top[0];

            // Filter top row: (t[i-1] + 2*t[i] + t[i+1] + 2) >> 2
            if (topAvail)
            {
                // filtTop[1] = t0 in FFmpeg: when !has_topleft, substitute corner with
                // first top pixel (SRC(0,-1)) per FFmpeg PREDICT_8x8_LOAD_TOP macro
                int t0Left = !hasTopLeft ? top[1] : top[0];
                filtTop[1] = (t0Left + 2 * top[1] + top[2] + 2) >> 2;
                for (int i = 2; i <= 7; i++)
                    filtTop[i] = (top[i - 1] + 2 * top[i] + top[i + 1] + 2) >> 2;
                filtTop[8] = (top[7] + 2 * top[8] + top[9] + 2) >> 2;
                for (int i = 9; i <= 15; i++)
                    filtTop[i] = (top[i - 1] + 2 * top[i] + top[i + 1] + 2) >> 2;
                // Rightmost position: (top[15] + 3*top[16] + 2) >> 2 per ffmpeg t15
                filtTop[16] = (top[15] + 3 * top[16] + 2) >> 2;
            }
            else
            {
                top.Slice(0, 17).CopyTo(filtTop);
            }

            // Filter left column: (left[i-1] + 2*left[i] + left[i+1] + 2) >> 2
            // left[-1] is top-left = top[0]
            if (leftAvail)
            {
                // filtLeft[0] = l0 in FFmpeg: when !has_topleft, substitute corner with
                // first left pixel (SRC(-1,0)) per FFmpeg PREDICT_8x8_LOAD_LEFT macro
                int l0Above = !hasTopLeft ? left[0] : top[0];
                filtLeft[0] = (l0Above + 2 * left[0] + left[1] + 2) >> 2;
                for (int i = 1; i < 7; i++)
                    filtLeft[i] = (left[i - 1] + 2 * left[i] + left[i + 1] + 2) >> 2;
                filtLeft[7] = (left[6] + 3 * left[7] + 2) >> 2;
            }
            else
            {
                left.Slice(0, 8).CopyTo(filtLeft);
            }
        }

        ReadOnlySpan<int> t = filtTop;
        ReadOnlySpan<int> l = filtLeft;

        switch (mode)
        {
            case 0: // Vertical
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        pred[y * 8 + x] = t[x + 1];
                break;

            case 1: // Horizontal
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        pred[y * 8 + x] = l[y];
                break;

            case 2: // DC (uses filtered samples per ffmpeg pred8x8l_dc)
            {
                int sum = 0;
                int count = 0;
                if (topAvail) { for (int i = 1; i <= 8; i++) sum += t[i]; count += 8; }
                if (leftAvail) { for (int i = 0; i < 8; i++) sum += l[i]; count += 8; }
                int dc = (count > 0) ? ((sum + count / 2) / count) : neutralChroma;
                pred.Slice(0, 64).Fill(dc);
                break;
            }

            case 3: // Diagonal down-left
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int idx = x + y;
                        if (idx < 14)
                            pred[y * 8 + x] = (t[idx + 1] + 2 * t[idx + 2] + t[idx + 3] + 2) >> 2;
                        else
                            pred[y * 8 + x] = (t[15] + 3 * t[16] + 2) >> 2;
                    }
                break;

            case 4: // Diagonal down-right
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        if (x > y)
                            pred[y * 8 + x] = (t[x - y - 1] + 2 * t[x - y] + t[x - y + 1] + 2) >> 2;
                        else if (x < y)
                        {
                            int ia = y - x - 2, ib = y - x - 1;
                            int a = ia >= 0 ? l[ia] : t[0];
                            pred[y * 8 + x] = (a + 2 * l[ib] + l[y - x] + 2) >> 2;
                        }
                        else // x == y
                            pred[y * 8 + x] = (l[0] + 2 * t[0] + t[1] + 2) >> 2;
                    }
                break;

            case 5: // Vertical-right (matches ffmpeg pred8x8l_vertical_right)
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int zVR = 2 * x - y;
                        if (zVR >= 0 && (zVR & 1) == 0)
                            pred[y * 8 + x] = (t[x - (y >> 1)] + t[x - (y >> 1) + 1] + 1) >> 1;
                        else if (zVR >= 0)
                            pred[y * 8 + x] = (t[x - (y >> 1) - 1] + 2 * t[x - (y >> 1)] + t[x - (y >> 1) + 1] + 2) >> 2;
                        else if (zVR == -1)
                            pred[y * 8 + x] = (l[0] + 2 * t[0] + t[1] + 2) >> 2;
                        else
                        {
                            // zVR < -1: triple uses (row-3, row-2, row-1) where row = y - 2*x
                            int row = y - 2 * x;
                            int a = row >= 3 ? l[row - 3] : t[0];
                            pred[y * 8 + x] = (a + 2 * l[row - 2] + l[row - 1] + 2) >> 2;
                        }
                    }
                break;

            case 6: // Horizontal-down (matches ffmpeg pred8x8l_horizontal_down)
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int zHD = 2 * y - x;
                        if (zHD >= 0 && (zHD & 1) == 0)
                        {
                            int i1 = y - (x >> 1);
                            int i0 = i1 - 1;
                            int a = i0 >= 0 ? l[i0] : t[0]; // i0 == -1 means top-left
                            pred[y * 8 + x] = (l[i1] + a + 1) >> 1;
                        }
                        else if (zHD >= 0)
                        {
                            int i2 = y - (x >> 1);
                            int i1 = i2 - 1;
                            int i0 = i2 - 2;
                            int a = i0 >= 0 ? l[i0] : t[0];
                            int b = i1 >= 0 ? l[i1] : t[0];
                            pred[y * 8 + x] = (a + 2 * b + l[i2] + 2) >> 2;
                        }
                        else if (zHD == -1)
                            pred[y * 8 + x] = (l[0] + 2 * t[0] + t[1] + 2) >> 2;
                        else
                        {
                            // zHD < -1: triple uses (col-2, col-1, col) where col = x - 2*y
                            int col = x - 2 * y;
                            pred[y * 8 + x] = (t[col - 2] + 2 * t[col - 1] + t[col] + 2) >> 2;
                        }
                    }
                break;

            case 7: // Vertical-left
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int idx = x + (y >> 1);
                        if ((y & 1) == 0)
                            pred[y * 8 + x] = (t[idx + 1] + t[idx + 2] + 1) >> 1;
                        else
                            pred[y * 8 + x] = (t[idx + 1] + 2 * t[idx + 2] + t[idx + 3] + 2) >> 2;
                    }
                break;

            case 8: // Horizontal-up
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int zHU = x + 2 * y;
                        if (zHU < 13)
                        {
                            int idx = y + (x >> 1);
                            if ((x & 1) == 0)
                                pred[y * 8 + x] = (l[idx] + l[idx + 1] + 1) >> 1;
                            else
                                pred[y * 8 + x] = (l[idx] + 2 * l[idx + 1] + l[idx + 2] + 2) >> 2;
                        }
                        else if (zHU == 13)
                            pred[y * 8 + x] = (l[6] + 3 * l[7] + 2) >> 2;
                        else
                            pred[y * 8 + x] = l[7];
                    }
                break;

            default:
                pred.Slice(0, 64).Fill(neutralChroma);
                break;
        }
    }
    
    private void GetBlock4x4Neighbors<TDepth>(Span<byte> buffer, int mbOffset, int stride, int pixelWidth, int blkX, int blkY,
        Span<int> mbTop, Span<int> mbLeft, Span<int> blockTop, Span<int> blockLeft)
        where TDepth : struct, IPixelDepth
    {
        int blockOffset = mbOffset + blkY * stride + blkX;
        
        // Top-left corner
        if (blkX == 0 && blkY == 0)
            blockTop[0] = mbTop[0];
        else if (blkY == 0)
            blockTop[0] = mbTop[blkX];  // From MB top row
        else if (blkX == 0)
            blockTop[0] = mbLeft[blkY - 1];  // From MB left column
        else
            blockTop[0] = ReadPixel<TDepth>(buffer, blockOffset - stride - 1);  // From already reconstructed
        
        // Top row (4 pixels)
        if (blkY == 0)
        {
            for (int i = 0; i < 4; i++)
                blockTop[i + 1] = mbTop[blkX + i + 1];
        }
        else
        {
            for (int i = 0; i < 4; i++)
                blockTop[i + 1] = ReadPixel<TDepth>(buffer, blockOffset - stride + i);
        }
        
        // Top-right (4 pixels for diagonal modes)
        // Per H.264 spec 8.3.1.2.3, upper-right availability depends on z-scan order.
        // Blocks at z-scan 3 (blkX=4,blkY=4) and z-scan 11 (blkX=4,blkY=12) have their
        // upper-right block not yet decoded (z-scan 4 and 12 respectively).
        bool topRightAvailable;
        if (blkY == 0)
        {
            // Top-row blocks: TR from MB above (same or upper-right MB)
            int absolutePixelX = mbOffset % pixelWidth + blkX + 4;
            topRightAvailable = mbOffset >= stride && absolutePixelX < pixelWidth;
        }
        else
        {
            // Interior blocks: TR must be within MB AND already decoded in z-scan
            topRightAvailable = blkX + 4 < 16 && !(blkX == 4 && (blkY == 4 || blkY == 12));
        }
        
        if (topRightAvailable && blkY == 0)
        {
            for (int i = 0; i < 4; i++)
            {
                if (blkX + 4 + i < 16)
                    blockTop[i + 5] = mbTop[blkX + 5 + i];
                else
                    blockTop[i + 5] = ReadPixel<TDepth>(buffer, mbOffset - stride + blkX + 4 + i);
            }
        }
        else if (topRightAvailable)
        {
            for (int i = 0; i < 4; i++)
            {
                int offset = blockOffset - stride + 4 + i;
                blockTop[i + 5] = (offset >= 0 && offset < buffer.Length) ? ReadPixel<TDepth>(buffer, offset) : blockTop[4];
            }
        }
        else
        {
            // Replicate the rightmost top pixel per spec
            for (int i = 0; i < 4; i++)
                blockTop[i + 5] = blockTop[4];
        }
        
        // Left column (4 pixels)
        if (blkX == 0)
        {
            for (int i = 0; i < 4; i++)
                blockLeft[i] = mbLeft[blkY + i];
        }
        else
        {
            for (int i = 0; i < 4; i++)
                blockLeft[i] = ReadPixel<TDepth>(buffer, blockOffset - 1 + i * stride);
        }
    }

    private void ReconstructIntra16x16Luma<TDepth>(ref Macroblock mb, Span<byte> buffer, int lumaOffset, int stride, Span<int> top, Span<int> left, Span<short> residual,
        bool sliceTopAvail, bool sliceLeftAvail)
        where TDepth : struct, IPixelDepth
    {
        bool isTraceMb = DiagTraceMbX == mb.PositionX && DiagTraceMbY == mb.PositionY && (DiagTraceFrame < 0 || DeblockingFilter.DeblockCallCount == DiagTraceFrame);
        Scratch.Of256<int> predStorage = default;
        Span<int> pred = predStorage;
        int predMode = (int)mb.Intra16x16PredMode;
        int qp = mb.QuantizationParameter;

        // Generate 16x16 prediction (need neighbor availability for DC mode)
        bool topAvailable = lumaOffset >= stride && sliceTopAvail;
        bool leftAvailable = (lumaOffset % context.CodedWidth) > 0 && sliceLeftAvail;
        
        Intra16x16Predict(pred, predMode, top, left, topAvailable, leftAvailable);

        if (isTraceMb)
        {
            Console.WriteLine($"  Intra16x16: predMode={predMode} QP={qp} qpDiv6={qp / 6} qpMod6={qp % 6}");
            Console.Write($"    pred[0,0..3]:");
            for (int r = 0; r < 4; r++) Console.Write($" {pred[r * 16]}");
            Console.WriteLine();
        }

        // Extract DC coefficients from residual (stored in scan order at positions 0,16,32,...,240)
        // and apply inverse zigzag to convert from scan order to raster order for Hadamard
        Scratch.Of16<short> dcScanStorage = default;
        Span<short> dcScan = dcScanStorage;
        Scratch.Of16<short> dcInStorage = default;
        Span<short> dcIn = dcInStorage;
        for (int i = 0; i < 16; i++)
            dcScan[i] = residual[i * 16];
        
        // Already raster order: the parser scanned the direct-current block as it decoded
        // it, and the redistribution above preserves the position.
        dcScan.CopyTo(dcIn);
        
        int qpDiv6 = qp / 6;
        int qpMod6 = qp % 6;
        var pps16x16 = context.ActivePps!;

        if (TDepth.PixelShift > 0)
        {
            // 10-bit path: use int32 buffers to prevent dequant overflow
            Scratch.Of16<int> dcOutWideStorage = default;
            Span<int> dcOutWide = dcOutWideStorage;
            H264Transform.HadamardInverse4x4(dcIn, dcOutWide);
            DequantizeLumaDcCoefficients(dcOutWide.Slice(0, 16), qpDiv6, qpMod6, pps16x16.ScalingMatrix4[0][0]);

            Scratch.Of16<int> transformedBlockWideStorage = default;
            Span<int> transformedBlockWide = transformedBlockWideStorage;
            Scratch.Of16<int> blockCoeffsWideStorage = default;
            Span<int> blockCoeffsWide = blockCoeffsWideStorage;
            Scratch.Of16<short> scanCoeffsStorage = default;
            Span<short> scanCoeffs = scanCoeffsStorage;
            for (int blkIdx = 0; blkIdx < 16; blkIdx++)
            {
                int blkX = (((blkIdx >> 2) & 1) * 2 + (blkIdx & 1)) * 4;
                int blkY = ((blkIdx >> 3) * 2 + ((blkIdx >> 1) & 1)) * 4;
                int rasterIdx = (blkY / 4) * 4 + (blkX / 4);

                LoadAcCoefficients(residual, blkIdx * 16, blockCoeffsWide);
                blockCoeffsWide[0] = dcOutWide[rasterIdx];

                H264Transform.Dequantize4x4WithMatrix(blockCoeffsWide.Slice(1, 15), pps16x16.ScalingMatrix4[0], qp, rasterOffset: 1);
                H264Transform.InverseTransform4x4(blockCoeffsWide, transformedBlockWide);

                if (isTraceMb && blkIdx == 0)
                {
                    Console.WriteLine($"  I16x16 blk0: DC_raw={dcScan[0]} DC_dequant={dcOutWide[rasterIdx]} blkX={blkX} blkY={blkY}");
                    Console.Write($"    coeffsWide_pre_idct[0..3]:");
                    // Can't show pre-IDCT since we already ran it, but show DC
                    Console.Write($" dc={dcOutWide[rasterIdx]}");
                    Console.Write($"\n    idct[col0]:");
                    for (int r = 0; r < 4; r++) Console.Write($" {transformedBlockWide[r * 4]}");
                    Console.Write($"\n    pred[col0]:");
                    for (int r = 0; r < 4; r++) Console.Write($" {pred[(blkY + r) * 16 + blkX]}");
                    Console.Write($"\n    final[col0]:");
                    for (int r = 0; r < 4; r++) Console.Write($" {ClipPixel<TDepth>(pred[(blkY + r) * 16 + blkX] + transformedBlockWide[r * 4])}");
                    Console.Write($"\n    scanCoeffs[0..4]:");
                    for (int k = 0; k < 5; k++) Console.Write($" {residual[blkIdx * 16 + k]}");
                    Console.WriteLine();
                }

                int blockOffset = lumaOffset + blkY * stride + blkX;
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int predVal = pred[(blkY + y) * 16 + blkX + x];
                        int val = predVal + transformedBlockWide[y * 4 + x];
                        WritePixel<TDepth>(buffer, blockOffset + y * stride + x, ClipPixel<TDepth>(val));
                    }
                }
            }
        }
        else
        {
            // 8-bit path: original short pipeline
            Scratch.Of16<short> dcOutStorage = default;
            Span<short> dcOut = dcOutStorage;
            H264Transform.HadamardInverse4x4(dcIn, dcOut);
            DequantizeLumaDcCoefficients(dcOut.Slice(0, 16), qpDiv6, qpMod6, pps16x16.ScalingMatrix4[0][0]);

            Scratch.Of16<short> transformedBlockStorage = default;
            Span<short> transformedBlock = transformedBlockStorage;
            Scratch.Of16<short> blockCoeffsStorage = default;
            Span<short> blockCoeffs = blockCoeffsStorage;
            Scratch.Of16<short> scanCoeffsStorage = default;
            Span<short> scanCoeffs = scanCoeffsStorage;
            for (int blkIdx = 0; blkIdx < 16; blkIdx++)
            {
                int blkX = (((blkIdx >> 2) & 1) * 2 + (blkIdx & 1)) * 4;
                int blkY = ((blkIdx >> 3) * 2 + ((blkIdx >> 1) & 1)) * 4;
                int rasterIdx = (blkY / 4) * 4 + (blkX / 4);

                LoadAcCoefficients(residual, blkIdx * 16, blockCoeffs);
                blockCoeffs[0] = dcOut[rasterIdx];

                // The AC coefficients arrived dequantised; position 0 holds the DC term,
                // which DequantizeLumaDcCoefficients above has already scaled.
                H264Transform.InverseTransform4x4(blockCoeffs, transformedBlock);

                int blockOffset = lumaOffset + blkY * stride + blkX;
                // The clip rides along in the saturating pack; the scalar form below stays for the
                // high bit depths, where the destination is a ushort and the pack cannot be used.
                if (TDepth.PixelShift == 0)
                {
                    Dsp.ReconstructDsp.AddResidual8Bit(
                        pred, blkY * 16 + blkX, 16,
                        transformedBlock, buffer, blockOffset, stride, 4);
                }
                else
                {
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int predVal = pred[(blkY + y) * 16 + blkX + x];
                            int val = predVal + transformedBlock[y * 4 + x];
                            WritePixel<TDepth>(buffer, blockOffset + y * stride + x, ClipPixel<TDepth>(val));
                        }
                    }
                }
            }
        }
    }

    private void Intra4x4Predict(Span<int> output, int mode, ReadOnlySpan<int> top, ReadOnlySpan<int> left, bool topAvailable, bool leftAvailable)
    {
        // top: [topleft, t0, t1, t2, t3, tr0, tr1, tr2, tr3] - 9 elements
        // left: [l0, l1, l2, l3] - 4 elements
        int topLeft = top[0];
        
        switch (mode)
        {
            case 0: // Vertical
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                        output[y * 4 + x] = top[x + 1];
                break;
                
            case 1: // Horizontal
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                        output[y * 4 + x] = left[y];
                break;
                
            case 2: // DC (H.264 spec 8.3.1.1)
                {
                    int dcVal;
                    if (topAvailable && leftAvailable)
                    {
                        int sum = 0;
                        for (int i = 0; i < 4; i++)
                            sum += top[i + 1] + left[i];
                        dcVal = (sum + 4) >> 3;
                    }
                    else if (leftAvailable)
                    {
                        int sum = 0;
                        for (int i = 0; i < 4; i++)
                            sum += left[i];
                        dcVal = (sum + 2) >> 2;
                    }
                    else if (topAvailable)
                    {
                        int sum = 0;
                        for (int i = 0; i < 4; i++)
                            sum += top[i + 1];
                        dcVal = (sum + 2) >> 2;
                    }
                    else
                    {
                        dcVal = neutralChroma;
                    }
                    output.Slice(0, 16).Fill(dcVal);
                }
                break;
                
            case 3: // Diagonal Down-Left
                {
                    // p[x,y] = (t[x+y] + 2*t[x+y+1] + t[x+y+2] + 2) >> 2
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int idx = x + y;
                            if (x == 3 && y == 3)
                                output[y * 4 + x] = (top[idx + 1] + 3 * top[idx + 2] + 2) >> 2;
                            else
                                output[y * 4 + x] = (top[idx + 1] + 2 * top[idx + 2] + top[idx + 3] + 2) >> 2;
                        }
                    }
                }
                break;
                
            case 4: // Diagonal Down-Right (H.264 spec 8.3.1.2.5)
                {
                    // p[x-y-2,-1] + 2*p[x-y-1,-1] + p[x-y,-1] when x > y
                    // p[-1,y-x-2] + 2*p[-1,y-x-1] + p[-1,y-x] when x < y
                    // p[-1,0] + 2*p[-1,-1] + p[0,-1] when x == y
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            if (x > y)
                            {
                                int ti = x - y; // top index offset, maps to p[x-y-1,-1]=top[x-y]
                                output[y * 4 + x] = (top[ti - 1] + 2 * top[ti] + top[ti + 1] + 2) >> 2;
                            }
                            else if (x < y)
                            {
                                int li = y - x; // left index, li >= 1
                                int a = (li >= 2) ? left[li - 2] : topLeft;
                                output[y * 4 + x] = (a + 2 * left[li - 1] + left[li] + 2) >> 2;
                            }
                            else // x == y
                            {
                                output[y * 4 + x] = (left[0] + 2 * topLeft + top[1] + 2) >> 2;
                            }
                        }
                    }
                }
                break;
                
            case 5: // Vertical-Right (H.264 spec 8.3.1.2.6)
                {
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int zVR = 2 * x - y;
                            if (zVR >= 0 && (zVR & 1) == 0)
                            {
                                int ti = x - (y >> 1);
                                output[y * 4 + x] = (top[ti] + top[ti + 1] + 1) >> 1;
                            }
                            else if (zVR >= 0)
                            {
                                int ti = x - (y >> 1);
                                output[y * 4 + x] = (top[ti - 1] + 2 * top[ti] + top[ti + 1] + 2) >> 2;
                            }
                            else if (zVR == -1)
                            {
                                output[y * 4 + x] = (left[0] + 2 * topLeft + top[1] + 2) >> 2;
                            }
                            else if (zVR == -2)
                            {
                                output[y * 4 + x] = (left[1] + 2 * left[0] + topLeft + 2) >> 2;
                            }
                            else // zVR == -3
                            {
                                output[y * 4 + x] = (left[2] + 2 * left[1] + left[0] + 2) >> 2;
                            }
                        }
                    }
                }
                break;
                
            case 6: // Horizontal-Down (H.264 spec 8.3.1.2.7)
                {
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int zHD = 2 * y - x;
                            if (zHD >= 0 && (zHD & 1) == 0)
                            {
                                int li = y - (x >> 1);
                                int a = (li >= 1) ? left[li - 1] : topLeft;
                                output[y * 4 + x] = (a + left[li] + 1) >> 1;
                            }
                            else if (zHD >= 0)
                            {
                                int li = y - (x >> 1);
                                int a = (li >= 2) ? left[li - 2] : topLeft;
                                int b = (li >= 1) ? left[li - 1] : topLeft;
                                output[y * 4 + x] = (a + 2 * b + left[li] + 2) >> 2;
                            }
                            else if (zHD == -1)
                            {
                                output[y * 4 + x] = (left[0] + 2 * topLeft + top[1] + 2) >> 2;
                            }
                            else if (zHD == -2)
                            {
                                output[y * 4 + x] = (top[2] + 2 * top[1] + topLeft + 2) >> 2;
                            }
                            else // zHD == -3
                            {
                                output[y * 4 + x] = (top[3] + 2 * top[2] + top[1] + 2) >> 2;
                            }
                        }
                    }
                }
                break;
                
            case 7: // Vertical-Left
                {
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int idx = x + (y >> 1) + 1;
                            if ((y & 1) == 0)
                                output[y * 4 + x] = (top[idx] + top[idx + 1] + 1) >> 1;
                            else
                                output[y * 4 + x] = (top[idx] + 2 * top[idx + 1] + top[idx + 2] + 2) >> 2;
                        }
                    }
                }
                break;
                
            case 8: // Horizontal-Up
                {
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int zHU = x + 2 * y;
                            int idx = y + (x >> 1);
                            
                            if (zHU < 5)
                            {
                                if ((x & 1) == 0)
                                    output[y * 4 + x] = (left[idx] + left[Math.Min(idx + 1, 3)] + 1) >> 1;
                                else
                                    output[y * 4 + x] = (left[idx] + 2 * left[Math.Min(idx + 1, 3)] + left[Math.Min(idx + 2, 3)] + 2) >> 2;
                            }
                            else if (zHU == 5)
                            {
                                output[y * 4 + x] = (left[2] + 3 * left[3] + 2) >> 2;
                            }
                            else
                            {
                                output[y * 4 + x] = left[3];
                            }
                        }
                    }
                }
                break;
                
            default:
                // Unknown mode - use DC
                output.Slice(0, 16).Fill(neutralChroma);
                break;
        }
    }

    private void Intra16x16Predict(Span<int> output, int mode, ReadOnlySpan<int> top, ReadOnlySpan<int> left, bool topAvailable, bool leftAvailable)
    {
        // top: [top-left, top[0..15]] = 17 elements
        // left: [left[0..15]] = 16 elements
        switch (mode)
        {
            case 0: // Vertical
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                        output[y * 16 + x] = top[x + 1];
                break;
                
            case 1: // Horizontal
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                        output[y * 16 + x] = left[y];
                break;
                
            case 2: // DC (H.264 spec 8.3.3.4)
                int dcVal;
                if (topAvailable && leftAvailable)
                {
                    int sum = 0;
                    for (int i = 0; i < 16; i++)
                        sum += top[i + 1] + left[i];
                    dcVal = (sum + 16) >> 5;
                }
                else if (leftAvailable)
                {
                    int sum = 0;
                    for (int i = 0; i < 16; i++)
                        sum += left[i];
                    dcVal = (sum + 8) >> 4;
                }
                else if (topAvailable)
                {
                    int sum = 0;
                    for (int i = 0; i < 16; i++)
                        sum += top[i + 1];
                    dcVal = (sum + 8) >> 4;
                }
                else
                {
                    dcVal = neutralChroma;
                }
                output.Fill(dcVal);
                break;
                
            case 3: // Plane
                // H.264 spec: H = sum(i=1..8) of i * (p[8+i,-1] - p[8-i,-1])
                // For 16x16: p[x,-1] = top[x+1], p[-1,y] = left[y], p[-1,-1] = top[0]
                int H = 0, V = 0;
                for (int i = 1; i <= 8; i++)
                {
                    H += i * (top[8 + i] - top[8 - i]);
                    V += i * (left[7 + i] - (i <= 7 ? left[7 - i] : top[0]));
                }
                int a = 16 * (left[15] + top[16]);
                int b = (5 * H + 32) >> 6;
                int c = (5 * V + 32) >> 6;
                
                for (int y = 0; y < 16; y++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        int val = (a + b * (x - 7) + c * (y - 7) + 16) >> 5;
                        output[y * 16 + x] = Math.Clamp(val, 0, maxPixelValue);
                    }
                }
                break;
                
            default:
                // Unknown mode, use DC
                output.Fill(neutralChroma);
                break;
        }
    }

    private void ReconstructChroma<TDepth>(ref Macroblock mb, Span<byte> buffer, int uOffset, int vOffset, int stride, int chromaYBase, Span<short> residual,
        bool sliceTopAvail, bool sliceLeftAvail, bool cipLeftTopHalfUnavail, bool cipLeftBottomHalfUnavail)
        where TDepth : struct, IPixelDepth
    {
        // H.264 chroma reconstruction for 4:2:0
        // residual layout (from CABAC/CAVLC decoding):
        //   Cb: DC at [0-3], AC blocks at [i*16+1..(i+1)*16-1] for i=0..3
        //   Cr: DC at [64-67], AC blocks at [64 + i*16+1..64+(i+1)*16-1] for i=0..3
        
        // Calculate separate chroma QPs for Cb and Cr using mapping table
        var pps = context.ActivePps!;
        int qpICb = Math.Clamp(mb.QuantizationParameter + pps.ChromaQpIndexOffset, 0, maxQp);
        int chromaQpCb = chromaQpTable[qpICb];
        int qpDiv6Cb = chromaQpCb / 6;
        int qpMod6Cb = chromaQpCb % 6;
        
        int qpICr = Math.Clamp(mb.QuantizationParameter + pps.SecondChromaQpIndexOffset, 0, maxQp);
        int chromaQpCr = chromaQpTable[qpICr];
        int qpDiv6Cr = chromaQpCr / 6;
        int qpMod6Cr = chromaQpCr % 6;
        
        // For 4:2:2 chroma DC dequant, FFmpeg uses QP+3 and normShift=6
        // (h264_mb_template.c line 230: qp[0] = sl->chroma_qp[0] + 3)
        // The 2×4 Hadamard has double the normalization of 2×2, requiring the extra shift
        int dcNormShift = isChroma422 ? 6 : 5;
        int dcQpCb = isChroma422 ? chromaQpCb + 3 : chromaQpCb;
        int dcQpDiv6Cb = dcQpCb / 6;
        int dcQpMod6Cb = dcQpCb % 6;
        int dcQpCr = isChroma422 ? chromaQpCr + 3 : chromaQpCr;
        int dcQpDiv6Cr = dcQpCr / 6;
        int dcQpMod6Cr = dcQpCr % 6;
        
        // Generate chroma prediction (8×8 for 4:2:0, 8×16 for 4:2:2)
        int chromaPredHeight = isChroma422 ? 16 : 8;
        int chromaPredSize = 8 * chromaPredHeight;
        Scratch.Of128<int> chromaPredUStorage = default;
        Span<int> chromaPredU = ((Span<int>)chromaPredUStorage)[..chromaPredSize];
        Scratch.Of128<int> chromaPredVStorage = default;
        Span<int> chromaPredV = ((Span<int>)chromaPredVStorage)[..chromaPredSize];
        if (isChroma422)
        {
            GenerateChromaIntraPrediction422<TDepth>(buffer, uOffset, stride, mb.IntraChromaPredMode, chromaPredU, mb.PositionX, chromaYBase, sliceTopAvail, sliceLeftAvail, cipLeftTopHalfUnavail, cipLeftBottomHalfUnavail);
            GenerateChromaIntraPrediction422<TDepth>(buffer, vOffset, stride, mb.IntraChromaPredMode, chromaPredV, mb.PositionX, chromaYBase, sliceTopAvail, sliceLeftAvail, cipLeftTopHalfUnavail, cipLeftBottomHalfUnavail);
        }
        else
        {
            GenerateChromaIntraPrediction<TDepth>(buffer, uOffset, stride, mb.IntraChromaPredMode, chromaPredU, mb.PositionX, chromaYBase, sliceTopAvail, sliceLeftAvail, cipLeftTopHalfUnavail, cipLeftBottomHalfUnavail);
            GenerateChromaIntraPrediction<TDepth>(buffer, vOffset, stride, mb.IntraChromaPredMode, chromaPredV, mb.PositionX, chromaYBase, sliceTopAvail, sliceLeftAvail, cipLeftTopHalfUnavail, cipLeftBottomHalfUnavail);
        }
        
        Scratch.Of8<short> chromaDcStorage = default;
        Span<short> chromaDc = ((Span<short>)chromaDcStorage)[..chromaDcCoeffs];

        if (TDepth.PixelShift > 0)
        {
            // 10-bit path: int32 coefficient pipeline
            Scratch.Of8<int> chromaDcWideStorage = default;
        Span<int> chromaDcWide = ((Span<int>)chromaDcWideStorage)[..chromaDcCoeffs];
            Scratch.Of16<int> blockCoeffsWideStorage = default;
            Span<int> blockCoeffsWide = blockCoeffsWideStorage;
            Scratch.Of16<int> transformedWideStorage = default;
            Span<int> transformedWide = transformedWideStorage;

            bool isDiagChroma = isDiagnosticMb;

            // Process Cb (U plane) - DC
            for (int i = 0; i < chromaDcCoeffs; i++)
                chromaDc[i] = residual[chromaCbDcBase + i];

            
            if (isDiagChroma) TraceChroma1(chromaDc, mb);

            [MethodImpl(MethodImplOptions.NoInlining)]

            void TraceChroma1(Span<short> chromaDc, in Macroblock mb)

            {

            DiagnosticOutput?.WriteLine($"  CHROMA-10BIT MB({mb.PositionX},{mb.PositionY}) Cb raw_dc=[{string.Join(",", chromaDc.ToArray())}] cbpChroma={mb.CodedBlockPatternChroma} chromaQp={chromaQpCb} qpDiv6={qpDiv6Cb} qpMod6={qpMod6Cb} predMode={mb.IntraChromaPredMode}");

            }

            if (isChroma422)
                H264Transform.HadamardInverse2x4(chromaDc, chromaDcWide);
            else
                H264Transform.HadamardInverse2x2(chromaDc, chromaDcWide);

            
            if (isDiagChroma) TraceChroma2(chromaDcWide);

            [MethodImpl(MethodImplOptions.NoInlining)]

            void TraceChroma2(Span<int> chromaDcWide)

            {

            DiagnosticOutput?.WriteLine($"    hadamard_dc=[{string.Join(",", chromaDcWide.ToArray())}]");

            }

            DequantizeChromaDcCoefficients(chromaDcWide, dcQpDiv6Cb, dcQpMod6Cb, pps.ScalingMatrix4[1][0], dcNormShift);

            
            if (isDiagChroma) TraceChroma3(chromaDcWide);

            [MethodImpl(MethodImplOptions.NoInlining)]

            void TraceChroma3(Span<int> chromaDcWide)

            {

            DiagnosticOutput?.WriteLine($"    dequant_dc=[{string.Join(",", chromaDcWide.ToArray())}] scaleVal={pps.ScalingMatrix4[1][0]}");

            }

            for (int blkIdx = 0; blkIdx < chromaBlocksPerComponent; blkIdx++)
            {
                int blkX = (blkIdx % 2) * 4;
                int blkY = (blkIdx / 2) * 4;

                LoadAcCoefficients(residual, blkIdx * 16, blockCoeffsWide);
                blockCoeffsWide[0] = chromaDcWide[blkIdx];

                
                if (isDiagChroma) TraceChroma4(blockCoeffsWide);

                [MethodImpl(MethodImplOptions.NoInlining)]

                void TraceChroma4(Span<int> blockCoeffsWide)

                {

                                    DiagnosticOutput?.Write($"    blk{blkIdx} pre_dequant=[");

                                    for (int dd = 0; dd < 16; dd++) DiagnosticOutput?.Write($"{blockCoeffsWide[dd]}{(dd < 15 ? "," : "")}");

                                    DiagnosticOutput?.WriteLine("]");

                                }

                H264Transform.Dequantize4x4WithMatrix(blockCoeffsWide.Slice(1, 15), pps.ScalingMatrix4[1], chromaQpCb, rasterOffset: 1);

                
                if (isDiagChroma) TraceChroma5(blockCoeffsWide);

                [MethodImpl(MethodImplOptions.NoInlining)]

                void TraceChroma5(Span<int> blockCoeffsWide)

                {

                                    DiagnosticOutput?.Write($"    blk{blkIdx} post_dequant=[");

                                    for (int dd = 0; dd < 16; dd++) DiagnosticOutput?.Write($"{blockCoeffsWide[dd]}{(dd < 15 ? "," : "")}");

                                    DiagnosticOutput?.WriteLine("]");

                                }

                H264Transform.InverseTransform4x4(blockCoeffsWide, transformedWide);

                
                if (isDiagChroma) TraceChroma6(transformedWide, chromaPredU);

                [MethodImpl(MethodImplOptions.NoInlining)]

                void TraceChroma6(Span<int> transformedWide, Span<int> chromaPredU)

                {

                                    DiagnosticOutput?.Write($"    blk{blkIdx} idct=[");

                                    for (int dd = 0; dd < 16; dd++) DiagnosticOutput?.Write($"{transformedWide[dd]}{(dd < 15 ? "," : "")}");

                                    DiagnosticOutput?.WriteLine("]");

                                    DiagnosticOutput?.Write($"    blk{blkIdx} pred=[");

                                    for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) DiagnosticOutput?.Write($"{chromaPredU[(blkY + y) * 8 + blkX + x]}{(y * 4 + x < 15 ? "," : "")}");

                                    DiagnosticOutput?.WriteLine("]");

                                }

                int blockOffset = uOffset + blkY * stride + blkX;
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int predVal = chromaPredU[(blkY + y) * 8 + blkX + x];
                        int val = predVal + transformedWide[y * 4 + x];
                        WritePixel<TDepth>(buffer, blockOffset + y * stride + x, ClipPixel<TDepth>(val));
                    }
                }
            }

            // Process Cr (V plane) - DC
            for (int i = 0; i < chromaDcCoeffs; i++)
                chromaDc[i] = residual[chromaCrDcBase + i];
            if (isChroma422)
                H264Transform.HadamardInverse2x4(chromaDc, chromaDcWide);
            else
                H264Transform.HadamardInverse2x2(chromaDc, chromaDcWide);
            DequantizeChromaDcCoefficients(chromaDcWide, dcQpDiv6Cr, dcQpMod6Cr, pps.ScalingMatrix4[2][0], dcNormShift);

            for (int blkIdx = 0; blkIdx < chromaBlocksPerComponent; blkIdx++)
            {
                int blkX = (blkIdx % 2) * 4;
                int blkY = (blkIdx / 2) * 4;

                LoadAcCoefficients(residual, chromaCrAcBase + blkIdx * 16, blockCoeffsWide);
                blockCoeffsWide[0] = chromaDcWide[blkIdx];

                H264Transform.Dequantize4x4WithMatrix(blockCoeffsWide.Slice(1, 15), pps.ScalingMatrix4[2], chromaQpCr, rasterOffset: 1);
                H264Transform.InverseTransform4x4(blockCoeffsWide, transformedWide);

                int blockOffset = vOffset + blkY * stride + blkX;
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int predVal = chromaPredV[(blkY + y) * 8 + blkX + x];
                        int val = predVal + transformedWide[y * 4 + x];
                        WritePixel<TDepth>(buffer, blockOffset + y * stride + x, ClipPixel<TDepth>(val));
                    }
                }
            }
        }
        else
        {
            // 8-bit path: original short pipeline
            Scratch.Of16<short> blockCoeffsStorage = default;
            Span<short> blockCoeffs = blockCoeffsStorage;
            Scratch.Of16<short> transformedBlockStorage = default;
            Span<short> transformedBlock = transformedBlockStorage;

            // Process Cb (U plane) - DC
            for (int i = 0; i < chromaDcCoeffs; i++)
                chromaDc[i] = residual[chromaCbDcBase + i];
            if (isChroma422)
                H264Transform.HadamardInverse2x4(chromaDc);
            else
                InverseHadamard2x2(chromaDc);
            DequantizeChromaDcCoefficients(chromaDc, dcQpDiv6Cb, dcQpMod6Cb, pps.ScalingMatrix4[1][0], dcNormShift);

            for (int blkIdx = 0; blkIdx < chromaBlocksPerComponent; blkIdx++)
            {
                int blkX = (blkIdx % 2) * 4;
                int blkY = (blkIdx / 2) * 4;

                LoadAcCoefficients(residual, blkIdx * 16, blockCoeffs);
                blockCoeffs[0] = chromaDc[blkIdx];

                // Dequantised at the parser's store, against this component's own quantiser.
                H264Transform.InverseTransform4x4(blockCoeffs, transformedBlock);

                int blockOffset = uOffset + blkY * stride + blkX;
                // The clip rides along in the saturating pack; the scalar form below stays for the
                // high bit depths, where the destination is a ushort and the pack cannot be used.
                if (TDepth.PixelShift == 0)
                {
                    Dsp.ReconstructDsp.AddResidual8Bit(
                        chromaPredU, blkY * 8 + blkX, 8,
                        transformedBlock, buffer, blockOffset, stride, 4);
                }
                else
                {
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int predVal = chromaPredU[(blkY + y) * 8 + blkX + x];
                            int val = predVal + transformedBlock[y * 4 + x];
                            WritePixel<TDepth>(buffer, blockOffset + y * stride + x, ClipPixel<TDepth>(val));
                        }
                    }
                }
            }

            // Process Cr (V plane) - DC
            for (int i = 0; i < chromaDcCoeffs; i++)
                chromaDc[i] = residual[chromaCrDcBase + i];
            if (isChroma422)
                H264Transform.HadamardInverse2x4(chromaDc);
            else
                InverseHadamard2x2(chromaDc);
            DequantizeChromaDcCoefficients(chromaDc, dcQpDiv6Cr, dcQpMod6Cr, pps.ScalingMatrix4[2][0], dcNormShift);

            for (int blkIdx = 0; blkIdx < chromaBlocksPerComponent; blkIdx++)
            {
                int blkX = (blkIdx % 2) * 4;
                int blkY = (blkIdx / 2) * 4;

                LoadAcCoefficients(residual, chromaCrAcBase + blkIdx * 16, blockCoeffs);
                blockCoeffs[0] = chromaDc[blkIdx];

                // Dequantised at the parser's store, against this component's own quantiser.
                H264Transform.InverseTransform4x4(blockCoeffs, transformedBlock);

                int blockOffset = vOffset + blkY * stride + blkX;
                // The clip rides along in the saturating pack; the scalar form below stays for the
                // high bit depths, where the destination is a ushort and the pack cannot be used.
                if (TDepth.PixelShift == 0)
                {
                    Dsp.ReconstructDsp.AddResidual8Bit(
                        chromaPredV, blkY * 8 + blkX, 8,
                        transformedBlock, buffer, blockOffset, stride, 4);
                }
                else
                {
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int predVal = chromaPredV[(blkY + y) * 8 + blkX + x];
                            int val = predVal + transformedBlock[y * 4 + x];
                            WritePixel<TDepth>(buffer, blockOffset + y * stride + x, ClipPixel<TDepth>(val));
                        }
                    }
                }
            }
        }
    }
    
    private void GenerateChromaIntraPrediction<TDepth>(Span<byte> buffer, int planeOffset, int stride, IntraChromaPredictionMode mode, Span<int> prediction, int mbX,
        int chromaYBase, bool sliceTopAvail, bool sliceLeftAvail, bool cipLeftTopHalfUnavail, bool cipLeftBottomHalfUnavail)
        where TDepth : struct, IPixelDepth
    {
        // Get 8x8 chroma neighbors (blocked at slice boundaries matching FFmpeg)
        // Use chromaYBase >= stride for top availability (matches luma's lumaOffset >= stride check).
        // For MBAFF field MBs with doubled stride, this correctly prevents reading before the
        // chroma plane start when the field-level top neighbor doesn't exist (pair row 0).
        Scratch.Of8<int> topStorage = default;
        Span<int> top = topStorage;
        Scratch.Of8<int> leftStorage = default;
        Span<int> left = leftStorage;
        bool hasTop = chromaYBase >= stride && sliceTopAvail;
        bool hasLeft = mbX > 0 && sliceLeftAvail;
        
        if (hasTop)
        {
            for (int i = 0; i < 8; i++)
                top[i] = ReadPixel<TDepth>(buffer, planeOffset - stride + i);
        }
        else
        {
            top.Fill(neutralChroma);
        }
        
        if (hasLeft)
        {
            for (int i = 0; i < 8; i++)
                left[i] = ReadPixel<TDepth>(buffer, planeOffset - 1 + i * stride);
        }
        else
        {
            left.Fill(neutralChroma);
        }
        
        // MBAFF CIP partial left: when one half of left is CIP-unavailable,
        // chroma DC uses FFmpeg's "mad cow" (ALZHEIMER) per-sub-block prediction.
        // For non-DC modes, partial left triggers mode remapping in FFmpeg —
        // Vertical stays (doesn't use left), others would error in valid streams.
        bool partialCipLeft = hasLeft && (cipLeftTopHalfUnavail || cipLeftBottomHalfUnavail);
        
        switch (mode)
        {
            case IntraChromaPredictionMode.Dc:
                if (partialCipLeft)
                    GenerateChromaAlzheimerDcPrediction(prediction, top, left, hasTop, !cipLeftTopHalfUnavail, !cipLeftBottomHalfUnavail);
                else
                    GenerateChromaDcPrediction(prediction, top, left, hasTop, hasLeft);
                break;
            case IntraChromaPredictionMode.Horizontal:
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        prediction[y * 8 + x] = hasLeft ? left[y] : neutralChroma;
                break;
            case IntraChromaPredictionMode.Vertical:
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        prediction[y * 8 + x] = hasTop ? top[x] : neutralChroma;
                break;
            case IntraChromaPredictionMode.Plane:
                GenerateChromaPlanePrediction<TDepth>(prediction, top, left, hasTop, hasLeft, buffer, planeOffset, stride);
                break;
            default:
                prediction.Fill(neutralChroma);
                break;
        }
    }
    
    private void GenerateChromaDcPrediction(Span<int> prediction, ReadOnlySpan<int> top, ReadOnlySpan<int> left, bool hasTop, bool hasLeft)
    {
        // H.264 chroma DC prediction: 4 sub-blocks (4x4 each) with separate DC values
        // Sub-block layout: [TL][TR]
        //                   [BL][BR]
        
        int dcTL, dcTR, dcBL, dcBR;
        
        if (hasTop && hasLeft)
        {
            int sumTL = 0, sumTR = 0, sumBL = 0;
            for (int i = 0; i < 4; i++) sumTL += top[i] + left[i];
            for (int i = 4; i < 8; i++) sumTR += top[i];
            for (int i = 4; i < 8; i++) sumBL += left[i];
            dcTL = (sumTL + 4) >> 3;
            dcTR = (sumTR + 2) >> 2;
            dcBL = (sumBL + 2) >> 2;
            dcBR = (sumTR + sumBL + 4) >> 3;
        }
        else if (hasTop)
        {
            int sumT0 = 0, sumT1 = 0;
            for (int i = 0; i < 4; i++) sumT0 += top[i];
            for (int i = 4; i < 8; i++) sumT1 += top[i];
            dcTL = (sumT0 + 2) >> 2;
            dcTR = (sumT1 + 2) >> 2;
            dcBL = dcTL;
            dcBR = dcTR;
        }
        else if (hasLeft)
        {
            int sumL0 = 0, sumL1 = 0;
            for (int i = 0; i < 4; i++) sumL0 += left[i];
            for (int i = 4; i < 8; i++) sumL1 += left[i];
            dcTL = (sumL0 + 2) >> 2;
            dcTR = dcTL;
            dcBL = (sumL1 + 2) >> 2;
            dcBR = dcBL;
        }
        else
        {
            dcTL = dcTR = dcBL = dcBR = neutralChroma;
        }
        
        // Fill prediction
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
                prediction[y * 8 + x] = dcTL;
            for (int x = 4; x < 8; x++)
                prediction[y * 8 + x] = dcTR;
        }
        for (int y = 4; y < 8; y++)
        {
            for (int x = 0; x < 4; x++)
                prediction[y * 8 + x] = dcBL;
            for (int x = 4; x < 8; x++)
                prediction[y * 8 + x] = dcBR;
        }
    }
    
    /// <summary>
    /// FFmpeg's "mad cow disease" (ALZHEIMER) chroma DC prediction for MBAFF CIP partial left.
    /// When one half of the left column is CIP-unavailable (inter neighbor), each 4x4 sub-block
    /// uses only the available neighbors. Ports FFmpeg's pred8x8_mad_cow_dc_{l0t,0lt,l00,0l0}.
    /// </summary>
    private void GenerateChromaAlzheimerDcPrediction(Span<int> prediction, ReadOnlySpan<int> top, ReadOnlySpan<int> left,
        bool hasTop, bool leftTopHalfAvail, bool leftBotHalfAvail)
    {
        // Each 4x4 sub-block independently uses its available neighbors:
        //   TL: top[0-3] + left[0-3]    TR: top[4-7]
        //   BL: left[4-7]               BR: top[4-7] + left[4-7]
        
        int sumT0 = 0, sumT1 = 0, sumL0 = 0, sumL1 = 0;
        if (hasTop) { for (int i = 0; i < 4; i++) sumT0 += top[i]; for (int i = 4; i < 8; i++) sumT1 += top[i]; }
        if (leftTopHalfAvail) { for (int i = 0; i < 4; i++) sumL0 += left[i]; }
        if (leftBotHalfAvail) { for (int i = 4; i < 8; i++) sumL1 += left[i]; }
        
        int dcTL, dcTR, dcBL, dcBR;
        
        // TL: uses top[0-3] and/or left[0-3]
        if (hasTop && leftTopHalfAvail) dcTL = (sumT0 + sumL0 + 4) >> 3;
        else if (hasTop) dcTL = (sumT0 + 2) >> 2;
        else if (leftTopHalfAvail) dcTL = (sumL0 + 2) >> 2;
        else dcTL = neutralChroma;
        
        // TR: uses top[4-7]; falls back to left[0-3] DC if no top
        if (hasTop) dcTR = (sumT1 + 2) >> 2;
        else if (leftTopHalfAvail) dcTR = (sumL0 + 2) >> 2;
        else dcTR = neutralChroma;
        
        // BL: uses left[4-7]; falls back to top[0-3] DC if no left bot
        if (leftBotHalfAvail) dcBL = (sumL1 + 2) >> 2;
        else if (hasTop) dcBL = (sumT0 + 2) >> 2;
        else dcBL = neutralChroma;
        
        // BR: uses top[4-7] and/or left[4-7]
        if (hasTop && leftBotHalfAvail) dcBR = (sumT1 + sumL1 + 4) >> 3;
        else if (hasTop) dcBR = (sumT1 + 2) >> 2;
        else if (leftBotHalfAvail) dcBR = (sumL1 + 2) >> 2;
        else dcBR = neutralChroma;
        
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++) prediction[y * 8 + x] = dcTL;
            for (int x = 4; x < 8; x++) prediction[y * 8 + x] = dcTR;
        }
        for (int y = 4; y < 8; y++)
        {
            for (int x = 0; x < 4; x++) prediction[y * 8 + x] = dcBL;
            for (int x = 4; x < 8; x++) prediction[y * 8 + x] = dcBR;
        }
    }
    
    private void GenerateChromaPlanePrediction<TDepth>(Span<int> prediction, ReadOnlySpan<int> top, ReadOnlySpan<int> left, bool hasTop, bool hasLeft, Span<byte> buffer, int planeOffset, int stride)
        where TDepth : struct, IPixelDepth
    {
        if (!hasTop || !hasLeft)
        {
            prediction.Fill(neutralChroma);
            return;
        }
        
        // H.264 spec section 8.3.4.4 - Plane prediction
        int topLeft = (planeOffset >= stride && (planeOffset % stride) > 0) 
            ? ReadPixel<TDepth>(buffer, planeOffset - stride - 1) : neutralChroma;
        
        int h = 0, v = 0;
        for (int i = 0; i < 4; i++)
        {
            // H.264 spec 8.3.4.4: when i=3, index 2-i = -1 means p[-1,-1] = topLeft
            int topRef = i < 3 ? top[2 - i] : topLeft;
            int leftRef = i < 3 ? left[2 - i] : topLeft;
            h += (i + 1) * (top[4 + i] - topRef);
            v += (i + 1) * (left[4 + i] - leftRef);
        }
        
        int a = 16 * (left[7] + top[7]);
        int b = (17 * h + 16) >> 5;
        int c = (17 * v + 16) >> 5;
        
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int val = (a + b * (x - 3) + c * (y - 3) + 16) >> 5;
                prediction[y * 8 + x] = Math.Clamp(val, 0, maxPixelValue);
            }
        }
    }
    
    /// <summary>
    /// Generates 8×16 chroma intra prediction for 4:2:2 format.
    /// Port of FFmpeg's pred8x16_* functions from h264pred_template.c.
    /// </summary>
    private void GenerateChromaIntraPrediction422<TDepth>(Span<byte> buffer, int planeOffset, int stride, IntraChromaPredictionMode mode, Span<int> prediction, int mbX,
        int chromaYBase, bool sliceTopAvail, bool sliceLeftAvail, bool cipLeftTopHalfUnavail, bool cipLeftBottomHalfUnavail)
        where TDepth : struct, IPixelDepth
    {
        Scratch.Of8<int> topStorage = default;
        Span<int> top = topStorage;
        Scratch.Of16<int> leftStorage = default;
        Span<int> left = leftStorage;
        bool hasTop = chromaYBase >= stride && sliceTopAvail;
        bool hasLeft = mbX > 0 && sliceLeftAvail;
        
        if (hasTop)
        {
            for (int i = 0; i < 8; i++)
                top[i] = ReadPixel<TDepth>(buffer, planeOffset - stride + i);
        }
        else
        {
            top.Fill(neutralChroma);
        }
        
        if (hasLeft)
        {
            for (int i = 0; i < 16; i++)
                left[i] = ReadPixel<TDepth>(buffer, planeOffset - 1 + i * stride);
        }
        else
        {
            left.Fill(neutralChroma);
        }
        
        switch (mode)
        {
            case IntraChromaPredictionMode.Dc:
                GenerateChromaDcPrediction422(prediction, top, left, hasTop, hasLeft);
                break;
            case IntraChromaPredictionMode.Horizontal:
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 8; x++)
                        prediction[y * 8 + x] = hasLeft ? left[y] : neutralChroma;
                break;
            case IntraChromaPredictionMode.Vertical:
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 8; x++)
                        prediction[y * 8 + x] = hasTop ? top[x] : neutralChroma;
                break;
            case IntraChromaPredictionMode.Plane:
                GenerateChromaPlanePrediction422<TDepth>(prediction, top, left, hasTop, hasLeft, buffer, planeOffset, stride);
                break;
            default:
                prediction.Fill(neutralChroma);
                break;
        }
    }
    
    /// <summary>
    /// 8×16 chroma DC prediction for 4:2:2 format.
    /// Port of FFmpeg's pred8x16_dc. Uses 4 horizontal bands (8×4 each) with 8 DC values.
    /// Left edge is sampled every 4 rows (4 quarters), top edge left/right halves.
    /// </summary>
    private void GenerateChromaDcPrediction422(Span<int> prediction, ReadOnlySpan<int> top, ReadOnlySpan<int> left, bool hasTop, bool hasLeft)
    {
        if (hasTop && hasLeft)
        {
            // dc0: left[0-3] + top[0-3] averaged (8 values)
            // dc1: top[4-7] (4 values)
            // dc2: left[4-7] (4 values)
            // dc3: left[8-11] (4 values)
            // dc4: left[12-15] (4 values)
            int dc0 = 0, dc1 = 0, dc2 = 0, dc3 = 0, dc4 = 0;
            for (int i = 0; i < 4; i++)
            {
                dc0 += left[i] + top[i];
                dc1 += top[4 + i];
                dc2 += left[4 + i];
                dc3 += left[8 + i];
                dc4 += left[12 + i];
            }
            
            int valTL = (dc0 + 4) >> 3;             // Band 0, left half
            int valTR = (dc1 + 2) >> 2;             // Band 0, right half
            int val1L = (dc2 + 2) >> 2;             // Band 1, left half
            int val1R = (dc1 + dc2 + 4) >> 3;       // Band 1, right half
            int val2L = (dc3 + 2) >> 2;             // Band 2, left half
            int val2R = (dc1 + dc3 + 4) >> 3;       // Band 2, right half
            int val3L = (dc4 + 2) >> 2;             // Band 3, left half
            int val3R = (dc1 + dc4 + 4) >> 3;       // Band 3, right half
            
            FillChroma422Bands(prediction, valTL, valTR, val1L, val1R, val2L, val2R, val3L, val3R);
        }
        else if (hasTop)
        {
            int sumT0 = 0, sumT1 = 0;
            for (int i = 0; i < 4; i++) sumT0 += top[i];
            for (int i = 4; i < 8; i++) sumT1 += top[i];
            int dcL = (sumT0 + 2) >> 2;
            int dcR = (sumT1 + 2) >> 2;
            // All bands use top values since no left available
            FillChroma422Bands(prediction, dcL, dcR, dcL, dcR, dcL, dcR, dcL, dcR);
        }
        else if (hasLeft)
        {
            int sumL0 = 0, sumL1 = 0, sumL2 = 0, sumL3 = 0;
            for (int i = 0; i < 4; i++) sumL0 += left[i];
            for (int i = 4; i < 8; i++) sumL1 += left[i];
            for (int i = 8; i < 12; i++) sumL2 += left[i];
            for (int i = 12; i < 16; i++) sumL3 += left[i];
            int dc0 = (sumL0 + 2) >> 2;
            int dc1 = (sumL1 + 2) >> 2;
            int dc2 = (sumL2 + 2) >> 2;
            int dc3 = (sumL3 + 2) >> 2;
            FillChroma422Bands(prediction, dc0, dc0, dc1, dc1, dc2, dc2, dc3, dc3);
        }
        else
        {
            prediction.Fill(neutralChroma);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillChroma422Bands(Span<int> prediction,
        int b0L, int b0R, int b1L, int b1R, int b2L, int b2R, int b3L, int b3R)
    {
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++) prediction[y * 8 + x] = b0L;
            for (int x = 4; x < 8; x++) prediction[y * 8 + x] = b0R;
        }
        for (int y = 4; y < 8; y++)
        {
            for (int x = 0; x < 4; x++) prediction[y * 8 + x] = b1L;
            for (int x = 4; x < 8; x++) prediction[y * 8 + x] = b1R;
        }
        for (int y = 8; y < 12; y++)
        {
            for (int x = 0; x < 4; x++) prediction[y * 8 + x] = b2L;
            for (int x = 4; x < 8; x++) prediction[y * 8 + x] = b2R;
        }
        for (int y = 12; y < 16; y++)
        {
            for (int x = 0; x < 4; x++) prediction[y * 8 + x] = b3L;
            for (int x = 4; x < 8; x++) prediction[y * 8 + x] = b3R;
        }
    }
    
    /// <summary>
    /// 8×16 chroma plane prediction for 4:2:2 format.
    /// Port of FFmpeg's pred8x16_plane from h264pred_template.c.
    /// Key differences from 8×8: V gradient extends to weights 2-8, normalization is (5*V+32)>>6,
    /// and base offset uses -7*V instead of -3*V.
    /// </summary>
    private void GenerateChromaPlanePrediction422<TDepth>(Span<int> prediction, ReadOnlySpan<int> top, ReadOnlySpan<int> left,
        bool hasTop, bool hasLeft, Span<byte> buffer, int planeOffset, int stride)
        where TDepth : struct, IPixelDepth
    {
        if (!hasTop || !hasLeft)
        {
            prediction.Fill(neutralChroma);
            return;
        }
        
        int topLeft = (planeOffset >= stride && (planeOffset % stride) > 0)
            ? ReadPixel<TDepth>(buffer, planeOffset - stride - 1) : neutralChroma;
        
        // Horizontal gradient: same as 8×8, weights 1-4 over top row
        int h = 0;
        for (int i = 0; i < 4; i++)
        {
            int topRef = i < 3 ? top[2 - i] : topLeft;
            h += (i + 1) * (top[4 + i] - topRef);
        }
        
        // Vertical gradient: weights 1-8 over left column (16 rows)
        // FFmpeg: src1 starts at row 8, src2 at row 6. When k=8, src2 reaches row -1
        // which is the topLeft pixel (row above the block).
        int v = 0;
        for (int k = 1; k <= 8; k++)
        {
            int upper = left[7 + k];  // left[8..15]
            int lower = k < 8 ? left[7 - k] : topLeft;  // left[6..0], then topLeft
            v += k * (upper - lower);
        }
        
        int bH = (17 * h + 16) >> 5;
        int cV = (5 * v + 32) >> 6;       // Different from 8×8: (5*V+32)>>6
        
        int a = 16 * (left[15] + top[7] + 1) - 7 * cV - 3 * bH;  // -7*V (not -3*V)
        
        // FFmpeg pred8x16_plane: pixel = (b + x*H) >> 5, NO +16 rounding
        // a already includes -3*bH, so use x*bH (not (x-3)*bH)
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int val = (a + bH * x) >> 5;
                prediction[y * 8 + x] = Math.Clamp(val, 0, maxPixelValue);
            }
            a += cV;
        }
    }
    
    private static void InverseHadamard2x2(Span<short> dc)
    {
        // 2x2 inverse Hadamard transform for chroma DC (4:2:0)
        // H.264 spec section 8.5.11.2 — NO normalization shift for 4:2:0
        short a = dc[0];
        short b = dc[1];
        short c = dc[2];
        short d = dc[3];
        
        dc[0] = (short)(a + b + c + d);
        dc[1] = (short)(a - b + c - d);
        dc[2] = (short)(a + b - c - d);
        dc[3] = (short)(a - b - c + d);
    }

    // normAdjust values from H.264 spec table 8-315, position (0,0)
    private static readonly int[] DcNormAdjust = { 10, 11, 13, 14, 16, 18 };

    // Default flat scaling list value (used when no custom scaling matrix is signaled)
    private const int DefaultScalingListValue = 16;

    /// <summary>
    /// Dequantizes luma DC coefficients after 4x4 inverse Hadamard per H.264 spec 8.5.12.2.
    /// InvLevelScale = normAdjust * weightScale (weightScale = 16 for default flat scaling list).
    /// Formula: d = c * InvLevelScale &lt;&lt; (qpDiv6 - 6)     for qpDiv6 >= 6
    ///          d = (c * InvLevelScale + rnd) >> (6 - qpDiv6)  for qpDiv6 &lt; 6
    /// Matches ffmpeg's luma_dc_dequant_idct: (f * qmul + 128) >> 8
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DequantizeLumaDcCoefficients(Span<short> dc, int qpDiv6, int qpMod6, int dcScaleValue = DefaultScalingListValue)
    {
        int invLevelScale = DcNormAdjust[qpMod6] * dcScaleValue;
        if (qpDiv6 >= 6)
        {
            int shift = qpDiv6 - 6;
            for (int i = 0; i < dc.Length; i++)
                dc[i] = (short)(dc[i] * invLevelScale << shift);
        }
        else
        {
            int shift = 6 - qpDiv6;
            int rnd = 1 << (shift - 1);
            for (int i = 0; i < dc.Length; i++)
                dc[i] = (short)((dc[i] * invLevelScale + rnd) >> shift);
        }
    }

    /// <summary>Luma DC dequant with int32 output for 10-bit decode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DequantizeLumaDcCoefficients(Span<int> dc, int qpDiv6, int qpMod6, int dcScaleValue = DefaultScalingListValue)
    {
        int invLevelScale = DcNormAdjust[qpMod6] * dcScaleValue;
        if (qpDiv6 >= 6)
        {
            int shift = qpDiv6 - 6;
            for (int i = 0; i < dc.Length; i++)
                dc[i] = dc[i] * invLevelScale << shift;
        }
        else
        {
            int shift = 6 - qpDiv6;
            int rnd = 1 << (shift - 1);
            for (int i = 0; i < dc.Length; i++)
                dc[i] = (dc[i] * invLevelScale + rnd) >> shift;
        }
    }

    /// <summary>
    /// Dequantizes chroma DC coefficients after inverse Hadamard per H.264 spec 8.5.11.2.
    /// normShift=5 for 4:2:0 (2×2 Hadamard): matches FFmpeg's (f * qmul) >> 7, no rounding.
    /// normShift=6 for 4:2:2 (2×4 Hadamard): matches FFmpeg's (f * qmul + 128) >> 8, with rounding.
    /// For 4:2:2, caller must also pass QP+3 (per FFmpeg h264_mb_template.c line 230).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DequantizeChromaDcCoefficients(Span<short> dc, int qpDiv6, int qpMod6, int dcScaleValue = DefaultScalingListValue, int normShift = 5)
    {
        int invLevelScale = DcNormAdjust[qpMod6] * dcScaleValue;
        if (qpDiv6 >= normShift)
        {
            int shift = qpDiv6 - normShift;
            for (int i = 0; i < dc.Length; i++)
                dc[i] = (short)(dc[i] * invLevelScale << shift);
        }
        else
        {
            int shift = normShift - qpDiv6;
            int rnd = normShift > 5 ? (1 << (shift - 1)) : 0;
            for (int i = 0; i < dc.Length; i++)
                dc[i] = (short)((dc[i] * invLevelScale + rnd) >> shift);
        }
    }

    /// <summary>Chroma DC dequant with int32 output for 10-bit decode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DequantizeChromaDcCoefficients(Span<int> dc, int qpDiv6, int qpMod6, int dcScaleValue = DefaultScalingListValue, int normShift = 5)
    {
        int invLevelScale = DcNormAdjust[qpMod6] * dcScaleValue;
        if (qpDiv6 >= normShift)
        {
            int shift = qpDiv6 - normShift;
            for (int i = 0; i < dc.Length; i++)
                dc[i] = dc[i] * invLevelScale << shift;
        }
        else
        {
            int shift = normShift - qpDiv6;
            int rnd = normShift > 5 ? (1 << (shift - 1)) : 0;
            for (int i = 0; i < dc.Length; i++)
                dc[i] = (dc[i] * invLevelScale + rnd) >> shift;
        }
    }

    /// <summary>
    /// Returns the reference frame buffer for inter prediction.
    /// Uses dpb[0] (oldest/IDR) as a safe fallback until P-frame decode is fully correct.
    /// TODO: Switch to dpb[dpbCount-1] once CABAC desync is fixed and P-frames decode completely.
    /// </summary>
    private byte[]? GetDefaultReferenceBuffer()
    {
        if (dpbCount > 0)
            return dpb[0].Buffer;
        return null;
    }

    /// <summary>
    /// Copies the most recent reference frame into the current frame buffer.
    /// Used as a fallback for B-slices that cannot yet be fully decoded.
    /// Uses the latest DPB entry (not dpb[0]) for best visual quality since
    /// B-frame fallback copies are not stored in the DPB.
    /// </summary>
    private void CopyReferenceToCurrentFrame()
    {
        if (currentFrameBuffer == null || dpbCount == 0)
            return;

        byte[] refBuffer = dpb[dpbCount - 1].Buffer;
        int frameDataSize = context.CodedWidth * context.CodedHeight * 3 / 2;
        refBuffer.AsSpan(0, frameDataSize).CopyTo(currentFrameBuffer.AsSpan(0, frameDataSize));
    }

    /// <summary>
    /// Whether a skipped macroblock's sixteen blocks all share one motion vector and reference,
    /// so the whole thing is a single 16x16 partition.
    /// </summary>
    /// <remarks>
    /// Measured at 100.0% of skipped macroblocks on a 1080p stream, which is what makes the
    /// partition path worth having. It is still tested rather than assumed: B_Skip derives its
    /// motion per 8x8 partition and spatial direct can differ between them.
    /// </remarks>
    private bool SkipMotionIsUniform(int mbAddr, bool isBSlice)
    {
        int first = Scan8[0];
        short l0x = mvCacheL0X[first], l0y = mvCacheL0Y[first];
        short l1x = mvCacheL1X[first], l1y = mvCacheL1Y[first];

        for (int blk = 1; blk < 16; blk++)
        {
            int at = Scan8[blk];
            if (mvCacheL0X[at] != l0x || mvCacheL0Y[at] != l0y ||
                mvCacheL1X[at] != l1x || mvCacheL1Y[at] != l1y)
                return false;
        }

        // Reference indices are held per 8x8 partition.
        if (mbRefIdxL0 is not null)
        {
            sbyte r0 = mbRefIdxL0[mbAddr * 4];
            for (int part = 1; part < 4; part++)
                if (mbRefIdxL0[mbAddr * 4 + part] != r0) return false;
        }

        if (isBSlice && mbRefIdxL1 is not null)
        {
            sbyte r1 = mbRefIdxL1[mbAddr * 4];
            for (int part = 1; part < 4; part++)
                if (mbRefIdxL1[mbAddr * 4 + part] != r1) return false;
        }

        return true;
    }

    private void ReconstructSkippedMacroblock<TShape, TDepth>(Span<byte> buffer, int lumaOffset, int lumaStride, int uOffset, int vOffset, int chromaStride, int mbX, int mbY)
        where TShape : struct, IPictureShape

        where TDepth : struct, IPixelDepth    {
        // Skip MBs use derived MVs (set during CABAC parse via DerivePSkipMotionVector/DeriveBDirectSpatialMotionVectors)
        int width = context.CodedWidth;
        int height = context.CodedHeight;
        int lumaPlaneSize = width * height; // Always frame-level, for chroma plane offset calculation
        int chromaPlaneWidth = width / 2;
        int chromaPlaneHeight = height / subHeightC;
        int chromaPlaneSize = chromaPlaneWidth * chromaPlaneHeight;
        int mcHeight = currentFieldPicture ? height / 2 : height;
        int mcChromaHeight = currentFieldPicture ? chromaPlaneHeight / 2 : chromaPlaneHeight;
        int mbPixelX = mbX * 16;
        int mbPixelY = mbY * 16;
        int mbAddr = mbY * context.WidthInMacroblocks + mbX;
        int chromaMbX = mbPixelX / 2;
        int chromaMbY = mbPixelY / subHeightC;
        
        // MBAFF field MBs: MC reads from one field of the reference (stride 2x, field offset).
        // blockPixelY must be in field coordinates: (mbY >> 1) * 16.
        // Maps to FFmpeg h264_mb_template.c lines 65-96 and h264_mb.c line 60.
        bool isMbaffFieldMb = TShape.Mbaff && mbFieldDecodingFlag != null && mbFieldDecodingFlag[mbAddr];
        int mbaffRefLumaFieldOffset = 0;
        int mbaffRefChromaFieldOffset = 0;
        if (isMbaffFieldMb)
        {
            mbPixelY = (mbY >> 1) * 16;
            chromaMbY = (mbY >> 1) * (16 / subHeightC);
            mcHeight = height / 2;
            mcChromaHeight = chromaPlaneHeight / 2;
            mbaffRefLumaFieldOffset = (mbY & 1) * width;
            mbaffRefChromaFieldOffset = (mbY & 1) * chromaPlaneWidth;
        }
        
        bool isBSlice = currentSliceHeader != null && currentSliceHeader.IsBSlice;

        
        if (isDiagnosticMb && DiagnosticOutput != null) TraceSkip1();

        [MethodImpl(MethodImplOptions.NoInlining)]

        void TraceSkip1()

        {

                    int s8 = Scan8[0];

                    DiagnosticOutput.WriteLine($"  SKIP mv=({mvCacheL0X[s8]},{mvCacheL0Y[s8]})/({mvCacheL1X[s8]},{mvCacheL1Y[s8]}) isBSlice={isBSlice} refL0={mbRefIdxL0?[mbAddr * 4] ?? 0} refL1={mbRefIdxL1?[mbAddr * 4] ?? -1} poc={currentPoc}");

                }
        
        // MBAFF B_Skip field MB trace: print MV, ref, and field offset for diagnosis
                if (isMbaffFieldMb && isBSlice && DiagnosticOutput != null) TraceSkip2();
        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceSkip2()
        {
                    int s8 = Scan8[0];
                    sbyte fRefL0 = mbRefIdxL0?[mbAddr * 4] ?? -1;
                    sbyte fRefL1 = mbRefIdxL1?[mbAddr * 4] ?? -1;
                    int frameRefL0 = fRefL0 >= 0 ? fRefL0 >> 1 : -1;
                    int frameRefL1 = fRefL1 >= 0 ? fRefL1 >> 1 : -1;
                    int fieldOffL0 = fRefL0 >= 0 ? (((fRefL0 & 1) ^ (mbY & 1)) != 0 ? 1 : 0) : -1;
                    int fieldOffL1 = fRefL1 >= 0 ? (((fRefL1 & 1) ^ (mbY & 1)) != 0 ? 1 : 0) : -1;
                    DiagnosticOutput.WriteLine($"  BSKIP_FIELD MB({mbX},{mbY}) addr={mbAddr} mvL0=({mvCacheL0X[s8]},{mvCacheL0Y[s8]}) mvL1=({mvCacheL1X[s8]},{mvCacheL1Y[s8]}) fRefL0={fRefL0} fRefL1={fRefL1} frameRef=({frameRefL0},{frameRefL1}) fieldOff=({fieldOffL0},{fieldOffL1}) mbPixY={mbPixelY} poc={currentPoc}");
                }
        
        // Field picture B-Skip trace for FREXT4 debugging
                if (currentFieldPicture && isBSlice && DiagnosticOutput != null && DiagMcStrideMb.HasValue 
            && mbX == DiagMcStrideMb.Value.mbX && mbY == DiagMcStrideMb.Value.mbY) TraceSkip3();
        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceSkip3()
        {
                    int s8 = Scan8[0];
                    sbyte refL0 = mbRefIdxL0?[mbAddr * 4] ?? -1;
                    sbyte refL1 = mbRefIdxL1?[mbAddr * 4] ?? -1;
                    DiagnosticOutput.WriteLine($"  BSKIP_FPIC MB({mbX},{mbY}) addr={mbAddr} poc={currentPoc} " +
                        $"mvL0=({mvCacheL0X[s8]},{mvCacheL0Y[s8]}) mvL1=({mvCacheL1X[s8]},{mvCacheL1Y[s8]}) " +
                        $"refL0={refL0} refL1={refL1} bpx=({mbPixelX},{mbPixelY}) lumaOff={lumaOffset} " +
                        $"fieldPic={currentFieldPicture} mcH={mcHeight} stride={lumaStride}");
                    // Show ref POCs
                    if (refL0 >= 0 && refL0 < refPicListL0Poc.Length)
                        DiagnosticOutput.Write($" L0poc={refPicListL0Poc[refL0]}");
                    if (refL1 >= 0 && refL1 < refPicListL1Poc.Length)
                        DiagnosticOutput.Write($" L1poc={refPicListL1Poc[refL1]}");
                    DiagnosticOutput.WriteLine();
                    // Show reference pixels at the target position
                    if (refL0 >= 0)
                    {
                        var refBuf = GetReferenceBuffer(refL0, 0);
                        if (refBuf != null)
                        {
                            int refOff = GetRefLumaOffset(refL0, 0);
                            int pos = refOff + mbPixelY * lumaStride + mbPixelX;
                            if (pos + 3 < refBuf.Length)
                                DiagnosticOutput.WriteLine($"  L0ref[{refOff}+{mbPixelY}*{lumaStride}+{mbPixelX}={pos}]=[{refBuf[pos]},{refBuf[pos+1]},{refBuf[pos+2]},{refBuf[pos+3]}]");
                        }
                    }
                    if (refL1 >= 0)
                    {
                        var refBuf = GetReferenceBuffer(refL1, 1);
                        if (refBuf != null)
                        {
                            int refOff = GetRefLumaOffset(refL1, 1);
                            int pos = refOff + mbPixelY * lumaStride + mbPixelX;
                            if (pos + 3 < refBuf.Length)
                                DiagnosticOutput.WriteLine($"  L1ref[{refOff}+{mbPixelY}*{lumaStride}+{mbPixelX}={pos}]=[{refBuf[pos]},{refBuf[pos+1]},{refBuf[pos+2]},{refBuf[pos+3]}]");
                        }
                    }
                    // Check implicit weighted pred
                    DiagnosticOutput.WriteLine($"  useIWP={useImplicitWeightedPred} hasWP={currentSliceHeader?.HasWeightedPred} direct_spatial={currentSliceHeader?.DirectSpatialMvPrediction}");
                }

        // Room for a whole macroblock. When its sixteen blocks share a motion vector and a
        // reference - which is every skipped macroblock measured on a 1080p stream - the
        // prediction is built, blended and written once instead of sixteen times. That is the
        // partition-level shape FFmpeg uses, where a skipped macroblock is one 16x16 partition.
        Scratch.Of256<byte> predL0Storage = default;
        Span<byte> predL0 = predL0Storage;
        Scratch.Of256<byte> predL1Storage = default;
        Span<byte> predL1 = predL1Storage;
        Scratch.Of256<byte> predFinalStorage = default;
        Span<byte> predFinal = predFinalStorage;

        bool wholeMacroblock = SkipMotionIsUniform(mbAddr, isBSlice);
        int partSize = wholeMacroblock ? 16 : 4;
        int partSamples = partSize * partSize;

        // Bi-prediction needs somewhere to build its second list before blending, and a
        // weighted partition has to be scaled before it lands, so both keep the temporary.
        bool predictInPlace = TDepth.PixelShift == 0 && !currentSliceHeader!.HasWeightedPred;

        // One partition, or sixteen 4x4 blocks when the motion is not uniform.
        for (int blkIdx = 0; blkIdx < 16; blkIdx++)
        {
            if (wholeMacroblock && blkIdx > 0)
                break;
            int blkX = (((blkIdx >> 2) & 1) * 2 + (blkIdx & 1)) * 4;
            int blkY = ((blkIdx >> 3) * 2 + ((blkIdx >> 1) & 1)) * 4;
            int blockPixelX = mbPixelX + blkX;
            int blockPixelY = mbPixelY + blkY;

            // FFmpeg's mc_part_std hands the picture itself to mc_dir_part: a partition
            // predicted from one list with no weighting goes straight into the frame, never
            // through a temporary. Doing the same removes a write and a read of every
            // predicted pixel, and a skipped macroblock is nothing but prediction.
            int blockOffset = lumaOffset + blkY * lumaStride + blkX;
            Span<byte> lumaTarget = predictInPlace ? buffer.Slice(blockOffset) : predFinal;
            int lumaTargetStride = predictInPlace ? lumaStride : partSize;
            bool wroteInPlace = predictInPlace;
            
            // Read per-block MV from the cache (temporal direct sets different MVs per 8x8 partition)
            int scan8Pos = Scan8[blkIdx];
            short mvL0X = mvCacheL0X[scan8Pos];
            short mvL0Y = mvCacheL0Y[scan8Pos];
            short mvL1X = mvCacheL1X[scan8Pos];
            short mvL1Y = mvCacheL1Y[scan8Pos];
            
            if (isBSlice)
            {
                // Read per-8x8-block ref_idx
                int sub8x8 = blkIdx >> 2;
                sbyte blkRefL0 = mbRefIdxL0![mbAddr * 4 + sub8x8];
                sbyte blkRefL1 = mbRefIdxL1![mbAddr * 4 + sub8x8];
                
                // Save original field ref indices before >>1 conversion for field weight lookup
                int origFieldRefL0 = blkRefL0;
                int origFieldRefL1 = blkRefL1;
                
                // MBAFF field MBs: map field-level ref indices to frame-level and compute per-ref field offsets.
                // Field ref_idx encodes both frame ref and parity: frame_ref = field_ref >> 1.
                // Even ref → same parity as current MB, odd → opposite parity.
                int refFieldOffL0 = mbaffRefLumaFieldOffset;
                int refFieldOffL1 = mbaffRefLumaFieldOffset;
                if (isMbaffFieldMb)
                {
                    if (blkRefL0 >= 0)
                    {
                        int fieldRef = blkRefL0;
                        blkRefL0 = (sbyte)(fieldRef >> 1);
                        refFieldOffL0 = ((fieldRef & 1) ^ (mbY & 1)) != 0 ? width : 0;
                    }
                    if (blkRefL1 >= 0)
                    {
                        int fieldRef = blkRefL1;
                        blkRefL1 = (sbyte)(fieldRef >> 1);
                        refFieldOffL1 = ((fieldRef & 1) ^ (mbY & 1)) != 0 ? width : 0;
                    }
                }
                
                bool usesL0 = blkRefL0 >= 0;
                bool usesL1 = blkRefL1 >= 0;
                
                // Per-block diagnostic for specific MBAFF error MBs
                                if (isMbaffFieldMb && DiagnosticOutput != null && (mbY & 1) != 0 && mbAddr == 83) TraceSkip4();
                [MethodImpl(MethodImplOptions.NoInlining)]
                void TraceSkip4()
                {
                                    DiagnosticOutput.WriteLine($"  BLKDIAG MB({mbX},{mbY}) blk={blkIdx} scan8={scan8Pos} mvL0=({mvL0X},{mvL0Y}) mvL1=({mvL1X},{mvL1Y}) oRefL0={origFieldRefL0} oRefL1={origFieldRefL1} refL0={blkRefL0} refL1={blkRefL1} usesL0={usesL0} usesL1={usesL1} bpx=({blockPixelX},{blockPixelY})");
                                }
                
                if (usesL0 && usesL1)
                {
                    wroteInPlace = false;
                    var refBufL0 = GetReferenceBuffer(blkRefL0, 0);
                    var refBufL1 = GetReferenceBuffer(blkRefL1, 1);
                    
                    // MC stride diagnostic: show exact MC parameters for target MB
                                        if (DiagMcStrideMb.HasValue && mbX == DiagMcStrideMb.Value.mbX && mbY == DiagMcStrideMb.Value.mbY 
                        && DiagnosticOutput != null && blkIdx == 0) TraceSkip5();
                    [MethodImpl(MethodImplOptions.NoInlining)]
                    void TraceSkip5()
                    {
                                            int offL0 = isMbaffFieldMb ? refFieldOffL0 : GetRefLumaOffset(blkRefL0, 0);
                                            int offL1 = isMbaffFieldMb ? refFieldOffL1 : GetRefLumaOffset(blkRefL1, 1);
                                            bool sameBuffer = ReferenceEquals(refBufL0, refBufL1);
                                            int storedStrideL0 = blkRefL0 >= 0 && blkRefL0 < refPicListL0RefStride.Length ? refPicListL0RefStride[blkRefL0] : -1;
                                            int storedStrideL1 = blkRefL1 >= 0 && blkRefL1 < refPicListL1RefStride.Length ? refPicListL1RefStride[blkRefL1] : -1;
                                            DiagnosticOutput.WriteLine($"  MC-BIPRED blk0: lumaStride={lumaStride} storedStrideL0={storedStrideL0} storedStrideL1={storedStrideL1} " +
                                                $"offL0={offL0} offL1={offL1} mvL0=({mvL0X},{mvL0Y}) mvL1=({mvL1X},{mvL1Y}) " +
                                                $"refL0={blkRefL0} refL1={blkRefL1} bpx=({blockPixelX},{blockPixelY}) mcH={mcHeight} SAME_BUF={sameBuffer} L0len={refBufL0?.Length} L1len={refBufL1?.Length}");
                                            // Compare buffers at several positions to check if data is actually different
                                            if (refBufL0 != null && refBufL1 != null)
                                            {
                                                int diffs = 0;
                                                for (int ci = 0; ci < Math.Min(1000, refBufL0.Length); ci++)
                                                    if (refBufL0[ci] != refBufL1[ci]) diffs++;
                                                DiagnosticOutput.Write($"  BUF-CMP: first1000diffs={diffs}");
                                                diffs = 0;
                                                for (int ci = 0; ci < refBufL0.Length; ci++)
                                                    if (refBufL0[ci] != refBufL1[ci]) diffs++;
                                                DiagnosticOutput.WriteLine($" totalDiffs={diffs}/{refBufL0.Length}");
                                            }
                                        }
                    
                    if (refBufL0 != null)
                    {
                        int refOff = isMbaffFieldMb ? refFieldOffL0 : GetRefLumaOffset(blkRefL0, 0);
                        MotionCompensation.CompensateLuma(refBufL0.AsSpan(refOff), lumaStride,
                            predL0, partSize, mvL0X, mvL0Y, blockPixelX, blockPixelY,
                            partSize, partSize, width, mcHeight);
                    }
                    if (refBufL1 != null)
                    {
                        int refOff = isMbaffFieldMb ? refFieldOffL1 : GetRefLumaOffset(blkRefL1, 1);
                        MotionCompensation.CompensateLuma(refBufL1.AsSpan(refOff), lumaStride,
                            predL1, partSize, mvL1X, mvL1Y, blockPixelX, blockPixelY,
                            partSize, partSize, width, mcHeight);
                    }
                    
                    // Diagnostic: trace reference buffer identity and pixels for selected MBs
                                        if (DiagnosticDumpAllMbsForPoc == currentPoc && DiagnosticOutput != null 
                        && blkIdx == 0 && refBufL0 != null && refBufL1 != null
                        && (mbAddr == 0 || (mbX >= 5 && mbX <= 7 && mbY == 2))) TraceSkip6(predL0, predL1);
                    [MethodImpl(MethodImplOptions.NoInlining)]
                    void TraceSkip6(ReadOnlySpan<byte> predL0, ReadOnlySpan<byte> predL1)
                    {
                                            bool sameRef = ReferenceEquals(refBufL0, refBufL1);
                                            DiagnosticOutput.Write($"  BIPRED MB({mbX},{mbY}) blk0: mv0=({mvL0X},{mvL0Y}) mv1=({mvL1X},{mvL1Y}) ref0=L0[{blkRefL0}]poc{refPicListL0Poc[blkRefL0]} ref1=L1[{blkRefL1}]poc{refPicListL1Poc[blkRefL1]}");
                                            DiagnosticOutput.Write($" SAME_BUF={sameRef} useIWP={useImplicitWeightedPred} hasWP={currentSliceHeader!.HasWeightedPred}");
                                            int rawOff = blockPixelY * lumaStride + blockPixelX;
                                            DiagnosticOutput.Write($" L0pix=[{predL0[0]},{predL0[1]},{predL0[2]},{predL0[3]}] L1pix=[{predL1[0]},{predL1[1]},{predL1[2]},{predL1[3]}]");
                                            DiagnosticOutput.Write($" rawL0=[{refBufL0[rawOff]},{refBufL0[rawOff+1]},{refBufL0[rawOff+2]},{refBufL0[rawOff+3]}]");
                                            DiagnosticOutput.Write($" rawL1=[{refBufL1[rawOff]},{refBufL1[rawOff+1]},{refBufL1[rawOff+2]},{refBufL1[rawOff+3]}]");
                                            // Check a diagnostic offset where POC0 and POC4 are known to differ (row 41, col 96 = MB(6,2) y9)
                                            int diagOff = 41 * lumaStride + 96;
                                            if (diagOff + 3 < refBufL0.Length && diagOff + 3 < refBufL1.Length)
                                                DiagnosticOutput.Write($" @(96,41):L0=[{refBufL0[diagOff]},{refBufL0[diagOff+1]}] L1=[{refBufL1[diagOff]},{refBufL1[diagOff+1]}]");
                                            DiagnosticOutput.WriteLine();
                                        }
                    
                    if (refBufL0 != null && refBufL1 != null)
                    {
                        if (useImplicitWeightedPred && blkRefL0 < refPicListL0Count && blkRefL1 < refPicListL1Count)
                        {
                            int w1 = isMbaffFieldMb
                                ? implicitWeightL1Field[origFieldRefL0, origFieldRefL1, mbY & 1]
                                : implicitWeightL1[blkRefL0, blkRefL1];
                            // An implicit weight of 32 is the plain average: with a denominator of 5,
                            // ((32a + 32b + 32) >> 6) is exactly (a + b + 1) >> 1, which is what the
                            // vectorised averager computes in one instruction per sixteen samples.
                            // FFmpeg turns weighting off for the whole slice in the commonest case of
                            // this - one reference each way with symmetric POCs (h264_slice.c:705) -
                            // and testing the weight itself catches every pair that lands on 32, not
                            // only that case.
                            if (w1 == 32)
                                MotionCompensation.BiPrediction4x4(predL0[..partSamples], predL1[..partSamples], predFinal);
                            else
                                MotionCompensation.WeightedBiPrediction(predL0[..partSamples], predL1[..partSamples], predFinal,
                                    64 - w1, w1, 0, 0, 5);
                        }
                        else if (currentSliceHeader!.HasWeightedPred)
                        {
                            MotionCompensation.WeightedBiPrediction(predL0[..partSamples], predL1[..partSamples], predFinal,
                                currentSliceHeader.LumaWeightL0[blkRefL0], currentSliceHeader.LumaWeightL1[blkRefL1],
                                currentSliceHeader.LumaOffsetL0[blkRefL0], currentSliceHeader.LumaOffsetL1[blkRefL1],
                                (int)currentSliceHeader.LumaLog2WeightDenom);
                        }
                        else if (predictInPlace)
                        {
                            // The average is the finished sample, so it goes where it belongs
                            // rather than into a third buffer that is then copied there.
                            MotionCompensation.BiPredictionToFrame(
                                predL0[..partSamples], predL1[..partSamples],
                                buffer, blockOffset, lumaStride, partSize);
                            wroteInPlace = true;
                        }
                        else
                        {
                            MotionCompensation.BiPrediction4x4(predL0[..partSamples], predL1[..partSamples], predFinal);
                        }
                        
                        // Targeted diagnostic: dump L0/L1 pred and ref pixels for DiagMcStrideMb
                                                if (DiagMcStrideMb.HasValue && mbX == DiagMcStrideMb.Value.mbX && mbY == DiagMcStrideMb.Value.mbY
                            && DiagnosticOutput != null && refBufL0 != null && refBufL1 != null) TraceSkip7(predL0, predL1, predFinal);
                        [MethodImpl(MethodImplOptions.NoInlining)]
                        void TraceSkip7(ReadOnlySpan<byte> predL0, ReadOnlySpan<byte> predL1, ReadOnlySpan<byte> predFinal)
                        {
                                                    int refOffL0 = isMbaffFieldMb ? refFieldOffL0 : GetRefLumaOffset(blkRefL0, 0);
                                                    int refOffL1 = isMbaffFieldMb ? refFieldOffL1 : GetRefLumaOffset(blkRefL1, 1);
                                                    DiagnosticOutput.WriteLine($"  MC-DETAIL poc={currentPoc} blk=({blkX},{blkY}) mv0=({mvL0X},{mvL0Y}) mv1=({mvL1X},{mvL1Y}) ref0={blkRefL0} ref1={blkRefL1} " +
                                                        $"bpx=({blockPixelX},{blockPixelY}) offL0={refOffL0} offL1={refOffL1} stride={lumaStride}");
                                                    DiagnosticOutput.WriteLine($"    predL0=[{predL0[0]},{predL0[1]},{predL0[2]},{predL0[3]}] predL1=[{predL1[0]},{predL1[1]},{predL1[2]},{predL1[3]}] final=[{predFinal[0]},{predFinal[1]},{predFinal[2]},{predFinal[3]}]");
                                                    // For integer-MV blocks, dump raw ref pixels at the exact position
                                                    if (mvL0X % 4 == 0 && mvL0Y % 4 == 0)
                                                    {
                                                        int srcY0 = blockPixelY + mvL0Y / 4;
                                                        int srcX0 = blockPixelX + mvL0X / 4;
                                                        if (srcY0 >= 0 && srcX0 >= 0 && refOffL0 + srcY0 * lumaStride + srcX0 + 3 < refBufL0.Length)
                                                            DiagnosticOutput.WriteLine($"    rawL0@({srcX0},{srcY0})=[{refBufL0[refOffL0 + srcY0 * lumaStride + srcX0]},{refBufL0[refOffL0 + srcY0 * lumaStride + srcX0 + 1]},{refBufL0[refOffL0 + srcY0 * lumaStride + srcX0 + 2]},{refBufL0[refOffL0 + srcY0 * lumaStride + srcX0 + 3]}]");
                                                    }
                                                    if (mvL1X % 4 == 0 && mvL1Y % 4 == 0)
                                                    {
                                                        int srcY1 = blockPixelY + mvL1Y / 4;
                                                        int srcX1 = blockPixelX + mvL1X / 4;
                                                        if (srcY1 >= 0 && srcX1 >= 0 && refOffL1 + srcY1 * lumaStride + srcX1 + 3 < refBufL1.Length)
                                                            DiagnosticOutput.WriteLine($"    rawL1@({srcX1},{srcY1})=[{refBufL1[refOffL1 + srcY1 * lumaStride + srcX1]},{refBufL1[refOffL1 + srcY1 * lumaStride + srcX1 + 1]},{refBufL1[refOffL1 + srcY1 * lumaStride + srcX1 + 2]},{refBufL1[refOffL1 + srcY1 * lumaStride + srcX1 + 3]}]");
                                                    }
                                                }
                        
                        // Diagnostic: trace final prediction pixels for first blocks
                                                if (DiagnosticDumpAllMbsForPoc == currentPoc && DiagnosticOutput != null 
                            && blkIdx == 0 && mbAddr < 3) TraceSkip8(predL0, predL1, predFinal);
                        [MethodImpl(MethodImplOptions.NoInlining)]
                        void TraceSkip8(ReadOnlySpan<byte> predL0, ReadOnlySpan<byte> predL1, ReadOnlySpan<byte> predFinal)
                        {
                                                    DiagnosticOutput.WriteLine($"  FINAL MB({mbX},{mbY}) blk0: pred=[{predFinal[0]},{predFinal[1]},{predFinal[2]},{predFinal[3]}] exp_avg=[{(predL0[0]+predL1[0]+1)>>1},{(predL0[1]+predL1[1]+1)>>1},{(predL0[2]+predL1[2]+1)>>1},{(predL0[3]+predL1[3]+1)>>1}]");
                                                }
                    }
                    else if (refBufL0 != null)
                        predL0.CopyTo(predFinal);
                    else if (refBufL1 != null)
                        predL1.CopyTo(predFinal);
                    else
                        predFinal[..partSamples].Fill(128);
                }
                else if (usesL1)
                {
                    var refBuf = GetReferenceBuffer(blkRefL1, 1);
                    if (refBuf != null)
                    {
                        int refOff = isMbaffFieldMb ? refFieldOffL1 : GetRefLumaOffset(blkRefL1, 1);
                        
                        // Detailed trace for MBAFF bottom-field B_Skip MBs with MV=(0,0)
                                                if (isMbaffFieldMb && DiagnosticOutput != null && blkIdx == 0 && (mbY & 1) != 0
                            && mvL1X == 0 && mvL1Y == 0 && mbAddr == 83) TraceSkip9();
                        [MethodImpl(MethodImplOptions.NoInlining)]
                        void TraceSkip9()
                        {
                                                    int baseOff = GetRefLumaOffset(blkRefL1, 1);
                                                    // Dump entire first row (16 bytes) of reference bottom field for this MB
                                                    var sb = new System.Text.StringBuilder();
                                                    sb.Append($"  BSKIP_REFROW MB({mbX},{mbY}) refOff={refOff} row0_ref=[");
                                                    for (int px = 0; px < 16; px++)
                                                    {
                                                        if (px > 0) sb.Append(',');
                                                        sb.Append(refBuf[refOff + 0 * lumaStride + mbPixelX + px]);
                                                    }
                                                    sb.Append("] row0_topRef=[");
                                                    int topOff = baseOff;
                                                    for (int px = 0; px < 16; px++)
                                                    {
                                                        if (px > 0) sb.Append(',');
                                                        sb.Append(refBuf[topOff + 0 * lumaStride + mbPixelX + px]);
                                                    }
                                                    sb.Append("]");
                                                    // Also dump 4 rows of bottom field for this MB
                                                    for (int ry = 0; ry < 8; ry++)
                                                    {
                                                        sb.Append($" row{ry}=[");
                                                        for (int px = 0; px < 16; px++)
                                                        {
                                                            if (px > 0) sb.Append(',');
                                                            sb.Append(refBuf[refOff + ry * lumaStride + mbPixelX + px]);
                                                        }
                                                        sb.Append(']');
                                                    }
                                                    sb.Append($" isSame={ReferenceEquals(refBuf, currentFrameBuffer)} refL1Poc={refPicListL1Poc[blkRefL1]}");
                                                    DiagnosticOutput.WriteLine(sb.ToString());
                                                }
                        
                        MotionCompensation.CompensateLuma(refBuf.AsSpan(refOff), lumaStride,
                            lumaTarget, lumaTargetStride, mvL1X, mvL1Y, blockPixelX, blockPixelY,
                            partSize, partSize, width, mcHeight);
                    }
                    else
                    {
                        for (int y = 0; y < partSize; y++)
                            lumaTarget.Slice(y * lumaTargetStride, partSize).Fill(128);
                    }
                    
                    if (currentSliceHeader!.HasWeightedPred)
                    {
                        MotionCompensation.WeightedPrediction(predFinal[..partSamples], predFinal,
                            currentSliceHeader.LumaWeightL1[blkRefL1], currentSliceHeader.LumaOffsetL1[blkRefL1],
                            (int)currentSliceHeader.LumaLog2WeightDenom);
                    }
                }
                else
                {
                    int effectiveRef = blkRefL0 >= 0 ? blkRefL0 : 0;
                    var refBuf = GetReferenceBuffer(effectiveRef, 0);
                    if (refBuf != null)
                    {
                        int refOff = isMbaffFieldMb ? refFieldOffL0 : GetRefLumaOffset(effectiveRef, 0);
                        MotionCompensation.CompensateLuma(refBuf.AsSpan(refOff), lumaStride,
                            lumaTarget, lumaTargetStride, mvL0X, mvL0Y, blockPixelX, blockPixelY,
                            partSize, partSize, width, mcHeight);
                    }
                    else
                    {
                        for (int y = 0; y < partSize; y++)
                            lumaTarget.Slice(y * lumaTargetStride, partSize).Fill(128);
                    }
                    
                    if (currentSliceHeader!.HasWeightedPred)
                    {
                        MotionCompensation.WeightedPrediction(predFinal[..partSamples], predFinal,
                            currentSliceHeader.LumaWeightL0[effectiveRef], currentSliceHeader.LumaOffsetL0[effectiveRef],
                            (int)currentSliceHeader.LumaLog2WeightDenom);
                    }
                }
            }
            else
            {
                // P_Skip: L0 only, uniform MV
                var refBuf = GetReferenceBuffer(0, 0);
                if (refBuf != null)
                {
                    int refOff = isMbaffFieldMb ? mbaffRefLumaFieldOffset : GetRefLumaOffset(0, 0);
                    MotionCompensation.CompensateLuma(refBuf.AsSpan(refOff), lumaStride,
                        lumaTarget, lumaTargetStride, mvL0X, mvL0Y, blockPixelX, blockPixelY,
                        partSize, partSize, width, mcHeight);
                    
                    // Diagnostic: dump reference buffer bytes vs MC output for target POC
                                        if (DiagnosticDumpAllMbsForPoc == currentPoc && DiagnosticOutput != null && blkIdx == 0) TraceSkip10(predFinal);
                    [MethodImpl(MethodImplOptions.NoInlining)]
                    void TraceSkip10(ReadOnlySpan<byte> predFinal)
                    {
                                            int refPos = (blockPixelY + (mvL0Y >> 2)) * lumaStride + (blockPixelX + (mvL0X >> 2));
                                            DiagnosticOutput.Write($"  REFBUF MB({mbX},{mbY}) blk0: refOff={refOff} refBufLen={refBuf.Length} refPos={refPos} stride={lumaStride}");
                                            if (refPos >= 0 && refPos + 3 * lumaStride + 3 < refBuf.Length)
                                            {
                                                DiagnosticOutput.Write($" ref=[{refBuf[refOff + refPos]},{refBuf[refOff + refPos+1]},{refBuf[refOff + refPos+2]},{refBuf[refOff + refPos+3]}]");
                                                DiagnosticOutput.Write($" mc=[{predFinal[0]},{predFinal[1]},{predFinal[2]},{predFinal[3]}]");
                                            }
                                            DiagnosticOutput.Write($" sameBuf={ReferenceEquals(refBuf, currentFrameBuffer)}");
                                            DiagnosticOutput.Write($" isMbaffField={isMbaffFieldMb} mv=({mvL0X},{mvL0Y}) bpx=({blockPixelX},{blockPixelY}) mcH={mcHeight}");
                                            // For MBAFF field MBs, also dump frame-coordinate reference pixel at known position
                                            if (isMbaffFieldMb)
                                            {
                                                int frameRow = (blockPixelY * 2) + (mbY & 1);
                                                int framePos = frameRow * width + blockPixelX;
                                                if (framePos >= 0 && framePos + 3 < refBuf.Length)
                                                    DiagnosticOutput.Write($" frameRef[row{frameRow}]=[{refBuf[framePos]},{refBuf[framePos+1]},{refBuf[framePos+2]},{refBuf[framePos+3]}]");
                                            }
                                            DiagnosticOutput.WriteLine();
                                        }
                }
                else
                {
                    for (int y = 0; y < partSize; y++)
                        lumaTarget.Slice(y * lumaTargetStride, partSize).Fill(128);
                }
                
                if (currentSliceHeader!.HasWeightedPred)
                {
                    MotionCompensation.WeightedPrediction(predFinal[..partSamples], predFinal,
                        currentSliceHeader.LumaWeightL0[0], currentSliceHeader.LumaOffsetL0[0],
                        (int)currentSliceHeader.LumaLog2WeightDenom);
                }
            }
            
            // Write the prediction out - unless it was written into the frame to begin with.
            if (!wroteInPlace)
            {
                if (TDepth.PixelShift == 0)
                {
                    // A partition's rows are contiguous in both buffers, so each is one copy.
                    // This is only worth doing now the partition is sixteen wide - the same
                    // change measured at a loss when a row was four bytes.
                    Dsp.ReconstructDsp.CopyRows(
                        predFinal, 0, partSize,
                        buffer, blockOffset, lumaStride, partSize, partSize);
                }
                else
                {
                    for (int y = 0; y < partSize; y++)
                        for (int x = 0; x < partSize; x++)
                            WritePixel<TDepth>(buffer, blockOffset + y * lumaStride + x,
                                predFinal[y * partSize + x]);
                }
            }
        }
        
        // Chroma: 2x2 of 4x4 blocks per plane (Cb then Cr)
        // Skip for monochrome — chroma planes stay at 128 (pre-initialized)
        if (context.ActiveSps!.ChromaFormat == ChromaFormat.Monochrome)
            return;
            
        // One 8x8 partition per plane when the macroblock's motion is uniform, otherwise four
        // 4x4 blocks of four 2x2 sub-blocks. The narrow path produced four pixels per call and
        // paid a full setup - fractional position, bounds test, weight products - for each of
        // them, thirty-two times per macroblock. FFmpeg does one call per partition per plane.
        Scratch.Of64<byte> chromaPredL0Storage = default;
        Span<byte> chromaPredL0 = chromaPredL0Storage;
        Scratch.Of64<byte> chromaPredL1Storage = default;
        Span<byte> chromaPredL1 = chromaPredL1Storage;
        Scratch.Of64<byte> chromaPredFinalStorage = default;
        Span<byte> chromaPredFinal = chromaPredFinalStorage;

        int chromaPartSize = wholeMacroblock ? 8 : 4;
        int chromaPartSamples = chromaPartSize * chromaPartSize;
        int chromaSubSize = wholeMacroblock ? 8 : 2;
        int chromaSubCount = wholeMacroblock ? 1 : 4;
        
        for (int plane = 0; plane < 2; plane++)
        {
            int planeOffset = plane == 0 ? uOffset : vOffset;
            
            for (int blkIdx = 0; blkIdx < 4; blkIdx++)
            {
                if (wholeMacroblock && blkIdx > 0)
                    break;

                int blkX = (blkIdx % 2) * 4;
                int blkY = (blkIdx / 2) * 4;
                int chromaBlockX = chromaMbX + blkX;
                int chromaBlockY = chromaMbY + blkY;

                // Chroma goes into the frame directly on the same terms as luma.
                int chromaBlockOffset = planeOffset + blkY * chromaStride + blkX;
                Span<byte> chromaTarget = predictInPlace
                    ? buffer.Slice(chromaBlockOffset) : chromaPredFinal;
                int chromaTargetStride = predictInPlace ? chromaStride : chromaPartSize;
                bool chromaWroteInPlace = predictInPlace;
                
                // Per-2x2 sub-block chroma MC for sub-partition correctness
                int lumaBase = blkIdx * 4;
                
                if (isBSlice)
                {
                    sbyte blkRefL0 = mbRefIdxL0![mbAddr * 4 + blkIdx];
                    sbyte blkRefL1 = mbRefIdxL1![mbAddr * 4 + blkIdx];
                    
                    // Save original field ref indices before >>1 conversion for field weight lookup
                    int origChromaFieldRefL0 = blkRefL0;
                    int origChromaFieldRefL1 = blkRefL1;
                    
                    // MBAFF field MBs: map field-level ref indices to frame-level and compute per-ref chroma field offsets.
                    int refChromaFieldOffL0 = mbaffRefChromaFieldOffset;
                    int refChromaFieldOffL1 = mbaffRefChromaFieldOffset;
                    if (isMbaffFieldMb)
                    {
                        if (blkRefL0 >= 0)
                        {
                            int fieldRef = blkRefL0;
                            blkRefL0 = (sbyte)(fieldRef >> 1);
                            refChromaFieldOffL0 = ((fieldRef & 1) ^ (mbY & 1)) != 0 ? chromaPlaneWidth : 0;
                        }
                        if (blkRefL1 >= 0)
                        {
                            int fieldRef = blkRefL1;
                            blkRefL1 = (sbyte)(fieldRef >> 1);
                            refChromaFieldOffL1 = ((fieldRef & 1) ^ (mbY & 1)) != 0 ? chromaPlaneWidth : 0;
                        }
                    }
                    
                    bool usesL0 = blkRefL0 >= 0;
                    bool usesL1 = blkRefL1 >= 0;
                    
                    // Field chroma MV parity adjustment (FFmpeg h264_mb.c:289-291)
                    // MB_FIELD includes both field pictures AND MBAFF field MBs.
                    int chromaAdjL0 = 0, chromaAdjL1 = 0;
                    if (currentFieldPicture)
                    {
                        int curParity = currentPictureStructure == ReferenceFrame.PictBottomField ? 1 : 0;
                        int effectiveL0 = blkRefL0 >= 0 ? blkRefL0 : 0;
                        chromaAdjL0 = 2 * (curParity - (GetRefLumaOffset(effectiveL0, 0) > 0 ? 1 : 0));
                        if (usesL1)
                            chromaAdjL1 = 2 * (curParity - (GetRefLumaOffset(blkRefL1, 1) > 0 ? 1 : 0));
                    }
                    else if (isMbaffFieldMb)
                    {
                        int curParity = mbY & 1;
                        if (blkRefL0 >= 0)
                        {
                            int refParity = refChromaFieldOffL0 > 0 ? 1 : 0;
                            chromaAdjL0 = 2 * (curParity - refParity);
                        }
                        if (usesL1 && blkRefL1 >= 0)
                        {
                            int refParity = refChromaFieldOffL1 > 0 ? 1 : 0;
                            chromaAdjL1 = 2 * (curParity - refParity);
                        }
                    }
                    
                    
                    if (usesL0 && usesL1)
                    {
                        chromaWroteInPlace = false;
                        var refBufL0 = GetReferenceBuffer(blkRefL0, 0);
                        var refBufL1 = GetReferenceBuffer(blkRefL1, 1);
                        int refChromaOffL0 = isMbaffFieldMb ? refChromaFieldOffL0 : GetRefLumaOffset(blkRefL0, 0) / 2;
                        int refChromaOffL1 = isMbaffFieldMb ? refChromaFieldOffL1 : GetRefLumaOffset(blkRefL1, 1) / 2;
                        ReadOnlySpan<byte> refChromaL0 = refBufL0 != null ? refBufL0.AsSpan(lumaPlaneSize + plane * chromaPlaneSize + refChromaOffL0) : ReadOnlySpan<byte>.Empty;
                        ReadOnlySpan<byte> refChromaL1 = refBufL1 != null ? refBufL1.AsSpan(lumaPlaneSize + plane * chromaPlaneSize + refChromaOffL1) : ReadOnlySpan<byte>.Empty;
                        
                        for (int sub = 0; sub < chromaSubCount; sub++)
                        {
                            int subX = (sub & 1) * chromaSubSize;
                            int subY = (sub >> 1) * chromaSubSize;
                            int scan8Pos = Scan8[lumaBase + sub];
                            
                            if (!refChromaL0.IsEmpty)
                            {
                                MotionCompensation.CompensateChroma(refChromaL0, chromaStride,
                                    chromaPredL0.Slice(subY * chromaPartSize + subX), chromaPartSize,
                                    mvCacheL0X[scan8Pos], (short)(mvCacheL0Y[scan8Pos] + chromaAdjL0),
                                    chromaBlockX + subX, chromaBlockY + subY, chromaSubSize, chromaSubSize,
                                    chromaPlaneWidth, mcChromaHeight);
                            }
                            if (!refChromaL1.IsEmpty)
                            {
                                MotionCompensation.CompensateChroma(refChromaL1, chromaStride,
                                    chromaPredL1.Slice(subY * chromaPartSize + subX), chromaPartSize,
                                    mvCacheL1X[scan8Pos], (short)(mvCacheL1Y[scan8Pos] + chromaAdjL1),
                                    chromaBlockX + subX, chromaBlockY + subY, chromaSubSize, chromaSubSize,
                                    chromaPlaneWidth, mcChromaHeight);
                            }
                        }
                        
                        if (!refChromaL0.IsEmpty && !refChromaL1.IsEmpty)
                        {
                            if (useImplicitWeightedPred && blkRefL0 < refPicListL0Count && blkRefL1 < refPicListL1Count)
                            {
                                int w1 = isMbaffFieldMb
                                    ? implicitWeightL1Field[origChromaFieldRefL0, origChromaFieldRefL1, mbY & 1]
                                    : implicitWeightL1[blkRefL0, blkRefL1];
                                // An implicit weight of 32 is the plain average: with a denominator of 5,
                                // ((32a + 32b + 32) >> 6) is exactly (a + b + 1) >> 1, which is what the
                                // vectorised averager computes in one instruction per sixteen samples.
                                // FFmpeg turns weighting off for the whole slice in the commonest case of
                                // this - one reference each way with symmetric POCs (h264_slice.c:705) -
                                // and testing the weight itself catches every pair that lands on 32, not
                                // only that case.
                                if (w1 == 32)
                                    MotionCompensation.BiPrediction4x4(chromaPredL0[..chromaPartSamples], chromaPredL1[..chromaPartSamples], chromaPredFinal);
                                else
                                    MotionCompensation.WeightedBiPrediction(chromaPredL0[..chromaPartSamples], chromaPredL1[..chromaPartSamples], chromaPredFinal,
                                        64 - w1, w1, 0, 0, 5);
                            }
                            else if (currentSliceHeader!.HasWeightedPred)
                            {
                                MotionCompensation.WeightedBiPrediction(chromaPredL0[..chromaPartSamples], chromaPredL1[..chromaPartSamples], chromaPredFinal,
                                    currentSliceHeader.ChromaWeightL0[blkRefL0, plane], currentSliceHeader.ChromaWeightL1[blkRefL1, plane],
                                    currentSliceHeader.ChromaOffsetL0[blkRefL0, plane], currentSliceHeader.ChromaOffsetL1[blkRefL1, plane],
                                    (int)currentSliceHeader.ChromaLog2WeightDenom);
                            }
                            else
                            {
                                MotionCompensation.BiPrediction4x4(chromaPredL0[..chromaPartSamples], chromaPredL1[..chromaPartSamples], chromaPredFinal);
                            }
                        }
                        else if (!refChromaL0.IsEmpty)
                            chromaPredL0.CopyTo(chromaPredFinal);
                        else if (!refChromaL1.IsEmpty)
                            chromaPredL1.CopyTo(chromaPredFinal);
                        else
                            chromaPredFinal[..chromaPartSamples].Fill(128);
                    }
                    else if (usesL1)
                    {
                        var refBuf = GetReferenceBuffer(blkRefL1, 1);
                        int refChromaOff = isMbaffFieldMb ? refChromaFieldOffL1 : GetRefLumaOffset(blkRefL1, 1) / 2;
                        ReadOnlySpan<byte> refChroma = refBuf != null ? refBuf.AsSpan(lumaPlaneSize + plane * chromaPlaneSize + refChromaOff) : ReadOnlySpan<byte>.Empty;
                        if (!refChroma.IsEmpty)
                        {
                            for (int sub = 0; sub < chromaSubCount; sub++)
                            {
                                int subX = (sub & 1) * chromaSubSize;
                                int subY = (sub >> 1) * chromaSubSize;
                                int scan8Pos = Scan8[lumaBase + sub];
                                MotionCompensation.CompensateChroma(refChroma, chromaStride,
                                    chromaTarget.Slice(subY * chromaTargetStride + subX), chromaTargetStride,
                                    mvCacheL1X[scan8Pos], (short)(mvCacheL1Y[scan8Pos] + chromaAdjL1),
                                    chromaBlockX + subX, chromaBlockY + subY, chromaSubSize, chromaSubSize,
                                    chromaPlaneWidth, mcChromaHeight);
                            }
                            
                            if (currentSliceHeader!.HasWeightedPred)
                            {
                                MotionCompensation.WeightedPrediction(chromaPredFinal[..chromaPartSamples], chromaPredFinal,
                                    currentSliceHeader.ChromaWeightL1[blkRefL1, plane], currentSliceHeader.ChromaOffsetL1[blkRefL1, plane],
                                    (int)currentSliceHeader.ChromaLog2WeightDenom);
                            }
                        }
                        else
                        {
                            for (int y = 0; y < chromaPartSize; y++)
                                chromaTarget.Slice(y * chromaTargetStride, chromaPartSize).Fill(128);
                        }
                    }
                    else
                    {
                        int effectiveRef = blkRefL0 >= 0 ? blkRefL0 : 0;
                        var refBuf = GetReferenceBuffer(effectiveRef, 0);
                        int refChromaOff = isMbaffFieldMb ? refChromaFieldOffL0 : GetRefLumaOffset(effectiveRef, 0) / 2;
                        ReadOnlySpan<byte> refChroma = refBuf != null ? refBuf.AsSpan(lumaPlaneSize + plane * chromaPlaneSize + refChromaOff) : ReadOnlySpan<byte>.Empty;
                        if (!refChroma.IsEmpty)
                        {
                            for (int sub = 0; sub < chromaSubCount; sub++)
                            {
                                int subX = (sub & 1) * chromaSubSize;
                                int subY = (sub >> 1) * chromaSubSize;
                                int scan8Pos = Scan8[lumaBase + sub];
                                MotionCompensation.CompensateChroma(refChroma, chromaStride,
                                    chromaTarget.Slice(subY * chromaTargetStride + subX), chromaTargetStride,
                                    mvCacheL0X[scan8Pos], (short)(mvCacheL0Y[scan8Pos] + chromaAdjL0),
                                    chromaBlockX + subX, chromaBlockY + subY, chromaSubSize, chromaSubSize,
                                    chromaPlaneWidth, mcChromaHeight);
                            }
                            
                            if (currentSliceHeader!.HasWeightedPred)
                            {
                                MotionCompensation.WeightedPrediction(chromaPredFinal[..chromaPartSamples], chromaPredFinal,
                                    currentSliceHeader.ChromaWeightL0[effectiveRef, plane], currentSliceHeader.ChromaOffsetL0[effectiveRef, plane],
                                    (int)currentSliceHeader.ChromaLog2WeightDenom);
                            }
                        }
                        else
                        {
                            for (int y = 0; y < chromaPartSize; y++)
                                chromaTarget.Slice(y * chromaTargetStride, chromaPartSize).Fill(128);
                        }
                    }
                }
                else
                {
                    // P-skip: single MV for entire MB, use top-left 4x4's MV (all are same)
                    int lumaScan8 = Scan8[lumaBase];
                    short mvL0X = mvCacheL0X[lumaScan8];
                    short mvL0Y = mvCacheL0Y[lumaScan8];
                    
                    // Field chroma MV parity adjustment
                    if (currentFieldPicture)
                    {
                        int curParity = currentPictureStructure == ReferenceFrame.PictBottomField ? 1 : 0;
                        int refParity = GetRefLumaOffset(0, 0) > 0 ? 1 : 0;
                        mvL0Y = (short)(mvL0Y + 2 * (curParity - refParity));
                    }
                    
                    var refBuf = GetReferenceBuffer(0, 0);
                    int refChromaOff = isMbaffFieldMb ? mbaffRefChromaFieldOffset : GetRefLumaOffset(0, 0) / 2;
                    ReadOnlySpan<byte> refChroma = refBuf != null ? refBuf.AsSpan(lumaPlaneSize + plane * chromaPlaneSize + refChromaOff) : ReadOnlySpan<byte>.Empty;
                    if (!refChroma.IsEmpty)
                        MotionCompensation.CompensateChroma(refChroma, chromaStride,
                            chromaTarget, chromaTargetStride, mvL0X, mvL0Y,
                            chromaBlockX, chromaBlockY, chromaPartSize, chromaPartSize,
                            chromaPlaneWidth, mcChromaHeight);
                    else
                    {
                        for (int y = 0; y < chromaPartSize; y++)
                            chromaTarget.Slice(y * chromaTargetStride, chromaPartSize).Fill(128);
                    }
                    
                    // DIAGNOSTIC: trace chroma MC for first bottom-field MB
                                        if (DiagnosticOutput != null && currentPictureStructure == ReferenceFrame.PictBottomField 
                        && mbAddr == 0 && plane == 0 && blkIdx == 1 && !refChroma.IsEmpty) TraceSkip11(refChroma, chromaPredFinal);
                    [MethodImpl(MethodImplOptions.NoInlining)]
                    void TraceSkip11(ReadOnlySpan<byte> refChroma, ReadOnlySpan<byte> chromaPredFinal)
                    {
                                            DiagnosticOutput.Write($"  CHROMA-DIAG MB(0,0) plane=U blk=1: mv=({mvL0X},{mvL0Y}) refOff={refChromaOff} stride={chromaStride}");
                                            DiagnosticOutput.Write($" blockXY=({chromaBlockX},{chromaBlockY}) planeWH=({chromaPlaneWidth},{mcChromaHeight})");
                                            DiagnosticOutput.Write($" ref[0,4..7]=[{refChroma[4]},{refChroma[5]},{refChroma[6]},{refChroma[7]}]");
                                            DiagnosticOutput.Write($" ref[stride,4..7]=[{refChroma[chromaStride + 4]},{refChroma[chromaStride + 5]},{refChroma[chromaStride + 6]},{refChroma[chromaStride + 7]}]");
                                            DiagnosticOutput.Write($" pred=[{chromaPredFinal[0]},{chromaPredFinal[1]},{chromaPredFinal[2]},{chromaPredFinal[3]}]");
                                            DiagnosticOutput.Write($" sameBuf={ReferenceEquals(refBuf, currentFrameBuffer)}");
                                            DiagnosticOutput.WriteLine();
                                        }
                    
                    if (currentSliceHeader!.HasWeightedPred)
                    {
                        MotionCompensation.WeightedPrediction(chromaPredFinal[..chromaPartSamples], chromaPredFinal,
                            currentSliceHeader.ChromaWeightL0[0, plane], currentSliceHeader.ChromaOffsetL0[0, plane],
                            (int)currentSliceHeader.ChromaLog2WeightDenom);
                    }
                }
                
                if (chromaWroteInPlace)
                    continue;

                if (TDepth.PixelShift == 0)
                {
                    // Contiguous in both buffers, so one copy per row.
                    Dsp.ReconstructDsp.CopyRows(
                        chromaPredFinal, 0, chromaPartSize,
                        buffer, chromaBlockOffset, chromaStride, chromaPartSize, chromaPartSize);
                }
                else
                {
                    for (int y = 0; y < chromaPartSize; y++)
                        for (int x = 0; x < chromaPartSize; x++)
                            WritePixel<TDepth>(buffer, chromaBlockOffset + y * chromaStride + x,
                                chromaPredFinal[y * chromaPartSize + x]);
                }
            }
        }
    }

    private void ReconstructInterMacroblock<TShape, TDepth>(ref Macroblock mb, Span<byte> buffer, int lumaOffset, int lumaStride, int uOffset, int vOffset, int chromaStride, Span<short> residualLuma, Span<short> residualChroma)
        where TShape : struct, IPictureShape

        where TDepth : struct, IPixelDepth    {
        int mbAddr = mb.Address;
        int width = context.CodedWidth;
        int height = context.CodedHeight;
        int mcHeight = currentFieldPicture ? height / 2 : height;
        int mbPixelX = mb.PositionX * 16;
        int mbPixelY = mb.PositionY * 16;
        int chromaPlaneWidth = width / 2;
        int chromaPlaneHeight = height / subHeightC;
        
        bool isMbaffFieldMb = TShape.Mbaff && mbFieldDecodingFlag != null && mbFieldDecodingFlag[mbAddr];
        int mbaffRefLumaFieldOffset = 0;
        int mbaffRefChromaFieldOffset = 0;
        if (isMbaffFieldMb)
        {
            mbPixelY = (mb.PositionY >> 1) * 16;
            mcHeight = height / 2;
            mbaffRefLumaFieldOffset = (mb.PositionY & 1) * width;
            mbaffRefChromaFieldOffset = (mb.PositionY & 1) * (width / 2);
        }
        
        // Room for a whole macroblock's prediction. Three quarters of non-skip inter
        // macroblocks carry one motion vector throughout, and those are predicted and blended
        // once instead of sixteen times - the partition-level shape FFmpeg's mc_part uses. The
        // residual stays per 4x4 block because that is the size the transform works at; only the
        // prediction is hoisted, so the delicate dequantise-and-add code below is untouched.
        Scratch.Of256<byte> predL0Storage = default;
        Span<byte> predL0 = predL0Storage;
        Scratch.Of256<byte> predL1Storage = default;
        Span<byte> predL1 = predL1Storage;
        Scratch.Of256<byte> predFinalStorage = default;
        Span<byte> predFinal = predFinalStorage;

        // Pre-allocate transform buffers based on bit depth
        Scratch.Of16<int> coeffsWideStorage = default;
        Span<int> coeffsWide = TDepth.PixelShift > 0 ? (Span<int>)coeffsWideStorage : Span<int>.Empty;
        Scratch.Of16<int> transformedWideStorage = default;
        Span<int> transformedWide = TDepth.PixelShift > 0 ? (Span<int>)transformedWideStorage : Span<int>.Empty;
        Scratch.Of16<short> transformedBlockStorage = default;
        Span<short> transformedBlock = TDepth.PixelShift == 0 ? (Span<short>)transformedBlockStorage : Span<short>.Empty;
        Scratch.Of16<short> coeffsStorage = default;
        Span<short> coeffs = TDepth.PixelShift == 0 ? (Span<short>)coeffsStorage : Span<short>.Empty;

        // Diagnostic scratch. These used to be stackalloc'd inside the sixteen-block loop,
        // where C# gives each iteration its own allocation and none of them come back until
        // the method returns.
        Scratch.Of16<short> rawCoeffsDiagStorage = default;
        Span<short> rawCoeffsDiag = rawCoeffsDiagStorage;
        Scratch.Of16<short> dequantCoeffsDiagStorage = default;
        Span<short> dequantCoeffsDiag = dequantCoeffsDiagStorage;

        bool wholeMacroblock = SkipMotionIsUniform(mbAddr, currentSliceHeader?.IsBSlice ?? false);
        int partSize = wholeMacroblock ? 16 : 4;
        int partSamples = partSize * partSize;

        // Three quarters of non-skip inter macroblocks code no luma residual at all. Where the
        // motion is also uniform the whole macroblock is one copy of the prediction, so block
        // zero can write all sixteen rows and the loop ends there.
        // Bi-prediction needs somewhere to build its second list before blending, and a
        // weighted partition has to be scaled before it lands, so both keep the temporary.
        // Everything else is predicted straight into the frame, the way mc_part_std does it.
        bool canPredictInPlace = TDepth.PixelShift == 0 && !currentSliceHeader!.HasWeightedPred;

        bool predictionOnlyMacroblock = wholeMacroblock && !mb.Transform8x8 && TDepth.PixelShift == 0
            && !residualLuma[..256].ContainsAnyExcept((short)0);

        // Luma: prediction once per partition, residual per 4x4 block
        for (int blkIdx = 0; blkIdx < 16; blkIdx++)
        {
            int sub8x8 = blkIdx >> 2;
            int scan8Pos = Scan8[blkIdx];
            
            // Block pixel position within MB
            int blkX = (((blkIdx >> 2) & 1) * 2 + (blkIdx & 1)) * 4;
            int blkY = ((blkIdx >> 3) * 2 + ((blkIdx >> 1) & 1)) * 4;
            int blockPixelX = mbPixelX + blkX;
            int blockPixelY = mbPixelY + blkY;

            // Where this block's samples sit inside the prediction, and whether it is this
            // block's turn to produce them. For a whole macroblock only block zero predicts,
            // and every block afterwards reads its own corner of what block zero wrote.
            int predAt = wholeMacroblock ? blkY * 16 + blkX : 0;
            bool predictNow = !wholeMacroblock || blkIdx == 0;
            
            // Get MVs and ref indices
            short mvL0X = mvCacheL0X[scan8Pos];
            short mvL0Y = mvCacheL0Y[scan8Pos];
            short mvL1X = mvCacheL1X[scan8Pos];
            short mvL1Y = mvCacheL1Y[scan8Pos];
            sbyte refIdxL0 = mbRefIdxL0![mbAddr * 4 + sub8x8];
            sbyte refIdxL1 = mbRefIdxL1![mbAddr * 4 + sub8x8];
            
            // Save original field ref indices before >>1 conversion for field weight lookup
            int origFieldRefL0 = refIdxL0;
            int origFieldRefL1 = refIdxL1;
            
            // MBAFF field MBs: map field-level ref indices to frame-level and compute per-ref field offsets.
            // Field ref_idx encodes both frame ref and parity: frame_ref = field_ref >> 1.
            // Even ref → same parity as current MB, odd → opposite parity.
            // Maps to FFmpeg h264_mb_template.c ref list field mapping.
            int refFieldOffL0 = mbaffRefLumaFieldOffset;
            int refFieldOffL1 = mbaffRefLumaFieldOffset;
            if (isMbaffFieldMb)
            {
                if (refIdxL0 >= 0)
                {
                    int fieldRef = refIdxL0;
                    refIdxL0 = (sbyte)(fieldRef >> 1);
                    refFieldOffL0 = ((fieldRef & 1) ^ (mb.PositionY & 1)) != 0 ? width : 0;
                }
                if (refIdxL1 >= 0)
                {
                    int fieldRef = refIdxL1;
                    refIdxL1 = (sbyte)(fieldRef >> 1);
                    refFieldOffL1 = ((fieldRef & 1) ^ (mb.PositionY & 1)) != 0 ? width : 0;
                }
            }
            
            bool usesL0 = refIdxL0 >= 0;
            bool usesL1 = refIdxL1 >= 0;

            // The prediction goes into the frame itself unless something has to be blended
            // into it first. Reading it back from there is what lets the residual be added in
            // place, which is exactly what ff_h264_idct_add does.
            int blockOffset = lumaOffset + blkY * lumaStride + blkX;
            bool wroteInPlace = canPredictInPlace && !(usesL0 && usesL1);
            Span<byte> predSource = wroteInPlace ? buffer.Slice(blockOffset) : predFinal.Slice(predAt);
            int predSourceStride = wroteInPlace ? lumaStride : partSize;
            
            // Targeted diagnostic for frext4 field picture bug
                        if (DiagnosticDumpAllMbsForPoc == currentPoc && DiagnosticOutput != null && blkIdx == 0 
                && mbAddr == DiagDumpDetailMbAddr) TraceInter1(mb);
            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceInter1(in Macroblock mb)
            {
                            DiagnosticOutput.WriteLine($"  MC-DISPATCH MB({mb.PositionX},{mb.PositionY}) addr={mbAddr}: refL0={refIdxL0} refL1={refIdxL1} mvL0=({mvL0X},{mvL0Y}) mvL1=({mvL1X},{mvL1Y}) usesL0={usesL0} usesL1={usesL1} isSkip={mb.IsSkipped} sliceType={currentSliceHeader?.Type} isBSlice={currentSliceHeader?.IsBSlice}");
                        }
            
            if (predictNow)
            {
                // Motion compensation
                if (usesL0 && usesL1)
                {
                    // Bi-prediction
                    var refBufL0 = GetReferenceBuffer(refIdxL0, 0);
                    var refBufL1 = GetReferenceBuffer(refIdxL1, 1);
                    if (refBufL0 != null)
                    {
                        int refOff = isMbaffFieldMb ? refFieldOffL0 : GetRefLumaOffset(refIdxL0, 0);
                        MotionCompensation.CompensateLuma(refBufL0.AsSpan(refOff), lumaStride,
                            predL0, partSize, mvL0X, mvL0Y,
                            blockPixelX, blockPixelY, partSize, partSize, width, mcHeight);
                    }
                    if (refBufL1 != null)
                    {
                        int refOff = isMbaffFieldMb ? refFieldOffL1 : GetRefLumaOffset(refIdxL1, 1);
                        MotionCompensation.CompensateLuma(refBufL1.AsSpan(refOff), lumaStride,
                            predL1, partSize, mvL1X, mvL1Y,
                            blockPixelX, blockPixelY, partSize, partSize, width, mcHeight);
                    }
                
                    if (refBufL0 != null && refBufL1 != null)
                    {
                        if (useImplicitWeightedPred && refIdxL0 < refPicListL0Count && refIdxL1 < refPicListL1Count)
                        {
                            int w1 = isMbaffFieldMb
                                ? implicitWeightL1Field[origFieldRefL0, origFieldRefL1, mb.PositionY & 1]
                                : implicitWeightL1[refIdxL0, refIdxL1];
                            // An implicit weight of 32 is the plain average: with a denominator of 5,
                            // ((32a + 32b + 32) >> 6) is exactly (a + b + 1) >> 1, which is what the
                            // vectorised averager computes in one instruction per sixteen samples.
                            // FFmpeg turns weighting off for the whole slice in the commonest case of
                            // this - one reference each way with symmetric POCs (h264_slice.c:705) -
                            // and testing the weight itself catches every pair that lands on 32, not
                            // only that case.
                            if (w1 == 32)
                                MotionCompensation.BiPrediction4x4(predL0[..partSamples], predL1[..partSamples], predFinal);
                            else
                                MotionCompensation.WeightedBiPrediction(predL0[..partSamples], predL1[..partSamples], predFinal,
                                    64 - w1, w1, 0, 0, 5);
                        }
                        else if (currentSliceHeader!.HasWeightedPred)
                        {
                            MotionCompensation.WeightedBiPrediction(predL0[..partSamples], predL1[..partSamples], predFinal,
                                currentSliceHeader.LumaWeightL0[refIdxL0], currentSliceHeader.LumaWeightL1[refIdxL1],
                                currentSliceHeader.LumaOffsetL0[refIdxL0], currentSliceHeader.LumaOffsetL1[refIdxL1],
                                (int)currentSliceHeader.LumaLog2WeightDenom);
                        }
                        else
                        {
                            MotionCompensation.BiPrediction4x4(predL0[..partSamples], predL1[..partSamples], predFinal);
                        }
                    
                        // Diagnostic: dump bi-prediction for specific MB
                                                if (DiagnosticDumpAllMbsForPoc == currentPoc && DiagnosticOutput != null
                            && mbAddr == DiagDumpDetailMbAddr) TraceInter2(predL0, predL1, predFinal, mb);
                        [MethodImpl(MethodImplOptions.NoInlining)]
                        void TraceInter2(Span<byte> predL0, Span<byte> predL1, Span<byte> predFinal, in Macroblock mb)
                        {
                                                    int rp0 = refIdxL0 < refPicListL0Poc.Length ? refPicListL0Poc[refIdxL0] : -1;
                                                    int rp1 = refIdxL1 < refPicListL1Poc.Length ? refPicListL1Poc[refIdxL1] : -1;
                                                    int off0 = isMbaffFieldMb ? refFieldOffL0 : GetRefLumaOffset(refIdxL0, 0);
                                                    int off1 = isMbaffFieldMb ? refFieldOffL1 : GetRefLumaOffset(refIdxL1, 1);
                                                    DiagnosticOutput.Write($"  BIPRED blk{blkIdx} ({blkX},{blkY}): mvL0=({mvL0X},{mvL0Y}) mvL1=({mvL1X},{mvL1Y}) ref=L0[{refIdxL0}]poc{rp0} L1[{refIdxL1}]poc{rp1} off0={off0} off1={off1} stride={lumaStride} mcH={mcHeight} bpx=({blockPixelX},{blockPixelY}) field={isMbaffFieldMb} origRef=({origFieldRefL0},{origFieldRefL1})");
                                                    if (currentSliceHeader!.HasWeightedPred)
                                                        DiagnosticOutput.Write($" wL0={currentSliceHeader.LumaWeightL0[refIdxL0]} oL0={currentSliceHeader.LumaOffsetL0[refIdxL0]} wL1={currentSliceHeader.LumaWeightL1[refIdxL1]} oL1={currentSliceHeader.LumaOffsetL1[refIdxL1]} denom={currentSliceHeader.LumaLog2WeightDenom}");
                                                    if (useImplicitWeightedPred)
                                                    {
                                                        int w1 = isMbaffFieldMb
                                                            ? implicitWeightL1Field[origFieldRefL0, origFieldRefL1, mb.PositionY & 1]
                                                            : implicitWeightL1[refIdxL0, refIdxL1];
                                                        DiagnosticOutput.Write($" IMPLICIT w0={64 - w1} w1={w1} origFieldRef=({origFieldRefL0},{origFieldRefL1}) mbY={mb.PositionY}");
                                                    }
                                                    DiagnosticOutput.WriteLine($"\n    predL0=[{string.Join(",", predL0.ToArray())}]\n    predL1=[{string.Join(",", predL1.ToArray())}]\n    final =[{string.Join(",", predFinal.ToArray())}]");
                                                    // Dump raw reference pixels at MC source position for blk2 (row 4-5)
                                                    if (blkIdx >= 2 && refBufL0 != null)
                                                    {
                                                        int fullX0 = blockPixelX + (mvL0X >> 2);
                                                        int fullY0 = blockPixelY + (mvL0Y >> 2);
                                                        var sb = new System.Text.StringBuilder();
                                                        sb.Append($"    L0-RAW fullPos=({fullX0},{fullY0}) fracX={mvL0X & 3}: ");
                                                        for (int ry = 0; ry < 4; ry++)
                                                        {
                                                            int srcY = Math.Clamp(fullY0 + ry, 0, mcHeight - 1);
                                                            sb.Append($"row{ry}=[");
                                                            for (int rx = -2; rx < 8; rx++)
                                                            {
                                                                int srcX = Math.Clamp(fullX0 + rx, 0, width - 1);
                                                                sb.Append($"{refBufL0[off0 + srcY * lumaStride + srcX]},");
                                                            }
                                                            sb.Append("] ");
                                                        }
                                                        DiagnosticOutput.WriteLine(sb.ToString());
                                                    }
                                                    if (blkIdx >= 2 && refBufL1 != null)
                                                    {
                                                        int fullX1 = blockPixelX + (mvL1X >> 2);
                                                        int fullY1 = blockPixelY + (mvL1Y >> 2);
                                                        var sb = new System.Text.StringBuilder();
                                                        sb.Append($"    L1-RAW fullPos=({fullX1},{fullY1}) fracX={mvL1X & 3}: ");
                                                        for (int ry = 0; ry < 4; ry++)
                                                        {
                                                            int srcY = Math.Clamp(fullY1 + ry, 0, mcHeight - 1);
                                                            sb.Append($"row{ry}=[");
                                                            for (int rx = -2; rx < 8; rx++)
                                                            {
                                                                int srcX = Math.Clamp(fullX1 + rx, 0, width - 1);
                                                                sb.Append($"{refBufL1[off1 + srcY * lumaStride + srcX]},");
                                                            }
                                                            sb.Append("] ");
                                                        }
                                                        DiagnosticOutput.WriteLine(sb.ToString());
                                                    }
                                                }
                    }
                    else if (refBufL0 != null)
                        predL0[..partSamples].CopyTo(predFinal);
                    else if (refBufL1 != null)
                        predL1[..partSamples].CopyTo(predFinal);
                    else
                        predFinal[..partSamples].Fill(128);
                }
                else if (usesL1)
                {
                    var refBuf = GetReferenceBuffer(refIdxL1, 1);
                    if (refBuf != null)
                    {
                        int refOff = isMbaffFieldMb ? refFieldOffL1 : GetRefLumaOffset(refIdxL1, 1);
                        MotionCompensation.CompensateLuma(refBuf.AsSpan(refOff), lumaStride,
                            predSource, predSourceStride, mvL1X, mvL1Y,
                            blockPixelX, blockPixelY, partSize, partSize, width, mcHeight);
                    }
                    else
                    {
                        for (int y = 0; y < partSize; y++)
                            predSource.Slice(y * predSourceStride, partSize).Clear();
                    }
                
                    if (currentSliceHeader!.HasWeightedPred)
                    {
                        MotionCompensation.WeightedPrediction(predFinal[..partSamples], predFinal,
                            currentSliceHeader.LumaWeightL1[refIdxL1], currentSliceHeader.LumaOffsetL1[refIdxL1],
                            (int)currentSliceHeader.LumaLog2WeightDenom);
                    }
                }
                else
                {
                    // L0 (default for P-slices)
                    int effectiveRefIdx = refIdxL0 >= 0 ? refIdxL0 : 0;
                    var refBuf = GetReferenceBuffer(effectiveRefIdx, 0);
                    if (refBuf != null)
                    {
                        int refOff = isMbaffFieldMb ? refFieldOffL0 : GetRefLumaOffset(effectiveRefIdx, 0);
                        MotionCompensation.CompensateLuma(refBuf.AsSpan(refOff), lumaStride,
                            predSource, predSourceStride, mvL0X, mvL0Y,
                            blockPixelX, blockPixelY, partSize, partSize, width, mcHeight);
                    
                        // Diagnostic: dump reference buffer bytes vs MC output for target POC
                                                if (DiagnosticDumpAllMbsForPoc == currentPoc && DiagnosticOutput != null && blkIdx == 0) TraceInter3(predFinal, mb);
                        [MethodImpl(MethodImplOptions.NoInlining)]
                        void TraceInter3(Span<byte> predFinal, in Macroblock mb)
                        {
                                                    int fullX = blockPixelX + (mvL0X >> 2);
                                                    int fullY = blockPixelY + (mvL0Y >> 2);
                                                    int refPos = fullY * lumaStride + fullX;
                                                    DiagnosticOutput.Write($"  REFBUF MB({mb.PositionX},{mb.PositionY}) blk0: refOff={refOff} refIdx={effectiveRefIdx} refPos={refPos} stride={lumaStride} mv=({mvL0X},{mvL0Y})");
                                                    if (refOff + refPos >= 0 && refOff + refPos + 3 * lumaStride + 3 < refBuf.Length)
                                                    {
                                                        int p = refOff + refPos;
                                                        DiagnosticOutput.Write($" ref=[{refBuf[p]},{refBuf[p+1]},{refBuf[p+2]},{refBuf[p+3]}]");
                                                        DiagnosticOutput.Write($" mc=[{predFinal[0]},{predFinal[1]},{predFinal[2]},{predFinal[3]}]");
                                                    }
                                                    DiagnosticOutput.Write($" sameBuf={ReferenceEquals(refBuf, currentFrameBuffer)}");
                                                    DiagnosticOutput.WriteLine();
                                                }
                    }
                    else
                    {
                        for (int y = 0; y < partSize; y++)
                            predSource.Slice(y * predSourceStride, partSize).Clear();
                    }
                
                    if (currentSliceHeader!.HasWeightedPred)
                    {
                        MotionCompensation.WeightedPrediction(predFinal[..partSamples], predFinal,
                            currentSliceHeader.LumaWeightL0[effectiveRefIdx], currentSliceHeader.LumaOffsetL0[effectiveRefIdx],
                            (int)currentSliceHeader.LumaLog2WeightDenom);
                    }
                }
            }

            // Diagnostic: trace prediction for ALL blocks of target POC MB(0,0)


            if (predictionOnlyMacroblock)
            {
                if (!wroteInPlace)
                    Dsp.ReconstructDsp.CopyRows(
                        predFinal, 0, 16,
                        buffer, lumaOffset, lumaStride, 16, 16);
                break;
            }

            // With the 8x8 transform the residual is added in a second pass below, so only the
            // prediction is written here. Otherwise a block whose coefficients are all zero also
            // wants nothing but the prediction: dequantising zeros gives zeros, the inverse
            // transform of zeros is zeros, and adding zero leaves the prediction untouched.
            // FFmpeg reads the same fact out of non_zero_count_cache and skips its idct.
            if (mb.Transform8x8 || !residualLuma.Slice(blkIdx * 16, 16).ContainsAnyExcept((short)0))
            {
                if (wroteInPlace)
                {
                    // Already in the frame.
                }
                else if (TDepth.PixelShift == 0)
                {
                    // Contiguous in both buffers, so one copy per row.
                    Dsp.ReconstructDsp.CopyRows(
                        predFinal, predAt, partSize,
                        buffer, blockOffset, lumaStride, 4, 4);
                }
                else
                {
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                            WritePixel<TDepth>(buffer, blockOffset + y * lumaStride + x,
                                predFinal[predAt + y * partSize + x]);
                }
            }
            else
            {
                // Inverse transform residual
                // Loaded per bit-depth arm below, straight out of the residual array.
                var ppsInter = context.ActivePps!;

                // Prediction + residual → output
                if (TDepth.PixelShift > 0)
                {
                    // 10-bit path: int32 coefficient pipeline
                    LoadBlockCoefficients(residualLuma, blkIdx * 16, coeffsWide);

                    H264Transform.Dequantize4x4WithMatrix(coeffsWide, ppsInter.ScalingMatrix4[3], mb.QuantizationParameter);
                    H264Transform.InverseTransform4x4(coeffsWide, transformedWide);

                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int val = predSource[y * predSourceStride + x] + transformedWide[y * 4 + x];
                            WritePixel<TDepth>(buffer, blockOffset + y * lumaStride + x, ClipPixel<TDepth>(val));
                        }
                    }
                }
                else
                {
                    // 8-bit path
                    LoadBlockCoefficients(residualLuma, blkIdx * 16, coeffs);

                    
                    if (isDiagnosticMb) TraceInter4(coeffs, rawCoeffsDiag);

                    [MethodImpl(MethodImplOptions.NoInlining)]

                    void TraceInter4(Span<short> coeffs, Span<short> rawCoeffsDiag)

                    {

                    coeffs.CopyTo(rawCoeffsDiag);

                    }

                    // Already dequantised: both parsers scale a category 2 coefficient against
                    // list 3 for an inter macroblock as they store it, so there is nothing to do
                    // here but transform. The wide path above still dequantises, because a
                    // coefficient above eight bits does not fit the short the parsers write to.

                    
                    if (isDiagnosticMb) TraceInter5(coeffs, dequantCoeffsDiag);

                    [MethodImpl(MethodImplOptions.NoInlining)]

                    void TraceInter5(Span<short> coeffs, Span<short> dequantCoeffsDiag)

                    {

                    coeffs.CopyTo(dequantCoeffsDiag);

                    }

                    H264Transform.InverseTransform4x4(coeffs, transformedBlock);

                    
                    if (isDiagnosticMb && DiagnosticOutput != null) TraceInter6(predFinal, transformedBlock, residualLuma.Slice(blkIdx * 16, 16), rawCoeffsDiag, dequantCoeffsDiag, mb);

                    [MethodImpl(MethodImplOptions.NoInlining)]

                    void TraceInter6(Span<byte> predFinal, Span<short> transformedBlock, Span<short> scanCoeffs, Span<short> rawCoeffsDiag, Span<short> dequantCoeffsDiag, in Macroblock mb)

                    {

                                            DiagnosticOutput.Write($"  blk{blkIdx} pos=({blockPixelX},{blockPixelY}) mv=({mvL0X},{mvL0Y})/({mvL1X},{mvL1Y}) ref=({refIdxL0},{refIdxL1})");

                                            DiagnosticOutput.Write($" pred=[{predFinal[0]},{predFinal[1]},{predFinal[2]},{predFinal[3]}]");

                                            DiagnosticOutput.Write($" res=[{transformedBlock[0]},{transformedBlock[1]},{transformedBlock[2]},{transformedBlock[3]}]");

                                            DiagnosticOutput.Write($" scan=[{string.Join(",", scanCoeffs.ToArray())}]");

                                            DiagnosticOutput.Write($" raw=[{string.Join(",", rawCoeffsDiag.ToArray())}]");

                                            DiagnosticOutput.Write($" dq=[{string.Join(",", dequantCoeffsDiag.ToArray())}]");

                                            bool hasResidual = false;

                                            for (int k = 0; k < 16; k++) if (scanCoeffs[k] != 0) { hasResidual = true; break; }

                                            DiagnosticOutput.Write($" hasRes={hasResidual} cbpL={mb.CodedBlockPatternLuma}");

                                            bool wp = currentSliceHeader!.HasWeightedPred;

                                            bool bipred = usesL0 && usesL1;

                                            bool implW = useImplicitWeightedPred && bipred;

                                            int wpWeight = 0, wpOffset = 0, wpDenom = 0;

                                            if (wp && !bipred)

                                            {

                                                int eidx = refIdxL0 >= 0 ? refIdxL0 : 0;

                                                wpWeight = currentSliceHeader.LumaWeightL0[eidx];

                                                wpOffset = currentSliceHeader.LumaOffsetL0[eidx];

                                                wpDenom = (int)currentSliceHeader.LumaLog2WeightDenom;

                                            }

                                            DiagnosticOutput.Write($" wp={wp} bipred={bipred} implW={implW} w={wpWeight} o={wpOffset} d={wpDenom}");

                                            int mcFullX = blockPixelX + (mvL0X >> 2);

                                            int mcFullY = blockPixelY + (mvL0Y >> 2);

                                            int mcFracX = mvL0X & 3;

                                            int mcFracY = mvL0Y & 3;

                                            var refBufDiag = GetReferenceBuffer(refIdxL0 >= 0 ? refIdxL0 : 0, 0);

                                            if (refBufDiag != null)

                                            {

                                                int clampedX = Math.Clamp(mcFullX, 0, width - 1);

                                                int clampedY = Math.Clamp(mcFullY, 0, height - 1);

                                                byte refVal = refBufDiag[clampedY * lumaStride + clampedX];

                                                byte refSamePos = refBufDiag[Math.Clamp(blockPixelY, 0, height-1) * lumaStride + Math.Clamp(blockPixelX, 0, width-1)];

                                                DiagnosticOutput.Write($" refAtMC=({clampedX},{clampedY})={refVal} refAtSame={refSamePos} frac=({mcFracX},{mcFracY})");

                                            }

                                            DiagnosticOutput.WriteLine();

                                        }

                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int val = predSource[y * predSourceStride + x] + transformedBlock[y * 4 + x];
                            WritePixel<TDepth>(buffer, blockOffset + y * lumaStride + x, ClipPixel<TDepth>(val));
                        }
                    }
                }
                

            }
        }

        // Second pass: add 8x8 residuals when using 8x8 transform
        if (mb.Transform8x8)
        {
            Scratch.Of64<int> coeffs8x8WideStorage = default;
            Span<int> coeffs8x8Wide = TDepth.PixelShift > 0 ? (Span<int>)coeffs8x8WideStorage : Span<int>.Empty;
            Scratch.Of64<int> transformed8x8WideStorage = default;
            Span<int> transformed8x8Wide = TDepth.PixelShift > 0 ? (Span<int>)transformed8x8WideStorage : Span<int>.Empty;
            Scratch.Of64<short> coeffs8x8Storage = default;
            Span<short> coeffs8x8 = TDepth.PixelShift == 0 ? (Span<short>)coeffs8x8Storage : Span<short>.Empty;
            Scratch.Of64<short> transformed8x8Storage = default;
            Span<short> transformed8x8 = TDepth.PixelShift == 0 ? (Span<short>)transformed8x8Storage : Span<short>.Empty;

            for (int i8x8 = 0; i8x8 < 4; i8x8++)
            {
                if ((mb.CodedBlockPatternLuma & (1 << i8x8)) == 0)
                    continue;
                    
                // TEMP DIAG: dump first non-zero 8x8 block coefficients
                                if (DiagnosticDumpAllMbsForFrame >= 0 && DiagnosticOutput != null) TraceInter7(mb, residualLuma);
                [MethodImpl(MethodImplOptions.NoInlining)]
                void TraceInter7(in Macroblock mb, ReadOnlySpan<short> residualLuma)
                {
                                    int resOffset = i8x8 * 64;
                                    DiagnosticOutput.Write($"  mb={mb.Address} 8x8[{i8x8}] qp={mb.QuantizationParameter} raw=[");
                                    for (int j = 0; j < 64; j++) DiagnosticOutput.Write($"{residualLuma[resOffset + j]}{(j < 63 ? "," : "")}");
                                    DiagnosticOutput.WriteLine("]");
                                }

                // 8x8 block pixel position within MB
                int blk8x = (i8x8 & 1) * 8;
                int blk8y = (i8x8 >> 1) * 8;

                if (!SkipInter8x8Residual)
                {
                    int resOffset2 = i8x8 * 64;
                    var pps8x8 = context.ActivePps!;
                    int blockOffset = lumaOffset + blk8y * lumaStride + blk8x;

                    if (TDepth.PixelShift > 0)
                    {
                        // 10-bit path: int32 coefficient pipeline
                        for (int k = 0; k < 64; k++)
                            coeffs8x8Wide[k] = residualLuma[resOffset2 + k];

                        H264Transform.Dequantize8x8WithMatrix(coeffs8x8Wide, pps8x8.ScalingMatrix8[3], mb.QuantizationParameter);
                        H264Transform.InverseTransform8x8(coeffs8x8Wide, transformed8x8Wide);

                        for (int y = 0; y < 8; y++)
                        {
                            for (int x = 0; x < 8; x++)
                            {
                                int val = ReadPixel<TDepth>(buffer, blockOffset + y * lumaStride + x) + transformed8x8Wide[y * 8 + x];
                                WritePixel<TDepth>(buffer, blockOffset + y * lumaStride + x, ClipPixel<TDepth>(val));
                            }
                        }
                    }
                    else
                    {
                        // 8-bit path
                        residualLuma.Slice(resOffset2, 64).CopyTo(coeffs8x8);
                        // Already dequantised at the parser's store, against the 8x8 inter
                        // list. The wide path above still does it here.
                        H264Transform.InverseTransform8x8(coeffs8x8, transformed8x8);

                        // Motion compensation already wrote the prediction into the frame, so
                        // this is FFmpeg's idct8_add: add and clip what is already there.
                        Dsp.ReconstructDsp.AddResidualInPlace8Bit(
                            transformed8x8, buffer, blockOffset, lumaStride, 8);
                    }
                }
            }
        }
        
        // Chroma: per-4x4 block MC + residual (skip for monochrome)
        if (context.ActiveSps!.ChromaFormat != ChromaFormat.Monochrome)
            ReconstructInterChromaMc<TShape, TDepth>(ref mb, buffer, uOffset, vOffset, chromaStride, residualChroma,
                mbAddr, mbPixelX, mbPixelY, chromaPlaneWidth, chromaPlaneHeight, wholeMacroblock);
    }
    
    /// <summary>
    /// Chroma inter reconstruction with proper motion compensation.
    /// For 4:2:0, each chroma 4x4 block corresponds to an 8x8 luma region.
    /// Uses the MV from the top-left 4x4 luma block of each 8x8 region.
    /// </summary>
    private void ReconstructInterChromaMc<TShape, TDepth>(ref Macroblock mb, Span<byte> buffer, int uOffset, int vOffset, int chromaStride,
        Span<short> residualChroma, int mbAddr, int mbPixelX, int mbPixelY, int chromaPlaneWidth, int chromaPlaneHeight, bool wholeMacroblock)
        where TShape : struct, IPictureShape

        where TDepth : struct, IPixelDepth    {
        var pps = context.ActivePps!;
        int qpICb = Math.Clamp(mb.QuantizationParameter + pps.ChromaQpIndexOffset, 0, maxQp);
        int chromaQpCb = chromaQpTable[qpICb];
        int qpICr = Math.Clamp(mb.QuantizationParameter + pps.SecondChromaQpIndexOffset, 0, maxQp);
        int chromaQpCr = chromaQpTable[qpICr];
        
        int chromaMbX = mbPixelX / 2;
        int chromaMbY = mbPixelY / subHeightC;
        int lumaStride = context.CodedWidth;
        int lumaHeight = context.CodedHeight;
        int lumaPlaneSize = lumaStride * lumaHeight; // Always frame-level
        int chromaPlaneSize = chromaPlaneWidth * chromaPlaneHeight;
        int mcChromaHeight = currentFieldPicture ? chromaPlaneHeight / 2 : chromaPlaneHeight;
        
        bool isMbaffFieldMb = TShape.Mbaff && mbFieldDecodingFlag != null && mbFieldDecodingFlag[mbAddr];
        int mbaffRefChromaFieldOffset = 0;
        if (isMbaffFieldMb)
        {
            chromaMbY = (mb.PositionY >> 1) * (16 / subHeightC);
            mcChromaHeight = chromaPlaneHeight / 2;
            mbaffRefChromaFieldOffset = (mb.PositionY & 1) * chromaPlaneWidth;
        }
        
        Scratch.Of4<short> chromaDcStorage = default;
        Span<short> chromaDc = chromaDcStorage;
        // One 8x8 partition per plane when the macroblock's motion is uniform, otherwise four
        // 4x4 blocks built out of 2x2 sub-blocks. The narrow path produced four pixels per call
        // and paid a whole setup - fractional position, bounds test, weight products - for each
        // of them, thirty-two times per macroblock. FFmpeg makes one call per partition per plane.
        Scratch.Of64<byte> predL0Storage = default;
        Span<byte> predL0 = predL0Storage;
        Scratch.Of64<byte> predL1Storage = default;
        Span<byte> predL1 = predL1Storage;
        Scratch.Of64<byte> predFinalStorage = default;
        Span<byte> predFinal = predFinalStorage;


        // Same terms as luma: one list and no weighting means the prediction is written into
        // the frame, and the residual is then added to it in place.
        bool canPredictInPlace = TDepth.PixelShift == 0 && !currentSliceHeader!.HasWeightedPred;
        int chromaPartSize = wholeMacroblock ? 8 : 4;
        int chromaPartSamples = chromaPartSize * chromaPartSize;
        int chromaSubSize = wholeMacroblock ? 8 : 2;
        int chromaSubCount = wholeMacroblock ? 1 : 4;
        
        // Pre-allocate transform buffers based on bit depth
        Scratch.Of4<int> chromaDcWideStorage = default;
        Span<int> chromaDcWide = TDepth.PixelShift > 0 ? (Span<int>)chromaDcWideStorage : Span<int>.Empty;
        Scratch.Of16<int> blockCoeffsWideStorage = default;
        Span<int> blockCoeffsWide = TDepth.PixelShift > 0 ? (Span<int>)blockCoeffsWideStorage : Span<int>.Empty;
        Scratch.Of16<int> transformedWideStorage = default;
        Span<int> transformedWide = TDepth.PixelShift > 0 ? (Span<int>)transformedWideStorage : Span<int>.Empty;
        Scratch.Of16<short> blockCoeffsStorage = default;
        Span<short> blockCoeffs = TDepth.PixelShift == 0 ? (Span<short>)blockCoeffsStorage : Span<short>.Empty;
        Scratch.Of16<short> transformedBlockStorage = default;
        Span<short> transformedBlock = TDepth.PixelShift == 0 ? (Span<short>)transformedBlockStorage : Span<short>.Empty;
        
        bool hasChromaResidual = mb.CodedBlockPatternChroma > 0;
        
        // DIAGNOSTIC: trace first bottom-field inter MB chroma params
                if (DiagnosticOutput != null && currentPictureStructure == ReferenceFrame.PictBottomField && mbAddr < 2) TraceInterChroma1(mb);
        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceInterChroma1(in Macroblock mb)
        {
                    sbyte r0 = mbRefIdxL0![mbAddr * 4];
                    sbyte r1 = mbRefIdxL1![mbAddr * 4];
                    int s8 = Scan8[0];
                    var rb = GetReferenceBuffer(r0 >= 0 ? r0 : 0, 0);
                    DiagnosticOutput.WriteLine($"  INTER-CHROMA MB({mb.PositionX},{mb.PositionY}) addr={mbAddr} refL0={r0} refL1={r1} mv=({mvCacheL0X[s8]},{mvCacheL0Y[s8]}) chromaStride={chromaStride} chromaOff={GetRefLumaOffset(r0 >= 0 ? r0 : 0, 0) / 2} hasResidual={hasChromaResidual} sameBuf={ReferenceEquals(rb, currentFrameBuffer)}");
                    // Dump reference pixel values at chroma position (5, 0) for debugging
                    if (mbAddr == 0 && rb != null)
                    {
                        int cOff = GetRefLumaOffset(r0 >= 0 ? r0 : 0, 0) / 2;
                        var refCb = rb.AsSpan(lumaStride * lumaHeight + cOff);
                        DiagnosticOutput.Write($"    REF-CB row0[0..15]=[");
                        for (int xx = 0; xx < 16; xx++) DiagnosticOutput.Write($"{refCb[xx]:X2} ");
                        DiagnosticOutput.Write($"] row1[0..15]=[");
                        for (int xx = 0; xx < 16; xx++) DiagnosticOutput.Write($"{refCb[chromaStride + xx]:X2} ");
                        DiagnosticOutput.WriteLine("]");
                    }
                }
        
        // Process both Cb and Cr planes
        // The four 2x2 chroma sub-blocks of an 8x8 partition share a motion vector whenever that
        // partition is not sub-divided further - measured at 2,000,000 of 2,000,000 quadrants on a
        // 1080p stream. Where they do, the partition is one four-wide call rather than four
        // two-wide ones, which is what lets the vector kernel take it at all: two samples is
        // below anything it can load, so a third of chroma motion compensation used to run
        // scalar. On its own this collapse saves about what deciding it costs and is not worth
        // doing; it earns its place by feeding BilinearChromaRow4.
        //
        // Decided here, once per partition, rather than inside the plane loop where it would be
        // asked twice for the same answer, and reading only the lists a partition actually uses:
        // in a P slice the L1 motion cache is never otherwise touched.
        int uniformPartitions = 0;
        if (!wholeMacroblock)
        {
            for (int part = 0; part < 4; part++)
            {
                bool partUsesL0 = mbRefIdxL0![mbAddr * 4 + part] >= 0;
                bool partUsesL1 = mbRefIdxL1![mbAddr * 4 + part] >= 0;
                int first = Scan8[part * 4];
                bool uniform = true;

                for (int q = 1; q < 4 && uniform; q++)
                {
                    int at = Scan8[part * 4 + q];
                    if (partUsesL0
                        && (mvCacheL0X[at] != mvCacheL0X[first] || mvCacheL0Y[at] != mvCacheL0Y[first]))
                        uniform = false;
                    else if (partUsesL1
                        && (mvCacheL1X[at] != mvCacheL1X[first] || mvCacheL1Y[at] != mvCacheL1Y[first]))
                        uniform = false;
                }

                if (uniform)
                    uniformPartitions |= 1 << part;
            }
        }
        for (int plane = 0; plane < 2; plane++)
        {
            int planeOffset = plane == 0 ? uOffset : vOffset;
            int dcBaseIdx = plane == 0 ? chromaCbDcBase : chromaCrDcBase;
            int acBaseIdx = plane == 0 ? 0 : chromaCrAcBase;
            int planeChromaQp = plane == 0 ? chromaQpCb : chromaQpCr;
            int planeQpDiv6 = planeChromaQp / 6;
            int planeQpMod6 = planeChromaQp % 6;
            
            // DC coefficients
            if (hasChromaResidual)
            {
                chromaDc[0] = residualChroma[dcBaseIdx];
                chromaDc[1] = residualChroma[dcBaseIdx + 1];
                chromaDc[2] = residualChroma[dcBaseIdx + 2];
                chromaDc[3] = residualChroma[dcBaseIdx + 3];
                if (TDepth.PixelShift > 0)
                {
                    H264Transform.HadamardInverse2x2(chromaDc, chromaDcWide);
                    DequantizeChromaDcCoefficients(chromaDcWide, planeQpDiv6, planeQpMod6, pps.ScalingMatrix4[4 + plane][0]);
                }
                else
                {
                    InverseHadamard2x2(chromaDc);
                    DequantizeChromaDcCoefficients(chromaDc, planeQpDiv6, planeQpMod6, pps.ScalingMatrix4[4 + plane][0]);
                }
            }
            
            for (int blkIdx = 0; blkIdx < 4; blkIdx++)
            {
                int blkX = (blkIdx % 2) * 4;
                int blkY = (blkIdx / 2) * 4;
                int chromaBlockX = chromaMbX + blkX;
                int chromaBlockY = chromaMbY + blkY;

                // Where this block's samples sit inside the prediction, and whether it is this
                // block's turn to produce them. For a whole partition only block zero predicts.
                int predAt = wholeMacroblock ? blkY * 8 + blkX : 0;
                bool predictNow = !wholeMacroblock || blkIdx == 0;
                
                // Per-2x2 sub-block chroma MC: each luma 4x4 within the 8x8 region
                // may have a different MV (sub-8x8 partitions), so we MC each 2x2
                // chroma sub-block independently using its corresponding luma 4x4 MV
                int lumaBase = blkIdx * 4;
                sbyte refIdxL0 = mbRefIdxL0![mbAddr * 4 + blkIdx];
                sbyte refIdxL1 = mbRefIdxL1![mbAddr * 4 + blkIdx];
                
                // Save original field ref indices before >>1 conversion for field weight lookup
                int origChromaFieldRefL0 = refIdxL0;
                int origChromaFieldRefL1 = refIdxL1;
                
                // MBAFF field MBs: map field-level ref indices to frame-level and compute per-ref chroma field offsets.
                int refChromaFieldOffL0 = mbaffRefChromaFieldOffset;
                int refChromaFieldOffL1 = mbaffRefChromaFieldOffset;
                if (isMbaffFieldMb)
                {
                    if (refIdxL0 >= 0)
                    {
                        int fieldRef = refIdxL0;
                        refIdxL0 = (sbyte)(fieldRef >> 1);
                        refChromaFieldOffL0 = ((fieldRef & 1) ^ (mb.PositionY & 1)) != 0 ? chromaPlaneWidth : 0;
                    }
                    if (refIdxL1 >= 0)
                    {
                        int fieldRef = refIdxL1;
                        refIdxL1 = (sbyte)(fieldRef >> 1);
                        refChromaFieldOffL1 = ((fieldRef & 1) ^ (mb.PositionY & 1)) != 0 ? chromaPlaneWidth : 0;
                    }
                }
                
                bool usesL0 = refIdxL0 >= 0;
                bool usesL1 = refIdxL1 >= 0;

                // Where this block's prediction lives, and whether the frame already has it.
                int blockOffset = planeOffset + blkY * chromaStride + blkX;
                bool wroteInPlace = canPredictInPlace && !(usesL0 && usesL1);
                Span<byte> predSource = wroteInPlace
                    ? buffer.Slice(blockOffset) : predFinal.Slice(predAt);
                int predSourceStride = wroteInPlace ? chromaStride : chromaPartSize;
                
                // Field chroma MV parity adjustment (H.264 spec 8.4.2.1, FFmpeg h264_mb.c:289-293)
                // For YUV420 field pictures and MBAFF field MBs, adjust chroma MV Y by ±2
                // (1/8-pel) based on the parity difference between current and reference fields.
                // FFmpeg: my += 2 * ((sl->mb_y & 1) - (pic->reference - 1));
                int chromaAdjL0 = 0, chromaAdjL1 = 0;
                if (currentFieldPicture)
                {
                    int curParity = currentPictureStructure == ReferenceFrame.PictBottomField ? 1 : 0;
                    int effectiveL0 = refIdxL0 >= 0 ? refIdxL0 : 0;
                    chromaAdjL0 = 2 * (curParity - (GetRefLumaOffset(effectiveL0, 0) > 0 ? 1 : 0));
                    if (usesL1)
                        chromaAdjL1 = 2 * (curParity - (GetRefLumaOffset(refIdxL1, 1) > 0 ? 1 : 0));
                }
                else if (isMbaffFieldMb)
                {
                    // MBAFF field MBs: curParity from mb_y, refParity from field offset.
                    // refChromaFieldOffL0/L1 already computed per-ref at lines above.
                    int curParity = mb.PositionY & 1;
                    if (refIdxL0 >= 0)
                        chromaAdjL0 = 2 * (curParity - (refChromaFieldOffL0 > 0 ? 1 : 0));
                    if (usesL1)
                        chromaAdjL1 = 2 * (curParity - (refChromaFieldOffL1 > 0 ? 1 : 0));
                }
                
                if (predictNow)
                {
                    int chromaCallSize = chromaSubSize;
                    int chromaCallCount = chromaSubCount;
                    if (chromaSubCount == 4 && (uniformPartitions & (1 << blkIdx)) != 0)
                    {
                        chromaCallSize = 4;
                        chromaCallCount = 1;
                    }

                    // Motion compensate the partition, or its 2x2 sub-blocks when motion varies.
                    if (usesL0 && usesL1)
                    {
                        var refBufL0 = GetReferenceBuffer(refIdxL0, 0);
                        var refBufL1 = GetReferenceBuffer(refIdxL1, 1);
                        int refChromaOffL0 = isMbaffFieldMb ? refChromaFieldOffL0 : GetRefLumaOffset(refIdxL0, 0) / 2;
                        int refChromaOffL1 = isMbaffFieldMb ? refChromaFieldOffL1 : GetRefLumaOffset(refIdxL1, 1) / 2;
                        ReadOnlySpan<byte> refChromaL0 = refBufL0 != null ? refBufL0.AsSpan(lumaPlaneSize + plane * chromaPlaneSize + refChromaOffL0) : ReadOnlySpan<byte>.Empty;
                        ReadOnlySpan<byte> refChromaL1 = refBufL1 != null ? refBufL1.AsSpan(lumaPlaneSize + plane * chromaPlaneSize + refChromaOffL1) : ReadOnlySpan<byte>.Empty;
                    
                        // One call per list for the whole partition.
                        for (int sub = 0; sub < chromaCallCount; sub++)
                        {
                            int subX = (sub & 1) * chromaCallSize;
                            int subY = (sub >> 1) * chromaCallSize;
                            int scan8Pos = Scan8[lumaBase + sub];
                        
                            if (!refChromaL0.IsEmpty)
                            {
                                MotionCompensation.CompensateChroma(refChromaL0, chromaStride,
                                    predL0.Slice(subY * chromaPartSize + subX), chromaPartSize,
                                    mvCacheL0X[scan8Pos], (short)(mvCacheL0Y[scan8Pos] + chromaAdjL0),
                                    chromaBlockX + subX, chromaBlockY + subY, chromaCallSize, chromaCallSize,
                                    chromaPlaneWidth, mcChromaHeight);
                            }
                            if (!refChromaL1.IsEmpty)
                            {
                                MotionCompensation.CompensateChroma(refChromaL1, chromaStride,
                                    predL1.Slice(subY * chromaPartSize + subX), chromaPartSize,
                                    mvCacheL1X[scan8Pos], (short)(mvCacheL1Y[scan8Pos] + chromaAdjL1),
                                    chromaBlockX + subX, chromaBlockY + subY, chromaCallSize, chromaCallSize,
                                    chromaPlaneWidth, mcChromaHeight);
                            }
                        }
                    
                        if (!refChromaL0.IsEmpty && !refChromaL1.IsEmpty)
                        {
                            if (useImplicitWeightedPred && refIdxL0 < refPicListL0Count && refIdxL1 < refPicListL1Count)
                            {
                                int w1 = isMbaffFieldMb
                                    ? implicitWeightL1Field[origChromaFieldRefL0, origChromaFieldRefL1, mb.PositionY & 1]
                                    : implicitWeightL1[refIdxL0, refIdxL1];
                                // An implicit weight of 32 is the plain average: with a denominator of 5,
                                // ((32a + 32b + 32) >> 6) is exactly (a + b + 1) >> 1, which is what the
                                // vectorised averager computes in one instruction per sixteen samples.
                                // FFmpeg turns weighting off for the whole slice in the commonest case of
                                // this - one reference each way with symmetric POCs (h264_slice.c:705) -
                                // and testing the weight itself catches every pair that lands on 32, not
                                // only that case.
                                if (w1 == 32)
                                    MotionCompensation.BiPrediction4x4(predL0[..chromaPartSamples], predL1[..chromaPartSamples], predFinal);
                                else
                                    MotionCompensation.WeightedBiPrediction(predL0[..chromaPartSamples], predL1[..chromaPartSamples], predFinal,
                                        64 - w1, w1, 0, 0, 5);
                            }
                            else if (currentSliceHeader!.HasWeightedPred)
                            {
                                MotionCompensation.WeightedBiPrediction(predL0[..chromaPartSamples], predL1[..chromaPartSamples], predFinal,
                                    currentSliceHeader.ChromaWeightL0[refIdxL0, plane], currentSliceHeader.ChromaWeightL1[refIdxL1, plane],
                                    currentSliceHeader.ChromaOffsetL0[refIdxL0, plane], currentSliceHeader.ChromaOffsetL1[refIdxL1, plane],
                                    (int)currentSliceHeader.ChromaLog2WeightDenom);
                            }
                            else
                            {
                                MotionCompensation.BiPrediction4x4(predL0[..chromaPartSamples], predL1[..chromaPartSamples], predFinal);
                            }
                        }
                        else if (!refChromaL0.IsEmpty)
                            predL0[..chromaPartSamples].CopyTo(predFinal);
                        else if (!refChromaL1.IsEmpty)
                            predL1[..chromaPartSamples].CopyTo(predFinal);
                        else
                            predFinal[..chromaPartSamples].Fill(128);
                    }
                    else if (usesL1)
                    {
                        var refBuf = GetReferenceBuffer(refIdxL1, 1);
                        int refChromaOff = isMbaffFieldMb ? refChromaFieldOffL1 : GetRefLumaOffset(refIdxL1, 1) / 2;
                        ReadOnlySpan<byte> refChroma = refBuf != null ? refBuf.AsSpan(lumaPlaneSize + plane * chromaPlaneSize + refChromaOff) : ReadOnlySpan<byte>.Empty;
                        if (!refChroma.IsEmpty)
                        {
                            for (int sub = 0; sub < chromaCallCount; sub++)
                            {
                                int subX = (sub & 1) * chromaCallSize;
                                int subY = (sub >> 1) * chromaCallSize;
                                int scan8Pos = Scan8[lumaBase + sub];
                                MotionCompensation.CompensateChroma(refChroma, chromaStride,
                                    predSource.Slice(subY * predSourceStride + subX), predSourceStride,
                                    mvCacheL1X[scan8Pos], (short)(mvCacheL1Y[scan8Pos] + chromaAdjL1),
                                    chromaBlockX + subX, chromaBlockY + subY, chromaCallSize, chromaCallSize,
                                    chromaPlaneWidth, mcChromaHeight);
                            }
                        }
                        else
                        {
                            for (int y = 0; y < chromaPartSize; y++)
                                predSource.Slice(y * predSourceStride, chromaPartSize).Clear();
                        }
                    
                        if (currentSliceHeader!.HasWeightedPred)
                        {
                            MotionCompensation.WeightedPrediction(predFinal[..chromaPartSamples], predFinal,
                                currentSliceHeader.ChromaWeightL1[refIdxL1, plane], currentSliceHeader.ChromaOffsetL1[refIdxL1, plane],
                                (int)currentSliceHeader.ChromaLog2WeightDenom);
                        }
                    }
                    else
                    {
                        int effectiveRefIdx = refIdxL0 >= 0 ? refIdxL0 : 0;
                        var refBuf = GetReferenceBuffer(effectiveRefIdx, 0);
                        int refChromaOff = isMbaffFieldMb ? refChromaFieldOffL0 : GetRefLumaOffset(effectiveRefIdx, 0) / 2;
                        ReadOnlySpan<byte> refChroma = refBuf != null ? refBuf.AsSpan(lumaPlaneSize + plane * chromaPlaneSize + refChromaOff) : ReadOnlySpan<byte>.Empty;
                        if (!refChroma.IsEmpty)
                        {
                            for (int sub = 0; sub < chromaCallCount; sub++)
                            {
                                int subX = (sub & 1) * chromaCallSize;
                                int subY = (sub >> 1) * chromaCallSize;
                                int scan8Pos = Scan8[lumaBase + sub];
                                MotionCompensation.CompensateChroma(refChroma, chromaStride,
                                    predSource.Slice(subY * predSourceStride + subX), predSourceStride,
                                    mvCacheL0X[scan8Pos], (short)(mvCacheL0Y[scan8Pos] + chromaAdjL0),
                                    chromaBlockX + subX, chromaBlockY + subY, chromaCallSize, chromaCallSize,
                                    chromaPlaneWidth, mcChromaHeight);
                            }
                        }
                        else
                        {
                            for (int y = 0; y < chromaPartSize; y++)
                                predSource.Slice(y * predSourceStride, chromaPartSize).Clear();
                        }
                    
                        if (currentSliceHeader!.HasWeightedPred)
                        {
                            MotionCompensation.WeightedPrediction(predFinal[..chromaPartSamples], predFinal,
                                currentSliceHeader.ChromaWeightL0[effectiveRefIdx, plane], currentSliceHeader.ChromaOffsetL0[effectiveRefIdx, plane],
                                (int)currentSliceHeader.ChromaLog2WeightDenom);
                        }
                    }
                }

                // Add residual
                // Nothing coded and one motion vector throughout: the plane's whole 8x8 is a
                // copy of the prediction, so block zero writes all eight rows and the loop ends.
                if (wholeMacroblock && !hasChromaResidual && TDepth.PixelShift == 0)
                {
                    if (!wroteInPlace)
                        Dsp.ReconstructDsp.CopyRows(
                            predFinal, 0, 8,
                            buffer, planeOffset, chromaStride, 8, 8);
                    break;
                }

                if (hasChromaResidual)
                {
                    if (TDepth.PixelShift > 0)
                    {
                        LoadAcCoefficients(residualChroma, acBaseIdx + blkIdx * 16, blockCoeffsWide);
                        blockCoeffsWide[0] = chromaDcWide[blkIdx];

                        H264Transform.Dequantize4x4WithMatrix(blockCoeffsWide.Slice(1, 15), pps.ScalingMatrix4[4 + plane], planeChromaQp, rasterOffset: 1);
                        H264Transform.InverseTransform4x4(blockCoeffsWide, transformedWide);

                        for (int y = 0; y < 4; y++)
                        {
                            for (int x = 0; x < 4; x++)
                            {
                                int val = predSource[y * predSourceStride + x] + transformedWide[y * 4 + x];
                                WritePixel<TDepth>(buffer, blockOffset + y * chromaStride + x, ClipPixel<TDepth>(val));
                            }
                        }
                    }
                    else
                    {
                        LoadAcCoefficients(residualChroma, acBaseIdx + blkIdx * 16, blockCoeffs);
                        blockCoeffs[0] = chromaDc[blkIdx];

                        // Dequantised at the parser's store, against this component's own quantiser.
                        H264Transform.InverseTransform4x4(blockCoeffs, transformedBlock);

                        for (int y = 0; y < 4; y++)
                        {
                            for (int x = 0; x < 4; x++)
                            {
                                int val = predSource[y * predSourceStride + x] + transformedBlock[y * 4 + x];
                                WritePixel<TDepth>(buffer, blockOffset + y * chromaStride + x, ClipPixel<TDepth>(val));
                            }
                        }
                    }
                }
                else if (wroteInPlace)
                {
                    // No residual, and the prediction is already in the frame.
                }
                else if (TDepth.PixelShift == 0)
                {
                    // No residual - the prediction is the answer, one copy per row.
                    Dsp.ReconstructDsp.CopyRows(
                        predFinal, predAt, chromaPartSize,
                        buffer, blockOffset, chromaStride, 4, 4);
                }
                else
                {
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                            WritePixel<TDepth>(buffer, blockOffset + y * chromaStride + x,
                                predFinal[predAt + y * chromaPartSize + x]);
                }
            }
        }
    }
    
    private void ReconstructInterChroma<TDepth>(ref Macroblock mb, Span<byte> buffer, Span<byte> refBuffer, int uOffset, int vOffset, int chromaStride, Span<short> residualChroma)
        where TDepth : struct, IPixelDepth
    {
        // Calculate separate chroma QPs for Cb and Cr
        var pps = context.ActivePps!;
        int qpICb = Math.Clamp(mb.QuantizationParameter + pps.ChromaQpIndexOffset, 0, maxQp);
        int chromaQpCb = chromaQpTable[qpICb];
        int qpDiv6Cb = chromaQpCb / 6;
        int qpMod6Cb = chromaQpCb % 6;
        
        int qpICr = Math.Clamp(mb.QuantizationParameter + pps.SecondChromaQpIndexOffset, 0, maxQp);
        int chromaQpCr = chromaQpTable[qpICr];
        int qpDiv6Cr = chromaQpCr / 6;
        int qpMod6Cr = chromaQpCr % 6;
        
        Scratch.Of4<short> chromaDcStorage = default;
        Span<short> chromaDc = chromaDcStorage;
        bool hasChromaResidual = mb.CodedBlockPatternChroma > 0;
        
        if (hasChromaResidual)
        {
            if (TDepth.PixelShift > 0)
            {
                // 10-bit path
                Scratch.Of4<int> chromaDcWideStorage = default;
                Span<int> chromaDcWide = chromaDcWideStorage;
                Scratch.Of16<int> blockCoeffsWideStorage = default;
                Span<int> blockCoeffsWide = blockCoeffsWideStorage;
                Scratch.Of16<int> transformedWideStorage = default;
                Span<int> transformedWide = transformedWideStorage;

                // Process Cb (U plane)
                for (int i = 0; i < 4; i++)
                    chromaDc[i] = residualChroma[chromaCbDcBase + i];
                H264Transform.HadamardInverse2x2(chromaDc, chromaDcWide);
                DequantizeChromaDcCoefficients(chromaDcWide, qpDiv6Cb, qpMod6Cb, pps.ScalingMatrix4[4][0]);

                for (int blkIdx = 0; blkIdx < 4; blkIdx++)
                {
                    int blkX = (blkIdx % 2) * 4;
                    int blkY = (blkIdx / 2) * 4;

                    LoadAcCoefficients(residualChroma, blkIdx * 16, blockCoeffsWide);
                    blockCoeffsWide[0] = chromaDcWide[blkIdx];

                    H264Transform.Dequantize4x4WithMatrix(blockCoeffsWide.Slice(1, 15), pps.ScalingMatrix4[4], chromaQpCb, rasterOffset: 1);
                    H264Transform.InverseTransform4x4(blockCoeffsWide, transformedWide);

                    int blockOffset = uOffset + blkY * chromaStride + blkX;
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int refVal = ReadPixel<TDepth>(refBuffer, blockOffset + y * chromaStride + x);
                            int val = refVal + transformedWide[y * 4 + x];
                            WritePixel<TDepth>(buffer, blockOffset + y * chromaStride + x, ClipPixel<TDepth>(val));
                        }
                    }
                }

                // Process Cr (V plane)
                for (int i = 0; i < 4; i++)
                    chromaDc[i] = residualChroma[chromaCrDcBase + i];
                H264Transform.HadamardInverse2x2(chromaDc, chromaDcWide);
                DequantizeChromaDcCoefficients(chromaDcWide, qpDiv6Cr, qpMod6Cr, pps.ScalingMatrix4[5][0]);

                for (int blkIdx = 0; blkIdx < 4; blkIdx++)
                {
                    int blkX = (blkIdx % 2) * 4;
                    int blkY = (blkIdx / 2) * 4;

                    LoadAcCoefficients(residualChroma, chromaCrAcBase + blkIdx * 16, blockCoeffsWide);
                    blockCoeffsWide[0] = chromaDcWide[blkIdx];

                    H264Transform.Dequantize4x4WithMatrix(blockCoeffsWide.Slice(1, 15), pps.ScalingMatrix4[5], chromaQpCr, rasterOffset: 1);
                    H264Transform.InverseTransform4x4(blockCoeffsWide, transformedWide);

                    int blockOffset = vOffset + blkY * chromaStride + blkX;
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int refVal = ReadPixel<TDepth>(refBuffer, blockOffset + y * chromaStride + x);
                            int val = refVal + transformedWide[y * 4 + x];
                            WritePixel<TDepth>(buffer, blockOffset + y * chromaStride + x, ClipPixel<TDepth>(val));
                        }
                    }
                }
            }
            else
            {
                // 8-bit path
                Scratch.Of16<short> blockCoeffsStorage = default;
                Span<short> blockCoeffs = blockCoeffsStorage;
                Scratch.Of16<short> transformedBlockStorage = default;
                Span<short> transformedBlock = transformedBlockStorage;

                // Process Cb (U plane)
                for (int i = 0; i < 4; i++)
                    chromaDc[i] = residualChroma[chromaCbDcBase + i];
                InverseHadamard2x2(chromaDc);
                DequantizeChromaDcCoefficients(chromaDc, qpDiv6Cb, qpMod6Cb, pps.ScalingMatrix4[4][0]);

                for (int blkIdx = 0; blkIdx < 4; blkIdx++)
                {
                    int blkX = (blkIdx % 2) * 4;
                    int blkY = (blkIdx / 2) * 4;

                    LoadAcCoefficients(residualChroma, blkIdx * 16, blockCoeffs);
                    blockCoeffs[0] = chromaDc[blkIdx];

                    // Dequantised at the parser's store, against this component's own quantiser.
                    H264Transform.InverseTransform4x4(blockCoeffs, transformedBlock);

                    int blockOffset = uOffset + blkY * chromaStride + blkX;
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int refVal = ReadPixel<TDepth>(refBuffer, blockOffset + y * chromaStride + x);
                            int val = refVal + transformedBlock[y * 4 + x];
                            WritePixel<TDepth>(buffer, blockOffset + y * chromaStride + x, ClipPixel<TDepth>(val));
                        }
                    }
                }

                // Process Cr (V plane)
                for (int i = 0; i < 4; i++)
                    chromaDc[i] = residualChroma[chromaCrDcBase + i];
                InverseHadamard2x2(chromaDc);
                DequantizeChromaDcCoefficients(chromaDc, qpDiv6Cr, qpMod6Cr, pps.ScalingMatrix4[5][0]);

                for (int blkIdx = 0; blkIdx < 4; blkIdx++)
                {
                    int blkX = (blkIdx % 2) * 4;
                    int blkY = (blkIdx / 2) * 4;

                    LoadAcCoefficients(residualChroma, chromaCrAcBase + blkIdx * 16, blockCoeffs);
                    blockCoeffs[0] = chromaDc[blkIdx];

                    // Dequantised at the parser's store, against this component's own quantiser.
                    H264Transform.InverseTransform4x4(blockCoeffs, transformedBlock);

                    int blockOffset = vOffset + blkY * chromaStride + blkX;
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            int refVal = ReadPixel<TDepth>(refBuffer, blockOffset + y * chromaStride + x);
                            int val = refVal + transformedBlock[y * 4 + x];
                            WritePixel<TDepth>(buffer, blockOffset + y * chromaStride + x, ClipPixel<TDepth>(val));
                        }
                    }
                }
            }
        }
        else
        {
            // No chroma residual - just copy from reference
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    WritePixel<TDepth>(buffer, uOffset + y * chromaStride + x, ReadPixel<TDepth>(refBuffer, uOffset + y * chromaStride + x));
                    WritePixel<TDepth>(buffer, vOffset + y * chromaStride + x, ReadPixel<TDepth>(refBuffer, vOffset + y * chromaStride + x));
                }
            }
        }
    }
}


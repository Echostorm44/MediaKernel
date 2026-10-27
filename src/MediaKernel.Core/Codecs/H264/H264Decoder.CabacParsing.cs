// H.264/AVC CABAC entropy parsing
// Partial class: CABAC macroblock decoding, syntax element parsing, context derivation

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Codecs.H264;

public sealed partial class H264Decoder
{
    
    /// <summary>
    /// Per-MB flag: true when all sub-macroblock partitions are at least 8x8.
    /// Set during P_8x8/B_8x8 sub-MB type decode; used to gate transform_size_8x8_flag parsing.
    /// H.264 spec 7.4.5: noSubMbPartSizeLessThan8x8Flag.
    /// </summary>
    private bool allSubPartitionsAreMinimum8x8;
    
    private void DecodeMacroblockCabac<TShape>(ref CabacDecoder decoder, SliceHeader sliceHeader, SequenceParameterSet sps, PictureParameterSet pps, int mbAddr, out Macroblock mb, Span<short> residualLuma, Span<short> residualChroma)
        where TShape : struct, IPictureShape
    {
        int mbWidth = context.WidthInMacroblocks;
        int mbX = mbAddr % mbWidth;
        int mbY = mbAddr / mbWidth;
        cabacCurrentMbAddr = mbAddr;
        cabacCurrentMbWidth = mbWidth;
        
        mb = new Macroblock
        {
            Address = mbAddr,
            PositionX = mbX,
            PositionY = mbY,
            QuantizationParameter = sliceHeader.GetSliceQp(pps, qpBdOffset) + qpBdOffset,
            BSliceMbType = -1
        };

        // Set diagnostic flag early so CABAC tracing works for this MB
        isDiagnosticMb = DiagnosticTargetMb.HasValue && DiagnosticTargetMb.Value.mbX == mbX && DiagnosticTargetMb.Value.mbY == mbY
            && (DiagnosticTargetMb.Value.frame < 0 || DiagnosticTargetMb.Value.frame == currentFrameNum)
            && (DiagCabacTracePoc < 0 || currentPoc == DiagCabacTracePoc);
        
        // Also trace the MB immediately before the target (for stale cache analysis)
        bool isDiagPrevMb = DiagnosticTargetMb.HasValue && DiagnosticTargetMb.Value.mbX - 1 == mbX && DiagnosticTargetMb.Value.mbY == mbY
            && (DiagnosticTargetMb.Value.frame < 0 || DiagnosticTargetMb.Value.frame == currentFrameNum);
        
                
        if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac1();
        
        [MethodImpl(MethodImplOptions.NoInlining)]
        
        void TraceCabac1()
        
        {
        
                    DiagnosticOutput.WriteLine($"  MV-CACHE-INIT pos30: mv=({mvCacheL0X[30]},{mvCacheL0Y[30]}) ref={refCacheL0[30]}");
        
                    DiagnosticOutput.WriteLine($"  MV-CACHE-INIT pos28: mv=({mvCacheL0X[28]},{mvCacheL0Y[28]}) pos29: mv=({mvCacheL0X[29]},{mvCacheL0Y[29]})");
        
                    DiagnosticOutput.WriteLine($"  MV-CACHE-INIT pos36: mv=({mvCacheL0X[36]},{mvCacheL0Y[36]}) pos37: mv=({mvCacheL0X[37]},{mvCacheL0Y[37]})");
        
                }

        residualLuma.Clear();
        residualChroma.Clear();
        allSubPartitionsAreMinimum8x8 = true;
        
        // MBAFF CABAC desync trace: log bit position at start of each MB for pair 2 (rows 4-5)
        // Also include row 3 (end of pair 1) for comparison
        bool mbaDesyncTrace = DiagnosticOutput != null && DiagCabacTracePoc >= 0 
            && currentPoc == DiagCabacTracePoc && mbY >= 0 && mbY <= 5;
        if (mbaDesyncTrace)
            DiagnosticOutput!.WriteLine($"[MB-START] ({mbX},{mbY}) addr={mbAddr} bp={decoder.CurrentBitPosition} range={decoder.DebugRange}");
        
        bool isISlice = sliceHeader.Type == SliceType.ISlice || sliceHeader.Type == SliceType.SiSlice;
        
        // For P/B slices, decode mb_skip_flag
        if (!isISlice)
        {
            bool skip;

            // MBAFF bottom MB: if previous (top) was skipped, use pre-decoded value
            // FFmpeg h264_cabac.c:1955-1956
            if (TShape.Mbaff && (mbY & 1) == 1 && mbaffPrevMbSkipped)
            {
                skip = mbaffNextMbSkipped;
            }
            else
            {
                int skipCtx = TShape.Mbaff
                    ? GetMbSkipFlagContextMbaff(mbX, mbY, mbWidth, sliceHeader.Type)
                    : GetMbSkipFlagContext(mbAddr, mbWidth, sliceHeader.Type);
                
                // CABAC trace: dump state before skip flag decode
                bool cabacTrace = DiagnosticOutput != null && mbAddr < DiagCabacTraceMbCount
                    && (DiagCabacTracePoc == -2 || (DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc));
                if (cabacTrace)
                    DiagnosticOutput!.WriteLine($"  MB({mbX},{mbY}) skipCtx={skipCtx} pre:{decoder.DiagState} {decoder.GetContextState(skipCtx)}");
                
                int skipFlag = decoder.DecodeBin(skipCtx);
                skip = skipFlag == 1;
                
                if (cabacTrace)
                    DiagnosticOutput!.WriteLine($"    skipFlag={skipFlag} post:{decoder.DiagState}");
            }
            
            if (skip)
            {
                mb.IsSkipped = true;
                mb.IsIntra = false;
                if (cabacMbTypes != null)
                    cabacMbTypes[mbAddr] = -1;
                if (mbIsIntra != null)
                    mbIsIntra[mbAddr] = false;
                ClearMvdBoundaries(mbAddr);

                // MBAFF top MB skip: also decode bottom skip and maybe field flag
                // FFmpeg h264_cabac.c:1961-1966
                if (TShape.Mbaff && (mbY & 1) == 0)
                {
                    int bottomSkipCtx = GetMbSkipFlagContextMbaff(mbX, mbY + 1, mbWidth, sliceHeader.Type);
                    mbaffNextMbSkipped = decoder.DecodeBin(bottomSkipCtx) == 1;

                    if (!mbaffNextMbSkipped)
                    {
                        // Bottom will be coded → decode field flag for this pair
                        mbaffCurrentFieldFlag = DecodeCabacFieldDecodingFlag(ref decoder, mbX, mbY, mbWidth);
                    }
                    // else: both skip → field flag stays at predicted value
                }

                // Store MBAFF state
                if (TShape.Mbaff)
                {
                    mbFieldDecodingFlag![mbAddr] = mbaffCurrentFieldFlag;
                    mbaffPrevMbSkipped = true;
                    // MBAFF skip MBs need neighbor computation for MV cache,
                    // just like non-skip MBs. Maps to FFmpeg decode_mb_skip →
                    // fill_decode_neighbors (h264_mvpred.h:956).
                    ComputeMbaffNeighbors(mbAddr, mbX, mbY, mbWidth);
                }
                
                // Derive skip motion vectors for reconstruction
                PrepareRefCache<TShape>(mbAddr, mbX, mbY, mbWidth);
                PrepareDirectCache<TShape>(mbAddr, mbX, mbY, mbWidth);
                PrepareMvCache<TShape>(mbAddr, mbX, mbY, mbWidth);
                if (sliceHeader.IsBSlice)
                {
                    if (sliceHeader.DirectSpatialMvPrediction)
                        DeriveBDirectSpatialMotionVectors<TShape>(mbAddr);
                    else
                        DeriveBDirectTemporalMotionVectors<TShape>(mbAddr);
                    // B_Skip: all 16 4x4 blocks are Direct
                    FillDirectCache(Scan8[0], 4, 4, 1);
                }
                else
                {
                    DerivePSkipMotionVector<TShape>(mbAddr);
                    // Fill ref cache for skip: L0 active with ref=0, L1 unused
                    FillRefCache(Scan8[0], 4, 4, 0);
                    FillRefCacheL1(Scan8[0], 4, 4, -1);
                }
                StoreMvBoundaries(mbAddr);
                StoreRefBoundaries(mbAddr);
                StoreDirectBoundaries(mbAddr);
                StoreBlockDataForDeblocking<TShape>(mbAddr);
                
                // Per-MB MV trace for MBAFF debugging — dump ALL 16 per-4x4 block MVs
                                if (DiagnosticOutput != null && TShape.Mbaff && DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc) TraceCabac2();
                [MethodImpl(MethodImplOptions.NoInlining)]
                void TraceCabac2()
                {
                                    DiagnosticOutput.Write($"MVTRACE MB({mbX},{mbY}) skip=1 field={mbFieldDecodingFlag![mbAddr]}");
                                    for (int blk = 0; blk < 16; blk++)
                                        DiagnosticOutput.Write($" mv[{blk}]=({mvCacheL0X[Scan8[blk]]},{mvCacheL0Y[Scan8[blk]]})r{refCacheL0[Scan8[blk]]}");
                                    DiagnosticOutput.WriteLine();
                                }
                
                // Skip MB inherits QP from previous MB (delta is implicitly 0)
                mb.QuantizationParameter = previousMbQp;
                previousMbQpDelta = 0;
                
                if (mbaDesyncTrace)
                    DiagnosticOutput!.WriteLine($"[MB-END]   ({mbX},{mbY}) type=SKIP intra=0 skip=1 bp={decoder.CurrentBitPosition} range={decoder.DebugRange}");
                return;
            }
        }

        // Not skipped: for MBAFF top MB, decode field flag before mb_type
        // FFmpeg h264_cabac.c:1978-1982
        if (TShape.Mbaff)
        {
            if ((mbY & 1) == 0)
            {
                mbaffCurrentFieldFlag = DecodeCabacFieldDecodingFlag(ref decoder, mbX, mbY, mbWidth);
                                if (DiagnosticOutput != null && mbAddr < 50) TraceCabac3(ref decoder);
                [MethodImpl(MethodImplOptions.NoInlining)]
                void TraceCabac3(ref CabacDecoder decoder)
                {
                DiagnosticOutput.WriteLine($"  FIELD_FLAG: mb={mbAddr} ({mbX},{mbY}) decoded={mbaffCurrentFieldFlag} state={decoder.DiagState}");
                }
            }
            mbFieldDecodingFlag![mbAddr] = mbaffCurrentFieldFlag;

            // The scan has to be chosen here, not before ReconstructMacroblock where it used
            // to be: the coefficients are now stored at their raster positions as they are
            // decoded, and this is the first point in the macroblock where the field flag is
            // known. A field-coded macroblock scans column-first (Table 8-13), and the flag
            // is decoded only for the top of a pair - the bottom inherits it.
            activeScan4x4 = mbaffCurrentFieldFlag ? FieldScan4x4 : ZigzagScan4x4;
            mbaffPrevMbSkipped = false;
            // FFmpeg calls fill_decode_neighbors BEFORE mb_type decoding (h264_cabac.c:1986).
            // ComputeMbaffNeighbors sets mbaffLeftXYTop/mbaffTopXY used by context derivation.
            ComputeMbaffNeighbors(mbAddr, mbX, mbY, mbWidth);
        }

        // Decode mb_type
                if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac4(ref decoder);
        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceCabac4(ref CabacDecoder decoder)
        {
        DiagnosticOutput.WriteLine($"  CABAC-MBTYPE-PRE: {decoder.DiagState} {decoder.GetContextState(3)} {decoder.GetContextState(4)} {decoder.GetContextState(5)}");
        }
        {
            bool cabacTrace1 = DiagnosticOutput != null && mbAddr < DiagCabacTraceMbCount
                && (DiagCabacTracePoc == -2 || (DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc));
            if (cabacTrace1)
                DiagnosticOutput!.WriteLine($"  MB({mbX},{mbY}) addr={mbAddr} slice={currentSliceIdx} pre:{decoder.DiagState} ctx69pS={decoder.GetPState(69)} hash={decoder.ContextStateHash():X8}");
        }
        int mbType = DecodeMbTypeCabac<TShape>(ref decoder, sliceHeader.Type, mbAddr, mbWidth);
        
        // CABAC trace: dump mb_type for non-skip MBs
                if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac5(ref decoder);
        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceCabac5(ref CabacDecoder decoder)
        {
        DiagnosticOutput.WriteLine($"  CABAC-MBTYPE-POST: mbType={mbType} {decoder.DiagState}");
        }
        {
            bool cabacTrace2 = DiagnosticOutput != null && mbAddr < DiagCabacTraceMbCount
                && (DiagCabacTracePoc == -2 || (DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc));
            if (cabacTrace2)
                DiagnosticOutput!.WriteLine($"    mbType={mbType} post:{decoder.DiagState}");
        }
        
                
        if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac6();
        
        [MethodImpl(MethodImplOptions.NoInlining)]
        
        void TraceCabac6()
        
        {
        
        DiagnosticOutput.WriteLine($"  CABAC: mbType={mbType} sliceType={sliceHeader.Type} numRefL0={sliceHeader.NumRefIdxL0ActiveMinus1 + 1}");
        
        }
        
        // Store raw mb_type for neighbor context derivation
        if (cabacMbTypes != null)
            cabacMbTypes[mbAddr] = mbType;
        
        // Parse mb_type into macroblock structure
        ParseMbTypeCabac(mbType, sliceHeader.Type, ref mb);
        
        
                
        
        if (isDiagPrevMb && DiagnosticOutput != null) TraceCabac7(ref mb);
        
        
        [MethodImpl(MethodImplOptions.NoInlining)]
        
        
        void TraceCabac7(ref Macroblock mb)
        
        
        {
        
        
        DiagnosticOutput.WriteLine($"  PREV-MB({mbX},{mbY}): mbType={mbType} isIntra={mb.IsIntra} intrType={mb.IntraType}");
        
        
        }

        // Track I16x16 and I4x4 status for context derivation
        if (cabacIsIntra16x16 != null)
            cabacIsIntra16x16[mbAddr] = mb.IntraType >= IntraMacroblockType.Intra16x16_0_0_0 && 
                                        mb.IntraType <= IntraMacroblockType.Intra16x16_3_2_1;
        if (cabacIsIntra4x4 != null)
            cabacIsIntra4x4[mbAddr] = mb.IsIntra && mb.IntraType == IntraMacroblockType.Intra4x4;
        if (mbIsIntra != null)
            mbIsIntra[mbAddr] = mb.IsIntra;

        // I_PCM: raw PCM samples embedded in the CABAC bitstream (H.264 spec 7.3.5)
        if (mb.IntraType == IntraMacroblockType.IntraPcm)
        {
            HandleIntraPcmCabac(ref decoder, ref mb, sliceHeader, sps, pps, mbAddr, residualLuma, residualChroma);
            return;
        }

        // For intra macroblocks, decode prediction modes
        if (mb.IsIntra && mb.IntraType == IntraMacroblockType.Intra4x4)
        {
            // High Profile: decode transform_size_8x8_flag before intra modes
                        if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac8();
            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCabac8()
            {
            DiagnosticOutput.WriteLine($"  CABAC-T8x8: pps.Transform8x8Mode={pps.Transform8x8Mode} profile={sps.Profile} frameMbsOnly={sps.FrameMacroblockOnly} mbaff={sps.MbAdaptiveFrameField}");
            }
            if (pps.Transform8x8Mode)
            {
                int neighborTransformSize = GetNeighborTransformSize<TShape>(mbAddr, mbX, mbY, mbWidth);
                                if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac9(ref decoder);
                [MethodImpl(MethodImplOptions.NoInlining)]
                void TraceCabac9(ref CabacDecoder decoder)
                {
                DiagnosticOutput.WriteLine($"  CABAC-T8x8-PRE: {decoder.DiagState} {decoder.GetContextState(399 + neighborTransformSize)}");
                }
                int transform8x8Flag = decoder.DecodeBin(399 + neighborTransformSize);
                mb.Transform8x8 = transform8x8Flag == 1;
                                if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac10(ref decoder);
                [MethodImpl(MethodImplOptions.NoInlining)]
                void TraceCabac10(ref CabacDecoder decoder)
                {
                DiagnosticOutput.WriteLine($"  CABAC-T8x8: neighborSize={neighborTransformSize} ctx={399 + neighborTransformSize} flag={transform8x8Flag} POST:{decoder.DiagState}");
                }
                if (cabacTransform8x8 != null)
                    cabacTransform8x8[mbAddr] = transform8x8Flag;

                if (mb.Transform8x8)
                    DecodeIntra8x8ModesCabac<TShape>(ref decoder, ref mb);
                else
                    DecodeIntra4x4ModesCabac<TShape>(ref decoder, ref mb);
            }
            else
            {
                DecodeIntra4x4ModesCabac<TShape>(ref decoder, ref mb);
            }
        }
        
        if (mb.IsIntra && sps.ChromaFormat != ChromaFormat.Monochrome)
        {
            int chromaMode = DecodeIntraChromaPredModeCabac<TShape>(ref decoder, mbAddr, mbWidth);
            mb.IntraChromaPredMode = (IntraChromaPredictionMode)chromaMode;
            if (cabacChromaPredMode != null)
                cabacChromaPredMode[mbAddr] = chromaMode;
        }
        
        // For inter macroblocks, decode reference indices and motion vectors
        if (!mb.IsIntra)
        {
            PrepareMvdCache<TShape>(mbAddr, mbX, mbY, mbWidth);
            PrepareRefCache<TShape>(mbAddr, mbX, mbY, mbWidth);
            PrepareDirectCache<TShape>(mbAddr, mbX, mbY, mbWidth);
            PrepareMvCache<TShape>(mbAddr, mbX, mbY, mbWidth);
            DecodeInterPredictionCabac<TShape>(ref decoder, sliceHeader, mbType, mbAddr);
            StoreMvdBoundaries(mbAddr);
            StoreRefBoundaries(mbAddr);
            StoreDirectBoundaries(mbAddr);
            StoreMvBoundaries(mbAddr);
            StoreBlockDataForDeblocking<TShape>(mbAddr);
            // Per-MB MV trace for MBAFF debugging — dump ALL 16 per-4x4 block MVs
                        if (DiagnosticOutput != null && TShape.Mbaff && DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc) TraceCabac11();
            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCabac11()
            {
                            int b = mbAddr * 4;
                            DiagnosticOutput.Write($"MVTRACE MB({mbX},{mbY}) skip=0 field={mbFieldDecodingFlag![mbAddr]} type={mbType} ref={cabacRefL0Right![b]}");
                            for (int blk = 0; blk < 16; blk++)
                            {
                                int bx = (blk & 3); int by = (blk >> 2);
                                if (by >= 2) bx -= 2;
                                if (by >= 2) by -= 2;
                                // Use scan8 indices for the actual 4x4 block positions
                                DiagnosticOutput.Write($" mv[{blk}]=({mvCacheL0X[Scan8[blk]]},{mvCacheL0Y[Scan8[blk]]})r{refCacheL0[Scan8[blk]]}");
                            }
                            DiagnosticOutput.WriteLine();
                        }
                        if (isDiagPrevMb && DiagnosticOutput != null) TraceCabac12();
            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCabac12()
            {
                            DiagnosticOutput.WriteLine($"  PREV-MB({mbX},{mbY}) INTER MV-cache after decode:");
                            for (int si = 0; si < 16; si++)
                                DiagnosticOutput.WriteLine($"    scan8[{si}]={Scan8[si]}: mv=({mvCacheL0X[Scan8[si]]},{mvCacheL0Y[Scan8[si]]}) ref={refCacheL0[Scan8[si]]}");
                        }
        }
        else
        {
            // Intra MB: fill interior cache with MV=(0,0), ref=-1, mvd=(0,0).
            // The next inter MB may read interior positions as C neighbors for sub-8x8
            // partitions (e.g., pos 30 = Scan8[12] for P_L0_8x4 bottom partition).
            // Stale values corrupt PredMotion's matchCount logic — a stale ref=0 changes
            // matchCount from 1→2, switching from "use matching neighbor" to "median".
            FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[0], 4, 4, 0, 0);
            FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[0], 4, 4, 0, 0);
            FillRefCache(Scan8[0], 4, 4, -1);
            FillRefCacheL1(Scan8[0], 4, 4, -1);
            FillMvdCache(Scan8[0], 4, 4, 0, 0);
            FillMvdCacheL1(Scan8[0], 4, 4, 0, 0);

            ClearMvdBoundaries(mbAddr);
            ClearRefBoundaries(mbAddr);
            ClearDirectBoundaries(mbAddr);
            ClearMvBoundaries(mbAddr);
            ClearBlockDataForDeblocking(mbAddr);
            // Per-MB MV trace for intra MBAFF debugging  
                        if (DiagnosticOutput != null && TShape.Mbaff && DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc) TraceCabac13();
            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCabac13()
            {
                            DiagnosticOutput.WriteLine($"MVTRACE MB({mbX},{mbY}) skip=0 field={mbFieldDecodingFlag![mbAddr]} type=INTRA ref=-1 mv0=(0,0) mv1=(0,0) mv2=(0,0) mv3=(0,0)");
                        }
        }

        // For non-I16x16 macroblocks, decode coded_block_pattern
        bool isIntra16x16 = mb.IntraType >= IntraMacroblockType.Intra16x16_0_0_0 && 
                            mb.IntraType <= IntraMacroblockType.Intra16x16_3_2_1;
        
        if (!isIntra16x16 && mb.IntraType != IntraMacroblockType.IntraPcm)
        {
            DecodeCbpCabac<TShape>(ref decoder, ref mb, mb.IsIntra);
        }

        // Compact per-MB CABAC trace
        {
            bool cabacTrace3 = DiagnosticOutput != null && mbAddr < DiagCabacTraceMbCount
                && (DiagCabacTracePoc == -2 || (DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc));
            if (cabacTrace3)
                DiagnosticOutput!.WriteLine($"    cbp=L{mb.CodedBlockPatternLuma}C{mb.CodedBlockPatternChroma} intra={mb.IntraType} t8x8={mb.Transform8x8} bp={decoder.CurrentBitPosition}");
        }

        // Store CBP values for neighbor context derivation in subsequent MBs.
        if (cabacCbpLuma != null)
            cabacCbpLuma[mbAddr] = mb.CodedBlockPatternLuma;
        if (cabacCbpChroma != null)
            cabacCbpChroma[mbAddr] = mb.CodedBlockPatternChroma;
        
        // High Profile: decode transform_size_8x8_flag for inter MBs after CBP
        // Per H.264 spec 7.3.5.1: only when pps.Transform8x8Mode, MB is inter (not intra),
        // at least one luma 8x8 block has coded coefficients, AND all sub-partitions are ≥ 8x8.
        if (pps.Transform8x8Mode && !mb.IsIntra && mb.CodedBlockPatternLuma > 0 && allSubPartitionsAreMinimum8x8)
        {
            int neighborTransformSize = GetNeighborTransformSize<TShape>(mbAddr, mbX, mbY, mbWidth);
            int transform8x8Flag = decoder.DecodeBin(399 + neighborTransformSize);
            mb.Transform8x8 = transform8x8Flag == 1;
            if (cabacTransform8x8 != null)
                cabacTransform8x8[mbAddr] = transform8x8Flag;
        }

        // Decode QP delta if there are coded coefficients
        if (mb.CodedBlockPatternLuma > 0 || mb.CodedBlockPatternChroma > 0 || isIntra16x16)
        {
            int qpDelta = DecodeMbQpDeltaCabac(ref decoder);
            int qpWrapModulus = 52 + qpBdOffset;
            int wrappedQp = (previousMbQp + qpDelta) % qpWrapModulus;
            mb.QuantizationParameter = wrappedQp < 0 ? wrappedQp + qpWrapModulus : wrappedQp;
        }
        else
        {
            mb.QuantizationParameter = previousMbQp;
            previousMbQpDelta = 0;
        }
        previousMbQp = mb.QuantizationParameter;

        // Decode residual data
        if (mb.IntraType != IntraMacroblockType.IntraPcm)
        {
            // MBAFF field MBs use field scan tables, same as field pictures (FFmpeg IS_INTERLACED)
            bool fieldMode = sliceHeader.FieldPicture || (TShape.Mbaff && mbFieldDecodingFlag != null && mbFieldDecodingFlag[mbAddr]);
#if MK_STAGE_TIMING
            long _res0 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            DecodeResidualCabac<TShape>(ref decoder, ref mb, isIntra16x16, fieldMode, residualLuma, residualChroma);
#if MK_STAGE_TIMING
            StageResidualTicks += System.Diagnostics.Stopwatch.GetTimestamp() - _res0;
            StageResidualCalls++;
#endif
        }
        
        if (mbaDesyncTrace)
            DiagnosticOutput!.WriteLine($"[MB-END]   ({mbX},{mbY}) type={mbType} intra={mb.IsIntra} skip=0 cbpL={mb.CodedBlockPatternLuma} cbpC={mb.CodedBlockPatternChroma} bp={decoder.CurrentBitPosition} range={decoder.DebugRange}");
    }

    /// <summary>
    /// Handles I_PCM macroblock in CABAC mode: reads raw PCM samples from the bitstream,
    /// writes them directly to the frame buffer, sets neighbor contexts, and re-initializes CABAC.
    /// Per H.264 spec sections 7.3.5 and 9.3.3.5.
    /// </summary>
    private void HandleIntraPcmCabac(ref CabacDecoder decoder, ref Macroblock mb,
        SliceHeader sliceHeader, SequenceParameterSet sps, PictureParameterSet pps,
        int mbAddr, Span<short> residualLuma, Span<short> residualChroma)
    {
        // PCM byte count: 256 luma + chroma (128 for 4:2:0 at 8-bit, 0 for monochrome)
        int lumaCount = 16 * 16;
        bool isMonochrome = sps.ChromaFormat == ChromaFormat.Monochrome;
        int chromaCount = isMonochrome ? 0 : 2 * 8 * 8;
        int pcmByteCount = lumaCount + chromaCount;

        // Read raw PCM bytes and re-initialize arithmetic engine (NOT context states).
        byte[] pcmBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(pcmByteCount);
        try
        {
            Span<byte> pcmData = pcmBuffer.AsSpan(0, pcmByteCount);

            // Diagnostic: print byte position before PCM read
                        if (DiagnosticOutput != null) TraceCabac14(ref decoder);
            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCabac14(ref CabacDecoder decoder)
            {
            DiagnosticOutput.WriteLine($"[PCM_CABAC] mbAddr={mbAddr} PRE-REINIT {decoder.DiagState} pcmBytes={pcmByteCount}");
            }

            decoder.SkipPcmAndReinitialize(pcmByteCount, pcmData);

            // Diagnostic: print CABAC state after reinit
                        if (DiagnosticOutput != null) TraceCabac15(ref decoder);
            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCabac15(ref CabacDecoder decoder)
            {
            DiagnosticOutput.WriteLine($"[PCM_CABAC] mbAddr={mbAddr} POST-REINIT {decoder.DiagState}");
            }

            // Diagnostic: print first 16 PCM luma bytes
                        if (DiagnosticOutput != null) TraceCabac16(pcmData);
            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCabac16(Span<byte> pcmData)
            {
                            var sb = new System.Text.StringBuilder("[PCM_DATA] luma[0..15]: ");
                            for (int i = 0; i < 16 && i < pcmByteCount; i++)
                                sb.Append($"{pcmData[i],4}");
                            DiagnosticOutput.WriteLine(sb.ToString());
                        }

            // Populate residual buffers so ReconstructPcmMacroblock can write pixels
            for (int i = 0; i < lumaCount; i++)
                residualLuma[i] = pcmData[i];
            if (!isMonochrome)
            {
                for (int i = 0; i < chromaCount; i++)
                    residualChroma[i] = pcmData[lumaCount + i];
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(pcmBuffer);
        }

        // I_PCM: QP=0 for deblocking (stored in MB), but do NOT change previousMbQp.
        // FFmpeg keeps sl->qscale unchanged after PCM — the running QP carries over.
        mb.QuantizationParameter = 0;
        previousMbQpDelta = 0;
        // previousMbQp intentionally NOT changed — next MB uses pre-PCM QP as base

        // I_PCM: all blocks "coded" — set CBP and neighbor context arrays
        mb.CodedBlockPatternLuma = 15;
        mb.CodedBlockPatternChroma = isMonochrome ? 0 : 2;
        if (cabacCbpLuma != null) cabacCbpLuma[mbAddr] = 15;
        if (cabacCbpChroma != null) cabacCbpChroma[mbAddr] = isMonochrome ? 0 : 2;
        if (cabacChromaPredMode != null) cabacChromaPredMode[mbAddr] = 0;
        if (cabacLumaDcCoded != null) cabacLumaDcCoded[mbAddr] = 1;
        if (cabacLumaCbf != null) cabacLumaCbf[mbAddr] = 0xFFFF;
        if (cabacChromaCbf != null)
        {
            // Set all AC CBF bits + DC CBF bits for PCM macroblock
            int allAcBits = (1 << (2 * chromaBlocksPerComponent)) - 1;  // bits 0..2*blocks-1
            int dcBits = 0x3 << (2 * chromaBlocksPerComponent);          // DC bits for Cb + Cr
            cabacChromaCbf[mbAddr] = allAcBits | dcBits;
        }

        ClearMvdBoundaries(mbAddr);
        ClearRefBoundaries(mbAddr);
        ClearDirectBoundaries(mbAddr);
    }

    private int GetMbSkipFlagContext(int mbAddr, int mbWidth, SliceType sliceType)
    {
        // H.264 spec 9.3.3.1.1.1: mb_skip_flag context
        // condTermFlagA = 1 if left MB available AND NOT skipped, 0 otherwise
        // condTermFlagB = 1 if top MB available AND NOT skipped, 0 otherwise
        int mbX = mbAddr % mbWidth;
        int mbY = mbAddr / mbWidth;
        
        int condTermA = 0;
        int condTermB = 0;
        
        if (mbX > 0 && cabacMbTypes != null && mbSliceIndex![mbAddr - 1] == currentSliceIdx)
            condTermA = (cabacMbTypes[mbAddr - 1] != -1) ? 1 : 0;
        if (mbY > 0 && cabacMbTypes != null && mbSliceIndex![mbAddr - mbWidth] == currentSliceIdx)
            condTermB = (cabacMbTypes[mbAddr - mbWidth] != -1) ? 1 : 0;
        
        // Base context: 11 for P-slice, 24 for B-slice
        int baseCtx = (sliceType == SliceType.BSlice) ? 24 : 11;
        return baseCtx + condTermA + condTermB;
    }

    /// <summary>
    /// MBAFF-aware skip flag context derivation.
    /// Maps to FFmpeg decode_cabac_mb_skip MBAFF path (h264_cabac.c:1348-1367).
    /// Uses the current pair's field flag for neighbor address computation.
    /// </summary>
    private int GetMbSkipFlagContextMbaff(int mbX, int mbY, int mbWidth, SliceType sliceType)
    {
        int ctx = 0;
        int pairTopAddr = (mbY & ~1) * mbWidth + mbX;

        // Left neighbor (A): start with left pair's top MB
        if (mbX > 0)
        {
            int mbaAddr = pairTopAddr - 1;
            // For bottom MB: if left pair has same interlace mode, use its bottom MB
            if ((mbY & 1) == 1 && mbaAddr >= 0 && mbaAddr < mbSliceIndex!.Length
                && mbSliceIndex[mbaAddr] == currentSliceIdx
                && mbaffCurrentFieldFlag == mbFieldDecodingFlag![mbaAddr])
            {
                mbaAddr += mbWidth;
            }
            if (mbaAddr >= 0 && mbaAddr < cabacMbTypes!.Length
                && mbSliceIndex![mbaAddr] == currentSliceIdx
                && cabacMbTypes[mbaAddr] != -1)
            {
                ctx++;
            }
        }

        // Top neighbor (B): depends on current field flag
        int mbbAddr;
        if (mbaffCurrentFieldFlag)
        {
            // Field mode: one row up from pair's top
            mbbAddr = pairTopAddr - mbWidth;
            // For top MB: if above is also field-coded, go up one more row
            if ((mbY & 1) == 0 && mbbAddr >= 0 && mbbAddr < mbFieldDecodingFlag!.Length
                && mbSliceIndex![mbbAddr] == currentSliceIdx
                && mbFieldDecodingFlag[mbbAddr])
            {
                mbbAddr -= mbWidth;
            }
        }
        else
        {
            // Frame mode: row above current MB position
            mbbAddr = mbX + (mbY - 1) * mbWidth;
        }

        if (mbbAddr >= 0 && mbbAddr < cabacMbTypes!.Length
            && mbSliceIndex![mbbAddr] == currentSliceIdx
            && cabacMbTypes[mbbAddr] != -1)
        {
            ctx++;
        }

        int baseCtx = (sliceType == SliceType.BSlice) ? 24 : 11;
        return baseCtx + ctx;
    }

    /// <summary>
    /// Decodes mb_field_decoding_flag from CABAC at ctxIdx 70+ctx.
    /// Maps to FFmpeg decode_cabac_field_decoding_flag (h264_cabac.c:1298-1308).
    /// ctx bit 0: previous pair's field flag (if left pair available).
    /// ctx bit 1: top pair's IS_INTERLACED flag (if in same slice).
    /// </summary>
    private bool DecodeCabacFieldDecodingFlag(ref CabacDecoder decoder, int mbX, int mbY, int mbWidth)
    {
        int ctx = 0;

        // Left pair's field flag (mbaffCurrentFieldFlag holds the previous pair's value)
        if (mbX > 0)
            ctx += mbaffCurrentFieldFlag ? 1 : 0;

        // Top pair's interlaced flag: 2 rows above current pair's top
        int topPairTopAddr = (mbY - 2) * mbWidth + mbX;
        if (mbY >= 2 && topPairTopAddr >= 0 && topPairTopAddr < mbFieldDecodingFlag!.Length
            && mbSliceIndex![topPairTopAddr] == currentSliceIdx)
        {
            ctx += mbFieldDecodingFlag[topPairTopAddr] ? 1 : 0;
        }

        return decoder.DecodeBin(70 + ctx) != 0;
    }

    /// <summary>
    /// Predicts mb_field_decoding_flag from neighbors for all-skip pairs.
    /// Called at the start of each new pair row.
    /// Maps to FFmpeg predict_field_decoding_flag (h264_slice.c:2502-2510).
    /// </summary>
    private void PredictFieldDecodingFlag(int mbX, int mbY, int mbWidth)
    {
        int mbAddr = mbY * mbWidth + mbX;

        // Try left neighbor first (only valid if mbX > 0 to avoid row wrap)
        if (mbX > 0)
        {
            int leftAddr = mbAddr - 1;
            if (leftAddr >= 0 && leftAddr < mbSliceIndex!.Length
                && mbSliceIndex[leftAddr] == currentSliceIdx)
            {
                mbaffCurrentFieldFlag = mbFieldDecodingFlag![leftAddr];
                return;
            }
        }

        // Try top neighbor (one row up = bottom of previous pair in same column)
        int topAddr = mbAddr - mbWidth;
        if (topAddr >= 0 && topAddr < mbSliceIndex!.Length
            && mbSliceIndex[topAddr] == currentSliceIdx)
        {
            mbaffCurrentFieldFlag = mbFieldDecodingFlag![topAddr];
            return;
        }

        // No valid neighbor: default to frame mode
        mbaffCurrentFieldFlag = false;
    }

    /// <summary>
    /// Computes MBAFF-aware neighbor addresses for the current macroblock.
    /// Sets mbaffLeftXYTop, mbaffLeftXYBot, mbaffTopXY, mbaffTopLeftXY, mbaffTopRightXY,
    /// and mbaffLeftBlockIdx. Maps to FFmpeg's fill_decode_neighbors() in h264_mvpred.h:487-574.
    /// Must be called before PrepareRefCache/PrepareMvCache/PrepareDirectCache for MBAFF MBs.
    /// </summary>
    private void ComputeMbaffNeighbors(int mbAddr, int mbX, int mbY, int mbWidth)
    {
        bool currentIsField = mbFieldDecodingFlag![mbAddr];
        bool isBottomMb = (mbY & 1) != 0;

        // Default addresses (same as non-MBAFF)
        int topAddr = mbAddr - (currentIsField ? 2 * mbWidth : mbWidth);
        int topLeftAddr = topAddr - 1;
        int topRightAddr = topAddr + 1;

        // Default top neighbor
        mbaffTopXY = topAddr;
        mbaffTopLeftXY = topLeftAddr;
        mbaffTopRightXY = topRightAddr;
        mbaffLeftBlockIdx = 0; // Frame→Frame default
        mbaffTopleftPartition = -1; // Default: read bottom-right of topleft MB

        // Left neighbor: must be -1 when mbX==0 (picture edge, no wrap!)
        // FFmpeg handles this via mb_stride = mb_width + 1 padding; we handle explicitly.
        if (mbX == 0)
        {
            mbaffLeftXYTop = -1;
            mbaffLeftXYBot = -1;
            mbaffTopLeftXY = -1; // no top-left at left edge either
        }
        else
        {
            // mbX > 0: left neighbor exists
            // Default: both LTOP and LBOT point to adjacent MB (mb_xy - 1).
            // This is correct for same-mode (FFmpeg: left_xy[LTOP] = left_xy[LBOT] = mb_xy - 1)
            int leftAddr = mbAddr - 1;
            mbaffLeftXYTop = leftAddr;
            mbaffLeftXYBot = leftAddr;

            // Determine left neighbor's field status
            // Left pair's top MB is at (mbY & ~1) * mbWidth + (mbX - 1)
            int leftPairTopAddr = (mbY & ~1) * mbWidth + (mbX - 1);
            bool leftIsField = mbFieldDecodingFlag[leftPairTopAddr];

            if (isBottomMb)
            {
                // Bottom MB of pair
                if (leftIsField != currentIsField)
                {
                    // Field/frame mismatch at bottom of pair
                    // Both LTOP and LBOT start from previous pair's top
                    mbaffLeftXYTop = leftPairTopAddr;
                    mbaffLeftXYBot = leftPairTopAddr;

                    if (currentIsField)
                    {
                        // Current=field, left=frame: LBOT reads from left pair's bottom
                        mbaffLeftXYBot = leftPairTopAddr + mbWidth;
                        mbaffLeftBlockIdx = 3; // Field→Field cross-pair table
                    }
                    else
                    {
                        // Current=frame, left=field: read from field pair
                        mbaffLeftBlockIdx = 1; // Frame→Field
                        // Adjust topleft for frame reading from field bottom
                        mbaffTopLeftXY = topLeftAddr + mbWidth;
                        // FFmpeg: topleft_partition = 0 → read from middle of topleft MB
                        // instead of bottom-right (takes top-right 8x8 partition)
                        mbaffTopleftPartition = 0;
                    }
                }
                else
                {
                    // Same mode: left is adjacent bottom MB
                    mbaffLeftXYTop = leftPairTopAddr + mbWidth;
                    mbaffLeftXYBot = leftPairTopAddr + mbWidth;
                }
            }
            else
            {
                // Top MB of pair
                if (leftIsField != currentIsField)
                {
                    if (currentIsField)
                    {
                        // Current=field, left=frame: LBOT reads from frame's bottom half
                        mbaffLeftXYBot = leftPairTopAddr + mbWidth;
                        mbaffLeftBlockIdx = 3; // Field→Field cross-pair
                    }
                    else
                    {
                        // Current=frame, left=field: only block indices change, NOT LBOT
                        // (FFmpeg fill_decode_neighbors: left_xy[LBOT] stays at mb_xy-1)
                        mbaffLeftBlockIdx = 2;
                    }
                }
                // else same mode: default addresses are correct
            }
        }

        // Top/topleft/topright neighbor adjustment for field MBs at top of pair.
        // FFmpeg (fill_decode_neighbors, lines 527-531): each adjusted INDEPENDENTLY
        // based on its own mb_type, not gated by the top neighbor's field flag.
        if (!isBottomMb && currentIsField)
        {
            if (mbaffTopXY >= 0 && mbaffTopXY < mbFieldDecodingFlag.Length
                && !mbFieldDecodingFlag[mbaffTopXY])
                mbaffTopXY += mbWidth;

            if (mbaffTopLeftXY >= 0 && mbaffTopLeftXY < mbFieldDecodingFlag.Length
                && !mbFieldDecodingFlag[mbaffTopLeftXY])
                mbaffTopLeftXY += mbWidth;

            if (mbaffTopRightXY >= 0 && mbaffTopRightXY < mbFieldDecodingFlag.Length
                && !mbFieldDecodingFlag[mbaffTopRightXY])
                mbaffTopRightXY += mbWidth;
        }
    }

    private int DecodeMbTypeCabac<TShape>(ref CabacDecoder decoder, SliceType sliceType, int mbAddr, int mbWidth)
        where TShape : struct, IPictureShape
    {
        bool isISlice = sliceType == SliceType.ISlice || sliceType == SliceType.SiSlice;
        
        // Compute ctxIdxInc for bin 0 using neighbor mb_types (spec 9.3.3.1.1.3)
        // For MBAFF: use MBAFF-adjusted neighbors (FFmpeg uses fill_decode_neighbors results).
        int mbX = mbAddr % mbWidth;
        int mbY = mbAddr / mbWidth;
        
        int leftAddr = TShape.Mbaff ? mbaffLeftXYTop : mbAddr - 1;
        int topAddr = TShape.Mbaff ? mbaffTopXY : mbAddr - mbWidth;
        bool leftAvail = TShape.Mbaff
            ? (mbX > 0 && leftAddr >= 0 && leftAddr < cabacMbTypes!.Length && mbSliceIndex![leftAddr] == currentSliceIdx)
            : (mbX > 0 && cabacMbTypes != null && mbSliceIndex![mbAddr - 1] == currentSliceIdx);
        bool topAvail = TShape.Mbaff
            ? (topAddr >= 0 && topAddr < cabacMbTypes!.Length && mbSliceIndex![topAddr] == currentSliceIdx)
            : (mbY > 0 && cabacMbTypes != null && mbSliceIndex![mbAddr - mbWidth] == currentSliceIdx);
        
        int condTermA = 0;
        int condTermB = 0;
        
        if (isISlice)
        {
            // I-slice: condTermFlag = 1 if neighbor is I16x16 or I_PCM, 0 if I4x4 or unavailable.
            // H.264 spec 9.3.3.1.1.3: condTermFlagN = 1 if neighbor available AND NOT I_NxN.
            // FFmpeg: left_type & (MB_TYPE_INTRA16x16 | MB_TYPE_INTRA_PCM)
            // In our mbType encoding: 0 = I4x4 (I_NxN), 1-24 = I16x16, 25 = I_PCM
            if (leftAvail) condTermA = (cabacMbTypes![leftAddr] > 0) ? 1 : 0;
            if (topAvail) condTermB = (cabacMbTypes![topAddr] > 0) ? 1 : 0;
        }
        else if (sliceType == SliceType.PSlice)
        {
            // P-slice: condTermFlag = 1 if neighbor available AND NOT P_Skip (-1)
            if (leftAvail) condTermA = (cabacMbTypes![leftAddr] >= 0) ? 1 : 0;
            if (topAvail) condTermB = (cabacMbTypes![topAddr] >= 0) ? 1 : 0;
        }
        else
        {
            // B-slice: condTermFlag = 1 if neighbor available AND NOT B_Skip (-1) AND NOT B_Direct (0)
            // FFmpeg h264_cabac.c: !IS_DIRECT(left_type - 1) — types with MB_TYPE_DIRECT2 (0x100) bit
            // (B_Skip and B_Direct) give ctx += 0; coded MBs without that bit give ctx += 1
            if (leftAvail) condTermA = (cabacMbTypes![leftAddr] > 0) ? 1 : 0;
            if (topAvail) condTermB = (cabacMbTypes![topAddr] > 0) ? 1 : 0;
        }
        int ctxIdxInc = condTermA + condTermB;
        
        if (isISlice)
        {
            return DecodeMbTypeISliceCabac(ref decoder, 3, ctxIdxInc);
        }
        else if (sliceType == SliceType.PSlice)
        {
            // P-slice mb_type — matches FFmpeg h264_cabac.c ff_h264_decode_mb_cabac()
            // bin 0 at ctx 14: 0=inter, 1=intra (FFmpeg uses fixed ctx 14, no ctxIdxInc)
            int bin0 = decoder.DecodeBin(14);
            if (bin0 != 0)
            {
                // Intra macroblock in P-slice
                int intraMbType = DecodeMbTypeISliceCabac(ref decoder, 17, 0);
                return 5 + intraMbType;
            }
            
            // Inter types: 3-bin tree (FFmpeg contexts: bin1=15, bin2=16 or 17)
            int bin1 = decoder.DecodeBin(15);
            if (bin1 == 0)
            {
                // P_L0_16x16 or P_8x8
                int bin2 = decoder.DecodeBin(16);
                return bin2 == 0 ? 0 : 3;
            }
            else
            {
                // P_L0_L0_8x16 or P_L0_L0_16x8
                int bin2 = decoder.DecodeBin(17);
                return bin2 == 0 ? 2 : 1;
            }
        }
        else
        {
            // B-slice mb_type — matches FFmpeg h264_cabac.c exactly
            bool binTrace = DiagCabacBinTrace && DiagnosticOutput != null 
                && DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc;
            
            if (binTrace)
                DiagnosticOutput!.WriteLine($"  B-MTYPE mbAddr={mbAddr} ctxIdxInc={ctxIdxInc} pre:{decoder.DiagState} {decoder.GetContextState(27+ctxIdxInc)}");
            
            // bin 0 at ctx 27+ctxIdxInc: 0=B_Direct_16x16
            int bin0 = decoder.DecodeBin(27 + ctxIdxInc);
            if (binTrace)
                DiagnosticOutput!.WriteLine($"    bin0={bin0} ctx={27+ctxIdxInc} post:{decoder.DiagState}");
            if (bin0 == 0)
                return 0;  // B_Direct_16x16
            
            // bin 1 at ctx 30: 0=B_L0/L1_16x16, 1=more complex
            int bin1 = decoder.DecodeBin(30);
            if (binTrace)
                DiagnosticOutput!.WriteLine($"    bin1={bin1} ctx=30 post:{decoder.DiagState} {decoder.GetContextState(30)}");
            if (bin1 == 0)
            {
                // One more bin at ctx 32: 0=B_L0_16x16, 1=B_L1_16x16
                int bin2 = decoder.DecodeBin(32);
                if (binTrace)
                    DiagnosticOutput!.WriteLine($"    bin2={bin2} ctx=32 → mbType={1+bin2}");
                return 1 + bin2;
            }
            
            // 4 bins: ctx 31, 32, 32, 32
            int b2 = decoder.DecodeBin(31);
            if (binTrace)
                DiagnosticOutput!.WriteLine($"    bit3={b2} ctx=31 post:{decoder.DiagState} {decoder.GetContextState(31)}");
            int b3 = decoder.DecodeBin(32);
            if (binTrace)
                DiagnosticOutput!.WriteLine($"    bit4={b3} ctx=32 post:{decoder.DiagState} {decoder.GetContextState(32)}");
            int b4 = decoder.DecodeBin(32);
            if (binTrace)
                DiagnosticOutput!.WriteLine($"    bit5={b4} ctx=32 post:{decoder.DiagState}");
            int b5 = decoder.DecodeBin(32);
            if (binTrace)
                DiagnosticOutput!.WriteLine($"    bit6={b5} ctx=32 post:{decoder.DiagState}");
            int bits = (b2 << 3) | (b3 << 2) | (b4 << 1) | b5;
            
            if (binTrace)
                DiagnosticOutput!.WriteLine($"    4bits={bits} (b2={b2} b3={b3} b4={b4} b5={b5})");
            
            if (bits < 8)
                return bits + 3;  // types 3-10 (B_Bi_16x16 through B_L1_L0_16x8)
            
            if (bits == 13)
            {
                // Intra macroblock in B-slice
                int intraMbType = DecodeMbTypeISliceCabac(ref decoder, 32, 0);
                return 23 + intraMbType;
            }
            
            if (bits == 14)
                return 11;  // B_L1_L0_8x16
            
            if (bits == 15)
                return 22;  // B_8x8
            
            // bits 8-12: one more bin at ctx 32 → types 12-21
            int extraBin = decoder.DecodeBin(32);
            return (bits << 1) + extraBin - 4;
        }
    }

    /// <summary>
    /// Decode I-slice mb_type per Table 9-34 / Table 9-39 / Table 9-41.
    /// ctxIdxOffset: 3 for I-slice, 17 for P/SP-slice intra, 32 for B-slice intra.
    /// </summary>
    private int DecodeMbTypeISliceCabac(ref CabacDecoder decoder, int ctxIdxOffset, int bin0CtxIdxInc)
    {
        // Follows FFmpeg's decode_cabac_intra_mb_type exactly.
        // intra_slice=1 for I-slice (ctxIdxOffset=3), 0 for P/B-slice intra.
        int intraSlice = (ctxIdxOffset == 3) ? 1 : 0;

        // bin 0: ctx = ctxIdxOffset + ctxIdxInc (neighbor-based)
        int bin0 = decoder.DecodeBin(ctxIdxOffset + bin0CtxIdxInc);
        if (bin0 == 0)
            return 0;  // I_NxN (I_4x4 or I_8x8)

        // bin 1: ctx = 276 (TERMINATE) — differentiates I_PCM from I_16x16
        int bin1 = decoder.DecodeTerminate();
        if (bin1 != 0)
            return 25;  // I_PCM

        // FFmpeg does state += 2 for intra_slice before suffix bins, shifting the base
        // For I-slice (intraSlice=1): suffixBase = ctxIdxOffset + 2, contexts 6,7,8,9,10
        // For P/B intra (intraSlice=0): suffixBase = ctxIdxOffset, contexts 4,5,5,6,6
        int suffixBase = ctxIdxOffset + 2 * intraSlice;
        int mbType = 1;
        mbType += 12 * decoder.DecodeBin(suffixBase + 1);  // cbpLumaAC

        if (decoder.DecodeBin(suffixBase + 2) != 0)  // cbpChroma bit 0
            mbType += 4 + 4 * decoder.DecodeBin(suffixBase + 2 + intraSlice);  // cbpChroma bit 1

        mbType += 2 * decoder.DecodeBin(suffixBase + 3 + intraSlice);  // predMode bit 0
        mbType += decoder.DecodeBin(suffixBase + 3 + 2 * intraSlice);  // predMode bit 1

        return mbType;
    }

    private void ParseMbTypeCabac(int mbType, SliceType sliceType, ref Macroblock mb)
    {
        bool isISlice = sliceType == SliceType.ISlice || sliceType == SliceType.SiSlice;
        
        if (isISlice)
        {
            mb.IsIntra = true;
            if (mbType == 0)
            {
                mb.IntraType = IntraMacroblockType.Intra4x4;
            }
            else if (mbType >= 1 && mbType <= 24)
            {
                mb.IntraType = (IntraMacroblockType)mbType;
                int i16x16Idx = mbType - 1;
                mb.Intra16x16PredMode = (Intra16x16PredictionMode)(i16x16Idx % 4);
                mb.CodedBlockPatternChroma = (i16x16Idx / 4) % 3;
                mb.CodedBlockPatternLuma = (i16x16Idx / 12) > 0 ? 15 : 0;
            }
            else
            {
                mb.IntraType = IntraMacroblockType.IntraPcm;
            }
        }
        else
        {
            int intraOffset = sliceType == SliceType.PSlice ? 5 : 23;
            if (mbType >= intraOffset)
            {
                mb.IsIntra = true;
                int intraMbType = mbType - intraOffset;
                if (intraMbType == 0)
                    mb.IntraType = IntraMacroblockType.Intra4x4;
                else if (intraMbType >= 1 && intraMbType <= 24)
                {
                    mb.IntraType = (IntraMacroblockType)intraMbType;
                    int i16x16Idx = intraMbType - 1;
                    mb.Intra16x16PredMode = (Intra16x16PredictionMode)(i16x16Idx % 4);
                    mb.CodedBlockPatternChroma = (i16x16Idx / 4) % 3;
                    mb.CodedBlockPatternLuma = (i16x16Idx / 12) > 0 ? 15 : 0;
                }
                else
                    mb.IntraType = IntraMacroblockType.IntraPcm;
            }
            else
            {
                mb.IsIntra = false;
                // Inter macroblock - simplified handling
            }
        }
    }

    private void DecodeIntra4x4ModesCabac<TShape>(ref CabacDecoder decoder, ref Macroblock mb)
        where TShape : struct, IPictureShape
    {
        int mbAddr = mb.Address;
        int mbX = mb.PositionX;
        int mbY = mb.PositionY;
        int mbWidth = context.WidthInMacroblocks;
        
        for (int i = 0; i < 16; i++)
        {
            // Compute most probable mode from left and top neighbors
            int bx = ZscanToBlockX[i];
            int by = ZscanToBlockY[i];
            
            int modeA = GetIntra4x4PredModeNeighbor<TShape>(mbAddr, mbX, mbY, mbWidth, bx - 1, by, i);
            int modeB = GetIntra4x4PredModeNeighbor<TShape>(mbAddr, mbX, mbY, mbWidth, bx, by - 1, i);
            int minMode = Math.Min(modeA, modeB);
            int mostProbableMode = minMode < 0 ? 2 : minMode; // DC_PRED=2 when unavailable
            
                        
            if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac17(ref decoder);
            
            [MethodImpl(MethodImplOptions.NoInlining)]
            
            void TraceCabac17(ref CabacDecoder decoder)
            
            {
            
            DiagnosticOutput.WriteLine($"  I4x4-PRE[{i}] ctx68={decoder.GetContextState(68)} ctx69={decoder.GetContextState(69)} {decoder.DiagState}");
            
            }
            int prevModeFlag = decoder.DecodeBin(68);
            if (prevModeFlag == 1)
            {
                unsafe { mb.Intra4x4PredModes[i] = (byte)mostProbableMode; }
                mbIntra4x4Modes![mbAddr * 16 + i] = (byte)mostProbableMode;
                                if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac18();
                [MethodImpl(MethodImplOptions.NoInlining)]
                void TraceCabac18()
                {
                DiagnosticOutput.WriteLine($"  I4x4-MODE[{i}] blk=({bx},{by}) modeA={modeA} modeB={modeB} mpp={mostProbableMode} prev=1 → mode={mostProbableMode}");
                }
            }
            else
            {
                                if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac19(ref decoder);
                [MethodImpl(MethodImplOptions.NoInlining)]
                void TraceCabac19(ref CabacDecoder decoder)
                {
                DiagnosticOutput.WriteLine($"  I4x4-REM-PRE[{i}] ctx69={decoder.GetContextState(69)} R={decoder.DebugRange} O={decoder.DebugOffset}");
                }
                int mode = 0;
                mode |= decoder.DecodeBin(69);
                mode |= decoder.DecodeBin(69) << 1;
                mode |= decoder.DecodeBin(69) << 2;
                if (mode >= mostProbableMode)
                    mode++;
                unsafe { mb.Intra4x4PredModes[i] = (byte)mode; }
                mbIntra4x4Modes![mbAddr * 16 + i] = (byte)mode;
                                if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac20();
                [MethodImpl(MethodImplOptions.NoInlining)]
                void TraceCabac20()
                {
                DiagnosticOutput.WriteLine($"  I4x4-MODE[{i}] blk=({bx},{by}) modeA={modeA} modeB={modeB} mpp={mostProbableMode} prev=0 rem={mode - (mode > mostProbableMode ? 1 : 0)} → mode={mode}");
                }
            }
        }
    }

    /// <summary>
    /// Returns 0, 1, or 2: the count of neighboring MBs (left + top) that use 8x8 transform.
    /// Used as ctxIdxInc for transform_size_8x8_flag at context index 399 + result.
    /// </summary>
    private int GetNeighborTransformSize<TShape>(int mbAddr, int mbX, int mbY, int mbWidth)
        where TShape : struct, IPictureShape
    {
        int leftAddr = TShape.Mbaff ? mbaffLeftXYTop : mbAddr - 1;
        int topAddr = TShape.Mbaff ? mbaffTopXY : mbAddr - mbWidth;
        int count = 0;
        if (mbX > 0 && cabacTransform8x8 != null && leftAddr >= 0 && leftAddr < mbSliceIndex!.Length && mbSliceIndex[leftAddr] == currentSliceIdx && cabacTransform8x8[leftAddr] != 0)
            count++;
        if (topAddr >= 0 && topAddr < mbSliceIndex!.Length && cabacTransform8x8 != null && mbSliceIndex[topAddr] == currentSliceIdx && cabacTransform8x8[topAddr] != 0)
            count++;
        return count;
    }

    /// <summary>
    /// Decodes intra 8x8 prediction modes (4 modes, one per 8x8 block).
    /// Each mode fills a 2x2 region in the 4x4 mode cache, matching ffmpeg's fill_rectangle.
    /// Same CABAC syntax as intra4x4 (prev_mode_flag + rem_mode), just 4 iterations.
    /// </summary>
    private void DecodeIntra8x8ModesCabac<TShape>(ref CabacDecoder decoder, ref Macroblock mb)
        where TShape : struct, IPictureShape
    {
        int mbAddr = mb.Address;
        int mbX = mb.PositionX;
        int mbY = mb.PositionY;
        int mbWidth = context.WidthInMacroblocks;

        // Iterate over the 4 8x8 blocks; i is the first 4x4 block in each 8x8 group
        for (int i8x8 = 0; i8x8 < 4; i8x8++)
        {
            int i = i8x8 * 4; // First 4x4 block index: 0, 4, 8, 12

            // Get most probable mode from left/top neighbor of this 8x8 block
            int bx = ZscanToBlockX[i];
            int by = ZscanToBlockY[i];

            int modeA = GetIntra4x4PredModeNeighbor<TShape>(mbAddr, mbX, mbY, mbWidth, bx - 1, by, i);
            int modeB = GetIntra4x4PredModeNeighbor<TShape>(mbAddr, mbX, mbY, mbWidth, bx, by - 1, i);
            int minMode = Math.Min(modeA, modeB);
            int mostProbableMode = minMode < 0 ? 2 : minMode;

            int prevModeFlag = decoder.DecodeBin(68);
            int mode;
            if (prevModeFlag == 1)
            {
                mode = mostProbableMode;
            }
            else
            {
                mode = 0;
                mode |= decoder.DecodeBin(69);
                mode |= decoder.DecodeBin(69) << 1;
                mode |= decoder.DecodeBin(69) << 2;
                if (mode >= mostProbableMode)
                    mode++;
            }

            // Fill all 4 sub-blocks with the same mode (2x2 in scan8 cache)
            unsafe
            {
                mb.Intra4x4PredModes[i] = (byte)mode;
                mb.Intra4x4PredModes[i + 1] = (byte)mode;
                mb.Intra4x4PredModes[i + 2] = (byte)mode;
                mb.Intra4x4PredModes[i + 3] = (byte)mode;
            }
            mbIntra4x4Modes![mbAddr * 16 + i] = (byte)mode;
            mbIntra4x4Modes[mbAddr * 16 + i + 1] = (byte)mode;
            mbIntra4x4Modes[mbAddr * 16 + i + 2] = (byte)mode;
            mbIntra4x4Modes[mbAddr * 16 + i + 3] = (byte)mode;
        }
    }

    private int GetIntra4x4PredModeNeighbor<TShape>(int mbAddr, int mbX, int mbY, int mbWidth, int nx, int ny, int blkIdx)
        where TShape : struct, IPictureShape
    {
        // Per H.264 spec 8.3.1.1:
        // If neighbor is Intra4x4/Intra8x8: return that block's prediction mode
        // If neighbor is available but NOT Intra4x4: return 2 (DC prediction mode)
        // If neighbor is unavailable: return -1
        // When constrained_intra_pred_flag is 1: inter-coded neighbors are treated as unavailable
        
        if (nx >= 0 && nx < 4 && ny >= 0 && ny < 4)
        {
            // Internal neighbor within current MB — always I4x4
            int neighborZscan = RasterToZscan[ny * 4 + nx];
            return mbIntra4x4Modes![mbAddr * 16 + neighborZscan];
        }
        
        if (nx < 0)
        {
            if (mbX == 0) return -1;
            int leftAddr;
            int actualNy = ny;
            if (TShape.Mbaff)
            {
                // MBAFF: use LTOP for ny 0,1 and LBOT for ny 2,3
                // Apply left_block remapping for the row index
                leftAddr = ny < 2 ? mbaffLeftXYTop : mbaffLeftXYBot;
                int[] lb = LeftBlockOptions[mbaffLeftBlockIdx];
                actualNy = lb[ny];
            }
            else
            {
                leftAddr = mbAddr - 1;
            }
            if (leftAddr < 0 || leftAddr >= mbSliceIndex!.Length || mbSliceIndex[leftAddr] != currentSliceIdx) return -1;
            if (!IsIntra4x4Mb(leftAddr))
            {
                // Under CIP, inter-coded neighbors are unavailable
                if (constrainedIntraPred && mbIsIntra != null && !mbIsIntra[leftAddr])
                    return -1;
                return 2; // Available but not I4x4 → DC mode per spec
            }
            int neighborZscan = RasterToZscan[actualNy * 4 + 3];
            return mbIntra4x4Modes![leftAddr * 16 + neighborZscan];
        }
        
        if (ny < 0)
        {
            int topAddr = TShape.Mbaff ? mbaffTopXY : mbAddr - mbWidth;
            if (topAddr < 0 || topAddr >= mbSliceIndex!.Length || mbSliceIndex[topAddr] != currentSliceIdx) return -1;
            if (!IsIntra4x4Mb(topAddr))
            {
                if (constrainedIntraPred && mbIsIntra != null && !mbIsIntra[topAddr])
                    return -1;
                return 2; // Available but not I4x4 → DC mode per spec
            }
            int neighborZscan = RasterToZscan[3 * 4 + nx];
            return mbIntra4x4Modes![topAddr * 16 + neighborZscan];
        }
        
        return -1;
    }
    
    private bool IsIntra4x4Mb(int mbAddr)
    {
        return cabacIsIntra4x4 != null && cabacIsIntra4x4[mbAddr];
    }

    private int DecodeIntraChromaPredModeCabac<TShape>(ref CabacDecoder decoder, int mbAddr, int mbWidth)
        where TShape : struct, IPictureShape
    {
        // Context 64-67 for intra_chroma_pred_mode (TU, cMax=3)
        // bin 0: ctx = 64 + ctxIdxInc (neighbor mode != 0 → increment)
        // bins 1-2: ctx = 64 + 3 = 67
        // FFmpeg: uses sl->left_mb_xy[0] and sl->top_mb_xy (MBAFF-aware addresses)
        int mbX = mbAddr % mbWidth;
        int mbY = mbAddr / mbWidth;
        int leftAddr = TShape.Mbaff ? mbaffLeftXYTop : mbAddr - 1;
        int topAddr = TShape.Mbaff ? mbaffTopXY : mbAddr - mbWidth;
        int ctx = 0;
        if (mbX > 0 && cabacChromaPredMode != null && leftAddr >= 0 && leftAddr < mbSliceIndex!.Length && mbSliceIndex[leftAddr] == currentSliceIdx && cabacChromaPredMode[leftAddr] != 0)
            ctx++;
        if (topAddr >= 0 && topAddr < mbSliceIndex!.Length && cabacChromaPredMode != null && mbSliceIndex[topAddr] == currentSliceIdx && cabacChromaPredMode[topAddr] != 0)
            ctx++;
        
        if (decoder.DecodeBin(64 + ctx) == 0)
            return 0;
        if (decoder.DecodeBin(67) == 0)
            return 1;
        if (decoder.DecodeBin(67) == 0)
            return 2;
        return 3;
    }

    /// <summary>
    /// Prepare the MVD cache for the current macroblock from stored neighbor boundary values.
    /// Left border (cache col 3) from left MB's right column; top border (cache row 0) from top MB's bottom row.
    /// </summary>
    private void PrepareMvdCache<TShape>(int mbAddr, int mbX, int mbY, int mbWidth)
        where TShape : struct, IPictureShape
    {
        // Match FFmpeg fill_decode_caches: only set border positions, leave interior stale.
        // For 16x16 B-slice types with an unused prediction list, the stale interior values
        // propagate through StoreMvdBoundaries to neighboring MBs, matching the encoder's
        // amvd context assumptions. Clearing the interior would produce wrong amvd values.
        
        // Top border (positions 4-7): from top MB's bottom row, or 0 if unavailable
        int topAddr = TShape.Mbaff ? mbaffTopXY : mbAddr - mbWidth;
        if (topAddr >= 0 && topAddr < mbSliceIndex!.Length && cabacAbsMvdXBottom != null && mbSliceIndex[topAddr] == currentSliceIdx)
        {
            int topBase = topAddr * 4;
            mvdCacheX[4] = cabacAbsMvdXBottom[topBase];
            mvdCacheX[5] = cabacAbsMvdXBottom[topBase + 1];
            mvdCacheX[6] = cabacAbsMvdXBottom[topBase + 2];
            mvdCacheX[7] = cabacAbsMvdXBottom[topBase + 3];
            mvdCacheY[4] = cabacAbsMvdYBottom![topBase];
            mvdCacheY[5] = cabacAbsMvdYBottom[topBase + 1];
            mvdCacheY[6] = cabacAbsMvdYBottom[topBase + 2];
            mvdCacheY[7] = cabacAbsMvdYBottom[topBase + 3];
            mvdCacheL1X[4] = cabacAbsMvdL1XBottom![topBase];
            mvdCacheL1X[5] = cabacAbsMvdL1XBottom[topBase + 1];
            mvdCacheL1X[6] = cabacAbsMvdL1XBottom[topBase + 2];
            mvdCacheL1X[7] = cabacAbsMvdL1XBottom[topBase + 3];
            mvdCacheL1Y[4] = cabacAbsMvdL1YBottom![topBase];
            mvdCacheL1Y[5] = cabacAbsMvdL1YBottom[topBase + 1];
            mvdCacheL1Y[6] = cabacAbsMvdL1YBottom[topBase + 2];
            mvdCacheL1Y[7] = cabacAbsMvdL1YBottom[topBase + 3];
            
            // MBAFF MVD Y scaling for cross-type top neighbor (FFmpeg MAP_F2F)
            if (TShape.Mbaff)
            {
                bool currentIsField = mbFieldDecodingFlag![mbAddr];
                bool topIsField = mbFieldDecodingFlag[topAddr];
                if (currentIsField && !topIsField)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (cabacRefL0Bottom[topBase + i] >= 0) mvdCacheY[4 + i] >>= 1;
                        if (cabacRefL1Bottom![topBase + i] >= 0) mvdCacheL1Y[4 + i] >>= 1;
                    }
                }
                else if (!currentIsField && topIsField)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (cabacRefL0Bottom[topBase + i] >= 0) mvdCacheY[4 + i] <<= 1;
                        if (cabacRefL1Bottom![topBase + i] >= 0) mvdCacheL1Y[4 + i] <<= 1;
                    }
                }
            }
        }
        else
        {
            mvdCacheX[4] = mvdCacheX[5] = mvdCacheX[6] = mvdCacheX[7] = 0;
            mvdCacheY[4] = mvdCacheY[5] = mvdCacheY[6] = mvdCacheY[7] = 0;
            mvdCacheL1X[4] = mvdCacheL1X[5] = mvdCacheL1X[6] = mvdCacheL1X[7] = 0;
            mvdCacheL1Y[4] = mvdCacheL1Y[5] = mvdCacheL1Y[6] = mvdCacheL1Y[7] = 0;
        }
        
        // Left border (positions 11, 19, 27, 35): from left MB's right column
        // MBAFF: uses LTOP for rows 0,1 (positions 11,19) and LBOT for rows 2,3 (positions 27,35)
        // with left_block remapping for the source row index
        if (TShape.Mbaff && mbX > 0)
        {
            int[] lb = LeftBlockOptions[mbaffLeftBlockIdx];
            int ltAddr = mbaffLeftXYTop;
            int lbAddr = mbaffLeftXYBot;
            
            // Positions 11, 19 (rows 0, 1) → from LTOP
            if (ltAddr >= 0 && ltAddr < mbSliceIndex!.Length && cabacAbsMvdXRight != null && mbSliceIndex[ltAddr] == currentSliceIdx)
            {
                int ltBase = ltAddr * 4;
                mvdCacheX[11] = cabacAbsMvdXRight[ltBase + lb[0]];
                mvdCacheX[19] = cabacAbsMvdXRight[ltBase + lb[1]];
                mvdCacheY[11] = cabacAbsMvdYRight![ltBase + lb[0]];
                mvdCacheY[19] = cabacAbsMvdYRight[ltBase + lb[1]];
                mvdCacheL1X[11] = cabacAbsMvdL1XRight![ltBase + lb[0]];
                mvdCacheL1X[19] = cabacAbsMvdL1XRight[ltBase + lb[1]];
                mvdCacheL1Y[11] = cabacAbsMvdL1YRight![ltBase + lb[0]];
                mvdCacheL1Y[19] = cabacAbsMvdL1YRight[ltBase + lb[1]];
                
                // MBAFF MVD Y scaling for cross-type left-top neighbor (FFmpeg MAP_F2F)
                bool currentIsField = mbFieldDecodingFlag![mbAddr];
                bool ltIsField = mbFieldDecodingFlag[ltAddr];
                if (currentIsField && !ltIsField)
                {
                    if (cabacRefL0Right![ltBase + lb[0]] >= 0) mvdCacheY[11] >>= 1;
                    if (cabacRefL0Right[ltBase + lb[1]] >= 0) mvdCacheY[19] >>= 1;
                    if (cabacRefL1Right![ltBase + lb[0]] >= 0) mvdCacheL1Y[11] >>= 1;
                    if (cabacRefL1Right[ltBase + lb[1]] >= 0) mvdCacheL1Y[19] >>= 1;
                }
                else if (!currentIsField && ltIsField)
                {
                    if (cabacRefL0Right![ltBase + lb[0]] >= 0) mvdCacheY[11] <<= 1;
                    if (cabacRefL0Right[ltBase + lb[1]] >= 0) mvdCacheY[19] <<= 1;
                    if (cabacRefL1Right![ltBase + lb[0]] >= 0) mvdCacheL1Y[11] <<= 1;
                    if (cabacRefL1Right[ltBase + lb[1]] >= 0) mvdCacheL1Y[19] <<= 1;
                }
            }
            else
            {
                mvdCacheX[11] = mvdCacheX[19] = 0;
                mvdCacheY[11] = mvdCacheY[19] = 0;
                mvdCacheL1X[11] = mvdCacheL1X[19] = 0;
                mvdCacheL1Y[11] = mvdCacheL1Y[19] = 0;
            }
            
            // Positions 27, 35 (rows 2, 3) → from LBOT
            if (lbAddr >= 0 && lbAddr < mbSliceIndex!.Length && cabacAbsMvdXRight != null && mbSliceIndex[lbAddr] == currentSliceIdx)
            {
                int lbBase = lbAddr * 4;
                mvdCacheX[27] = cabacAbsMvdXRight[lbBase + lb[2]];
                mvdCacheX[35] = cabacAbsMvdXRight[lbBase + lb[3]];
                mvdCacheY[27] = cabacAbsMvdYRight![lbBase + lb[2]];
                mvdCacheY[35] = cabacAbsMvdYRight[lbBase + lb[3]];
                mvdCacheL1X[27] = cabacAbsMvdL1XRight![lbBase + lb[2]];
                mvdCacheL1X[35] = cabacAbsMvdL1XRight[lbBase + lb[3]];
                mvdCacheL1Y[27] = cabacAbsMvdL1YRight![lbBase + lb[2]];
                mvdCacheL1Y[35] = cabacAbsMvdL1YRight[lbBase + lb[3]];
                
                // MBAFF MVD Y scaling for cross-type left-bottom neighbor (FFmpeg MAP_F2F)
                bool curIsField = mbFieldDecodingFlag![mbAddr];
                bool lbIsField = mbFieldDecodingFlag[lbAddr];
                if (curIsField && !lbIsField)
                {
                    if (cabacRefL0Right![lbBase + lb[2]] >= 0) mvdCacheY[27] >>= 1;
                    if (cabacRefL0Right[lbBase + lb[3]] >= 0) mvdCacheY[35] >>= 1;
                    if (cabacRefL1Right![lbBase + lb[2]] >= 0) mvdCacheL1Y[27] >>= 1;
                    if (cabacRefL1Right[lbBase + lb[3]] >= 0) mvdCacheL1Y[35] >>= 1;
                }
                else if (!curIsField && lbIsField)
                {
                    if (cabacRefL0Right![lbBase + lb[2]] >= 0) mvdCacheY[27] <<= 1;
                    if (cabacRefL0Right[lbBase + lb[3]] >= 0) mvdCacheY[35] <<= 1;
                    if (cabacRefL1Right![lbBase + lb[2]] >= 0) mvdCacheL1Y[27] <<= 1;
                    if (cabacRefL1Right[lbBase + lb[3]] >= 0) mvdCacheL1Y[35] <<= 1;
                }
            }
            else
            {
                mvdCacheX[27] = mvdCacheX[35] = 0;
                mvdCacheY[27] = mvdCacheY[35] = 0;
                mvdCacheL1X[27] = mvdCacheL1X[35] = 0;
                mvdCacheL1Y[27] = mvdCacheL1Y[35] = 0;
            }
        }
        else if (mbX > 0 && cabacAbsMvdXRight != null && mbSliceIndex![mbAddr - 1] == currentSliceIdx)
        {
            int leftBase = (mbAddr - 1) * 4;
            mvdCacheX[11] = cabacAbsMvdXRight[leftBase];
            mvdCacheX[19] = cabacAbsMvdXRight[leftBase + 1];
            mvdCacheX[27] = cabacAbsMvdXRight[leftBase + 2];
            mvdCacheX[35] = cabacAbsMvdXRight[leftBase + 3];
            mvdCacheY[11] = cabacAbsMvdYRight![leftBase];
            mvdCacheY[19] = cabacAbsMvdYRight[leftBase + 1];
            mvdCacheY[27] = cabacAbsMvdYRight[leftBase + 2];
            mvdCacheY[35] = cabacAbsMvdYRight[leftBase + 3];
            mvdCacheL1X[11] = cabacAbsMvdL1XRight![leftBase];
            mvdCacheL1X[19] = cabacAbsMvdL1XRight[leftBase + 1];
            mvdCacheL1X[27] = cabacAbsMvdL1XRight[leftBase + 2];
            mvdCacheL1X[35] = cabacAbsMvdL1XRight[leftBase + 3];
            mvdCacheL1Y[11] = cabacAbsMvdL1YRight![leftBase];
            mvdCacheL1Y[19] = cabacAbsMvdL1YRight[leftBase + 1];
            mvdCacheL1Y[27] = cabacAbsMvdL1YRight[leftBase + 2];
            mvdCacheL1Y[35] = cabacAbsMvdL1YRight[leftBase + 3];
        }
        else
        {
            mvdCacheX[11] = mvdCacheX[19] = mvdCacheX[27] = mvdCacheX[35] = 0;
            mvdCacheY[11] = mvdCacheY[19] = mvdCacheY[27] = mvdCacheY[35] = 0;
            mvdCacheL1X[11] = mvdCacheL1X[19] = mvdCacheL1X[27] = mvdCacheL1X[35] = 0;
            mvdCacheL1Y[11] = mvdCacheL1Y[19] = mvdCacheL1Y[27] = mvdCacheL1Y[35] = 0;
        }
    }
    
    /// <summary>
    /// Store the MVD cache boundary values (right column and bottom row) for the current macroblock.
    /// Stores both L0 and L1 boundaries for B-slice support.
    /// </summary>
    private void StoreMvdBoundaries(int mbAddr)
    {
        int b = mbAddr * 4;
        // L0: Right column (cache positions 15, 23, 31, 39) and bottom row (36, 37, 38, 39)
        cabacAbsMvdXRight![b] = mvdCacheX[15]; cabacAbsMvdXRight[b + 1] = mvdCacheX[23];
        cabacAbsMvdXRight[b + 2] = mvdCacheX[31]; cabacAbsMvdXRight[b + 3] = mvdCacheX[39];
        cabacAbsMvdYRight![b] = mvdCacheY[15]; cabacAbsMvdYRight[b + 1] = mvdCacheY[23];
        cabacAbsMvdYRight[b + 2] = mvdCacheY[31]; cabacAbsMvdYRight[b + 3] = mvdCacheY[39];
        cabacAbsMvdXBottom![b] = mvdCacheX[36]; cabacAbsMvdXBottom[b + 1] = mvdCacheX[37];
        cabacAbsMvdXBottom[b + 2] = mvdCacheX[38]; cabacAbsMvdXBottom[b + 3] = mvdCacheX[39];
        cabacAbsMvdYBottom![b] = mvdCacheY[36]; cabacAbsMvdYBottom[b + 1] = mvdCacheY[37];
        cabacAbsMvdYBottom[b + 2] = mvdCacheY[38]; cabacAbsMvdYBottom[b + 3] = mvdCacheY[39];
        // L1: same positions from L1 cache
        cabacAbsMvdL1XRight![b] = mvdCacheL1X[15]; cabacAbsMvdL1XRight[b + 1] = mvdCacheL1X[23];
        cabacAbsMvdL1XRight[b + 2] = mvdCacheL1X[31]; cabacAbsMvdL1XRight[b + 3] = mvdCacheL1X[39];
        cabacAbsMvdL1YRight![b] = mvdCacheL1Y[15]; cabacAbsMvdL1YRight[b + 1] = mvdCacheL1Y[23];
        cabacAbsMvdL1YRight[b + 2] = mvdCacheL1Y[31]; cabacAbsMvdL1YRight[b + 3] = mvdCacheL1Y[39];
        cabacAbsMvdL1XBottom![b] = mvdCacheL1X[36]; cabacAbsMvdL1XBottom[b + 1] = mvdCacheL1X[37];
        cabacAbsMvdL1XBottom[b + 2] = mvdCacheL1X[38]; cabacAbsMvdL1XBottom[b + 3] = mvdCacheL1X[39];
        cabacAbsMvdL1YBottom![b] = mvdCacheL1Y[36]; cabacAbsMvdL1YBottom[b + 1] = mvdCacheL1Y[37];
        cabacAbsMvdL1YBottom[b + 2] = mvdCacheL1Y[38]; cabacAbsMvdL1YBottom[b + 3] = mvdCacheL1Y[39];
    }
    
    /// <summary>
    /// Store zero MVD boundaries for skipped or intra macroblocks (both L0 and L1).
    /// </summary>
    private void ClearMvdBoundaries(int mbAddr)
    {
        if (cabacAbsMvdXRight == null) return;
        int b = mbAddr * 4;
        cabacAbsMvdXRight[b] = cabacAbsMvdXRight[b + 1] = cabacAbsMvdXRight[b + 2] = cabacAbsMvdXRight[b + 3] = 0;
        cabacAbsMvdYRight![b] = cabacAbsMvdYRight[b + 1] = cabacAbsMvdYRight[b + 2] = cabacAbsMvdYRight[b + 3] = 0;
        cabacAbsMvdXBottom![b] = cabacAbsMvdXBottom[b + 1] = cabacAbsMvdXBottom[b + 2] = cabacAbsMvdXBottom[b + 3] = 0;
        cabacAbsMvdYBottom![b] = cabacAbsMvdYBottom[b + 1] = cabacAbsMvdYBottom[b + 2] = cabacAbsMvdYBottom[b + 3] = 0;
        cabacAbsMvdL1XRight![b] = cabacAbsMvdL1XRight[b + 1] = cabacAbsMvdL1XRight[b + 2] = cabacAbsMvdL1XRight[b + 3] = 0;
        cabacAbsMvdL1YRight![b] = cabacAbsMvdL1YRight[b + 1] = cabacAbsMvdL1YRight[b + 2] = cabacAbsMvdL1YRight[b + 3] = 0;
        cabacAbsMvdL1XBottom![b] = cabacAbsMvdL1XBottom[b + 1] = cabacAbsMvdL1XBottom[b + 2] = cabacAbsMvdL1XBottom[b + 3] = 0;
        cabacAbsMvdL1YBottom![b] = cabacAbsMvdL1YBottom[b + 1] = cabacAbsMvdL1YBottom[b + 2] = cabacAbsMvdL1YBottom[b + 3] = 0;
    }
    
    /// <summary>
    /// Fill a rectangle in the MVD cache with the given absolute mvd values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillMvdCache(int startPos, int width, int height, int absMvdX, int absMvdY)
    {
        for (int row = 0; row < height; row++)
        {
            int rowStart = startPos + row * 8;
            for (int col = 0; col < width; col++)
            {
                mvdCacheX[rowStart + col] = absMvdX;
                mvdCacheY[rowStart + col] = absMvdY;
            }
        }
    }
    
    /// <summary>
    /// Decode a pair of mvd components (x, y) for block index n, updating both
    /// the absolute mvd cache (for CABAC context) and the actual MV cache (for reconstruction).
    /// Computes MV = MVPred + signedMVD using full FFmpeg-style pred_motion.
    /// </summary>
    private void DecodeMvdPairCabac<TShape>(ref CabacDecoder decoder, int n, int fillWidth, int fillHeight, int partWidth, int refIdx)
        where TShape : struct, IPictureShape
    {
        int pos = Scan8[n];
        int amvdX = mvdCacheX[pos - 1] + mvdCacheX[pos - 8];
        int amvdY = mvdCacheY[pos - 1] + mvdCacheY[pos - 8];
        
        diagMvdBinTrace = DiagnosticOutput != null && DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc 
            && cabacCurrentMbAddr == DiagDumpDetailMbAddr;
        
        int absX = DecodeMvdCabac(ref decoder, 40, amvdX, out int signedMvdX);
        int absY = DecodeMvdCabac(ref decoder, 47, amvdY, out int signedMvdY);
        diagMvdBinTrace = false;
        
        FillMvdCache(pos, fillWidth, fillHeight, absX, absY);
        
        PredMotion<TShape>(n, partWidth, 0, refIdx, out short mvpX, out short mvpY);
        short mvX = (short)(mvpX + signedMvdX);
        short mvY = (short)(mvpY + signedMvdY);
        
                
        if (DiagnosticOutput != null && DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc
            && cabacCurrentMbAddr == DiagDumpDetailMbAddr) TraceCabac21();
        
        [MethodImpl(MethodImplOptions.NoInlining)]
        
        void TraceCabac21()
        
        {
        
                    DiagnosticOutput.WriteLine($"  L0-MVD n={n}: mb={cabacCurrentMbAddr} pos={pos} amvd=({amvdX},{amvdY}) mvd=({signedMvdX},{signedMvdY}) abs=({absX},{absY}) mvp=({mvpX},{mvpY}) mv=({mvX},{mvY}) ref={refIdx} pw={partWidth}");
        
                    int idx8 = Scan8[n];
        
                    DiagnosticOutput.WriteLine($"    MVP-CACHE mb={cabacCurrentMbAddr}: leftRef={refCacheL0[idx8-1]} topRef={refCacheL0[idx8-8]} leftMV=({mvCacheL0X[idx8-1]},{mvCacheL0Y[idx8-1]}) topMV=({mvCacheL0X[idx8-8]},{mvCacheL0Y[idx8-8]})");
        
                    int diagRef = FetchDiagonalMv<TShape>(refCacheL0, mvCacheL0X, mvCacheL0Y, idx8, partWidth, 0, out short dMvX, out short dMvY);
        
                    DiagnosticOutput.WriteLine($"    MVP-DIAG mb={cabacCurrentMbAddr}: diagRef={diagRef} diagMV=({dMvX},{dMvY})");
        
                    if (TShape.Mbaff)
        
                        DiagnosticOutput.WriteLine($"    MBAFF-CTX mb={cabacCurrentMbAddr}: lxyT={mbaffLeftXYTop} lxyB={mbaffLeftXYBot} topXY={mbaffTopXY} lbIdx={mbaffLeftBlockIdx} isField={mbFieldDecodingFlag![cabacCurrentMbAddr]}");
        
                }
        
        FillMvCache(mvCacheL0X, mvCacheL0Y, pos, fillWidth, fillHeight, mvX, mvY);
    }
    
    /// <summary>
    /// Decode L0 MVD with 16x8-specific prediction (top or bottom partition).
    /// </summary>
    private void DecodeMvdPairCabac16x8<TShape>(ref CabacDecoder decoder, int n, int refIdx)
        where TShape : struct, IPictureShape
    {
        int pos = Scan8[n];
        int amvdX = mvdCacheX[pos - 1] + mvdCacheX[pos - 8];
        int amvdY = mvdCacheY[pos - 1] + mvdCacheY[pos - 8];
        int absX = DecodeMvdCabac(ref decoder, 40, amvdX, out int signedMvdX);
        int absY = DecodeMvdCabac(ref decoder, 47, amvdY, out int signedMvdY);
        FillMvdCache(pos, 4, 2, absX, absY);
        
        Pred16x8Motion<TShape>(n, 0, refIdx, out short mvpX, out short mvpY);
        short mvX = (short)(mvpX + signedMvdX);
        short mvY = (short)(mvpY + signedMvdY);
        
                
        if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac22();
        
        [MethodImpl(MethodImplOptions.NoInlining)]
        
        void TraceCabac22()
        
        {
        
        DiagnosticOutput.WriteLine($"  MVD16x8: n={n} amvd=({amvdX},{amvdY}) mvd=({signedMvdX},{signedMvdY}) mvp=({mvpX},{mvpY}) mv=({mvX},{mvY}) ref={refIdx}");
        
        }
        
        FillMvCache(mvCacheL0X, mvCacheL0Y, pos, 4, 2, mvX, mvY);
    }
    
    /// <summary>
    /// Decode L0 MVD with 8x16-specific prediction (left or right partition).
    /// </summary>
    private void DecodeMvdPairCabac8x16<TShape>(ref CabacDecoder decoder, int n, int refIdx)
        where TShape : struct, IPictureShape
    {
        int pos = Scan8[n];
        int amvdX = mvdCacheX[pos - 1] + mvdCacheX[pos - 8];
        int amvdY = mvdCacheY[pos - 1] + mvdCacheY[pos - 8];
        int absX = DecodeMvdCabac(ref decoder, 40, amvdX, out int signedMvdX);
        int absY = DecodeMvdCabac(ref decoder, 47, amvdY, out int signedMvdY);
        FillMvdCache(pos, 2, 4, absX, absY);
        
        Pred8x16Motion<TShape>(n, 0, refIdx, out short mvpX, out short mvpY);
        short mvX = (short)(mvpX + signedMvdX);
        short mvY = (short)(mvpY + signedMvdY);
        FillMvCache(mvCacheL0X, mvCacheL0Y, pos, 2, 4, mvX, mvY);
    }
    
    /// <summary>
    /// Decode a pair of L1 mvd components (x, y) for block index n, updating both
    /// the absolute L1 mvd cache and the actual L1 MV cache.
    /// </summary>
    private void DecodeMvdPairCabacL1<TShape>(ref CabacDecoder decoder, int n, int fillWidth, int fillHeight, int partWidth, int refIdx)
        where TShape : struct, IPictureShape
    {
        int pos = Scan8[n];
        int amvdX = mvdCacheL1X[pos - 1] + mvdCacheL1X[pos - 8];
        int amvdY = mvdCacheL1Y[pos - 1] + mvdCacheL1Y[pos - 8];
        
        bool traceL1 = DiagnosticOutput != null && DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc;
        diagMvdBinTrace = traceL1 && cabacCurrentMbAddr == DiagDumpDetailMbAddr;
        
        int absX = DecodeMvdCabac(ref decoder, 40, amvdX, out int signedMvdX);
        int absY = DecodeMvdCabac(ref decoder, 47, amvdY, out int signedMvdY);
        diagMvdBinTrace = false;
        
        FillMvdCacheL1(pos, fillWidth, fillHeight, absX, absY);
        
        PredMotion<TShape>(n, partWidth, 1, refIdx, out short mvpX, out short mvpY);
        short mvX = (short)(mvpX + signedMvdX);
        short mvY = (short)(mvpY + signedMvdY);
        
        if (traceL1)
        {
            DiagnosticOutput!.WriteLine($"  L1-MVD n={n}: pos={pos} amvd=({amvdX},{amvdY}) mvd=({signedMvdX},{signedMvdY}) abs=({absX},{absY}) mvp=({mvpX},{mvpY}) mv=({mvX},{mvY}) ref={refIdx}");
            DiagnosticOutput.WriteLine($"    amvdNeighL=({mvdCacheL1X[pos-1]},{mvdCacheL1Y[pos-1]}) amvdNeighT=({mvdCacheL1X[pos-8]},{mvdCacheL1Y[pos-8]})");
            int cPos = pos - 8 + partWidth;
            int dPos = pos - 8 - 1;
            DiagnosticOutput.WriteLine($"    A(ref={refCacheL1[pos-1]},mv=({mvCacheL1X[pos-1]},{mvCacheL1Y[pos-1]})) B(ref={refCacheL1[pos-8]},mv=({mvCacheL1X[pos-8]},{mvCacheL1Y[pos-8]})) C(pos={cPos},ref={refCacheL1[cPos]},mv=({mvCacheL1X[cPos]},{mvCacheL1Y[cPos]})) D(pos={dPos},ref={refCacheL1[dPos]},mv=({mvCacheL1X[dPos]},{mvCacheL1Y[dPos]}))");
        }
        
        FillMvCache(mvCacheL1X, mvCacheL1Y, pos, fillWidth, fillHeight, mvX, mvY);
    }
    
    /// <summary>
    /// Decode L1 MVD with 16x8-specific prediction.
    /// </summary>
    private void DecodeMvdPairCabacL1_16x8<TShape>(ref CabacDecoder decoder, int n, int refIdx)
        where TShape : struct, IPictureShape
    {
        int pos = Scan8[n];
        int amvdX = mvdCacheL1X[pos - 1] + mvdCacheL1X[pos - 8];
        int amvdY = mvdCacheL1Y[pos - 1] + mvdCacheL1Y[pos - 8];
        int absX = DecodeMvdCabac(ref decoder, 40, amvdX, out int signedMvdX);
        int absY = DecodeMvdCabac(ref decoder, 47, amvdY, out int signedMvdY);
        FillMvdCacheL1(pos, 4, 2, absX, absY);
        
        Pred16x8Motion<TShape>(n, 1, refIdx, out short mvpX, out short mvpY);
        short mvX = (short)(mvpX + signedMvdX);
        short mvY = (short)(mvpY + signedMvdY);
        FillMvCache(mvCacheL1X, mvCacheL1Y, pos, 4, 2, mvX, mvY);
    }
    
    /// <summary>
    /// Decode L1 MVD with 8x16-specific prediction.
    /// </summary>
    private void DecodeMvdPairCabacL1_8x16<TShape>(ref CabacDecoder decoder, int n, int refIdx)
        where TShape : struct, IPictureShape
    {
        int pos = Scan8[n];
        int amvdX = mvdCacheL1X[pos - 1] + mvdCacheL1X[pos - 8];
        int amvdY = mvdCacheL1Y[pos - 1] + mvdCacheL1Y[pos - 8];
        int absX = DecodeMvdCabac(ref decoder, 40, amvdX, out int signedMvdX);
        int absY = DecodeMvdCabac(ref decoder, 47, amvdY, out int signedMvdY);
        FillMvdCacheL1(pos, 2, 4, absX, absY);
        
        Pred8x16Motion<TShape>(n, 1, refIdx, out short mvpX, out short mvpY);
        short mvX = (short)(mvpX + signedMvdX);
        short mvY = (short)(mvpY + signedMvdY);
        FillMvCache(mvCacheL1X, mvCacheL1Y, pos, 2, 4, mvX, mvY);
    }
    
    /// <summary>
    /// Fill a rectangle in the L1 MVD cache with the given absolute mvd values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillMvdCacheL1(int startPos, int width, int height, int absMvdX, int absMvdY)
    {
        for (int row = 0; row < height; row++)
        {
            int rowStart = startPos + row * 8;
            for (int col = 0; col < width; col++)
            {
                mvdCacheL1X[rowStart + col] = absMvdX;
                mvdCacheL1Y[rowStart + col] = absMvdY;
            }
        }
    }

    /// <summary>
    /// Prepare the ref cache for the current macroblock from stored neighbor boundary values.
    /// Same scan8 layout and border/interior pattern as MVD cache.
    /// Stores 0 for unavailable neighbors (skip/intra/Direct don't contribute to ref_idx context).
    /// </summary>
    private void PrepareRefCache<TShape>(int mbAddr, int mbX, int mbY, int mbWidth)
        where TShape : struct, IPictureShape
    {
        // Initialize all border positions to PART_NOT_AVAILABLE (unavailable)
        refCacheL0[3] = refCacheL0[8] = PartNotAvailable;
        refCacheL1[3] = refCacheL1[8] = PartNotAvailable;
        refCacheL0[4] = refCacheL0[5] = refCacheL0[6] = refCacheL0[7] = PartNotAvailable;
        refCacheL1[4] = refCacheL1[5] = refCacheL1[6] = refCacheL1[7] = PartNotAvailable;
        refCacheL0[11] = refCacheL0[19] = refCacheL0[27] = refCacheL0[35] = PartNotAvailable;
        refCacheL1[11] = refCacheL1[19] = refCacheL1[27] = refCacheL1[35] = PartNotAvailable;
        refCacheL0[16] = refCacheL0[24] = refCacheL0[32] = PartNotAvailable;
        refCacheL1[16] = refCacheL1[24] = refCacheL1[32] = PartNotAvailable;

        if (TShape.Mbaff)
        {
            ComputeMbaffNeighbors(mbAddr, mbX, mbY, mbWidth);
            PrepareRefCacheMbaff(mbAddr, mbX, mbY, mbWidth);
            return;
        }

        // --- Non-MBAFF path (unchanged) ---
        if (mbY > 0 && cabacRefL0Bottom != null)
        {
            int topAddr = mbAddr - mbWidth;
            if (mbSliceIndex![topAddr] == currentSliceIdx)
            {
                int topBase = topAddr * 4;
                refCacheL0[4] = cabacRefL0Bottom[topBase];
                refCacheL0[5] = cabacRefL0Bottom[topBase + 1];
                refCacheL0[6] = cabacRefL0Bottom[topBase + 2];
                refCacheL0[7] = cabacRefL0Bottom[topBase + 3];
                refCacheL1[4] = cabacRefL1Bottom![topBase];
                refCacheL1[5] = cabacRefL1Bottom[topBase + 1];
                refCacheL1[6] = cabacRefL1Bottom[topBase + 2];
                refCacheL1[7] = cabacRefL1Bottom[topBase + 3];
            }
            
            if (mbX < mbWidth - 1)
            {
                int topRightAddr = topAddr + 1;
                if (mbSliceIndex[topRightAddr] == currentSliceIdx)
                {
                    int trBase = topRightAddr * 4;
                    refCacheL0[8] = cabacRefL0Bottom[trBase];
                    refCacheL1[8] = cabacRefL1Bottom![trBase];
                }
            }
            
            if (mbX > 0)
            {
                int topLeftAddr = topAddr - 1;
                if (mbSliceIndex[topLeftAddr] == currentSliceIdx)
                {
                    int tlBase = topLeftAddr * 4;
                    refCacheL0[3] = cabacRefL0Bottom[tlBase + 3];
                    refCacheL1[3] = cabacRefL1Bottom![tlBase + 3];
                }
            }
        }
        
        if (mbX > 0 && cabacRefL0Right != null)
        {
            int leftAddr = mbAddr - 1;
            if (mbSliceIndex![leftAddr] == currentSliceIdx)
            {
                int leftBase = leftAddr * 4;
                refCacheL0[11] = cabacRefL0Right[leftBase];
                refCacheL0[19] = cabacRefL0Right[leftBase + 1];
                refCacheL0[27] = cabacRefL0Right[leftBase + 2];
                refCacheL0[35] = cabacRefL0Right[leftBase + 3];
                refCacheL1[11] = cabacRefL1Right![leftBase];
                refCacheL1[19] = cabacRefL1Right[leftBase + 1];
                refCacheL1[27] = cabacRefL1Right[leftBase + 2];
                refCacheL1[35] = cabacRefL1Right[leftBase + 3];
            }
        }
    }

    /// <summary>
    /// MBAFF-aware ref cache preparation. Uses pre-computed neighbor addresses from
    /// ComputeMbaffNeighbors() and applies left_block remapping + ref index scaling.
    /// </summary>
    private void PrepareRefCacheMbaff(int mbAddr, int mbX, int mbY, int mbWidth)
    {
        bool currentIsField = mbFieldDecodingFlag![mbAddr];
        int totalMbs = mbFieldDecodingFlag.Length;

        // Top border
        if (mbaffTopXY >= 0 && mbaffTopXY < totalMbs && cabacRefL0Bottom != null
            && mbSliceIndex![mbaffTopXY] == currentSliceIdx)
        {
            int topBase = mbaffTopXY * 4;
            refCacheL0[4] = cabacRefL0Bottom[topBase];
            refCacheL0[5] = cabacRefL0Bottom[topBase + 1];
            refCacheL0[6] = cabacRefL0Bottom[topBase + 2];
            refCacheL0[7] = cabacRefL0Bottom[topBase + 3];
            refCacheL1[4] = cabacRefL1Bottom![topBase];
            refCacheL1[5] = cabacRefL1Bottom[topBase + 1];
            refCacheL1[6] = cabacRefL1Bottom[topBase + 2];
            refCacheL1[7] = cabacRefL1Bottom[topBase + 3];

            // Ref scaling: field↔frame mismatch
            bool topIsField = mbFieldDecodingFlag[mbaffTopXY];
            if (currentIsField && !topIsField)
            {
                // Current=field, neighbor=frame: double ref, halve MV.y
                for (int i = 4; i <= 7; i++)
                {
                    if (refCacheL0[i] >= 0) refCacheL0[i] <<= 1;
                    if (refCacheL1[i] >= 0) refCacheL1[i] <<= 1;
                }
            }
            else if (!currentIsField && topIsField)
            {
                // Current=frame, neighbor=field: halve ref, double MV.y
                for (int i = 4; i <= 7; i++)
                {
                    if (refCacheL0[i] >= 0) refCacheL0[i] >>= 1;
                    if (refCacheL1[i] >= 0) refCacheL1[i] >>= 1;
                }
            }
        }

        // Top-right corner
        if (mbX < mbWidth - 1 && mbaffTopRightXY >= 0 && mbaffTopRightXY < totalMbs
            && cabacRefL0Bottom != null && mbSliceIndex![mbaffTopRightXY] == currentSliceIdx)
        {
            int trBase = mbaffTopRightXY * 4;
            refCacheL0[8] = cabacRefL0Bottom[trBase];
            refCacheL1[8] = cabacRefL1Bottom![trBase];

            bool trIsField = mbFieldDecodingFlag[mbaffTopRightXY];
            if (currentIsField && !trIsField)
            {
                if (refCacheL0[8] >= 0) refCacheL0[8] <<= 1;
                if (refCacheL1[8] >= 0) refCacheL1[8] <<= 1;
            }
            else if (!currentIsField && trIsField)
            {
                if (refCacheL0[8] >= 0) refCacheL0[8] >>= 1;
                if (refCacheL1[8] >= 0) refCacheL1[8] >>= 1;
            }
        }

        // Top-left corner
        if (mbX > 0 && mbaffTopLeftXY >= 0 && mbaffTopLeftXY < totalMbs
            && cabacRefL0Bottom != null && mbSliceIndex![mbaffTopLeftXY] == currentSliceIdx)
        {
            int tlBase = mbaffTopLeftXY * 4;
            // topleft_partition: -1 = bottom-right 8x8, 0 = top-right 8x8
            if (mbaffTopleftPartition == 0)
            {
                refCacheL0[3] = cabacRefL0Right![tlBase + 1];
                refCacheL1[3] = cabacRefL1Right![tlBase + 1];
            }
            else
            {
                refCacheL0[3] = cabacRefL0Bottom[tlBase + 3];
                refCacheL1[3] = cabacRefL1Bottom![tlBase + 3];
            }

            bool tlIsField = mbFieldDecodingFlag[mbaffTopLeftXY];
            if (currentIsField && !tlIsField)
            {
                if (refCacheL0[3] >= 0) refCacheL0[3] <<= 1;
                if (refCacheL1[3] >= 0) refCacheL1[3] <<= 1;
            }
            else if (!currentIsField && tlIsField)
            {
                if (refCacheL0[3] >= 0) refCacheL0[3] >>= 1;
                if (refCacheL1[3] >= 0) refCacheL1[3] >>= 1;
            }
        }

        // Left border with MBAFF left_block remapping
        if (mbX > 0 && cabacRefL0Right != null)
        {
            int[] lb = LeftBlockOptions[mbaffLeftBlockIdx];

            // LTOP: fills cache positions 11, 19 (top half of left border)
            if (mbaffLeftXYTop >= 0 && mbaffLeftXYTop < totalMbs
                && mbSliceIndex![mbaffLeftXYTop] == currentSliceIdx)
            {
                int ltBase = mbaffLeftXYTop * 4;
                refCacheL0[11] = cabacRefL0Right[ltBase + lb[0]];
                refCacheL0[19] = cabacRefL0Right[ltBase + lb[1]];
                refCacheL1[11] = cabacRefL1Right![ltBase + lb[0]];
                refCacheL1[19] = cabacRefL1Right[ltBase + lb[1]];

                bool ltIsField = mbFieldDecodingFlag[mbaffLeftXYTop];
                if (currentIsField && !ltIsField)
                {
                    if (refCacheL0[11] >= 0) refCacheL0[11] <<= 1;
                    if (refCacheL0[19] >= 0) refCacheL0[19] <<= 1;
                    if (refCacheL1[11] >= 0) refCacheL1[11] <<= 1;
                    if (refCacheL1[19] >= 0) refCacheL1[19] <<= 1;
                }
                else if (!currentIsField && ltIsField)
                {
                    if (refCacheL0[11] >= 0) refCacheL0[11] >>= 1;
                    if (refCacheL0[19] >= 0) refCacheL0[19] >>= 1;
                    if (refCacheL1[11] >= 0) refCacheL1[11] >>= 1;
                    if (refCacheL1[19] >= 0) refCacheL1[19] >>= 1;
                }
            }

            // LBOT: fills cache positions 27, 35 (bottom half of left border)
            if (mbaffLeftXYBot >= 0 && mbaffLeftXYBot < totalMbs
                && mbSliceIndex![mbaffLeftXYBot] == currentSliceIdx)
            {
                int lbBase = mbaffLeftXYBot * 4;
                refCacheL0[27] = cabacRefL0Right[lbBase + lb[2]];
                refCacheL0[35] = cabacRefL0Right[lbBase + lb[3]];
                refCacheL1[27] = cabacRefL1Right![lbBase + lb[2]];
                refCacheL1[35] = cabacRefL1Right[lbBase + lb[3]];

                bool lbIsField = mbFieldDecodingFlag[mbaffLeftXYBot];
                if (currentIsField && !lbIsField)
                {
                    if (refCacheL0[27] >= 0) refCacheL0[27] <<= 1;
                    if (refCacheL0[35] >= 0) refCacheL0[35] <<= 1;
                    if (refCacheL1[27] >= 0) refCacheL1[27] <<= 1;
                    if (refCacheL1[35] >= 0) refCacheL1[35] <<= 1;
                }
                else if (!currentIsField && lbIsField)
                {
                    if (refCacheL0[27] >= 0) refCacheL0[27] >>= 1;
                    if (refCacheL0[35] >= 0) refCacheL0[35] >>= 1;
                    if (refCacheL1[27] >= 0) refCacheL1[27] >>= 1;
                    if (refCacheL1[35] >= 0) refCacheL1[35] >>= 1;
                }
            }
        }
    }
    
    /// <summary>Four consecutive reference indices as one vector.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ReadQuad(int[] source, int index)
        => Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(source), (nuint)index);

    /// <summary>Writes four consecutive reference indices as one vector.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteQuad(int[] destination, int index, Vector128<int> value)
        => value.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(destination), (nuint)index);

    /// <summary>The cache's right column - rows 15, 23, 31, 39 - gathered into one vector.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> GatherRefColumn(int[] cache)
        => Vector128.Create(cache[15], cache[23], cache[31], cache[39]);

    /// <summary>
    /// Store the ref cache boundary values (right column and bottom row) for the current macroblock.
    /// </summary>
    private void StoreRefBoundaries(int mbAddr)
    {
        int b = mbAddr * 4;

        // The bottom row is four consecutive cache entries going to four consecutive frame
        // entries - sixteen bytes, one vector. The right column is four cache rows apart, so
        // only the store side widens.
        WriteQuad(cabacRefL0Bottom!, b, ReadQuad(refCacheL0, 36));
        WriteQuad(cabacRefL1Bottom!, b, ReadQuad(refCacheL1, 36));

        WriteQuad(cabacRefL0Right!, b, GatherRefColumn(refCacheL0));
        WriteQuad(cabacRefL1Right!, b, GatherRefColumn(refCacheL1));
    }
    
    /// <summary>
    /// Store -1 (LIST_NOT_USED) ref boundaries for skipped, intra, or Direct macroblocks.
    /// This ensures neighbor MV prediction doesn't confuse intra refs with ref_idx=0.
    /// </summary>
    private void ClearRefBoundaries(int mbAddr)
    {
        if (cabacRefL0Right == null) return;
        int b = mbAddr * 4;
        Vector128<int> unused = Vector128.Create(-1);
        WriteQuad(cabacRefL0Right, b, unused);
        WriteQuad(cabacRefL0Bottom!, b, unused);
        WriteQuad(cabacRefL1Right!, b, unused);
        WriteQuad(cabacRefL1Bottom!, b, unused);
    }
    
    /// <summary>
    /// Prepare directCache borders from neighboring MBs (like ffmpeg's direct_cache).
    /// Clears all positions to 0, then loads boundary flags from left and top MBs.
    /// </summary>
    private void PrepareDirectCache<TShape>(int mbAddr, int mbX, int mbY, int mbWidth)
        where TShape : struct, IPictureShape
    {
        // Forty entries is a hundred and sixty bytes, which Array.Clear reaches through a
        // length-dispatched helper - the same cost that makes Span.Clear nine times a vector
        // store at these sizes. Ten stores settle it.
        ref int directOrigin = ref MemoryMarshal.GetArrayDataReference(directCache);
        for (int at = 0; at < 40; at += 4)
            Vector128<int>.Zero.StoreUnsafe(ref directOrigin, (nuint)at);

        if (TShape.Mbaff)
        {
            int totalMbs = mbFieldDecodingFlag!.Length;

            // Top: use MBAFF top address
            if (mbaffTopXY >= 0 && mbaffTopXY < totalMbs && directBoundaryBottom != null
                && mbSliceIndex![mbaffTopXY] == currentSliceIdx)
            {
                int topBase = mbaffTopXY * 4;
                directCache[4] = directBoundaryBottom[topBase];
                directCache[5] = directBoundaryBottom[topBase + 1];
                directCache[6] = directBoundaryBottom[topBase + 2];
                directCache[7] = directBoundaryBottom[topBase + 3];
            }

            // Left: use MBAFF left addresses with left_block remapping
            if (mbX > 0 && directBoundaryRight != null)
            {
                int[] lb = LeftBlockOptions[mbaffLeftBlockIdx];

                if (mbaffLeftXYTop >= 0 && mbaffLeftXYTop < totalMbs
                    && mbSliceIndex![mbaffLeftXYTop] == currentSliceIdx)
                {
                    int ltBase = mbaffLeftXYTop * 4;
                    directCache[11] = directBoundaryRight[ltBase + lb[0]];
                    directCache[19] = directBoundaryRight[ltBase + lb[1]];
                }

                if (mbaffLeftXYBot >= 0 && mbaffLeftXYBot < totalMbs
                    && mbSliceIndex![mbaffLeftXYBot] == currentSliceIdx)
                {
                    int lbBase = mbaffLeftXYBot * 4;
                    directCache[27] = directBoundaryRight[lbBase + lb[2]];
                    directCache[35] = directBoundaryRight[lbBase + lb[3]];
                }
            }
            return;
        }

        // Non-MBAFF path (unchanged)
        if (mbY > 0 && directBoundaryBottom != null && mbSliceIndex![mbAddr - mbWidth] == currentSliceIdx)
        {
            int topBase = (mbAddr - mbWidth) * 4;
            directCache[4] = directBoundaryBottom[topBase];
            directCache[5] = directBoundaryBottom[topBase + 1];
            directCache[6] = directBoundaryBottom[topBase + 2];
            directCache[7] = directBoundaryBottom[topBase + 3];
        }
        
        if (mbX > 0 && directBoundaryRight != null && mbSliceIndex![mbAddr - 1] == currentSliceIdx)
        {
            int leftBase = (mbAddr - 1) * 4;
            directCache[11] = directBoundaryRight[leftBase];
            directCache[19] = directBoundaryRight[leftBase + 1];
            directCache[27] = directBoundaryRight[leftBase + 2];
            directCache[35] = directBoundaryRight[leftBase + 3];
        }
    }
    
    /// <summary>
    /// Store directCache boundary values (right column and bottom row) for cross-MB propagation.
    /// </summary>
    private void StoreDirectBoundaries(int mbAddr)
    {
        if (directBoundaryRight == null) return;
        int b = mbAddr * 4;
        directBoundaryRight[b] = directCache[15]; directBoundaryRight[b + 1] = directCache[23];
        directBoundaryRight[b + 2] = directCache[31]; directBoundaryRight[b + 3] = directCache[39];
        directBoundaryBottom![b] = directCache[36]; directBoundaryBottom[b + 1] = directCache[37];
        directBoundaryBottom[b + 2] = directCache[38]; directBoundaryBottom[b + 3] = directCache[39];
    }
    
    /// <summary>
    /// Clear directCache boundaries to 0 for intra/PCM MBs (no Direct sub-blocks).
    /// </summary>
    private void ClearDirectBoundaries(int mbAddr)
    {
        if (directBoundaryRight == null) return;
        int b = mbAddr * 4;
        directBoundaryRight[b] = directBoundaryRight[b + 1] = directBoundaryRight[b + 2] = directBoundaryRight[b + 3] = 0;
        directBoundaryBottom![b] = directBoundaryBottom[b + 1] = directBoundaryBottom[b + 2] = directBoundaryBottom[b + 3] = 0;
    }
    
    /// <summary>
    /// Fill a rectangle in the directCache with the given flag value (1=Direct, 0=not).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillDirectCache(int startPos, int width, int height, int value)
    {
        for (int row = 0; row < height; row++)
        {
            int rowStart = startPos + row * 8;
            for (int col = 0; col < width; col++)
                directCache[rowStart + col] = value;
        }
    }
    
    /// <summary>
    /// Fill a rectangle in the L0 ref cache with the given ref value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillRefCache(int startPos, int width, int height, int refValue)
    {
        for (int row = 0; row < height; row++)
        {
            int rowStart = startPos + row * 8;
            for (int col = 0; col < width; col++)
                refCacheL0[rowStart + col] = refValue;
        }
    }
    
    /// <summary>
    /// Fill a rectangle in the L1 ref cache with the given ref value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillRefCacheL1(int startPos, int width, int height, int refValue)
    {
        for (int row = 0; row < height; row++)
        {
            int rowStart = startPos + row * 8;
            for (int col = 0; col < width; col++)
                refCacheL1[rowStart + col] = refValue;
        }
    }
    
    /// <summary>
    /// Decode inter prediction syntax elements: ref_idx and mvd for P/B-slice inter MBs.
    /// Must consume the correct CABAC bins to keep arithmetic decoder synchronized.
    /// </summary>
    private void DecodeInterPredictionCabac<TShape>(ref CabacDecoder decoder, SliceHeader sliceHeader, int mbType, int mbAddr)
        where TShape : struct, IPictureShape
    {
        bool isPSlice = sliceHeader.Type == SliceType.PSlice;
        int numRefL0 = sliceHeader.NumRefIdxL0ActiveMinus1 + 1;
        int numRefL1 = sliceHeader.NumRefIdxL1ActiveMinus1 + 1;
        // MBAFF field MBs: double ref count (FFmpeg: ref_count << MB_MBAFF)
        if (TShape.Mbaff && mbFieldDecodingFlag != null && mbFieldDecodingFlag[mbAddr])
        {
            numRefL0 <<= 1;
            numRefL1 <<= 1;
        }
        
        // DIAG: trace ref count doubling for field MBs
                if (DiagnosticOutput != null && TShape.Mbaff && mbFieldDecodingFlag != null && mbFieldDecodingFlag[mbAddr]) TraceCabac23(ref decoder);
        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceCabac23(ref CabacDecoder decoder)
        {
        DiagnosticOutput.WriteLine($"  FIELD-INTER: mb={mbAddr} type={mbType} numRefL0={numRefL0} numRefL1={numRefL1} pre:{decoder.DiagState}");
        }
        
        if (isPSlice)
        {
            // P-slices only use L0; zero L1 caches
            FillRefCacheL1(Scan8[0], 4, 4, 0);
            FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[0], 4, 4, 0, 0);
            StoreRefIdx16x16(mbAddr, 1, -1);
            
            switch (mbType)
            {
                case 0: // P_L0_16x16: 1 partition (n=0, 4×4 fill)
                {
                                        if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac24();
                    [MethodImpl(MethodImplOptions.NoInlining)]
                    void TraceCabac24()
                    {
                    DiagnosticOutput.WriteLine($"  SWITCH: case 0 (P_L0_16x16) mbType={mbType}");
                    }
                    int ref0 = DecodeRefIdxCabac(ref decoder, 0, 0, numRefL0 - 1);
                                        if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac25();
                    [MethodImpl(MethodImplOptions.NoInlining)]
                    void TraceCabac25()
                    {
                    DiagnosticOutput.WriteLine($"  P_L0_16x16: ref0={ref0} numRefL0={numRefL0}");
                    }
                    FillRefCache(Scan8[0], 4, 4, ref0);
                    StoreRefIdx16x16(mbAddr, 0, (sbyte)ref0);
                    DecodeMvdPairCabac<TShape>(ref decoder, 0, 4, 4, 4, ref0);
                    break;
                }
                    
                case 1: // P_L0_L0_16x8: 2 partitions (top n=0, bottom n=8)
                {
                                        if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac26();
                    [MethodImpl(MethodImplOptions.NoInlining)]
                    void TraceCabac26()
                    {
                    DiagnosticOutput.WriteLine($"  P_L0_L0_16x8: entering, numRefL0={numRefL0}");
                    }
                    int ref0 = DecodeRefIdxCabac(ref decoder, 0, 0, numRefL0 - 1);
                    FillRefCache(Scan8[0], 4, 2, ref0);
                    StoreRefIdx(mbAddr, 0, 0, (sbyte)ref0);
                    StoreRefIdx(mbAddr, 1, 0, (sbyte)ref0);
                    int ref1 = DecodeRefIdxCabac(ref decoder, 8, 0, numRefL0 - 1);
                    FillRefCache(Scan8[8], 4, 2, ref1);
                    StoreRefIdx(mbAddr, 2, 0, (sbyte)ref1);
                    StoreRefIdx(mbAddr, 3, 0, (sbyte)ref1);
                    DecodeMvdPairCabac16x8<TShape>(ref decoder, 0, ref0);
                    DecodeMvdPairCabac16x8<TShape>(ref decoder, 8, ref1);
                    break;
                }
                    
                case 2: // P_L0_L0_8x16: 2 partitions (left n=0, right n=4)
                {
                    int ref0 = DecodeRefIdxCabac(ref decoder, 0, 0, numRefL0 - 1);
                    FillRefCache(Scan8[0], 2, 4, ref0);
                    StoreRefIdx(mbAddr, 0, 0, (sbyte)ref0);
                    StoreRefIdx(mbAddr, 2, 0, (sbyte)ref0);
                    int ref1 = DecodeRefIdxCabac(ref decoder, 4, 0, numRefL0 - 1);
                    FillRefCache(Scan8[4], 2, 4, ref1);
                    StoreRefIdx(mbAddr, 1, 0, (sbyte)ref1);
                    StoreRefIdx(mbAddr, 3, 0, (sbyte)ref1);
                    DecodeMvdPairCabac8x16<TShape>(ref decoder, 0, ref0);
                    DecodeMvdPairCabac8x16<TShape>(ref decoder, 4, ref1);
                    break;
                }
                    
                case 3: // P_8x8: 4 sub-macroblocks
                    DecodeSubMbTypePSliceCabac<TShape>(ref decoder, numRefL0, mbAddr);
                    break;
            }
        }
        else
        {
            // B-slice inter prediction
            DecodeBSliceInterPredictionCabac<TShape>(ref decoder, sliceHeader, mbType, numRefL0, numRefL1, mbAddr);
        }
    }
    
    /// <summary>
    /// Decode P_8x8 sub-macroblock types, ref indices, and motion vectors.
    /// </summary>
    private void DecodeSubMbTypePSliceCabac<TShape>(ref CabacDecoder decoder, int numRefL0, int mbAddr)
        where TShape : struct, IPictureShape
    {
        // On the stack. This ran once per P_8x8 macroblock, so a four-element array was allocated
        // for every such macroblock in every frame.
        Scratch.Of4<int> subMbTypesStorage = default;
        Span<int> subMbTypes = subMbTypesStorage;
        for (int i = 0; i < 4; i++)
        {
                        if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac27(ref decoder);
            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCabac27(ref CabacDecoder decoder)
            {
            DiagnosticOutput.WriteLine($"  sub_mb_type[{i}] pre: {decoder.DiagState} {decoder.GetContextState(21)} {decoder.GetContextState(22)} {decoder.GetContextState(23)}");
            }
            int bin0 = decoder.DecodeBin(21);
            if (bin0 != 0) { subMbTypes[i] = 0; }
            else
            {
                int bin1 = decoder.DecodeBin(22);
                if (bin1 == 0) subMbTypes[i] = 1;
                else
                {
                    int bin2 = decoder.DecodeBin(23);
                    subMbTypes[i] = bin2 != 0 ? 2 : 3;
                }
            }
                        if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac28(subMbTypes);
            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCabac28(Span<int> subMbTypes)
            {
            DiagnosticOutput.WriteLine($"  sub_mb_type[{i}] = {subMbTypes[i]} ({"P_L0_8x8,P_L0_8x4,P_L0_4x8,P_L0_4x4".Split(',')[subMbTypes[i]]})");
            }
        }
        
        // H.264 spec 7.4.5: noSubMbPartSizeLessThan8x8Flag — only P_L0_8x8 (sub_mb_type=0) is 8x8
        for (int i = 0; i < 4; i++)
        {
            if (subMbTypes[i] != 0)
            {
                allSubPartitionsAreMinimum8x8 = false;
                break;
            }
        }
        
        // Decode ref_idx for each 8x8 partition, fill ref cache and store
        Scratch.Of4<int> refValsStorage = default;
        Span<int> refVals = refValsStorage;
        for (int i = 0; i < 4; i++)
        {
            refVals[i] = DecodeRefIdxCabac(ref decoder, 4 * i, 0, numRefL0 - 1);
            FillRefCache(Scan8[4 * i], 2, 2, refVals[i]);
            StoreRefIdx(mbAddr, i, 0, (sbyte)refVals[i]);
        }
        
        // Mark inner-boundary positions as unavailable per H.264 spec / FFmpeg fill_decode_caches.
        // Scan8[4]=14 (sub-MB 1 origin) and Scan8[12]=30 (sub-MB 3 origin) are C (top-right)
        // neighbors for sub-8x8 partitions in sub-MBs 0 and 2 respectively.
        // They must stay PART_NOT_AVAILABLE until each sub-MB's own MVDs are decoded,
        // otherwise PredMotion reads stale data from the ref fill above.
        refCacheL0[Scan8[4]] = PartNotAvailable;
        refCacheL0[Scan8[12]] = PartNotAvailable;
        mvCacheL0X[Scan8[4]] = mvCacheL0Y[Scan8[4]] = 0;
        mvCacheL0X[Scan8[12]] = mvCacheL0Y[Scan8[12]] = 0;
        
        // Decode mvd for each sub-partition with correct partWidth and refIdx
        for (int i = 0; i < 4; i++)
        {
            // Restore the correct ref at position +0 from +1 (was PART_NOT_AVAILABLE for i=1,3)
            refCacheL0[Scan8[4 * i]] = refCacheL0[Scan8[4 * i] + 1];
            
            int baseBlock = 4 * i;
            int refI = refVals[i];
            switch (subMbTypes[i])
            {
                case 0: // P_L0_8x8: partWidth=2
                    DecodeMvdPairCabac<TShape>(ref decoder, baseBlock, 2, 2, 2, refI);
                    break;
                case 1: // P_L0_8x4: partWidth=2
                    DecodeMvdPairCabac<TShape>(ref decoder, baseBlock, 2, 1, 2, refI);
                    // Targeted trace for mbAddr=14 (MB(14,0)) sub-block 2 bottom 8x4
                                        if (DiagnosticOutput != null && TShape.Mbaff && i == 2 && mbAddr == 14 
                        && DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc) TraceCabac29();
                    [MethodImpl(MethodImplOptions.NoInlining)]
                    void TraceCabac29()
                    {
                                            int s = Scan8[baseBlock + 2]; // scan8[10] = 36
                                            DiagnosticOutput.WriteLine($"  PRED-TRACE blk={baseBlock+2} scan8={s}:");
                                            DiagnosticOutput.WriteLine($"    A(left)  pos={s-1}: mv=({mvCacheL0X[s-1]},{mvCacheL0Y[s-1]}) ref={refCacheL0[s-1]}");
                                            DiagnosticOutput.WriteLine($"    B(top)   pos={s-8}: mv=({mvCacheL0X[s-8]},{mvCacheL0Y[s-8]}) ref={refCacheL0[s-8]}");
                                            DiagnosticOutput.WriteLine($"    C(topR)  pos={s+2-8}: mv=({mvCacheL0X[s+2-8]},{mvCacheL0Y[s+2-8]}) ref={refCacheL0[s+2-8]}");
                                            DiagnosticOutput.WriteLine($"    D(topL)  pos={s-1-8}: mv=({mvCacheL0X[s-1-8]},{mvCacheL0Y[s-1-8]}) ref={refCacheL0[s-1-8]}");
                                        }
                    DecodeMvdPairCabac<TShape>(ref decoder, baseBlock + 2, 2, 1, 2, refI);
                    // After decode: show final MV
                                        if (DiagnosticOutput != null && TShape.Mbaff && i == 2 && mbAddr == 14
                        && DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc) TraceCabac30();
                    [MethodImpl(MethodImplOptions.NoInlining)]
                    void TraceCabac30()
                    {
                                            int s = Scan8[baseBlock + 2];
                                            DiagnosticOutput.WriteLine($"    RESULT:  pos={s}: mv=({mvCacheL0X[s]},{mvCacheL0Y[s]}) ref={refCacheL0[s]}");
                                        }
                    break;
                case 2: // P_L0_4x8: partWidth=1
                    DecodeMvdPairCabac<TShape>(ref decoder, baseBlock, 1, 2, 1, refI);
                    DecodeMvdPairCabac<TShape>(ref decoder, baseBlock + 1, 1, 2, 1, refI);
                    break;
                default: // P_L0_4x4: partWidth=1
                    for (int j = 0; j < 4; j++)
                        DecodeMvdPairCabac<TShape>(ref decoder, baseBlock + j, 1, 1, 1, refI);
                    break;
            }
        }
    }
    
    /// <summary>
    /// Decode B-slice inter prediction syntax elements: ref_idx and mvd per H.264 spec 7.3.5.1.
    /// Uses FFmpeg's exact decode order: all refs (L0 then L1), then all mvds (L0 then L1).
    /// Partition shapes: even mbTypes are 16x8, odd are 8x16 (for types 4-21).
    /// </summary>
    private void DecodeBSliceInterPredictionCabac<TShape>(ref CabacDecoder decoder, SliceHeader sliceHeader, int mbType, int numRefL0, int numRefL1, int mbAddr)
        where TShape : struct, IPictureShape
    {
        if (mbType == 0)
        {
            // B_Direct_16x16: no syntax elements decoded, derive MVs from direct mode
            FillMvdCache(Scan8[0], 4, 4, 0, 0);
            FillMvdCacheL1(Scan8[0], 4, 4, 0, 0);
            if (sliceHeader.DirectSpatialMvPrediction)
                DeriveBDirectSpatialMotionVectors<TShape>(mbAddr);
            else
                DeriveBDirectTemporalMotionVectors<TShape>(mbAddr);
            // B_Direct_16x16: all 16 4x4 blocks are Direct
            FillDirectCache(Scan8[0], 4, 4, 1);
            // FFmpeg h264_cabac.c:2224 — B_Direct gates 8x8 transform on direct_8x8_inference
            if (!context.ActiveSps!.Direct8x8Inference)
                allSubPartitionsAreMinimum8x8 = false;
            return;
        }
        
        if (mbType >= 1 && mbType <= 3)
        {
            bool usesL0 = (mbType == 1 || mbType == 3);
            bool usesL1 = (mbType == 2 || mbType == 3);
            int refL0Val = 0, refL1Val = 0;
            if (usesL0)
            {
                refL0Val = DecodeRefIdxCabac(ref decoder, 0, 0, numRefL0 - 1);
                FillRefCache(Scan8[0], 4, 4, refL0Val);
                StoreRefIdx16x16(mbAddr, 0, (sbyte)refL0Val);
            }
            else
            {
                FillRefCache(Scan8[0], 4, 4, -1);
                StoreRefIdx16x16(mbAddr, 0, -1);
            }
            if (usesL1)
            {
                refL1Val = DecodeRefIdxCabac(ref decoder, 0, 1, numRefL1 - 1);
                FillRefCacheL1(Scan8[0], 4, 4, refL1Val);
                StoreRefIdx16x16(mbAddr, 1, (sbyte)refL1Val);
            }
            else
            {
                FillRefCacheL1(Scan8[0], 4, 4, -1);
                StoreRefIdx16x16(mbAddr, 1, -1);
            }
            if (usesL0) DecodeMvdPairCabac<TShape>(ref decoder, 0, 4, 4, 4, refL0Val);
            else { FillMvdCache(Scan8[0], 4, 4, 0, 0); FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[0], 4, 4, 0, 0); }
            if (usesL1) DecodeMvdPairCabacL1<TShape>(ref decoder, 0, 4, 4, 4, refL1Val);
            else { FillMvdCacheL1(Scan8[0], 4, 4, 0, 0); FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[0], 4, 4, 0, 0); }
        }
        else if (mbType >= 4 && mbType <= 21)
        {
            // Two partitions. Even=16x8, odd=8x16.
            // FFmpeg explicitly zeros unused partition/list combos (lines 2285, 2321).
            bool is16x8 = (mbType % 2 == 0);
            int n0 = 0;
            int n1 = is16x8 ? 8 : 4;
            int fw = is16x8 ? 4 : 2;
            int fh = is16x8 ? 2 : 4;
            
            // Determine which list each partition uses (from H.264 Table 7-14)
            bool p0L0, p0L1, p1L0, p1L1;
            switch (mbType)
            {
                case 4: case 5: p0L0 = p1L0 = true; p0L1 = p1L1 = false; break;
                case 6: case 7: p0L0 = p1L0 = false; p0L1 = p1L1 = true; break;
                case 8: case 9: p0L0 = true; p0L1 = false; p1L0 = false; p1L1 = true; break;
                case 10: case 11: p0L0 = false; p0L1 = true; p1L0 = true; p1L1 = false; break;
                case 12: case 13: p0L0 = true; p0L1 = false; p1L0 = p1L1 = true; break;
                case 14: case 15: p0L0 = false; p0L1 = true; p1L0 = p1L1 = true; break;
                case 16: case 17: p0L0 = p0L1 = true; p1L0 = true; p1L1 = false; break;
                case 18: case 19: p0L0 = p0L1 = true; p1L0 = false; p1L1 = true; break;
                default: p0L0 = p0L1 = p1L0 = p1L1 = true; break; // 20, 21: Bi_Bi
            }
            
            // ref_idx: L0 part0, L0 part1, L1 part0, L1 part1
            {
                int r0l0 = p0L0 ? DecodeRefIdxCabac(ref decoder, n0, 0, numRefL0 - 1) : 0;
                FillRefCache(Scan8[n0], fw, fh, p0L0 ? r0l0 : -1);
                int r1l0 = p1L0 ? DecodeRefIdxCabac(ref decoder, n1, 0, numRefL0 - 1) : 0;
                FillRefCache(Scan8[n1], fw, fh, p1L0 ? r1l0 : -1);
                int r0l1 = p0L1 ? DecodeRefIdxCabac(ref decoder, n0, 1, numRefL1 - 1) : 0;
                FillRefCacheL1(Scan8[n0], fw, fh, p0L1 ? r0l1 : -1);
                int r1l1 = p1L1 ? DecodeRefIdxCabac(ref decoder, n1, 1, numRefL1 - 1) : 0;
                FillRefCacheL1(Scan8[n1], fw, fh, p1L1 ? r1l1 : -1);
                
                sbyte refP0L0 = p0L0 ? (sbyte)r0l0 : (sbyte)-1;
                sbyte refP1L0 = p1L0 ? (sbyte)r1l0 : (sbyte)-1;
                sbyte refP0L1 = p0L1 ? (sbyte)r0l1 : (sbyte)-1;
                sbyte refP1L1 = p1L1 ? (sbyte)r1l1 : (sbyte)-1;
                if (is16x8)
                {
                    StoreRefIdx(mbAddr, 0, 0, refP0L0); StoreRefIdx(mbAddr, 1, 0, refP0L0);
                    StoreRefIdx(mbAddr, 2, 0, refP1L0); StoreRefIdx(mbAddr, 3, 0, refP1L0);
                    StoreRefIdx(mbAddr, 0, 1, refP0L1); StoreRefIdx(mbAddr, 1, 1, refP0L1);
                    StoreRefIdx(mbAddr, 2, 1, refP1L1); StoreRefIdx(mbAddr, 3, 1, refP1L1);
                }
                else
                {
                    StoreRefIdx(mbAddr, 0, 0, refP0L0); StoreRefIdx(mbAddr, 2, 0, refP0L0);
                    StoreRefIdx(mbAddr, 1, 0, refP1L0); StoreRefIdx(mbAddr, 3, 0, refP1L0);
                    StoreRefIdx(mbAddr, 0, 1, refP0L1); StoreRefIdx(mbAddr, 2, 1, refP0L1);
                    StoreRefIdx(mbAddr, 1, 1, refP1L1); StoreRefIdx(mbAddr, 3, 1, refP1L1);
                }
                
                // mvd L0: use partition-specific prediction
                if (is16x8)
                {
                    if (p0L0) DecodeMvdPairCabac16x8<TShape>(ref decoder, n0, r0l0);
                    else { FillMvdCache(Scan8[n0], fw, fh, 0, 0); FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[n0], fw, fh, 0, 0); }
                    if (p1L0) DecodeMvdPairCabac16x8<TShape>(ref decoder, n1, r1l0);
                    else { FillMvdCache(Scan8[n1], fw, fh, 0, 0); FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[n1], fw, fh, 0, 0); }
                    if (p0L1) DecodeMvdPairCabacL1_16x8<TShape>(ref decoder, n0, r0l1);
                    else { FillMvdCacheL1(Scan8[n0], fw, fh, 0, 0); FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[n0], fw, fh, 0, 0); }
                    if (p1L1) DecodeMvdPairCabacL1_16x8<TShape>(ref decoder, n1, r1l1);
                    else { FillMvdCacheL1(Scan8[n1], fw, fh, 0, 0); FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[n1], fw, fh, 0, 0); }
                }
                else
                {
                    if (p0L0) DecodeMvdPairCabac8x16<TShape>(ref decoder, n0, r0l0);
                    else { FillMvdCache(Scan8[n0], fw, fh, 0, 0); FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[n0], fw, fh, 0, 0); }
                    if (p1L0) DecodeMvdPairCabac8x16<TShape>(ref decoder, n1, r1l0);
                    else { FillMvdCache(Scan8[n1], fw, fh, 0, 0); FillMvCache(mvCacheL0X, mvCacheL0Y, Scan8[n1], fw, fh, 0, 0); }
                    if (p0L1) DecodeMvdPairCabacL1_8x16<TShape>(ref decoder, n0, r0l1);
                    else { FillMvdCacheL1(Scan8[n0], fw, fh, 0, 0); FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[n0], fw, fh, 0, 0); }
                    if (p1L1) DecodeMvdPairCabacL1_8x16<TShape>(ref decoder, n1, r1l1);
                    else { FillMvdCacheL1(Scan8[n1], fw, fh, 0, 0); FillMvCache(mvCacheL1X, mvCacheL1Y, Scan8[n1], fw, fh, 0, 0); }
                }
            }
        }
        else if (mbType == 22)
        {
            // B_8x8: 4 sub-macroblocks, each with independent sub_mb_type
            DecodeBSlice8x8Cabac<TShape>(ref decoder, numRefL0, numRefL1, mbAddr);
        }
    }
    
    /// <summary>
    /// Decode B_8x8 sub-macroblock types, ref indices, and motion vectors.
    /// FFmpeg's decode_cabac_b_mb_sub_type tree: ctx 36/37/38/39.
    /// </summary>
    private void DecodeBSlice8x8Cabac<TShape>(ref CabacDecoder decoder, int numRefL0, int numRefL1, int mbAddr)
        where TShape : struct, IPictureShape
    {
        // B sub_mb_type prediction modes: 0=Direct, 1=L0, 2=L1, 3=Bi,
        // 4=L0(8x4), 5=L0(4x8), 6=L1(8x4), 7=L1(4x8), 8=Bi(8x4), 9=Bi(4x8),
        // 10=L0(4x4), 11=L1(4x4), 12=Bi(4x4)
        // On the stack, once per B_8x8 macroblock - see the P_8x8 case above.
        Scratch.Of4<int> subMbTypesStorage = default;
        Span<int> subMbTypes = subMbTypesStorage;
        for (int i = 0; i < 4; i++)
        {
            // FFmpeg's decode_cabac_b_mb_sub_type
            if (decoder.DecodeBin(36) == 0) { subMbTypes[i] = 0; continue; }
            if (decoder.DecodeBin(37) == 0) { subMbTypes[i] = 1 + decoder.DecodeBin(39); continue; }
            int type = 3;
            if (decoder.DecodeBin(38) != 0)
            {
                if (decoder.DecodeBin(39) != 0) { subMbTypes[i] = 11 + decoder.DecodeBin(39); continue; }
                type += 4; // type = 7
            }
            type += 2 * decoder.DecodeBin(39);
            type += decoder.DecodeBin(39);
            subMbTypes[i] = type;
        }
        
                
        if ((isDiagnosticMb || mbAddr == DiagDumpDetailMbAddr) && DiagnosticOutput != null) TraceCabac31(subMbTypes);
        
        [MethodImpl(MethodImplOptions.NoInlining)]
        
        void TraceCabac31(Span<int> subMbTypes)
        
        {
        
        DiagnosticOutput.WriteLine($"  B_8x8 subMbTypes: [{subMbTypes[0]},{subMbTypes[1]},{subMbTypes[2]},{subMbTypes[3]}] DirectSpatial={currentSliceHeader!.DirectSpatialMvPrediction}");
        
        }
        
        // H.264 spec 7.4.5: noSubMbPartSizeLessThan8x8Flag
        // B_Direct_8x8 (0) is 8x8 only if direct_8x8_inference_flag is set, B_L0/L1/Bi_8x8 (1-3) are 8x8, ≥4 is < 8x8
        bool direct8x8Inference = context.ActiveSps!.Direct8x8Inference;
        for (int i = 0; i < 4; i++)
        {
            if (subMbTypes[i] == 0 && !direct8x8Inference)
            {
                allSubPartitionsAreMinimum8x8 = false;
                break;
            }
            if (subMbTypes[i] > 3)
            {
                allSubPartitionsAreMinimum8x8 = false;
                break;
            }
        }
        
        // ref_idx L0 for all 4 sub-MBs (skip Direct and L1-only types)
        // Fill ref cache: Direct and non-L0 sub-MBs get 0, L0 sub-MBs get decoded ref
        for (int i = 0; i < 4; i++)
        {
            int st = subMbTypes[i];
            if (st != 0 && BSubMbUsesL0(st))
            {
                int refVal = DecodeRefIdxCabac(ref decoder, 4 * i, 0, numRefL0 - 1);
                FillRefCache(Scan8[4 * i], 2, 2, refVal);
                StoreRefIdx(mbAddr, i, 0, (sbyte)refVal);
            }
            else
            {
                FillRefCache(Scan8[4 * i], 2, 2, st == 0 ? 0 : -1);
                StoreRefIdx(mbAddr, i, 0, st == 0 ? (sbyte)0 : (sbyte)-1);
            }
        }
        // ref_idx L1 for all 4 sub-MBs (skip Direct and L0-only types)
        for (int i = 0; i < 4; i++)
        {
            int st = subMbTypes[i];
            if (st != 0 && BSubMbUsesL1(st))
            {
                int refVal = DecodeRefIdxCabac(ref decoder, 4 * i, 1, numRefL1 - 1);
                FillRefCacheL1(Scan8[4 * i], 2, 2, refVal);
                StoreRefIdx(mbAddr, i, 1, (sbyte)refVal);
            }
            else
            {
                FillRefCacheL1(Scan8[4 * i], 2, 2, st == 0 ? 0 : -1);
                StoreRefIdx(mbAddr, i, 1, st == 0 ? (sbyte)0 : (sbyte)-1);
            }
        }
        
        // Mark inner-boundary positions as unavailable (same as P_8x8 fix).
        // Prevents stale C neighbor reads in sub-8x8 PredMotion.
        refCacheL0[Scan8[4]] = PartNotAvailable;
        refCacheL0[Scan8[12]] = PartNotAvailable;
        refCacheL1[Scan8[4]] = PartNotAvailable;
        refCacheL1[Scan8[12]] = PartNotAvailable;
        mvCacheL0X[Scan8[4]] = mvCacheL0Y[Scan8[4]] = 0;
        mvCacheL0X[Scan8[12]] = mvCacheL0Y[Scan8[12]] = 0;
        mvCacheL1X[Scan8[4]] = mvCacheL1Y[Scan8[4]] = 0;
        mvCacheL1X[Scan8[12]] = mvCacheL1Y[Scan8[12]] = 0;
        
        // Derive motion vectors for B_Direct_8x8 sub-MBs before MVD decode.
        // Without this, Direct sub-MBs retain stale MV cache values from the previous MB,
        // corrupting PredMotion neighbor reads for subsequent non-Direct sub-MBs.
        bool hasDirectSubMb = subMbTypes[0] == 0 || subMbTypes[1] == 0 || subMbTypes[2] == 0 || subMbTypes[3] == 0;
        if (hasDirectSubMb)
        {
            if (currentSliceHeader!.DirectSpatialMvPrediction)
                DeriveBDirect8x8SpatialMotionVectors<TShape>(mbAddr, subMbTypes);
            else
                DeriveBDirect8x8TemporalMotionVectors<TShape>(mbAddr, subMbTypes);
            
            // Re-set PartNotAvailable at scan8[4] and scan8[12] AFTER direct MV derivation.
            // Direct derivation overwrites these positions with actual ref values, but they
            // must remain PART_NOT_AVAILABLE so PredMotion for non-direct sub-MBs correctly
            // falls back to the D (top-left) neighbor instead of using stale C (top-right).
            // Matches FFmpeg h264_cabac.c lines 2125-2128.
            refCacheL0[Scan8[4]] = PartNotAvailable;
            refCacheL0[Scan8[12]] = PartNotAvailable;
            refCacheL1[Scan8[4]] = PartNotAvailable;
            refCacheL1[Scan8[12]] = PartNotAvailable;
            
            // Mark Direct sub-MBs in directCache for CABAC context propagation
            for (int i = 0; i < 4; i++)
            {
                if (subMbTypes[i] == 0)
                    FillDirectCache(Scan8[4 * i], 2, 2, 1);
            }
        }
        
        // Read ref values from +1 positions (not +0, which is now PART_NOT_AVAILABLE for i=1,3)
        Scratch.Of4<int> refL0ValsStorage = default;
        Span<int> refL0Vals = refL0ValsStorage;
        Scratch.Of4<int> refL1ValsStorage = default;
        Span<int> refL1Vals = refL1ValsStorage;
        for (int i = 0; i < 4; i++)
        {
            refL0Vals[i] = refCacheL0[Scan8[4 * i] + 1];
            refL1Vals[i] = refCacheL1[Scan8[4 * i] + 1];
        }
        
        for (int i = 0; i < 4; i++)
        {
            // Restore correct L0 ref at +0 from +1 before MVD decode
            refCacheL0[Scan8[4 * i]] = refCacheL0[Scan8[4 * i] + 1];
            
            int st = subMbTypes[i];
            int baseBlock = 4 * i;
            if (st == 0)
            {
                // B_Direct_8x8: zero mvd cache for this sub-MB
                int pos = Scan8[baseBlock];
                FillMvdCache(pos, 2, 2, 0, 0);
                continue;
            }
            if (!BSubMbUsesL0(st))
            {
                int pos = Scan8[baseBlock];
                FillMvdCache(pos, 2, 2, 0, 0);
                FillMvCache(mvCacheL0X, mvCacheL0Y, pos, 2, 2, 0, 0);
                continue;
            }
            DecodeBSubMbMvdParts<TShape>(ref decoder, st, baseBlock, isL1: false, refL0Vals[i]);
        }
        // mvd L1 for all 4 sub-MBs with correct partition shapes
        for (int i = 0; i < 4; i++)
        {
            // Restore correct L1 ref at +0 from +1 before MVD decode
            refCacheL1[Scan8[4 * i]] = refCacheL1[Scan8[4 * i] + 1];
            int st = subMbTypes[i];
            int baseBlock = 4 * i;
            if (st == 0)
            {
                int pos = Scan8[baseBlock];
                FillMvdCacheL1(pos, 2, 2, 0, 0);
                continue;
            }
            if (!BSubMbUsesL1(st))
            {
                int pos = Scan8[baseBlock];
                FillMvdCacheL1(pos, 2, 2, 0, 0);
                FillMvCache(mvCacheL1X, mvCacheL1Y, pos, 2, 2, 0, 0);
                continue;
            }
            DecodeBSubMbMvdParts<TShape>(ref decoder, st, baseBlock, isL1: true, refL1Vals[i]);
        }
        
                
        if ((isDiagnosticMb || mbAddr == DiagDumpDetailMbAddr) && DiagnosticOutput != null) TraceCabac32();
        
        [MethodImpl(MethodImplOptions.NoInlining)]
        
        void TraceCabac32()
        
        {
        
                    DiagnosticOutput.WriteLine($"  B_8x8 after MVD: L1 cache at sub-MBs:");
        
                    for (int i = 0; i < 4; i++)
        
                    {
        
                        int p = Scan8[4*i];
        
                        DiagnosticOutput.WriteLine($"    sub{i}: pos={p} mv=({mvCacheL1X[p]},{mvCacheL1Y[p]})/({mvCacheL1X[p+1]},{mvCacheL1Y[p+1]})/({mvCacheL1X[p+8]},{mvCacheL1Y[p+8]})/({mvCacheL1X[p+9]},{mvCacheL1Y[p+9]}) ref=({refCacheL0[p]},{refCacheL1[p]})");
        
                    }
        
                }
        // NOTE: Direct sub-MB refs are NOT clamped. Actual derived refs (possibly > 0) are
        // preserved for PredMotion and spatial direct in subsequent MBs. CABAC context
        // suppression is handled by directCache (like ffmpeg's direct_cache).
    }
    
    /// <summary>
    /// Decode mvd for sub-partitions of a B_8x8 sub-macroblock based on its sub_mb_type.
    /// </summary>
    private void DecodeBSubMbMvdParts<TShape>(ref CabacDecoder decoder, int subMbType, int baseBlock, bool isL1, int refIdx)
        where TShape : struct, IPictureShape
    {
        int list = isL1 ? 1 : 0;
        // Partition shapes by sub_mb_type: 1-3=8x8(1), 4/6/8=8x4(2), 5/7/9=4x8(2), 10-12=4x4(4)
        if (subMbType <= 3)
        {
            // 8x8: partWidth=2
            if (isL1) DecodeMvdPairCabacL1<TShape>(ref decoder, baseBlock, 2, 2, 2, refIdx);
            else DecodeMvdPairCabac<TShape>(ref decoder, baseBlock, 2, 2, 2, refIdx);
        }
        else if (subMbType <= 9)
        {
            bool is8x4 = (subMbType == 4 || subMbType == 6 || subMbType == 8);
            if (is8x4)
            {
                // 8x4: partWidth=2
                if (isL1)
                {
                    DecodeMvdPairCabacL1<TShape>(ref decoder, baseBlock, 2, 1, 2, refIdx);
                    DecodeMvdPairCabacL1<TShape>(ref decoder, baseBlock + 2, 2, 1, 2, refIdx);
                }
                else
                {
                    DecodeMvdPairCabac<TShape>(ref decoder, baseBlock, 2, 1, 2, refIdx);
                    DecodeMvdPairCabac<TShape>(ref decoder, baseBlock + 2, 2, 1, 2, refIdx);
                }
            }
            else
            {
                // 4x8: partWidth=1
                if (isL1)
                {
                    DecodeMvdPairCabacL1<TShape>(ref decoder, baseBlock, 1, 2, 1, refIdx);
                    DecodeMvdPairCabacL1<TShape>(ref decoder, baseBlock + 1, 1, 2, 1, refIdx);
                }
                else
                {
                    DecodeMvdPairCabac<TShape>(ref decoder, baseBlock, 1, 2, 1, refIdx);
                    DecodeMvdPairCabac<TShape>(ref decoder, baseBlock + 1, 1, 2, 1, refIdx);
                }
            }
        }
        else
        {
            // 4x4: partWidth=1
            for (int j = 0; j < 4; j++)
            {
                if (isL1) DecodeMvdPairCabacL1<TShape>(ref decoder, baseBlock + j, 1, 1, 1, refIdx);
                else DecodeMvdPairCabac<TShape>(ref decoder, baseBlock + j, 1, 1, 1, refIdx);
            }
        }
    }
    
    /// <summary>Returns true if B sub_mb_type uses L0 prediction (L0 or BiPred).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool BSubMbUsesL0(int subMbType)
    {
        // L0: 1, Bi: 3, L0_8x4: 4, L0_4x8: 5, Bi_8x4: 8, Bi_4x8: 9, L0_4x4: 10, Bi_4x4: 12
        return subMbType != 2 && subMbType != 6 && subMbType != 7 && subMbType != 11;
    }
    
    /// <summary>Returns true if B sub_mb_type uses L1 prediction (L1 or BiPred).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool BSubMbUsesL1(int subMbType)
    {
        // L1: 2, Bi: 3, L1_8x4: 6, L1_4x8: 7, Bi_8x4: 8, Bi_4x8: 9, L1_4x4: 11, Bi_4x4: 12
        return subMbType != 1 && subMbType != 4 && subMbType != 5 && subMbType != 10;
    }
    
    /// <summary>
    /// Decode ref_idx using CABAC full unary binarization with scan8-based neighbor context.
    /// Returns the decoded reference index (0 if maxVal=0).
    /// FFmpeg decode_cabac_mb_ref: uses full unary (no maxVal bound on while loop)
    /// because x264 always sends a trailing 0-bin even for ref=maxVal.
    /// ctx = (left_ref>0 && !direct) + 2*(top_ref>0 && !direct)
    /// </summary>
    private int DecodeRefIdxCabac(ref CabacDecoder decoder, int n, int list, int maxVal)
    {
        if (maxVal == 0) return 0;
        
        // Derive bin 0 context from left and top neighbor ref values in scan8 layout.
        // H.264 spec 9.3.3.1.1.7: condTermFlagN = (refIdxN > 0) && !isDirectOrSkipN
        // directCache marks Direct blocks (B_Skip, B_Direct_16x16, B_Direct_8x8 sub-MBs).
        // Like ffmpeg's direct_cache: suppresses condTermFlag for Direct neighbors while
        // preserving actual ref values for PredMotion and spatial direct derivation.
        int pos = Scan8[n];
        int[] refCache = (list == 0) ? refCacheL0 : refCacheL1;
        int refa = refCache[pos - 1];
        int refb = refCache[pos - 8];
        
        int ctxA = (refa > 0 && directCache[pos - 1] == 0) ? 1 : 0;
        int ctxB = (refb > 0 && directCache[pos - 8] == 0) ? 2 : 0;
        int ctx = ctxA + ctxB;
        
        // Full unary: read bins until 0-bin (matches FFmpeg/x264 behavior)
        int bin0 = decoder.DecodeBin(54 + ctx);
        if (bin0 == 0) return 0;
        
        // bin 1+: ctx transitions 54+(ctx>>2)+4. For ctx 0-3, bin 1 uses ctx 58, bin 2+ uses ctx 59.
        int refIdx = 1;
        ctx = 4; // (0..3 >> 2) + 4 = 4
        while (decoder.DecodeBin(54 + ctx) != 0)
        {
            refIdx++;
            ctx = 5; // (4 >> 2) + 4 = 5, stays at 5
            if (refIdx >= 32) return 32; // FFmpeg: overflow guard
        }
        return refIdx;
    }
    
    /// <summary>
    /// Decode motion vector difference component.
    /// Returns absolute value for CABAC context tracking; outputs signed value via signedMvd.
    /// ctxIdxOffset: 40 for horizontal, 47 for vertical.
    /// amvd: sum of absolute mvds from left and top neighbors (determines bin 0 context).
    /// </summary>
    private int DecodeMvdCabac(ref CabacDecoder decoder, int ctxIdxOffset, int amvd, out int signedMvd)
    {
        bool binTrace = diagMvdBinTrace && DiagnosticOutput != null;
        
        // Bin 0 context: ctxIdxOffset + {0,1,2} based on amvd magnitude (FFmpeg amvd_index)
        int ctxIdxInc = (amvd < 3) ? 0 : (amvd < 33) ? 1 : 2;
        
        if (binTrace)
            DiagnosticOutput!.Write($"      MVD-BIN base={ctxIdxOffset} amvd={amvd} ctxInc={ctxIdxInc} | bin0@{ctxIdxOffset+ctxIdxInc} {decoder.GetContextState(ctxIdxOffset+ctxIdxInc)} {decoder.DiagState} →");
        
        int bin0 = decoder.DecodeBin(ctxIdxOffset + ctxIdxInc);
        
        if (binTrace)
            DiagnosticOutput!.Write($" {bin0}");
        
        if (bin0 == 0) { signedMvd = 0; if (binTrace) DiagnosticOutput!.WriteLine(" → abs=0"); return 0; }
        
        // Prefix: unary coded bins 1-8 with incrementing contexts
        int absMvd = 1;
        int mvdCtx = ctxIdxOffset + 3;
        while (absMvd < 9)
        {
            if (binTrace)
                DiagnosticOutput!.Write($" | @{mvdCtx} {decoder.GetContextState(mvdCtx)} →");
            
            int bin = decoder.DecodeBin(mvdCtx);
            
            if (binTrace)
                DiagnosticOutput!.Write($" {bin}");
            
            if (bin == 0) break;
            if (absMvd < 4) mvdCtx++;
            absMvd++;
        }
        
        if (absMvd >= 9)
        {
            // UEG3 suffix: exponential-golomb with k=3
            int k = 3;
            while (decoder.DecodeBypass() != 0)
            {
                absMvd += 1 << k;
                k++;
                if (k > 24) break;
            }
            while (k-- > 0)
            {
                int bit = decoder.DecodeBypass();
                if (bit != 0)
                    absMvd += 1 << k;
            }
        }
        
        // Sign bit
        int sign = decoder.DecodeBypass();
        signedMvd = sign != 0 ? -absMvd : absMvd;
        
        if (binTrace)
            DiagnosticOutput!.WriteLine($" | sign={sign} → abs={absMvd} signed={signedMvd}");
        
        // Clamp for context storage (FFmpeg: mvd < 70 ? mvd : 70)
        return absMvd < 70 ? absMvd : 70;
    }

    private void DecodeCbpCabac<TShape>(ref CabacDecoder decoder, ref Macroblock mb, bool isIntra)
        where TShape : struct, IPictureShape
    {
        // Decode coded_block_pattern
        // Luma: 4 bits for 4 8x8 luma blocks (contexts 73-76)
        // Chroma: 2 bits (contexts 77-84)
        
        cbpLumaPartial = 0;
        bool cbpTrace = DiagnosticOutput != null && DiagCabacTracePoc >= 0 && currentPoc == DiagCabacTracePoc
            && mb.PositionX >= 14 && mb.PositionX <= 18 && mb.PositionY <= 3;
        for (int i = 0; i < 4; i++)
        {
            int ctxInc = GetCbpLumaContext<TShape>(i, mb.PositionX, mb.PositionY, context.WidthInMacroblocks);
            int ctxIdx = 73 + ctxInc;
                        if (cbpTrace) TraceCabac33(ref mb, ref decoder);
            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCabac33(ref Macroblock mb, ref CabacDecoder decoder)
            {
                            int mbAddr2 = mb.PositionY * context.WidthInMacroblocks + mb.PositionX;
                            int topAddr2 = TShape.Mbaff ? mbaffTopXY : mbAddr2 - context.WidthInMacroblocks;
                            DiagnosticOutput!.WriteLine($"    [CBP] MB({mb.PositionX},{mb.PositionY}) blk{i}: ctxInc={ctxInc} ctx={ctxIdx} " +
                                $"LTOP={mbaffLeftXYTop} LBOT={mbaffLeftXYBot} topA={topAddr2} lbIdx={mbaffLeftBlockIdx} " +
                                $"cbpL_ltop={(mbaffLeftXYTop >= 0 && mbaffLeftXYTop < cabacCbpLuma!.Length ? cabacCbpLuma[mbaffLeftXYTop] : -1)} " +
                                $"cbpL_lbot={(mbaffLeftXYBot >= 0 && mbaffLeftXYBot < cabacCbpLuma!.Length ? cabacCbpLuma[mbaffLeftXYBot] : -1)} " +
                                $"cbpL_top={(topAddr2 >= 0 && topAddr2 < cabacCbpLuma!.Length ? cabacCbpLuma[topAddr2] : -1)} " +
                                $"pre:{decoder.DiagState}");
                        }
            int bin = decoder.DecodeBin(ctxIdx);
            if (bin != 0)
            {
                cbpLumaPartial |= (1 << i);
            }
                        if (cbpTrace) TraceCabac34(ref decoder);
            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCabac34(ref CabacDecoder decoder)
            {
            DiagnosticOutput!.WriteLine($"      bin{i}={bin} partial=0x{cbpLumaPartial:X} post:{decoder.DiagState}");
            }
        }
        mb.CodedBlockPatternLuma = cbpLumaPartial;
        
        // Decode chroma CBP (Table 9-34, spec 9.3.3.1.1.3)
        // For monochrome (ChromaArrayType == 0), chroma CBP bins are not present
        int cbpChroma = 0;
        if (context.ActiveSps!.ChromaFormat != ChromaFormat.Monochrome)
        {
            // bin 0 (non-zero chroma DC): ctxIdx = 77 + ctxIdxInc (0-3)
            // bin 1 (non-zero chroma AC): ctxIdx = 81 + ctxIdxInc (0-3)
            // FFmpeg: uses sl->left_cbp and sl->top_cbp which are MBAFF-aware
            int mbAddr = mb.PositionY * context.WidthInMacroblocks + mb.PositionX;
            int leftAddr = TShape.Mbaff ? mbaffLeftXYTop : mbAddr - 1;
            int topAddr = TShape.Mbaff ? mbaffTopXY : mbAddr - context.WidthInMacroblocks;
            
            int bin0CondA = 0, bin0CondB = 0;
            int bin1CondA = 0, bin1CondB = 0;
            
            if (mb.PositionX > 0 && cabacCbpChroma != null && leftAddr >= 0 && leftAddr < mbSliceIndex!.Length && mbSliceIndex[leftAddr] == currentSliceIdx)
            {
                int leftChroma = cabacCbpChroma[leftAddr];
                bin0CondA = (leftChroma > 0) ? 1 : 0;
                bin1CondA = (leftChroma == 2) ? 1 : 0;
            }
            if (topAddr >= 0 && topAddr < mbSliceIndex!.Length && cabacCbpChroma != null && mbSliceIndex[topAddr] == currentSliceIdx)
            {
                int topChroma = cabacCbpChroma[topAddr];
                bin0CondB = (topChroma > 0) ? 1 : 0;
                bin1CondB = (topChroma == 2) ? 1 : 0;
            }
            
            if (decoder.DecodeBin(77 + bin0CondA + 2 * bin0CondB) != 0)
            {
                cbpChroma = 1;
                if (decoder.DecodeBin(81 + bin1CondA + 2 * bin1CondB) != 0)
                {
                    cbpChroma = 2;
                }
            }
        }
        mb.CodedBlockPatternChroma = cbpChroma;
    }

    private int GetCbpLumaContext<TShape>(int blkIdx, int mbX, int mbY, int mbWidth)
        where TShape : struct, IPictureShape
    {
        // H.264 spec 9.3.3.1.1.3: coded_block_pattern luma context derivation
        // 8x8 block layout: 0|1 / 2|3
        // condTermFlagA/B:
        //   0 if neighbor not available, OR neighbor is I_PCM, OR neighbor CBP luma bit is set (coded)
        //   1 if neighbor available AND neighbor CBP luma bit is not set (not coded)
        // Note: I16x16 stores cbpLuma as 0 or 15 via mb_type, so bit check works directly.
        int condTermA, condTermB;
        int mbAddr = mbY * mbWidth + mbX;
        int topAddr = TShape.Mbaff ? mbaffTopXY : mbAddr - mbWidth;
        
        switch (blkIdx)
        {
            case 0:
            {
                // Left = left MB's right-top 8x8 block, Top = top MB's block 2
                if (mbX > 0 && cabacCbpLuma != null)
                {
                    if (TShape.Mbaff)
                    {
                        // MBAFF: use LTOP with left_block remapping
                        // FFmpeg: (cbp_table[LTOP] >> (left_block[0] & ~1)) & 2
                        int ltAddr = mbaffLeftXYTop;
                        if (ltAddr >= 0 && ltAddr < mbSliceIndex!.Length && mbSliceIndex[ltAddr] == currentSliceIdx)
                        {
                            int[] lb = LeftBlockOptions[mbaffLeftBlockIdx];
                            condTermA = ((cabacCbpLuma[ltAddr] >> (lb[0] & ~1)) & 2) != 0 ? 0 : 1;
                        }
                        else condTermA = 0;
                    }
                    else
                    {
                        condTermA = (mbSliceIndex![mbAddr - 1] == currentSliceIdx)
                            ? (((cabacCbpLuma[mbAddr - 1] & 2) != 0) ? 0 : 1)
                            : 0;
                    }
                }
                else
                    condTermA = 0;
                
                if (topAddr >= 0 && topAddr < mbSliceIndex!.Length && cabacCbpLuma != null && mbSliceIndex[topAddr] == currentSliceIdx)
                    condTermB = ((cabacCbpLuma[topAddr] & 4) != 0) ? 0 : 1;
                else
                    condTermB = 0;
                break;
            }
            
            case 1:
            {
                // Left = this MB's block 0 (just decoded), Top = top MB's block 3
                condTermA = ((cbpLumaPartial & 1) != 0) ? 0 : 1;
                
                if (topAddr >= 0 && topAddr < mbSliceIndex!.Length && cabacCbpLuma != null && mbSliceIndex[topAddr] == currentSliceIdx)
                    condTermB = ((cabacCbpLuma[topAddr] & 8) != 0) ? 0 : 1;
                else
                    condTermB = 0;
                break;
            }
            
            case 2:
            {
                // Left = left MB's right-bottom 8x8 block, Top = this MB's block 0
                if (mbX > 0 && cabacCbpLuma != null)
                {
                    if (TShape.Mbaff)
                    {
                        // MBAFF: use LBOT with left_block remapping
                        // FFmpeg: ((cbp_table[LBOT] >> (left_block[2] & ~1)) & 2)
                        int lbAddr = mbaffLeftXYBot;
                        if (lbAddr >= 0 && lbAddr < mbSliceIndex!.Length && mbSliceIndex[lbAddr] == currentSliceIdx)
                        {
                            int[] lb = LeftBlockOptions[mbaffLeftBlockIdx];
                            condTermA = ((cabacCbpLuma[lbAddr] >> (lb[2] & ~1)) & 2) != 0 ? 0 : 1;
                        }
                        else condTermA = 0;
                    }
                    else
                    {
                        condTermA = (mbSliceIndex![mbAddr - 1] == currentSliceIdx)
                            ? (((cabacCbpLuma[mbAddr - 1] & 8) != 0) ? 0 : 1)
                            : 0;
                    }
                }
                else
                    condTermA = 0;
                
                condTermB = ((cbpLumaPartial & 1) != 0) ? 0 : 1;
                break;
            }
            
            case 3:
            default:
                // Left = this MB's block 2, Top = this MB's block 1
                condTermA = ((cbpLumaPartial & 4) != 0) ? 0 : 1;
                condTermB = ((cbpLumaPartial & 2) != 0) ? 0 : 1;
                break;
        }
        
        return condTermA + 2 * condTermB;
    }

    private int DecodeMbQpDeltaCabac(ref CabacDecoder decoder)
    {
        // H.264 spec 9.3.2.7: mb_qp_delta binarization
        // Unary coded with sign embedded in parity (NO separate bypass sign bin).
        // v=0→delta=0, v=1→+1, v=2→-1, v=3→+2, v=4→-2, ...
        // bin 0: ctx = 60 + ctxIdxInc (0 if prev delta=0, 1 if prev delta!=0)
        // bin 1: ctx = 62
        // bins ≥ 2: ctx = 63
        int bin0CtxInc = (previousMbQpDelta != 0) ? 1 : 0;
        int bin0 = decoder.DecodeBin(60 + bin0CtxInc);
        if (bin0 == 0)
        {
            previousMbQpDelta = 0;
            return 0;
        }
        
        // Decode unary value: count consecutive 1-bins, terminated by 0
        int unaryValue = 1;
        while (decoder.DecodeBin(unaryValue == 1 ? 62 : 63) != 0)
        {
            unaryValue++;
            if (unaryValue > 102) // 2*51 max possible
                break;
        }
        
        // Map unary value to signed delta (sign encoded in parity, not bypass bin)
        // Odd → positive: (v+1)/2, Even → negative: -(v+1)/2
        int delta;
        if ((unaryValue & 1) != 0)
            delta = (unaryValue + 1) >> 1;
        else
            delta = -((unaryValue + 1) >> 1);
        
        previousMbQpDelta = delta;
        return delta;
    }

    /// <summary>
    /// Compute coded_block_flag ctxIdxInc for I16x16 luma DC (category 0).
    /// Checks whether the left/top neighbor's luma DC had nonzero coefficients.
    /// FFmpeg equivalent: get_cabac_cbf_ctx with is_dc=1 and cat=0.
    /// </summary>
    private int GetLumaDcCbfCtxIdxInc<TShape>(int mbAddr, int mbWidth)
        where TShape : struct, IPictureShape
    {
        int mbX = mbAddr % mbWidth;
        int leftAddr = TShape.Mbaff ? mbaffLeftXYTop : mbAddr - 1;
        int topAddr = TShape.Mbaff ? mbaffTopXY : mbAddr - mbWidth;
        
        int condTermA;
        if (mbX > 0 && leftAddr >= 0 && leftAddr < mbSliceIndex!.Length && mbSliceIndex[leftAddr] == currentSliceIdx)
            condTermA = cabacLumaDcCoded![leftAddr];
        else
            condTermA = 1; // I16x16 is always intra, unavailable default = 1
        
        int condTermB;
        if (topAddr >= 0 && topAddr < mbSliceIndex!.Length && mbSliceIndex[topAddr] == currentSliceIdx)
            condTermB = cabacLumaDcCoded![topAddr];
        else
            condTermB = 1;
        
        return condTermA + 2 * condTermB;
    }

    /// <summary>
    /// Compute coded_block_flag ctxIdxInc for luma 4x4 blocks (categories 1 and 2).
    /// Uses actual neighbor CBF values when available.
    /// Block indices use H.264 z-scan order (not raster): within each 8x8 block,
    /// 4x4 sub-blocks are z-scanned, and 8x8 blocks are z-scanned within the MB.
    /// </summary>
    private int GetLumaCbfCtxIdxInc<TShape>(int blk4x4Idx, int mbAddr, int mbWidth, bool isIntra)
        where TShape : struct, IPictureShape
    {
        // Convert z-scan block index to (x, y) in 4x4-block units (0-3 each)
        int blkX = ((blk4x4Idx >> 2) & 1) * 2 + (blk4x4Idx & 1);
        int blkY = (blk4x4Idx >> 3) * 2 + ((blk4x4Idx >> 1) & 1);
        int mbX = mbAddr % mbWidth;
        
        // condTermFlagA: left neighbor's CBF
        int condTermA;
        if (blkX > 0)
        {
            int leftBlkIdx = Luma4x4BlkIdx(blkX - 1, blkY);
            condTermA = (cabacLumaCbf![mbAddr] >> leftBlkIdx) & 1;
        }
        else if (mbX > 0)
        {
            if (TShape.Mbaff)
            {
                int leftAddr = blkY < 2 ? mbaffLeftXYTop : mbaffLeftXYBot;
                if (leftAddr >= 0 && leftAddr < mbSliceIndex!.Length && mbSliceIndex[leftAddr] == currentSliceIdx)
                {
                    int[] lb = LeftBlockOptions[mbaffLeftBlockIdx];
                    int remappedY = lb[blkY];
                    int leftBlkIdx = Luma4x4BlkIdx(3, remappedY);
                    condTermA = (cabacLumaCbf![leftAddr] >> leftBlkIdx) & 1;
                }
                else
                    condTermA = isIntra ? 1 : 0;
            }
            else if (mbSliceIndex![mbAddr - 1] == currentSliceIdx)
            {
                int leftBlkIdx = Luma4x4BlkIdx(3, blkY);
                condTermA = (cabacLumaCbf![mbAddr - 1] >> leftBlkIdx) & 1;
            }
            else
                condTermA = isIntra ? 1 : 0;
        }
        else
        {
            condTermA = isIntra ? 1 : 0;
        }
        
        // condTermFlagB: top neighbor's CBF
        int condTermB;
        if (blkY > 0)
        {
            int topBlkIdx = Luma4x4BlkIdx(blkX, blkY - 1);
            condTermB = (cabacLumaCbf![mbAddr] >> topBlkIdx) & 1;
        }
        else
        {
            int topAddr = TShape.Mbaff ? mbaffTopXY : mbAddr - mbWidth;
            if (topAddr >= 0 && topAddr < mbSliceIndex!.Length && mbSliceIndex![topAddr] == currentSliceIdx)
            {
                int topBlkIdx = Luma4x4BlkIdx(blkX, 3);
                condTermB = (cabacLumaCbf![topAddr] >> topBlkIdx) & 1;
            }
            else
            {
                condTermB = isIntra ? 1 : 0;
            }
        }
        
        return condTermA + 2 * condTermB;
    }

    /// <summary>
    /// Convert (x, y) in 4x4-block units to H.264 z-scan block index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Luma4x4BlkIdx(int x, int y)
    {
        int i8x8 = (y >> 1) * 2 + (x >> 1);
        int i4x4 = (y & 1) * 2 + (x & 1);
        return i8x8 * 4 + i4x4;
    }
    
    /// <summary>
    /// Compute coded_block_flag ctxIdxInc for chroma blocks (categories 3 and 4).
    /// </summary>
    private int GetChromaCbfCtxIdxInc<TShape>(int ctxBlockCat, int chromaBlkIdx, int mbAddr, int mbWidth, bool isIntra = true)
        where TShape : struct, IPictureShape
    {
        int mbX = mbAddr % mbWidth;
        
        if (ctxBlockCat == 3)
        {
            // Chroma DC: one block per plane, neighbor is left/top MB's chroma DC
            // DC bits stored at position 2*chromaBlocksPerComponent + chromaBlkIdx
            int bitShift = 2 * chromaBlocksPerComponent + chromaBlkIdx;
            int dcDefault = isIntra ? 1 : 0;
            int leftAddr = TShape.Mbaff ? mbaffLeftXYTop : mbAddr - 1;
            int topAddr = TShape.Mbaff ? mbaffTopXY : mbAddr - mbWidth;
            
            int condTermA;
            if (mbX > 0 && leftAddr >= 0 && leftAddr < mbSliceIndex!.Length && mbSliceIndex[leftAddr] == currentSliceIdx)
                condTermA = (cabacChromaCbf![leftAddr] >> bitShift) & 1;
            else
                condTermA = dcDefault;
            
            int condTermB;
            if (topAddr >= 0 && topAddr < mbSliceIndex!.Length && mbSliceIndex[topAddr] == currentSliceIdx)
                condTermB = (cabacChromaCbf![topAddr] >> bitShift) & 1;
            else
                condTermB = dcDefault;
            
            return condTermA + 2 * condTermB;
        }
        else  // Cat 4: Chroma AC
        {
            // Blocks per plane in 2×N grid (N=2 for 4:2:0, N=4 for 4:2:2)
            int plane = chromaBlkIdx / chromaBlocksPerComponent;  // 0=Cb, 1=Cr
            int blkInPlane = chromaBlkIdx % chromaBlocksPerComponent;
            int blkX = blkInPlane % 2;
            int blkY = blkInPlane / 2;
            
            int condTermA;
            if (blkX > 0)
            {
                condTermA = (cabacChromaCbf![mbAddr] >> (plane * chromaBlocksPerComponent + blkInPlane - 1)) & 1;
            }
            else if (mbX > 0)
            {
                // Left border: need MBAFF-aware address and chroma row remapping
                int leftAddr;
                int remappedBlkY;
                if (TShape.Mbaff)
                {
                    // For 4:2:0: 2 block rows, split at row 1 (blkY<1 → LTOP)
                    // For 4:2:2: 4 block rows, split at row 2 (blkY<2 → LTOP)
                    int chromaRowSplit = chromaBlocksPerComponent / 4;
                    leftAddr = blkY < chromaRowSplit ? mbaffLeftXYTop : mbaffLeftXYBot;
                    var chromaLbo = isChroma422 ? ChromaLeftBlockOptions422 : ChromaLeftBlockOptions420;
                    remappedBlkY = chromaLbo[mbaffLeftBlockIdx][blkY];
                }
                else
                {
                    leftAddr = mbAddr - 1;
                    remappedBlkY = blkY;
                }
                if (leftAddr >= 0 && leftAddr < mbSliceIndex!.Length && mbSliceIndex[leftAddr] == currentSliceIdx)
                    condTermA = (cabacChromaCbf![leftAddr] >> (plane * chromaBlocksPerComponent + 1 + remappedBlkY * 2)) & 1;
                else
                    condTermA = isIntra ? 1 : 0;
            }
            else
            {
                condTermA = isIntra ? 1 : 0;
            }
            
            int condTermB;
            if (blkY > 0)
            {
                // Top neighbor within same MB: same column, one row up = blkInPlane - 2
                condTermB = (cabacChromaCbf![mbAddr] >> (plane * chromaBlocksPerComponent + blkInPlane - 2)) & 1;
            }
            else
            {
                int topAddr = TShape.Mbaff ? mbaffTopXY : mbAddr - mbWidth;
                if (topAddr >= 0 && topAddr < mbSliceIndex!.Length && mbSliceIndex![topAddr] == currentSliceIdx)
                    condTermB = (cabacChromaCbf![topAddr] >> (plane * chromaBlocksPerComponent + (chromaBlocksPerComponent - 2) + blkX)) & 1;
                else
                    condTermB = isIntra ? 1 : 0;
            }
            
            return condTermA + 2 * condTermB;
        }
    }

    private void DecodeResidualCabac<TShape>(ref CabacDecoder decoder, ref Macroblock mb, bool isIntra16x16, bool fieldMode, Span<short> residualLuma, Span<short> residualChroma)
        where TShape : struct, IPictureShape
    {
        int mbAddr = mb.PositionY * context.WidthInMacroblocks + mb.PositionX;
        int mbWidth = context.WidthInMacroblocks;
        
        if (isIntra16x16)
        {
            // Decode luma DC block (16 coefficients) with coded_block_flag.
            // CBF context uses DC-specific neighbor tracking (left/top MB's luma DC coded flag).
            int dcCbfCtxInc = GetLumaDcCbfCtxIdxInc<TShape>(mbAddr, mbWidth);
            int dcCbf = DecodeBlockCabac(ref decoder, residualLuma.Slice(0, 16), 0, 16, ctxBlockCat: 0, cbfCtxIdxInc: dcCbfCtxInc, fieldMode);
            if (dcCbf != 0)
                cabacLumaDcCoded![mbAddr] = 1;
            
            // Diagnostic: print DC coefficients for early MBs
                        if (DiagnosticOutput != null && mbAddr <= 3) TraceCabac35(residualLuma);
            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceCabac35(Span<short> residualLuma)
            {
                            var sb = new System.Text.StringBuilder($"[DC_COEFF] addr={mbAddr} cbf={dcCbf} DC=[");
                            for (int i = 0; i < 16; i++)
                                sb.Append($"{residualLuma[i],4}");
                            sb.Append(']');
                            DiagnosticOutput.WriteLine(sb.ToString());
                        }
            
            // Redistribute DC coefficients from [0-15] to positions [0, 16, 32, 48...]
            for (int i = 15; i >= 1; i--)
            {
                residualLuma[i * 16] = residualLuma[i];
                residualLuma[i] = 0;
            }
            
            // Decode 16 4x4 AC blocks (15 coefficients each, skipping DC)
            // AC blocks DO have coded_block_flag
            if (mb.CodedBlockPatternLuma > 0)
            {
                // The alternating-current coefficients are dequantised at the store, against the
                // intra list - an intra 16x16 macroblock is intra by definition. The direct
                // current block above is not: it goes through its inverse Hadamard transform
                // first and is dequantised after it (8.5.12.2), which the reconstruct side still
                // does. Positions here start at raster position 1, and the multiplier is indexed
                // by that position and not by the offset into the span.
                ReadOnlySpan<byte> intra16AcMatrix = pixelShift == 0
                    ? context.ActivePps!.ScalingMatrix4[0]
                    : default;

                for (int i = 0; i < 16; i++)
                {
                    int offset = i * 16 + 1;  // Skip DC position
                    int cbfCtxInc = GetLumaCbfCtxIdxInc<TShape>(i, mbAddr, mbWidth, mb.IsIntra);
                    int cbf = DecodeBlockCabac(ref decoder, residualLuma.Slice(offset, 15), 1, 15, ctxBlockCat: 1, cbfCtxIdxInc: cbfCtxInc, fieldMode,
                        dequantMatrix: intra16AcMatrix, qp: mb.QuantizationParameter);
                    if (cbf != 0)
                    {
                        cabacLumaCbf![mbAddr] |= (1 << i);
                        unsafe { mb.NonZeroCountLuma[i] = 1; }
                    }
                }
            }
        }
        else
        {
            if (mb.Transform8x8)
            {
                // Dequantised at the store, against list 0 for an intra macroblock and list 3
                // for an inter one, which is the choice the two reconstruct paths make. Above
                // eight bits the levels are carried instead - see the 4x4 branch below.
                ReadOnlySpan<byte> luma8x8Matrix = pixelShift == 0
                    ? context.ActivePps!.ScalingMatrix8[mb.IsIntra ? 0 : 3]
                    : default;

                // 8x8 transform: decode 4 blocks of 64 coefficients each (ctxBlockCat=5)
                for (int i8x8 = 0; i8x8 < 4; i8x8++)
                {
                    if ((mb.CodedBlockPatternLuma & (1 << i8x8)) == 0)
                        continue;

                    int offset = i8x8 * 64;
                    DecodeBlock8x8Cabac(ref decoder, residualLuma.Slice(offset, 64), fieldMode,
                        dequantMatrix: luma8x8Matrix, qp: mb.QuantizationParameter);
                    
                    // Mark all4 sub-blocks as having non-zero coefficients
                    for (int i4x4 = 0; i4x4 < 4; i4x4++)
                    {
                        cabacLumaCbf![mbAddr] |= (1 << (i8x8 * 4 + i4x4));
                        unsafe { mb.NonZeroCountLuma[i8x8 * 4 + i4x4] = 1; }
                    }
                }
            }
            else
            {
                // Dequantise at the store below rather than on the reconstruct side. Intra 4x4
                // macroblocks scale against list 0 and inter ones against list 3, which is the
                // same choice ReconstructIntra4x4Luma and the inter path make - a block of this
                // category only ever belongs to one of those two, because an intra 16x16
                // macroblock codes its AC as category 1 and an 8x8 transform as category 5.
                //
                // Above eight bits the result does not fit this buffer, so those streams keep
                // parsing levels and dequantising into the wide block the reconstruct side
                // already has for them.
                ReadOnlySpan<byte> luma4x4Matrix = pixelShift == 0
                    ? context.ActivePps!.ScalingMatrix4[mb.IsIntra ? 0 : 3]
                    : default;

                // 4x4 transform: decode 16 blocks of 16 coefficients each (ctxBlockCat=2)
                for (int i8x8 = 0; i8x8 < 4; i8x8++)
                {
                    if ((mb.CodedBlockPatternLuma & (1 << i8x8)) == 0)
                        continue;
                    
                    for (int i4x4 = 0; i4x4 < 4; i4x4++)
                    {
                        int blkIdx = i8x8 * 4 + i4x4;
                        int offset = blkIdx * 16;
                        int cbfCtxInc = GetLumaCbfCtxIdxInc<TShape>(blkIdx, mbAddr, mbWidth, mb.IsIntra);
                                                if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac36(ref decoder);
                        [MethodImpl(MethodImplOptions.NoInlining)]
                        void TraceCabac36(ref CabacDecoder decoder)
                        {
                                                    DiagnosticOutput.WriteLine($"  CABAC-RES blk{blkIdx}: cbfCtxInc={cbfCtxInc} state={decoder.DiagState}");
                                                    if (blkIdx >= 10 && blkIdx <= 12)
                                                        DiagnosticOutput.WriteLine($"    {decoder.DumpBytes()}");
                                                }
                        int cbf = DecodeBlockCabac(ref decoder, residualLuma.Slice(offset, 16), 0, 16, ctxBlockCat: 2, cbfCtxIdxInc: cbfCtxInc, fieldMode,
                            dequantMatrix: luma4x4Matrix, qp: mb.QuantizationParameter);
                                                if (isDiagnosticMb && DiagnosticOutput != null) TraceCabac37(residualLuma);
                        [MethodImpl(MethodImplOptions.NoInlining)]
                        void TraceCabac37(Span<short> residualLuma)
                        {
                        DiagnosticOutput.WriteLine($"  CABAC-RES blk{blkIdx}: cbf={cbf} coeffs=[{string.Join(",", residualLuma.Slice(offset, 16).ToArray())}]");
                        }
                        if (cbf != 0)
                        {
                            cabacLumaCbf![mbAddr] |= (1 << blkIdx);
                            unsafe { mb.NonZeroCountLuma[blkIdx] = 1; }
                        }
                    }
                }
            }
        }
        
        // Decode chroma
        if (mb.CodedBlockPatternChroma > 0)
        {
            // Chroma DC blocks: 4 coefficients (4:2:0) or 8 coefficients (4:2:2)
            int cbDcCtx = GetChromaCbfCtxIdxInc<TShape>(3, 0, mbAddr, mbWidth, mb.IsIntra);
            int cbDcCbf = DecodeBlockCabac(ref decoder, residualChroma.Slice(chromaCbDcBase, chromaDcCoeffs),
                0, chromaDcCoeffs, ctxBlockCat: 3, cbfCtxIdxInc: cbDcCtx, fieldMode, chroma422Dc: isChroma422);
            if (cbDcCbf != 0)
                cabacChromaCbf![mbAddr] |= (1 << (2 * chromaBlocksPerComponent));
            
            int crDcCtx = GetChromaCbfCtxIdxInc<TShape>(3, 1, mbAddr, mbWidth, mb.IsIntra);
            int crDcCbf = DecodeBlockCabac(ref decoder, residualChroma.Slice(chromaCrDcBase, chromaDcCoeffs),
                0, chromaDcCoeffs, ctxBlockCat: 3, cbfCtxIdxInc: crDcCtx, fieldMode, chroma422Dc: isChroma422);
            if (crDcCbf != 0)
                cabacChromaCbf![mbAddr] |= (1 << (2 * chromaBlocksPerComponent + 1));
            
            if (mb.CodedBlockPatternChroma >= 2)
            {
                // Chroma AC blocks: 4 per component (4:2:0) or 8 per component (4:2:2)
                // For 4:2:2, iterate as 2 groups of 4 blocks (i8x8=0..1, i=0..3)
                // FFmpeg block indices: 16+16*c+8*i8x8+i for CABAC neighbor tracking
                // Chroma is dequantised at the store like luma, but against its own quantiser:
                // the luma one plus this component's offset, mapped through Table 8-15. The
                // three reconstruct paths that read these blocks - intra, inter, and the
                // per-plane inter path - all derive it exactly this way, and intra chroma scales
                // against lists 1 and 2 where inter chroma uses 4 and 5.
                //
                // The direct current coefficients are not dequantised here: they go through a
                // 2x2 or 2x4 Hadamard transform first (8.5.11.2), and at 4:2:2 against a
                // quantiser three higher than this one.
                ReadOnlySpan<byte> chromaAcMatrixCb = default, chromaAcMatrixCr = default;
                int chromaQpCb = 0, chromaQpCr = 0;
                if (pixelShift == 0)
                {
                    var chromaPps = context.ActivePps!;
                    chromaAcMatrixCb = chromaPps.ScalingMatrix4[mb.IsIntra ? 1 : 4];
                    chromaAcMatrixCr = chromaPps.ScalingMatrix4[mb.IsIntra ? 2 : 5];
                    chromaQpCb = chromaQpTable[Math.Clamp(mb.QuantizationParameter + chromaPps.ChromaQpIndexOffset, 0, maxQp)];
                    chromaQpCr = chromaQpTable[Math.Clamp(mb.QuantizationParameter + chromaPps.SecondChromaQpIndexOffset, 0, maxQp)];
                }

                for (int c = 0; c < 2; c++)
                {
                    int acBase = c == 0 ? 0 : chromaCrAcBase;
                    ReadOnlySpan<byte> acMatrix = c == 0 ? chromaAcMatrixCb : chromaAcMatrixCr;
                    int acQp = c == 0 ? chromaQpCb : chromaQpCr;
                    int chromaGroupCount = isChroma422 ? 2 : 1;
                    for (int i8x8 = 0; i8x8 < chromaGroupCount; i8x8++)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            int blockIndex = i8x8 * 4 + i;
                            int offset = acBase + blockIndex * 16 + 1;
                            int chromaBlkIdx = c * chromaBlocksPerComponent + blockIndex;
                            int acCtx = GetChromaCbfCtxIdxInc<TShape>(4, chromaBlkIdx, mbAddr, mbWidth, mb.IsIntra);
                            int cbf = DecodeBlockCabac(ref decoder, residualChroma.Slice(offset, 15), 1, 15, ctxBlockCat: 4, cbfCtxIdxInc: acCtx, fieldMode,
                                dequantMatrix: acMatrix, qp: acQp);
                            if (cbf != 0)
                            {
                                cabacChromaCbf![mbAddr] |= (1 << (c * chromaBlocksPerComponent + blockIndex));
                                unsafe { mb.NonZeroCountChroma[c * chromaBlocksPerComponent + blockIndex] = 1; }
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Decode a block of CABAC coefficients with coded_block_flag.
    /// Returns the coded_block_flag value (0 or 1) for neighbor tracking.
    /// </summary>
    /// <param name="dequantMatrix">
    /// The scaling matrix to dequantise against as each coefficient is stored, or empty to store
    /// the parsed levels and leave dequantisation to the reconstruct side.
    /// </param>
    /// <param name="qp">The quantiser the block was coded at. Read only when dequantising.</param>
    /// <remarks>
    /// FFmpeg applies <c>qmul[j]</c> inside this loop rather than afterwards, at the positions the
    /// significance map has just reported non-zero, and that - not the lookup table they build it
    /// from - is what the arrangement is worth: the CABAC state machine is already the thing
    /// stalled on memory here, and the coefficients are already in registers. Doing it on the
    /// reconstruct side instead means a call, a span, a second read of all sixteen positions and
    /// a loop around them, for every coded block.
    ///
    /// Empty is not the same as a flat matrix: chroma DC and the luma DC of an intra 16x16
    /// macroblock are dequantised after their Hadamard transform (8.5.11.2, 8.5.12.2), and the
    /// high bit depth path dequantises into a wider buffer than this one, because a dequantised
    /// coefficient does not fit a short above eight bits.
    /// </remarks>
    private int DecodeBlockCabac(ref CabacDecoder decoder, Span<short> block, int startIdx, int maxCoeffs, int ctxBlockCat, int cbfCtxIdxInc, bool fieldMode, bool chroma422Dc = false,
        ReadOnlySpan<byte> dequantMatrix = default, int qp = 0)
    {
        bool trace = isDiagnosticMb && DiagnosticOutput != null && ctxBlockCat == 2;
        bool dequantise = !dequantMatrix.IsEmpty;

        // Clamped and split exactly as Dequantize4x4WithMatrix does it, once for the whole block.
        int qpDiv6 = 0, qpMod6 = 0;
        if (dequantise)
        {
            int clamped = qp < 0 ? 0 : (qp > 63 ? 63 : qp);
            qpDiv6 = clamped / 6;
            qpMod6 = clamped % 6;
        }
        {
            int cbfCtxOffset;
            switch (ctxBlockCat)
            {
                case 0: cbfCtxOffset = 0; break;
                case 1: cbfCtxOffset = 4; break;
                case 2: cbfCtxOffset = 8; break;
                case 3: cbfCtxOffset = 12; break;
                case 4: cbfCtxOffset = 16; break;
                default: cbfCtxOffset = 8; break;
            }
            int cbfCtx = 85 + cbfCtxOffset + cbfCtxIdxInc;
            if (trace) DiagnosticOutput!.Write($"    CBF ctx={cbfCtx} {decoder.GetContextState(cbfCtx)} R={decoder.DebugRange} O={decoder.DebugOffset}");
            int codedBlockFlag = decoder.DecodeBin(cbfCtx);
            if (trace) DiagnosticOutput!.WriteLine($" → {codedBlockFlag}");
            
            if (codedBlockFlag == 0)
                return 0;
            
            // H.264 Table 9-34: significant_coeff_flag and last_significant_coeff_flag
            // use different context base offsets for field-coded vs frame-coded blocks
            int sigCtxBase, lastCtxBase;
            if (fieldMode)
            {
                switch (ctxBlockCat)
                {
                    case 0: sigCtxBase = 277; lastCtxBase = 338; break;
                    case 1: sigCtxBase = 292; lastCtxBase = 353; break;
                    case 2: sigCtxBase = 306; lastCtxBase = 367; break;
                    case 3: sigCtxBase = 321; lastCtxBase = 382; break;
                    case 4: sigCtxBase = 324; lastCtxBase = 385; break;
                    default: sigCtxBase = 306; lastCtxBase = 367; break;
                }
            }
            else
            {
                switch (ctxBlockCat)
                {
                    case 0: sigCtxBase = 105; lastCtxBase = 166; break;
                    case 1: sigCtxBase = 120; lastCtxBase = 181; break;
                    case 2: sigCtxBase = 134; lastCtxBase = 195; break;
                    case 3: sigCtxBase = 149; lastCtxBase = 210; break;
                    case 4: sigCtxBase = 152; lastCtxBase = 213; break;
                    default: sigCtxBase = 134; lastCtxBase = 195; break;
                }
            }
            
            Scratch.Of16<int> significantCoeffsStorage = default;
            Span<int> significantCoeffs = significantCoeffsStorage;
            int numSignificant = 0;
            bool foundLast = false;
            
            for (int scanPos = 0; scanPos < maxCoeffs - 1 && numSignificant < maxCoeffs; scanPos++)
            {
                int absPos = startIdx + scanPos;
                // For chroma 4:2:2 DC, use sig_coeff_offset_dc mapping (groups positions into 3 contexts)
                int sigInc = chroma422Dc ? SigCoeffOffsetDc[scanPos] : Math.Min(scanPos, 14);
                int sigCtx = sigCtxBase + sigInc;
                if (trace) DiagnosticOutput!.Write($"    sig[{scanPos}] ctx={sigCtx} {decoder.GetContextState(sigCtx)} R={decoder.DebugRange} O={decoder.DebugOffset}");
                int sig = decoder.DecodeBin(sigCtx);
                if (trace) DiagnosticOutput!.Write($" → {sig}");
                if (sig != 0)
                {
                    significantCoeffs[numSignificant++] = absPos;
                    int lastInc = chroma422Dc ? SigCoeffOffsetDc[scanPos] : Math.Min(scanPos, 14);
                    int lastCtx = lastCtxBase + lastInc;
                    if (trace) DiagnosticOutput!.Write($" | last ctx={lastCtx} {decoder.GetContextState(lastCtx)} R={decoder.DebugRange} O={decoder.DebugOffset}");
                    int last = decoder.DecodeBin(lastCtx);
                    if (trace) DiagnosticOutput!.Write($" → {last}");
                    if (last != 0)
                    {
                        foundLast = true;
                        if (trace) DiagnosticOutput!.WriteLine(" LAST");
                        break;
                    }
                }
                if (trace) DiagnosticOutput!.WriteLine();
            }
            
            if (!foundLast && numSignificant < maxCoeffs)
            {
                significantCoeffs[numSignificant++] = startIdx + maxCoeffs - 1;
                if (trace) DiagnosticOutput!.WriteLine($"    implicit last at pos {maxCoeffs - 1}");
            }
            
            if (trace) DiagnosticOutput!.WriteLine($"    sigPositions=[{string.Join(",", significantCoeffs.Slice(0, numSignificant).ToArray())}] numSig={numSignificant}");
            
            int levelCtxOffset;
            switch (ctxBlockCat)
            {
                case 0: levelCtxOffset = 227; break;
                case 1: levelCtxOffset = 237; break;
                case 2: levelCtxOffset = 247; break;
                case 3: levelCtxOffset = 257; break;
                case 4: levelCtxOffset = 266; break;
                default: levelCtxOffset = 247; break;
            }

            int nodeCtx = 0;

            for (int i = numSignificant - 1; i >= 0; i--)
            {
                int pos = significantCoeffs[i];
                int ctxIdxInc0 = CoeffAbsLevel1Ctx[nodeCtx];
                int prefix = 0;
                if (trace) DiagnosticOutput!.Write($"    level[{i}] pos={pos} bin0 ctx={levelCtxOffset + ctxIdxInc0} R={decoder.DebugRange} O={decoder.DebugOffset}");
                int bin0 = decoder.DecodeBin(levelCtxOffset + ctxIdxInc0);
                if (trace) DiagnosticOutput!.Write($" → {bin0}");

                if (bin0 != 0)
                {
                    prefix = 1;
                    int ctxIdxIncN = chroma422Dc
                        ? CoeffAbsLevelGt1Ctx422Dc[nodeCtx]
                        : CoeffAbsLevelGt1Ctx[nodeCtx];
                    while (prefix < 14)
                    {
                        if (trace) DiagnosticOutput!.Write($" binN ctx={levelCtxOffset + ctxIdxIncN}");
                        int bin = decoder.DecodeBin(levelCtxOffset + ctxIdxIncN);
                        if (trace) DiagnosticOutput!.Write($"→{bin}");
                        if (bin == 0)
                            break;
                        prefix++;
                    }
                }

                int absLevelMinus1;
                if (prefix < 14) absLevelMinus1 = prefix;
                else
                {
                    uint suffix = decoder.DecodeUeg0Bypass();
                    absLevelMinus1 = 14 + (int)Math.Min(suffix, 2047);
                }

                int absLevel = absLevelMinus1 + 1;
                nodeCtx = absLevel == 1
                    ? LevelTransitionEq1[nodeCtx]
                    : LevelTransitionGt1[nodeCtx];

                int sign = decoder.DecodeBypass();
                // Raster order, not scan order. FFmpeg's decode_cabac_residual writes
                // block[scantable[index]] as it parses, so nothing is reordered afterwards -
                // and the eight-by-eight path below has always done the same here. Doing it
                // on this side touches only the coefficients that are actually present,
                // where the reconstruct side had to scatter all sixteen positions of every
                // coded block whether they held anything or not.
                //
                // Chroma DC is exempt: its coefficients are a 2x2 or 2x4 Hadamard block with
                // no four-by-four scan, and the reconstruct side reads them as they arrive.
                int rasterPos = ctxBlockCat == 3 ? pos : activeScan4x4[pos];
                int blockPos = rasterPos - startIdx;
                if (blockPos >= 0 && blockPos < block.Length)
                {
                    int level = sign != 0 ? -absLevel : absLevel;
                    // The multiplier is indexed by the position in the block, not by the index of
                    // this coefficient in the span: an AC-only block starts at raster position 1,
                    // and the scaling matrix entry for position 1 is not the entry for position 0.
                    block[blockPos] = dequantise
                        ? H264Transform.DequantizeLevel(
                              level, H264Transform.Multiplier4x4(dequantMatrix, qpDiv6, qpMod6, rasterPos))
                        : (short)level;
                }
                if (trace) DiagnosticOutput!.WriteLine($" prefix={prefix} absLvl={absLevel} sign={sign} → coeff={block[blockPos]}");
            }
            return 1;
        }
    }

    // CABAC coefficient level context state machine tables (from ffmpeg h264_cabac.c).
    // node_ctx tracks the state; transitions depend on whether the decoded level was 1 or >1.
    // The counter-based approach (numEq1/numGt1) diverges from this when level=1 appears
    // between level>1 values, because level=1 also advances the node in the "gt1 track".
    private static ReadOnlySpan<byte> CoeffAbsLevel1Ctx => new byte[] { 1, 2, 3, 4, 0, 0, 0, 0 };
    private static ReadOnlySpan<byte> CoeffAbsLevelGt1Ctx => new byte[] { 5, 5, 5, 5, 6, 7, 8, 9 };
    // For chroma 4:2:2 DC: last entry is 8 instead of 9 (from ffmpeg coeff_abs_levelgt1_ctx[1])
    private static ReadOnlySpan<byte> CoeffAbsLevelGt1Ctx422Dc => new byte[] { 5, 5, 5, 5, 6, 7, 8, 8 };
    private static ReadOnlySpan<byte> LevelTransitionEq1 => new byte[] { 1, 2, 3, 3, 4, 5, 6, 7 };
    private static ReadOnlySpan<byte> LevelTransitionGt1 => new byte[] { 4, 4, 4, 4, 5, 6, 7, 7 };
    
    // Significance context offset for DC blocks when is_dc && chroma422.
    // Maps scan position to context increment (from ffmpeg sig_coeff_offset_dc[7]).
    private static ReadOnlySpan<byte> SigCoeffOffsetDc => new byte[] { 0, 0, 1, 1, 2, 2, 2 };

    // Per-position significance context offset for 8x8 luma (ctxBlockCat=5).
    // From ffmpeg h264_cabac.c significant_coeff_flag_offset_8x8[2][63].
    // Index 0 = frame mode, index 1 = field mode.
    private static ReadOnlySpan<byte> SigCoeffFlagOffset8x8Frame => new byte[]
    {
         0,  1,  2,  3,  4,  5,  5,  4,  4,  3,  3,  4,  4,  4,  5,  5,
         4,  4,  4,  4,  3,  3,  6,  7,  7,  7,  8,  9, 10,  9,  8,  7,
         7,  6, 11, 12, 13, 11,  6,  7,  8,  9, 14, 10,  9,  8,  6, 11,
        12, 13, 11,  6,  9, 14, 10,  9, 11, 12, 13, 11, 14, 10, 12,
    };

    private static ReadOnlySpan<byte> SigCoeffFlagOffset8x8Field => new byte[]
    {
         0,  1,  1,  2,  2,  3,  3,  4,  5,  6,  7,  7,  7,  8,  4,  5,
         6,  9, 10, 10,  8, 11, 12, 11,  9,  9, 10, 10,  8, 11, 12, 11,
         9,  9, 10, 10,  8, 11, 12, 11,  9,  9, 10, 10,  8, 13, 13,  9,
         9, 10, 10,  8, 13, 13,  9,  9, 10, 10, 14, 14, 14, 14, 14,
    };

    // Per-position last coefficient context offset for 8x8 luma (ctxBlockCat=5).
    // From ffmpeg h264_cabac.c ff_h264_last_coeff_flag_offset_8x8[63].
    // Same table for both frame and field mode.
    private static ReadOnlySpan<byte> LastCoeffFlagOffset8x8 => new byte[]
    {
         0,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
         2,  2,  2,  2,  2,  2,  2,  2,  2,  2,  2,  2,  2,  2,  2,  2,
         3,  3,  3,  3,  3,  3,  3,  3,  4,  4,  4,  4,  4,  4,  4,  4,
         5,  5,  5,  5,  6,  6,  6,  6,  7,  7,  7,  7,  8,  8,  8,
    };

    /// <summary>
    /// Decodes a block of 64 CABAC coefficients for ctxBlockCat=5 (8x8 luma).
    /// Key differences from 4x4 blocks: NO coded_block_flag (CBP bit serves this role),
    /// per-position significance/last context offsets, and 64 coefficients.
    /// Matches ffmpeg decode_cabac_residual_nondc for cat=5.
    /// </summary>
    /// <param name="dequantMatrix">
    /// The 8x8 scaling matrix to dequantise against as each coefficient is stored, or empty to
    /// store the parsed levels - see <see cref="DecodeBlockCabac"/>.
    /// </param>
    /// <param name="qp">The quantiser the block was coded at. Read only when dequantising.</param>
    private void DecodeBlock8x8Cabac(ref CabacDecoder decoder, Span<short> block, bool fieldMode,
        ReadOnlySpan<byte> dequantMatrix = default, int qp = 0)
    {
        bool dequantise = !dequantMatrix.IsEmpty;
        int qpDiv6 = 0, qpMod6 = 0;
        if (dequantise)
        {
            int clamped = qp < 0 ? 0 : (qp > 63 ? 63 : qp);
            qpDiv6 = clamped / 6;
            qpMod6 = clamped % 6;
        }

        // Context bases differ between frame and field mode (H.264 Table 9-34)
        int sigCtxBase = fieldMode ? 436 : 402;
        int lastCtxBase = fieldMode ? 451 : 417;
        const int levelCtxOffset = 426; // coeff_abs_level_minus1 base for cat=5 (same for frame/field)
        ReadOnlySpan<byte> sigOffsets = fieldMode ? SigCoeffFlagOffset8x8Field : SigCoeffFlagOffset8x8Frame;

        // Significance map: find positions of non-zero coefficients (scan positions 0..62)
        Scratch.Of64<int> significantCoeffsStorage = default;
        Span<int> significantCoeffs = significantCoeffsStorage;
        int numSignificant = 0;
        bool foundLast = false;

        for (int scanPos = 0; scanPos < 63 && numSignificant < 64; scanPos++)
        {
            int sigCtx = sigCtxBase + sigOffsets[scanPos];
            int sig = decoder.DecodeBin(sigCtx);
            if (sig != 0)
            {
                significantCoeffs[numSignificant++] = scanPos;
                int lastCtx = lastCtxBase + LastCoeffFlagOffset8x8[scanPos];
                int last = decoder.DecodeBin(lastCtx);
                if (last != 0)
                {
                    foundLast = true;
                    break;
                }
            }
        }

        // Position 63 is implicitly significant if we haven't found last
        if (!foundLast && numSignificant < 64)
            significantCoeffs[numSignificant++] = 63;

        // Decode coefficient levels(reverse order, using ffmpeg's node_ctx state machine)
        int nodeCtx = 0;

        for (int i = numSignificant - 1; i >= 0; i--)
        {
            int scanPos = significantCoeffs[i];
            int ctxIdxInc0 = CoeffAbsLevel1Ctx[nodeCtx];

            int prefix = 0;
            int bin0 = decoder.DecodeBin(levelCtxOffset + ctxIdxInc0);
            if (bin0 != 0)
            {
                prefix = 1;
                int ctxIdxIncN = CoeffAbsLevelGt1Ctx[nodeCtx];
                while (prefix < 14)
                {
                    int bin = decoder.DecodeBin(levelCtxOffset + ctxIdxIncN);
                    if (bin == 0)
                        break;
                    prefix++;
                }
            }

            int absLevelMinus1;
            if (prefix < 14)
                absLevelMinus1 = prefix;
            else
            {
                uint suffix = decoder.DecodeUeg0Bypass();
                absLevelMinus1 = 14 + (int)Math.Min(suffix, 2047);
            }

            int absLevel = absLevelMinus1 + 1;
            nodeCtx = absLevel == 1
                ? LevelTransitionEq1[nodeCtx]
                : LevelTransitionGt1[nodeCtx];

            int sign = decoder.DecodeBypass();
            // Store at raster position (convert scan→raster using appropriate scan table).
            // Our row-first IDCT is mathematically equivalent to FFmpeg's transposed-scan +
            // column-first IDCT, so we use the non-transposed scan tables here.
            ReadOnlySpan<byte> scan8x8 = fieldMode ? ScanOrder.FieldScan8x8 : ScanOrder.ZigZag8x8;
            int rasterPos = scan8x8[scanPos];
            if (rasterPos < block.Length)
            {
                int level = sign != 0 ? -absLevel : absLevel;
                block[rasterPos] = dequantise
                    ? H264Transform.DequantizeLevel(
                          level, H264Transform.Multiplier8x8(dequantMatrix, qpDiv6, qpMod6, rasterPos))
                    : (short)level;
            }
        }
        
    }
}

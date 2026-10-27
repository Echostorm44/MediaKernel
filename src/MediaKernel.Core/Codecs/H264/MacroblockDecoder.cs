using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.H264
{
    /// <summary>
    /// Decodes H.264 macroblocks from entropy-coded bitstream.
    /// Handles both CABAC and CAVLC entropy coding modes.
    /// </summary>
    public ref struct MacroblockDecoder
    {
        private readonly SequenceParameterSet sps;
        private readonly PictureParameterSet pps;
        private readonly SliceHeader sliceHeader;
        private readonly bool useCabac;
        private readonly bool constrainedIntraPred;
        private readonly int widthInMacroblocks;
        private readonly int heightInMacroblocks;
        private readonly int nzCountsPerMb;

        // Scratch space for coefficient decoding
        private readonly Span<int> coefficientBuffer;

        // Per-MB intra4x4 prediction modes (16 per MB) for most-probable-mode computation
        private readonly Span<byte> intra4x4Modes;
        
        // Per-MB intra status for constrained intra prediction neighbor checks
        private readonly Span<bool> mbIsIntra;

        // MBAFF neighbor addresses for NNZ context (set by caller before DecodeMacroblockCavlc)
        private bool isMbaffActive;
        internal bool isFieldMb; // True when current MB is field-coded in MBAFF (for scan/ref count)
        private int mbaffLeftAddrTop;   // Left neighbor for top 4x4 rows (y4=0,1). -1 if unavailable
        private int mbaffLeftAddrBot;   // Left neighbor for bottom 4x4 rows (y4=2,3). -1 if unavailable
        private int mbaffTopAddr;       // Top neighbor. -1 if unavailable
        private int mbaffLeftBlockRemap; // Index into MbaffLeftBlockOptions (0-3)

        // CBP mapping tables(H.264 spec Table 9-4a and 9-4b, ChromaArrayType != 0)
        private static readonly byte[] IntraCbpTable =
        {
            47,31,15, 0,23,27,29,30, 7,11,13,14,39,43,45,46,
            16, 3, 5,10,12,19,21,26,28,35,37,42,44, 1, 2, 4,
             8,17,18,20,24, 6, 9,22,25,32,33,34,36,40,38,41,
        };
        private static readonly byte[] InterCbpTable =
        {
             0,16, 1, 2, 4, 8,32, 3, 5,10,12,15,47, 7,11,13,
            14, 6, 9,31,35,37,42,44,33,34,36,40,39,43,45,46,
            17,18,20,24,19,21,26,28,23,27,29,30,22,25,38,41,
        };

        // CBP mapping tables for monochrome (H.264 spec Table 9-4a and 9-4b, ChromaArrayType == 0)
        // Only 16 entries (luma-only, 0-15), no chroma bits
        private static readonly byte[] IntraCbpTableMono =
        {
            15, 0, 7, 11, 13, 14, 3, 5, 10, 12, 1, 2, 4, 8, 6, 9,
        };
        private static readonly byte[] InterCbpTableMono =
        {
            0, 1, 2, 4, 8, 3, 5, 10, 12, 15, 7, 11, 13, 14, 6, 9,
        };

        // MBAFF left block row remapping: [table_index][y4] → actual y4 in neighbor MB
        // Matches FFmpeg's left_block_options for NNZ context in h264_mvpred.h
        private static readonly int[][] MbaffLeftBlockOptions = new int[4][]
        {
            new int[] { 0, 1, 2, 3 },   // 0: Frame→Frame (identity)
            new int[] { 2, 2, 3, 3 },   // 1: Frame→Field (current=frame, left=field)
            new int[] { 0, 0, 1, 1 },   // 2: Field→Frame (current=field, left=frame)
            new int[] { 0, 2, 0, 2 }    // 3: Field→Field cross-pair
        };

        /// <summary>
        /// Creates a macroblock decoder for a slice.
        /// </summary>
        /// <param name="sps">Sequence parameter set.</param>
        /// <param name="pps">Picture parameter set.</param>
        /// <param name="sliceHeader">Slice header with QP and slice type.</param>
        /// <param name="coefficientStorage">Pre-allocated storage for coefficients (256 ints minimum).</param>
        public MacroblockDecoder(
            SequenceParameterSet sps,
            PictureParameterSet pps,
            SliceHeader sliceHeader,
            Span<int> coefficientStorage,
            Span<byte> intra4x4ModeStorage,
            Span<bool> mbIsIntraStorage)
        {
            this.sps = sps;
            this.pps = pps;
            this.sliceHeader = sliceHeader;
            useCabac = pps.EntropyCodingMode;
            constrainedIntraPred = pps.ConstrainedIntraPrediction;
            widthInMacroblocks = sps.PictureWidthInMacroblocksMinus1 + 1;
            heightInMacroblocks = sps.FrameMacroblockOnly
                ? sps.PictureHeightInMapUnitsMinus1 + 1
                : (sps.PictureHeightInMapUnitsMinus1 + 1) * 2;
            nzCountsPerMb = sps.ChromaFormat == ChromaFormat.Chroma422 ? 32 : 24;

            if (coefficientStorage.Length < 256)
                throw new ArgumentException("Coefficient storage must have at least 256 elements");

            coefficientBuffer = coefficientStorage;
            intra4x4Modes = intra4x4ModeStorage;
            mbIsIntra = mbIsIntraStorage;
        }

        /// <summary>
        /// Configures MBAFF-aware neighbor addresses for NNZ context computation.
        /// Must be called before DecodeMacroblockCavlc when decoding MBAFF frames.
        /// Addresses of -1 indicate unavailable neighbors (picture/slice boundary).
        /// </summary>
        public void SetMbaffNeighbors(int leftAddrTop, int leftAddrBot, int topAddr, int leftBlockIdx, bool fieldMb)
        {
            isMbaffActive = true;
            isFieldMb = fieldMb;
            mbaffLeftAddrTop = leftAddrTop;
            mbaffLeftAddrBot = leftAddrBot;
            mbaffTopAddr = topAddr;
            mbaffLeftBlockRemap = leftBlockIdx;
        }

        /// <summary>
        /// Clears MBAFF neighbor state, reverting to standard non-MBAFF NNZ derivation.
        /// </summary>
        public void ClearMbaffNeighbors()
        {
            isMbaffActive = false;
            isFieldMb = false;
        }

        /// <summary>
        /// Decodes a macroblock using CAVLC entropy coding.
        /// </summary>
        /// <param name="decoder">CAVLC bitstream decoder.</param>
        /// <param name="mbAddress">Current macroblock address (raster scan).</param>
        /// <param name="allNzCounts">NZC storage for all MBs (24 bytes each: 16 luma + 4 Cb + 4 Cr).</param>
        /// <param name="macroblock">Output: decoded macroblock data.</param>
        /// <param name="residualLuma">Output: 16x16 luma residual coefficients.</param>
        /// <param name="residualChroma">Output: chroma residual coefficients (8x8 for 4:2:0).</param>
        public void DecodeMacroblockCavlc(
            ref CavlcDecoder decoder,
            int mbAddress,
            Span<byte> allNzCounts,
            ref int previousMbQp,
            out Macroblock macroblock,
            Span<short> residualLuma,
            Span<short> residualChroma)
        {
            macroblock = new Macroblock
            {
                Address = mbAddress,
                PositionX = mbAddress % widthInMacroblocks,
                PositionY = mbAddress / widthInMacroblocks,
                QuantizationParameter = previousMbQp,
                InterPartitionMode = -1,
                BSliceMbType = -1
            };

            bool diagMb = H264Decoder.DiagnosticOutput != null && mbAddress <= 45;

            // For I-slice, decode mb_type
            if (sliceHeader.Type == SliceType.ISlice || sliceHeader.Type == SliceType.SiSlice)
            {
                DecodeMbTypeIntra(ref decoder, ref macroblock);
            }
            else
            {
                // For P/B slices, mb_skip_run was already handled by the caller.
                // Decode mb_type for the coded MB.
                DecodeMbTypeInter(ref decoder, ref macroblock);
            }
            if (diagMb) H264Decoder.DiagnosticOutput!.WriteLine($"CAVLC-STEP: addr={mbAddress} afterMbType bitPos={decoder.BitPosition} type={macroblock.IntraType}");

            // Decode coded block pattern
            if (macroblock.IntraType != IntraMacroblockType.IntraPcm)
            {
                DecodeCbp(ref decoder, ref macroblock);
            }
            if (diagMb) H264Decoder.DiagnosticOutput!.WriteLine($"CAVLC-STEP: addr={mbAddress} afterCbp bitPos={decoder.BitPosition} cbpL={macroblock.CodedBlockPatternLuma} cbpC={macroblock.CodedBlockPatternChroma}");

            // Per H.264 spec 7.3.5.1: inter MBs with luma residual can use 8x8 transform
            // BUT only when all sub-partitions are at least 8x8 (noSubMbPartSizeLessThan8x8Flag)
            if (pps.Transform8x8Mode && (macroblock.CodedBlockPatternLuma & 0xF) != 0 && !macroblock.IsIntra
                && AllSubPartitionsAreMinimum8x8(ref macroblock))
            {
                macroblock.Transform8x8 = decoder.ReadBits(1) != 0;
            }

            // Decode QP delta — present for Intra16x16 regardless of CBP (H.264 spec 7.3.5)
            bool isIntra16x16 = macroblock.IntraType >= IntraMacroblockType.Intra16x16_0_0_0 &&
                                macroblock.IntraType <= IntraMacroblockType.Intra16x16_3_2_1;
            if (isIntra16x16 || macroblock.CodedBlockPatternLuma > 0 || macroblock.CodedBlockPatternChroma > 0)
            {
                int mbQpDelta = decoder.ReadExpGolombSigned();
                int qpWrapModulus = 52 + 6 * (sps.BitDepthLuma - 8);
                int wrappedQp = (previousMbQp + mbQpDelta) % qpWrapModulus;
                macroblock.QuantizationParameter = wrappedQp < 0 ? wrappedQp + qpWrapModulus : wrappedQp;
            }
            if (diagMb) H264Decoder.DiagnosticOutput!.WriteLine($"CAVLC-STEP: addr={mbAddress} afterQpD bitPos={decoder.BitPosition} qp={macroblock.QuantizationParameter}");

            // Decode residual data
            DecodeResidual(ref decoder, ref macroblock, mbAddress, allNzCounts, residualLuma, residualChroma);

            // Update previousMbQp for the next macroblock
            previousMbQp = macroblock.QuantizationParameter;
        }

        /// <summary>
        /// Decodes macroblock type for intra-coded macroblock.
        /// </summary>
        private void DecodeMbTypeIntra(ref CavlcDecoder decoder, ref Macroblock macroblock)
        {
            uint mbType = decoder.ReadExpGolombUnsigned();

            macroblock.IsIntra = true;

            if (mbType == 0)
            {
                macroblock.IntraType = IntraMacroblockType.Intra4x4;
            }
            else if (mbType >= 1 && mbType <= 24)
            {
                // Intra16x16 types encode prediction mode and CBP
                macroblock.IntraType = (IntraMacroblockType)mbType;

                // Decode the embedded CBP and prediction mode
                int i16x16Type = (int)mbType - 1;
                macroblock.Intra16x16PredMode = (Intra16x16PredictionMode)(i16x16Type % 4);
                macroblock.CodedBlockPatternChroma = (i16x16Type / 4) % 3;
                macroblock.CodedBlockPatternLuma = (i16x16Type / 12) > 0 ? 15 : 0;
            }
            else
            {
                macroblock.IntraType = IntraMacroblockType.IntraPcm;
            }

            // Decode intra prediction modes
            if (macroblock.IntraType == IntraMacroblockType.Intra4x4)
            {
                // High Profile: decode transform_size_8x8_flag before intra modes
                // Per FFmpeg h264_cavlc.c line 773: only when dct8x8_allowed (pps.transform_8x8_mode)
                if (pps.Transform8x8Mode && decoder.ReadBits(1) != 0)
                {
                    macroblock.Transform8x8 = true;
                }

                DecodeIntra4x4Modes(ref decoder, ref macroblock);
            }

            // Decode intra chroma prediction mode (not present for I_PCM)
            if (macroblock.IntraType != IntraMacroblockType.IntraPcm &&
                sps.ChromaFormat != ChromaFormat.Monochrome)
            {
                macroblock.IntraChromaPredMode = (IntraChromaPredictionMode)decoder.ReadExpGolombUnsigned();
            }
        }

        /// <summary>
        /// Decodes macroblock type for inter-coded macroblock.
        /// </summary>
        private void DecodeMbTypeInter(ref CavlcDecoder decoder, ref Macroblock macroblock)
        {
            uint mbType = decoder.ReadExpGolombUnsigned();

            // Check if this is actually an intra MBin an inter slice
            bool isIntra;
            uint intraMbTypeOffset;
            if (sliceHeader.Type == SliceType.PSlice)
            {
                isIntra = mbType >= 5;
                intraMbTypeOffset = 5;
            }
            else // B-slice
            {
                isIntra = mbType >= 23;
                intraMbTypeOffset = 23;
            }

            if (isIntra)
            {
                macroblock.IsIntra = true;
                uint intraType = mbType - intraMbTypeOffset;
                if (intraType == 0)
                {
                    macroblock.IntraType = IntraMacroblockType.Intra4x4;
                }
                else if (intraType <= 24)
                {
                    macroblock.IntraType = (IntraMacroblockType)intraType;
                    // Extract embedded CBP and prediction mode from Intra16x16 mb_type
                    int i16x16Type = (int)intraType - 1;
                    macroblock.Intra16x16PredMode = (Intra16x16PredictionMode)(i16x16Type % 4);
                    macroblock.CodedBlockPatternChroma = (i16x16Type / 4) % 3;
                    macroblock.CodedBlockPatternLuma = (i16x16Type / 12) > 0 ? 15 : 0;
                }
                else
                {
                    macroblock.IntraType = IntraMacroblockType.IntraPcm;
                }

                if (macroblock.IntraType == IntraMacroblockType.Intra4x4)
                {
                    // High Profile: decode transform_size_8x8_flag before intra modes
                    if (pps.Transform8x8Mode && decoder.ReadBits(1) != 0)
                        macroblock.Transform8x8 = true;

                    DecodeIntra4x4Modes(ref decoder, ref macroblock);
                }

                // Chroma prediction mode is not present for I_PCM
                if (macroblock.IntraType != IntraMacroblockType.IntraPcm &&
                    sps.ChromaFormat != ChromaFormat.Monochrome)
                    macroblock.IntraChromaPredMode = (IntraChromaPredictionMode)decoder.ReadExpGolombUnsigned();
                return;
            }

            macroblock.IsIntra = false;

            if (sliceHeader.Type == SliceType.PSlice)
            {
                DecodeInterPredictionP(ref decoder, ref macroblock, mbType);
            }
            else
            {
                DecodeInterPredictionB(ref decoder, ref macroblock, mbType);
            }
        }

        /// <summary>
        /// Decodes inter prediction syntax for P-slice macroblocks.
        /// Per H.264 spec Table 7-13 and 7.3.5.1.
        /// </summary>
        private unsafe void DecodeInterPredictionP(ref CavlcDecoder decoder, ref Macroblock macroblock, uint mbType)
        {
            int numRefIdxL0Active = sliceHeader.NumRefIdxL0ActiveMinus1 + 1;
            // MBAFF field MBs: double ref count (FFmpeg: ref_count << MB_MBAFF)
            if (isFieldMb)
                numRefIdxL0Active <<= 1;

            macroblock.InterPartitionMode = (int)mbType;

            if (mbType <= 2)
            {
                // P_L0_16x16 (0), P_L0_L0_16x8 (1), P_L0_L0_8x16 (2)
                int numPartitions = mbType == 0 ? 1 : 2;

                // ref_idx_l0 for each partition (te(v) where v = numRefIdxL0Active - 1)
                for (int i = 0; i < numPartitions; i++)
                {
                    int refIdx = 0;
                    if (numRefIdxL0Active > 1)
                    {
                        refIdx = (int)decoder.ReadTruncatedExpGolomb(numRefIdxL0Active - 1);
                    }
                    macroblock.InterRefIdxL0[i] = (sbyte)refIdx;
                }

                // mvd_l0 for each partition (dx, dy)
                for (int i = 0; i < numPartitions; i++)
                {
                    var mx = decoder.ReadExpGolombSigned();
                    var my = decoder.ReadExpGolombSigned();
                    macroblock.InterMvdL0X[i] = (short)mx;
                    macroblock.InterMvdL0Y[i] = (short)my;
                }
            }
            else if (mbType == 3 || mbType == 4)
            {
                // P_8x8 (3) or P_8x8ref0 (4) — sub-macroblock partitions
                for (int i = 0; i < 4; i++)
                {
                    uint subType = decoder.ReadExpGolombUnsigned();
                    macroblock.InterSubMbTypes[i] = (byte)subType;
                }

                // ref_idx_l0 for each sub-MB (P_8x8ref0 always has ref=0)
                for (int i = 0; i < 4; i++)
                {
                    int refIdx = 0;
                    if (mbType == 3 && numRefIdxL0Active > 1)
                    {
                        refIdx = (int)decoder.ReadTruncatedExpGolomb(numRefIdxL0Active - 1);
                    }
                    macroblock.InterRefIdxL0[i] = (sbyte)refIdx;
                }

                // mvd_l0 for each sub-MB partition (stored sequentially)
                int mvdIdx = 0;
                for (int i = 0; i < 4; i++)
                {
                    int numSubParts = GetNumSubMbPartsP(macroblock.InterSubMbTypes[i]);
                    for (int j = 0; j < numSubParts; j++)
                    {
                        var mx = decoder.ReadExpGolombSigned();
                        var my = decoder.ReadExpGolombSigned();
                        macroblock.InterMvdL0X[mvdIdx] = (short)mx;
                        macroblock.InterMvdL0Y[mvdIdx] = (short)my;
                        mvdIdx++;
                    }
                }
            }
        }

        /// <summary>
        /// Decodes inter prediction syntax for B-slice macroblocks.
        /// Per H.264 spec Table 7-14, 7-17, 7-18 and 7.3.5.1.
        /// </summary>
        private unsafe void DecodeInterPredictionB(ref CavlcDecoder decoder, ref Macroblock macroblock, uint mbType)
        {
            int numRefIdxL0Active = sliceHeader.NumRefIdxL0ActiveMinus1 + 1;
            int numRefIdxL1Active = sliceHeader.NumRefIdxL1ActiveMinus1 + 1;
            // MBAFF field MBs: double ref count (FFmpeg: ref_count << MB_MBAFF)
            if (isFieldMb)
            {
                numRefIdxL0Active <<= 1;
                numRefIdxL1Active <<= 1;
            }
            macroblock.BSliceMbType = (int)mbType;

            if (mbType == 0)
            {
                // B_Direct_16x16: no explicit ref_idx or mvd — derived at decode time
                return;
            }

            if (mbType >= 1 && mbType <= 21)
            {
                // Non-sub-MB B-slice types (1..21)
                // Buffers on the stack: this runs per B-slice macroblock, and as arrays the two
                // flag sets were allocated for every one of them.
                Scratch.Of2<bool> usesL0Storage = default;
                Span<bool> usesL0 = usesL0Storage;
                Scratch.Of2<bool> usesL1Storage = default;
                Span<bool> usesL1 = usesL1Storage;
                GetBSlicePartInfo(mbType, out int numPartitions, usesL0, usesL1);

                // ref_idx_l0 for each partition that uses L0
                for (int i = 0; i < numPartitions; i++)
                {
                    if (usesL0[i])
                    {
                        int refIdx = numRefIdxL0Active > 1 ? (int)decoder.ReadTruncatedExpGolomb(numRefIdxL0Active - 1) : 0;
                        macroblock.InterRefIdxL0[i] = (sbyte)refIdx;
                    }
                    else
                    {
                        macroblock.InterRefIdxL0[i] = -1;
                    }
                }

                // ref_idx_l1 for each partition that uses L1
                for (int i = 0; i < numPartitions; i++)
                {
                    if (usesL1[i])
                    {
                        int refIdx = numRefIdxL1Active > 1 ? (int)decoder.ReadTruncatedExpGolomb(numRefIdxL1Active - 1) : 0;
                        macroblock.InterRefIdxL1[i] = (sbyte)refIdx;
                    }
                    else
                    {
                        macroblock.InterRefIdxL1[i] = -1;
                    }
                }

                // mvd_l0 for each partition that uses L0 (partition-indexed)
                for (int i = 0; i < numPartitions; i++)
                {
                    if (usesL0[i])
                    {
                        macroblock.InterMvdL0X[i] = (short)decoder.ReadExpGolombSigned();
                        macroblock.InterMvdL0Y[i] = (short)decoder.ReadExpGolombSigned();
                    }
                }

                // mvd_l1 for each partition that uses L1 (partition-indexed)
                for (int i = 0; i < numPartitions; i++)
                {
                    if (usesL1[i])
                    {
                        macroblock.InterMvdL1X[i] = (short)decoder.ReadExpGolombSigned();
                        macroblock.InterMvdL1Y[i] = (short)decoder.ReadExpGolombSigned();
                    }
                }
            }
            else if (mbType == 22)
            {
                // B_8x8 — sub-macroblock partitions
                for (int i = 0; i < 4; i++)
                    macroblock.BSliceSubMbTypes[i] = (byte)decoder.ReadExpGolombUnsigned();

                // ref_idx_l0 for each sub-MB that uses L0
                for (int i = 0; i < 4; i++)
                {
                    if (SubMbUsesL0B(macroblock.BSliceSubMbTypes[i]))
                    {
                        int refIdx = numRefIdxL0Active > 1 ? (int)decoder.ReadTruncatedExpGolomb(numRefIdxL0Active - 1) : 0;
                        macroblock.InterRefIdxL0[i] = (sbyte)refIdx;
                    }
                    else
                    {
                        macroblock.InterRefIdxL0[i] = -1;
                    }
                }

                // ref_idx_l1 for each sub-MB that uses L1
                for (int i = 0; i < 4; i++)
                {
                    if (SubMbUsesL1B(macroblock.BSliceSubMbTypes[i]))
                    {
                        int refIdx = numRefIdxL1Active > 1 ? (int)decoder.ReadTruncatedExpGolomb(numRefIdxL1Active - 1) : 0;
                        macroblock.InterRefIdxL1[i] = (sbyte)refIdx;
                    }
                    else
                    {
                        macroblock.InterRefIdxL1[i] = -1;
                    }
                }

                // mvd_l0 for each sub-MB partition that uses L0
                int mvdL0Idx = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (SubMbUsesL0B(macroblock.BSliceSubMbTypes[i]))
                    {
                        int numSubParts = GetNumSubMbPartsB(macroblock.BSliceSubMbTypes[i]);
                        for (int j = 0; j < numSubParts; j++)
                        {
                            macroblock.InterMvdL0X[mvdL0Idx] = (short)decoder.ReadExpGolombSigned();
                            macroblock.InterMvdL0Y[mvdL0Idx] = (short)decoder.ReadExpGolombSigned();
                            mvdL0Idx++;
                        }
                    }
                }

                // mvd_l1 for each sub-MB partition that uses L1
                int mvdL1Idx = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (SubMbUsesL1B(macroblock.BSliceSubMbTypes[i]))
                    {
                        int numSubParts = GetNumSubMbPartsB(macroblock.BSliceSubMbTypes[i]);
                        for (int j = 0; j < numSubParts; j++)
                        {
                            macroblock.InterMvdL1X[mvdL1Idx] = (short)decoder.ReadExpGolombSigned();
                            macroblock.InterMvdL1Y[mvdL1Idx] = (short)decoder.ReadExpGolombSigned();
                            mvdL1Idx++;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// H.264 spec 7.4.5: noSubMbPartSizeLessThan8x8Flag.
        /// Returns true if all sub-partitions are at least 8x8 (allowing transform_size_8x8_flag).
        /// For P_8x8: all sub_mb_types must be P_L0_8x8 (0).
        /// For B_8x8: all must be B_Direct_8x8 (0, requires direct_8x8_inference), B_L0/L1/Bi_8x8 (1-3).
        /// For non-sub-MB inter types (16x16, 16x8, 8x16): always true.
        /// </summary>
        private unsafe bool AllSubPartitionsAreMinimum8x8(ref Macroblock mb)
        {
            // B_Direct_16x16 — FFmpeg h264_cabac.c:2224: dct8x8_allowed &= direct_8x8_inference_flag
            if (mb.BSliceMbType == 0)
                return sps.Direct8x8Inference;
            
            // P_8x8 (InterPartitionMode 3 or 4)
            if (mb.InterPartitionMode == 3 || mb.InterPartitionMode == 4)
            {
                for (int i = 0; i < 4; i++)
                    if (mb.InterSubMbTypes[i] != 0) return false;
                return true;
            }
            
            // B_8x8 (BSliceMbType == 22)
            if (mb.BSliceMbType == 22)
            {
                for (int i = 0; i < 4; i++)
                {
                    byte st = mb.BSliceSubMbTypes[i];
                    if (st == 0 && !sps.Direct8x8Inference) return false;
                    else if (st > 3) return false;
                }
                return true;
            }
            
            return true;
        }

        /// <summary>
        /// Gets number of sub-MB partitions for P-slice sub_mb_type (Table 7-17).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetNumSubMbPartsP(uint subMbType)
        {
            return subMbType switch
            {
                0 => 1, // P_L0_8x8
                1 => 2, // P_L0_8x4
                2 => 2, // P_L0_4x8
                3 => 4, // P_L0_4x4
                _ => 1
            };
        }

        /// <summary>
        /// Gets number of sub-MB partitions for B-slice sub_mb_type (Table 7-18).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetNumSubMbPartsB(uint subMbType)
        {
            return subMbType switch
            {
                0 => 1,  // B_Direct_8x8 (no explicit mvd)
                1 => 1, 2 => 1, 3 => 1,   // 8x8 types
                4 => 2, 5 => 2, 6 => 2, 7 => 2, 8 => 2, 9 => 2, // 8x4 and 4x8 types
                10 => 4, 11 => 4, 12 => 4, // 4x4 types
                _ => 1
            };
        }

        /// <summary>
        /// Checks if a B-slice sub_mb_type uses L0 prediction (Table 7-18).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SubMbUsesL0B(uint subMbType)
        {
            // B_Direct_8x8=0 uses neither explicitly,
            // B_L0_*=1,4,5  B_Bi_*=3,8,9,12  use L0
            // B_L1_*=2,6,7  do NOT use L0
            return subMbType switch
            {
                0 => false, // Direct — implicit
                1 => true, 2 => false, 3 => true,
                4 => true, 5 => true, 6 => false, 7 => false,
                8 => true, 9 => true, 10 => true, 11 => false, 12 => true,
                _ => false
            };
        }

        /// <summary>
        /// Checks if a B-slice sub_mb_type uses L1 prediction (Table 7-18).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SubMbUsesL1B(uint subMbType)
        {
            return subMbType switch
            {
                0 => false,
                1 => false, 2 => true, 3 => true,
                4 => false, 5 => false, 6 => true, 7 => true,
                8 => true, 9 => true, 10 => false, 11 => true, 12 => true,
                _ => false
            };
        }

        /// <summary>
        /// Gets partition info for B-slice mb_type 1..21 (Table 7-14).
        /// Returns number of partitions and whether each uses L0/L1.
        /// </summary>
        private static void GetBSlicePartInfo(uint mbType, out int numPartitions, Span<bool> usesL0, Span<bool> usesL1)
        {
            // B-slice types 1..3 are 16x16 (1 partition)
            // Types 4..21 are 16x8 or 8x16 (2 partitions)
            if (mbType <= 3)
            {
                numPartitions = 1;
                usesL0.Clear();
                usesL1.Clear();
                switch (mbType)
                {
                    case 1: usesL0[0] = true; break;                // B_L0_16x16
                    case 2: usesL1[0] = true; break;                // B_L1_16x16
                    case 3: usesL0[0] = true; usesL1[0] = true; break; // B_Bi_16x16
                }
            }
            else
            {
                numPartitions = 2;
                usesL0.Clear();
                usesL1.Clear();

                // Table 7-14: types 4..21 are 16x8 or 8x16 with two partitions
                // Pattern: L0_L0, L1_L1, L0_L1, L1_L0, L0_Bi, L1_Bi, Bi_L0, Bi_L1, Bi_Bi
                switch (mbType)
                {
                    case 4:  usesL0[0] = true; usesL0[1] = true; break;                                // B_L0_L0_16x8
                    case 5:  usesL0[0] = true; usesL0[1] = true; break;                                // B_L0_L0_8x16
                    case 6:  usesL1[0] = true; usesL1[1] = true; break;                                // B_L1_L1_16x8
                    case 7:  usesL1[0] = true; usesL1[1] = true; break;                                // B_L1_L1_8x16
                    case 8:  usesL0[0] = true; usesL1[1] = true; break;                                // B_L0_L1_16x8
                    case 9:  usesL0[0] = true; usesL1[1] = true; break;                                // B_L0_L1_8x16
                    case 10: usesL1[0] = true; usesL0[1] = true; break;                                // B_L1_L0_16x8
                    case 11: usesL1[0] = true; usesL0[1] = true; break;                                // B_L1_L0_8x16
                    case 12: usesL0[0] = true; usesL0[1] = true; usesL1[1] = true; break;              // B_L0_Bi_16x8
                    case 13: usesL0[0] = true; usesL0[1] = true; usesL1[1] = true; break;              // B_L0_Bi_8x16
                    case 14: usesL1[0] = true; usesL0[1] = true; usesL1[1] = true; break;              // B_L1_Bi_16x8
                    case 15: usesL1[0] = true; usesL0[1] = true; usesL1[1] = true; break;              // B_L1_Bi_8x16
                    case 16: usesL0[0] = true; usesL1[0] = true; usesL0[1] = true; break;              // B_Bi_L0_16x8
                    case 17: usesL0[0] = true; usesL1[0] = true; usesL0[1] = true; break;              // B_Bi_L0_8x16
                    case 18: usesL0[0] = true; usesL1[0] = true; usesL1[1] = true; break;              // B_Bi_L1_16x8
                    case 19: usesL0[0] = true; usesL1[0] = true; usesL1[1] = true; break;              // B_Bi_L1_8x16
                    case 20: usesL0[0] = true; usesL1[0] = true; usesL0[1] = true; usesL1[1] = true; break; // B_Bi_Bi_16x8
                    case 21: usesL0[0] = true; usesL1[0] = true; usesL0[1] = true; usesL1[1] = true; break; // B_Bi_Bi_8x16
                }
            }
        }

        /// <summary>
        /// Decodes 16 intra 4x4 prediction modes.
        /// </summary>
        private unsafe void DecodeIntra4x4Modes(ref CavlcDecoder decoder, ref Macroblock macroblock)
        {
            int mbAddr = macroblock.Address;
            int mbX = macroblock.PositionX;
            int mbY = macroblock.PositionY;

            // Per FFmpeg h264_cavlc.c line 775: 8x8 mode iterates by 4 (4 modes), 4x4 by 1 (16 modes)
            int step = macroblock.Transform8x8 ? 4 : 1;

            for (int i = 0; i < 16; i += step)
            {
                int bx = Block4x4ToX4[i];
                int by = Block4x4ToY4[i];

                int modeA = GetIntra4x4NeighborMode(mbAddr, mbX, mbY, bx - 1, by, i);
                int modeB = GetIntra4x4NeighborMode(mbAddr, mbX, mbY, bx, by - 1, i);

                int minMode = Math.Min(modeA, modeB);
                int mostProbableMode = minMode < 0 ? 2 : minMode;

                uint prevModeFlag = decoder.ReadBits(1);

                int mode;
                if (prevModeFlag != 0)
                {
                    mode = mostProbableMode;
                }
                else
                {
                    int remMode = (int)decoder.ReadBits(3);
                    if (remMode >= mostProbableMode)
                        remMode++;
                    mode = remMode;
                }

                if (macroblock.Transform8x8)
                {
                    // Per FFmpeg h264_cavlc.c line 789: fill_rectangle 2x2 for 8x8 blocks
                    // Replicate mode across all four 4x4 sub-blocks of the 8x8 block
                    for (int dy = 0; dy < 2; dy++)
                    {
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int subIdx = RasterToZscan[(by + dy) * 4 + (bx + dx)];
                            macroblock.Intra4x4PredModes[subIdx] = (byte)mode;
                            if (!intra4x4Modes.IsEmpty)
                                intra4x4Modes[mbAddr * 16 + subIdx] = (byte)mode;
                        }
                    }
                }
                else
                {
                    macroblock.Intra4x4PredModes[i] = (byte)mode;
                    if (!intra4x4Modes.IsEmpty)
                        intra4x4Modes[mbAddr * 16 + i] = (byte)mode;
                }
            }
        }

        /// <summary>
        /// Gets the Intra4x4 prediction mode of a neighboring 4x4 block.
        /// Returns -1 if neighbor is unavailable (picture boundary or different slice),
        /// 2 (DC) if neighbor is not Intra4x4.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetIntra4x4NeighborMode(int mbAddr, int mbX, int mbY, int nx, int ny, int blkIdx)
        {
            // Internal neighbor within current MB
            if (nx >= 0 && nx < 4 && ny >= 0 && ny < 4)
            {
                if (intra4x4Modes.IsEmpty) return 2;
                int neighborIdx = RasterToZscan[ny * 4 + nx];
                return intra4x4Modes[mbAddr * 16 + neighborIdx];
            }

            int firstMbInSlice = (int)sliceHeader.FirstMacroblockAddress;

            // Left neighbor (nx < 0)
            if (nx < 0)
            {
                int leftAddr;
                int actualNy = ny;
                if (isMbaffActive)
                {
                    leftAddr = (ny < 2) ? mbaffLeftAddrTop : mbaffLeftAddrBot;
                    if (leftAddr < 0) return -1;
                    actualNy = MbaffLeftBlockOptions[mbaffLeftBlockRemap][ny];
                }
                else
                {
                    leftAddr = mbAddr - 1;
                    if (mbX == 0 || leftAddr < firstMbInSlice) return -1;
                }
                if (constrainedIntraPred && !mbIsIntra.IsEmpty && !mbIsIntra[leftAddr])
                    return -1;
                if (intra4x4Modes.IsEmpty) return 2;
                int neighborIdx = RasterToZscan[actualNy * 4 + 3];
                return intra4x4Modes[leftAddr * 16 + neighborIdx];
            }

            // Top neighbor (ny < 0)
            if (ny < 0)
            {
                int topAddr;
                if (isMbaffActive)
                {
                    topAddr = mbaffTopAddr;
                    if (topAddr < 0) return -1;
                }
                else
                {
                    topAddr = mbAddr - widthInMacroblocks;
                    if (mbY == 0 || topAddr < firstMbInSlice) return -1;
                }
                if (constrainedIntraPred && !mbIsIntra.IsEmpty && !mbIsIntra[topAddr])
                    return -1;
                if (intra4x4Modes.IsEmpty) return 2;
                int neighborIdx = RasterToZscan[3 * 4 + nx];
                return intra4x4Modes[topAddr * 16 + neighborIdx];
            }

            return -1;
        }

        // Spatial (y*4+x) → Z-scan index for 4x4 block within macroblock
        private static ReadOnlySpan<byte> RasterToZscan => new byte[]
        {
             0,  1,  4,  5,
             2,  3,  6,  7,
             8,  9, 12, 13,
            10, 11, 14, 15
        };

        /// <summary>
        /// Decodes coded block pattern (CBP) using H.264 Table 9-4 mapping.
        /// </summary>
        private void DecodeCbp(ref CavlcDecoder decoder, ref Macroblock macroblock)
        {
            if (macroblock.IntraType >= IntraMacroblockType.Intra16x16_0_0_0 &&
                macroblock.IntraType <= IntraMacroblockType.Intra16x16_3_2_1)
            {
                // CBP already encoded in mb_type for I16x16
                return;
            }

            uint codeNum = decoder.ReadExpGolombUnsigned();

            // Map through Table 9-4a (intra) or Table 9-4b (inter)
            // Use monochrome tables when ChromaArrayType == 0
            byte[] cbpTable;
            if (sps.ChromaFormat == ChromaFormat.Monochrome)
                cbpTable = macroblock.IsIntra ? IntraCbpTableMono : InterCbpTableMono;
            else
                cbpTable = macroblock.IsIntra ? IntraCbpTable : InterCbpTable;
            int cbp = codeNum < (uint)cbpTable.Length ? cbpTable[codeNum] : 0;

            macroblock.CodedBlockPatternLuma = cbp & 0xF;
            macroblock.CodedBlockPatternChroma = sps.ChromaFormat == ChromaFormat.Monochrome ? 0 : (cbp >> 4) & 0x3;
        }

        /// <summary>
        /// Decodes residual data for the macroblock.
        /// </summary>
        private void DecodeResidual(
            ref CavlcDecoder decoder,
            ref Macroblock macroblock,
            int mbAddress,
            Span<byte> allNzCounts,
            Span<short> residualLuma,
            Span<short> residualChroma)
        {
            residualLuma.Clear();
            residualChroma.Clear();

            if (macroblock.IntraType == IntraMacroblockType.IntraPcm)
            {
                // I_PCM: read raw samples
                DecodePcm(ref decoder, residualLuma, residualChroma);
                // PCM blocks have NZC = 16 for all sub-blocks
                var myNzc = allNzCounts.Slice(mbAddress * nzCountsPerMb, nzCountsPerMb);
                myNzc.Fill(16);
                return;
            }

            bool diagRes = H264Decoder.DiagnosticOutput != null && mbAddress <= 45;

            // Decode luma residual
            if (macroblock.CodedBlockPatternLuma > 0 ||
                (macroblock.IntraType >= IntraMacroblockType.Intra16x16_0_0_0 &&
                 macroblock.IntraType <= IntraMacroblockType.Intra16x16_3_2_1))
            {
                long lumaStart = diagRes ? decoder.BitPosition : 0;
                DecodeLumaResidual(ref decoder, ref macroblock, mbAddress, allNzCounts, residualLuma);
                if (diagRes) H264Decoder.DiagnosticOutput!.WriteLine($"CAVLC-STEP: addr={mbAddress} afterLuma bitPos={decoder.BitPosition} lumaBits={decoder.BitPosition - lumaStart}");
            }
            else
            {
                // Zero out luma NZC for this MB
                var myNzc = allNzCounts.Slice(mbAddress * nzCountsPerMb, 16);
                myNzc.Clear();
            }

            // Decode chroma residual
            if (macroblock.CodedBlockPatternChroma > 0 && sps.ChromaFormat != ChromaFormat.Monochrome)
            {
                long chromaStart = diagRes ? decoder.BitPosition : 0;
                DecodeChromaResidual(ref decoder, ref macroblock, mbAddress, allNzCounts, residualChroma);
                if (diagRes) H264Decoder.DiagnosticOutput!.WriteLine($"CAVLC-STEP: addr={mbAddress} afterChroma bitPos={decoder.BitPosition} chromaBits={decoder.BitPosition - chromaStart}");
            }
            else
            {
                // Zero out chroma NZC for this MB
                var myNzc = allNzCounts.Slice(mbAddress * nzCountsPerMb + 16, nzCountsPerMb - 16);
                myNzc.Clear();
            }
        }

        /// <summary>
        /// Decodes I_PCM raw samples. Per H.264 spec 7.3.4, the bitstream
        /// must be byte-aligned before reading pcm_sample_luma/chroma.
        /// </summary>
        private void DecodePcm(ref CavlcDecoder decoder, Span<short> lumaOut, Span<short> chromaOut)
        {
            // H.264 spec 7.3.4: pcm_alignment_zero_bit — align to byte boundary
            decoder.AlignToByte();

            int bitDepthY = sps.BitDepthLuma;
            int bitDepthC = sps.BitDepthChroma;

            // Read 256 luma samples
            for (int i = 0; i < 256; i++)
            {
                lumaOut[i] = (short)decoder.ReadBits(bitDepthY);
            }

            // Read chroma samples (128 for 4:2:0)
            if (sps.ChromaFormat == ChromaFormat.Chroma420)
            {
                for (int i = 0; i < 128; i++)
                {
                    chromaOut[i] = (short)decoder.ReadBits(bitDepthC);
                }
            }
        }

        /// <summary>
        /// Decodes luma residual coefficients with proper nC context tracking.
        /// Handles both 4x4 and 8x8 transform modes.
        /// </summary>

        /// <summary>
        /// The four-by-four scan for the current macroblock. Field pictures and field-coded MBAFF
        /// macroblocks scan column-first (H.264 Table 8-13), and coefficients are stored at their
        /// raster positions as they are decoded, so nothing is reordered at reconstruction.
        /// </summary>
        private ReadOnlySpan<byte> CavlcScan4x4
            => (sliceHeader.FieldPicture || isFieldMb) ? ScanOrder.Field4x4 : ScanOrder.ZigZag4x4;

        private void DecodeLumaResidual(
            ref CavlcDecoder decoder,
            ref Macroblock macroblock,
            int mbAddress,
            Span<byte> allNzCounts,
            Span<short> residualLuma)
        {
            Span<int> block = coefficientBuffer.Slice(0, 16);
            var myNzc = allNzCounts.Slice(mbAddress * nzCountsPerMb, nzCountsPerMb);

            bool isIntra16x16 = macroblock.IntraType >= IntraMacroblockType.Intra16x16_0_0_0 &&
                               macroblock.IntraType <= IntraMacroblockType.Intra16x16_3_2_1;

            ReadOnlySpan<byte> cavlcScan4x4 = CavlcScan4x4;
            if (isIntra16x16)
            {
                // Decode luma DC block (16 coefficients)
                int nC = ComputeLumaNc(0, 0, mbAddress, allNzCounts);
                int numCoeffs = decoder.DecodeResidualBlock(block, nC, 16);

                // Store DC coefficients in scan order
                for (int i = 0; i < 16; i++)
                    residualLuma[cavlcScan4x4[i] * 16] = (short)block[i];
            }

            if (macroblock.Transform8x8 && !isIntra16x16)
            {
                // 8x8 transform: decode 4 blocks of 64 coefficients each
                // Per FFmpeg h264_cavlc.c line 632-645: each 8x8 block = 4 sub-4x4 decodes
                // Sub-block s handles zigzag scan positions 16*s..16*s+15
                DecodeLumaResidual8x8(ref decoder, ref macroblock, mbAddress, allNzCounts, residualLuma);
            }
            else
            {
                // 4x4 transform or Intra16x16 AC
                bool decodeAcBlocks = isIntra16x16 ? macroblock.CodedBlockPatternLuma > 0 : true;

                // Both halves of this loop dequantise at the store: the plain 4x4 category, and
                // the alternating-current blocks of an intra 16x16 macroblock, whose levels start
                // at raster position 1. Their direct-current block is the exception - it is
                // dequantised after its inverse Hadamard transform (8.5.12.2), on the reconstruct
                // side, and it is stored above rather than in this loop.
                //
                // Above eight bits a dequantised coefficient does not fit this buffer, so those
                // streams keep the levels. The decoder derives its pixel shift from the same
                // field, so the two sides cannot disagree about which it is.
                bool dequantiseAtTheStore = sps.BitDepthLuma == 8;
                ReadOnlySpan<byte> luma4x4Matrix = dequantiseAtTheStore
                    ? pps.ScalingMatrix4[macroblock.IsIntra ? 0 : 3]
                    : default;
                int clampedQp = Math.Clamp(macroblock.QuantizationParameter, 0, 63);
                int qpDiv6 = clampedQp / 6;
                int qpMod6 = clampedQp % 6;
                for (int blkIdx = 0; blkIdx < 16; blkIdx++)
                {
                    if (!decodeAcBlocks && isIntra16x16)
                    {
                        myNzc[blkIdx] = 0;
                        continue;
                    }
                    if (!macroblock.HasLumaResidual(blkIdx) && !isIntra16x16)
                    {
                        myNzc[blkIdx] = 0;
                        continue;
                    }

                    int x4 = Block4x4ToX4[blkIdx];
                    int y4 = Block4x4ToY4[blkIdx];
                    int nC = ComputeLumaNc(x4, y4, mbAddress, allNzCounts);
                    
                    // Diagnostic: dump nA/nB context for blk0 of target MB
                    if (H264Decoder.DiagDumpDetailMbAddr == mbAddress && H264Decoder.DiagnosticOutput != null && blkIdx == 0)
                    {
                        int dNA = -99, dNB = -99, dLeftMb = -1, dTopMb = -1;
                        int mbXd = mbAddress % widthInMacroblocks;
                        if (x4 > 0) { dNA = allNzCounts[mbAddress * nzCountsPerMb + Pos4x4ToBlockIndex(x4 - 1, y4)]; dLeftMb = mbAddress; }
                        else if (mbXd > 0 && isMbaffActive)
                        {
                            dLeftMb = (y4 < 2) ? mbaffLeftAddrTop : mbaffLeftAddrBot;
                            if (dLeftMb >= 0 && (dLeftMb + 1) * nzCountsPerMb <= allNzCounts.Length)
                                dNA = allNzCounts[dLeftMb * nzCountsPerMb + Pos4x4ToBlockIndex(3, MbaffLeftBlockOptions[mbaffLeftBlockRemap][y4])];
                        }
                        if (y4 > 0) { dNB = allNzCounts[mbAddress * nzCountsPerMb + Pos4x4ToBlockIndex(x4, y4 - 1)]; dTopMb = mbAddress; }
                        else if (mbAddress / widthInMacroblocks > 0 && isMbaffActive && mbaffTopAddr >= 0)
                        {
                            dTopMb = mbaffTopAddr;
                            if ((dTopMb + 1) * nzCountsPerMb <= allNzCounts.Length) dNB = allNzCounts[dTopMb * nzCountsPerMb + Pos4x4ToBlockIndex(x4, 3)];
                        }
                        H264Decoder.DiagnosticOutput.WriteLine($"  nC-DIAG blk0: nA={dNA} nB={dNB} nC={nC} leftMb={dLeftMb} topMb={dTopMb} remap={mbaffLeftBlockRemap} field={isFieldMb}");
                        if (dLeftMb >= 0 && (dLeftMb + 1) * nzCountsPerMb <= allNzCounts.Length)
                            H264Decoder.DiagnosticOutput.WriteLine($"  LEFT-NZC[{dLeftMb}]: [{string.Join(",", allNzCounts.Slice(dLeftMb * nzCountsPerMb, 16).ToArray())}]");
                        if (dTopMb >= 0 && (dTopMb + 1) * nzCountsPerMb <= allNzCounts.Length)
                            H264Decoder.DiagnosticOutput.WriteLine($"  TOP-NZC[{dTopMb}]: [{string.Join(",", allNzCounts.Slice(dTopMb * nzCountsPerMb, 16).ToArray())}]");
                    }

                    int startIdx = isIntra16x16 ? 1 : 0;
                    int maxCoeffs = isIntra16x16 ? 15 : 16;
                    bool diagBlk = H264Decoder.DiagnosticOutput != null && mbAddress <= 45;
                    long diagBitBefore = diagBlk ? decoder.BitPosition : 0;

                    // Diagnostic: capture bit position before decode
                    long bitPosBefore = H264Decoder.DiagDumpDetailMbAddr == mbAddress ? decoder.BitPosition : 0;
                    uint peekBits = 0;
                    uint peekBits32 = 0;
                    if (H264Decoder.DiagDumpDetailMbAddr == mbAddress && H264Decoder.DiagnosticOutput != null)
                    {
                        peekBits = decoder.PeekRawBits(16);
                        peekBits32 = decoder.PeekRawBits(32);
                    }
                    
                    int numCoeffs;
                    string? cavlcDebug = null;
                    if (H264Decoder.DiagDumpDetailMbAddr == mbAddress && H264Decoder.DiagnosticOutput != null)
                    {
                        numCoeffs = decoder.DecodeResidualBlock(block, nC, maxCoeffs, out int tz, out int t1, out cavlcDebug);
                    }
                    else
                    {
                        numCoeffs = decoder.DecodeResidualBlock(block, nC, maxCoeffs);
                    }

                    // Diagnostic: trace CAVLC decode for target MB
                    if (H264Decoder.DiagDumpDetailMbAddr == mbAddress && H264Decoder.DiagnosticOutput != null)
                    {
                        long bitPosAfter = decoder.BitPosition;
                        H264Decoder.DiagnosticOutput.Write($"  CAVLC blk{blkIdx} x4={x4} y4={y4} nC={nC} numCoeffs={numCoeffs} bits={bitPosAfter - bitPosBefore}");
                        H264Decoder.DiagnosticOutput.Write($" {cavlcDebug}");
                        H264Decoder.DiagnosticOutput.Write($" bitpos={bitPosBefore} peek32=0x{peekBits32:X8}");
                        H264Decoder.DiagnosticOutput.Write($" peek=0b{Convert.ToString((int)peekBits, 2).PadLeft(16, '0')}");
                        H264Decoder.DiagnosticOutput.Write($" scan=[{string.Join(",", block.Slice(0, 16).ToArray())}]");
                        H264Decoder.DiagnosticOutput.WriteLine();
                    }

                    myNzc[blkIdx] = (byte)numCoeffs;
                    if (diagBlk) H264Decoder.DiagnosticOutput!.WriteLine($"CAVLC-BLK: addr={mbAddress} blk={blkIdx} x4={x4} y4={y4} nC={nC} nzc={numCoeffs} bits={decoder.BitPosition - diagBitBefore} pos={decoder.BitPosition}");

                    for (int i = startIdx; i < 16; i++)
                    {
                        int rasterPosition = cavlcScan4x4[i];
                        int level = block[i - startIdx];

                        // Dequantised here, at the scatter, for the same reason the CABAC parser
                        // does it at its store: the reconstruct side then reads the block once,
                        // for the transform, instead of once to dequantise and once to transform.
                        // Both parsers have to agree about this, because they feed the same
                        // reconstruct code - it has no way to tell which one produced a block.
                        //
                        // Zero is left alone rather than multiplied by the matrix for nothing.
                        // This loop writes all sixteen positions of a coded block whether they
                        // hold a coefficient or not - it is what clears the ones that do not -
                        // and most of them do not, so the test is a branch the predictor gets
                        // right against fifteen multiplies it would otherwise do per block.
                        residualLuma[blkIdx * 16 + rasterPosition] = dequantiseAtTheStore && level != 0
                            ? H264Transform.DequantizeLevel(
                                  level, H264Transform.Multiplier4x4(luma4x4Matrix, qpDiv6, qpMod6, rasterPosition))
                            : (short)level;
                    }

                    unsafe
                    {
                        macroblock.NonZeroCountLuma[blkIdx] = (byte)numCoeffs;
                    }
                }
            }
        }

        /// <summary>
        /// Decodes 8x8 transform luma residual. Per FFmpeg h264_cavlc.c line 632-645:
        /// each 8x8 block is decoded as 4 sub-4x4 blocks of 16 coefficients.
        /// Sub-block s handles zigzag scan positions 16*s..16*s+15 within the 8x8 block.
        /// </summary>
        private void DecodeLumaResidual8x8(
            ref CavlcDecoder decoder,
            ref Macroblock macroblock,
            int mbAddress,
            Span<byte> allNzCounts,
            Span<short> residualLuma)
        {
            Span<int> block = coefficientBuffer.Slice(0, 16);
            var myNzc = allNzCounts.Slice(mbAddress * nzCountsPerMb, nzCountsPerMb);

            // Dequantised at the scatter below, as in the 4x4 path and in the CABAC parser.
            bool dequantiseAtTheStore = sps.BitDepthLuma == 8;
            ReadOnlySpan<byte> luma8x8Matrix = dequantiseAtTheStore
                ? pps.ScalingMatrix8[macroblock.IsIntra ? 0 : 3]
                : default;
            int clampedQp = Math.Clamp(macroblock.QuantizationParameter, 0, 63);
            int qpDiv6 = clampedQp / 6;
            int qpMod6 = clampedQp % 6;

            for (int i8x8 = 0; i8x8 < 4; i8x8++)
            {
                if ((macroblock.CodedBlockPatternLuma & (1 << i8x8)) == 0)
                {
                    for (int j = 0; j < 4; j++)
                    {
                        myNzc[i8x8 * 4 + j] = 0;
                        unsafe { macroblock.NonZeroCountLuma[i8x8 * 4 + j] = 0; }
                    }
                    continue;
                }

                int totalNzc = 0;
                for (int i4x4 = 0; i4x4 < 4; i4x4++)
                {
                    int blkIdx = i8x8 * 4 + i4x4;
                    int x4 = Block4x4ToX4[blkIdx];
                    int y4 = Block4x4ToY4[blkIdx];
                    int nC = ComputeLumaNc(x4, y4, mbAddress, allNzCounts);

                    int numCoeffs = decoder.DecodeResidualBlock(block, nC, 16);
                    myNzc[blkIdx] = (byte)numCoeffs;
                    totalNzc += numCoeffs;

                    // Use appropriate 8x8 CAVLC scan table for frame/field mode.
                    // Field pictures and MBAFF field MBs use column-first scan (H.264 spec Table 8-14).
                    ReadOnlySpan<byte> cavlcScan = (sliceHeader.FieldPicture || isFieldMb)
                        ? ScanOrder.FieldScan8x8Cavlc
                        : ScanOrder.ZigZagScan8x8Cavlc;
                    int baseOffset = i8x8 * 64;
                    int scanOffset = i4x4 * 16;
                    for (int k = 0; k < 16; k++)
                    {
                        int rasterPosition = cavlcScan[scanOffset + k];
                        int level = block[k];
                        residualLuma[baseOffset + rasterPosition] = dequantiseAtTheStore && level != 0
                            ? H264Transform.DequantizeLevel(
                                  level, H264Transform.Multiplier8x8(luma8x8Matrix, qpDiv6, qpMod6, rasterPosition))
                            : (short)level;
                    }

                    unsafe { macroblock.NonZeroCountLuma[blkIdx] = (byte)numCoeffs; }
                }

                // Per FFmpeg h264_cavlc.c line 643-644: aggregate NZC for cross-MB context.
                // For nC prediction: position 0 gets the total sum (used by next MB's CAVLC).
                // For deblocking: ALL 4 positions must reflect that the 8x8 block has residual,
                // because the 8x8 IDCT spreads coefficients across all 64 pixels. FFmpeg's fast
                // path (h264_loopfilter.c:360) hardcodes bS=2 for coded 8x8 blocks; we replicate
                // this by filling all 4 NonZeroCountLuma positions uniformly.
                int pos0 = i8x8 * 4;
                byte aggregatedNzc = (byte)Math.Min(totalNzc, 255);
                myNzc[pos0] = aggregatedNzc;
                unsafe
                {
                    macroblock.NonZeroCountLuma[pos0] = aggregatedNzc;
                    macroblock.NonZeroCountLuma[pos0 + 1] = aggregatedNzc;
                    macroblock.NonZeroCountLuma[pos0 + 2] = aggregatedNzc;
                    macroblock.NonZeroCountLuma[pos0 + 3] = aggregatedNzc;
                }
            }
        }

        /// <summary>
        /// Decodes chroma residual coefficients with proper nC context.
        /// </summary>
        private void DecodeChromaResidual(
            ref CavlcDecoder decoder,
            ref Macroblock macroblock,
            int mbAddress,
            Span<byte> allNzCounts,
            Span<short> residualChroma)
        {
            Span<int> block = coefficientBuffer.Slice(0, 16);
            var myNzc = allNzCounts.Slice(mbAddress * nzCountsPerMb, nzCountsPerMb);
            ReadOnlySpan<byte> cavlcScan4x4 = CavlcScan4x4;

            // Chroma alternating-current coefficients are dequantised at the store, as luma is,
            // against this component's own quantiser: the luma one plus the component offset,
            // through Table 8-15. Only at eight bits, where the result fits a short, and only
            // the AC blocks - the DC coefficients go through their Hadamard transform first.
            //
            // The eight-bit table is the only one needed here for that reason, so there is no
            // bit depth to select on and nothing that can disagree with the decoder's choice.
            bool dequantiseAtTheStore = sps.BitDepthLuma == 8;
            ReadOnlySpan<byte> chromaAcMatrixCb = default, chromaAcMatrixCr = default;
            int chromaQpCb = 0, chromaQpCr = 0;
            if (dequantiseAtTheStore)
            {
                chromaAcMatrixCb = pps.ScalingMatrix4[macroblock.IsIntra ? 1 : 4];
                chromaAcMatrixCr = pps.ScalingMatrix4[macroblock.IsIntra ? 2 : 5];
                chromaQpCb = H264Decoder.ChromaQpTable8[
                    Math.Clamp(macroblock.QuantizationParameter + pps.ChromaQpIndexOffset, 0, 51)];
                chromaQpCr = H264Decoder.ChromaQpTable8[
                    Math.Clamp(macroblock.QuantizationParameter + pps.SecondChromaQpIndexOffset, 0, 51)];
            }
            int chromaQpCbDiv6 = chromaQpCb / 6, chromaQpCbMod6 = chromaQpCb % 6;
            int chromaQpCrDiv6 = chromaQpCr / 6, chromaQpCrMod6 = chromaQpCr % 6;

            bool isChroma422Local = sps.ChromaFormat == ChromaFormat.Chroma422;
            int numChromaBlocks = isChroma422Local ? 8 : 4;
            int cbDcBase = isChroma422Local ? 256 : 128;
            int crDcBase = isChroma422Local ? 264 : 132;
            int crAcBase = isChroma422Local ? 128 : 64;
            int dcCoeffs = isChroma422Local ? 8 : 4;
            int chromaDcNc = isChroma422Local ? -2 : -1;

            // Decode Cb DC
            if (macroblock.HasChromaDcResidual)
            {
                int numCoeffs = decoder.DecodeResidualBlock(block, chromaDcNc, dcCoeffs);
                for (int i = 0; i < dcCoeffs; i++)
                    residualChroma[cbDcBase + i] = (short)block[i];
            }

            // Decode Cr DC
            if (macroblock.HasChromaDcResidual)
            {
                int numCoeffs = decoder.DecodeResidualBlock(block, chromaDcNc, dcCoeffs);
                for (int i = 0; i < dcCoeffs; i++)
                    residualChroma[crDcBase + i] = (short)block[i];
            }

            // Decode Cb/Cr AC if needed
            if (macroblock.HasChromaAcResidual)
            {
                // Cb blocks — store AC in scan order at blkIdx*16+1
                for (int blkIdx = 0; blkIdx < numChromaBlocks; blkIdx++)
                {
                    int nC = ComputeChromaNc(blkIdx, 0, mbAddress, allNzCounts);
                    int numCoeffs = decoder.DecodeResidualBlock(block, nC, 15);

                    for (int i = 1; i < 16; i++)
                    {
                        int rasterPosition = cavlcScan4x4[i];
                        int level = block[i - 1];
                        residualChroma[blkIdx * 16 + rasterPosition] = dequantiseAtTheStore && level != 0
                            ? H264Transform.DequantizeLevel(level, H264Transform.Multiplier4x4(
                                  chromaAcMatrixCb, chromaQpCbDiv6, chromaQpCbMod6, rasterPosition))
                            : (short)level;
                    }

                    myNzc[16 + blkIdx] = (byte)numCoeffs;
                    unsafe
                    {
                        if (blkIdx < 16)
                            macroblock.NonZeroCountChroma[blkIdx] = (byte)numCoeffs;
                    }
                }

                // Cr blocks — store AC at crAcBase+blkIdx*16+1
                for (int blkIdx = 0; blkIdx < numChromaBlocks; blkIdx++)
                {
                    int nC = ComputeChromaNc(blkIdx, 1, mbAddress, allNzCounts);
                    int numCoeffs = decoder.DecodeResidualBlock(block, nC, 15);

                    for (int i = 1; i < 16; i++)
                    {
                        int rasterPosition = cavlcScan4x4[i];
                        int level = block[i - 1];
                        residualChroma[crAcBase + blkIdx * 16 + rasterPosition] = dequantiseAtTheStore && level != 0
                            ? H264Transform.DequantizeLevel(level, H264Transform.Multiplier4x4(
                                  chromaAcMatrixCr, chromaQpCrDiv6, chromaQpCrMod6, rasterPosition))
                            : (short)level;
                    }

                    myNzc[16 + numChromaBlocks + blkIdx] = (byte)numCoeffs;
                }
            }
            else
            {
                // Clear chroma NZC
                for (int i = 0; i < numChromaBlocks * 2; i++)
                    myNzc[16 + i] = 0;
            }
        }

        // H.264 4x4 block index to position (x4, y4) in 4x4 block units.
        // Uses Z-scan (raster within 8x8 blocks): 0-3=8x8#0, 4-7=8x8#1, 8-11=8x8#2, 12-15=8x8#3
        private static ReadOnlySpan<byte> Block4x4ToX4 => new byte[]
        {
            0, 1, 0, 1,  2, 3, 2, 3,  0, 1, 0, 1,  2, 3, 2, 3
        };
        private static ReadOnlySpan<byte> Block4x4ToY4 => new byte[]
        {
            0, 0, 1, 1,  0, 0, 1, 1,  2, 2, 3, 3,  2, 2, 3, 3
        };

        /// <summary>
        /// Converts (x4, y4) position back to H.264 4x4 block index.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Pos4x4ToBlockIndex(int x4, int y4)
        {
            int mb8x8 = (y4 / 2) * 2 + (x4 / 2);
            int sub = (y4 % 2) * 2 + (x4 % 2);
            return mb8x8 * 4 + sub;
        }

        /// <summary>
        /// Computes nC for a luma 4x4 block using left and above neighbor non-zero counts.
        /// Per H.264 spec 9.2.1 and 6.4.8, cross-slice neighbors are unavailable.
        /// For DC block (x4=-1, y4=-1), uses the (0,0) block neighbors.
        /// </summary>
        private int ComputeLumaNc(int x4, int y4, int mbAddress, ReadOnlySpan<byte> allNzCounts)
        {
            if (x4 < 0) { x4 = 0; y4 = 0; } // DC block uses same context as (0,0)

            int mbX = mbAddress % widthInMacroblocks;
            int mbY = mbAddress / widthInMacroblocks;

            int nA = -1; // left
            int nB = -1; // above

            // Left neighbor
            if (x4 > 0)
            {
                int leftIdx = Pos4x4ToBlockIndex(x4 - 1, y4);
                nA = allNzCounts[mbAddress * nzCountsPerMb + leftIdx];
            }
            else if (mbX > 0)
            {
                if (isMbaffActive)
                {
                    int leftMb = (y4 < 2) ? mbaffLeftAddrTop : mbaffLeftAddrBot;
                    if (leftMb >= 0 && (leftMb + 1) * nzCountsPerMb <= allNzCounts.Length)
                    {
                        int remappedY = MbaffLeftBlockOptions[mbaffLeftBlockRemap][y4];
                        int leftIdx = Pos4x4ToBlockIndex(3, remappedY);
                        nA = allNzCounts[leftMb * nzCountsPerMb + leftIdx];
                    }
                }
                else
                {
                    int leftMb = mbAddress - 1;
                    int firstMbInSlice = (int)sliceHeader.FirstMacroblockAddress;
                    if (leftMb >= firstMbInSlice)
                    {
                        int leftIdx = Pos4x4ToBlockIndex(3, y4);
                        nA = allNzCounts[leftMb * nzCountsPerMb + leftIdx];
                    }
                }
            }

            // Above neighbor
            if (y4 > 0)
            {
                int aboveIdx = Pos4x4ToBlockIndex(x4, y4 - 1);
                nB = allNzCounts[mbAddress * nzCountsPerMb + aboveIdx];
            }
            else if (mbY > 0)
            {
                if (isMbaffActive)
                {
                    if (mbaffTopAddr >= 0 && (mbaffTopAddr + 1) * nzCountsPerMb <= allNzCounts.Length)
                    {
                        int aboveIdx = Pos4x4ToBlockIndex(x4, 3);
                        nB = allNzCounts[mbaffTopAddr * nzCountsPerMb + aboveIdx];
                    }
                }
                else
                {
                    int aboveMb = mbAddress - widthInMacroblocks;
                    int firstMbInSlice = (int)sliceHeader.FirstMacroblockAddress;
                    if (aboveMb >= firstMbInSlice)
                    {
                        int aboveIdx = Pos4x4ToBlockIndex(x4, 3);
                        nB = allNzCounts[aboveMb * nzCountsPerMb + aboveIdx];
                    }
                }
            }

            if (nA >= 0 && nB >= 0)
                return (nA + nB + 1) >> 1;
            if (nA >= 0) return nA;
            if (nB >= 0) return nB;
            return 0;
        }

        /// <summary>
        /// Computes nC for a chroma 4x4 block using neighbor NZ counts.
        /// For 4:2:0, each chroma plane has 4 blocks in 2x2 arrangement.
        /// Cross-slice neighbors are treated as unavailable (matching FFmpeg behavior).
        /// </summary>
        private int ComputeChromaNc(int blkIdx, int plane, int mbAddress, ReadOnlySpan<byte> allNzCounts)
        {
            int cx = blkIdx % 2;
            int cy = blkIdx / 2;
            int mbX = mbAddress % widthInMacroblocks;
            int mbY = mbAddress / widthInMacroblocks;

            // 4:2:0: 4 blocks/plane (2×2), 4:2:2: 8 blocks/plane (2×4)
            int blocksPerPlane = (nzCountsPerMb - 16) / 2;
            int chromaRows = blocksPerPlane / 2;
            int planeOffset = 16 + plane * blocksPerPlane;

            int nA = -1, nB = -1;

            if (cx > 0)
            {
                nA = allNzCounts[mbAddress * nzCountsPerMb + planeOffset + (cy * 2 + cx - 1)];
            }
            else if (mbX > 0)
            {
                if (isMbaffActive)
                {
                    int leftMb = (cy < chromaRows / 2) ? mbaffLeftAddrTop : mbaffLeftAddrBot;
                    if (leftMb >= 0 && (leftMb + 1) * nzCountsPerMb <= allNzCounts.Length)
                    {
                        // Map chroma row to luma row for MBAFF remapping, then back to chroma
                        int lumaY = cy * 16 / (chromaRows * 4);
                        int remappedLumaY = MbaffLeftBlockOptions[mbaffLeftBlockRemap][Math.Min(lumaY, 3)];
                        int remappedCy = remappedLumaY * chromaRows / 4;
                        if (remappedCy >= chromaRows) remappedCy = chromaRows - 1;
                        nA = allNzCounts[leftMb * nzCountsPerMb + planeOffset + (remappedCy * 2 + 1)];
                    }
                }
                else
                {
                    int leftMb = mbAddress - 1;
                    int firstMbInSlice = (int)sliceHeader.FirstMacroblockAddress;
                    if (leftMb >= firstMbInSlice)
                    {
                        nA = allNzCounts[leftMb * nzCountsPerMb + planeOffset + (cy * 2 + 1)];
                    }
                }
            }

            if (cy > 0)
            {
                nB = allNzCounts[mbAddress * nzCountsPerMb + planeOffset + ((cy - 1) * 2 + cx)];
            }
            else if (mbY > 0)
            {
                // Bottom row of above MB's chroma grid
                int bottomRow = chromaRows - 1;
                if (isMbaffActive)
                {
                    if (mbaffTopAddr >= 0 && (mbaffTopAddr + 1) * nzCountsPerMb <= allNzCounts.Length)
                    {
                        nB = allNzCounts[mbaffTopAddr * nzCountsPerMb + planeOffset + (bottomRow * 2 + cx)];
                    }
                }
                else
                {
                    int aboveMb = mbAddress - widthInMacroblocks;
                    int firstMbInSlice = (int)sliceHeader.FirstMacroblockAddress;
                    if (aboveMb >= firstMbInSlice)
                    {
                        nB = allNzCounts[aboveMb * nzCountsPerMb + planeOffset + (bottomRow * 2 + cx)];
                    }
                }
            }

            if (nA >= 0 && nB >= 0)
                return (nA + nB + 1) >> 1;
            if (nA >= 0) return nA;
            if (nB >= 0) return nB;
            return 0;
        }
    }
}
